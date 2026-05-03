using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Combobulate.Caching;
using Combobulate.Sorting;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;
using CompositionExpressions;

namespace Combobulate.Rendering;

/// <summary>
/// Analytical "aspect-graph" renderer. The caller supplies a typed
/// <see cref="Matrix4x4Node"/> describing the model transform plus a list
/// of <see cref="TransformAnimationAxis"/> entries identifying every live
/// scalar input the AST depends on. The renderer:
///
/// <list type="bullet">
///   <item>Runs the bake compute (sweeping the axes, evaluating the AST,
///         and running the painter sorter at every sample) on a background
///         thread so the UI stays responsive even when the input space is
///         large (3-axis rotation can mean thousands of evaluations).</item>
///   <item>Materialises one <see cref="ContainerVisual"/> per painter
///         cell on the UI thread once compute completes, with sprites
///         already in painter order. Each container's <c>Opacity</c> is
///         driven by an <see cref="ExpressionAnimation"/> that tests
///         whether the live axis values fall inside the cell's box (per
///         axis, AND-ed, periodic axes wrapped in-expression).</item>
/// </list>
///
/// <para>While compute is in flight the renderer keeps the previously-
/// baked trees visible (so the user keeps seeing a valid render),
/// scheduling at most one bake at a time and discarding stale results.</para>
///
/// <para>After bake, the runtime is pure compositor work: zero CPU per
/// frame. By construction, revolutions of any periodic axis are bit-
/// identical because the same K cells are referenced via the same
/// expression every revolution.</para>
/// </summary>
internal sealed class BakedAspectGraphRenderer : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;

    private ContainerVisual[]? _trees;

    // Cached bake inputs (so MaybeRebake / completion callbacks can compare).
    private Matrix4x4Node? _transformNode;
    private TransformAnimationAxis[]? _axes;
    private ObjGeometry? _bakedGeometry;
    private ResolvedQuadMaterials? _bakedBindings;
    private float _bakedScale, _bakedHostW, _bakedHostH;
    private float _bakedCullMarginCos, _bakedCameraDistance;
    private SortAlgorithm _bakedAlgorithm;

    // Background-thread bake coordination. _bakeGeneration is bumped by
    // the UI thread on every fresh bake request; the background task
    // checks the generation when it completes and discards its result if
    // the world has moved on.
    private int _bakeGeneration;
    private CancellationTokenSource? _bakeCts;

    /// <summary>
    /// Set whenever a bake request is in flight; UI-thread <c>Update</c>
    /// reads this to decide whether to skip starting another bake.
    /// </summary>
    public bool BakeInFlight => _bakeCts != null;

    public int CellCount => _trees?.Length ?? 0;

    public BakedAspectGraphRenderer(Compositor compositor, ContainerVisual parent)
    {
        _compositor = compositor;
        _parent = parent;
    }

    /// <summary>
    /// Begin a fresh bake. The compute runs on the thread pool;
    /// materialisation happens on the UI thread when compute completes,
    /// scheduled via <see cref="DispatcherQueue.GetForCurrentThread"/>.
    /// Returns immediately. If a previous bake is still in flight, it is
    /// cancelled.
    /// </summary>
    public void RequestBake(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale,
        float hostW,
        float hostH,
        float cullMarginCos,
        float cameraDistance,
        SortAlgorithm sortAlgorithm)
    {
        if (axes is null || axes.Length == 0)
            throw new ArgumentException("At least one axis required.", nameof(axes));

        _transformNode = transformNode;
        _axes = axes;
        _bakedGeometry = geometry;
        _bakedBindings = bindings;
        _bakedScale = scale;
        _bakedHostW = hostW;
        _bakedHostH = hostH;
        _bakedCullMarginCos = cullMarginCos;
        _bakedCameraDistance = cameraDistance;
        _bakedAlgorithm = sortAlgorithm;

        // Cancel any in-flight bake; bump generation so the previous one's
        // completion callback (if it raced past cancellation) sees a stale
        // generation and bails out.
        _bakeCts?.Cancel();
        _bakeCts = new CancellationTokenSource();
        var ct = _bakeCts.Token;
        int myGeneration = ++_bakeGeneration;

        // Capture the UI dispatcher so we can hop back for materialisation.
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _ = Task.Run(() =>
        {
            try
            {
                ComputedBake computed = ComputeBake(transformNode, axes, geometry,
                    cullMarginCos, cameraDistance, sortAlgorithm, ct);

                if (ct.IsCancellationRequested) return;

                // Hop back to UI thread to materialise.
                ui.TryEnqueue(() =>
                {
                    // Discard if the world has moved on.
                    if (myGeneration != _bakeGeneration) return;
                    if (ct.IsCancellationRequested) return;

                    Materialise(computed, geometry, bindings, scale, hostW, hostH, axes);
                    _bakeCts?.Dispose();
                    _bakeCts = null;
                });
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex)
            {
                // Don't take the app down on a bake error, but surface the
                // problem so the user/dev can see it during development.
                System.Diagnostics.Debug.WriteLine(
                    $"[BakedAspectGraphRenderer] Bake failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }, ct);
    }

    /// <summary>Pure-CPU phase: produces one <see cref="CellSig"/> per cell.</summary>
    private struct CellSig
    {
        public float[] Lo;
        public float[] Hi;
        public int[] Order;
        public bool[] Visibility;
    }

    private struct ComputedBake { public CellSig[] Cells; }

    /// <summary>Background-thread compute: sweep axes + sort. No Composition deps.</summary>
    private static ComputedBake ComputeBake(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        float cullMarginCos,
        float cameraDistance,
        SortAlgorithm sortAlgorithm,
        CancellationToken ct)
    {
        var sorter = FaceSorterFactory.Create(sortAlgorithm, geometry);

        // Override each axis's UI-thread provider to read from a shared
        // sweep array. The Evaluate() walk reads the override on each
        // matching leaf — same-thread reads, no cross-thread compositor
        // call, fast.
        var sweep = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            int idx = i;
            axes[i].Scalar.SetLiveValueProvider(() => sweep[idx]);
        }

        try
        {
            CellSig[] cellSigs;
            if (axes.Length == 1)
            {
                var axis = axes[0];
                var cells = AspectGraphBake.BakeOneAxis(
                    sorter, geometry,
                    v =>
                    {
                        ct.ThrowIfCancellationRequested();
                        sweep[0] = axis.Min + v;
                        return transformNode.Evaluate();
                    },
                    axis.Length,
                    cullMarginCos,
                    cameraDistance);
                cellSigs = new CellSig[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                {
                    cellSigs[i] = new CellSig
                    {
                        Lo = new[] { axis.Min + cells[i].Lo },
                        Hi = new[] { axis.Min + cells[i].Hi },
                        Order = cells[i].Order,
                        Visibility = cells[i].Visibility,
                    };
                }
            }
            else
            {
                var bakeAxes = new AspectGraphBake.AxisSweep[axes.Length];
                for (int i = 0; i < axes.Length; i++)
                    bakeAxes[i] = new AspectGraphBake.AxisSweep(axes[i].Min, axes[i].Length, axes[i].Samples, axes[i].Periodic);
                var cells = AspectGraphBake.BakeMultiAxis(
                    sorter, geometry,
                    input =>
                    {
                        ct.ThrowIfCancellationRequested();
                        Array.Copy(input, sweep, axes.Length);
                        return transformNode.Evaluate();
                    },
                    bakeAxes,
                    cullMarginCos,
                    cameraDistance);
                cellSigs = new CellSig[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                {
                    cellSigs[i] = new CellSig
                    {
                        Lo = cells[i].Lo,
                        Hi = cells[i].Hi,
                        Order = cells[i].Order,
                        Visibility = cells[i].Visibility,
                    };
                }
            }
            return new ComputedBake { Cells = cellSigs };
        }
        finally
        {
            for (int i = 0; i < axes.Length; i++)
                axes[i].Scalar.SetLiveValueProvider(null);
        }
    }

    /// <summary>UI-thread phase: materialise cells into Composition trees.</summary>
    private void Materialise(
        ComputedBake computed,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        TransformAnimationAxis[] axes)
    {
        DisposeTrees();

        _trees = new ContainerVisual[computed.Cells.Length];
        for (int i = 0; i < _trees.Length; i++)
        {
            var c = computed.Cells[i];
            var tree = _compositor.CreateContainerVisual();
            BuildTreeContent(tree, geometry, bindings, scale, hostW, hostH, c.Order, c.Visibility);
            tree.StartAnimation("Opacity", BuildAxisInCellExpression(axes, c.Lo, c.Hi));
            _parent.Children.InsertAtTop(tree);
            _trees[i] = tree;
        }
    }

    private static ScalarNode BuildAxisInCellExpression(
        TransformAnimationAxis[] axes, float[] lo, float[] hi)
    {
        BooleanNode? acc = null;
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = axes[i];
            // Compute the "live" axis value to compare against absolute
            // [lo, hi). For a periodic axis we wrap into [Min, Min+Length).
            // Avoid subtracting axis.Min from axis.Scalar directly: when
            // axis.Min is negative the toolkit emits "(scalar - -180)" which
            // some Composition expression evaluators reject. Instead add
            // (-Min) when needed.
            ScalarNode liveVal;
            if (axis.Periodic)
            {
                // shifted = axis.Scalar + (-axis.Min) → maps the live value
                // into [0, +Length+epsilon). Then normalise mod Length and
                // shift back: liveVal = wrapped + axis.Min.
                ScalarNode shifted = axis.Scalar + (ScalarNode)(-axis.Min);
                ScalarNode ratio = shifted / (ScalarNode)axis.Length;
                ScalarNode wrapped = shifted - ExpressionFunctions.Floor(ratio) * (ScalarNode)axis.Length;
                liveVal = wrapped + (ScalarNode)axis.Min;
            }
            else
            {
                liveVal = axis.Scalar;
            }

            BooleanNode test;
            if (axis.Periodic && hi[i] <= lo[i])
            {
                // Wrap cell across the periodic boundary.
                test = ExpressionFunctions.Or(
                    liveVal >= (ScalarNode)lo[i],
                    liveVal < (ScalarNode)hi[i]);
            }
            else
            {
                test = ExpressionFunctions.And(
                    liveVal >= (ScalarNode)lo[i],
                    liveVal < (ScalarNode)hi[i]);
            }
            acc = acc is null ? test : ExpressionFunctions.And(acc, test);
        }
        return ExpressionFunctions.Conditional(acc!, (ScalarNode)1f, (ScalarNode)0f);
    }

    private void BuildTreeContent(
        ContainerVisual tree,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        int[] order, bool[] visibility)
    {
        var origin = new Vector3(hostW / 2f, hostH / 2f, 0);
        var quads = geometry.Quads;
        var sprites = new SpriteVisual[quads.Length];
        for (int q = 0; q < quads.Length; q++)
        {
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(1f, 1f);
            var cq = quads[q];
            var v0 = cq.V0 * scale + origin;
            var v1 = cq.V1 * scale + origin;
            var v3 = cq.V3 * scale + origin;
            var xAxis = v1 - v0;
            var yAxis = v3 - v0;
            var zAxis = Vector3.Normalize(Vector3.Cross(xAxis, yAxis));
            sprite.TransformMatrix = new Matrix4x4(
                xAxis.X, xAxis.Y, xAxis.Z, 0,
                yAxis.X, yAxis.Y, yAxis.Z, 0,
                zAxis.X, zAxis.Y, zAxis.Z, 0,
                v0.X,    v0.Y,    v0.Z,    1);
            sprite.Brush = bindings.Bindings[q].Brush;
            sprite.IsVisible = visibility[q];
            sprites[q] = sprite;
        }
        for (int i = 0; i < order.Length; i++)
        {
            int qi = order[i];
            if (!visibility[qi]) continue;
            tree.Children.InsertAtTop(sprites[qi]);
        }
    }

    public void Dispose()
    {
        _bakeCts?.Cancel();
        _bakeCts?.Dispose();
        _bakeCts = null;
        DisposeTrees();
    }

    private void DisposeTrees()
    {
        if (_trees == null) return;
        foreach (var t in _trees)
        {
            t.StopAnimation("Opacity");
            t.Children.RemoveAll();
            if (_parent.Children.Contains(t)) _parent.Children.Remove(t);
            t.Dispose();
        }
        _trees = null;
    }
}

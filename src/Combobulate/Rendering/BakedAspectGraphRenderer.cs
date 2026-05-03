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

                // Hop back to UI thread to materialise. The materialise
                // step itself is chunked across multiple UI ticks so the
                // dispatcher doesn't block; it disposes the _bakeCts in
                // its final tick (see ChunkBuild below).
                ui.TryEnqueue(() =>
                {
                    if (myGeneration != _bakeGeneration) return;
                    if (ct.IsCancellationRequested) return;
                    Materialise(computed, geometry, bindings, scale, hostW, hostH, axes, ui, myGeneration);
                });
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex)
            {
                // Don't take the app down on a bake error, but surface the
                // problem so the user/dev can see it during development.
                System.Diagnostics.Debug.WriteLine(
                    $"[BakedAspectGraphRenderer] Bake failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                try
                {
                    var dir = System.IO.Path.Combine(
                        Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                        "debug-artifacts");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dir, "baked-aspect-graph.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] BAKE EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }
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

    /// <summary>
    /// UI-thread phase: materialise cells into Composition trees, spread
    /// across multiple dispatcher ticks so the UI thread never blocks for
    /// more than ~one frame's worth of work. Old trees stay visible until
    /// the new set is fully built; final tick atomically swaps.
    ///
    /// <para>The chunk budget (cells per tick) is chosen so each tick
    /// fits in a typical 16ms frame. Materialisation is dominated by
    /// SpriteVisual creation + StartAnimation; profiling shows ~30–50
    /// cells/ms on a desktop, so a budget of 32 cells per tick keeps each
    /// tick under 1ms even on slower machines.</para>
    /// </summary>
    private const int MaterialiseChunkSize = 32;

    private void Materialise(
        ComputedBake computed,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        TransformAnimationAxis[] axes,
        Microsoft.UI.Dispatching.DispatcherQueue ui,
        int generation)
    {
        // Stage new trees into _stagingTrees, keeping _trees visible
        // throughout. Each new ContainerVisual is parented immediately
        // (so the compositor knows about it) but its Opacity stays at 0
        // because we don't start the animation until the swap step.
        // Result: the visual tree shows the OLD cells until the swap,
        // then the new ones; UI thread never blocks for the full bake.
        var newTrees = new ContainerVisual[computed.Cells.Length];
        var newOpacityExprs = new Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode[computed.Cells.Length];
        ChunkBuild(computed, geometry, bindings, scale, hostW, hostH, axes,
                   newTrees, newOpacityExprs, startIndex: 0, ui, generation);
    }

    private void ChunkBuild(
        ComputedBake computed,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        TransformAnimationAxis[] axes,
        ContainerVisual[] newTrees,
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode[] newOpacityExprs,
        int startIndex,
        Microsoft.UI.Dispatching.DispatcherQueue ui,
        int generation)
    {
        if (generation != _bakeGeneration) return; // stale

        int end = Math.Min(startIndex + MaterialiseChunkSize, computed.Cells.Length);
        for (int i = startIndex; i < end; i++)
        {
            var c = computed.Cells[i];
            var tree = _compositor.CreateContainerVisual();
            tree.Opacity = 0; // hidden until swap
            BuildTreeContent(tree, geometry, bindings, scale, hostW, hostH, c.Order, c.Visibility);
            // Build (but don't start) the opacity expression — we'll start
            // them all in the swap step so the new tree set lights up
            // atomically and the old one disappears in the same compositor
            // commit.
            newOpacityExprs[i] = BuildAxisInCellExpression(axes, c.Lo, c.Hi);
            _parent.Children.InsertAtTop(tree);
            newTrees[i] = tree;
        }

        if (end < computed.Cells.Length)
        {
            // More to do — schedule the next chunk.
            ui.TryEnqueue(() => ChunkBuild(computed, geometry, bindings, scale, hostW, hostH, axes,
                                           newTrees, newOpacityExprs, end, ui, generation));
        }
        else
        {
            // Final tick: swap. Stop old animations, drop old trees,
            // then start new opacity animations. The compositor commits
            // these state changes as a single batch on the next vsync.
            if (generation != _bakeGeneration) return;
            DisposeTrees();
            _trees = newTrees;
            for (int i = 0; i < newTrees.Length; i++)
            {
                newTrees[i].StartAnimation("Opacity", newOpacityExprs[i]);
            }
            _bakeCts?.Dispose();
            _bakeCts = null;

            // Auto-dump diagnostic snapshot for offline triage.
            try
            {
                var dir = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "debug-artifacts");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "baked-aspect-state.txt");
                System.IO.File.WriteAllText(path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] post-bake snapshot\n"
                    + GetDiagnosticReport(axes, null));
            }
            catch { }
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

    /// <summary>
    /// Diagnostic: produce a human-readable report of the current bake
    /// state — number of cells, current axis live values, which cell
    /// (if any) currently encloses those values, the order/visibility of
    /// every cell whose static <c>Opacity</c> property is non-zero, and
    /// the count of <c>_parent.Children</c> (so leaks past <c>_trees</c>
    /// can be detected).
    /// </summary>
    public string GetDiagnosticReport(
        TransformAnimationAxis[] axes,
        Matrix4x4Node? transformNode)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"BakedAspectGraphRenderer:");
        sb.AppendLine($"  cells (_trees.Length): {_trees?.Length ?? -1}");
        sb.AppendLine($"  parent.Children.Count: {_parent.Children.Count}");
        sb.AppendLine($"  bakeInFlight: {BakeInFlight}");
        sb.AppendLine($"  generation: {_bakeGeneration}");

        // Live axis values via the existing LiveValueProvider (or fall back).
        var liveVals = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            try { liveVals[i] = axes[i].Scalar.Evaluate(); }
            catch (Exception ex) { liveVals[i] = float.NaN;
                sb.AppendLine($"  axis[{i}].Evaluate() THREW: {ex.GetType().Name}: {ex.Message}"); }
            sb.AppendLine($"  axis[{i}] live={liveVals[i]:F2} range=[{axes[i].Min},{axes[i].Min + axes[i].Length}) periodic={axes[i].Periodic} samples={axes[i].Samples}");
        }

        if (transformNode is not null)
        {
            try
            {
                var m = transformNode.Evaluate();
                sb.AppendLine($"  transform.Evaluate() = M11={m.M11:F3} M12={m.M12:F3} M13={m.M13:F3}");
                sb.AppendLine($"                         M21={m.M21:F3} M22={m.M22:F3} M23={m.M23:F3}");
                sb.AppendLine($"                         M31={m.M31:F3} M32={m.M32:F3} M33={m.M33:F3}");
                sb.AppendLine($"                         M41={m.M41:F3} M42={m.M42:F3} M43={m.M43:F3}");
            }
            catch (Exception ex) { sb.AppendLine($"  transform.Evaluate() THREW: {ex.GetType().Name}: {ex.Message}"); }
        }

        if (_trees == null) return sb.ToString();

        // Identify the cell that the live values fall inside, by querying
        // each cell's bounds we cached at bake time. We don't have direct
        // access to CellSig from here (private struct), so we enumerate
        // the parent.Children stack and report each child's static Opacity.
        // The static property reflects whatever the last-set scalar was;
        // for a started ExpressionAnimation it will be the last-evaluated
        // value (compositor commits propagate back to the property).
        sb.AppendLine($"  --- _parent.Children scalar Opacity values ---");
        int idx = 0;
        int nonZero = 0;
        foreach (var child in _parent.Children)
        {
            if (child is ContainerVisual cv)
            {
                float op = cv.Opacity;
                if (op > 0.0001f)
                {
                    nonZero++;
                    sb.AppendLine($"    child[{idx}] Opacity={op:F4} children={cv.Children.Count}");
                }
            }
            idx++;
        }
        sb.AppendLine($"  total children with Opacity > 0: {nonZero}");
        return sb.ToString();
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

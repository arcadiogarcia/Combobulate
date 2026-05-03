using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Combobulate.Caching;
using Combobulate.Sorting;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;
using CompositionExpressions;

namespace Combobulate.Rendering;

/// <summary>
/// Analytical "aspect-graph" renderer driven by a typed transform expression
/// tree (<see cref="Matrix4x4Node"/>). The caller hands Combobulate the
/// transform AST and identifies which scalar leaf is the periodic "primary"
/// input (typically an animated yaw scalar in degrees). At setup the
/// renderer:
///
/// <list type="number">
///   <item>Sweeps the primary axis across <c>[0, period)</c> with the
///         primary's <c>LiveValueProvider</c> overridden to return each
///         sweep value, then evaluates the AST on the UI thread via
///         <see cref="Matrix4x4Node.Evaluate"/> to obtain the same
///         <see cref="Matrix4x4"/> the GPU will produce. The matrix is
///         fed to the configured <see cref="IFaceSorter"/>; every adjacent
///         yaw pair whose painter signature differs is bisected to find
///         the exact breakpoint.</item>
///   <item>Pre-builds one <c>ContainerVisual</c> per constant-painter-order
///         cell, populated with sprites in that cell's painter order, with
///         per-quad <see cref="Visual.IsVisible"/> applied. Each container
///         starts an <see cref="ExpressionAnimation"/> on its
///         <see cref="Visual.Opacity"/> built from the same primary-axis
///         subtree the caller passed, so the references carried by the
///         primary subtree (slider props, KFAs, trackers...) are correctly
///         threaded.</item>
/// </list>
///
/// <para>The runtime cost per frame is zero: the compositor evaluates the
/// rotation expression and all opacity expressions on its own thread. The
/// only ongoing CPU job is to detect when "secondary" inputs (everything
/// in the AST other than the primary axis) change, at which point we
/// trigger a rebake via <see cref="MaybeRebake"/>. By construction
/// revolution N is bit-identical to revolution 1 because both reference
/// the same K cells indexed by the same expression.</para>
/// </summary>
internal sealed class BakedAspectGraphRenderer : IDisposable
{
    /// <summary>How many initial samples to take across one yaw period.</summary>
    private const int CoarseSamples = 720; // 0.5° step

    /// <summary>Bisection terminates when the bracket is below this width (degrees).</summary>
    private const float BisectEpsilonDeg = 1e-3f;

    /// <summary>Maximum bisection iterations before accepting current bracket.</summary>
    private const int MaxBisectIterations = 24;

    /// <summary>
    /// Probe yaws used to detect secondary inputs changing between bakes.
    /// Spread enough that any non-trivial change in any other input
    /// (pitch, roll, slider, perspective distance, ...) flips at least
    /// one element of at least one probe matrix.
    /// </summary>
    private static readonly float[] SecondaryProbePrimaryYaws = { 0f, 73f, 137f, 211f, 293f };

    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;

    private ContainerVisual[]? _trees;

    // Bake inputs / cached state
    private Matrix4x4Node? _transformNode;
    private ScalarNode? _primaryAxis;
    private float _period;
    private ObjGeometry? _bakedGeometry;
    private ResolvedQuadMaterials? _bakedBindings;
    private float _bakedScale, _bakedHostW, _bakedHostH;
    private float _bakedCullMarginCos, _bakedCameraDistance;
    private SortAlgorithm _bakedAlgorithm;
    private Matrix4x4[]? _secondaryProbes;

    public BakedAspectGraphRenderer(Compositor compositor, ContainerVisual parent)
    {
        _compositor = compositor;
        _parent = parent;
    }

    public int CellCount => _trees?.Length ?? 0;

    public int Bake(
        Matrix4x4Node transformNode,
        ScalarNode primaryAxis,
        float primaryAxisPeriod,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale,
        float hostW,
        float hostH,
        float cullMarginCos,
        float cameraDistance,
        SortAlgorithm sortAlgorithm)
    {
        if (primaryAxisPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(primaryAxisPeriod));

        DisposeTrees();

        _transformNode = transformNode;
        _primaryAxis = primaryAxis;
        _period = primaryAxisPeriod;
        _bakedGeometry = geometry;
        _bakedBindings = bindings;
        _bakedScale = scale;
        _bakedHostW = hostW;
        _bakedHostH = hostH;
        _bakedCullMarginCos = cullMarginCos;
        _bakedCameraDistance = cameraDistance;
        _bakedAlgorithm = sortAlgorithm;

        var sorter = FaceSorterFactory.Create(sortAlgorithm, geometry);

        // Override the primary axis's UI-thread provider so tree.Evaluate()
        // returns the matrix at our chosen sweep yaw. Always restore on exit.
        var sweepYaw = 0f;
        primaryAxis.SetLiveValueProvider(() => sweepYaw);

        try
        {
            // Delegate the bake math to the pure-CPU helper so the algorithm
            // is exercised by the test project (which can't reference the
            // renderer due to Composition deps).
            var cells = AspectGraphBake.BakeOneAxis(
                sorter,
                geometry,
                yaw =>
                {
                    sweepYaw = yaw;
                    return transformNode.Evaluate();
                },
                primaryAxisPeriod,
                cullMarginCos,
                cameraDistance);

            _trees = new ContainerVisual[cells.Length];
            for (int i = 0; i < _trees.Length; i++)
            {
                var c = cells[i];
                var tree = _compositor.CreateContainerVisual();
                BuildTreeContent(tree, geometry, bindings, scale, hostW, hostH, c.Order, c.Visibility);
                InstallOpacityExpression(tree, primaryAxis, primaryAxisPeriod, c.Lo, c.Hi);
                _parent.Children.InsertAtTop(tree);
                _trees[i] = tree;
            }

            // Snapshot secondary-input fingerprint.
            _secondaryProbes = new Matrix4x4[SecondaryProbePrimaryYaws.Length];
            for (int p = 0; p < SecondaryProbePrimaryYaws.Length; p++)
            {
                sweepYaw = SecondaryProbePrimaryYaws[p];
                _secondaryProbes[p] = transformNode.Evaluate();
            }
        }
        finally
        {
            primaryAxis.SetLiveValueProvider(null);
        }

        return _trees!.Length;
    }

    /// <summary>
    /// Re-bake if any non-primary input contributed to the AST has changed
    /// since the last bake. Returns true iff a re-bake happened.
    /// </summary>
    public bool MaybeRebake()
    {
        if (_transformNode is null || _primaryAxis is null || _secondaryProbes is null) return false;
        if (!SecondaryInputsChanged()) return false;

        Bake(
            _transformNode,
            _primaryAxis,
            _period,
            _bakedGeometry!,
            _bakedBindings!,
            _bakedScale,
            _bakedHostW,
            _bakedHostH,
            _bakedCullMarginCos,
            _bakedCameraDistance,
            _bakedAlgorithm);
        return true;
    }

    private bool SecondaryInputsChanged()
    {
        if (_transformNode is null || _primaryAxis is null || _secondaryProbes is null) return false;
        var sweepYaw = 0f;
        _primaryAxis.SetLiveValueProvider(() => sweepYaw);
        try
        {
            for (int p = 0; p < SecondaryProbePrimaryYaws.Length; p++)
            {
                sweepYaw = SecondaryProbePrimaryYaws[p];
                var current = _transformNode.Evaluate();
                if (current != _secondaryProbes[p]) return true;
            }
        }
        finally
        {
            _primaryAxis.SetLiveValueProvider(null);
        }
        return false;
    }

    private void BuildTreeContent(
        ContainerVisual tree,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale,
        float hostW,
        float hostH,
        int[] order,
        bool[] visibility)
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
    /// Build a typed opacity expression for this cell from the same primary
    /// axis subtree the rotation expression uses, then start it on the
    /// tree's <see cref="Visual.Opacity"/>.
    /// </summary>
    private void InstallOpacityExpression(
        ContainerVisual tree,
        ScalarNode primaryAxis,
        float period,
        float lo,
        float hi)
    {
        // ScalarNode has implicit conversion from float, so we can write
        // bound checks using ordinary literals.
        ScalarNode periodNode = period;
        var ratio = primaryAxis / periodNode;
        var yawN = ratio - ExpressionFunctions.Floor(ratio);

        ScalarNode loN = lo / period;
        ScalarNode hiN = hi / period;

        BooleanNode inCell;
        if (hi > lo)
        {
            inCell = ExpressionFunctions.And(yawN >= loN, yawN < hiN);
        }
        else
        {
            // Wraparound: [loN, 1) ∪ [0, hiN).
            inCell = ExpressionFunctions.Or(yawN >= loN, yawN < hiN);
        }

        ScalarNode opacity = ExpressionFunctions.Conditional(inCell, (ScalarNode)1f, (ScalarNode)0f);

        tree.StartAnimation("Opacity", opacity);
    }

    public void Dispose()
    {
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

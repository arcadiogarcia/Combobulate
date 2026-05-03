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
/// Analytical "aspect-graph" renderer. The caller supplies a typed
/// <see cref="Matrix4x4Node"/> describing the model transform, plus a list
/// of <see cref="TransformAnimationAxis"/> entries identifying every live
/// scalar input the AST depends on. At setup the renderer:
///
/// <list type="bullet">
///   <item>Compiles <c>transformNode</c> via <c>ToExpressionString()</c>
///         and starts that animation on <c>_root.TransformMatrix</c>, so
///         the GPU paints the same transform the bake reasoned about.</item>
///   <item>Sweeps the supplied axes (1-D = bisection, 2+D = regular grid)
///         while overriding each axis's <c>LiveValueProvider</c> so
///         <c>transformNode.Evaluate()</c> returns the same matrix the
///         GPU will produce for those input values. The configured
///         <see cref="IFaceSorter"/> runs at every sample to compute the
///         painter signature.</item>
///   <item>Pre-builds one <c>ContainerVisual</c> per constant-painter-order
///         cell with sprites already in painter order. Each container's
///         <c>Opacity</c> is driven by an <c>ExpressionAnimation</c> that
///         tests whether the live axis values fall inside this cell's
///         axis-aligned box (modulo the periodic axes).</item>
/// </list>
///
/// <para>After bake, the runtime is pure compositor work: zero CPU per
/// frame, no clock drift possible by construction.</para>
/// </summary>
internal sealed class BakedAspectGraphRenderer : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;

    private ContainerVisual[]? _trees;

    // Bake inputs / cached state
    private Matrix4x4Node? _transformNode;
    private TransformAnimationAxis[]? _axes;
    private ObjGeometry? _bakedGeometry;
    private ResolvedQuadMaterials? _bakedBindings;
    private float _bakedScale, _bakedHostW, _bakedHostH;
    private float _bakedCullMarginCos, _bakedCameraDistance;
    private SortAlgorithm _bakedAlgorithm;

    public BakedAspectGraphRenderer(Compositor compositor, ContainerVisual parent)
    {
        _compositor = compositor;
        _parent = parent;
    }

    public int CellCount => _trees?.Length ?? 0;

    /// <summary>Bake K cells using either bisection (1-axis) or grid (N-axis).</summary>
    public int Bake(
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

        DisposeTrees();

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

        var sorter = FaceSorterFactory.Create(sortAlgorithm, geometry);

        // Override each axis's UI-thread provider; restore on completion.
        var sweep = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            int idx = i;
            axes[i].Scalar.SetLiveValueProvider(() => sweep[idx]);
        }

        try
        {
            if (axes.Length == 1)
            {
                BakeOneAxisInternal(sorter, geometry, bindings, scale, hostW, hostH,
                    cullMarginCos, cameraDistance, axes[0], transformNode, sweep);
            }
            else
            {
                BakeMultiAxisInternal(sorter, geometry, bindings, scale, hostW, hostH,
                    cullMarginCos, cameraDistance, axes, transformNode, sweep);
            }
        }
        finally
        {
            for (int i = 0; i < axes.Length; i++)
                axes[i].Scalar.SetLiveValueProvider(null);
        }

        return _trees!.Length;
    }

    private void BakeOneAxisInternal(
        IFaceSorter sorter,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        float cullMarginCos, float cameraDistance,
        TransformAnimationAxis axis,
        Matrix4x4Node transformNode,
        float[] sweep)
    {
        var cells = AspectGraphBake.BakeOneAxis(
            sorter, geometry,
            v =>
            {
                sweep[0] = axis.Min + v;   // shift coarse-sweep [0, Length) to axis input space
                return transformNode.Evaluate();
            },
            axis.Length,
            cullMarginCos,
            cameraDistance);

        _trees = new ContainerVisual[cells.Length];
        for (int i = 0; i < _trees.Length; i++)
        {
            var c = cells[i];
            var tree = _compositor.CreateContainerVisual();
            BuildTreeContent(tree, geometry, bindings, scale, hostW, hostH, c.Order, c.Visibility);
            // Translate relative-bounds back to absolute axis-input space.
            float lo = axis.Min + c.Lo;
            float hi = axis.Min + c.Hi;
            tree.StartAnimation("Opacity",
                BuildAxisInCellExpression(new[] { axis }, new[] { lo }, new[] { hi }));
            _parent.Children.InsertAtTop(tree);
            _trees[i] = tree;
        }
    }

    private void BakeMultiAxisInternal(
        IFaceSorter sorter,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        float cullMarginCos, float cameraDistance,
        TransformAnimationAxis[] axes,
        Matrix4x4Node transformNode,
        float[] sweep)
    {
        var bakeAxes = new AspectGraphBake.AxisSweep[axes.Length];
        for (int i = 0; i < axes.Length; i++)
            bakeAxes[i] = new AspectGraphBake.AxisSweep(axes[i].Min, axes[i].Length, axes[i].Samples, axes[i].Periodic);

        var cells = AspectGraphBake.BakeMultiAxis(
            sorter, geometry,
            input =>
            {
                Array.Copy(input, sweep, axes.Length);
                return transformNode.Evaluate();
            },
            bakeAxes,
            cullMarginCos,
            cameraDistance);

        _trees = new ContainerVisual[cells.Length];
        for (int i = 0; i < _trees.Length; i++)
        {
            var c = cells[i];
            var tree = _compositor.CreateContainerVisual();
            BuildTreeContent(tree, geometry, bindings, scale, hostW, hostH, c.Order, c.Visibility);
            tree.StartAnimation("Opacity",
                BuildAxisInCellExpression(axes, c.Lo, c.Hi));
            _parent.Children.InsertAtTop(tree);
            _trees[i] = tree;
        }
    }

    /// <summary>
    /// Build a typed opacity expression that returns 1 iff the live axes
    /// fall inside the supplied [lo, hi) box (per-axis, AND-ed across all
    /// axes). Periodic axes are wrapped mod Length before the test.
    /// </summary>
    private static ScalarNode BuildAxisInCellExpression(
        TransformAnimationAxis[] axes, float[] lo, float[] hi)
    {
        BooleanNode? acc = null;
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = axes[i];
            ScalarNode raw = axis.Scalar - (ScalarNode)axis.Min;
            ScalarNode normRange;
            if (axis.Periodic)
            {
                // (scalar - min) - floor((scalar - min)/Length) * Length, gives [0, Length).
                ScalarNode ratio = raw / (ScalarNode)axis.Length;
                normRange = raw - ExpressionFunctions.Floor(ratio) * (ScalarNode)axis.Length;
            }
            else
            {
                normRange = raw;
            }

            float relLo = lo[i] - axis.Min;
            float relHi = hi[i] - axis.Min;
            BooleanNode test;
            if (axis.Periodic && relHi <= relLo)
            {
                // Wrap cell: [relLo, Length) ∪ [0, relHi).
                test = ExpressionFunctions.Or(
                    normRange >= (ScalarNode)relLo,
                    normRange < (ScalarNode)relHi);
            }
            else
            {
                test = ExpressionFunctions.And(
                    normRange >= (ScalarNode)relLo,
                    normRange < (ScalarNode)relHi);
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

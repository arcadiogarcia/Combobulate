using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Sorting;

namespace Combobulate.Rendering;

/// <summary>
/// Pure-CPU bake math for the analytical aspect-graph renderer. Takes a
/// transform-vs-input function and decomposes its painter-sort behaviour
/// into a finite set of constant-order cells. Has no
/// <see cref="Microsoft.UI.Composition"/> dependencies so it is exercised
/// directly by the test project.
///
/// <para>Supports both 1-D bakes (one periodic input scalar, e.g. a yaw
/// rotation) and N-D grid bakes (multiple input scalars, e.g.
/// pitch+yaw or yaw+pitch+roll). The 1-D path uses bisection to find
/// breakpoints exactly (down to <see cref="BisectEpsilon"/>); the N-D path
/// uses a regular grid + adjacency merge for tractability.</para>
/// </summary>
internal static class AspectGraphBake
{
    public const int CoarseSamples1D = 720; // 0.5° step over a 360° period
    public const float BisectEpsilon = 1e-3f;
    public const int MaxBisectIterations = 24;

    /// <summary>
    /// A constant-painter-order cell in 1-D input space. Bounds are
    /// half-open [Lo, Hi); when Hi &lt; Lo the cell wraps the period.
    /// </summary>
    public readonly struct Cell1D
    {
        public Cell1D(float lo, float hi, int[] order, bool[] visibility)
        {
            Lo = lo; Hi = hi; Order = order; Visibility = visibility;
        }
        public float Lo { get; }
        public float Hi { get; }
        public int[] Order { get; }
        public bool[] Visibility { get; }
    }

    /// <summary>
    /// Sweep <paramref name="evaluate"/> across <c>[0, period)</c> and find
    /// every breakpoint where the painter ordering or visibility changes,
    /// using a coarse sweep + bisection. Returns one <see cref="Cell1D"/>
    /// per constant-order interval; cells partition the period.
    /// </summary>
    public static Cell1D[] BakeOneAxis(
        IFaceSorter sorter,
        ObjGeometry geometry,
        Func<float, Matrix4x4> evaluate,
        float period,
        float cullMarginCos = 0f,
        float cameraDistance = 0f,
        int coarseSamples = CoarseSamples1D)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        if (coarseSamples < 4) throw new ArgumentOutOfRangeException(nameof(coarseSamples));

        int q = geometry.Quads.Length;
        var orderBuf = new int[q];
        var visBuf = new bool[q];

        (int[] order, bool[] visibility) SortAt(float v)
        {
            int n = sorter.Sort(evaluate(v), orderBuf, visBuf, cameraDistance, cullMarginCos);
            var ord = new int[n];
            Array.Copy(orderBuf, ord, n);
            var vis = (bool[])visBuf.Clone();
            return (ord, vis);
        }

        var samples = new (int[] order, bool[] visibility)[coarseSamples];
        for (int i = 0; i < coarseSamples; i++)
        {
            float v = (i / (float)coarseSamples) * period;
            samples[i] = SortAt(v);
        }

        var breakpoints = new List<float>();
        for (int i = 0; i < coarseSamples; i++)
        {
            int j = (i + 1) % coarseSamples;
            if (!SignatureEquals(samples[i], samples[j]))
            {
                float lo = (i / (float)coarseSamples) * period;
                float hi = (j == 0) ? period : (j / (float)coarseSamples) * period;
                var loSig = samples[i];
                for (int k = 0; k < MaxBisectIterations && (hi - lo) > BisectEpsilon; k++)
                {
                    float mid = 0.5f * (lo + hi);
                    var midSig = SortAt(mid);
                    if (SignatureEquals(loSig, midSig)) lo = mid;
                    else hi = mid;
                }
                breakpoints.Add(hi);
            }
        }

        if (breakpoints.Count == 0)
        {
            return new[] { new Cell1D(0f, period, samples[0].order, samples[0].visibility) };
        }

        breakpoints.Sort();
        var cells = new Cell1D[breakpoints.Count];
        for (int i = 0; i < breakpoints.Count; i++)
        {
            float lo = breakpoints[i];
            float hi = breakpoints[(i + 1) % breakpoints.Count];
            float midV;
            if (hi > lo) midV = 0.5f * (lo + hi);
            else
            {
                float wrapLen = (period - lo) + hi;
                midV = (lo + 0.5f * wrapLen) % period;
            }
            var sig = SortAt(midV);
            cells[i] = new Cell1D(lo, hi, sig.order, sig.visibility);
        }
        return cells;
    }

    /// <summary>
    /// A constant-painter-order cell in N-D input space, represented as an
    /// axis-aligned box. Bounds are half-open per axis. Each axis may be
    /// periodic; if a cell wraps a periodic axis its hi-bound for that axis
    /// is less than its lo-bound.
    /// </summary>
    public readonly struct CellND
    {
        public CellND(float[] lo, float[] hi, int[] order, bool[] visibility)
        {
            Lo = lo; Hi = hi; Order = order; Visibility = visibility;
        }
        public float[] Lo { get; }
        public float[] Hi { get; }
        public int[] Order { get; }
        public bool[] Visibility { get; }
    }

    /// <summary>
    /// Describes an axis of the N-D input space: its sweep range
    /// <c>[Min, Min+Length)</c>, whether it is periodic (wraps at
    /// <c>Min+Length</c> back to <c>Min</c>), and how many grid samples to
    /// use.
    /// </summary>
    public readonly struct AxisSweep
    {
        public AxisSweep(float min, float length, int samples, bool periodic)
        {
            Min = min; Length = length; Samples = samples; Periodic = periodic;
        }
        public float Min { get; }
        public float Length { get; }
        public int Samples { get; }
        public bool Periodic { get; }
    }

    /// <summary>
    /// Multi-dimensional grid bake. Samples the painter signature at every
    /// vertex of an axis-aligned grid spanning the supplied
    /// <paramref name="axes"/>, then groups grid cells with identical
    /// signatures into a list of <see cref="CellND"/>. Adjacency-merged
    /// cells stay axis-aligned (no diagonal merges) so opacity expressions
    /// remain a simple AND of per-axis halfspace tests.
    /// </summary>
    public static CellND[] BakeMultiAxis(
        IFaceSorter sorter,
        ObjGeometry geometry,
        Func<float[], Matrix4x4> evaluate,
        AxisSweep[] axes,
        float cullMarginCos = 0f,
        float cameraDistance = 0f)
    {
        if (axes is null || axes.Length == 0) throw new ArgumentException("At least one axis required.", nameof(axes));
        int q = geometry.Quads.Length;
        var orderBuf = new int[q];
        var visBuf = new bool[q];

        // Sample at the centre of each grid cell so signatures represent
        // an interior point, not a boundary.
        var cellCounts = new int[axes.Length];
        long total = 1;
        for (int i = 0; i < axes.Length; i++)
        {
            cellCounts[i] = axes[i].Samples;
            total *= cellCounts[i];
            if (cellCounts[i] < 1) throw new ArgumentOutOfRangeException(nameof(axes), "Each axis needs at least 1 sample.");
        }

        var sigs = new (int[] order, bool[] visibility)[total];
        var input = new float[axes.Length];
        var indices = new int[axes.Length];
        for (long flat = 0; flat < total; flat++)
        {
            // Decompose flat -> indices.
            long rem = flat;
            for (int i = axes.Length - 1; i >= 0; i--)
            {
                indices[i] = (int)(rem % cellCounts[i]);
                rem /= cellCounts[i];
            }
            // Centre of each axis cell.
            for (int i = 0; i < axes.Length; i++)
            {
                float step = axes[i].Length / cellCounts[i];
                input[i] = axes[i].Min + (indices[i] + 0.5f) * step;
            }
            int n = sorter.Sort(evaluate(input), orderBuf, visBuf, cameraDistance, cullMarginCos);
            var ord = new int[n];
            Array.Copy(orderBuf, ord, n);
            sigs[flat] = (ord, (bool[])visBuf.Clone());
        }

        // For each grid cell emit one CellND. The previous greedy adjacency
        // merge had a subtle bug that produced overlapping cells; we keep
        // every grid cell separate and rely on the renderer's per-cell
        // ExpressionAnimation Opacity test to pick exactly one. Memory is
        // K_yaw * K_pitch * K_roll * Q SpriteVisuals — fine for low-poly
        // meshes (cube ~ 3456*6 = ~20k sprites at 24x12x12), and the
        // grid-cell-per-CellND construction is provably gap-free and
        // overlap-free.
        var result = new List<CellND>(checked((int)total));
        var idx = new int[axes.Length];
        for (long flat = 0; flat < total; flat++)
        {
            // Decompose flat -> indices.
            long rem = flat;
            for (int i = axes.Length - 1; i >= 0; i--)
            {
                idx[i] = (int)(rem % cellCounts[i]);
                rem /= cellCounts[i];
            }
            var lo = new float[axes.Length];
            var hi = new float[axes.Length];
            for (int i = 0; i < axes.Length; i++)
            {
                float step = axes[i].Length / cellCounts[i];
                lo[i] = axes[i].Min + idx[i] * step;
                hi[i] = axes[i].Min + (idx[i] + 1) * step;
            }
            result.Add(new CellND(lo, hi, sigs[flat].order, sigs[flat].visibility));
        }

        return result.ToArray();
    }

    private static bool SignatureEquals((int[] order, bool[] visibility) a, (int[] order, bool[] visibility) b)
    {
        if (a.order.Length != b.order.Length) return false;
        for (int i = 0; i < a.order.Length; i++)
            if (a.order[i] != b.order[i]) return false;
        if (a.visibility.Length != b.visibility.Length) return false;
        for (int i = 0; i < a.visibility.Length; i++)
            if (a.visibility[i] != b.visibility[i]) return false;
        return true;
    }
}

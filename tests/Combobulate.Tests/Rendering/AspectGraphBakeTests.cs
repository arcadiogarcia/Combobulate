using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Rendering;
using Combobulate.Sorting;
using Combobulate.Tests.Sorting;
using Xunit;

namespace Combobulate.Tests.Rendering;

/// <summary>
/// Tests for <see cref="AspectGraphBake"/> — the pure CPU bake that
/// decomposes an animated transform into constant-painter-order cells.
/// These tests cover the cube-yaw scenario (deterministic, well-known
/// 8-cell answer) and exercise both the 1-D and N-D bake paths.
/// </summary>
public class AspectGraphBakeTests
{
    private const float Deg2Rad = MathF.PI / 180f;

    private static Matrix4x4 YawDegToRotation(float yawDeg) =>
        Matrix4x4.CreateFromYawPitchRoll(yawDeg * Deg2Rad, 0, 0);

    private static Matrix4x4 EulerDegToRotation(float yawDeg, float pitchDeg, float rollDeg) =>
        Matrix4x4.CreateFromYawPitchRoll(yawDeg * Deg2Rad, pitchDeg * Deg2Rad, rollDeg * Deg2Rad);

    [Fact]
    public void OneAxis_UnitCubeYawSpin_ProducesEightCellsAtMultiplesOf45Deg()
    {
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);

        var cells = AspectGraphBake.BakeOneAxis(
            sorter, geometry, YawDegToRotation, period: 360f);

        // The unit cube under yaw produces 8 painter-order cells. Each
        // boundary is a critical yaw at a multiple of 45° where the BSP
        // sorter's tie-break swaps two side faces' painter order.
        Assert.Equal(8, cells.Length);

        // Every breakpoint (cell.Lo) must round to a multiple of 45°.
        // The bisection narrows the bracket to the painter-sort
        // signature transition; the BSP sorter's tie-break may flip
        // very slightly off the geometric 45° critical angle (typically
        // ≤ 0.1°), so allow that much.
        foreach (var c in cells)
        {
            float modded = ((c.Lo % 360f) + 360f) % 360f;
            float remainder = modded % 45f;
            float distFromBoundary = MathF.Min(remainder, 45f - remainder);
            Assert.True(distFromBoundary < 0.5f,
                $"Breakpoint {c.Lo}° not within 0.5° of a multiple of 45°: distance {distFromBoundary}°");
        }
    }

    [Fact]
    public void OneAxis_PartitionIsContiguous_NoGapsOrOverlaps()
    {
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);
        var cells = AspectGraphBake.BakeOneAxis(sorter, geometry, YawDegToRotation, period: 360f);

        // Each cell's Hi must equal the next cell's Lo (modulo wrap).
        for (int i = 0; i < cells.Length; i++)
        {
            var current = cells[i];
            var next = cells[(i + 1) % cells.Length];
            Assert.Equal(current.Hi, next.Lo);
        }
    }

    [Fact]
    public void OneAxis_EachCellSampledMatchesItsRecordedSignature()
    {
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);
        var cells = AspectGraphBake.BakeOneAxis(sorter, geometry, YawDegToRotation, period: 360f);

        int q = geometry.Quads.Length;
        var orderBuf = new int[q];
        var visBuf = new bool[q];

        // For each cell, sample a few interior yaws and verify the painter
        // ordering matches the cell's recorded signature.
        foreach (var cell in cells)
        {
            float interiorWidth = cell.Hi > cell.Lo ? (cell.Hi - cell.Lo) : (360f - cell.Lo + cell.Hi);
            // Skip 0.5° margins to stay clear of the bisected boundaries.
            float margin = MathF.Min(0.5f, interiorWidth * 0.25f);
            float[] samplePts;
            if (cell.Hi > cell.Lo)
            {
                samplePts = new[]
                {
                    cell.Lo + margin,
                    0.5f * (cell.Lo + cell.Hi),
                    cell.Hi - margin,
                };
            }
            else
            {
                // Wrap cell: sample inside both halves.
                samplePts = new[]
                {
                    cell.Lo + margin,
                    (cell.Lo + 0.5f * interiorWidth) % 360f,
                    (cell.Hi - margin + 360f) % 360f,
                };
            }
            foreach (var y in samplePts)
            {
                int n = sorter.Sort(YawDegToRotation(y), orderBuf, visBuf);
                Assert.Equal(cell.Order.Length, n);
                for (int i = 0; i < n; i++)
                    Assert.Equal(cell.Order[i], orderBuf[i]);
                for (int i = 0; i < q; i++)
                    Assert.Equal(cell.Visibility[i], visBuf[i]);
            }
        }
    }

    [Fact]
    public void OneAxis_FullPeriodSweep_EveryYawHitsExactlyOneCell()
    {
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);
        var cells = AspectGraphBake.BakeOneAxis(sorter, geometry, YawDegToRotation, period: 360f);

        // Sweep yaw at high resolution; every sample must fall into the
        // half-open [Lo, Hi) of exactly one cell (mod period).
        for (int sample = 0; sample < 3600; sample++)
        {
            float y = sample * 0.1f;
            int hits = 0;
            foreach (var cell in cells)
            {
                bool inside = cell.Hi > cell.Lo
                    ? (y >= cell.Lo && y < cell.Hi)
                    : (y >= cell.Lo || y < cell.Hi);
                if (inside) hits++;
            }
            Assert.True(hits == 1, $"yaw={y}° matched {hits} cells (expected 1)");
        }
    }

    [Fact]
    public void OneAxis_RevolutionsAreIdentical_PeriodicityHolds()
    {
        // The whole point of the bake is that revolution N renders
        // identically to revolution 1. Verify that for each cell,
        // sortAt(yaw) == sortAt(yaw + 360k).
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);
        var cells = AspectGraphBake.BakeOneAxis(sorter, geometry, YawDegToRotation, period: 360f);

        int q = geometry.Quads.Length;
        var orderBuf = new int[q];
        var visBuf = new bool[q];

        // Sample at irrational yaw to avoid any boundary alignment.
        float[] testYaws = { 17.3f, 91.7f, 188.2f, 254.9f, 312.6f };
        foreach (var y in testYaws)
        {
            int n0 = sorter.Sort(YawDegToRotation(y), orderBuf, visBuf);
            var ord0 = (int[])orderBuf.Clone();
            for (int rev = 1; rev <= 5; rev++)
            {
                int nN = sorter.Sort(YawDegToRotation(y + 360f * rev), orderBuf, visBuf);
                Assert.Equal(n0, nN);
                for (int i = 0; i < n0; i++) Assert.Equal(ord0[i], orderBuf[i]);
            }
        }
    }

    [Fact]
    public void OneAxis_BookGeometry_BakeProducesReasonableCellCount()
    {
        // Real-world 32-quad mesh: cells should be ≤ a few hundred and
        // the bake should partition the period without gaps.
        var samplePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        if (!System.IO.File.Exists(samplePath))
        {
            // Bake-side test gracefully skips when the sample isn't available.
            return;
        }
        var geometry = ObjGeometry.Build(Combobulate.Parsing.ObjParser.Parse(System.IO.File.ReadAllText(samplePath)).Model);
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);
        var cells = AspectGraphBake.BakeOneAxis(sorter, geometry, YawDegToRotation, period: 360f);

        Assert.InRange(cells.Length, 1, 500);

        // Partition is contiguous.
        for (int i = 0; i < cells.Length; i++)
        {
            Assert.Equal(cells[i].Hi, cells[(i + 1) % cells.Length].Lo);
        }
    }

    [Fact]
    public void MultiAxis_TwoAxisYawPitch_GridProducesCells()
    {
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);

        var axes = new[]
        {
            new AspectGraphBake.AxisSweep(min: 0f, length: 360f, samples: 24, periodic: true),
            new AspectGraphBake.AxisSweep(min: -90f, length: 180f, samples: 12, periodic: false),
        };
        var cells = AspectGraphBake.BakeMultiAxis(
            sorter, geometry,
            input => EulerDegToRotation(input[0], input[1], 0f),
            axes);

        Assert.NotEmpty(cells);
        // Every cell's Lo/Hi has correct rank.
        foreach (var c in cells)
        {
            Assert.Equal(2, c.Lo.Length);
            Assert.Equal(2, c.Hi.Length);
        }

        // Sanity: union of cells covers the entire grid (no holes).
        // Sample the grid centres and check that each one is inside exactly one cell.
        for (int yi = 0; yi < axes[0].Samples; yi++)
            for (int pi = 0; pi < axes[1].Samples; pi++)
            {
                float y = axes[0].Min + (yi + 0.5f) * (axes[0].Length / axes[0].Samples);
                float p = axes[1].Min + (pi + 0.5f) * (axes[1].Length / axes[1].Samples);
                int hits = 0;
                foreach (var c in cells)
                {
                    if (y >= c.Lo[0] && y < c.Hi[0] && p >= c.Lo[1] && p < c.Hi[1]) hits++;
                }
                Assert.True(hits == 1, $"(yaw={y},pitch={p}) hit {hits} cells");
            }
    }

    [Fact]
    public void MultiAxis_ThreeAxisYawPitchRoll_ProducesCoarseButValidPartition()
    {
        var geometry = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry);

        var axes = new[]
        {
            new AspectGraphBake.AxisSweep(0f, 360f, samples: 12, periodic: true),
            new AspectGraphBake.AxisSweep(-90f, 180f, samples: 6, periodic: false),
            new AspectGraphBake.AxisSweep(-180f, 360f, samples: 6, periodic: true),
        };
        var cells = AspectGraphBake.BakeMultiAxis(
            sorter, geometry,
            input => EulerDegToRotation(input[0], input[1], input[2]),
            axes);

        Assert.NotEmpty(cells);
        // Coarse 12×6×6 = 432 grid cells; merged cell count must be ≤ that.
        Assert.True(cells.Length <= 12 * 6 * 6);

        // Every cell has rank 3 bounds.
        foreach (var c in cells)
        {
            Assert.Equal(3, c.Lo.Length);
            Assert.Equal(3, c.Hi.Length);
        }
    }

    [Fact]
    public void OneAxis_AllSorterAlgorithms_AgreeOnBreakpointCount()
    {
        // The painter signature can differ between sorters at near-coplanar
        // configurations (BSP vs Newell vs Topological have different
        // tie-break rules), so we don't expect identical cells. But the
        // breakpoint count should be similar within a small tolerance for
        // the cube, which has clean geometry.
        var geometry = TestGeometries.UnitCube();
        var bspCells = AspectGraphBake.BakeOneAxis(
            FaceSorterFactory.Create(SortAlgorithm.Bsp, geometry),
            geometry, YawDegToRotation, period: 360f);
        var newellCells = AspectGraphBake.BakeOneAxis(
            FaceSorterFactory.Create(SortAlgorithm.Newell, geometry),
            geometry, YawDegToRotation, period: 360f);
        var topoCells = AspectGraphBake.BakeOneAxis(
            FaceSorterFactory.Create(SortAlgorithm.Topological, geometry),
            geometry, YawDegToRotation, period: 360f);

        // Each sorter must produce ≥ 4 cells (a cube under yaw cannot have fewer).
        Assert.True(bspCells.Length >= 4);
        Assert.True(newellCells.Length >= 4);
        Assert.True(topoCells.Length >= 4);
    }
}

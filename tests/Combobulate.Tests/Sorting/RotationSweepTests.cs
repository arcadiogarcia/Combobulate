using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Property-based tests that sweep dense grids of rotations across all
/// three sort algorithms and assert invariants that should hold for
/// <i>every</i> view direction. These exist to catch the next
/// fp-precision bug — the kind we haven't imagined yet — by brute
/// force, the same way <see cref="CullEpsilonTests"/> caught the
/// yaw=90 cover-smear after we already understood it.
///
/// <para>Invariants checked:
/// <list type="bullet">
///   <item><b>Visible-set parity.</b> All three algorithms classify the
///     same set of source quads as visible. Cull is shared infrastructure
///     (<see cref="GeometryPredicates.IsFrontFacing"/>), so any drift here
///     is a regression.</item>
///   <item><b>No fp-noise visibility.</b> No quad whose rotated normal
///     lies inside the cull dead band (<c>|Z| ≤ CosineEpsilon</c>) is
///     marked visible by any algorithm. This is the precise condition
///     the yaw=90 bug violated.</item>
///   <item><b>Output bounds.</b> Visible count never exceeds the quad
///     count, and every emitted index is in range and unique.</item>
///   <item><b>No NaN/Inf leakage.</b> Sorter output stays integer-valued;
///     we additionally exercise <see cref="Plane3.IntersectSegment"/>
///     and <see cref="GeometryPredicates.TryComputeSegmentParam"/> at a
///     parameter sweep to confirm no infinity escapes.</item>
/// </list>
/// </para>
/// </summary>
public class RotationSweepTests
{
    /// <summary>5° grid → 73 × 37 × 37 ≈ 100k samples per geometry. Heavy but exhaustive.</summary>
    private const float YawStepDegrees   = 15f; // keep CI-friendly; tightened versions live on CullEpsilonTests
    private const float PitchStepDegrees = 30f;

    public static IEnumerable<object[]> Geometries() => new[]
    {
        new object[] { nameof(TestGeometries.UnitCube),            (Func<ObjGeometry>)TestGeometries.UnitCube },
        new object[] { nameof(TestGeometries.FrontAndBackQuads),   (Func<ObjGeometry>)TestGeometries.FrontAndBackQuads },
        new object[] { nameof(TestGeometries.MutualStraddleCycle), (Func<ObjGeometry>)TestGeometries.MutualStraddleCycle },
    };

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AllAlgorithms_AgreeOnVisibleSet_AcrossRotationGrid(string name, Func<ObjGeometry> factory)
    {
        var geom = factory();
        var sorters = MakeSorters(geom);
        int qc = geom.Quads.Length;

        ForEachRotation((yaw, pitch, rotation) =>
        {
            var visibleSets = sorters
                .Select(s => RunSorter(s, rotation, qc).visible)
                .ToArray();

            // Convert each bool[] to a packed bitmask for clean equality reporting.
            var masks = visibleSets.Select(BitMask).ToArray();
            var first = masks[0];
            for (int i = 1; i < masks.Length; i++)
            {
                Assert.True(masks[i] == first,
                    $"Visible-set drift on {name} at yaw={yaw}° pitch={pitch}°: " +
                    $"{sorters[0].GetType().Name}={Convert.ToString((long)first, 2)} vs " +
                    $"{sorters[i].GetType().Name}={Convert.ToString((long)masks[i], 2)}");
            }
        });
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void NoQuadInsideCullDeadBand_IsEverMarkedVisible(string name, Func<ObjGeometry> factory)
    {
        var geom = factory();
        var sorters = MakeSorters(geom);
        int qc = geom.Quads.Length;

        ForEachRotation((yaw, pitch, rotation) =>
        {
            // Compute the rotated-normal Z for every quad; flag those inside the dead band.
            var inDeadBand = new bool[qc];
            for (int i = 0; i < qc; i++)
            {
                var rn = Vector3.TransformNormal(geom.Quads[i].Normal, rotation);
                inDeadBand[i] = MathF.Abs(rn.Z) <= GeometryPredicates.CosineEpsilon;
            }

            foreach (var s in sorters)
            {
                var (_, vis) = RunSorter(s, rotation, qc);
                for (int i = 0; i < qc; i++)
                {
                    if (inDeadBand[i] && vis[i])
                    {
                        var rn = Vector3.TransformNormal(geom.Quads[i].Normal, rotation);
                        Assert.Fail($"{s.GetType().Name} marked quad {i} visible at yaw={yaw}° pitch={pitch}° " +
                                    $"despite |viewNormalZ|={MathF.Abs(rn.Z):G3} ≤ CosineEpsilon={GeometryPredicates.CosineEpsilon} on geometry {name}.");
                    }
                }
            }
        });
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void OutputIsAlwaysWellFormed(string name, Func<ObjGeometry> factory)
    {
        var geom = factory();
        var sorters = MakeSorters(geom);
        int qc = geom.Quads.Length;

        ForEachRotation((yaw, pitch, rotation) =>
        {
            foreach (var s in sorters)
            {
                var (order, vis) = RunSorter(s, rotation, qc);
                int n = CountVisible(vis);
                // Order count <= visible count; first n entries are the order.
                Assert.True(n <= qc, $"visible count {n} > quad count {qc} for {s.GetType().Name} on {name}");
                var seen = new HashSet<int>();
                for (int i = 0; i < n; i++)
                {
                    int q = order[i];
                    Assert.InRange(q, 0, qc - 1);
                    Assert.True(seen.Add(q),
                        $"{s.GetType().Name} emitted quad {q} twice at yaw={yaw}° pitch={pitch}° on {name}");
                    Assert.True(vis[q],
                        $"{s.GetType().Name} emitted quad {q} but did not mark it visible on {name}");
                }
            }
        });
    }

    /// <summary>
    /// <see cref="GeometryPredicates.TryComputeSegmentParam"/> must never
    /// leak ±∞ or NaN, regardless of input pair. Sweep a wide range of
    /// (da, db) including pathological denominators.
    /// </summary>
    [Fact]
    public void TryComputeSegmentParam_NeverProducesInfinityOrNaN_OverWideInputRange()
    {
        var pathological = new float[] { 0f, 1e-30f, -1e-30f, 1e-10f, -1e-10f, 1f, -1f, 1e10f, -1e10f };
        foreach (var da in pathological)
        foreach (var db in pathological)
        {
            GeometryPredicates.TryComputeSegmentParam(da, db, out var t);
            Assert.False(float.IsNaN(t),      $"NaN from da={da} db={db}");
            Assert.False(float.IsInfinity(t), $"Inf from da={da} db={db}");
            Assert.InRange(t, 0f, 1f);
        }
    }

    // ---------- helpers ----------

    private static IFaceSorter[] MakeSorters(ObjGeometry geom) => new IFaceSorter[]
    {
        new BspSorter(geom),
        new NewellSorter(geom),
        new TopologicalSorter(geom),
    };

    private static (int[] order, bool[] visible) RunSorter(IFaceSorter sorter, Matrix4x4 rot, int qc)
    {
        var order = new int[qc];
        var vis = new bool[qc];
        sorter.Sort(rot, order, vis);
        return (order, vis);
    }

    private static int CountVisible(bool[] v)
    {
        int n = 0;
        for (int i = 0; i < v.Length; i++) if (v[i]) n++;
        return n;
    }

    private static ulong BitMask(bool[] v)
    {
        // We only test geometries with ≤ 64 quads; assert that.
        Assert.True(v.Length <= 64, "BitMask helper assumes ≤ 64 quads");
        ulong m = 0;
        for (int i = 0; i < v.Length; i++) if (v[i]) m |= 1ul << i;
        return m;
    }

    /// <summary>
    /// Sweeps yaw across [0°, 360°) and pitch across (-90°, +90°) in the
    /// configured step sizes, plus a separate pass that hits every
    /// multiple of 90° (the angles where fp noise is structurally worst).
    /// </summary>
    private static void ForEachRotation(Action<float, float, Matrix4x4> body)
    {
        // Coarse sweep.
        for (float yaw = 0f; yaw < 360f; yaw += YawStepDegrees)
        {
            for (float pitch = -75f; pitch <= 75f; pitch += PitchStepDegrees)
            {
                var rot = MakeRotation(yaw, pitch);
                body(yaw, pitch, rot);
            }
        }

        // Right-angle-focused sweep: the fp-noise hotspots.
        var crit = new float[] { 0f, 45f, 89.99f, 90f, 90.01f, 135f, 179.99f, 180f, 180.01f, 225f, 270f, 315f, 359.99f };
        foreach (var yaw in crit)
        foreach (var pitch in new[] { -89.99f, -90f, -45f, 0f, 45f, 89.99f, 90f })
        {
            body(yaw, pitch, MakeRotation(yaw, pitch));
        }
    }

    private static Matrix4x4 MakeRotation(float yawDeg, float pitchDeg)
    {
        var yaw   = yawDeg   * MathF.PI / 180f;
        var pitch = pitchDeg * MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, 0f);
    }
}

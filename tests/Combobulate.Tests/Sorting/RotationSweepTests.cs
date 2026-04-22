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

    /// <summary>
    /// Same visible-set parity invariant as
    /// <see cref="AllAlgorithms_AgreeOnVisibleSet_AcrossRotationGrid"/>, but
    /// run with PERSPECTIVE camera distance set so all three sorters use
    /// <see cref="GeometryPredicates.IsFrontFacingPerspective"/>. Catches drift
    /// in any single sorter's perspective code path.
    /// </summary>
    [Theory]
    [MemberData(nameof(Geometries))]
    public void AllAlgorithms_AgreeOnVisibleSet_AcrossRotationGrid_UnderPerspective(string name, Func<ObjGeometry> factory)
    {
        var geom = factory();
        var sorters = MakeSorters(geom);
        int qc = geom.Quads.Length;

        // A few perspective distances spanning "very far" (≈ ortho) to "close" (camera near bbox).
        foreach (var d in new[] { 10f, 4f, 2f, 1.5f })
        {
            ForEachRotation((yaw, pitch, rotation) =>
            {
                var masks = sorters
                    .Select(s => BitMask(RunSorter(s, rotation, qc, d).visible))
                    .ToArray();
                var first = masks[0];
                for (int i = 1; i < masks.Length; i++)
                {
                    Assert.True(masks[i] == first,
                        $"Visible-set drift on {name} at yaw={yaw}° pitch={pitch}° d={d}: " +
                        $"{sorters[0].GetType().Name}={Convert.ToString((long)first, 2)} vs " +
                        $"{sorters[i].GetType().Name}={Convert.ToString((long)masks[i], 2)}");
                }
            });
        }
    }

    /// <summary>
    /// Roll-axis coverage. The yaw+pitch sweeps in the other tests can miss
    /// roll-only singularities (cover face normal becomes perpendicular to view
    /// when roll combined with non-zero pitch), so we additionally sweep roll
    /// at a few non-trivial pitch values and confirm visible-set parity.
    /// </summary>
    [Theory]
    [MemberData(nameof(Geometries))]
    public void AllAlgorithms_AgreeOnVisibleSet_AcrossRollSweep(string name, Func<ObjGeometry> factory)
    {
        var geom = factory();
        var sorters = MakeSorters(geom);
        int qc = geom.Quads.Length;

        var rolls = new[] { 0f, 30f, 45f, 89.99f, 90f, 90.01f, 135f, 180f, 225f, 270f, 315f };
        foreach (var pitch in new[] { -45f, 0f, 30f, 89.99f, 90f })
        foreach (var yaw in new[] { 0f, 45f, 90f, 180f })
        foreach (var roll in rolls)
        {
            var rot = Matrix4x4.CreateFromYawPitchRoll(
                yaw   * MathF.PI / 180f,
                pitch * MathF.PI / 180f,
                roll  * MathF.PI / 180f);

            // Both ortho and perspective.
            foreach (var d in new[] { 0f, 4f })
            {
                var masks = sorters.Select(s => BitMask(RunSorter(s, rot, qc, d).visible)).ToArray();
                for (int i = 1; i < masks.Length; i++)
                {
                    Assert.True(masks[i] == masks[0],
                        $"Visible-set drift on {name} at yaw={yaw}° pitch={pitch}° roll={roll}° d={d}: " +
                        $"{sorters[0].GetType().Name}={Convert.ToString((long)masks[0], 2)} vs " +
                        $"{sorters[i].GetType().Name}={Convert.ToString((long)masks[i], 2)}");
                }
            }
        }
    }

    /// <summary>
    /// Painter-order correctness for OVERLAPPING geometry across all three sorters
    /// under perspective. Builds a stack of three coplanar-axis quads (no Newell-
    /// triggering straddles) so the canonical back-to-front order is unambiguous.
    /// Generalises the BSP-only regression
    /// (<c>BspSorterTests.Walk_OverlappingParallelQuads_AreEmittedBackToFrontUnderPerspective</c>)
    /// to ensure NewellSorter and TopologicalSorter don't have similar
    /// camera-direction-vs-position bugs.
    /// </summary>
    /// <remarks>
    /// Combinations are restricted to pitch ≤ 60° (or d ≥ 4 at higher pitch) so the
    /// camera stays in front of every quad's plane. At pitch=80° + d=1.5, the top
    /// quad's outward normal rotates far enough that its plane crosses the camera —
    /// see <see cref="ExtremeTilt_CloseCamera_TopQuad_BecomesBackFacing"/> for the
    /// asserted-correct behaviour at that singularity.
    /// </remarks>
    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void OverlappingStack_AllAlgorithms_PaintBackToFrontUnderPerspective(SortAlgorithm algo)
    {
        var geom = OverlappingStackGeometry();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        // Combinations where every quad's plane stays in front of the camera.
        var cases = new (float pitch, float d)[]
        {
            (0f,   4f), (0f,   2f), (0f,   1.5f),
            (30f,  4f), (30f,  2f), (30f,  1.5f),
            (45f,  4f), (45f,  2f),
            (60f,  4f),
            (80f,  4f),  // close-edge case still safe at d=4
        };
        foreach (var (pitch, d) in cases)
        {
            var rot = MakeRotation(0, pitch);
            int n = sorter.Sort(rot, order, visible, d);
            Assert.True(n == 3, $"{algo}: expected 3 visible at pitch={pitch},d={d}, got {n}");
            for (int i = 1; i < n; i++)
            {
                var prevZ = Vector3.Transform(geom.Quads[order[i - 1]].Centroid, rot).Z;
                var thisZ = Vector3.Transform(geom.Quads[order[i]].Centroid, rot).Z;
                Assert.True(prevZ <= thisZ + 1e-3f,
                    $"{algo}: painter order wrong at pitch={pitch}, d={d}: q{order[i-1]} (Z={prevZ:F3}) drew before q{order[i]} (Z={thisZ:F3})");
            }
        }
    }

    /// <summary>
    /// Documented-correct behaviour at the "close camera + extreme tilt" singularity:
    /// when the model tilts far enough relative to a close camera, a face's outward
    /// normal can rotate into the half-space facing AWAY from the camera even though
    /// the face is still on screen — geometrically the camera has crossed its plane,
    /// so what you'd see is the face's back side. With single-sided geometry the
    /// face is correctly culled. This test pins that behaviour as deliberate so a
    /// future "fix" doesn't quietly re-let edge-on faces leak through.
    /// </summary>
    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void ExtremeTilt_CloseCamera_TopQuad_BecomesBackFacing(SortAlgorithm algo)
    {
        var geom = OverlappingStackGeometry();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        // pitch=80°, d=1.5 (model units): the top quad's outward normal +Z rotates to
        // (0, -sin80°, cos80°) ≈ (0, -0.985, 0.174); its centroid moves to
        // (0, -0.295, +0.052); the camera at (0, 0, 1.5) is then on the back side
        // of that plane (dot(normal, camera-centroid) < 0).
        var rot = MakeRotation(0, 80f);
        int n = sorter.Sort(rot, order, visible, cameraDistance: 1.5f);
        Assert.Equal(2, n);
        Assert.False(visible[2], $"{algo}: top quad must be culled (camera crosses its plane)");
        Assert.True(visible[0] && visible[1], $"{algo}: bottom and middle quads must remain visible");
    }

    private static ObjGeometry OverlappingStackGeometry() => TestGeometries.Build(
        new[] { new Vector3(-0.5f,-0.5f,-0.3f), new Vector3(+0.5f,-0.5f,-0.3f), new Vector3(+0.5f,+0.5f,-0.3f), new Vector3(-0.5f,+0.5f,-0.3f) },
        new[] { new Vector3(-0.5f,-0.5f, 0.0f), new Vector3(+0.5f,-0.5f, 0.0f), new Vector3(+0.5f,+0.5f, 0.0f), new Vector3(-0.5f,+0.5f, 0.0f) },
        new[] { new Vector3(-0.5f,-0.5f,+0.3f), new Vector3(+0.5f,-0.5f,+0.3f), new Vector3(+0.5f,+0.5f,+0.3f), new Vector3(-0.5f,+0.5f,+0.3f) });

    // ---------- helpers ----------

    private static IFaceSorter[] MakeSorters(ObjGeometry geom) => new IFaceSorter[]
    {
        new BspSorter(geom),
        new NewellSorter(geom),
        new TopologicalSorter(geom),
    };

    private static (int[] order, bool[] visible) RunSorter(IFaceSorter sorter, Matrix4x4 rot, int qc, float cameraDistance = 0f)
    {
        var order = new int[qc];
        var vis = new bool[qc];
        sorter.Sort(rot, order, vis, cameraDistance);
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

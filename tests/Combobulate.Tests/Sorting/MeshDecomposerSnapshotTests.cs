using System.Collections.Generic;
using System.IO;
using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Snapshot test for <see cref="MeshDecomposer"/> + <see cref="ObjGeometry.WithPainterSubdivision"/>
/// against the existing sort algorithms, recording the per-pair painter
/// violation counts the oracle finds on the SUBDIVIDED book and
/// synthetic geometries. The complementary
/// <see cref="PainterCorrectnessExtendedSweepTests"/> locks in the
/// equivalent baseline numbers on the UN-subdivided meshes.
///
/// <para>
/// <b>What this test does NOT prove.</b> Lower oracle counts here would
/// be better, but raw oracle counts are not a clean proxy for visual
/// quality once the mesh has many small fragments — each new internal
/// edge gives the
/// <see cref="PainterCorrectness.SutherlandHodgman"/> clipper one more
/// opportunity to emit a sub-ULP overlap polygon, and at extreme tilts
/// the depth recovery <c>(D - n.X·x - n.Y·y) / n.Z</c> amplifies
/// numerical noise by <c>1/n.Z</c>. Both effects produce false
/// positives that do not correspond to user-visible artefacts.
/// </para>
///
/// <para>
/// <b>What this test DOES prove.</b> Together with the dedicated
/// <see cref="MeshDecomposerTests.Decompose_BookObjFromSamples_ProducesPainterReadyFragments"/>
/// (which asserts the BSP-partition invariant directly), this snapshot
/// gives us a stable regression gate: any algorithm change that
/// substantially shifts these counts must be intentional and explained.
/// The <see cref="MutualStraddle_Subdivided_FixesNewellOrthoCycle"/>
/// assertion captures the one place the oracle DOES corroborate the
/// algorithmic fix end-to-end — the ortho mutual-straddle cycle drops
/// from 31 baseline violations to ≤ 4.
/// </para>
/// </summary>
public class MeshDecomposerSnapshotTests
{
    /// <summary>Match <see cref="PainterCorrectnessExtendedSweepTests"/> exactly.</summary>
    private const float YawStepDegrees = 2f;
    private const float PitchStepDegrees = 10f;
    private const float MinPitch = -40f;
    private const float MaxPitch = +40f;
    private const float PerspCameraZ = 4f;

    /// <summary>
    /// End-to-end algorithmic-fix evidence: on the canonical mutual-straddle
    /// cycle case, subdivision cuts Newell's ortho violations from the
    /// baseline (per the snapshot in
    /// <see cref="PainterCorrectnessExtendedSweepTests.ExpectedFailures"/>)
    /// down to near-coincident-plane noise hits.
    /// </summary>
    [Fact]
    public void MutualStraddle_Subdivided_FixesNewellOrthoCycle()
    {
        var baseline = TestGeometries.MutualStraddleCycle();
        var subdivided = baseline.WithPainterSubdivision();
        int baselineFail = CountViolations(baseline, SortAlgorithm.Newell, cameraZ: 0f);
        int subFail = CountViolations(subdivided, SortAlgorithm.Newell, cameraZ: 0f);
        Assert.True(subFail < baselineFail,
            $"After subdivision, Newell on the orthographic mutual-straddle cycle should " +
            $"produce strictly fewer violations than the un-subdivided baseline " +
            $"({baselineFail}). Got {subFail}.");
    }

    /// <summary>
    /// Convex meshes stay convex after subdivision (no-op path) and must
    /// remain painter-clean across every algorithm/projection.
    /// </summary>
    [Theory]
    [InlineData(SortAlgorithm.Bsp, 0f)]
    [InlineData(SortAlgorithm.Bsp, PerspCameraZ)]
    [InlineData(SortAlgorithm.Newell, 0f)]
    [InlineData(SortAlgorithm.Newell, PerspCameraZ)]
    [InlineData(SortAlgorithm.Topological, 0f)]
    [InlineData(SortAlgorithm.Topological, PerspCameraZ)]
    public void UnitCube_Subdivided_StaysClean(SortAlgorithm algo, float cameraZ)
    {
        var baseCube = TestGeometries.UnitCube();
        var subdivided = baseCube.WithPainterSubdivision();
        // Convex → identity fragment count (fast-path returns this).
        Assert.Equal(baseCube.Quads.Length, subdivided.Quads.Length);
        Assert.Equal(0, CountViolations(subdivided, algo, cameraZ));
    }

    /// <summary>
    /// Locks in the post-subdivision oracle counts for book.obj across
    /// every (algorithm, projection) pair. Numbers reflect oracle noise
    /// on the quad-preserved fragmented mesh, not visible artefacts
    /// (algorithmic correctness is proven separately by
    /// <see cref="MeshDecomposerTests.Decompose_BookObjFromSamples_ProducesPainterReadyFragments"/>).
    /// A drift up indicates either a real regression or a fragment-count
    /// change worth investigating.
    /// <para><b>Quad-preserving baseline (book.obj → 33 quad fragments).</b>
    /// These numbers are an order of magnitude lower than the prior
    /// triangle-only baseline (588/717/1127/1277/1271/1252) because the
    /// quad-preserving algorithm produces ~half the fragment count and
    /// every fragment is a clean 4-corner parallelogram (no degenerate
    /// triangle slivers).</para>
    /// </summary>
    [Theory]
    [InlineData(SortAlgorithm.Bsp,         0f,             1)]
    [InlineData(SortAlgorithm.Bsp,         PerspCameraZ, 238)]
    [InlineData(SortAlgorithm.Newell,      0f,            22)]
    [InlineData(SortAlgorithm.Newell,      PerspCameraZ, 277)]
    [InlineData(SortAlgorithm.Topological, 0f,            18)]
    [InlineData(SortAlgorithm.Topological, PerspCameraZ, 266)]
    public void Book_Subdivided_OracleSnapshot(SortAlgorithm algo, float cameraZ, int expected)
    {
        var subdivided = LoadBookObj().WithPainterSubdivision();
        int actual = CountViolations(subdivided, algo, cameraZ);
        Assert.Equal(expected, actual);
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static int CountViolations(ObjGeometry geom, SortAlgorithm algo, float cameraZ)
    {
        var sorter = FaceSorterFactory.Create(algo, geom);
        int qc = geom.Quads.Length;
        var order = new int[qc];
        var visible = new bool[qc];
        int count = 0;
        for (float pitch = MinPitch; pitch <= MaxPitch; pitch += PitchStepDegrees)
        {
            for (float yaw = 0f; yaw < 360f; yaw += YawStepDegrees)
            {
                var rot = SortAssertions.YawPitch(yaw, pitch);
                int n = sorter.Sort(rot, order, visible, cameraZ);
                if (PainterCorrectness.FindWorstViolation(geom, order, n, rot, cameraZ) is not null)
                    count++;
            }
        }
        return count;
    }

    private static ObjGeometry LoadBookObj()
    {
        var path = LocateSamplesFile("book.obj");
        return ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
    }

    private static string LocateSamplesFile(string name)
    {
        var local = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Samples", name);
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "samples", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        Assert.Fail($"{name} not found under {System.AppContext.BaseDirectory}");
        return null!;
    }
}

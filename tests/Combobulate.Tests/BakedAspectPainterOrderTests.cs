using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Sorting;
using Combobulate.Tests.Sorting;
using Xunit;

namespace Combobulate.Tests;

/// <summary>
/// Regression tests for the BakedAspectGraph (Phase 0) painter-order
/// algorithm. The renderer's visible code paths can't run in this test
/// project because they pull in WinUI Composition; instead we test the
/// pure-math core that determines what a baked signature lights and in
/// what order:
///
/// <list type="number">
/// <item>For a given rotation, classify each face as front- or
///   back-facing using <c>(M · n).z &gt; 0</c> (the BAG visibility
///   convention).</item>
/// <item>Topologically sort visible faces by static model-space
///   plane-side relations (the BAG painter-order convention).</item>
/// <item>Assert via <see cref="PainterCorrectness.FindWorstViolation"/>
///   that no pair of overlapping visible faces is drawn in the wrong
///   z-order at this rotation.</item>
/// </list>
///
/// <para>This single property — "for every rotation, the BAG painter
/// order produces zero pixel-level violations" — would have caught
/// every bug from the recent session: cells-disappear (missing
/// signatures), pages-under-cover (centroid-Z mis-order), pair-sign
/// noise (precision-disagreement). The test runs against both the
/// built-in cube and the book sample at thousands of random rotations
/// per case, so degenerate angles (gimbal lock, axis-aligned, near-
/// coplanar) are exercised by sheer volume.</para>
/// </summary>
public class BakedAspectPainterOrderTests
{
    private const int RandomRotationsPerModel = 2000;
    private const int RandomSeed = 0xBA6_ED;

    [Fact]
    public void Cube_PainterOrderHasNoViolationsAcrossRandomRotations()
    {
        var geom = ObjGeometry.Build(ObjParser.Parse(CubeObj).Model);
        var fail = SweepRandomRotations(geom, RandomRotationsPerModel, RandomSeed);
        Assert.True(fail.violationCount == 0,
            $"cube: {fail.violationCount}/{RandomRotationsPerModel} rotations produced painter-order violations. " +
            $"first failure at rotation {fail.firstFailRotation:F4} (yaw,pitch,roll deg = {fail.firstFailYpr}); " +
            $"violation: {fail.firstViolation}");
    }

    [Fact]
    public void Book_PainterOrderHasNoViolationsAcrossRandomRotations()
    {
        // book.obj is copied to output via the test csproj; resolve from base.
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        Assert.True(File.Exists(path), $"book.obj missing at {path}");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var fail = SweepRandomRotations(geom, RandomRotationsPerModel, RandomSeed);
        Assert.True(fail.violationCount == 0,
            $"book: {fail.violationCount}/{RandomRotationsPerModel} rotations produced painter-order violations. " +
            $"first failure at rotation {fail.firstFailRotation:F4} (yaw,pitch,roll deg = {fail.firstFailYpr}); " +
            $"violation: {fail.firstViolation}");
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(45f, 0f, 0f)]
    [InlineData(90f, 0f, 0f)]
    [InlineData(180f, 0f, 0f)]
    [InlineData(0f, 45f, 0f)]
    [InlineData(0f, 90f, 0f)]      // pitch = +90: book.obj single-sided geometry — top page strip dominant
    [InlineData(0f, -90f, 0f)]     // pitch = -90: bottom page strip
    [InlineData(31f, -112f, 0f)]   // the original "pages under back cover" failure
    [InlineData(-47f, -33f, 0f)]   // the v110 "no signature matches" failure
    [InlineData(40f, 20f, 0f)]
    [InlineData(135f, 0f, 0f)]
    [InlineData(225f, 0f, 0f)]
    public void Book_KnownRotationsHaveNoViolations(float yawDeg, float pitchDeg, float rollDeg)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var rot = MakeRotation(yawDeg, pitchDeg, rollDeg);
        AssertNoViolation(geom, rot, $"book yaw={yawDeg} pitch={pitchDeg} roll={rollDeg}");
    }

    [Fact]
    public void Cube_AllSixFacesAppearInSomeRotation()
    {
        var geom = ObjGeometry.Build(ObjParser.Parse(CubeObj).Model);
        var seen = new bool[geom.Quads.Length];
        var rng = new Random(RandomSeed);
        for (int r = 0; r < 1000; r++)
        {
            var rot = RandomRotation(rng);
            var (vis, _) = ComputeBakedAspectOrder(geom, rot);
            for (int f = 0; f < vis.Length; f++) if (vis[f]) seen[f] = true;
        }
        for (int f = 0; f < seen.Length; f++)
            Assert.True(seen[f], $"cube face {f} never front-facing in 1000 random rotations");
    }

    // --- core helpers ---

    private struct SweepResult
    {
        public int violationCount;
        public Matrix4x4 firstFailRotation;
        public Vector3 firstFailYpr;
        public string firstViolation;
    }

    private static SweepResult SweepRandomRotations(ObjGeometry geom, int count, int seed)
    {
        var result = new SweepResult { violationCount = 0, firstViolation = "" };
        var rng = new Random(seed);
        for (int r = 0; r < count; r++)
        {
            (var rot, var ypr) = RandomRotationAndAngles(rng);
            var (visibility, order) = ComputeBakedAspectOrder(geom, rot);
            int visibleCount = 0;
            for (int i = 0; i < visibility.Length; i++) if (visibility[i]) visibleCount++;
            if (visibleCount < 2) continue; // can't have a pair-violation with <2 visible

            var v = PainterCorrectness.FindWorstViolation(geom, order, visibleCount, rot);
            if (v.HasValue)
            {
                if (result.violationCount == 0)
                {
                    result.firstFailRotation = rot;
                    result.firstFailYpr = ypr;
                    result.firstViolation = v.Value.ToString();
                }
                result.violationCount++;
            }
        }
        return result;
    }

    private static void AssertNoViolation(ObjGeometry geom, Matrix4x4 rot, string label)
    {
        var (visibility, order) = ComputeBakedAspectOrder(geom, rot);
        int visibleCount = 0;
        for (int i = 0; i < visibility.Length; i++) if (visibility[i]) visibleCount++;
        if (visibleCount < 2) return;
        var v = PainterCorrectness.FindWorstViolation(geom, order, visibleCount, rot);
        Assert.False(v.HasValue, $"{label}: painter-order violation: {v}");
    }

    /// <summary>
    /// Replicates the bake's per-sample painter-order computation
    /// (<c>SignatureBake.ProcessSample</c> + <c>EmitSignature</c>) without
    /// any Composition or ExpressionsFork dependencies. If this gets out
    /// of step with the bake the tests are silently testing the wrong
    /// thing, so any change to the bake's painter logic must be mirrored
    /// here. Keeping it short on purpose so divergence is obvious in code
    /// review.
    /// </summary>
    private static (bool[] visibility, int[] order) ComputeBakedAspectOrder(ObjGeometry geom, in Matrix4x4 M)
    {
        var quads = geom.Quads;
        int n = quads.Length;
        var visibility = new bool[n];
        for (int q = 0; q < n; q++)
        {
            var nrm = quads[q].Normal;
            float nz = nrm.X * M.M13 + nrm.Y * M.M23 + nrm.Z * M.M33;
            visibility[q] = nz > 0f;
        }

        // Static plane-side relations (rotation-invariant): planeSide[a,b] =
        // sign(n_b · (c_a − c_b)). +1 → a is on the viewer-facing side of b.
        var visibleIdxs = new List<int>(n);
        for (int q = 0; q < n; q++) if (visibility[q]) visibleIdxs.Add(q);
        visibleIdxs.Sort((a, b) =>
        {
            // a in front of b iff a is on the +n_b side AND b is on the -n_a side.
            float ab = Vector3.Dot(quads[b].Normal, quads[a].Centroid - quads[b].Centroid);
            float ba = Vector3.Dot(quads[a].Normal, quads[b].Centroid - quads[a].Centroid);
            if (ab < 0f && ba > 0f) return -1; // a behind b → a drawn first
            if (ab > 0f && ba < 0f) return +1; // a in front → drawn last
            return a.CompareTo(b); // tie-break by face index for stability
        });

        return (visibility, visibleIdxs.ToArray());
    }

    private static Matrix4x4 MakeRotation(float yawDeg, float pitchDeg, float rollDeg)
    {
        const float D2R = MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(yawDeg * D2R, pitchDeg * D2R, rollDeg * D2R);
    }

    private static (Matrix4x4, Vector3) RandomRotationAndAngles(Random rng)
    {
        // Uniform sample in (yaw, pitch, roll). Not a uniform SO(3)
        // distribution but adequate coverage of degenerate axis-aligned
        // cases when count is large.
        float yaw = (float)((rng.NextDouble() * 360.0) - 180.0);
        float pitch = (float)((rng.NextDouble() * 360.0) - 180.0);
        float roll = (float)((rng.NextDouble() * 360.0) - 180.0);
        return (MakeRotation(yaw, pitch, roll), new Vector3(yaw, pitch, roll));
    }

    private static Matrix4x4 RandomRotation(Random rng) => RandomRotationAndAngles(rng).Item1;

    // Same cube the sample app uses: 6 axis-aligned quads, V0→V1→V3 wound outward.
    private const string CubeObj = """
        v -1 -1 -1
        v  1 -1 -1
        v  1  1 -1
        v -1  1 -1
        v -1 -1  1
        v  1 -1  1
        v  1  1  1
        v -1  1  1
        f 5 6 7 8
        f 2 1 4 3
        f 6 2 3 7
        f 1 5 8 4
        f 8 7 3 4
        f 5 1 2 6
        """;
}

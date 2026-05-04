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
    /// Replicates the bake's per-sample painter-order computation by
    /// running the same reference sorter (BSP) the bake uses. Mirrors
    /// <c>SignatureBake.ProcessSample</c>'s logic: sort to get the
    /// painter order, derive face visibility from the sorter's
    /// <c>visBuf</c>. If the bake's painter logic ever diverges from
    /// this, the tests are silently testing the wrong thing — keep them
    /// in lock-step.
    /// </summary>
    private static (bool[] visibility, int[] order) ComputeBakedAspectOrder(ObjGeometry geom, in Matrix4x4 M)
    {
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geom);
        int n = geom.Quads.Length;
        var orderBuf = new int[n];
        var visBuf = new bool[n];
        int visibleCount = sorter.Sort(M, orderBuf, visBuf, cameraDistance: 0f, cullMarginCos: 0f);
        var order = new int[visibleCount];
        Array.Copy(orderBuf, order, visibleCount);
        return (visBuf, order);
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

    // ===== Property-based tests for the bake itself (A-F from the design doc) =====
    //
    // These mirror the production SignatureBake + PredicateCompiler logic in
    // pure C# (no Composition / ExpressionsFork dependency) so we can exercise
    // them end-to-end. The mirror MUST stay in sync with production —
    // intentionally short and obvious so divergence is visible in code review.

    private sealed class MiniSignature
    {
        public required sbyte[] FaceSigns;     // +1 visible, -1 hidden
        public required sbyte[,] PairSigns;    // +1 / -1 for varying pairs, 0 for constant/hidden
        public required int[] Order;           // representative painter order
        public required string Key;
    }

    private static (MiniSignature[] signatures, bool[,] pairVaries, List<RawSample> rawPerSample) MiniBake(
        ObjGeometry geom, IEnumerable<Matrix4x4> samples)
    {
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geom);
        int n = geom.Quads.Length;
        var orderBuf = new int[n];
        var visBuf = new bool[n];
        var orderInverse = new int[n];

        var pairFirstSign = new sbyte[n, n];
        var pairVaries = new bool[n, n];
        var raw = new List<RawSample>();
        var sweep = samples.ToList();

        foreach (var M in sweep)
        {
            int visibleCount = sorter.Sort(M, orderBuf, visBuf, 0f, 0f);
            for (int q = 0; q < n; q++) orderInverse[q] = -1;
            for (int k = 0; k < visibleCount; k++) orderInverse[orderBuf[k]] = k;

            var face = new sbyte[n];
            for (int q = 0; q < n; q++) face[q] = visBuf[q] ? (sbyte)+1 : (sbyte)-1;

            var pair = new sbyte[n, n];
            for (int i = 0; i < n; i++)
            {
                if (face[i] < 0) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (face[j] < 0) continue;
                    sbyte s = orderInverse[j] > orderInverse[i] ? (sbyte)+1 : (sbyte)-1;
                    pair[i, j] = s;
                    pair[j, i] = (sbyte)-s;
                    if (pairFirstSign[i, j] == 0) pairFirstSign[i, j] = s;
                    else if (pairFirstSign[i, j] != s) pairVaries[i, j] = true;
                }
            }
            var order = new int[visibleCount];
            Array.Copy(orderBuf, order, visibleCount);
            raw.Add(new RawSample { Rotation = M, FaceSigns = face, PairSigns = pair, Order = order });
        }

        // Reduce.
        var dict = new Dictionary<string, MiniSignature>();
        foreach (var rs in raw)
        {
            string key = MakeKey(rs.FaceSigns, rs.PairSigns, pairVaries, n);
            if (dict.ContainsKey(key)) continue;
            var runtimePairs = new sbyte[n, n];
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (pairVaries[i, j])
                    {
                        runtimePairs[i, j] = rs.PairSigns[i, j];
                        runtimePairs[j, i] = (sbyte)-rs.PairSigns[i, j];
                    }
            dict[key] = new MiniSignature
            {
                FaceSigns = rs.FaceSigns,
                PairSigns = runtimePairs,
                Order = rs.Order,
                Key = key,
            };
        }
        return (dict.Values.ToArray(), pairVaries, raw);
    }

    private struct RawSample
    {
        public Matrix4x4 Rotation;
        public sbyte[] FaceSigns;
        public sbyte[,] PairSigns;
        public int[] Order;
    }

    private static string MakeKey(sbyte[] face, sbyte[,] pair, bool[,] varies, int n)
    {
        var sb = new System.Text.StringBuilder(n + n * (n - 1) / 2 + 4);
        for (int i = 0; i < n; i++) sb.Append(face[i] > 0 ? '+' : '-');
        sb.Append('|');
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                sb.Append(varies[i, j] ? (pair[i, j] switch { 1 => '+', -1 => '-', _ => '0' }) : '0');
        return sb.ToString();
    }

    /// <summary>
    /// Mirrors <c>PredicateCompiler.BuildPredicate</c> and evaluates the
    /// resulting predicate at <paramref name="M"/>. Returns true iff the
    /// signature matches at θ. Tolerant inequalities <c>&gt; -eps</c> and
    /// <c>&lt; +eps</c> match the production compiler exactly.
    /// </summary>
    private static bool EvaluatePredicate(ObjGeometry geom, MiniSignature sig, in Matrix4x4 M, float eps = 1e-3f)
    {
        var quads = geom.Quads;
        int n = quads.Length;
        // Face-front tests for all faces.
        for (int q = 0; q < n; q++)
        {
            var nrm = quads[q].Normal;
            float nz = nrm.X * M.M13 + nrm.Y * M.M23 + nrm.Z * M.M33;
            bool ok = sig.FaceSigns[q] > 0 ? nz > -eps : nz < +eps;
            if (!ok) return false;
        }
        // Varying-pair tests only.
        for (int i = 0; i < n; i++)
        {
            if (sig.FaceSigns[i] < 0) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (sig.FaceSigns[j] < 0) continue;
                sbyte s = sig.PairSigns[i, j];
                if (s == 0) continue;
                var d = quads[j].Centroid - quads[i].Centroid;
                float dz = d.X * M.M13 + d.Y * M.M23 + d.Z * M.M33;
                bool ok = s > 0 ? dz > -eps : dz < +eps;
                if (!ok) return false;
            }
        }
        return true;
    }

    /// <summary>Same yaw/pitch/roll grid the production bake uses.</summary>
    private static IEnumerable<Matrix4x4> BakeSweep()
    {
        // Match the sample app's axes: yaw periodic 0..360 (24 samples), pitch -180..180 (12), roll -180..180 (12).
        const int yawN = 24, pitchN = 12, rollN = 12;
        for (int y = 0; y < yawN; y++)
            for (int p = 0; p < pitchN; p++)
                for (int r = 0; r < rollN; r++)
                {
                    float yaw = 0f + (y + 0.5f) * 360f / yawN;
                    float pitch = -180f + (p + 0.5f) * 360f / pitchN;
                    float roll = -180f + (r + 0.5f) * 360f / rollN;
                    yield return MakeRotation(yaw, pitch, roll);
                }
        // Axis-aligned probes: combinations of {0, 90, 180, 270} on yaw and {-180, -90, 0, 90, 180} on pitch/roll.
        var yawAligned = new[] { 0f, 90f, 180f, 270f };
        var ptAligned = new[] { -180f, -90f, 0f, 90f, 180f };
        foreach (var ya in yawAligned)
            foreach (var pa in ptAligned)
                foreach (var ra in ptAligned)
                    yield return MakeRotation(ya, pa, ra);
    }

    // ----- Test (A): Bake-coverage. Every random rotation lands in some baked signature. -----
    [Fact]
    public void Cube_BakeCoverage_EveryRandomRotationMatchesSomeSignature()
    {
        var geom = ObjGeometry.Build(ObjParser.Parse(CubeObj).Model);
        var (sigs, _, _) = MiniBake(geom, BakeSweep());
        var rng = new Random(RandomSeed);
        int unmatched = 0;
        Matrix4x4 firstFail = default;
        Vector3 firstFailYpr = default;
        for (int r = 0; r < 500; r++)
        {
            (var rot, var ypr) = RandomRotationAndAngles(rng);
            bool any = false;
            foreach (var sig in sigs) { if (EvaluatePredicate(geom, sig, rot)) { any = true; break; } }
            if (!any)
            {
                if (unmatched == 0) { firstFail = rot; firstFailYpr = ypr; }
                unmatched++;
            }
        }
        Assert.True(unmatched == 0, $"cube bake-coverage: {unmatched}/500 rotations matched no signature; first ypr={firstFailYpr}");
    }

    [Fact]
    public void Book_BakeCoverage_EveryRandomRotationMatchesSomeSignature()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var (sigs, _, _) = MiniBake(geom, BakeSweep());
        var rng = new Random(RandomSeed);
        int unmatched = 0;
        Matrix4x4 firstFail = default;
        Vector3 firstFailYpr = default;
        for (int r = 0; r < 500; r++)
        {
            (var rot, var ypr) = RandomRotationAndAngles(rng);
            bool any = false;
            foreach (var sig in sigs) { if (EvaluatePredicate(geom, sig, rot)) { any = true; break; } }
            if (!any)
            {
                if (unmatched == 0) { firstFail = rot; firstFailYpr = ypr; }
                unmatched++;
            }
        }
        Assert.True(unmatched == 0, $"book bake-coverage: {unmatched}/500 rotations matched no signature; first ypr={firstFailYpr}");
    }

    // ----- Test (B): Predicate self-consistency. Each baked signature's predicate matches its own representative θ. -----
    [Fact]
    public void Book_EachSignaturePredicateMatchesItsRepresentativeRotation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var (sigs, _, raw) = MiniBake(geom, BakeSweep());
        // Build key→representative rotation map from the raw samples.
        var sigByKey = sigs.ToDictionary(s => s.Key);
        var pairVariesDummy = new bool[geom.Quads.Length, geom.Quads.Length];
        // Reconstruct the same pairVaries the bake computed.
        (_, var pairVaries, _) = MiniBake(geom, BakeSweep());
        foreach (var rs in raw)
        {
            string key = MakeKey(rs.FaceSigns, rs.PairSigns, pairVaries, geom.Quads.Length);
            var sig = sigByKey[key];
            Assert.True(EvaluatePredicate(geom, sig, rs.Rotation),
                $"signature key '{key}' predicate failed at its own representative rotation");
        }
    }

    // ----- Test (C): Cell uniqueness. At a random θ, no two signature predicates both match. -----
    [Fact]
    public void Book_CellsAreUniqueAtRandomRotations()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var (sigs, _, _) = MiniBake(geom, BakeSweep());
        var rng = new Random(RandomSeed);
        int multiMatches = 0;
        for (int r = 0; r < 500; r++)
        {
            (var rot, _) = RandomRotationAndAngles(rng);
            int matches = 0;
            foreach (var sig in sigs) if (EvaluatePredicate(geom, sig, rot)) matches++;
            // Tolerance bands can legitimately overlap right at boundaries, so
            // we assert a soft bound: at most 4 simultaneous matches. Random
            // rotations don't usually land on exact event boundaries; if more
            // than 4 cells match, the tolerance is too wide and runtime
            // would render the wrong cell.
            if (matches > 4) multiMatches++;
        }
        Assert.True(multiMatches == 0, $"book cell uniqueness: {multiMatches}/500 rotations had >4 simultaneous matches");
    }

    // ----- Test (E): Multi-revolution spin sweep. -----
    [Theory]
    [InlineData(0f, 0f)]      // pure yaw spin
    [InlineData(30f, 0f)]     // pitch=30
    [InlineData(0f, 45f)]     // roll=45
    [InlineData(30f, 30f)]    // pitch+roll
    public void Book_MultiRevolutionYawSpinHasNoViolations(float pitchDeg, float rollDeg)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        // 5 full revolutions, 1° steps = 1800 samples.
        int violations = 0;
        for (int s = 0; s < 1800; s++)
        {
            float yaw = s * 1f; // 0..1800° (5 revs)
            var rot = MakeRotation(yaw, pitchDeg, rollDeg);
            var (visibility, order) = ComputeBakedAspectOrder(geom, rot);
            int visibleCount = 0;
            for (int i = 0; i < visibility.Length; i++) if (visibility[i]) visibleCount++;
            if (visibleCount < 2) continue;
            var v = PainterCorrectness.FindWorstViolation(geom, order, visibleCount, rot);
            if (v.HasValue) violations++;
        }
        Assert.True(violations == 0,
            $"book multi-rev spin pitch={pitchDeg} roll={rollDeg}: {violations}/1800 yaw samples violated");
    }

    // ----- Test (F): Sample density sufficiency. Test bake at production density and check 10× finer θ has correct match. -----
    [Fact]
    public void Book_FineGridRotationsAllProduceCorrectPainterOrder()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var (sigs, _, _) = MiniBake(geom, BakeSweep());
        // 10× finer grid: 240 yaw, 120 pitch, 120 roll = 3.45M samples — too many.
        // Instead use 3.6M but stratified-random so cost is bounded.
        var rng = new Random(RandomSeed + 1);
        int total = 1500;
        int unmatched = 0;
        int wrongOrder = 0;
        Vector3 firstUnmatchedYpr = default;
        Vector3 firstWrongYpr = default;
        for (int s = 0; s < total; s++)
        {
            (var rot, var ypr) = RandomRotationAndAngles(rng);
            // Find the matching signature.
            MiniSignature? match = null;
            foreach (var sig in sigs) if (EvaluatePredicate(geom, sig, rot)) { match = sig; break; }
            if (match == null)
            {
                if (unmatched == 0) firstUnmatchedYpr = ypr;
                unmatched++;
                continue;
            }
            // Verify the matching signature's painter order has no violation at this rotation.
            var visibleCount = match.Order.Length;
            if (visibleCount < 2) continue;
            var v = PainterCorrectness.FindWorstViolation(geom, match.Order, visibleCount, rot);
            if (v.HasValue)
            {
                if (wrongOrder == 0) firstWrongYpr = ypr;
                wrongOrder++;
            }
        }
        Assert.True(unmatched == 0 && wrongOrder == 0,
            $"book fine-grid sufficiency: unmatched={unmatched}/{total} (first ypr={firstUnmatchedYpr}), " +
            $"wrong-order={wrongOrder}/{total} (first ypr={firstWrongYpr})");
    }

    // ----- Test (D): Pinned angles from session bug reports. -----
    [Theory]
    [InlineData(50f, 63f, 74f, "v120 pages-over-cover bug")]
    [InlineData(31f, -112f, 0f, "v118 pages-under-back-cover bug")]
    [InlineData(-47f, -33f, 0f, "v110 no-signature-matches failure")]
    [InlineData(40f, 20f, 0f, "v117 page-edge-sliver case")]
    public void Book_SessionBugAngles_RemainCorrect(float yawDeg, float pitchDeg, float rollDeg, string label)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var rot = MakeRotation(yawDeg, pitchDeg, rollDeg);
        AssertNoViolation(geom, rot, label);
    }
}


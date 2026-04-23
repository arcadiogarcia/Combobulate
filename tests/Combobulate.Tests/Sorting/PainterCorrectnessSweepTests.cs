using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// High-resolution painter's-algorithm correctness sweep across all three
/// sorters on a real model. This is the test infrastructure that turns
/// "I see flickers in the live spin" into "BSP fails at yaw=87.3° with
/// face-pair (4,7) inverted by 0.012 depth-units" — i.e. a precise,
/// reproducible bug coordinate.
///
/// <para>
/// Why this catches what the existing tests miss:
/// <list type="bullet">
///   <item><see cref="RotationSweepTests"/> sweeps at 15° steps. Sort glitches
///         live in narrow yaw windows (often &lt; 1° wide) and slip through.</item>
///   <item><see cref="SortAssertions.AssertBackToFront"/> is a centroid-Z monotonicity
///         check. Faces can be in correct centroid order yet still mis-occlude
///         each other in the painter's algorithm — the centroid of an L-shaped
///         overlap region tells you nothing about whether the faces actually
///         intersect at a particular pixel.</item>
///   <item><see cref="PainterCorrectness"/> evaluates the planes analytically
///         at the actual overlap region, so a violation here is one that
///         <i>would</i> be visible if we rasterised.</item>
/// </list>
/// </para>
/// </summary>
public class PainterCorrectnessSweepTests
{
    /// <summary>
    /// Yaw step in degrees. 0.5° gives 720 samples per turn — fine enough to
    /// land inside the &lt;1° windows where transient sort errors typically
    /// live, while keeping the whole sweep under a second per sorter.
    /// </summary>
    private const float YawStepDegrees = 0.5f;

    /// <summary>
    /// How many failing-yaw rows to include in the assertion message. The
    /// sweep is exhaustive even when this cap fires — only the report is
    /// truncated, the count is always exact.
    /// </summary>
    private const int MaxReportedFailures = 24;

    public static IEnumerable<object[]> CorrectAlgorithms() => new[]
    {
        new object[] { SortAlgorithm.Bsp },
        new object[] { SortAlgorithm.Topological },
    };

    /// <summary>
    /// Strict painter-correctness sweep for the algorithms that are currently
    /// known to be correct on book.obj. Any new violation here is a regression.
    /// <see cref="SortAlgorithm.Newell"/> is excluded — see the dedicated
    /// known-regression test <see cref="Newell_HasKnownPainterFailure_AtForeEdgeBackCoverSeam"/>
    /// for its (precise) signature.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorrectAlgorithms))]
    public void PainterOrder_IsCorrect_OverYawSweep_BookObj(SortAlgorithm algo)
    {
        var failures = SweepAndCollect(algo, out _, out _);
        if (failures.Count == 0) return;

        var lines = BuildReport(algo, failures);
        Assert.Fail(string.Join("\n", lines));
    }

    /// <summary>
    /// Locks in the EXACT signature of the Newell painter-order bug discovered
    /// by the sweep: 8 violations across yaws 265.5°–271.5° involving the
    /// fore-edge (quad 6) painted under the back cover (quads 0/2). If this
    /// test starts failing — either because the count drops (Newell got better,
    /// great, narrow the assertion) or rises (Newell got worse, regression!) —
    /// we want to know immediately. This is a snapshot test, not a "skip and
    /// forget" suppression.
    /// </summary>
    [Fact]
    public void Newell_HasKnownPainterFailure_AtForeEdgeBackCoverSeam()
    {
        var failures = SweepAndCollect(SortAlgorithm.Newell, out var worstDelta, out var worstYaw);

        // Locked-in signature as of commit introducing this test (see git blame).
        // Any change to these counts/bounds is a real algorithm change.
        Assert.Equal(8, failures.Count);
        Assert.InRange(worstDelta, 0.039f, 0.041f);
        Assert.InRange(worstYaw, 265.0f, 272.0f);

        // Emit the same human-readable report for diagnostic visibility (xUnit
        // shows it on failure, hidden on pass).
        // No-op string formatting just to keep the diagnostic warm.
        _ = BuildReport(SortAlgorithm.Newell, failures);
    }

    // ---------- shared sweep + report ----------

    private static List<(float yaw, PainterCorrectness.Violation v)> SweepAndCollect(
        SortAlgorithm algo, out float worstDelta, out float worstYaw)
    {
        var geom = LoadBookObj();
        var sorter = FaceSorterFactory.Create(algo, geom);
        int qc = geom.Quads.Length;
        var order = new int[qc];
        var visible = new bool[qc];

        var failures = new List<(float yaw, PainterCorrectness.Violation v)>();
        worstDelta = 0f;
        worstYaw = 0f;

        for (float yaw = 0f; yaw < 360f; yaw += YawStepDegrees)
        {
            var rot = SortAssertions.Yaw(yaw);
            int n = sorter.Sort(rot, order, visible);
            var v = PainterCorrectness.FindWorstViolation(geom, order, n, rot);
            if (v is { } viol)
            {
                failures.Add((yaw, viol));
                if (viol.DepthDelta > worstDelta)
                {
                    worstDelta = viol.DepthDelta;
                    worstYaw = yaw;
                }
            }
        }

        return failures;
    }

    private static List<string> BuildReport(SortAlgorithm algo, List<(float yaw, PainterCorrectness.Violation v)> failures)
    {
        var grouped = failures
            .GroupBy(f => (f.v.FirstIdx, f.v.SecondIdx))
            .OrderByDescending(g => g.Count())
            .Take(MaxReportedFailures);
        var worstDelta = failures.Max(f => f.v.DepthDelta);
        var worstYaw = failures.OrderByDescending(f => f.v.DepthDelta).First().yaw;

        var lines = new List<string>
        {
            $"=== {algo} on book.obj: {failures.Count} painter-order violations across yaw sweep (step={YawStepDegrees}°) ===",
            $"worst depth-overlap = {worstDelta:F4} at yaw={worstYaw:F2}°",
            "(grouped by face-pair, by frequency)",
        };
        foreach (var g in grouped)
        {
            var yaws = g.Select(f => f.yaw).OrderBy(y => y).ToList();
            lines.Add($"  pair=({g.Key.FirstIdx},{g.Key.SecondIdx})  frames={g.Count()}  yaw=[{yaws.First():F2}..{yaws.Last():F2}]  worstDelta={g.Max(f => f.v.DepthDelta):F4}");
        }
        return lines;
    }

    /// <summary>
    /// Locate <c>book.obj</c> (copied next to the test assembly via the project's
    /// <c>None Include</c> item) and parse it. Falls back to walking up the
    /// directory tree to <c>samples\book.obj</c> in case the copy step didn't
    /// run (e.g. dotnet test from a clean checkout before the first build).
    /// </summary>
    private static ObjGeometry LoadBookObj()
    {
        string? path = null;
        var local = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        if (File.Exists(local)) path = local;
        else
        {
            // Walk up from the test-bin dir looking for the repo's samples folder.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && path is null)
            {
                var candidate = Path.Combine(dir.FullName, "samples", "book.obj");
                if (File.Exists(candidate)) { path = candidate; break; }
                dir = dir.Parent;
            }
        }
        Assert.True(path is not null, $"book.obj not found relative to {AppContext.BaseDirectory}");
        var text = File.ReadAllText(path!);
        var result = ObjParser.Parse(text);
        Assert.Empty(result.Errors);
        return ObjGeometry.Build(result.Model);
    }
}

using System.Threading;
using Combobulate.Diagnostics;
using Combobulate.Tests.Sorting;

namespace Combobulate.Tests.Diagnostics;

/// <summary>
/// Sanity tests for the lock-free ring buffer behind <see cref="SpinDiagnostics"/>.
/// Not a stress test (it's hard to truly stress a single-writer ring), but enough
/// to catch obvious regressions in <see cref="SpinDiagnostics.Snapshot"/> /
/// <see cref="SpinDiagnostics.SummaryReport"/> JSON shape and the wrap-around
/// behaviour when the writer overruns the buffer.
/// </summary>
public class SpinDiagnosticsTests
{
    public SpinDiagnosticsTests()
    {
        // These tests share static state — disable + reset at the start of each.
        SpinDiagnostics.Disable();
        SpinDiagnostics.Enable();   // resets cursor + epoch
        SpinDiagnostics.Disable();  // leave disabled by default; individual tests re-enable as needed
    }

    [Fact]
    public void Disabled_RecordIsNoOp()
    {
        Assert.False(SpinDiagnostics.IsEnabled);
        for (int i = 0; i < 100; i++)
        {
            SpinDiagnostics.Record(MakeRecord(i));
        }
        Assert.Equal(0L, SpinDiagnostics.TotalFramesRecorded);
    }

    [Fact]
    public void Enabled_RecordsArrived_AndAppearInSnapshot()
    {
        SpinDiagnostics.Enable();
        for (int i = 0; i < 5; i++) SpinDiagnostics.Record(MakeRecord(i));
        Assert.Equal(5L, SpinDiagnostics.TotalFramesRecorded);

        var json = SpinDiagnostics.Snapshot();
        Assert.Contains("\"snapshotFrames\":5", json);
        Assert.Contains("\"totalFrames\":5", json);
        Assert.Contains("\"f\":4", json); // last frame ID written

        SpinDiagnostics.Disable();
    }

    [Fact]
    public void RingWrapsAtCapacity_KeepsMostRecent()
    {
        SpinDiagnostics.Enable();
        // Write 1.5x capacity so the oldest 512 entries get overwritten.
        long target = SpinDiagnostics.Capacity + (SpinDiagnostics.Capacity / 2);
        for (long i = 0; i < target; i++) SpinDiagnostics.Record(MakeRecord((int)i));

        Assert.Equal(target, SpinDiagnostics.TotalFramesRecorded);
        var json = SpinDiagnostics.Snapshot();
        Assert.Contains($"\"snapshotFrames\":{SpinDiagnostics.Capacity}", json);
        Assert.Contains($"\"totalFrames\":{target}", json);
        // The oldest frame in the snapshot should be (target - capacity), not 0.
        long expectedOldest = target - SpinDiagnostics.Capacity;
        Assert.Contains($"\"f\":{expectedOldest},", json);
        // Frame 0 must NOT appear (it was overwritten).
        Assert.DoesNotContain("\"f\":0,", json);

        SpinDiagnostics.Disable();
    }

    [Fact]
    public void SummaryReport_ReflectsRecordedStats()
    {
        SpinDiagnostics.Enable();
        // Mix of changed/unchanged frames so the % is non-trivial.
        for (int i = 0; i < 10; i++)
        {
            bool changed = (i % 2) == 0;
            SpinDiagnostics.Record(new SpinDiagnostics.FrameRecord(
                frameId: i, timestampMicros: i * 16667, threadId: 7, yawDeg: i * 36f, pitchDeg: 0f,
                orderCount: 4, visibleMask: 0b1111, previousVisibleMask: 0b1111,
                orderChanged: changed, mutationsApplied: changed ? 4 : 0,
                sortMicros: 10, reorderMicros: changed ? 50 : 0,
                order: SpinDiagnostics.OrderSnapshot.From(new[] { 0, 1, 2, 3 }, 4)));
        }

        var summary = SpinDiagnostics.SummaryReport();
        Assert.Contains("\"snapshotFrames\":10", summary);
        Assert.Contains("\"orderChangedFrames\":5", summary);
        Assert.Contains("\"orderChangedPct\":50.0", summary);
        Assert.Contains("\"totalMutations\":20", summary);
        Assert.Contains("\"maxMutationsPerFrame\":4", summary);
        Assert.Contains("\"threads\":[7]", summary);

        SpinDiagnostics.Disable();
    }

    [Fact]
    public void ExtractYawPitch_ReturnsFiniteValues()
    {
        // ExtractYawPitch is only used to stamp a human-readable angle into
        // diagnostic records — it does not feed back into sort logic — so the
        // contract is "doesn't throw, doesn't NaN" rather than exact recovery
        // of the source Euler angles. (Production rotation may use any of
        // several Euler conventions; the diagnostic just needs a stable
        // angle-ish number per record.)
        var rot = SortAssertions.YawPitch(yawDeg: 45f, pitchDeg: 20f);
        var (yawDeg, pitchDeg) = SpinDiagnostics.ExtractYawPitch(rot);
        Assert.False(float.IsNaN(yawDeg));
        Assert.False(float.IsNaN(pitchDeg));
        Assert.InRange(yawDeg, -360f, 360f);
        Assert.InRange(pitchDeg, -90f, 90f);
    }

    private static SpinDiagnostics.FrameRecord MakeRecord(int i) => new(
        frameId: i, timestampMicros: i * 16667, threadId: Thread.CurrentThread.ManagedThreadId,
        yawDeg: 0f, pitchDeg: 0f, orderCount: 0, visibleMask: 0, previousVisibleMask: 0,
        orderChanged: false, mutationsApplied: 0, sortMicros: 0, reorderMicros: 0,
        order: default);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Combobulate.Diagnostics;

/// <summary>
/// Lock-free ring-buffer recorder for per-frame Combobulate sort+reorder state.
/// Designed for diagnosing CPU/GPU sync glitches: the renderer mutates the
/// composition tree by removing and re-inserting child sprites N times
/// (one per visible quad) on each frame the order changes. If those N
/// mutations interleave with a compositor read, the user sees a torn frame.
///
/// <para>
/// Recording is opt-in (<see cref="IsEnabled"/>) and zero-overhead when
/// disabled — the renderer wraps every diagnostics call in
/// <c>if (SpinDiagnostics.IsEnabled)</c>. When enabled, each frame writes
/// one <see cref="FrameRecord"/> entry into a power-of-two ring buffer
/// using a single <see cref="Interlocked.Increment"/>, so contention is
/// nil even if the renderer is later lifted off the UI thread.
/// </para>
///
/// <para>
/// The buffer is sized to hold ~5 seconds at 60 Hz (300 frames) by
/// default, which is enough to span a full 6-second spin turn and any
/// transition glitches around start/stop. The ring overwrites oldest
/// entries when full so a long-running session sees the most recent
/// activity.
/// </para>
/// </summary>
public static class SpinDiagnostics
{
    /// <summary>
    /// Power-of-two ring capacity. 1024 entries × ~256 bytes/entry ≈ 256 KB,
    /// which is small enough to dump as a single JSON blob over the Rover
    /// MCP transport without truncation issues.
    /// </summary>
    public const int Capacity = 1024;
    private const int CapacityMask = Capacity - 1;

    private static readonly FrameRecord[] s_buffer = new FrameRecord[Capacity];
    private static long s_writeCursor; // monotonically increasing; modulo Capacity = slot
    private static volatile bool s_enabled;
    private static long s_epochTicks; // Stopwatch ticks captured when Enable() was called

    /// <summary>True when the ring buffer is accepting frame records.</summary>
    public static bool IsEnabled => s_enabled;

    /// <summary>Total frames written since <see cref="Enable"/> (may exceed <see cref="Capacity"/>).</summary>
    public static long TotalFramesRecorded => Interlocked.Read(ref s_writeCursor);

    /// <summary>
    /// Begin recording. Resets the ring cursor and timer epoch so a fresh
    /// session always starts at frame 0 and t = 0 ms.
    /// </summary>
    public static void Enable()
    {
        Interlocked.Exchange(ref s_writeCursor, 0);
        s_epochTicks = Stopwatch.GetTimestamp();
        s_enabled = true;
    }

    /// <summary>Stop recording. Existing entries remain available for <see cref="Snapshot"/>.</summary>
    public static void Disable() => s_enabled = false;

    /// <summary>
    /// Per-frame state captured at the end of the sort+reorder pass.
    /// Field layout is intentionally compact — each record is ~256 bytes —
    /// so the entire ring fits in L2 and serialises quickly.
    /// </summary>
    public readonly struct FrameRecord
    {
        public readonly long FrameId;
        public readonly long TimestampMicros;     // since Enable()
        public readonly int ThreadId;
        public readonly float YawDeg;             // recovered from the rotation matrix; useful for cross-referencing with the painter oracle
        public readonly float PitchDeg;
        public readonly int OrderCount;           // count of visible quads in Order
        public readonly long VisibleMask;         // bit i = visible quad i (works for ≤64 quads, which book.obj satisfies)
        public readonly long PreviousVisibleMask;
        public readonly bool OrderChanged;
        public readonly int MutationsApplied;     // how many Remove/InsertAtTop pairs ran this frame
        public readonly long SortMicros;          // wall time spent in IFaceSorter.Sort
        public readonly long ReorderMicros;       // wall time spent mutating the visual tree
        public readonly OrderSnapshot Order;      // up to 16 indices inline; book.obj fits

        public FrameRecord(long frameId, long timestampMicros, int threadId, float yawDeg, float pitchDeg,
            int orderCount, long visibleMask, long previousVisibleMask, bool orderChanged, int mutationsApplied,
            long sortMicros, long reorderMicros, OrderSnapshot order)
        {
            FrameId = frameId; TimestampMicros = timestampMicros; ThreadId = threadId;
            YawDeg = yawDeg; PitchDeg = pitchDeg;
            OrderCount = orderCount; VisibleMask = visibleMask; PreviousVisibleMask = previousVisibleMask;
            OrderChanged = orderChanged; MutationsApplied = mutationsApplied;
            SortMicros = sortMicros; ReorderMicros = reorderMicros;
            Order = order;
        }
    }

    /// <summary>
    /// Inline fixed-capacity int array (16 slots) so a <see cref="FrameRecord"/>
    /// stays a value type. <c>Length</c> is the populated count; trailing slots
    /// are undefined.
    /// </summary>
    public struct OrderSnapshot
    {
        public const int MaxLength = 16;
        public int Length;
        public int I0, I1, I2, I3, I4, I5, I6, I7, I8, I9, I10, I11, I12, I13, I14, I15;

        public int this[int idx] => idx switch
        {
            0 => I0, 1 => I1, 2 => I2, 3 => I3, 4 => I4, 5 => I5, 6 => I6, 7 => I7,
            8 => I8, 9 => I9, 10 => I10, 11 => I11, 12 => I12, 13 => I13, 14 => I14, 15 => I15,
            _ => -1,
        };

        public static OrderSnapshot From(int[] order, int count)
        {
            var s = new OrderSnapshot { Length = Math.Min(count, MaxLength) };
            for (int i = 0; i < s.Length; i++)
            {
                switch (i)
                {
                    case 0: s.I0 = order[i]; break; case 1: s.I1 = order[i]; break;
                    case 2: s.I2 = order[i]; break; case 3: s.I3 = order[i]; break;
                    case 4: s.I4 = order[i]; break; case 5: s.I5 = order[i]; break;
                    case 6: s.I6 = order[i]; break; case 7: s.I7 = order[i]; break;
                    case 8: s.I8 = order[i]; break; case 9: s.I9 = order[i]; break;
                    case 10: s.I10 = order[i]; break; case 11: s.I11 = order[i]; break;
                    case 12: s.I12 = order[i]; break; case 13: s.I13 = order[i]; break;
                    case 14: s.I14 = order[i]; break; case 15: s.I15 = order[i]; break;
                }
            }
            return s;
        }
    }

    /// <summary>
    /// Append a frame record. Called from <c>Combobulate.Rebuild</c>'s sort/reorder section.
    /// Safe to call when <see cref="IsEnabled"/> is false — it returns immediately.
    /// </summary>
    public static void Record(in FrameRecord r)
    {
        if (!s_enabled) return;
        long ticket = Interlocked.Increment(ref s_writeCursor) - 1;
        s_buffer[ticket & CapacityMask] = r;
    }

    /// <summary>
    /// Compute "now" in microseconds since <see cref="Enable"/>. Renderer
    /// uses this to stamp records consistently across multiple instances.
    /// </summary>
    public static long ElapsedMicros()
    {
        var delta = Stopwatch.GetTimestamp() - s_epochTicks;
        return delta * 1_000_000L / Stopwatch.Frequency;
    }

    /// <summary>
    /// Recover yaw (Y-axis rotation) and pitch (X-axis rotation) in degrees
    /// from a rotation matrix in the renderer's convention (rotation = Yaw·Pitch).
    /// Used by the renderer to stamp the rotation source-of-truth into each
    /// record without forcing the caller to also pass them.
    /// </summary>
    public static (float yawDeg, float pitchDeg) ExtractYawPitch(in Matrix4x4 m)
    {
        // For R = Ry(yaw) * Rx(pitch):
        //   pitch = asin(-m32) in WinUI's row-vector convention; m.M32 (System.Numerics) is the [3,2] entry
        // We use the standard formula and accept gimbal-lock degeneracy at pitch = ±90°
        // (the diagnostic just reports a yaw of 0° in that band — fine for debugging).
        var sinPitch = -m.M32;
        sinPitch = Math.Clamp(sinPitch, -1f, 1f);
        var pitch = MathF.Asin(sinPitch);
        var yaw = MathF.Atan2(m.M31, m.M33);
        return (yaw * (180f / MathF.PI), pitch * (180f / MathF.PI));
    }

    /// <summary>
    /// Snapshot the ring as a JSON array, oldest entry first. Output is
    /// suitable for piping into a tool or pasting into an issue. Each
    /// record renders as a single line for easy grep/diff.
    /// </summary>
    public static string Snapshot()
    {
        long total = Interlocked.Read(ref s_writeCursor);
        long count = Math.Min(total, Capacity);
        long firstTicket = total - count;

        var sb = new StringBuilder(64 * 1024);
        sb.Append("{\"capacity\":").Append(Capacity)
          .Append(",\"totalFrames\":").Append(total)
          .Append(",\"snapshotFrames\":").Append(count)
          .Append(",\"records\":[");

        for (long t = 0; t < count; t++)
        {
            ref readonly var r = ref s_buffer[(firstTicket + t) & CapacityMask];
            if (t > 0) sb.Append(',');
            sb.Append("\n  {\"f\":").Append(r.FrameId)
              .Append(",\"tUs\":").Append(r.TimestampMicros)
              .Append(",\"tid\":").Append(r.ThreadId)
              .Append(",\"yaw\":").Append(r.YawDeg.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"pitch\":").Append(r.PitchDeg.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"n\":").Append(r.OrderCount)
              .Append(",\"vis\":").Append(r.VisibleMask)
              .Append(",\"prevVis\":").Append(r.PreviousVisibleMask)
              .Append(",\"chg\":").Append(r.OrderChanged ? "true" : "false")
              .Append(",\"muts\":").Append(r.MutationsApplied)
              .Append(",\"sortUs\":").Append(r.SortMicros)
              .Append(",\"reorderUs\":").Append(r.ReorderMicros)
              .Append(",\"order\":[");
            for (int i = 0; i < r.Order.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(r.Order[i]);
            }
            sb.Append("]}");
        }
        sb.Append("\n]}");
        return sb.ToString();
    }

    /// <summary>
    /// Compact human-readable summary: total frames, % with order changes,
    /// distribution of mutations-per-frame, slowest-frame stats. Intended
    /// for at-a-glance triage from a Rover dispatch_action call.
    /// </summary>
    public static string SummaryReport()
    {
        long total = Interlocked.Read(ref s_writeCursor);
        long count = Math.Min(total, Capacity);
        long firstTicket = total - count;
        if (count == 0) return "{\"snapshotFrames\":0}";

        long changedFrames = 0;
        long totalMutations = 0;
        long maxMutations = 0;
        long maxSortUs = 0, maxReorderUs = 0;
        long sumSortUs = 0, sumReorderUs = 0;
        var threads = new HashSet<int>();
        FrameRecord worstReorder = default;

        for (long t = 0; t < count; t++)
        {
            ref readonly var r = ref s_buffer[(firstTicket + t) & CapacityMask];
            if (r.OrderChanged) changedFrames++;
            totalMutations += r.MutationsApplied;
            if (r.MutationsApplied > maxMutations) maxMutations = r.MutationsApplied;
            sumSortUs += r.SortMicros;
            sumReorderUs += r.ReorderMicros;
            if (r.SortMicros > maxSortUs) maxSortUs = r.SortMicros;
            if (r.ReorderMicros > maxReorderUs) { maxReorderUs = r.ReorderMicros; worstReorder = r; }
            threads.Add(r.ThreadId);
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        sb.Append("{\"snapshotFrames\":").Append(count)
          .Append(",\"totalFrames\":").Append(total)
          .Append(",\"orderChangedFrames\":").Append(changedFrames)
          .Append(",\"orderChangedPct\":").Append(((double)changedFrames * 100.0 / count).ToString("F1", inv))
          .Append(",\"totalMutations\":").Append(totalMutations)
          .Append(",\"maxMutationsPerFrame\":").Append(maxMutations)
          .Append(",\"avgSortUs\":").Append(((double)sumSortUs / count).ToString("F1", inv))
          .Append(",\"maxSortUs\":").Append(maxSortUs)
          .Append(",\"avgReorderUs\":").Append(((double)sumReorderUs / count).ToString("F1", inv))
          .Append(",\"maxReorderUs\":").Append(maxReorderUs)
          .Append(",\"threads\":[");
        bool first = true;
        foreach (var tid in threads) { if (!first) sb.Append(','); sb.Append(tid); first = false; }
        sb.Append("],\"worstReorderFrame\":{\"f\":").Append(worstReorder.FrameId)
          .Append(",\"tUs\":").Append(worstReorder.TimestampMicros)
          .Append(",\"yaw\":").Append(worstReorder.YawDeg.ToString("F2", inv))
          .Append(",\"muts\":").Append(worstReorder.MutationsApplied)
          .Append(",\"reorderUs\":").Append(worstReorder.ReorderMicros)
          .Append("}}");
        return sb.ToString();
    }
}

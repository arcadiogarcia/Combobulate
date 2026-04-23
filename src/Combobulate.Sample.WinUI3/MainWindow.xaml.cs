using System;
using System.IO;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Combobulate.Sample.WinUI3;

public sealed partial class MainWindow : Window
{
    /// <summary>Stable cache key for the built-in cube. Used by both code and XAML (Source="cube").</summary>
    private const string CubeKey = "cube";

    public MainWindow()
    {
        this.InitializeComponent();

        // Path 1 — keyed cache for an app-supplied model. Parse + register once at startup;
        // every later request (including XAML `Source="cube"` thumbnails) reuses the cached
        // ObjGeometry. Repeated "Reset cube" clicks below do zero parsing.
        ObjCache.GetOrAdd(CubeKey, () => ObjParser.Parse(CubeObj).Model);

        LoadCube();
    }

    private void LoadCube()
    {
        // Resolve from the keyed cache. The first call built the geometry; this just
        // hands the same ObjModel back to the control.
        combobulate.Model = ObjCache.TryGet(CubeKey)!.Model;
        StatusText.Text = $"Loaded: built-in cube (cache key '{CubeKey}')";
    }

    private void ResetCube_Click(object sender, RoutedEventArgs e) => LoadCube();

    private void ResetRotation_Click(object sender, RoutedEventArgs e)
    {
        PitchSlider.Value = 0;
        YawSlider.Value = 0;
        RollSlider.Value = 0;
        // ValueChanged fires which routes through ApplyRotation.
    }

    private void Rotation_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => ApplyRotation();

    private void ExternalRotationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ApplyRotation();
        UpdateAutoRefresh();
    }

    private void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e) => UpdateAutoRefresh();

    /// <summary>
    /// Autonomous continuous Y-axis spin. Implicitly enables External + Auto-refresh
    /// (spin is meaningless without them: external routes rotation through composition,
    /// auto-refresh keeps cull/sort in sync).
    ///
    /// <para>Architecture: the visual rotation is GPU-driven by a Composition
    /// <c>ScalarKeyFrameAnimation</c> looping forever on a <c>SpinYaw</c> scalar in
    /// the shared property set. <c>p.SpinActive * p.SpinYaw</c> is added to the slider
    /// yaw inside the expression animation that produces the final <c>Rotation</c>
    /// vector — so both renderers' visuals keep spinning smoothly even if the UI
    /// thread hitches; no per-frame <c>InsertVector3</c> writes are needed.</para>
    ///
    /// <para>The CPU sampler installed by <c>EnableAutoRefresh</c> still runs on
    /// <c>CompositionTarget.Rendering</c> in parallel — its only job is to recompute
    /// the same yaw from wall-clock and feed it to <c>RebuildForExternalRotation</c>
    /// for back-face cull / painter-sort. CPU and GPU clocks share the same epoch +
    /// formula (<c>secs / SpinSecondsPerTurn * 360</c>), so they agree to sub-ms in
    /// steady state and the CPU naturally re-syncs on the next tick after any UI
    /// stall.</para>
    /// </summary>
    private void SpinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (SpinToggle == null) return;
        if (SpinToggle.IsOn)
        {
            if (ExternalRotationToggle != null && !ExternalRotationToggle.IsOn)
                ExternalRotationToggle.IsOn = true;
            if (AutoRefreshToggle != null && !AutoRefreshToggle.IsOn)
                AutoRefreshToggle.IsOn = true;
            StartGpuSpin();
        }
        else
        {
            StopGpuSpin();
        }
        UpdateAutoRefresh();
    }

    private DateTime? _spinStart;
    private const float SpinSecondsPerTurn = 6f;

    // ===== Spin instrumentation =====
    // Ring buffer of per-sampler-tick records, used by DumpSpinDiagnostics to
    // diagnose CPU/GPU drift, frame-time jitter, and GC pauses during long spins.
    private readonly System.Diagnostics.Stopwatch _spinClock = new();
    private struct SpinTick
    {
        public long ElapsedTicks;       // _spinClock.ElapsedTicks at sample
        public float CpuYawWrapped;     // what the cull/sort received
        public float CpuYawRaw;         // pre-wrap (to see precision drift)
        public float DeltaMsSincePrev;  // wall-clock gap between this tick and the previous
        public int Gc0, Gc1, Gc2;       // snapshot of GC.CollectionCount
        public byte VisibleMask;        // bit i = sprite i was last set IsVisible=true
        public byte VisibleCount;       // popcount of VisibleMask
        public float GpuSpinYawCached;  // TryGetScalar("SpinYaw") at this tick (often stale=0 for animated, but log it)
        public float CompositorMs;      // RenderingEventArgs.RenderingTime in ms (0 if sampler had no time)
        public float WallMs;            // _spinClock elapsed ms at the same instant
        public float DriftMs;           // WallMs - CompositorMs (positive = wall ahead of compositor)
    }
    private SpinTick[] _spinRing = new SpinTick[1024]; // ~17s at 60Hz
    private int _spinRingHead;       // next slot to write
    private long _spinTickCount;     // total sampler invocations since spin start
    private long _prevTickElapsedTicks;
    private int _gc0AtStart, _gc1AtStart, _gc2AtStart;
    // Compositor RenderingTime (ms) captured on the first sampler tick
    // after StartGpuSpin. Used as the epoch to convert subsequent
    // RenderingTime values into "seconds since spin start".
    private float _spinStartCompositorMs;

    /// <summary>
    /// Begin the spin. <c>SpinYaw</c> is no longer driven by a Composition
    /// KFA — instead the per-frame sampler computes the yaw on the UI thread
    /// from the compositor's <c>RenderingTime</c> and writes it directly into
    /// the <c>SpinYaw</c> scalar via <c>InsertScalar</c>. This makes the value
    /// the GPU expression reads identical-by-construction to the value the
    /// CPU cull/sort uses (both come from the exact same write each frame),
    /// eliminating every possible source of clock divergence:
    ///
    ///   - wall-clock vs compositor-clock drift (was: ~22ms over 60s)
    ///   - KFA epoch uncertainty (we can't read the compositor's commit time
    ///     for an animated property, so a KFA-vs-CPU mirror would always be
    ///     phase-shifted by an indeterminate fraction of a frame, producing
    ///     a constant cull-vs-draw offset visible from frame 1)
    ///
    /// Trade-off: if the UI thread stalls, the spin pauses for that frame
    /// instead of continuing GPU-side. That is acceptable here because the
    /// sampler is what would have produced the cull rebuild on that same
    /// stalled frame anyway — there is nothing for a "smooth GPU spin" to
    /// be in sync with during a stall.
    /// </summary>
    private void StartGpuSpin()
    {
        var (props, _) = GetOrCreateExternalRotation();
        // Make sure no leftover KFA is still bound to SpinYaw — if there is
        // one, our InsertScalar writes will be ignored while it is active.
        props.StopAnimation("SpinYaw");
        props.InsertScalar("SpinActive", 1f);
        props.InsertScalar("SpinYaw", 0f);
        // _spinStart serves as a "spin is on" sentinel; the actual epoch
        // used for yaw computation is _spinStartCompositorMs which is
        // captured on the first sampler tick.
        _spinStart = DateTime.UtcNow;
        _spinClock.Restart();
        _spinRingHead = 0;
        _spinTickCount = 0;
        _prevTickElapsedTicks = 0;
        Array.Clear(_spinRing, 0, _spinRing.Length);
        _spinStartCompositorMs = 0f;
        _gc0AtStart = GC.CollectionCount(0);
        _gc1AtStart = GC.CollectionCount(1);
        _gc2AtStart = GC.CollectionCount(2);
    }

    private void StopGpuSpin()
    {
        _spinStart = null;
        if (_externalRotationProps == null) return;
        // No KFA to stop, but be defensive in case a previous build (which
        // did use a KFA) left one running across an in-place package update.
        _externalRotationProps.StopAnimation("SpinYaw");
        _externalRotationProps.InsertScalar("SpinYaw", 0f);
        _externalRotationProps.InsertScalar("SpinActive", 0f);
    }

    /// <summary>
    /// Snapshot of the per-tick spin instrumentation. Returns one summary
    /// section followed by optionally <paramref name="lastN"/> raw rows.
    /// Called from the rover <c>DumpSpinDiagnostics</c> action; no harm if
    /// invoked outside spin (it just reports zeroed counters).
    /// </summary>
    internal System.Collections.Generic.List<string> DumpSpinDiagnosticsLines(int lastN)
    {
        var lines = new System.Collections.Generic.List<string>();
        bool spinning = _spinStart is DateTime;
        var elapsedMs = spinning ? (DateTime.UtcNow - _spinStart!.Value).TotalMilliseconds : 0;
        long total = _spinTickCount;

        // Compute frame-time stats by walking the ring buffer.
        float minDt = float.PositiveInfinity, maxDt = 0, sumDt = 0;
        int dtCount = 0;
        var dtSamples = new System.Collections.Generic.List<float>(_spinRing.Length);
        for (int i = 0; i < _spinRing.Length; i++)
        {
            var t = _spinRing[i];
            if (t.ElapsedTicks == 0 && t.CpuYawWrapped == 0 && t.CpuYawRaw == 0) continue;
            // Skip the very first tick after StartSpin (DeltaMsSincePrev == 0 by construction).
            if (t.DeltaMsSincePrev <= 0f) continue;
            dtSamples.Add(t.DeltaMsSincePrev);
            if (t.DeltaMsSincePrev < minDt) minDt = t.DeltaMsSincePrev;
            if (t.DeltaMsSincePrev > maxDt) maxDt = t.DeltaMsSincePrev;
            sumDt += t.DeltaMsSincePrev;
            dtCount++;
        }
        dtSamples.Sort();
        float avg = dtCount > 0 ? sumDt / dtCount : 0;
        float p50 = dtCount > 0 ? dtSamples[dtCount / 2] : 0;
        float p99 = dtCount > 0 ? dtSamples[(int)(dtCount * 0.99)] : 0;

        // Read GPU's last cached SpinYaw scalar. TryGetScalar returns the value
        // the compositor last synchronized to user-mode; while it's animated
        // this lags by 1 commit, but the relative drift between successive
        // reads still tells us if the GPU's animation timeline is advancing as
        // expected.
        float gpuSpinYaw = float.NaN;
        if (_externalRotationProps != null)
        {
            _externalRotationProps.TryGetScalar("SpinYaw", out gpuSpinYaw);
        }

        // Predict CPU yaw at this exact instant (mirroring the sampler's formula).
        float cpuYawNow = float.NaN, cpuRawNow = float.NaN;
        if (spinning)
        {
            var secs = (float)(elapsedMs / 1000.0);
            cpuRawNow = (secs / SpinSecondsPerTurn) * 360f;
            cpuYawNow = cpuRawNow - MathF.Floor(cpuRawNow / 360f) * 360f;
        }

        lines.Add($"spinning={spinning}");
        lines.Add($"elapsedMs={elapsedMs:F1}");
        lines.Add($"sampler.tickCount={total}");
        lines.Add($"sampler.avgFps={(elapsedMs > 0 ? total * 1000.0 / elapsedMs : 0):F2}");
        lines.Add($"frame.dt.count={dtCount}");
        lines.Add($"frame.dt.minMs={(float.IsPositiveInfinity(minDt) ? 0 : minDt):F3}");
        lines.Add($"frame.dt.avgMs={avg:F3}");
        lines.Add($"frame.dt.p50Ms={p50:F3}");
        lines.Add($"frame.dt.p99Ms={p99:F3}");
        lines.Add($"frame.dt.maxMs={maxDt:F3}");
        lines.Add($"gc.sinceSpinStart.gen0={GC.CollectionCount(0) - _gc0AtStart}");
        lines.Add($"gc.sinceSpinStart.gen1={GC.CollectionCount(1) - _gc1AtStart}");
        lines.Add($"gc.sinceSpinStart.gen2={GC.CollectionCount(2) - _gc2AtStart}");
        lines.Add($"cpu.yaw.now={cpuYawNow:F3}");
        lines.Add($"cpu.yaw.raw={cpuRawNow:F3}");
        lines.Add($"gpu.spinYaw.cached={gpuSpinYaw:F3}");
        lines.Add($"cpu_minus_gpu={(spinning && !float.IsNaN(gpuSpinYaw) ? cpuYawNow - gpuSpinYaw : 0):F3}");

        // Drift stats from the ring buffer: max abs(WallMs - (CompositorMs -
        // _spinStartCompositorMs)). If the new compositor-time sampler is
        // working as designed, this should stay tiny (sub-frame). If we ever
        // see this grow into double-digit ms, something is reclocking the
        // sampler off a different source than expected.
        float maxAbsDrift = 0f, lastDrift = 0f;
        for (int i = 0; i < _spinRing.Length; i++)
        {
            var d = _spinRing[i].DriftMs;
            var ad = MathF.Abs(d);
            if (ad > maxAbsDrift) maxAbsDrift = ad;
            if (_spinRing[i].ElapsedTicks != 0) lastDrift = d;
        }
        lines.Add($"drift.maxAbsMs={maxAbsDrift:F3}");
        lines.Add($"drift.lastMs={lastDrift:F3}");

        if (lastN > 0)
        {
            // Walk backwards from head over the last N entries, dump in chronological order.
            int len = _spinRing.Length;
            int n = (int)Math.Min(lastN, total);
            int start = ((_spinRingHead - n) % len + len) % len;
            lines.Add($"--- last {n} ticks (chronological, ms.relative cpuYawWrap cpuYawRaw deltaMs visMask visCnt gpuYaw drift gc0 gc1 gc2) ---");
            double tickMsPerCount = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            for (int i = 0; i < n; i++)
            {
                var idx = (start + i) % len;
                var t = _spinRing[idx];
                if (t.ElapsedTicks == 0 && t.CpuYawWrapped == 0 && t.CpuYawRaw == 0) continue;
                var ms = t.ElapsedTicks * tickMsPerCount;
                lines.Add($"t={ms:F2} yawW={t.CpuYawWrapped:F2} yawR={t.CpuYawRaw:F2} dt={t.DeltaMsSincePrev:F2} vis=0x{t.VisibleMask:X2}({t.VisibleCount}) gpuY={t.GpuSpinYawCached:F2} drift={t.DriftMs:F2} g0={t.Gc0} g1={t.Gc1} g2={t.Gc2}");
            }
        }
        return lines;
    }

    /// <summary>
    /// Auto-refresh only makes sense in external-rotation mode (in internal mode each
    /// rotation DP setter already triggers a Rebuild on the UI thread). When both
    /// toggles are on, subscribe Combobulate to <c>CompositionTarget.Rendering</c> via
    /// <c>EnableAutoRefresh</c> with a sampler that recomputes the rotation from the
    /// same inputs the GPU expression uses (slider scalars + wall-clock spin).
    /// During spin we never write back to the property set — the GPU KFA owns
    /// <c>SpinYaw</c> and slider changes already update their respective scalars
    /// directly. The sampler also pokes the SceneVisual renderer's
    /// <c>RebuildForExternalRotation</c> so both side-by-side views stay in sync.
    /// </summary>
    private void UpdateAutoRefresh()
    {
        if (combobulate == null) return;
        bool external = ExternalRotationToggle?.IsOn == true;
        bool wantAuto = external && AutoRefreshToggle?.IsOn == true;

        if (wantAuto)
        {
            // Use the compositor-time overload of EnableAutoRefresh: the
            // sampler is given the SAME RenderingTime the compositor will
            // use to evaluate the SpinYaw KFA on the GPU, so the CPU yaw
            // we feed into the cull is the EXACT yaw the compositor will
            // draw. No wall-clock vs compositor-clock drift is possible.
            // (Previously this used DateTime.UtcNow, which would drift
            // ahead of the compositor every time the compositor stalled
            // — accumulating into visible cull/order glitches after ~30s.)
            combobulate.EnableAutoRefresh((TimeSpan renderingTime) =>
            {
                var pitch = (float)(PitchSlider?.Value ?? 0);
                var yaw   = (float)(YawSlider?.Value ?? 0);
                var roll  = (float)(RollSlider?.Value ?? 0);
                float spinYawRaw = 0f, spinYawWrapped = 0f;
                float compMs = (float)renderingTime.TotalMilliseconds;
                if (_spinStart != null)
                {
                    // Latch the compositor-time epoch the FIRST time the
                    // sampler runs after StartGpuSpin. We must do this BEFORE
                    // computing yaw, otherwise the first tick computes yaw
                    // off a 0 epoch (which is the app's start-of-time, hours
                    // ago) and would push a wildly wrong initial yaw.
                    if (_spinStartCompositorMs == 0f && compMs > 0f)
                    {
                        _spinStartCompositorMs = compMs;
                    }
                    var secs = (compMs - _spinStartCompositorMs) / 1000f;
                    if (secs < 0) secs = 0;
                    spinYawRaw = (secs / SpinSecondsPerTurn) * 360f;
                    spinYawWrapped = spinYawRaw - MathF.Floor(spinYawRaw / 360f) * 360f;
                    yaw += spinYawWrapped;
                    // Push the same value to the GPU so the sprite renderer's
                    // ExpressionAnimation reads exactly the value we just used
                    // for cull/sort. With InsertScalar this becomes the value
                    // the compositor sees on the next commit (this same frame).
                    // No KFA, no clock divergence, no phase offset.
                    _externalRotationProps?.InsertScalar("SpinYaw", spinYawWrapped);
                }
                var live = new Vector3(pitch, yaw, roll);
                // CombobulateSceneVisual doesn't have its own auto-refresh hook yet,
                // so piggy-back the same per-frame tick to keep its mesh in sync.
                combobulateSceneVisual.RebuildForExternalRotation(live);
                // Instrumentation: record one ring-buffer entry per sampler tick.
                if (_spinStart != null)
                {
                    var nowTicks = _spinClock.ElapsedTicks;
                    var dtTicks  = nowTicks - _prevTickElapsedTicks;
                    var deltaMs  = (float)((dtTicks * 1000.0) / System.Diagnostics.Stopwatch.Frequency);
                    var wallMs   = (float)((nowTicks * 1000.0) / System.Diagnostics.Stopwatch.Frequency);
                    var driftMs  = compMs > 0f ? (wallMs - (compMs - _spinStartCompositorMs)) : 0f;
                    combobulate.CopyVisibleMaskByte(8, out byte mask, out byte cnt, out _);
                    // GPU-cached SpinYaw should now exactly match spinYawWrapped
                    // since we wrote it via InsertScalar above (no animation =
                    // TryGetScalar returns the actual current value).
                    float gpuYaw = 0f;
                    _externalRotationProps?.TryGetScalar("SpinYaw", out gpuYaw);
                    _spinRing[_spinRingHead] = new SpinTick
                    {
                        ElapsedTicks      = nowTicks,
                        CpuYawWrapped     = spinYawWrapped,
                        CpuYawRaw         = spinYawRaw,
                        DeltaMsSincePrev  = _spinTickCount == 0 ? 0f : deltaMs,
                        Gc0 = GC.CollectionCount(0),
                        Gc1 = GC.CollectionCount(1),
                        Gc2 = GC.CollectionCount(2),
                        VisibleMask       = mask,
                        VisibleCount      = cnt,
                        GpuSpinYawCached  = gpuYaw,
                        CompositorMs      = compMs,
                        WallMs            = wallMs,
                        DriftMs           = driftMs,
                    };
                    _spinRingHead = (_spinRingHead + 1) % _spinRing.Length;
                    _prevTickElapsedTicks = nowTicks;
                    _spinTickCount++;
                }
                return live;
            });
        }
        else
        {
            combobulate.DisableAutoRefresh();
        }
    }

    private void RefreshQuads_Click(object sender, RoutedEventArgs e)
    {
        var rot = new Vector3((float)PitchSlider.Value, (float)YawSlider.Value, (float)RollSlider.Value);
        combobulate.RebuildForExternalRotation(rot);
        combobulateSceneVisual.RebuildForExternalRotation(rot);
    }

    /// <summary>
    /// Routes the slider values either through the controls' rotation
    /// dependency properties (which trigger a paint-order rebuild) or via a
    /// shared <see cref="CompositionPropertySet"/> wired to each control's
    /// composition root via <c>SetExternalRotationSource</c>. In external
    /// mode the property update propagates through the composition graph
    /// without any further UI-thread involvement \u2014 subsequent updates
    /// (including those issued from a non-UI thread or driven by another
    /// composition animation) re-evaluate the bound expression directly on
    /// the composition thread.
    /// </summary>
    // Backing storage for the external-rotation expression. The property
    // set holds the live Vector3 value; the expression `p.Rotation` is what
    // the renderers reference. Subsequent slider changes call InsertVector3
    // and the new value flows entirely through composition with no UI-thread
    // matrix work.
    private CompositionPropertySet? _externalRotationProps;
    private ExpressionAnimation? _externalRotationExpr;

    /// <summary>
    /// Lazily creates the shared property set + composed expression that produces the
    /// final <c>Rotation</c> Vector3 used by both renderers.
    ///
    /// <para>Layout: scalars <c>PitchVal</c>/<c>YawVal</c>/<c>RollVal</c> mirror the
    /// sliders; <c>SpinYaw</c> is animated by a Composition KFA when spin is on;
    /// <c>SpinActive</c> is 0 or 1 to gate the spin contribution. The expression
    /// is <c>Vector3(p.PitchVal, p.YawVal + p.SpinActive * p.SpinYaw, p.RollVal)</c>
    /// — fully evaluated on the compositor every frame, with no UI-thread per-frame
    /// writes.</para>
    /// </summary>
    private (CompositionPropertySet props, ExpressionAnimation expr) GetOrCreateExternalRotation()
    {
        if (_externalRotationProps != null && _externalRotationExpr != null)
            return (_externalRotationProps, _externalRotationExpr);
        var compositor = ElementCompositionPreview.GetElementVisual(this.Content).Compositor;
        _externalRotationProps = compositor.CreatePropertySet();
        _externalRotationProps.InsertScalar("PitchVal",  0f);
        _externalRotationProps.InsertScalar("YawVal",    0f);
        _externalRotationProps.InsertScalar("RollVal",   0f);
        _externalRotationProps.InsertScalar("SpinYaw",   0f);
        _externalRotationProps.InsertScalar("SpinActive", 0f);
        _externalRotationExpr = compositor.CreateExpressionAnimation(
            "Vector3(p.PitchVal, p.YawVal + p.SpinActive * p.SpinYaw, p.RollVal)");
        _externalRotationExpr.SetReferenceParameter("p", _externalRotationProps);
        return (_externalRotationProps, _externalRotationExpr);
    }

    private void ApplyRotation()
    {
        if (combobulate == null) return;
        var x = (float)PitchSlider.Value;
        var y = (float)YawSlider.Value;
        var z = (float)RollSlider.Value;
        bool external = ExternalRotationToggle?.IsOn == true;

        if (external)
        {
            var (props, expr) = GetOrCreateExternalRotation();
            // Per-axis scalar updates: the composed expression rebuilds the Vector3
            // on the compositor without any UI-thread Vector3 write. During spin
            // the YawVal here is the slider base; the GPU KFA on SpinYaw adds the
            // per-frame delta automatically.
            props.InsertScalar("PitchVal", x);
            props.InsertScalar("YawVal",   y);
            props.InsertScalar("RollVal",  z);
            combobulate.SetExternalRotation(expr);
            combobulateSceneVisual.SetExternalRotation(expr);
        }
        else
        {
            combobulate.ClearExternalRotation();
            combobulateSceneVisual.ClearExternalRotation();
            combobulate.RotationX = x;
            combobulate.RotationY = y;
            combobulate.RotationZ = z;
            // combobulateSceneVisual mirrors combobulate's rotation via x:Bind.
        }
#if DEBUG
        LogCurrentRotation();
#endif
    }

    private async void LoadObjButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".obj");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            // Path 2 — file-path cache. Reads + parses on first access; subsequent loads
            // of the same path return the cached geometry, transparently re-parsing only
            // if LastWriteTimeUtc or length has changed on disk.
            var geometry = ObjCache.GetOrLoadFile(file.Path);
            if (geometry.Model.Quads.Count == 0)
            {
                StatusText.Text = $"{file.Name}: no quads found";
                return;
            }

            // Drive via Source so the control records the source directory and
            // any sibling mtllib files load automatically.
            combobulate.Source = file.Path;
            StatusText.Text = $"Loaded: {file.Name} ({geometry.Quads.Length} quads, cached)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load OBJ: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads <c>samples\book.obj</c> via <c>Source</c> so its sibling <c>book.mtl</c>
    /// (with <c>map_Kd cover.png</c> / <c>map_Kd pages.png</c>) auto-loads.
    /// </summary>
    private void LoadBook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ResolveSamplePath("book.obj");
            if (path == null)
            {
                StatusText.Text = "Could not locate samples/book.obj.";
                return;
            }
            combobulate.Source = path;
            StatusText.Text = $"Loaded book.obj — cover/pages textures via mtllib.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load book: {ex.Message}";
        }
    }

    private void MaterialModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (combobulate == null) return;
        var label = (MaterialModeBox.SelectedItem as ComboBoxItem)?.Content as string;
        combobulate.MaterialMode = label switch
        {
            "UseFallback" => global::Combobulate.MaterialMode.UseFallback,
            "UseDiffuse" => global::Combobulate.MaterialMode.UseDiffuse,
            _ => global::Combobulate.MaterialMode.Auto,
        };
    }

    private void SortAlgorithmBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (combobulate == null) return;
        var label = (SortAlgorithmBox.SelectedItem as ComboBoxItem)?.Content as string;
        combobulate.SortAlgorithm = label switch
        {
            "Newell"      => global::Combobulate.Sorting.SortAlgorithm.Newell,
            "Topological" => global::Combobulate.Sorting.SortAlgorithm.Topological,
            _             => global::Combobulate.Sorting.SortAlgorithm.Bsp,
        };
    }

    private static string? ResolveSamplePath(string fileName)
    {
        // Walk up from the app base looking for a samples directory containing the file.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "samples", fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // A unit cube centered on the origin, expressed as 6 quad faces.
    // Vertex order on each face is wound so V0→V1→V3 builds a parallelogram basis
    // pointing roughly outward.
    private const string CubeObj = """
        v -1 -1 -1
        v  1 -1 -1
        v  1  1 -1
        v -1  1 -1
        v -1 -1  1
        v  1 -1  1
        v  1  1  1
        v -1  1  1

        # +Z (front)
        f 5 6 7 8
        # -Z (back)
        f 2 1 4 3
        # +X (right)
        f 6 2 3 7
        # -X (left)
        f 1 5 8 4
        # +Y (top)
        f 8 7 3 4
        # -Y (bottom)
        f 5 1 2 6
        """;
}

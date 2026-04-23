#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Combobulate.Caching;
using Combobulate.Parsing;
using Microsoft.UI.Dispatching;
using Newtonsoft.Json.Linq;
using zRover.Core;

namespace Combobulate.Sample.WinUI3;

public sealed partial class MainWindow : zRover.Core.IActionableApp
{
    private static readonly IReadOnlyList<ActionDescriptor> _actions = new[]
    {
        new ActionDescriptor(
            name: "LoadObjFromPath",
            description: "Loads an OBJ file from the given absolute path into the Combobulate viewer, " +
                         "bypassing the file picker. Updates StatusText with the result.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""path""],
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""Absolute path to the .obj file."" }
  }
}"),
        new ActionDescriptor(
            name: "SetRotation",
            description: "Sets the viewer rotation in degrees. Any omitted axis keeps its current value.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""properties"": {
    ""x"": { ""type"": ""number"", ""minimum"": -360, ""maximum"": 360 },
    ""y"": { ""type"": ""number"", ""minimum"": -360, ""maximum"": 360 },
    ""z"": { ""type"": ""number"", ""minimum"": -360, ""maximum"": 360 }
  }
}"),
        new ActionDescriptor(
            name: "ResetRotation",
            description: "Resets all three rotation axes to 0.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "GetState",
            description: "Returns the current viewer state (rotation X/Y/Z degrees, zoom, external-rotation toggle, auto-refresh toggle, current Source) PLUS a snapshot of the sprite renderer's per-frame cull/order caches (visible[], order[]), the live _root.Children stack, and the live SpriteVisual.IsVisible flags. Returned via the ActionResult.Consequences string list as 'key=value' lines so a caller can correlate a screenshot with the exact internal state that produced it.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "ForceRebuild",
            description: "Diagnostic: invalidates the sprite renderer's per-frame cull/order skip caches (_lastVisible, _lastOrder) and triggers a full rebuild against the current rotation. Use this when the view looks broken: if it heals after this call, the bug is in the skip caches; if not, the bug is elsewhere (rotation matrix, expression animation, painter algorithm).",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "SetToggles",
            description: "Sets the ExternalRotation and AutoRefresh toggle switches. Either or both may be omitted to keep current. Use this to script the exact mode used to reproduce a rotation-update bug.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""properties"": {
    ""externalRotation"": { ""type"": ""boolean"" },
    ""autoRefresh"": { ""type"": ""boolean"" }
  }
}"),
        new ActionDescriptor(
            name: "ResetCube",
            description: "Reloads the built-in cube model.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "SetZoom",
            description: "Sets the viewer zoom (ModelScale). Typical range 10-500.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""zoom""],
  ""properties"": {
    ""zoom"": { ""type"": ""number"", ""minimum"": 1, ""maximum"": 1000 }
  }
}"),
        new ActionDescriptor(
            name: "SetMaterial",
            description: "Adjusts CombobulateSceneVisual material/render parameters at runtime so they can be tuned without rebuilding. Any omitted property keeps its current value. emissiveBoost: HDR multiplier on EmissiveFactor (try 1-10). baseColorScale: multiplier on BaseColorFactor (0 = pure-emissive). metallic/roughness: PBR factors. flipZ: toggle Z-axis negation in the projection (changes which faces are front-facing).",
            parameterSchema: @"{
  ""type"": ""object"",
  ""properties"": {
    ""emissiveBoost"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 50 },
    ""baseColorScale"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 10 },
    ""metallic"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
    ""roughness"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
    ""flipZ"": { ""type"": ""boolean"" }
  }
}"),
        new ActionDescriptor(
            name: "SetSpin",
            description: "Toggles the SpinToggle (autonomous Y-axis spin). Pass on=true to start, false to stop.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""on""],
  ""properties"": {
    ""on"": { ""type"": ""boolean"" }
  }
}"),
        new ActionDescriptor(
            name: "DumpSpinDiagnostics",
            description: "Returns per-tick spin instrumentation: total ticks, total wall-clock elapsed, frame-time stats (min/max/avg/p99 deltaMs), GC collection counts since spin start, last-N tick rows. Use this after running spin for ~30-60s to diagnose CPU/GPU clock drift, frame jitter, or GC-induced stalls.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""properties"": {
    ""lastN"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1024, ""description"": ""Number of most-recent ring-buffer rows to dump (default 0 = stats only)."" }
  }
}"),
        new ActionDescriptor(
            name: "EnableSortDiagnostics",
            description: "Turns on the renderer's sort/reorder ring buffer (Combobulate.Diagnostics.SpinDiagnostics). Each call to Combobulate.Rebuild thereafter records frameId, threadId, recovered yaw/pitch, visible-mask, order[], orderChanged, mutationsApplied, and per-phase microsecond timings. Use this BEFORE starting a spin to capture the first ~5 seconds of frames for sync-glitch diagnosis.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "DisableSortDiagnostics",
            description: "Turns off the sort/reorder ring buffer. Existing entries remain queryable via DumpSortDiagnostics until the next Enable call resets the cursor.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "DumpSortDiagnostics",
            description: "Returns the contents of the renderer's sort/reorder ring buffer. Two modes: 'summary' (default — counts, threads, mutation-per-frame stats, slowest reorder frame) or 'full' (every record as JSON). Use 'full' for offline analysis of sync glitches; 'summary' for at-a-glance triage.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""summary"", ""full""], ""description"": ""Default 'summary'. 'full' returns the entire ring serialised as JSON (~256KB)."" }
  }
}"),
        new ActionDescriptor(
            name: "RunZSortTest",
            description: "Spike: validates whether the WinUI Composition compositor honours per-sprite Offset.Z when paint-order-resolving overlapping siblings, OR whether it always paints in VisualCollection order regardless of Z. Creates a full-window overlay with two 200x200 SpriteVisuals (red at child[0], blue at child[1], both centred and overlapping), under a parent ContainerVisual that has a PerspectiveTransform applied via TransformMatrix. Sets the requested Z offsets, then the caller takes a screenshot and inspects which colour is on top. Convention: +Z = toward viewer (out of screen). Baseline (redZ=0,blueZ=0): blue should always be on top (later in tree). Test (redZ=+200,blueZ=0): if compositor depth-sorts, red appears on top; if compositor uses tree order, blue stays on top. The overlay persists between calls so you can call repeatedly with different Z values without rebuilding the scene.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""redZ"", ""blueZ""],
  ""properties"": {
    ""redZ"":  { ""type"": ""number"", ""minimum"": -500, ""maximum"": 500, ""description"": ""Offset.Z for the red sprite (child[0])."" },
    ""blueZ"": { ""type"": ""number"", ""minimum"": -500, ""maximum"": 500, ""description"": ""Offset.Z for the blue sprite (child[1])."" }
  }
}"),
        new ActionDescriptor(
            name: "ClearZSortTest",
            description: "Tears down the Z-sort spike overlay created by RunZSortTest, restoring the normal app UI.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "RunAtomicSwapTest",
            description: "Spike: validates whether two ExpressionAnimation-driven Opacity flips, both referencing the SAME CompositionPropertySet scalar, are guaranteed to land in the SAME composited frame. Sets up two fully-overlapping ContainerVisuals (treeA holds a red 400x400 sprite, treeB holds a blue 400x400 sprite, perfectly overlapping in the centre of the window). Both are attached as siblings under a single overlay container. A shared CompositionPropertySet exposes scalar 'K' in [0,1]. Each tree's Opacity is bound by ExpressionAnimation: treeA.Opacity = props.K < 0.5 ? 1 : 0, treeB.Opacity = props.K < 0.5 ? 0 : 1. Static mode (mode='static'): K is set to the supplied value, no animation - use to verify the static threshold behaviour. Animated mode (mode='animate'): a linear KFA drives K from 0 to 1 over durationSec seconds (default 8s) so the caller can capture screenshots at random moments and look for any frame where BOTH sprites are visible (purple/magenta blend) or NEITHER is visible (background showing through), either of which would prove that the two Opacity flips are NOT atomic and the dual-tree-swap architecture is unsafe. Stop with ClearAtomicSwapTest. Convention: red sprite is on screen-left half, blue is on screen-right half but they OVERLAP in the centre by 200px so atomicity violations show as a coloured stripe.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""mode""],
  ""properties"": {
    ""mode"":        { ""type"": ""string"", ""enum"": [""static"", ""animate""] },
    ""k"":           { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1, ""description"": ""Static-mode K value."" },
    ""durationSec"": { ""type"": ""number"", ""minimum"": 0.5, ""maximum"": 60, ""description"": ""Animated-mode 0->1 ramp duration."" }
  }
}"),
        new ActionDescriptor(
            name: "ClearAtomicSwapTest",
            description: "Tears down the atomic-swap spike overlay created by RunAtomicSwapTest.",
            parameterSchema: @"{""type"":""object"",""properties"":{}}"),
        new ActionDescriptor(
            name: "RunBspBoundaryEquivalenceTest",
            description: "CPU-only diagnostic for content-equivalence claim of the dual-tree-swap proposal. Loads the supplied OBJ, builds the BSP, picks a partitioning plane, computes the visible+ordered face list at the boundary yaw with cameraSide=+1 (forced) and cameraSide=-1 (forced) for that plane only. Returns both orders as Consequences; if the visible-and-ordered face lists are identical the swap is content-safe at that boundary; if they differ, the swap will produce a one-frame visual pop at boundary crossings - same problem we have today, just relocated.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""path"", ""yawDeg""],
  ""properties"": {
    ""path"":     { ""type"": ""string"", ""description"": ""Absolute path to the .obj file."" },
    ""yawDeg"":   { ""type"": ""number"", ""description"": ""Yaw angle (degrees) to evaluate the BSP at."" },
    ""planeIdx"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Which BSP partitioning plane to flip cameraSide on (0 = root). Default 0."" }
  }
}"),
        new ActionDescriptor(
            name: "SetSpinSeconds",
            description: "Sets SpinSecondsPerTurn live (drives the UI slider's Value, which triggers the same OnSpinSpeedChanged path: resets the sampler epoch and restarts the GPU KFA if currently spinning). Range 0.5..30. Use to test whether flicker is angular-speed-dependent: if drift scales with 1/seconds the cause is a constant TIME offset (clock-skew hypothesis); if drift is roughly speed-independent in degrees the cause is geometric/numeric in the sort path.",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""seconds""],
  ""properties"": {
    ""seconds"": { ""type"": ""number"", ""minimum"": 0.5, ""maximum"": 30, ""description"": ""Seconds per full 360 deg revolution."" }
  }
}"),
        new ActionDescriptor(
            name: "SetSpinPhaseOffset",
            description: "Tunes the millisecond offset subtracted from the latched compositor-time epoch when the spin sampler latches its first tick. Compensates for the gap between props.StartAnimation(SpinYaw, kfa) and the first observed CompositionTarget.Rendering tick — the GPU KFA actually starts ~1 frame BEFORE the latch, so without this offset the CPU's computed yaw lags the GPU's drawn yaw by a constant ~1°, which appears as a one-frame stale-order flicker at every sort-order boundary (~every 30° of spin). Default 16.667ms (one 60Hz frame). Sweep via repeated calls + visual inspection while spinning to find the offset where flicker disappears. Takes effect on the NEXT StartGpuSpin (toggle SetSpin off then on).",
            parameterSchema: @"{
  ""type"": ""object"",
  ""required"": [""offsetMs""],
  ""properties"": {
    ""offsetMs"": { ""type"": ""number"", ""minimum"": -100, ""maximum"": 100, ""description"": ""Milliseconds to subtract from the latched compositor-time epoch. Negative values shift CPU yaw BEHIND GPU yaw; positive values shift it AHEAD."" }
  }
}"),
    };

    public IReadOnlyList<ActionDescriptor> GetAvailableActions() => _actions;

    public Task<ActionResult> DispatchAsync(string actionName, string parametersJson)
    {
        switch (actionName)
        {
            case "LoadObjFromPath": return DispatchLoadObjFromPathAsync(parametersJson);
            case "SetRotation": return DispatchSetRotationAsync(parametersJson);
            case "ResetRotation": return RunOnUi(() => { PitchSlider.Value = 0; YawSlider.Value = 0; RollSlider.Value = 0; });
            case "GetState": return DispatchGetStateAsync();
            case "ForceRebuild": return RunOnUi(() =>
            {
                combobulate.InvalidateRenderCaches();
                var rot = new System.Numerics.Vector3((float)PitchSlider.Value, (float)YawSlider.Value, (float)RollSlider.Value);
                combobulate.RebuildForExternalRotation(rot);
            });
            case "SetToggles": return DispatchSetTogglesAsync(parametersJson);
            case "ResetCube": return RunOnUi(LoadCube);
            case "SetZoom": return DispatchSetZoomAsync(parametersJson);
            case "SetMaterial": return DispatchSetMaterialAsync(parametersJson);
            case "SetSpin": return DispatchSetSpinAsync(parametersJson);
            case "DumpSpinDiagnostics": return DispatchDumpSpinDiagnosticsAsync(parametersJson);
            case "EnableSortDiagnostics":
                global::Combobulate.Diagnostics.SpinDiagnostics.Enable();
                return Task.FromResult(ActionResult.Ok(new[] { "sortDiagnostics=enabled" }));
            case "DisableSortDiagnostics":
                global::Combobulate.Diagnostics.SpinDiagnostics.Disable();
                return Task.FromResult(ActionResult.Ok(new[] { "sortDiagnostics=disabled" }));
            case "DumpSortDiagnostics": return DispatchDumpSortDiagnosticsAsync(parametersJson);
            case "SetSpinPhaseOffset": return DispatchSetSpinPhaseOffsetAsync(parametersJson);
            case "SetSpinSeconds": return DispatchSetSpinSecondsAsync(parametersJson);
            case "RunZSortTest": return DispatchRunZSortTestAsync(parametersJson);
            case "ClearZSortTest": return RunOnUi(ClearZSortTest);
            case "RunAtomicSwapTest": return DispatchRunAtomicSwapTestAsync(parametersJson);
            case "ClearAtomicSwapTest": return RunOnUi(ClearAtomicSwapTest);
            case "RunBspBoundaryEquivalenceTest": return DispatchRunBspBoundaryEquivalenceTestAsync(parametersJson);
            default: return Task.FromResult(ActionResult.Fail("unknown_action", $"No action named '{actionName}'."));
        }
    }

    private async Task<ActionResult> DispatchLoadObjFromPathAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return ActionResult.Fail("validation_error", "params is not valid JSON."); }

        var path = p["path"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(path))
            return ActionResult.Fail("validation_error", "params.path is required.");
        if (!File.Exists(path))
            return ActionResult.Fail("validation_error", $"File not found: {path}");

        ObjGeometry geometry;
        try { geometry = ObjCache.GetOrLoadFile(path); }
        catch (Exception ex) { return ActionResult.Fail("execution_error", $"Load failed: {ex.Message}"); }

        if (geometry.Model.Quads.Count == 0)
            return ActionResult.Fail("execution_error", "No quads parsed.");

        return await RunOnUi(() =>
        {
            combobulate.Source = path;
            var name = Path.GetFileName(path);
            StatusText.Text = $"Loaded: {name} ({geometry.Quads.Length} quads, cached)";
        });
    }

    private Task<ActionResult> DispatchSetRotationAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

        return RunOnUi(() =>
        {
            if (p["x"] != null) PitchSlider.Value = p["x"]!.Value<double>();
            if (p["y"] != null) YawSlider.Value = p["y"]!.Value<double>();
            if (p["z"] != null) RollSlider.Value = p["z"]!.Value<double>();
        });
    }

    private Task<ActionResult> DispatchSetZoomAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

        var zoomToken = p["zoom"];
        if (zoomToken == null)
            return Task.FromResult(ActionResult.Fail("validation_error", "params.zoom is required."));

        return RunOnUi(() => combobulate.ModelScale = zoomToken.Value<double>());
    }

    private Task<ActionResult> DispatchSetMaterialAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

        return RunOnUi(() =>
        {
            // Apply to both renderers' ModelScale-equivalent material on the
            // 3D pane. The sprite renderer (combobulate) ignores these.
            if (p["emissiveBoost"] != null) combobulateSceneVisual.EmissiveBoost = p["emissiveBoost"]!.Value<double>();
            if (p["baseColorScale"] != null) combobulateSceneVisual.BaseColorScale = p["baseColorScale"]!.Value<double>();
            if (p["metallic"] != null) combobulateSceneVisual.MetallicFactor = p["metallic"]!.Value<double>();
            if (p["roughness"] != null) combobulateSceneVisual.RoughnessFactor = p["roughness"]!.Value<double>();
            if (p["flipZ"] != null) combobulateSceneVisual.FlipZ = p["flipZ"]!.Value<bool>();
            // Also push the same zoom to the 3D pane for convenience.
            combobulateSceneVisual.ModelScale = combobulate.ModelScale;
        });
    }

    private Task<ActionResult> DispatchSetSpinAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }
        var onToken = p["on"];
        if (onToken == null) return Task.FromResult(ActionResult.Fail("validation_error", "params.on is required."));
        var on = onToken.Value<bool>();
        return RunOnUi(() => { if (SpinToggle != null) SpinToggle.IsOn = on; });
    }

    private Task<ActionResult> DispatchSetSpinSecondsAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }
        var token = p["seconds"];
        if (token == null) return Task.FromResult(ActionResult.Fail("validation_error", "params.seconds is required."));
        var secs = token.Value<double>();
        if (secs < 0.5 || secs > 30) return Task.FromResult(ActionResult.Fail("validation_error", "seconds must be in [0.5, 30]."));
        return RunOnUi(() =>
        {
            if (SpinSpeedSlider != null) SpinSpeedSlider.Value = secs; // fires OnSpinSpeedChanged
        });
    }

    private Task<ActionResult> DispatchSetSpinPhaseOffsetAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }
        var token = p["offsetMs"];
        if (token == null) return Task.FromResult(ActionResult.Fail("validation_error", "params.offsetMs is required."));
        var offset = token.Value<float>();
        if (offset < -100f || offset > 100f) return Task.FromResult(ActionResult.Fail("validation_error", "offsetMs must be in [-100, 100]."));
        return RunOnUi(() =>
        {
            // Apply mid-spin: shift the latched compositor-time epoch by the
            // delta between old and new offset so the change takes effect on
            // the very next sampler tick. (offset is SUBTRACTED from compMs to
            // produce the latched epoch, so increasing offset pushes the
            // epoch BACKWARDS, which advances CPU yaw FORWARD.)
            var delta = _spinPhaseOffsetMs - offset;  // old - new
            _spinPhaseOffsetMs = offset;
            if (_spinStartCompositorMsD != 0.0)
            {
                _spinStartCompositorMsD += delta;
                _spinStartCompositorMs   = (float)_spinStartCompositorMsD;
            }
        });
    }

    private Task<ActionResult> DispatchDumpSpinDiagnosticsAsync(string parametersJson)
    {
        int lastN = 0;
        try
        {
            var p = JObject.Parse(parametersJson);
            if (p["lastN"] != null) lastN = Math.Clamp(p["lastN"]!.Value<int>(), 0, 1024);
        }
        catch { /* tolerate missing/empty params */ }

        var tcs = new TaskCompletionSource<ActionResult>();
        var dq = this.DispatcherQueue;
        if (!dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try { tcs.SetResult(ActionResult.Ok(DumpSpinDiagnosticsLines(lastN))); }
            catch (Exception ex) { tcs.SetResult(ActionResult.Fail("execution_error", ex.Message)); }
        }))
        {
            tcs.SetResult(ActionResult.Fail("execution_error", "Failed to enqueue UI work."));
        }
        return tcs.Task;
    }

    private Task<ActionResult> DispatchDumpSortDiagnosticsAsync(string parametersJson)
    {
        // No UI-thread marshalling required: SpinDiagnostics is lock-free and
        // safe to read from any thread. Snapshot may briefly race with new
        // writes from the rendering tick, which is fine — the report is for
        // human triage, not transactional inspection.
        string mode = "summary";
        try
        {
            var p = JObject.Parse(parametersJson);
            if (p["mode"] != null) mode = (p["mode"]!.Value<string>() ?? "summary").ToLowerInvariant();
        }
        catch { /* tolerate missing/empty params */ }

        try
        {
            var payload = mode == "full"
                ? global::Combobulate.Diagnostics.SpinDiagnostics.Snapshot()
                : global::Combobulate.Diagnostics.SpinDiagnostics.SummaryReport();
            // Wrap in a single Consequences line — the rover transport already
            // splits long strings into multiple message parts at the client.
            return Task.FromResult(ActionResult.Ok(new[] { payload }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("execution_error", ex.Message));
        }
    }

    private Task<ActionResult> DispatchSetTogglesAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

        return RunOnUi(() =>
        {
            if (p["externalRotation"] != null && ExternalRotationToggle != null)
                ExternalRotationToggle.IsOn = p["externalRotation"]!.Value<bool>();
            if (p["autoRefresh"] != null && AutoRefreshToggle != null)
                AutoRefreshToggle.IsOn = p["autoRefresh"]!.Value<bool>();
        });
    }

    private Task<ActionResult> DispatchGetStateAsync()
    {
        var tcs = new TaskCompletionSource<ActionResult>();
        var dq = this.DispatcherQueue;
        if (!dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                var (cacheVisible, cacheOrder) = combobulate.GetRenderCacheSnapshot();
                var actualOrder = combobulate.GetActualChildrenOrder();
                var liveVisible = combobulate.GetLiveSpriteVisibility();
                var lines = new List<string>
                {
                    $"rotationX={PitchSlider.Value:F3}",
                    $"rotationY={YawSlider.Value:F3}",
                    $"rotationZ={RollSlider.Value:F3}",
                    $"zoom={combobulate.ModelScale:F3}",
                    $"externalRotation={(ExternalRotationToggle?.IsOn == true)}",
                    $"autoRefresh={(AutoRefreshToggle?.IsOn == true)}",
                    $"perspective={combobulate.EnablePerspective}",
                    $"source={combobulate.Source ?? "(null)"}",
                    $"cache.visible={string.Join(",", cacheVisible)}",
                    $"cache.order={string.Join(",", cacheOrder)}",
                    $"children.actual={string.Join(",", actualOrder)}",
                    $"sprite.visible={string.Join(",", liveVisible)}",
                };
                tcs.SetResult(ActionResult.Ok(lines));
            }
            catch (Exception ex) { tcs.SetResult(ActionResult.Fail("execution_error", ex.Message)); }
        }))
        {
            tcs.SetResult(ActionResult.Fail("execution_error", "Failed to enqueue UI work."));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Continuous diagnostic log: writes the current rotation triple to the rover
    /// log every time ApplyRotation runs so a tail of the diagnostic log shows
    /// the live angle history.
    /// </summary>
    internal void LogCurrentRotation()
    {
        try
        {
            zRover.WinUI.RoverMcp.Log(
                "Combobulate",
                $"rotation x={PitchSlider.Value:F2} y={YawSlider.Value:F2} z={RollSlider.Value:F2} " +
                $"external={ExternalRotationToggle?.IsOn == true} auto={AutoRefreshToggle?.IsOn == true}");
        }
        catch { /* logger is best-effort */ }
    }

    private Task<ActionResult> RunOnUi(Action action)
    {
        var tcs = new TaskCompletionSource<ActionResult>();
        var dq = this.DispatcherQueue;
        if (!dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try { action(); tcs.SetResult(ActionResult.Ok()); }
            catch (Exception ex) { tcs.SetResult(ActionResult.Fail("execution_error", ex.Message)); }
        }))
        {
            tcs.SetResult(ActionResult.Fail("execution_error", "Failed to enqueue UI work."));
        }
        return tcs.Task;
    }

    // ---------- Z-sort validation spike ----------
    // Goal: prove or disprove that Microsoft.UI.Composition's compositor
    // depth-sorts overlapping siblings by Offset.Z. The visual tree is set
    // up as: hostVisual -> container (with PerspectiveTransform) -> [red, blue].
    // Red is child[0] (would paint FIRST in tree-order). Blue is child[1]
    // (would paint LAST = on top in tree-order). If the compositor honours
    // depth, raising red's Z above blue's should make red appear on top.

    private Microsoft.UI.Composition.ContainerVisual? _zTestContainer;
    private Microsoft.UI.Composition.SpriteVisual? _zTestRed;
    private Microsoft.UI.Composition.SpriteVisual? _zTestBlue;

    private Task<ActionResult> DispatchRunZSortTestAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }
        var rZ = p["redZ"];  if (rZ == null) return Task.FromResult(ActionResult.Fail("validation_error", "params.redZ is required."));
        var bZ = p["blueZ"]; if (bZ == null) return Task.FromResult(ActionResult.Fail("validation_error", "params.blueZ is required."));
        var redZ  = (float)rZ.Value<double>();
        var blueZ = (float)bZ.Value<double>();
        return RunOnUi(() => SetupOrUpdateZSortTest(redZ, blueZ));
    }

    private void SetupOrUpdateZSortTest(float redZ, float blueZ)
    {
        var hostVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this.Content);
        var compositor = hostVisual.Compositor;

        // Use the framework element's actual size, NOT hostVisual.Size, which
        // is (0,0) for the root because Composition sizes only get pushed up
        // for visuals inside ElementCompositionPreview-hosted trees.
        var rootFE = this.Content as Microsoft.UI.Xaml.FrameworkElement;
        float width  = rootFE != null && rootFE.ActualWidth  > 0 ? (float)rootFE.ActualWidth  : 1200f;
        float height = rootFE != null && rootFE.ActualHeight > 0 ? (float)rootFE.ActualHeight : 800f;

        if (_zTestContainer == null)
        {
            _zTestContainer = compositor.CreateContainerVisual();
            _zTestContainer.Size = new System.Numerics.Vector2(width, height);
            _zTestContainer.Offset = new System.Numerics.Vector3(0, 0, 0);

            // Perspective: looking down +Z. The classic "perspective dip"
            // matrix used in WinUI Composition tutorials is:
            //   M[3,2] = -1 / cameraDistance
            // This makes Offset.Z affect projected size AND, if the
            // compositor depth-sorts, paint order.
            var perspective = System.Numerics.Matrix4x4.Identity;
            perspective.M34 = -1.0f / 500.0f;
            // Centre the perspective vanishing point at the middle of the window.
            var centerOffset = System.Numerics.Matrix4x4.CreateTranslation(-width / 2, -height / 2, 0);
            var centerBack   = System.Numerics.Matrix4x4.CreateTranslation( width / 2,  height / 2, 0);
            _zTestContainer.TransformMatrix = centerOffset * perspective * centerBack;

            // Red sprite at child[0] = paints first under tree-order.
            // Place it slightly LEFT of centre so the overlap is obvious.
            _zTestRed = compositor.CreateSpriteVisual();
            _zTestRed.Size = new System.Numerics.Vector2(300, 300);
            _zTestRed.Offset = new System.Numerics.Vector3(width / 2 - 200, height / 2 - 150, 0);
            _zTestRed.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 220, 40, 40));
            _zTestContainer.Children.InsertAtTop(_zTestRed);

            // Blue sprite at child[1] = paints last under tree-order = on top in 2D.
            // Place it slightly RIGHT so left/right halves of each sprite are visible.
            _zTestBlue = compositor.CreateSpriteVisual();
            _zTestBlue.Size = new System.Numerics.Vector2(300, 300);
            _zTestBlue.Offset = new System.Numerics.Vector3(width / 2 - 100, height / 2 - 150, 0);
            _zTestBlue.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 40, 80, 220));
            _zTestContainer.Children.InsertAtTop(_zTestBlue);

            // Attach overlay on top of the existing app UI.
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(this.Content, _zTestContainer);
        }
        else
        {
            // Keep size in sync with current window size in case the user resized.
            _zTestContainer.Size = new System.Numerics.Vector2(width, height);
        }

        // Update Z values (only Z; X/Y stay where we put them above so the
        // overlap is fixed and only the depth changes).
        if (_zTestRed != null)
        {
            var off = _zTestRed.Offset;  off.Z = redZ;  _zTestRed.Offset = off;
        }
        if (_zTestBlue != null)
        {
            var off = _zTestBlue.Offset; off.Z = blueZ; _zTestBlue.Offset = off;
        }
    }

    private void ClearZSortTest()
    {
        if (_zTestContainer != null)
        {
            _zTestContainer.Children.RemoveAll();
            _zTestContainer.Dispose();
            _zTestContainer = null;
            _zTestRed  = null;
            _zTestBlue = null;
            // Restore the original child visual (which is what SetElementChildVisual
            // had replaced - usually null, the framework manages the host).
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(this.Content, null);
        }
    }

    // ---------- Atomic-swap validation spike ----------
    // Goal: prove or disprove that two ExpressionAnimation-driven Opacity flips
    // referencing the SAME CompositionPropertySet scalar land in the SAME
    // composited frame. If they don't, the dual-tree-swap architecture is unsafe
    // (could show a frame of "both opaque" = colour blend or "both transparent"
    // = background gap).

    private Microsoft.UI.Composition.ContainerVisual? _atomTestRoot;
    private Microsoft.UI.Composition.ContainerVisual? _atomTreeA;
    private Microsoft.UI.Composition.ContainerVisual? _atomTreeB;
    private Microsoft.UI.Composition.CompositionPropertySet? _atomProps;

    private Task<ActionResult> DispatchRunAtomicSwapTestAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }
        var modeToken = p["mode"];
        if (modeToken == null) return Task.FromResult(ActionResult.Fail("validation_error", "params.mode is required."));
        var mode = modeToken.Value<string>();
        var k = p["k"]?.Value<float>() ?? 0f;
        var dur = p["durationSec"]?.Value<float>() ?? 8f;
        return RunOnUi(() => SetupOrUpdateAtomicSwapTest(mode!, k, dur));
    }

    private void SetupOrUpdateAtomicSwapTest(string mode, float k, float durationSec)
    {
        var hostVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this.Content);
        var compositor = hostVisual.Compositor;

        var rootFE = this.Content as Microsoft.UI.Xaml.FrameworkElement;
        float width  = rootFE != null && rootFE.ActualWidth  > 0 ? (float)rootFE.ActualWidth  : 1200f;
        float height = rootFE != null && rootFE.ActualHeight > 0 ? (float)rootFE.ActualHeight : 800f;

        if (_atomTestRoot == null)
        {
            _atomTestRoot = compositor.CreateContainerVisual();
            _atomTestRoot.Size = new System.Numerics.Vector2(width, height);

            // Shared CompositionPropertySet exposing scalar K. Both trees'
            // Opacity is bound to the SAME property here, so if there's any
            // intra-frame coherence, both expressions see the same K sample.
            _atomProps = compositor.CreatePropertySet();
            _atomProps.InsertScalar("K", 0f);

            // treeA: red 400x400 sprite, screen-left half but centred so the
            // RIGHT 200px of red overlaps the LEFT 200px of blue.
            _atomTreeA = compositor.CreateContainerVisual();
            _atomTreeA.Size = new System.Numerics.Vector2(width, height);
            var redSprite = compositor.CreateSpriteVisual();
            redSprite.Size = new System.Numerics.Vector2(400, 400);
            redSprite.Offset = new System.Numerics.Vector3(width / 2 - 400, height / 2 - 200, 0);
            redSprite.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 230, 30, 30));
            _atomTreeA.Children.InsertAtTop(redSprite);
            _atomTestRoot.Children.InsertAtTop(_atomTreeA);

            // treeB: blue 400x400 sprite, screen-right half overlapping by 200.
            _atomTreeB = compositor.CreateContainerVisual();
            _atomTreeB.Size = new System.Numerics.Vector2(width, height);
            var blueSprite = compositor.CreateSpriteVisual();
            blueSprite.Size = new System.Numerics.Vector2(400, 400);
            blueSprite.Offset = new System.Numerics.Vector3(width / 2, height / 2 - 200, 0);
            blueSprite.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 30, 60, 230));
            _atomTreeB.Children.InsertAtTop(blueSprite);
            _atomTestRoot.Children.InsertAtTop(_atomTreeB);

            // Bind both Opacity values to the SAME shared property via expressions.
            // If the compositor evaluates these atomically per-frame, both flip in
            // the same composited frame at K crossing 0.5: red goes 1->0, blue goes
            // 0->1. If atomicity is broken, there's a visible coloured stripe in
            // the 200px overlap zone for at least one frame.
            var exprA = compositor.CreateExpressionAnimation("props.K < 0.5 ? 1.0 : 0.0");
            exprA.SetReferenceParameter("props", _atomProps);
            _atomTreeA.StartAnimation("Opacity", exprA);

            var exprB = compositor.CreateExpressionAnimation("props.K < 0.5 ? 0.0 : 1.0");
            exprB.SetReferenceParameter("props", _atomProps);
            _atomTreeB.StartAnimation("Opacity", exprB);

            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(this.Content, _atomTestRoot);
        }

        // Drive K either statically (for deterministic threshold tests) or by a
        // linear KFA (for stress-testing during continuous sweep).
        if (_atomProps != null)
        {
            // Stop any prior K animation by inserting a fresh static value.
            _atomProps.StopAnimation("K");
            if (mode == "static")
            {
                _atomProps.InsertScalar("K", k);
            }
            else if (mode == "animate")
            {
                var kfa = compositor.CreateScalarKeyFrameAnimation();
                kfa.InsertKeyFrame(0f, 0f, compositor.CreateLinearEasingFunction());
                kfa.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
                kfa.Duration = TimeSpan.FromSeconds(durationSec);
                kfa.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                _atomProps.StartAnimation("K", kfa);
            }
        }
    }

    private void ClearAtomicSwapTest()
    {
        if (_atomTestRoot != null)
        {
            _atomTreeA?.StopAnimation("Opacity");
            _atomTreeB?.StopAnimation("Opacity");
            _atomProps?.StopAnimation("K");
            _atomTestRoot.Children.RemoveAll();
            _atomTestRoot.Dispose();
            _atomTestRoot = null;
            _atomTreeA = null;
            _atomTreeB = null;
            _atomProps = null;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(this.Content, null);
        }
    }

    // ---------- BSP boundary content-equivalence diagnostic ----------
    // Goal: at a yaw close to a BSP plane crossing, do the two adjacent yaws
    // (yaw - eps and yaw + eps) produce the SAME visible+ordered face list?
    // If yes, the dual-tree-swap is content-safe at that boundary. If no,
    // swapping trees at the boundary will produce a one-frame pop.

    private Task<ActionResult> DispatchRunBspBoundaryEquivalenceTestAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }
        var path = p["path"]?.Value<string>();
        var yawDeg = p["yawDeg"]?.Value<double>();
        if (string.IsNullOrEmpty(path) || yawDeg == null)
            return Task.FromResult(ActionResult.Fail("validation_error", "params.path and params.yawDeg are required."));
        if (!System.IO.File.Exists(path))
            return Task.FromResult(ActionResult.Fail("validation_error", $"File not found: {path}"));

        try
        {
            var text = System.IO.File.ReadAllText(path);
            var parsed = global::Combobulate.Parsing.ObjParser.Parse(text);
            if (!parsed.Success)
                return Task.FromResult(ActionResult.Fail("parse_error", $"OBJ parse failed: {parsed.Errors.Count} errors."));

            var geom = global::Combobulate.Caching.ObjGeometry.Build(parsed.Model);
            var sorter = global::Combobulate.Sorting.FaceSorterFactory.Create(global::Combobulate.Sorting.SortAlgorithm.Bsp, geom);

            // Sample at yaw-eps and yaw+eps. eps deliberately small (0.01 deg)
            // - smaller than a typical compositor frame's worth of yaw drift.
            const double eps = 0.01;
            var yaw1 = (float)((yawDeg.Value - eps) * Math.PI / 180.0);
            var yaw2 = (float)((yawDeg.Value + eps) * Math.PI / 180.0);
            var rot1 = System.Numerics.Matrix4x4.CreateRotationY(yaw1);
            var rot2 = System.Numerics.Matrix4x4.CreateRotationY(yaw2);

            // Use the same CullMargin we ship with at v1.0.24 (3 deg) so this
            // test reflects production behaviour, not a hypothetical.
            var marginCos = (float)Math.Sin(3.0 * Math.PI / 180.0);

            var order1 = new int[geom.Quads.Length];
            var vis1 = new bool[geom.Quads.Length];
            var n1 = sorter.Sort(rot1, order1, vis1, 0f, marginCos);

            var order2 = new int[geom.Quads.Length];
            var vis2 = new bool[geom.Quads.Length];
            var n2 = sorter.Sort(rot2, order2, vis2, 0f, marginCos);

            var visList1 = new List<int>(n1);
            for (int i = 0; i < n1; i++) visList1.Add(order1[i]);
            var visList2 = new List<int>(n2);
            for (int i = 0; i < n2; i++) visList2.Add(order2[i]);

            bool identical = visList1.Count == visList2.Count;
            int firstDiffIndex = -1;
            if (identical)
            {
                for (int i = 0; i < visList1.Count; i++)
                {
                    if (visList1[i] != visList2[i]) { identical = false; firstDiffIndex = i; break; }
                }
            }

            var lines = new List<string>
            {
                $"path={path}",
                $"yaw_center={yawDeg.Value:0.000}deg eps={eps}deg margin=3deg",
                $"quadCount={geom.Quads.Length}",
                $"visibleCount_at_yaw-eps={n1}",
                $"visibleCount_at_yaw+eps={n2}",
                $"orders_identical={identical}",
                $"first_diff_index={firstDiffIndex}",
                $"order_at_yaw-eps=[{string.Join(",", visList1)}]",
                $"order_at_yaw+eps=[{string.Join(",", visList2)}]",
            };
            return Task.FromResult(ActionResult.Ok(lines));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("execution_error", ex.Message));
        }
    }
}
#endif

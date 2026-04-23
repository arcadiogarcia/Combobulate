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
}
#endif

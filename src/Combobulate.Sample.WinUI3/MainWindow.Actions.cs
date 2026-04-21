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

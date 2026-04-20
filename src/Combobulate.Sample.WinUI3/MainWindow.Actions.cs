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

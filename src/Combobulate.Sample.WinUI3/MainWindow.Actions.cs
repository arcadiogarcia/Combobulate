#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    };

    public IReadOnlyList<ActionDescriptor> GetAvailableActions() => _actions;

    public Task<ActionResult> DispatchAsync(string actionName, string parametersJson)
    {
        switch (actionName)
        {
            case "LoadObjFromPath": return DispatchLoadObjFromPathAsync(parametersJson);
            case "SetRotation": return DispatchSetRotationAsync(parametersJson);
            case "ResetRotation": return RunOnUi(() => { combobulate.RotationX = 0; combobulate.RotationY = 0; combobulate.RotationZ = 0; });
            case "ResetCube": return RunOnUi(LoadCube);
            case "SetZoom": return DispatchSetZoomAsync(parametersJson);
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

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) { return ActionResult.Fail("execution_error", $"Read failed: {ex.Message}"); }

        var result = ObjParser.Parse(text);
        if (result.Model.Quads.Count == 0)
            return ActionResult.Fail("execution_error", $"No quads parsed ({result.Errors.Count} errors).");

        return await RunOnUi(() =>
        {
            combobulate.Model = result.Model;
            var name = Path.GetFileName(path);
            StatusText.Text = result.Errors.Count == 0
                ? $"Loaded: {name} ({result.Model.Quads.Count} quads)"
                : $"Loaded: {name} ({result.Model.Quads.Count} quads, {result.Errors.Count} skipped)";
        });
    }

    private Task<ActionResult> DispatchSetRotationAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return Task.FromResult(ActionResult.Fail("validation_error", "params is not valid JSON.")); }

        return RunOnUi(() =>
        {
            if (p["x"] != null) combobulate.RotationX = p["x"]!.Value<double>();
            if (p["y"] != null) combobulate.RotationY = p["y"]!.Value<double>();
            if (p["z"] != null) combobulate.RotationZ = p["z"]!.Value<double>();
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

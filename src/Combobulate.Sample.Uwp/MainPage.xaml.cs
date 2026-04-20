using System;
using System.IO;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;

namespace Combobulate.Sample.Uwp;

public sealed partial class MainPage : Page
{
    /// <summary>Stable cache key for the built-in cube. Used by both code and XAML (Source="cube").</summary>
    private const string CubeKey = "cube";

    public MainPage()
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

    private async void LoadBook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // For UWP we ship samples as content under Assets\samples; load via ms-appx.
            var uri = new Uri("ms-appx:///Assets/samples/book.obj");
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            var text = await FileIO.ReadTextAsync(file);
            var geometry = ObjCache.GetOrAddText(file.Path, text);
            combobulate.Source = file.Path;
            StatusText.Text = $"Loaded book.obj ({geometry.Quads.Length} quads)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load book failed: {ex.Message}";
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

    private void ResetRotation_Click(object sender, RoutedEventArgs e)
    {
        PitchSlider.Value = 0;
        YawSlider.Value = 0;
        RollSlider.Value = 0;
    }

    private void Rotation_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => ApplyRotation();

    private void ExternalRotationToggle_Toggled(object sender, RoutedEventArgs e) => ApplyRotation();

    /// <summary>
    /// Routes the slider values either through the controls' rotation
    /// dependency properties (which trigger a paint-order rebuild) or via a
    /// composition <see cref="Visual.TransformMatrix"/> on each control's outer
    /// Visual (which does NOT — exposing back-face / paint-order artefacts as
    /// the model spins).
    /// </summary>
    private void ApplyRotation()
    {
        if (combobulate == null) return;
        var x = PitchSlider.Value;
        var y = YawSlider.Value;
        var z = RollSlider.Value;
        bool external = ExternalRotationToggle?.IsOn == true;

        if (external)
        {
            combobulate.RotationX = combobulate.RotationY = combobulate.RotationZ = 0;
            ApplyExternalRotation(combobulate, x, y, z);
            ApplyExternalRotation(combobulateSceneVisual, x, y, z);
        }
        else
        {
            ApplyExternalRotation(combobulate, 0, 0, 0);
            ApplyExternalRotation(combobulateSceneVisual, 0, 0, 0);
            combobulate.RotationX = x;
            combobulate.RotationY = y;
            combobulate.RotationZ = z;
        }
    }

    private static void ApplyExternalRotation(FrameworkElement element, double xDeg, double yDeg, double zDeg)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var w = (float)element.ActualWidth;
        var h = (float)element.ActualHeight;
        visual.CenterPoint = new Vector3(w / 2f, h / 2f, 0f);
        const float deg2rad = MathF.PI / 180f;
        visual.TransformMatrix = Matrix4x4.CreateFromYawPitchRoll(
            (float)yDeg * deg2rad,
            (float)xDeg * deg2rad,
            (float)zDeg * deg2rad);
    }

    private async void LoadObjButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".obj");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            // Path 2 — file-path cache. Reads + parses on first access; subsequent loads
            // of the same path return the cached geometry, transparently re-parsing only
            // if LastWriteTimeUtc or length has changed on disk.
            //
            // UWP picker hands us a StorageFile whose Path may not be readable directly
            // by File APIs (broker-mediated); fall back to FileIO + GetOrAddText keyed
            // on the path so the cache still amortizes repeat picks of the same file.
            ObjGeometry geometry;
            try
            {
                geometry = ObjCache.GetOrLoadFile(file.Path);
            }
            catch (UnauthorizedAccessException)
            {
                var text = await FileIO.ReadTextAsync(file);
                geometry = ObjCache.GetOrAddText(file.Path, text);
            }

            if (geometry.Model.Quads.Count == 0)
            {
                StatusText.Text = $"{file.Name}: no quads found";
                return;
            }

            combobulate.Model = geometry.Model;
            StatusText.Text = $"Loaded: {file.Name} ({geometry.Quads.Length} quads, cached)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load OBJ: {ex.Message}";
        }
    }

    // A unit cube centered on the origin, expressed as 6 quad faces.
    private const string CubeObj = @"
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
";
}

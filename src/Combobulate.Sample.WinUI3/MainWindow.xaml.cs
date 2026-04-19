using System;
using System.IO;
using Combobulate.Caching;
using Combobulate.Parsing;
using Microsoft.UI.Xaml;
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
        combobulate.RotationX = 0;
        combobulate.RotationY = 0;
        combobulate.RotationZ = 0;
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

            combobulate.Model = geometry.Model;
            StatusText.Text = $"Loaded: {file.Name} ({geometry.Quads.Length} quads, cached)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load OBJ: {ex.Message}";
        }
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

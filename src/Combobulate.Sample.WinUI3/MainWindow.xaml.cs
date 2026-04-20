using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Combobulate.Caching;
using Combobulate.Parsing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.UI;
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
            // Force the controls' own painting to identity so the only
            // rotation in effect is the external composition transform.
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
            // combobulateSceneVisual mirrors combobulate's rotation via x:Bind.
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

    private ObjTextureSource? _paintSource;
    private DispatcherTimer? _paintTimer;
    private int _paintTick;

    /// <summary>
    /// Demonstrates app-rendered textures via <see cref="ObjTextureSource.FromBitmap"/>:
    /// register a programmatic <see cref="ObjMaterialPack"/> against the "cube" key, then
    /// keep painting into the same source. Calls to <c>Update</c> repoint the cached
    /// composition surface — no <c>Rebuild</c>, no flicker.
    /// </summary>
    private void PaintFace_Click(object sender, RoutedEventArgs e)
    {
        if (_paintSource == null)
        {
            var bitmap = MakePaintBitmap(0);
            _paintSource = ObjTextureSource.FromBitmap(bitmap);

            ObjCache.RegisterMaterials(CubeKey, new ObjMaterialPackBuilder()
            {
                Fallback = new ObjMaterial
                {
                    Name = "paint",
                    DiffuseTexture = _paintSource,
                    DiffuseColor = Color.FromArgb(255, 200, 200, 200),
                },
            }.Build());

            // Re-source the control to pick up the freshly registered pack.
            combobulate.Source = CubeKey;

            _paintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _paintTimer.Tick += (_, _) =>
            {
                _paintTick++;
                _paintSource!.Update(MakePaintBitmap(_paintTick));
            };
            _paintTimer.Start();

            StatusText.Text = "Painting — same texture surface, live updates.";
        }
        else
        {
            _paintTimer?.Stop();
            _paintTimer = null;
            _paintSource = null;
            ObjCache.InvalidateMaterials(CubeKey);
            combobulate.Source = CubeKey;
            StatusText.Text = "Paint stopped.";
        }
    }

    private static SoftwareBitmap MakePaintBitmap(int tick)
    {
        const int size = 128;
        var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, size, size, BitmapAlphaMode.Premultiplied);
        var bytes = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                var phase = (x + y + tick * 3) * 0.05f;
                byte r = (byte)(127 + 127 * MathF.Sin(phase));
                byte g = (byte)(127 + 127 * MathF.Sin(phase + 2.094f));
                byte b = (byte)(127 + 127 * MathF.Sin(phase + 4.188f));
                bytes[i + 0] = b;
                bytes[i + 1] = g;
                bytes[i + 2] = r;
                bytes[i + 3] = 255;
            }
        }
        bmp.CopyFromBuffer(bytes.AsBuffer());
        return bmp;
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

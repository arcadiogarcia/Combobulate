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
    /// auto-refresh keeps cull/sort in sync). The per-frame sampler below writes the
    /// advanced yaw into the shared rotation <see cref="CompositionPropertySet"/>, so
    /// both renderers (<c>SpriteVisual</c> and <c>SceneVisual</c>) see identical live
    /// values each tick via their expression animation.
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
            _spinStart = DateTime.UtcNow;
            _spinBaseYaw = (float)(YawSlider?.Value ?? 0);
        }
        else
        {
            _spinStart = null;
        }
        UpdateAutoRefresh();
    }

    private DateTime? _spinStart;
    private float _spinBaseYaw;
    private const float SpinSecondsPerTurn = 6f;

    /// <summary>
    /// Auto-refresh only makes sense in external-rotation mode (in internal mode each
    /// rotation DP setter already triggers a Rebuild on the UI thread). When both
    /// toggles are on, subscribe Combobulate to <c>CompositionTarget.Rendering</c> via
    /// <c>EnableAutoRefresh</c> with a sampler that reads the live property set the
    /// expression animation already drives. The sampler also pokes the SceneVisual
    /// renderer's <c>RebuildForExternalRotation</c> so both side-by-side views stay
    /// in sync as the value updates from any thread.
    /// </summary>
    private void UpdateAutoRefresh()
    {
        if (combobulate == null) return;
        bool external = ExternalRotationToggle?.IsOn == true;
        bool wantAuto = external && AutoRefreshToggle?.IsOn == true;

        if (wantAuto)
        {
            var (props, _) = GetOrCreateExternalRotation();
            combobulate.EnableAutoRefresh(() =>
            {
                // If autonomous spin is engaged, advance the shared property set ourselves
                // from wall-clock time. Driving the value through the same props the
                // expression animation references means both renderers (and their cull/sort
                // samplers) see a single coherent yaw per frame — no composer-snapshot lag.
                if (_spinStart is DateTime t0)
                {
                    var secs = (float)(DateTime.UtcNow - t0).TotalSeconds;
                    var yaw = _spinBaseYaw + (secs / SpinSecondsPerTurn) * 360f;
                    // Wrap to keep the float well-conditioned over long runs.
                    yaw -= MathF.Floor(yaw / 360f) * 360f;
                    var pitch = (float)(PitchSlider?.Value ?? 0);
                    var roll = (float)(RollSlider?.Value ?? 0);
                    var live = new Vector3(pitch, yaw, roll);
                    props.InsertVector3("Rotation", live);
                    combobulateSceneVisual.RebuildForExternalRotation(live);
                    return live;
                }
                props.TryGetVector3("Rotation", out var r);
                // CombobulateSceneVisual doesn't have its own auto-refresh hook yet,
                // so piggy-back the same per-frame tick to keep its mesh in sync.
                combobulateSceneVisual.RebuildForExternalRotation(r);
                return r;
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

    private (CompositionPropertySet props, ExpressionAnimation expr) GetOrCreateExternalRotation()
    {
        if (_externalRotationProps != null && _externalRotationExpr != null)
            return (_externalRotationProps, _externalRotationExpr);
        var compositor = ElementCompositionPreview.GetElementVisual(this.Content).Compositor;
        _externalRotationProps = compositor.CreatePropertySet();
        _externalRotationProps.InsertVector3("Rotation", Vector3.Zero);
        _externalRotationExpr = compositor.CreateExpressionAnimation("p.Rotation");
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
            props.InsertVector3("Rotation", new Vector3(x, y, z));
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

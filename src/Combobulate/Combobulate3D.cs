using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Combobulate.Caching;
using Combobulate.Parsing;
using Windows.Foundation;

#if WINAPPSDK
using Windows.UI;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Scenes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
#else
using Windows.UI;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.UI.Composition.Scenes;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
#endif

namespace Combobulate;

/// <summary>
/// Alternate renderer that draws an <see cref="ObjModel"/> using composition
/// <c>SceneVisual</c> meshes instead of per-face <c>SpriteVisual</c> quads.
///
/// <para>
/// Each quad becomes its own <c>SceneMesh</c> (4 vertices, 2 triangles) under a single
/// model <c>SceneNode</c>, with a per-quad <c>SceneMetallicRoughnessMaterial</c> carrying
/// the quad's color via <c>BaseColorFactor</c>. The Scene rasterizer's depth buffer
/// resolves visibility, so no painter's-algorithm sort is needed.
/// </para>
///
/// <para>
/// Rotation is applied via the model node's <see cref="SceneModelTransform.Orientation"/>
/// (a quaternion), which lives on the composition thread. Updating
/// <see cref="RotationX"/>/<see cref="RotationY"/>/<see cref="RotationZ"/> only writes
/// the new quaternion — it does NOT trigger a mesh rebuild — so rotation animations
/// stay fluid and avoid the per-frame UI-thread cost of <see cref="Combobulate"/>.
/// </para>
///
/// <para>
/// <b>Known positioning limitation.</b> The SceneVisual rasterises with its
/// own scene-space origin at the top-left of its bounds, and the relationship
/// between scene-space units and host pixels is not 1:1 in this hosting
/// configuration — the model can render slightly offset from the host's centre.
/// This is fine for a side-by-side comparison with the SpriteVisual renderer,
/// but a production renderer would want to compose its own camera/projection.
/// </para>
///
/// <para>
/// Limitations vs <see cref="Combobulate"/>:
/// <list type="bullet">
///   <item>Projection is orthographic (the Scene API has no camera primitive in this
///         release). The <see cref="EnablePerspective"/> property is honored as a
///         no-op for API parity but does not foreshorten — toggling it has no effect.</item>
///   <item>Shading depends on the Scene runtime's default lighting; per-face colors come
///         from <c>BaseColorFactor</c>, not an explicit lit shader.</item>
/// </list>
/// </para>
/// </summary>
public sealed class Combobulate3D : Control
{
    private const string PartHost = "PART_Host";

    private FrameworkElement? _host;
    private Compositor? _compositor;
    private ContainerVisual? _root;
    private SpriteVisual? _surfaceSprite;
    private CompositionVisualSurface? _visualSurface;
    private SceneVisual? _sceneVisual;
    private SceneNode? _modelNode;
    private ObjGeometry? _geometry;

    public Combobulate3D()
    {
        this.DefaultStyleKey = typeof(Combobulate3D);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
    }

    #region Dependency Properties

    /// <summary>The OBJ model to render. May be null.</summary>
    public ObjModel? Model
    {
        get => (ObjModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(
            nameof(Model),
            typeof(ObjModel),
            typeof(Combobulate3D),
            new PropertyMetadata(null, (d, _) => ((Combobulate3D)d).OnModelChanged()));

    /// <summary>File path or registered <see cref="ObjCache"/> key. See <see cref="Combobulate.Source"/>.</summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(string),
            typeof(Combobulate3D),
            new PropertyMetadata(null, (d, e) => ((Combobulate3D)d).OnSourceChanged((string?)e.NewValue)));

    /// <summary>Multiplier applied to model-space positions.</summary>
    public double ModelScale
    {
        get => (double)GetValue(ModelScaleProperty);
        set => SetValue(ModelScaleProperty, value);
    }

    public static readonly DependencyProperty ModelScaleProperty =
        DependencyProperty.Register(
            nameof(ModelScale),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(100.0, (d, _) => ((Combobulate3D)d).RebuildMesh()));

    /// <summary>Honored as a no-op; the Scene rasterizer projects orthographically.</summary>
    public bool EnablePerspective
    {
        get => (bool)GetValue(EnablePerspectiveProperty);
        set => SetValue(EnablePerspectiveProperty, value);
    }

    public static readonly DependencyProperty EnablePerspectiveProperty =
        DependencyProperty.Register(
            nameof(EnablePerspective),
            typeof(bool),
            typeof(Combobulate3D),
            new PropertyMetadata(true));

    public double RotationX
    {
        get => (double)GetValue(RotationXProperty);
        set => SetValue(RotationXProperty, value);
    }

    public static readonly DependencyProperty RotationXProperty =
        DependencyProperty.Register(
            nameof(RotationX),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate3D)d).UpdateOrientation()));

    public double RotationY
    {
        get => (double)GetValue(RotationYProperty);
        set => SetValue(RotationYProperty, value);
    }

    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.Register(
            nameof(RotationY),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate3D)d).UpdateOrientation()));

    public double RotationZ
    {
        get => (double)GetValue(RotationZProperty);
        set => SetValue(RotationZProperty, value);
    }

    public static readonly DependencyProperty RotationZProperty =
        DependencyProperty.Register(
            nameof(RotationZ),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate3D)d).UpdateOrientation()));

    /// <summary>HDR multiplier applied to the per-face EmissiveFactor.</summary>
    public double EmissiveBoost
    {
        get => (double)GetValue(EmissiveBoostProperty);
        set => SetValue(EmissiveBoostProperty, value);
    }

    public static readonly DependencyProperty EmissiveBoostProperty =
        DependencyProperty.Register(
            nameof(EmissiveBoost),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate3D)d).RebuildMesh()));

    /// <summary>Scale applied to BaseColorFactor (0 = pure-emissive look).</summary>
    public double BaseColorScale
    {
        get => (double)GetValue(BaseColorScaleProperty);
        set => SetValue(BaseColorScaleProperty, value);
    }

    public static readonly DependencyProperty BaseColorScaleProperty =
        DependencyProperty.Register(
            nameof(BaseColorScale),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(1.0, (d, _) => ((Combobulate3D)d).RebuildMesh()));

    public double MetallicFactor
    {
        get => (double)GetValue(MetallicFactorProperty);
        set => SetValue(MetallicFactorProperty, value);
    }

    public static readonly DependencyProperty MetallicFactorProperty =
        DependencyProperty.Register(
            nameof(MetallicFactor),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate3D)d).RebuildMesh()));

    public double RoughnessFactor
    {
        get => (double)GetValue(RoughnessFactorProperty);
        set => SetValue(RoughnessFactorProperty, value);
    }

    public static readonly DependencyProperty RoughnessFactorProperty =
        DependencyProperty.Register(
            nameof(RoughnessFactor),
            typeof(double),
            typeof(Combobulate3D),
            new PropertyMetadata(1.0, (d, _) => ((Combobulate3D)d).RebuildMesh()));

    /// <summary>If true, negate Z when projecting (changes which faces are front).</summary>
    public bool FlipZ
    {
        get => (bool)GetValue(FlipZProperty);
        set => SetValue(FlipZProperty, value);
    }

    public static readonly DependencyProperty FlipZProperty =
        DependencyProperty.Register(
            nameof(FlipZ),
            typeof(bool),
            typeof(Combobulate3D),
            new PropertyMetadata(false, (d, _) => ((Combobulate3D)d).RebuildMesh()));

    #endregion

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild(PartHost) as FrameworkElement;
        TryAttachVisuals();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => TryAttachVisuals();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host != null)
            ElementCompositionPreview.SetElementChildVisual(_host, null);
        DisposeSceneTree();
        _root?.Dispose();
        _root = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRootLayout();
    }

    private void OnLayoutUpdated(object? sender, object e)
    {
        UpdateRootLayout();
    }

    private void TryAttachVisuals()
    {
        if (_host == null || _sceneVisual != null) return;

        try
        {
            _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            _sceneVisual = SceneVisual.Create(_compositor);
            // Give the SceneVisual an outsized initial size so vertices are
            // not clipped to a zero-sized rect before the host's layout pass
            // resolves its real dimensions.
            _sceneVisual.Size = new Vector2(4096f, 4096f);

            // SceneVisual does not appear to rasterise when attached directly via
            // ElementCompositionPreview.SetElementChildVisual on WinAppSDK 1.7 — only
            // when hosted inside a ContentIsland (as in the WinUI Gallery helmet
            // sample). Capture it into a CompositionVisualSurface and paint that
            // surface onto a SpriteVisual that we DO host on the XAML element.
            _visualSurface = _compositor.CreateVisualSurface();
            _visualSurface.SourceVisual = _sceneVisual;

            var brush = _compositor.CreateSurfaceBrush(_visualSurface);
            _surfaceSprite = _compositor.CreateSpriteVisual();
            _surfaceSprite.Brush = brush;

            _root = _compositor.CreateContainerVisual();
            _root.Children.InsertAtTop(_surfaceSprite);
            ElementCompositionPreview.SetElementChildVisual(_host, _root);
        }
        catch (Exception ex)
        {
            // Scenes API may be unavailable in some hosting contexts. Surface the
            // failure as the control's content rather than crashing the app.
            _root?.Dispose();
            _root = null;
            _sceneVisual = null;
            _compositor = null;
            ShowOverlay("SceneVisual unavailable: " + ex.Message);
            return;
        }

        UpdateRootLayout();
        RebuildMesh();
    }

    private void UpdateRootLayout()
    {
        if (_sceneVisual == null || _host == null) return;
        var w = (float)_host.ActualWidth;
        var h = (float)_host.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var size = new Vector2(w, h);
        bool sizeChanged = _surfaceSprite != null && _surfaceSprite.Size != size;
        _sceneVisual.Size = size;
        if (_visualSurface != null) _visualSurface.SourceSize = size;
        if (_surfaceSprite != null) _surfaceSprite.Size = size;
        if (_root != null) _root.Size = size;
        // Vertex centres are baked relative to the host size, so a size change
        // requires rebuilding the mesh.
        if (sizeChanged) RebuildMesh();
    }

    private void OnModelChanged()
    {
        _geometry = null;
        RebuildMesh();
    }

    private void OnSourceChanged(string? newValue)
    {
        if (string.IsNullOrWhiteSpace(newValue)) return;

        ObjGeometry geometry;
        try
        {
            geometry = ObjCache.Resolve(newValue!);
        }
        catch (Exception)
        {
            _geometry = null;
            Model = null;
            return;
        }

        _geometry = geometry;
        if (!ReferenceEquals(Model, geometry.Model))
        {
            Model = geometry.Model;
            _geometry = geometry;
        }
        else
        {
            RebuildMesh();
        }
    }

    private void DisposeSceneTree()
    {
        if (_modelNode != null)
        {
            // Children/Components collections own their items; clearing drops references.
            _modelNode.Children.Clear();
            _modelNode = null;
        }
        if (_sceneVisual != null)
        {
            _sceneVisual.Root = null;
        }
    }

    private void RebuildMesh()
    {
        if (_compositor == null || _sceneVisual == null) return;

        try
        {
            RebuildMeshCore();
        }
        catch (Exception ex)
        {
            ShowOverlay("Mesh build failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void ShowOverlay(string message)
    {
        if (_host is Border border)
        {
            border.Child = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8),
                Opacity = 0.7,
            };
        }
    }

    private void RebuildMeshCore()
    {        DisposeSceneTree();

        var model = Model;
        if (model == null) return;

        _geometry ??= ObjCache.ForModel(model);
        var quads = _geometry.Quads;
        if (quads.Length == 0) return;

        var scale = (float)ModelScale;

        // Bake the rotation and centering directly into the vertex positions.
        // Nested SceneNodes do not rasterise on this hosting path, and a
        // single-node Translation+Orientation pair is applied as
        // Translation-after-Orientation, which would rotate the cube around
        // the surface origin and fling it off-screen.
        //
        // The sibling sprite renderer rotates in pixel space (Y axis points
        // DOWN), but scene space puts Y UP, so naively reusing the same
        // CreateFromYawPitchRoll quaternion here makes pitch and roll spin
        // in the opposite direction. To match the sprite renderer exactly we
        // do the entire transform in pixel-down space first and only flip Y
        // once at the very end when handing the position to scene space.
        float w = _host != null ? (float)_host.ActualWidth : 0f;
        float h = _host != null ? (float)_host.ActualHeight : 0f;
        var center = new Vector3(w * 0.5f, h * 0.5f, 0f);

        const float deg2rad = MathF.PI / 180f;
        var rotation = Quaternion.CreateFromYawPitchRoll(
            (float)RotationY * deg2rad,
            (float)RotationX * deg2rad,
            (float)RotationZ * deg2rad);

        // Single SceneNode acting as the root.
        _modelNode = SceneNode.Create(_compositor);

        // Group quads by colour so we can build one mesh+material+renderer
        // per colour and host them on sibling child SceneNodes. The previous
        // attempt nested renderers under a parent that itself owned a
        // renderer; this version leaves the root pure-container so the
        // children's renderers are the only ones in the tree.
        var groups = new Dictionary<uint, List<CachedQuad>>();
        foreach (var q in quads)
        {
            var col = q.Color;
            uint key = (uint)col.R | ((uint)col.G << 8) | ((uint)col.B << 16) | ((uint)col.A << 24);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<CachedQuad>();
                groups[key] = list;
            }
            list.Add(q);
        }

        foreach (var kvp in groups)
        {
            var groupQuads = kvp.Value;
            var positions = new Vector3[groupQuads.Count * 4];
            var indices = new ushort[groupQuads.Count * 6];
            for (int i = 0; i < groupQuads.Count; i++)
            {
                var cq = groupQuads[i];
                int v = i * 4;
                positions[v + 0] = TransformVertex(cq.V0, scale, rotation, center, w, FlipZ);
                positions[v + 1] = TransformVertex(cq.V1, scale, rotation, center, w, FlipZ);
                positions[v + 2] = TransformVertex(cq.V2, scale, rotation, center, w, FlipZ);
                positions[v + 3] = TransformVertex(cq.V3, scale, rotation, center, w, FlipZ);

                int idx = i * 6;
                indices[idx + 0] = (ushort)(v + 0);
                indices[idx + 1] = (ushort)(v + 1);
                indices[idx + 2] = (ushort)(v + 2);
                indices[idx + 3] = (ushort)(v + 0);
                indices[idx + 4] = (ushort)(v + 2);
                indices[idx + 5] = (ushort)(v + 3);
            }

            var mesh = SceneMesh.Create(_compositor);
            mesh.PrimitiveTopology = DirectXPrimitiveTopology.TriangleList;
            FillAttribute(mesh, SceneAttributeSemantic.Vertex, DirectXPixelFormat.R32G32B32Float, positions);
            FillAttribute(mesh, SceneAttributeSemantic.Index, DirectXPixelFormat.R16UInt, indices);

            var faceColor = ColorToVector4(groupQuads[0].Color);
            var material = SceneMetallicRoughnessMaterial.Create(_compositor);
            // SceneMetallicRoughnessMaterial always runs through the IBL,
            // which washes BaseColorFactor down to a pastel and clips
            // EmissiveFactor > 1.0 to white. The compromise that reads
            // closest to the sprite renderer's flat colours is to leave
            // BaseColorFactor at full strength (so the IBL specular tints
            // toward the face colour), neutralise the metal/rough lighting
            // so the diffuse term dominates, and add a moderate emissive
            // boost in the same hue to shift the blend back toward the
            // intended colour without clipping.
            float baseScale = (float)BaseColorScale;
            material.BaseColorFactor = new Vector4(faceColor.X * baseScale, faceColor.Y * baseScale, faceColor.Z * baseScale, 1f);
            material.MetallicFactor = (float)MetallicFactor;
            material.RoughnessFactor = (float)RoughnessFactor;
            // Emissive is in linear-light HDR space and the pipeline tone-
            // maps the final colour back to sRGB. Setting Emissive to the
            // linearised face colour exactly reproduces the source sRGB
            // colour after tone mapping, which is the closest thing this
            // PBR-only API offers to an unlit material.
            material.EmissiveFactor = new Vector3(faceColor.X, faceColor.Y, faceColor.Z) * (float)EmissiveBoost;
            material.IsDoubleSided = true;

            var renderer = SceneMeshRendererComponent.Create(_compositor);
            renderer.Mesh = mesh;
            renderer.Material = material;

            var child = SceneNode.Create(_compositor);
            child.Components.Add(renderer);
            _modelNode.Children.Add(child);
        }

        _sceneVisual.Root = _modelNode;
    }

    private void UpdateOrientation()
    {
        // Rotation is baked into vertex positions, so changing it requires
        // rebuilding the mesh.
        RebuildMesh();
    }

    private static Vector3 TransformVertex(Vector3 v, float scale, Quaternion rotation, Vector3 center, float perspectiveDistance, bool flipZ)
    {
        // Compute the position the way the sprite renderer does: in pixel
        // space with Y pointing DOWN, with the same `M34 = -1/d, d = w`
        // perspective projection that Combobulate.UpdateRootTransform
        // applies. Doing the rotation+perspective+translation here
        // guarantees the projected silhouette matches the sibling renderer
        // exactly. Y is negated at the very end to convert into scene space
        // (Y UP).
        var scaled = new Vector3(v.X * scale, v.Y * scale, v.Z * scale);
        var rotated = Vector3.Transform(scaled, rotation);
        // Manual perspective divide. The sprite renderer's matrix has
        // M34 = -1/d which yields w' = 1 - z/d after multiplying with the
        // homogeneous (x, y, z, 1) vector centred at the screen origin;
        // dividing x and y by w' produces the foreshortening.
        float wPrime = 1f - rotated.Z / perspectiveDistance;
        if (wPrime < 0.001f) wPrime = 0.001f;
        var projected = new Vector3(rotated.X / wPrime, rotated.Y / wPrime, rotated.Z);
        var pixelDown = projected + center;
        return new Vector3(pixelDown.X, -pixelDown.Y, flipZ ? -pixelDown.Z : pixelDown.Z);
    }

    private static Vector4 ColorToVector4(Color c) =>
        new Vector4(SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B), c.A / 255f);

    private static float SrgbToLinear(byte channel)
    {
        // The PBR pipeline expects BaseColorFactor / EmissiveFactor in
        // linear-light space, but Color.FromArgb values are sRGB-encoded.
        // Skipping this conversion makes saturated sRGB inputs land in the
        // bright end of the tone-mapper's response curve, which is what
        // produces the washed-out pastel look on the right pane.
        float s = channel / 255f;
        return s <= 0.04045f
            ? s / 12.92f
            : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    private static unsafe void FillAttribute<T>(SceneMesh mesh, SceneAttributeSemantic semantic, DirectXPixelFormat format, T[] data)
        where T : unmanaged
    {
        int sizeBytes = data.Length * sizeof(T);
        var buffer = new MemoryBuffer((uint)sizeBytes);
        var reference = buffer.CreateReference();

        // Reach the underlying byte buffer through the legacy IMemoryBufferByteAccess
        // COM interface. Neither CsWinRT (WinAppSDK) nor classic UWP interop provide a
        // managed projection for it, so we acquire the raw IUnknown, QI for it, and
        // dispatch via the v-table directly.
        // Vtable layout: [0]=QueryInterface, [1]=AddRef, [2]=Release, [3]=GetBuffer.
        IntPtr unk = GetIUnknown(reference);
        try
        {
            Guid iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
            int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr accessPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var vtbl = *(IntPtr**)accessPtr;
                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)vtbl[3];
                byte* dest;
                uint capacity;
                int hr2 = getBuffer(accessPtr, &dest, &capacity);
                if (hr2 < 0) Marshal.ThrowExceptionForHR(hr2);
                fixed (T* src = data)
                {
                    Buffer.MemoryCopy(src, dest, sizeBytes, sizeBytes);
                }
            }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(unk); }

        mesh.FillMeshAttribute(semantic, format, buffer);
    }

    private static IntPtr GetIUnknown(object winrtObject)
    {
#if WINAPPSDK
        // CsWinRT-projected runtime classes are not classic RCWs, so
        // Marshal.GetIUnknownForObject returns an unrelated wrapper. Use the
        // CsWinRT marshaler to get a real IInspectable (which is also IUnknown).
        // FromManaged returns an AddRef'd pointer; the caller must Release.
        return WinRT.MarshalInspectable<object>.FromManaged(winrtObject);
#else
        return Marshal.GetIUnknownForObject(winrtObject);
#endif
    }
}

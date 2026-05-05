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
/// <b>Internal debugging / diagnostic control. Not part of the public
/// Combobulate API.</b> Kept in the assembly only so the sample app can
/// render the same model with a depth-buffered renderer side-by-side
/// with the production sprite-based <see cref="Combobulate"/> control,
/// to cross-check painter-sort ordering decisions while developing the
/// library. Consumers should not rely on this type; it has positioning
/// limitations (see below), no material/texture support, and may be
/// removed or significantly reshaped in any release.
///
/// <para>
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
public sealed class CombobulateSceneVisual : Control
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
    private string? _pendingSource;

    // Per color-group state preserved across rotation rebakes. The hot path
    // (RebuildForExternalRotation, called every frame during a spin) only
    // changes vertex positions; the index buffer, scene tree, materials and
    // native MemoryBuffer for positions are all reused. Set _geometryDirty
    // = true to force a full rebuild on the next call.
    private sealed class GroupCache
    {
        public CachedQuad[] Quads = Array.Empty<CachedQuad>();
        public Vector3[] PositionsScratch = Array.Empty<Vector3>();
        public SceneMesh? Mesh;
        public MemoryBuffer? PositionsBuffer;
        public object? PositionsRef; // IMemoryBufferReference, kept alive
        public IntPtr PositionsAccessPtr; // AddRef'd, released in Dispose
        public IntPtr PositionsDest; // raw write target inside the MemoryBuffer
        public int PositionsByteSize;
    }
    private List<GroupCache>? _groupCaches;
    private bool _geometryDirty = true;

    public CombobulateSceneVisual()
    {
        this.DefaultStyleKey = typeof(CombobulateSceneVisual);
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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(null, (d, _) => ((CombobulateSceneVisual)d).OnModelChanged()));

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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(null, (d, e) => ((CombobulateSceneVisual)d).OnSourceChanged((string?)e.NewValue)));

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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(100.0, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(true));

    /// <summary>
    /// Focal distance (in pixels) used by the per-vertex perspective
    /// divide in <c>TransformVertex</c>. Larger = weaker perspective
    /// (more orthographic), smaller = stronger perspective. Set to
    /// <c>0</c> (the default) to use the host's actual width, matching
    /// the historical behavior. Mirror of <c>Combobulate.PerspectiveDistance</c>
    /// so the two renderers stay visually identical when both are bound
    /// to the same value.
    /// </summary>
    public double PerspectiveDistance
    {
        get => (double)GetValue(PerspectiveDistanceProperty);
        set => SetValue(PerspectiveDistanceProperty, value);
    }

    public static readonly DependencyProperty PerspectiveDistanceProperty =
        DependencyProperty.Register(
            nameof(PerspectiveDistance),
            typeof(double),
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(0.0, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

    public double RotationX
    {
        get => (double)GetValue(RotationXProperty);
        set => SetValue(RotationXProperty, value);
    }

    public static readonly DependencyProperty RotationXProperty =
        DependencyProperty.Register(
            nameof(RotationX),
            typeof(double),
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(0.0, (d, _) => ((CombobulateSceneVisual)d).UpdateOrientation()));

    public double RotationY
    {
        get => (double)GetValue(RotationYProperty);
        set => SetValue(RotationYProperty, value);
    }

    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.Register(
            nameof(RotationY),
            typeof(double),
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(0.0, (d, _) => ((CombobulateSceneVisual)d).UpdateOrientation()));

    public double RotationZ
    {
        get => (double)GetValue(RotationZProperty);
        set => SetValue(RotationZProperty, value);
    }

    public static readonly DependencyProperty RotationZProperty =
        DependencyProperty.Register(
            nameof(RotationZ),
            typeof(double),
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(0.0, (d, _) => ((CombobulateSceneVisual)d).UpdateOrientation()));

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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(0.0, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(1.0, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

    public double MetallicFactor
    {
        get => (double)GetValue(MetallicFactorProperty);
        set => SetValue(MetallicFactorProperty, value);
    }

    public static readonly DependencyProperty MetallicFactorProperty =
        DependencyProperty.Register(
            nameof(MetallicFactor),
            typeof(double),
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(0.0, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

    public double RoughnessFactor
    {
        get => (double)GetValue(RoughnessFactorProperty);
        set => SetValue(RoughnessFactorProperty, value);
    }

    public static readonly DependencyProperty RoughnessFactorProperty =
        DependencyProperty.Register(
            nameof(RoughnessFactor),
            typeof(double),
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(1.0, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

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
            typeof(CombobulateSceneVisual),
            new PropertyMetadata(false, (d, _) => ((CombobulateSceneVisual)d).RebuildMesh()));

    #endregion

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild(PartHost) as FrameworkElement;
        TryAttachVisuals();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryAttachVisuals();
        if (_pendingSource != null)
        {
            var key = _pendingSource;
            _pendingSource = null;
            OnSourceChanged(key);
        }
    }

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
        TryStartExternalRotationAnimation();
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
        ApplyExternalRotationTransform();
    }

    private ExpressionAnimation? _externalRotationExpression;
    private ExpressionAnimation? _externalRotationAnimation;
    private CompositionPropertySet? _externalRotationBuffer;

    /// <summary>
    /// Drives the rotation of the captured SceneVisual surface off a caller-
    /// supplied <see cref="ExpressionAnimation"/> whose result is a
    /// <c>Vector3</c> of degrees \u2014 (X = pitch, Y = yaw, Z = roll). See
    /// <see cref="Combobulate.SetExternalRotation"/> for usage examples.
    ///
    /// <para>
    /// <b>Limitation.</b> Because the SceneVisual's mesh has rotation baked
    /// into its vertices, rotation under an external source is applied as a
    /// 2D affine transform on the already-rasterised surface (the captured
    /// snapshot of the mesh at whatever orientation the last internal
    /// rotation produced). For full 3D rotation, drive the
    /// <see cref="RotationX"/>/<see cref="RotationY"/>/<see cref="RotationZ"/>
    /// dependency properties instead, or use the sibling
    /// <c>Combobulate</c> sprite renderer whose external rotation applies a
    /// true 3D composition transform.
    /// </para>
    /// </summary>
    public void SetExternalRotation(ExpressionAnimation rotationDegrees)
    {
        if (rotationDegrees is null) throw new ArgumentNullException(nameof(rotationDegrees));
        // See Combobulate.SetExternalRotation: re-installing the same expression
        // every slider tick re-creates the composition animation chain and
        // produces a one-frame transform lag. Idempotent install fixes that.
        if (ReferenceEquals(_externalRotationExpression, rotationDegrees)) return;
        _externalRotationExpression = rotationDegrees;
        TryStartExternalRotationAnimation();
    }

    /// <summary>
    /// Detaches any previously-installed external rotation expression and
    /// stops the composition expression animation, returning the surface
    /// sprite to its identity transform.
    /// </summary>
    public void ClearExternalRotation()
    {
        if (_externalRotationExpression == null) return;
        _surfaceSprite?.StopAnimation("TransformMatrix");
        _externalRotationBuffer?.StopAnimation("R");
        _externalRotationAnimation?.Dispose();
        _externalRotationAnimation = null;
        _externalRotationExpression = null;
        if (_surfaceSprite != null) _surfaceSprite.TransformMatrix = Matrix4x4.Identity;
    }

    private void TryStartExternalRotationAnimation()
    {
        if (_surfaceSprite == null || _compositor == null) return;
        if (_externalRotationExpression == null) return;

        // See Combobulate.TryStartExternalRotationAnimation for the rationale
        // behind the internal property-set buffer: it lets the caller's
        // expression carry its own reference parameters via StartAnimation
        // instead of via the brittle SetExpressionReferenceParameter path.
        if (_externalRotationBuffer == null)
        {
            _externalRotationBuffer = _compositor.CreatePropertySet();
            _externalRotationBuffer.InsertVector3("R", Vector3.Zero);
        }
        _externalRotationBuffer.StopAnimation("R");
        _externalRotationBuffer.StartAnimation("R", _externalRotationExpression);

        const string D2R = "0.01745329251994";
        string toOrigin = "Matrix4x4.CreateTranslation(Vector3(-this.Target.Size.X / 2, -this.Target.Size.Y / 2, 0))";
        string fromOrigin = "Matrix4x4.CreateTranslation(Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0))";
        // Match CreateFromYawPitchRoll's effective order (RotZ * RotX * RotY
        // for row vectors) so the visible rotation aligns with what the
        // mesh-bake path produces internally.
        string rotation =
            $"Matrix4x4.CreateFromAxisAngle(Vector3(0,0,1), buf.R.Z * {D2R}) * " +
            $"Matrix4x4.CreateFromAxisAngle(Vector3(1,0,0), buf.R.X * {D2R}) * " +
            $"Matrix4x4.CreateFromAxisAngle(Vector3(0,1,0), buf.R.Y * {D2R})";

        string fullExpr = $"{toOrigin} * {rotation} * {fromOrigin}";

        _externalRotationAnimation?.Dispose();
        _externalRotationAnimation = _compositor.CreateExpressionAnimation(fullExpr);
        _externalRotationAnimation.SetReferenceParameter("buf", _externalRotationBuffer);
        _surfaceSprite.StartAnimation("TransformMatrix", _externalRotationAnimation);
    }

    private (double X, double Y, double Z) GetActiveRotation()
        => (RotationX, RotationY, RotationZ);

    private void ApplyExternalRotationTransform()
    {
        // Re-installing the expression on layout updates is unnecessary --
        // the expression references this.Target.Size which auto-updates.
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
            // See Combobulate.OnSourceChanged: defer retry until OnLoaded so XAML
            // Source="key" applied during InitializeComponent still works when the
            // app registers the keyed cache from code-behind right after.
            _geometry = null;
            _pendingSource = newValue;
            Model = null;
            return;
        }
        _pendingSource = null;

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
        if (_groupCaches != null)
        {
            foreach (var g in _groupCaches)
            {
                if (g.PositionsAccessPtr != IntPtr.Zero)
                {
                    Marshal.Release(g.PositionsAccessPtr);
                    g.PositionsAccessPtr = IntPtr.Zero;
                }
                (g.PositionsRef as IDisposable)?.Dispose();
                g.PositionsRef = null;
                g.PositionsBuffer?.Dispose();
                g.PositionsBuffer = null;
                g.Mesh = null;
                g.PositionsDest = IntPtr.Zero;
            }
            _groupCaches = null;
        }
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
        _geometryDirty = true;
    }

    /// <summary>
    /// Rebakes the mesh using the supplied rotation, in degrees. Intended
    /// for callers that drive <see cref="SetExternalRotation(ExpressionAnimation)"/>
    /// from the composition thread and need the static SceneVisual mesh to
    /// re-sync to the current animated value.
    ///
    /// <para>
    /// The control cannot read the animated value itself \u2014 it lives on
    /// the composition thread \u2014 so the caller must supply it. Must be
    /// called on the UI thread.
    /// </para>
    /// </summary>
    /// <param name="rotationDegrees">Current rotation as (X = pitch, Y = yaw, Z = roll), in degrees.</param>
    public void RebuildForExternalRotation(Vector3 rotationDegrees)
    {
        RebuildMesh(rotationDegrees);
        // The mesh now has the full rotation baked into its vertices. If
        // SetExternalRotation also installed a 2D affine TransformMatrix
        // animation against the rasterised surface, that animation would
        // double-apply on top of the baked rotation (visually: the right-
        // hand SceneVisual gets rotated/skewed twice). Stop the surface
        // animation and reset to identity so the baked mesh is the sole
        // source of truth. The expression buffer ("R") is left running
        // so callers can still drive the next rebake through the same
        // property set without re-installing.
        if (_surfaceSprite != null && _externalRotationExpression != null)
        {
            _surfaceSprite.StopAnimation("TransformMatrix");
            _surfaceSprite.TransformMatrix = Matrix4x4.Identity;
        }
    }

    private void RebuildMesh() => RebuildMesh(rotationOverride: null);

    private void RebuildMesh(Vector3? rotationOverride)
    {
        if (_compositor == null || _sceneVisual == null) return;

        try
        {
            // No-arg rebuilds are caused by structural property changes
            // (model, scale, perspective, materials, FlipZ) or layout
            // changes; force a full rebuild so the cache is rebuilt for
            // the new structure. Rotation-only rebakes (override supplied)
            // can reuse the cache as long as nothing else invalidated it.
            if (rotationOverride is null)
                _geometryDirty = true;

            RebuildMeshCore(rotationOverride);
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

    private void RebuildMeshCore(Vector3? rotationOverride)
    {
        var model = Model;
        if (model == null)
        {
            DisposeSceneTree();
            return;
        }

        _geometry ??= ObjCache.ForModel(model);
        var quads = _geometry.Quads;
        if (quads.Length == 0)
        {
            DisposeSceneTree();
            return;
        }

        // Compute the rotation+projection inputs that are baked into
        // every vertex.
        var scale = (float)ModelScale;
        float w = _host != null ? (float)_host.ActualWidth : 0f;
        float h = _host != null ? (float)_host.ActualHeight : 0f;
        var center = new Vector3(w * 0.5f, h * 0.5f, 0f);
        // Focal distance: explicit PerspectiveDistance if positive, otherwise host width
        // (the historical default that couples perspective to control size).
        float pd = (float)PerspectiveDistance;
        float persp = pd > 0f ? pd : (w > 0f ? w : 1f);

        const float deg2rad = MathF.PI / 180f;
        double rotX, rotY, rotZ;
        if (rotationOverride is { } ov)
        {
            rotX = ov.X; rotY = ov.Y; rotZ = ov.Z;
        }
        else
        {
            (rotX, rotY, rotZ) = GetActiveRotation();
        }
        var rotation = Quaternion.CreateFromYawPitchRoll(
            (float)rotY * deg2rad,
            (float)rotX * deg2rad,
            (float)rotZ * deg2rad);
        bool flipZ = FlipZ;

        // Fast path: caches are valid and only the rotation/projection
        // inputs may have changed. Refill the existing native MemoryBuffer
        // for each group's positions in-place and re-call FillMeshAttribute
        // so the SceneMesh re-uploads the new contents to the GPU. No new
        // SceneMesh / Material / Renderer / SceneNode / Dictionary / List /
        // Vector3[] / ushort[] / MemoryBuffer is allocated.
        if (!_geometryDirty && _groupCaches != null && _modelNode != null)
        {
            RebakePositions(scale, rotation, center, persp, flipZ);
            return;
        }

        // Slow path: full structural build. Group quads by colour, allocate
        // per-group SceneMesh + Material + Renderer + SceneNode and the
        // persistent native MemoryBuffer that subsequent fast-path rebakes
        // will rewrite into.
        DisposeSceneTree();
        _modelNode = SceneNode.Create(_compositor!);

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

        var caches = new List<GroupCache>(groups.Count);
        foreach (var kvp in groups)
        {
            var groupQuads = kvp.Value;
            var quadArray = groupQuads.ToArray();
            int vertCount = quadArray.Length * 4;
            int idxCount = quadArray.Length * 6;
            var positions = new Vector3[vertCount];
            var indices = new ushort[idxCount];
            for (int i = 0; i < quadArray.Length; i++)
            {
                var cq = quadArray[i];
                int v = i * 4;
                positions[v + 0] = TransformVertex(cq.V0, scale, rotation, center, persp, flipZ);
                positions[v + 1] = TransformVertex(cq.V1, scale, rotation, center, persp, flipZ);
                positions[v + 2] = TransformVertex(cq.V2, scale, rotation, center, persp, flipZ);
                positions[v + 3] = TransformVertex(cq.V3, scale, rotation, center, persp, flipZ);

                int idx = i * 6;
                indices[idx + 0] = (ushort)(v + 0);
                indices[idx + 1] = (ushort)(v + 1);
                indices[idx + 2] = (ushort)(v + 2);
                indices[idx + 3] = (ushort)(v + 0);
                indices[idx + 4] = (ushort)(v + 2);
                indices[idx + 5] = (ushort)(v + 3);
            }

            var mesh = SceneMesh.Create(_compositor!);
            mesh.PrimitiveTopology = DirectXPrimitiveTopology.TriangleList;

            // Allocate the persistent positions buffer + acquire the COM
            // byte-access pointer. The pointer is held for the lifetime of
            // the GroupCache so subsequent fast-path rebakes write directly
            // into the native memory without re-creating the buffer.
            var cache = new GroupCache
            {
                Quads = quadArray,
                PositionsScratch = positions,
                Mesh = mesh,
                PositionsByteSize = vertCount * sizeof(float) * 3,
            };
            CreatePersistentPositionsBuffer(cache);
            WritePositionsAndUpload(mesh, cache);

            // Indices never change after the structural build; fill once and
            // dispose the temporary index buffer immediately.
            FillAttribute(mesh, SceneAttributeSemantic.Index, DirectXPixelFormat.R16UInt, indices);

            var faceColor = ColorToVector4(quadArray[0].Color);
            var material = SceneMetallicRoughnessMaterial.Create(_compositor!);
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

            var renderer = SceneMeshRendererComponent.Create(_compositor!);
            renderer.Mesh = mesh;
            renderer.Material = material;

            var child = SceneNode.Create(_compositor!);
            child.Components.Add(renderer);
            _modelNode.Children.Add(child);

            caches.Add(cache);
        }

        _groupCaches = caches;
        _sceneVisual!.Root = _modelNode;
        _geometryDirty = false;
    }

    /// <summary>
    /// Fast-path rebake invoked when only the rotation/projection inputs
    /// have changed. Recomputes vertex positions for every cached group
    /// and re-uploads them into the persistent native MemoryBuffer that
    /// was allocated in the structural build.
    /// </summary>
    private void RebakePositions(float scale, Quaternion rotation, Vector3 center, float persp, bool flipZ)
    {
        var caches = _groupCaches!;
        for (int g = 0; g < caches.Count; g++)
        {
            var cache = caches[g];
            var quadArray = cache.Quads;
            var positions = cache.PositionsScratch;
            for (int i = 0; i < quadArray.Length; i++)
            {
                var cq = quadArray[i];
                int v = i * 4;
                positions[v + 0] = TransformVertex(cq.V0, scale, rotation, center, persp, flipZ);
                positions[v + 1] = TransformVertex(cq.V1, scale, rotation, center, persp, flipZ);
                positions[v + 2] = TransformVertex(cq.V2, scale, rotation, center, persp, flipZ);
                positions[v + 3] = TransformVertex(cq.V3, scale, rotation, center, persp, flipZ);
            }
            WritePositionsAndUpload(cache.Mesh!, cache);
        }
    }

    /// <summary>
    /// Allocates a single MemoryBuffer sized for this group's vertex
    /// positions and acquires the IMemoryBufferByteAccess byte pointer.
    /// The buffer + COM ref + dest pointer are stored on the cache and
    /// kept alive until <see cref="DisposeSceneTree"/> releases them.
    /// </summary>
    private static unsafe void CreatePersistentPositionsBuffer(GroupCache cache)
    {
        var buffer = new MemoryBuffer((uint)cache.PositionsByteSize);
        var reference = buffer.CreateReference();
        IntPtr unk = GetIUnknown(reference);
        IntPtr accessPtr;
        try
        {
            Guid iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
            int hr = Marshal.QueryInterface(unk, ref iid, out accessPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally { Marshal.Release(unk); }

        var vtbl = *(IntPtr**)accessPtr;
        var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)vtbl[3];
        byte* dest;
        uint capacity;
        int hr2 = getBuffer(accessPtr, &dest, &capacity);
        if (hr2 < 0)
        {
            Marshal.Release(accessPtr);
            Marshal.ThrowExceptionForHR(hr2);
        }

        cache.PositionsBuffer = buffer;
        cache.PositionsRef = reference;
        cache.PositionsAccessPtr = accessPtr;
        cache.PositionsDest = (IntPtr)dest;
    }

    /// <summary>
    /// Copies the cached <see cref="GroupCache.PositionsScratch"/> array
    /// into the persistent native buffer and re-calls FillMeshAttribute so
    /// the SceneMesh re-uploads to the GPU.
    /// </summary>
    private static unsafe void WritePositionsAndUpload(SceneMesh mesh, GroupCache cache)
    {
        fixed (Vector3* src = cache.PositionsScratch)
        {
            Buffer.MemoryCopy(src, (void*)cache.PositionsDest, cache.PositionsByteSize, cache.PositionsByteSize);
        }
        mesh.FillMeshAttribute(SceneAttributeSemantic.Vertex, DirectXPixelFormat.R32G32B32Float, cache.PositionsBuffer);
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

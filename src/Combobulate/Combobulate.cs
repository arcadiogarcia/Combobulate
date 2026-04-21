using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;

#if WINAPPSDK
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
#else
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
#endif

namespace Combobulate;

/// <summary>
/// Renders an <see cref="ObjModel"/> as a collection of composition <c>SpriteVisual</c> quads.
///
/// <para>
/// Each <see cref="ObjQuad"/> becomes a 1×1 <c>SpriteVisual</c> whose
/// <see cref="Visual.TransformMatrix"/> maps the unit rectangle into 3D space using
/// V0 as the origin, (V1 − V0) as the X basis, and (V3 − V0) as the Y basis.
/// </para>
///
/// <para>
/// This is exact for parallelogram quads (the typical OBJ case for cube faces, gridded
/// surfaces, etc.). Non-parallelogram quads are approximated: V2 is treated as
/// V0 + (V1 − V0) + (V3 − V0). For arbitrary planar quads a projective transform would
/// be required.
/// </para>
/// </summary>
public sealed class Combobulate : Control
{
    private const string PartHost = "PART_Host";

    private FrameworkElement? _host;
    private Compositor? _compositor;
    private ContainerVisual? _root;

    public Combobulate()
    {
        this.DefaultStyleKey = typeof(Combobulate);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
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
            typeof(Combobulate),
            new PropertyMetadata(null, (d, _) => ((Combobulate)d).OnModelChanged()));

    /// <summary>
    /// File path or registered <see cref="ObjCache"/> key identifying the OBJ to render.
    ///
    /// <para>
    /// Resolution: a value matching a key registered via <see cref="ObjCache.Register(string, ObjModel)"/>
    /// or <see cref="ObjCache.GetOrAdd(string, System.Func{ObjModel})"/> wins; otherwise the value is
    /// treated as a file path. The first request parses and caches; subsequent requests
    /// (from this or any other control) reuse the cached <see cref="ObjGeometry"/>.
    /// </para>
    ///
    /// <para>
    /// Setting <see cref="Source"/> assigns <see cref="Model"/> to the cached model. Setting
    /// <see cref="Model"/> directly bypasses the keyed cache but still benefits from the
    /// per-instance geometry cache, so reusing one <c>ObjModel</c> reference across many
    /// controls only does the per-quad model-space prep once.
    /// </para>
    /// </summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(string),
            typeof(Combobulate),
            new PropertyMetadata(null, (d, e) => ((Combobulate)d).OnSourceChanged((string?)e.NewValue)));

    /// <summary>
    /// Multiplier applied to model-space positions when computing pixel-space transforms.
    /// Defaults to 100 — i.e. one OBJ unit = 100 device-independent pixels.
    /// </summary>
    public double ModelScale
    {
        get => (double)GetValue(ModelScaleProperty);
        set => SetValue(ModelScaleProperty, value);
    }

    public static readonly DependencyProperty ModelScaleProperty =
        DependencyProperty.Register(
            nameof(ModelScale),
            typeof(double),
            typeof(Combobulate),
            new PropertyMetadata(100.0, (d, _) => ((Combobulate)d).Rebuild()));

    /// <summary>
    /// When true (the default), a perspective transform is applied to the visual root so
    /// quads with non-zero Z appear foreshortened.
    /// </summary>
    public bool EnablePerspective
    {
        get => (bool)GetValue(EnablePerspectiveProperty);
        set => SetValue(EnablePerspectiveProperty, value);
    }

    public static readonly DependencyProperty EnablePerspectiveProperty =
        DependencyProperty.Register(
            nameof(EnablePerspective),
            typeof(bool),
            typeof(Combobulate),
            new PropertyMetadata(true, (d, _) => ((Combobulate)d).UpdateRootTransform()));

    /// <summary>Rotation around the X axis, in degrees.</summary>
    public double RotationX
    {
        get => (double)GetValue(RotationXProperty);
        set => SetValue(RotationXProperty, value);
    }

    public static readonly DependencyProperty RotationXProperty =
        DependencyProperty.Register(
            nameof(RotationX),
            typeof(double),
            typeof(Combobulate),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate)d).OnRotationChanged()));

    /// <summary>Rotation around the Y axis, in degrees.</summary>
    public double RotationY
    {
        get => (double)GetValue(RotationYProperty);
        set => SetValue(RotationYProperty, value);
    }

    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.Register(
            nameof(RotationY),
            typeof(double),
            typeof(Combobulate),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate)d).OnRotationChanged()));

    /// <summary>Rotation around the Z axis, in degrees.</summary>
    public double RotationZ
    {
        get => (double)GetValue(RotationZProperty);
        set => SetValue(RotationZProperty, value);
    }

    public static readonly DependencyProperty RotationZProperty =
        DependencyProperty.Register(
            nameof(RotationZ),
            typeof(double),
            typeof(Combobulate),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate)d).OnRotationChanged()));

    /// <summary>
    /// Optional explicit material pack. When non-null this wins over
    /// <see cref="ObjCache.TryGetMaterials(string)"/> and any auto-loaded <c>mtllib</c>.
    /// </summary>
    public ObjMaterialPack? Materials
    {
        get => (ObjMaterialPack?)GetValue(MaterialsProperty);
        set => SetValue(MaterialsProperty, value);
    }

    public static readonly DependencyProperty MaterialsProperty =
        DependencyProperty.Register(
            nameof(Materials),
            typeof(ObjMaterialPack),
            typeof(Combobulate),
            new PropertyMetadata(null, (d, _) => ((Combobulate)d).Rebuild()));

    /// <summary>Controls how materials are resolved. Defaults to <see cref="MaterialMode.Auto"/>.</summary>
    public MaterialMode MaterialMode
    {
        get => (MaterialMode)GetValue(MaterialModeProperty);
        set => SetValue(MaterialModeProperty, value);
    }

    public static readonly DependencyProperty MaterialModeProperty =
        DependencyProperty.Register(
            nameof(MaterialMode),
            typeof(MaterialMode),
            typeof(Combobulate),
            new PropertyMetadata(MaterialMode.Auto, (d, _) => ((Combobulate)d).Rebuild()));

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
        // If a Source set in XAML failed to resolve during construction (e.g. the
        // app registered the keyed cache entry from code-behind AFTER InitializeComponent
        // returned), retry now that we are loaded and any code-behind initialisation has
        // had a chance to run.
        if (_pendingSource != null)
        {
            var key = _pendingSource;
            _pendingSource = null;
            OnSourceChanged(key);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisableAutoRefresh();
        ClearSpritePool();
        if (_host != null)
            ElementCompositionPreview.SetElementChildVisual(_host, null);
        _root?.Dispose();
        _root = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRootTransform();
        Rebuild();
    }

    private void TryAttachVisuals()
    {
        if (_host == null || _root != null) return;

        _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _root = _compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(_host, _root);

        UpdateRootTransform();
        Rebuild();
    }

    private ExpressionAnimation? _externalRotationExpression;
    private ExpressionAnimation? _externalRotationAnimation;
    private CompositionPropertySet? _externalRotationBuffer;

    /// <summary>
    /// Drives the 3D rotation of the rendered model directly off a caller-
    /// supplied <see cref="ExpressionAnimation"/> whose result is a
    /// <c>Vector3</c> of degrees \u2014 (X = pitch, Y = yaw, Z = roll).
    ///
    /// <para>
    /// The supplied expression is referenced as <c>src</c> inside this
    /// control's own matrix expression, which is bound to the composition
    /// root's <c>TransformMatrix</c>. Subsequent updates to anything the
    /// caller's expression references (property sets, other animatable
    /// composition objects, time, etc.) propagate automatically through the
    /// composition graph without ever touching the UI thread.
    /// </para>
    ///
    /// <para>
    /// Examples:
    /// <code>
    /// // Constant value updated off the UI thread:
    /// var p = compositor.CreatePropertySet();
    /// p.InsertVector3("R", Vector3.Zero);
    /// var rot = compositor.CreateExpressionAnimation("p.R");
    /// rot.SetReferenceParameter("p", p);
    /// combobulate.SetExternalRotation(rot);
    /// // later, on any thread:
    /// p.InsertVector3("R", new Vector3(pitch, yaw, roll));
    ///
    /// // Time-driven, never touches the UI thread again:
    /// var spin = compositor.CreateExpressionAnimation(
    ///     "Vector3(0, this.Target.GetGlobalTime() * 60, 0)");
    /// combobulate.SetExternalRotation(spin);
    /// </code>
    /// </para>
    ///
    /// <para>
    /// While this mode is active the painter sort and back-face cull state of
    /// the existing per-quad <c>SpriteVisual</c> children remain frozen at
    /// whatever the last internal rotation produced \u2014 the whole point of
    /// driving rotation from outside is to expose what happens when the
    /// renderer does not get a chance to refresh paint order.
    /// </para>
    ///
    /// <para>Setting <see cref="RotationX"/>/<see cref="RotationY"/>/<see cref="RotationZ"/>
    /// (or calling <see cref="ClearExternalRotation"/>) returns the control
    /// to internally-driven rotation.</para>
    /// </summary>
    public void SetExternalRotation(ExpressionAnimation rotationDegrees)
    {
        if (rotationDegrees is null) throw new ArgumentNullException(nameof(rotationDegrees));
        _externalRotationExpression = rotationDegrees;
        TryStartExternalRotationAnimation();
    }

    /// <summary>
    /// Detaches any previously-installed external rotation expression, stops
    /// the composition expression animation bound to the root, and reverts
    /// to the value computed from <see cref="RotationX"/>/<see cref="RotationY"/>/<see cref="RotationZ"/>.
    /// Triggers a rebuild so paint order and back-face culling re-sync.
    /// </summary>
    public void ClearExternalRotation()
    {
        if (_externalRotationExpression == null) return;
        _root?.StopAnimation("TransformMatrix");
        _externalRotationBuffer?.StopAnimation("R");
        _externalRotationAnimation?.Dispose();
        _externalRotationAnimation = null;
        _externalRotationExpression = null;
        UpdateRootTransform();
        Rebuild();
    }

    private void TryStartExternalRotationAnimation()
    {
        if (_root == null || _compositor == null || _host == null) return;
        if (_externalRotationExpression == null) return;

        var w = (float)_host.ActualWidth;
        var h = (float)_host.ActualHeight;
        // Even with a degenerate size we still install the animation \u2014 the
        // expression references this.Target.Size, so it will re-evaluate
        // once the host gets a real layout pass and we update _root.Size.
        if (w > 0 && h > 0) _root.Size = new Vector2(w, h);

        // Run the caller's Vector3 expression against an internal property
        // set we own, then have OUR matrix expression reference that buffer.
        // This avoids the SetExpressionReferenceParameter pitfall where
        // the caller's reference parameters (e.g. "p" in "p.Rotation") are
        // not visible to the substituted-into outer expression. With
        // StartAnimation, the caller's parameters travel with the animation.
        if (_externalRotationBuffer == null)
        {
            _externalRotationBuffer = _compositor.CreatePropertySet();
            _externalRotationBuffer.InsertVector3("R", Vector3.Zero);
        }
        _externalRotationBuffer.StopAnimation("R");
        _externalRotationBuffer.StartAnimation("R", _externalRotationExpression);

        const string D2R = "0.01745329251994";
        // Use the same composition order as Matrix4x4.CreateFromYawPitchRoll,
        // which is what Rebuild/RebuildForExternalRotation use for back-face
        // cull and painter sort. CreateFromYawPitchRoll(yaw,pitch,roll)
        // produces a quaternion q = qY * qX * qZ; for row-vector
        // multiplication that maps to a matrix M = RotZ * RotX * RotY,
        // which means "roll first, then pitch, then yaw".
        string rotationExpr =
            $"Matrix4x4.CreateFromAxisAngle(Vector3(0,0,1), buf.R.Z * {D2R}) * " +
            $"Matrix4x4.CreateFromAxisAngle(Vector3(1,0,0), buf.R.X * {D2R}) * " +
            $"Matrix4x4.CreateFromAxisAngle(Vector3(0,1,0), buf.R.Y * {D2R})";

        string toOriginExpr = "Matrix4x4.CreateTranslation(Vector3(-this.Target.Size.X / 2, -this.Target.Size.Y / 2, 0))";
        string fromOriginExpr = "Matrix4x4.CreateTranslation(Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0))";

        string fullExpr;
        if (EnablePerspective)
        {
            fullExpr = $"{toOriginExpr} * {rotationExpr} * persp * {fromOriginExpr}";
        }
        else
        {
            fullExpr = $"{toOriginExpr} * {rotationExpr} * {fromOriginExpr}";
        }

        _externalRotationAnimation?.Dispose();
        _externalRotationAnimation = _compositor.CreateExpressionAnimation(fullExpr);
        _externalRotationAnimation.SetReferenceParameter("buf", _externalRotationBuffer);
        if (EnablePerspective)
        {
            // perspective uses host width as the focal distance, matching
            // UpdateRootTransform's convention. Width can change on resize;
            // the animation gets re-installed by UpdateRootTransform when
            // that happens, so capturing w here is fine.
            float d = w > 0 ? w : 1f;
            var perspective = new Matrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1f / d,
                0, 0, 0, 1);
            _externalRotationAnimation.SetMatrix4x4Parameter("persp", perspective);
        }

        _root.StartAnimation("TransformMatrix", _externalRotationAnimation);
    }

    private void UpdateRootTransform()
    {
        if (_root == null || _host == null) return;

        var w = (float)_host.ActualWidth;
        var h = (float)_host.ActualHeight;
        if (w <= 0 || h <= 0) return;

        _root.Size = new Vector2(w, h);

        // When an external rotation expression is installed, _root.TransformMatrix
        // is driven by an ExpressionAnimation off the composition thread.
        // Re-install it so the perspective constant (which depends on width)
        // and EnablePerspective branch stay in sync with the current size.
        if (_externalRotationExpression != null)
        {
            TryStartExternalRotationAnimation();
            return;
        }

        var rotation = GetRotationMatrix();

        var toOrigin = Matrix4x4.CreateTranslation(-w / 2f, -h / 2f, 0f);
        var fromOrigin = Matrix4x4.CreateTranslation(w / 2f, h / 2f, 0f);

        Matrix4x4 transform;
        if (EnablePerspective)
        {
            var d = w;
            var perspective = new Matrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1f / d,
                0, 0, 0, 1);
            transform = toOrigin * rotation * perspective * fromOrigin;
        }
        else
        {
            transform = toOrigin * rotation * fromOrigin;
        }

        _root.TransformMatrix = transform;
    }

    private Matrix4x4 GetRotationMatrix()
    {
        const float deg2rad = MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(
            (float)RotationY * deg2rad,
            (float)RotationX * deg2rad,
            (float)RotationZ * deg2rad);
    }

    private void OnRotationChanged()
    {
        // Setting an internal rotation DP returns the control to internal mode
        // so the new value actually takes effect.
        if (_externalRotationExpression != null) ClearExternalRotation();
        UpdateRootTransform();
        // Visibility (back-face culling) depends on rotation.
        Rebuild();
    }

    private void OnModelChanged()
    {
        // Drop any stale per-instance geometry reference; it will be re-fetched on Rebuild.
        _geometry = null;
        Rebuild();
    }

    private void OnSourceChanged(string? newValue)
    {
        if (string.IsNullOrWhiteSpace(newValue))
        {
            // Clearing Source leaves Model untouched on purpose so callers can mix
            // direct Model assignment with optional source-driven loading.
            _sourceKey = null;
            _sourceDirectory = null;
            return;
        }

        ObjGeometry geometry;
        try
        {
            geometry = ObjCache.Resolve(newValue!);
        }
        catch (Exception)
        {
            // Resolution can fail when XAML sets Source before code-behind has had a
            // chance to register the keyed cache entry (Source="key" attribute is
            // applied during InitializeComponent). Stash the value and retry from
            // OnLoaded once user code has run.
            _sourceKey = null;
            _sourceDirectory = null;
            _geometry = null;
            _pendingSource = newValue;
            Model = null;
            return;
        }
        _pendingSource = null;

        _sourceKey = newValue;
        try
        {
            _sourceDirectory = System.IO.File.Exists(newValue!)
                ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(newValue!))
                : null;
        }
        catch
        {
            _sourceDirectory = null;
        }

        // Pre-seed _geometry so the upcoming Rebuild — whether triggered by Model
        // changing here or by the explicit call below when Model is unchanged —
        // skips the per-instance cache lookup.
        _geometry = geometry;
        if (!ReferenceEquals(Model, geometry.Model))
        {
            Model = geometry.Model; // triggers OnModelChanged → Rebuild
            _geometry = geometry;   // OnModelChanged nulled it; restore
        }
        else
        {
            Rebuild();
        }
    }

    private ObjGeometry? _geometry;
    private string? _sourceKey;
    private string? _sourceDirectory;
    private string? _pendingSource;

    // --- Per-instance render-state cache, keyed off the active geometry. -----
    // Sprites are created once per quad and reused across rebuilds; rotation only
    // toggles IsVisible and (when needed) sibling order.
    private SpriteVisual?[]? _spritePool;
    private bool[]? _lastVisible;
    private int[] _lastOrder = Array.Empty<int>();
    private ObjGeometry? _spritePoolGeometry;
    private float _spritePoolScale;
    private float _spritePoolHostW;
    private float _spritePoolHostH;
    private object? _spritePoolPackKey;
    private ResolvedQuadMaterials? _spritePoolBindings;
    private int[]? _visibleScratch;
    private int[]? _slotScratch;

    // Coalescing for RequestRebuildForExternalRotation.
    private Vector3 _pendingExternalRotation;
    private int _externalRebuildScheduled; // 0 = idle, 1 = scheduled

    // Auto-refresh subscription state.
    private Func<Vector3>? _autoRefreshSampler;
    private EventHandler<object>? _renderingHandler;

    /// <summary>
    /// Refreshes the per-quad <c>SpriteVisual</c> children (back-face cull
    /// and painter-sort) for the supplied rotation, in degrees. Intended for
    /// callers that drive <see cref="SetExternalRotation(ExpressionAnimation)"/>
    /// from the composition thread and need the visible mesh to re-sync to
    /// the current animated value.
    ///
    /// <para>
    /// The control cannot read the animated value itself \u2014 it lives on
    /// the composition thread \u2014 so the caller must supply it. This method
    /// must be called on the UI thread; it does not affect the
    /// <c>TransformMatrix</c> animation that
    /// <see cref="SetExternalRotation(ExpressionAnimation)"/> installed.
    /// </para>
    /// </summary>
    /// <param name="rotationDegrees">Current rotation as (X = pitch, Y = yaw, Z = roll), in degrees.</param>
    public void RebuildForExternalRotation(Vector3 rotationDegrees)
    {
        const float deg2rad = MathF.PI / 180f;
        var rotation = Matrix4x4.CreateFromYawPitchRoll(
            rotationDegrees.Y * deg2rad,
            rotationDegrees.X * deg2rad,
            rotationDegrees.Z * deg2rad);
        Rebuild(rotation);
    }

    /// <summary>
    /// Thread-safe, coalescing variant of <see cref="RebuildForExternalRotation(Vector3)"/>.
    /// Records the latest rotation and schedules a single rebuild on the UI thread; if
    /// further calls arrive before that rebuild runs, only the most recent value is used.
    /// Useful when feeding rotation samples from a non-UI thread or at a higher rate than
    /// the UI can absorb.
    /// </summary>
    public void RequestRebuildForExternalRotation(Vector3 rotationDegrees)
    {
        _pendingExternalRotation = rotationDegrees;
        if (System.Threading.Interlocked.Exchange(ref _externalRebuildScheduled, 1) != 0) return;

#if WINAPPSDK
        var dispatcher = this.DispatcherQueue;
#else
        var dispatcher = this.Dispatcher;
#endif
        if (dispatcher == null)
        {
            // No dispatcher attached yet (control not loaded). Drop the schedule flag and
            // bail; the next Loaded → Rebuild() will pick up live state from the DPs.
            System.Threading.Interlocked.Exchange(ref _externalRebuildScheduled, 0);
            return;
        }

#if WINAPPSDK
        dispatcher.TryEnqueue(() =>
        {
            System.Threading.Interlocked.Exchange(ref _externalRebuildScheduled, 0);
            RebuildForExternalRotation(_pendingExternalRotation);
        });
#else
        var _ = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        {
            System.Threading.Interlocked.Exchange(ref _externalRebuildScheduled, 0);
            RebuildForExternalRotation(_pendingExternalRotation);
        });
#endif
    }

    /// <summary>
    /// Subscribes to <c>CompositionTarget.Rendering</c> and calls
    /// <see cref="RebuildForExternalRotation(Vector3)"/> on every frame, sampling the
    /// current rotation from <paramref name="rotationSampler"/>. The sampler runs on the
    /// UI thread; it should read the value the caller is feeding into the composition
    /// expression (typically the same <c>Vector3</c> last pushed into a
    /// <c>CompositionPropertySet</c>).
    ///
    /// <para>
    /// Together with <see cref="SetExternalRotation(ExpressionAnimation)"/> this provides
    /// the full "composition-thread rotation, periodic UI-thread cull/sort" loop without
    /// requiring callers to wire <c>Rendering</c> themselves. Steady-state cost per tick
    /// is one cull pass plus, on frames where the painter order actually changes, the
    /// sibling reorder — see <c>docs/rendering-pipeline.md</c>.
    /// </para>
    /// </summary>
    public void EnableAutoRefresh(Func<Vector3> rotationSampler)
    {
        if (rotationSampler is null) throw new ArgumentNullException(nameof(rotationSampler));
        DisableAutoRefresh();
        _autoRefreshSampler = rotationSampler;
        _renderingHandler = OnRenderingTick;
#if WINAPPSDK
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#else
        Windows.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#endif
    }

    /// <summary>Stops the auto-refresh loop installed by <see cref="EnableAutoRefresh"/>.</summary>
    public void DisableAutoRefresh()
    {
        if (_renderingHandler != null)
        {
#if WINAPPSDK
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _renderingHandler;
#else
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= _renderingHandler;
#endif
            _renderingHandler = null;
        }
        _autoRefreshSampler = null;
    }

    private void OnRenderingTick(object? sender, object e)
    {
        var sampler = _autoRefreshSampler;
        if (sampler == null) return;
        try
        {
            RebuildForExternalRotation(sampler());
        }
        catch
        {
            // Sampler errors must not tear down the per-frame callback; the next tick
            // re-tries. Silent by design — diagnostics belong in the caller's sampler.
        }
    }

    private void Rebuild() => Rebuild(GetRotationMatrix());

    private void Rebuild(Matrix4x4 rotation)
    {
        if (_compositor == null || _root == null) return;

        var model = Model;
        if (model == null || model.Quads.Count == 0)
        {
            ClearSpritePool();
            return;
        }

        // Resolve cached geometry. ObjCache.ForModel is a ConditionalWeakTable lookup —
        // O(1) amortised — so reusing the same ObjModel across many controls and across
        // every rotation tick costs nothing beyond the first build.
        var geometry = _geometry;
        if (geometry == null || !ReferenceEquals(geometry.Model, model))
        {
            geometry = ObjCache.ForModel(model);
            _geometry = geometry;
        }

        // Resolve material pack: explicit Materials DP > registered key > auto mtllib > none.
        ObjMaterialPack? pack = null;
        if (MaterialMode != MaterialMode.UseFallback)
        {
            pack = Materials
                ?? (_sourceKey != null ? ObjCache.TryGetMaterials(_sourceKey) : null);
            if (pack == null && model.MaterialLibraries.Count > 0)
            {
                try { pack = ObjCache.GetOrLoadMtlForModel(model, _sourceDirectory); }
                catch { pack = null; }
            }
            if (pack != null && MaterialMode == MaterialMode.UseDiffuse)
                pack = StripTextures(pack);
        }

        // (Re)materialise the per-instance sprite pool keyed off geometry + scale + host
        // size + material pack. Anything in here is rotation-invariant; the rotation
        // affects only IsVisible (cull) and sibling order (sort).
        var scale = (float)ModelScale;
        var hostW = (float)(_host?.ActualWidth ?? 0);
        var hostH = (float)(_host?.ActualHeight ?? 0);

        var packKey = (object?)pack ?? NoPackSentinel;
        bool geometryChanged = !ReferenceEquals(_spritePoolGeometry, geometry);
        bool transformChanged = geometryChanged
            || scale != _spritePoolScale
            || hostW != _spritePoolHostW
            || hostH != _spritePoolHostH;
        bool packChanged = geometryChanged || !ReferenceEquals(_spritePoolPackKey, packKey);

        if (geometryChanged)
        {
            ClearSpritePool();
            _spritePoolGeometry = geometry;
            _spritePool = new SpriteVisual?[geometry.Quads.Length];
            _lastVisible = new bool[geometry.Quads.Length];
            _lastOrder = Array.Empty<int>();
        }

        ResolvedQuadMaterials? resolved = packChanged
            ? MaterialResolver.Resolve(_compositor, geometry, pack)
            : _spritePoolBindings;
        _spritePoolBindings = resolved;
        _spritePoolPackKey = packKey;
        _spritePoolScale = scale;
        _spritePoolHostW = hostW;
        _spritePoolHostH = hostH;

        var origin = new Vector3(hostW / 2f, hostH / 2f, 0);
        var cachedQuads = geometry.Quads;
        var pool = _spritePool!;
        var lastVisible = _lastVisible!;
        if (_visibleScratch == null || _visibleScratch.Length < cachedQuads.Length)
            _visibleScratch = new int[cachedQuads.Length];
        if (_slotScratch == null || _slotScratch.Length < cachedQuads.Length)
            _slotScratch = new int[cachedQuads.Length];

        // Lazy-create / refresh sprites for each cached quad.
        for (int i = 0; i < cachedQuads.Length; i++)
        {
            var sprite = pool[i];
            bool isNew = sprite == null;
            if (isNew)
            {
                sprite = _compositor.CreateSpriteVisual();
                sprite.Size = new Vector2(1f, 1f);
                sprite.IsVisible = false;
                pool[i] = sprite;
                // New sprites need transform + brush regardless of cached flags.
                _root.Children.InsertAtTop(sprite);
            }

            if (isNew || transformChanged)
            {
                var cq = cachedQuads[i];
                var v0 = cq.V0 * scale + origin;
                var v1 = cq.V1 * scale + origin;
                var v3 = cq.V3 * scale + origin;
                var xAxis = v1 - v0;
                var yAxis = v3 - v0;
                var zAxis = Vector3.Normalize(Vector3.Cross(xAxis, yAxis));
                sprite!.TransformMatrix = new Matrix4x4(
                    xAxis.X, xAxis.Y, xAxis.Z, 0,
                    yAxis.X, yAxis.Y, yAxis.Z, 0,
                    zAxis.X, zAxis.Y, zAxis.Z, 0,
                    v0.X,    v0.Y,    v0.Z,    1);
            }

            if ((isNew || packChanged) && resolved != null)
            {
                sprite!.Brush = resolved.Bindings[i].Brush;
            }
        }

        // Cull: update IsVisible only for quads whose state actually flipped.
        var visibleIndices = _visibleScratch!;
        int visCount = 0;
        for (int i = 0; i < cachedQuads.Length; i++)
        {
            var viewNormal = Vector3.TransformNormal(cachedQuads[i].Normal, rotation);
            bool visible = viewNormal.Z > 0;
            if (lastVisible[i] != visible)
            {
                pool[i]!.IsVisible = visible;
                lastVisible[i] = visible;
            }
            if (visible) visibleIndices[visCount++] = i;
        }

        // Painter's sort over the cached predecessor lists, restricted to visible quads.
        // Tiebreak by view-Z (back to front) so siblings without occlusion edges still get
        // a deterministic order that swaps cleanly as the camera rotates.
        var order = TopologicalPainterSort(visibleIndices, visCount, cachedQuads, geometry.Predecessors, rotation, _slotScratch!);

        // Reorder children only if the painter order actually changed. Sprites already
        // live under _root; VisualCollection.InsertAtTop throws E_INVALIDARG when the
        // child still has a parent (even if it's the same parent), so we must Remove
        // first. Walking visible quads back-to-front and re-attaching each one leaves
        // the last-painted (frontmost) on top.
        if (!OrderEquals(order, _lastOrder))
        {
            for (int i = 0; i < order.Length; i++)
            {
                var sprite = pool[order[i]]!;
                _root.Children.Remove(sprite);
                _root.Children.InsertAtTop(sprite);
            }
            _lastOrder = order;
        }
    }

    private static readonly object NoPackSentinel = new();

    private void ClearSpritePool()
    {
        if (_spritePool != null)
        {
            if (_root != null) _root.Children.RemoveAll();
            for (int i = 0; i < _spritePool.Length; i++)
            {
                _spritePool[i]?.Dispose();
                _spritePool[i] = null;
            }
        }
        _spritePool = null;
        _lastVisible = null;
        _lastOrder = Array.Empty<int>();
        _spritePoolGeometry = null;
        _spritePoolBindings = null;
        _spritePoolPackKey = null;
        _spritePoolScale = 0;
        _spritePoolHostW = 0;
        _spritePoolHostH = 0;
    }

    private static bool OrderEquals(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static ObjMaterialPack StripTextures(ObjMaterialPack pack)
    {
        var dict = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);
        foreach (var (name, mat) in pack.Materials)
        {
            dict[name] = new ObjMaterial
            {
                Name = mat.Name,
                DiffuseColor = mat.DiffuseColor,
                DiffuseTexture = null,
                UvScale = mat.UvScale,
                UvOffset = mat.UvOffset,
                ClampUv = mat.ClampUv,
            };
        }
        var fb = pack.Fallback;
        ObjMaterial? newFb = fb == null ? null : new ObjMaterial
        {
            Name = fb.Name,
            DiffuseColor = fb.DiffuseColor,
            DiffuseTexture = null,
            UvScale = fb.UvScale,
            UvOffset = fb.UvOffset,
            ClampUv = fb.ClampUv,
        };
        return new ObjMaterialPack(dict, newFb);
    }

    private readonly struct VisibleQuad
    {
        // Reserved for diagnostics; the live painter sort works directly off cached quad
        // indices to avoid per-frame allocations.
        public VisibleQuad(SpriteVisual sprite, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
            Vector3 centroid, Vector3 normal, float viewCentroidZ)
        {
            Sprite = sprite;
            V0 = v0; V1 = v1; V2 = v2; V3 = v3;
            Centroid = centroid;
            Normal = normal;
            ViewCentroidZ = viewCentroidZ;
        }

        public SpriteVisual Sprite { get; }
        public Vector3 V0 { get; }
        public Vector3 V1 { get; }
        public Vector3 V2 { get; }
        public Vector3 V3 { get; }
        public Vector3 Centroid { get; }
        public Vector3 Normal { get; }
        public float ViewCentroidZ { get; }
    }

    /// <summary>
    /// Returns the visible quads in painter's order (back to front) using the geometry's
    /// precomputed (rotation-invariant) topological partial order, with view-Z as the
    /// tiebreaker among nodes with no remaining predecessors.
    /// </summary>
    /// <param name="visible">Indices into <paramref name="cachedQuads"/> that survived culling.</param>
    /// <param name="cachedQuads">All cached quads for the current geometry.</param>
    /// <param name="predecessorsAll">For each cached quad index <c>b</c>, the quads that
    /// must paint before <c>b</c>; precomputed by <see cref="ObjGeometry.Predecessors"/>.</param>
    /// <param name="rotation">Current rotation matrix; used only for the view-Z tiebreaker.</param>
    private static int[] TopologicalPainterSort(
        int[] visible,
        int visibleCount,
        CachedQuad[] cachedQuads,
        int[][] predecessorsAll,
        Matrix4x4 rotation,
        int[] slotScratch)
    {
        int n = visibleCount;
        if (n == 0) return Array.Empty<int>();
        if (n == 1) return new[] { visible[0] };

        // Map cached-quad index → slot in the visible subset (or -1).
        // For small N this is the cheapest representation; for very large models a
        // dictionary would scale better, but the steady-state hot path here is small.
        int total = cachedQuads.Length;
        for (int i = 0; i < total; i++) slotScratch[i] = -1;
        for (int i = 0; i < n; i++) slotScratch[visible[i]] = i;

        var inDegree = new int[n];
        var viewZ = new float[n];
        for (int i = 0; i < n; i++)
        {
            var q = cachedQuads[visible[i]];
            viewZ[i] = Vector3.Transform(q.Centroid, rotation).Z;
            var preds = predecessorsAll[visible[i]];
            int deg = 0;
            for (int p = 0; p < preds.Length; p++)
            {
                if (slotScratch[preds[p]] >= 0) deg++;
            }
            inDegree[i] = deg;
        }

        var result = new int[n];
        var emitted = new bool[n];
        for (int step = 0; step < n; step++)
        {
            int pick = -1;
            float pickZ = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (emitted[i] || inDegree[i] != 0) continue;
                if (viewZ[i] < pickZ)
                {
                    pickZ = viewZ[i];
                    pick = i;
                }
            }

            if (pick < 0)
            {
                // Cycle (e.g. interpenetrating geometry). Fall back to view-Z ordering
                // for whatever remains.
                for (int i = 0; i < n; i++)
                {
                    if (!emitted[i] && viewZ[i] < pickZ)
                    {
                        pickZ = viewZ[i];
                        pick = i;
                    }
                }
                if (pick < 0) break;
            }

            emitted[pick] = true;
            result[step] = visible[pick];
            // Decrement in-degree of every visible successor of `pick`.
            // Successors of `pick` are quads `b` whose predecessor list contains `visible[pick]`.
            int picked = visible[pick];
            for (int j = 0; j < n; j++)
            {
                if (emitted[j]) continue;
                var preds = predecessorsAll[visible[j]];
                for (int k = 0; k < preds.Length; k++)
                {
                    if (preds[k] == picked) { inDegree[j]--; break; }
                }
            }
        }

        return result;
    }
}

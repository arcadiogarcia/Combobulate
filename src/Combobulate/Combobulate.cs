using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Rendering;
using CompositionExpressions;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;

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
#if !COMBOBULATE_NO_XAML
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
#endif
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
#if COMBOBULATE_NO_XAML
public sealed class Combobulate : DependencyObjectBase
#else
public sealed class Combobulate : Control
#endif
{
    private const string PartHost = "PART_Host";

#if !COMBOBULATE_NO_XAML
    private FrameworkElement? _host;
#endif
    private Compositor? _compositor;
    private ContainerVisual? _root;

#if COMBOBULATE_NO_XAML
    private float _hostWidthPx, _hostHeightPx, _rasterScale = 1f;
    private float HostWidth => _hostWidthPx;
    private float HostHeight => _hostHeightPx;
    private bool HasHost => true;
#else
    private float HostWidth => (float)(_host?.ActualWidth ?? 0);
    private float HostHeight => (float)(_host?.ActualHeight ?? 0);
    private bool HasHost => _host != null;
#endif

    /// <summary>
    /// The root <see cref="ContainerVisual"/> that hosts all rendered
    /// SpriteVisuals (the hand-off visual set via
    /// <c>ElementCompositionPreview.SetElementChildVisual</c>).
    /// <para>Apps that use <c>SceneLightingEffect</c> must add this visual
    /// to a <c>CompositionLight.Targets</c> directly — XAML
    /// <c>AddTargetElement</c> targets only the UIElement's backing visual
    /// which doesn't cascade to the hand-off visual tree.</para>
    /// </summary>
    public Visual? RootVisual => _root;

    public Combobulate()
    {
#if !COMBOBULATE_NO_XAML
        this.DefaultStyleKey = typeof(Combobulate);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
#endif
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

    /// <summary>
    /// Focal distance (in pixels) used by the perspective projection when
    /// <see cref="EnablePerspective"/> is true. The matrix uses
    /// <c>M34 = -1/d</c>, so larger values produce weaker perspective
    /// (more orthographic), smaller values produce stronger perspective
    /// (more visible side faces). Set to <c>0</c> (the default) to use
    /// the host's actual width, which is the historical behavior — this
    /// couples the perspective strength to the control size and to
    /// <see cref="ModelScale"/>. Setting an explicit positive value
    /// decouples them, so changing zoom no longer changes how much of a
    /// rotated face is visible.
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
            typeof(Combobulate),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate)d).UpdateRootTransform()));

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

    private MaterialSlotController? _materialSlots;

    /// <summary>
    /// Hot-path API for updating named material slots without replacing the
    /// entire <see cref="Materials"/> dependency property.
    /// </summary>
    public MaterialSlotController MaterialSlots => _materialSlots ??= new MaterialSlotController(this);

    public static readonly DependencyProperty MaterialsProperty =
        DependencyProperty.Register(
            nameof(Materials),
            typeof(ObjMaterialPack),
            typeof(Combobulate),
            new PropertyMetadata(null, (d, _) => ((Combobulate)d).OnMaterialsChanged()));

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

    /// <summary>
    /// Selects which back-to-front polygon sorting algorithm to use. Default is
    /// <see cref="global::Combobulate.Sorting.SortAlgorithm.Bsp"/>, which handles arbitrary
    /// mutual-straddle configurations correctly. <see cref="global::Combobulate.Sorting.SortAlgorithm.Newell"/>
    /// is also fully correct (uses runtime fragmentation only when needed).
    /// <see cref="global::Combobulate.Sorting.SortAlgorithm.Topological"/> is the original
    /// O(n²) Kahn-sort and may produce incorrect orderings when two polygons mutually
    /// straddle each other's planes (e.g. a flat cover meeting a thin page-edge strip).
    /// </summary>
    public global::Combobulate.Sorting.SortAlgorithm SortAlgorithm
    {
        get => (global::Combobulate.Sorting.SortAlgorithm)GetValue(SortAlgorithmProperty);
        set => SetValue(SortAlgorithmProperty, value);
    }

    public static readonly DependencyProperty SortAlgorithmProperty =
        DependencyProperty.Register(
            nameof(SortAlgorithm),
            typeof(global::Combobulate.Sorting.SortAlgorithm),
            typeof(Combobulate),
            new PropertyMetadata(
                global::Combobulate.Sorting.SortAlgorithm.Bsp,
                (d, _) => { var c = (Combobulate)d; c._sorter = null; c.Rebuild(); }));

    /// <summary>
    /// Widens the back-face cull boundary by this many degrees. Default 0
    /// (strict cull). Set to a small positive value (e.g. 6 = one frame at
    /// 60Hz with a 6-second-per-turn spin) when an external animation drives
    /// the rotation on the GPU and the CPU-supplied rotation may lag the
    /// GPU-drawn one by up to one frame. The widened cone keeps boundary
    /// faces visible through any single-frame CPU/GPU sync error so the
    /// painter sort never drops a face the GPU is actually drawing.
    ///
    /// <para>Cost: at any moment a few extra sprites near the back-face
    /// boundary stay parented and visible; the painter sort still draws
    /// them in the correct order, so the visual is identical to a strict
    /// cull plus a thin sliver of the same colour the next face over would
    /// have shown anyway.</para>
    /// </summary>
    public double CullMarginDegrees
    {
        get => (double)GetValue(CullMarginDegreesProperty);
        set => SetValue(CullMarginDegreesProperty, value);
    }

    public static readonly DependencyProperty CullMarginDegreesProperty =
        DependencyProperty.Register(
            nameof(CullMarginDegrees),
            typeof(double),
            typeof(Combobulate),
            new PropertyMetadata(0.0, (d, _) => ((Combobulate)d).Rebuild()));

    /// <summary>
    /// Whether to pre-split the source mesh into per-fragment painter
    /// units before sorting. Default <c>false</c>.
    ///
    /// <para><b>What it does.</b> Runs every loaded <see cref="ObjGeometry"/>
    /// through <see cref="global::Combobulate.Sorting.MeshDecomposer"/>,
    /// which splits faces against every other face's plane so the painter
    /// sorter operates on a fragment set that is mathematically guaranteed
    /// to admit a single correct back-to-front ordering for any view
    /// direction or finite-point camera position. See the
    /// <see cref="global::Combobulate.Sorting.MeshDecomposer"/> class-level
    /// remarks for the algorithm and the per-fragment invariant it
    /// provides.</para>
    ///
    /// <para><b>Why off by default — BakedAspectGraph incompatibility.</b>
    /// Subdivision dramatically grows quad count (e.g. book.obj 12 → 69
    /// when fully triangulated) which expands each
    /// <see cref="global::Combobulate.Rendering.PredicateCompiler"/>
    /// per-cell predicate to O(N + visible² ) signed terms AND'd together.
    /// The resulting <see cref="global::Microsoft.UI.Composition.ExpressionAnimation"/>
    /// string exceeds Composition's hard length cap and every cell's
    /// <c>StartAnimation("Opacity", predicate)</c> throws
    /// <c>ArgumentException: The expression string is too long.</c> —
    /// the visual stays blank. Two mitigations are already in place but
    /// may still be insufficient for the largest meshes:
    /// <list type="bullet">
    /// <item>The quad-preserving decomposer
    /// (<see cref="global::Combobulate.Sorting.MeshDecomposer.DecomposeForPainterOrder"/>)
    /// preserves quad topology where possible — book.obj subdivides to
    /// 33 quads, not 69 triangles.</item>
    /// <item><see cref="ObjGeometry.CoplanarGroups"/> identifies fragments
    /// that lie on a shared plane; the bake (<c>SignatureBake</c>) skips
    /// pair-sign tests between them since coplanar fragments never
    /// occlude each other. This trims the predicate by the count of
    /// same-plane pairs (often 10–20% of all pairs for typical meshes).
    /// </item>
    /// </list>
    /// Even small subdivided meshes (a cube with 12 quads, 2 cells) can
    /// still hit the cap. SpritePainter and DualTreeAtomicSwap render
    /// modes don't use baked predicates and are safe to combine with
    /// subdivision. Future work: split per-cell predicates across chained
    /// CompositionPropertySet scalars (pre-compute per-face <c>M·n_q</c>
    /// and per-pair <c>M·(c_j − c_i)</c> in a shared PropertySet so cell
    /// predicates become tiny scalar references). Tracked in plan.md.</para>
    ///
    /// <para><b>Rendering of fragment triangles.</b> Subdivision produces
    /// triangle <see cref="CachedQuad"/>s (V3 == V2). The renderer routes
    /// these through a per-sprite
    /// <see cref="global::Microsoft.UI.Composition.CompositionGeometricClip"/>
    /// referencing the shared unit-triangle path from
    /// <see cref="global::Combobulate.Rendering.TriangleClipFactory"/>, so
    /// the otherwise-rectangular sprite is masked to the triangle's
    /// (V0, V1, V2) area. Slivers whose
    /// <c>Cross(xAxis, yAxis)</c> underflows fall back to a unit Z basis
    /// inside the renderer rather than producing NaN transforms.</para>
    ///
    /// <para><b>Triggers.</b> A live change triggers <see cref="Rebuild"/>.
    /// The subdivided and non-subdivided geometry variants are cached
    /// separately by <see cref="ObjCache.ForModelSubdivided"/> and
    /// <see cref="ObjCache.ForModel"/>, so toggling this property back
    /// and forth is cheap after the first build.</para>
    /// </summary>
    public bool SubdivideForPainter
    {
        get => (bool)GetValue(SubdivideForPainterProperty);
        set => SetValue(SubdivideForPainterProperty, value);
    }

    public static readonly DependencyProperty SubdivideForPainterProperty =
        DependencyProperty.Register(
            nameof(SubdivideForPainter),
            typeof(bool),
            typeof(Combobulate),
            // Default OFF because BakedAspectGraph predicates explode past
            // Composition's ExpressionAnimation length cap when quad count
            // grows (see SubdivideForPainter XML doc for the empirical
            // measurement). Callers using SpritePainter or
            // DualTreeAtomicSwap may safely opt-in for painter-correct
            // non-convex rendering.
            new PropertyMetadata(false, (d, _) => ((Combobulate)d).OnSubdivideForPainterChanged()));

    private void OnSubdivideForPainterChanged()
    {
        // Switching variants is a topology change — the per-quad sprite
        // pool, sorter buffers, and bindings all become stale. A full
        // Rebuild is the safe path; ObjCache holds both variants so the
        // new lookup is a single weak-table hit.
        _geometry = null;
        Rebuild();
    }

    /// <summary>
    /// Selects the per-frame composition strategy. Default
    /// <see cref="global::Combobulate.Rendering.RenderingMode.SpritePainter"/>
    /// is the original single-tree path that runs cull and sort on the UI
    /// thread each frame; switch to
    /// <see cref="global::Combobulate.Rendering.RenderingMode.BakedAspectGraph"/>
    /// for compositor-driven animations that need zero per-frame CPU work.
    /// See <see cref="SetTransformAnimation"/> for the BakedAspectGraph
    /// setup.
    /// </summary>
    public global::Combobulate.Rendering.RenderingMode RenderingMode
    {
        get => (global::Combobulate.Rendering.RenderingMode)GetValue(RenderingModeProperty);
        set => SetValue(RenderingModeProperty, value);
    }

    public static readonly DependencyProperty RenderingModeProperty =
        DependencyProperty.Register(
            nameof(RenderingMode),
            typeof(global::Combobulate.Rendering.RenderingMode),
            typeof(Combobulate),
            new PropertyMetadata(
                global::Combobulate.Rendering.RenderingMode.SpritePainter,
                (d, _) => { var c = (Combobulate)d; c.OnRenderingModeChanged(); }));

    #endregion

#if !COMBOBULATE_NO_XAML
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild(PartHost) as FrameworkElement;
        TryAttachVisuals();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryAttachVisuals();

        // Re-layout when the rasterization scale changes (e.g. the window moves
        // to a monitor with a different display scaling), so the DIP→composition
        // mapping stays correct. Track the root we subscribe to so OnUnloaded can
        // detach even if XamlRoot has already been cleared by then (XamlRoot
        // outlives an unloaded control, so a dangling handler would leak this
        // instance across load/unload cycles).
        if (_subscribedXamlRoot != null && !ReferenceEquals(_subscribedXamlRoot, XamlRoot))
        {
            _subscribedXamlRoot.Changed -= OnXamlRootChanged;
            _subscribedXamlRoot = null;
        }
        if (XamlRoot != null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
            XamlRoot.Changed += OnXamlRootChanged;
            _subscribedXamlRoot = XamlRoot;
        }

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

    private double _lastRasterScale;
    private XamlRoot? _subscribedXamlRoot;
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _lastRasterScale)
        {
            _lastRasterScale = sender.RasterizationScale;
            UpdateRootTransform();
            Rebuild();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedXamlRoot != null)
        {
            _subscribedXamlRoot.Changed -= OnXamlRootChanged;
            _subscribedXamlRoot = null;
        }
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
#endif

#if COMBOBULATE_NO_XAML
    /// <summary>Bind to a system/lifted compositor. Creates the root container.
    /// After calling, the host must parent <see cref="RootVisual"/> into its target.</summary>
    public void BindHost(Compositor compositor)
    {
        _compositor = compositor;
        if (_root == null) _root = _compositor.CreateContainerVisual();
        UpdateRootTransform();
        Rebuild();
    }

    /// <summary>Provide the host surface size (device pixels) and rasterization scale.</summary>
    public void SetHostMetrics(float widthPx, float heightPx, float rasterScale)
    {
        _hostWidthPx = widthPx; _hostHeightPx = heightPx;
        _rasterScale = rasterScale > 0 ? rasterScale : 1f;
        UpdateRootTransform();
        Rebuild();
    }

    /// <summary>Drive one auto-refresh frame (cull/sort) — call from a host timer/clock.</summary>
    public void Tick(TimeSpan renderingTime)
    {
        var samplerT = _autoRefreshSamplerWithTime;
        var sampler = _autoRefreshSampler;
        var mSamplerT = _autoRefreshMatrixSamplerWithTime;
        var mSampler = _autoRefreshMatrixSampler;
        if (samplerT == null && sampler == null && mSamplerT == null && mSampler == null) return;
        try
        {
            if (mSamplerT != null) RebuildForExternalRotation(mSamplerT(renderingTime));
            else if (mSampler != null) RebuildForExternalRotation(mSampler());
            else
            {
                Vector3 rotation = samplerT != null ? samplerT(renderingTime) : sampler!();
                RebuildForExternalRotation(rotation);
            }
        }
        catch { }
    }

    /// <summary>Tear down composition resources.</summary>
    public void Unbind()
    {
        DisableAutoRefresh();
        ClearSpritePool();
        _root?.Dispose();
        _root = null;
    }
#endif

    private ExpressionAnimation? _externalRotationExpression;
    private ExpressionAnimation? _externalRotationAnimation;
    private CompositionPropertySet? _externalRotationBuffer;
    // When true, _externalRotationExpression evaluates to a Matrix4x4 rotation
    // supplied via SetExternalRotationMatrix (buffered into propertyset key "M");
    // when false it evaluates to a Vector3 of Euler degrees supplied via
    // SetExternalRotation (buffered into key "R"). Mutually exclusive.
    private bool _externalRotationIsMatrix;

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
        // Idempotent: re-installing the same expression every slider tick was
        // tearing down and rebuilding the composition animation chain on the
        // root, which causes a one-frame lag where _root.TransformMatrix is
        // still showing the previous matrix while the cull/sort run on the
        // freshly-pushed property-set value. The visible symptom is a quad
        // jump that looks "wrong" until the next slider tick recovers.
        if (ReferenceEquals(_externalRotationExpression, rotationDegrees) && !_externalRotationIsMatrix) return;
        _externalRotationExpression = rotationDegrees;
        _externalRotationIsMatrix = false;
        // BakedAspectGraph owns _root.TransformMatrix via the typed AST,
        // so don't install a competing ExpressionAnimation. Just record
        // the expression so future mode switches can restore it.
        if (IsBakedAspectGraphActive()) return;
        TryStartExternalRotationAnimation();
    }

    /// <summary>
    /// Rotation-only <b>matrix</b> variant of <see cref="SetExternalRotation(ExpressionAnimation)"/>.
    /// Installs a caller-supplied composition <see cref="ExpressionAnimation"/> that
    /// evaluates to a <c>Matrix4x4</c> <b>pure rotation</b>; the control keeps ownership
    /// of centering and perspective, wrapping the caller's matrix exactly as the Euler
    /// path does: <c>toOrigin * &lt;callerMatrix&gt; * [persp] * fromOrigin</c> on
    /// <c>_root.TransformMatrix</c>. Only the axis-angle synthesis is skipped, so for a
    /// matrix equal to the Euler path's <c>RotZ * RotX * RotY</c> the rendered result is
    /// identical.
    ///
    /// <para><b>Convention.</b> The matrix must be a standard <c>System.Numerics</c>
    /// row-vector rotation (as produced by <c>Matrix4x4.CreateFromAxisAngle</c>,
    /// <c>Matrix4x4.CreateFromQuaternion</c>, or <c>Matrix4x4.CreateFromYawPitchRoll</c>) —
    /// the same convention <see cref="Rebuild"/> and the back-face cull/painter sort use.
    /// To reproduce the Euler path exactly from angles, use
    /// <see cref="RotationMatrixFromEulerDegrees(Vector3)"/>. Supply a <b>translation-free,
    /// scale-free</b> rotation only; translation/perspective/centering are owned by the
    /// control and composing them into the caller matrix would double-apply them.</para>
    ///
    /// <para>Mutually exclusive with <see cref="SetExternalRotation(ExpressionAnimation)"/>
    /// and the <see cref="RotationX"/>/<see cref="RotationY"/>/<see cref="RotationZ"/> DPs
    /// (last writer wins); <see cref="ClearExternalRotation"/> reverts to the DP-driven
    /// rotation. To keep the CPU back-face cull/painter sort in sync while animating,
    /// pair with <see cref="RebuildForExternalRotation(Matrix4x4)"/> or
    /// <see cref="EnableAutoRefresh(System.Func{Matrix4x4})"/> feeding the same matrix.</para>
    /// </summary>
    /// <param name="rotationMatrix">Composition expression evaluating to a rotation-only <c>Matrix4x4</c>.</param>
    public void SetExternalRotationMatrix(ExpressionAnimation rotationMatrix)
    {
        if (rotationMatrix is null) throw new ArgumentNullException(nameof(rotationMatrix));
        if (ReferenceEquals(_externalRotationExpression, rotationMatrix) && _externalRotationIsMatrix) return;
        _externalRotationExpression = rotationMatrix;
        _externalRotationIsMatrix = true;
        if (IsBakedAspectGraphActive()) return;
        TryStartExternalRotationAnimation();
    }

    /// <summary>
    /// Builds the same rotation matrix the Euler external-rotation / DP path uses for
    /// the supplied (X = pitch, Y = yaw, Z = roll) angles <b>in degrees</b>, so callers of
    /// <see cref="SetExternalRotationMatrix(ExpressionAnimation)"/> can reproduce or seed
    /// the exact convention. Equivalent to
    /// <c>Matrix4x4.CreateFromYawPitchRoll(Y, X, Z)</c> (row-vector <c>RotZ*RotX*RotY</c>).
    /// </summary>
    public static Matrix4x4 RotationMatrixFromEulerDegrees(Vector3 pitchYawRollDegrees)
    {
        const float deg2rad = MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(
            pitchYawRollDegrees.Y * deg2rad,
            pitchYawRollDegrees.X * deg2rad,
            pitchYawRollDegrees.Z * deg2rad);
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
        _externalRotationBuffer?.StopAnimation("M");
        _externalRotationAnimation?.Dispose();
        _externalRotationAnimation = null;
        _externalRotationExpression = null;
        _externalRotationIsMatrix = false;
        UpdateRootTransform();
        Rebuild();
    }

    private void TryStartExternalRotationAnimation()
    {
        if (_root == null || _compositor == null || !HasHost) return;
        if (_externalRotationExpression == null) return;
        // BakedAspectGraph owns _root.TransformMatrix when SetTransformAnimation
        // has been wired up. Skip installing the legacy external-rotation
        // expression on top — it would fight with the baked transform animation
        // and cause unstable visuals ("freezes" while the compositor flips
        // between conflicting expressions).
        if (RenderingMode == global::Combobulate.Rendering.RenderingMode.BakedAspectGraph
            && _transformNode is not null)
        {
            return;
        }

        var w = (float)HostWidth;
        var h = (float)HostHeight;
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
            _externalRotationBuffer.InsertMatrix4x4("M", Matrix4x4.Identity);
        }

        const string D2R = "0.01745329251994";
        string rotationExpr;
        if (_externalRotationIsMatrix)
        {
            // Caller supplies the rotation matrix directly; buffer it and
            // reference it in place of the axis-angle synthesis. Everything
            // downstream (centering, perspective, install) is identical to the
            // Euler path, so an equivalent matrix renders pixel-identically.
            _externalRotationBuffer.StopAnimation("M");
            _externalRotationBuffer.StartAnimation("M", _externalRotationExpression);
            rotationExpr = "buf.M";
        }
        else
        {
            _externalRotationBuffer.StopAnimation("R");
            _externalRotationBuffer.StartAnimation("R", _externalRotationExpression);

            // Use the same composition order as Matrix4x4.CreateFromYawPitchRoll,
            // which is what Rebuild/RebuildForExternalRotation use for back-face
            // cull and painter sort. CreateFromYawPitchRoll(yaw,pitch,roll)
            // produces a quaternion q = qY * qX * qZ; for row-vector
            // multiplication that maps to a matrix M = RotZ * RotX * RotY,
            // which means "roll first, then pitch, then yaw".
            rotationExpr =
                $"Matrix4x4.CreateFromAxisAngle(Vector3(0,0,1), buf.R.Z * {D2R}) * " +
                $"Matrix4x4.CreateFromAxisAngle(Vector3(1,0,0), buf.R.X * {D2R}) * " +
                $"Matrix4x4.CreateFromAxisAngle(Vector3(0,1,0), buf.R.Y * {D2R})";
        }

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
            // Focal distance: use the user-specified PerspectiveDistance if positive,
            // otherwise fall back to the historical "use host width" behavior. The
            // animation gets re-installed by UpdateRootTransform on resize, so
            // capturing w (and the current PerspectiveDistance) here is fine.
            float pd = (float)PerspectiveDistance;
            float d = pd > 0f ? pd : (w > 0 ? w : 1f);
            var perspective = new Matrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1f / d,
                0, 0, 0, 1);
            _externalRotationAnimation.SetMatrix4x4Parameter("persp", perspective);
        }

        _root.StartAnimation("TransformMatrix", _externalRotationAnimation);
    }

    private float RasterScale()
    {
#if COMBOBULATE_NO_XAML
        var rs = _rasterScale;
#else
        var rs = (float)(XamlRoot?.RasterizationScale ?? 1.0);
#endif
        return rs > 0f ? rs : 1f;
    }

    private void UpdateRootTransform()
    {
        if (_root == null || !HasHost) return;
        // BakedAspectGraph owns _root.TransformMatrix while active. Forward
        // perspective / EnablePerspective changes by re-installing the
        // baked TransformMatrix animation (ApplyBakedTransformAnimation
        // detects the perspective delta via its idempotency guard) and
        // letting UpdateBakeIfNeeded rebake the painter ordering when the
        // sort camera distance changes. Without this forwarding the
        // PerspectiveDistance / EnablePerspective DPs would be silently
        // dropped in BAG mode.
        if (RenderingMode == global::Combobulate.Rendering.RenderingMode.BakedAspectGraph
            && _transformNode is not null)
        {
            ApplyBakedTransformAnimation();
            UpdateBakeIfNeeded();
            return;
        }

        // Composition child visuals attached via SetElementChildVisual are
        // authored in a space that the framework magnifies by the XamlRoot
        // rasterization scale (2.0 at 200% display scaling). The sprite
        // positions are built in the same DIP/rasterScale-divided space, so
        // divide the host dimensions here too to keep the rotation centre and
        // perspective focal length consistent. On a 100% display rs == 1 and
        // this is a no-op.
        // The composition child visual attached via SetElementChildVisual authors
        // its coordinates in the element's DIP space and the framework magnifies
        // them by the rasterization scale at render time. So the rotation centre,
        // _root.Size and perspective focal must be expressed in FULL DIP units
        // (ActualWidth/Height, NOT divided by rs) to line up with the element's
        // true centre — the same convention the external-rotation path uses. Only
        // the model SCALE is divided by rs (see Update()) to keep a stable pixel
        // size. Dividing the centre by rs here placed the die at 1/rs of the way
        // to the card centre (upper-left at 200%) and made every settle animation
        // orbit, because the sprite origin and rotation centre disagreed.
        var w = (float)HostWidth;
        var h = (float)HostHeight;
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
            float pd = (float)PerspectiveDistance;
            var d = pd > 0f ? pd : w;
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

    /// <summary>
    /// Convert <see cref="PerspectiveDistance"/> (in render-pixel units, matching the
    /// renderer's <c>w' = 1 - z/d</c> formula) into the model-space units that
    /// <see cref="Sorting.IFaceSorter.Sort"/> expects, so the perspective front-face
    /// test rays line up with the actual rendered projection. Mirrors the focal-distance
    /// fallback in <see cref="UpdateRootTransform"/>: the user value when positive,
    /// otherwise host width. Returns 0 ("use orthographic cull") when perspective is
    /// disabled or the scale is zero.
    /// </summary>
    private float ComputeSortCameraDistance(float scale)
    {
        if (!EnablePerspective || scale <= 0f) return 0f;
        float pd = (float)PerspectiveDistance;
        float w = (float)HostWidth;
        float d = pd > 0f ? pd : (w > 0f ? w : 1f);
        return d / scale;
    }

    /// <summary>Converts the user-facing <see cref="CullMarginDegrees"/> DP into
    /// the cosine-scale value the sorter expects (sin of the margin).</summary>
    private float ComputeCullMarginCos()
    {
        var marginDeg = CullMarginDegrees;
        if (marginDeg <= 0) return 0f;
        return MathF.Sin((float)(marginDeg * Math.PI / 180.0));
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

    private void OnMaterialsChanged()
    {
        _materialSlotsActive = false;
        _materialSlotMaterials.Clear();
        _materialSlotBindings = null;
        _materialSlotGeometry = null;
        _materialSlotToken = new object();
        Rebuild();
    }

    internal void ApplyMaterialSlotUpdates(IReadOnlyDictionary<string, ObjMaterial> updates)
    {
        if (updates == null) throw new ArgumentNullException(nameof(updates));
        if (updates.Count == 0) return;

        foreach (var pair in updates)
            _materialSlotMaterials[pair.Key] = pair.Value;
        _materialSlotsActive = true;
        _materialSlotToken = new object();

        if (_compositor == null || _root == null)
        {
            Rebuild();
            return;
        }

        var model = Model;
        if (model == null || model.IsEmpty)
        {
            Rebuild();
            return;
        }

        var geometry = ResolveGeometryForModel(model);
        _geometry = geometry;
        var pack = ResolveMaterialPack(model);
        var changedQuads = FindMaterialSlotQuads(geometry, updates.Keys);
        if (changedQuads.Length == 0)
            return;

        if (_materialSlotBindings == null || !ReferenceEquals(_materialSlotGeometry, geometry))
        {
            _materialSlotBindings = MaterialResolver.ResolveUnique(_compositor, geometry, BuildEffectiveMaterialPack(pack));
            _materialSlotGeometry = geometry;
        }
        else
        {
            MaterialResolver.UpdateMaterialSlots(_compositor, geometry, _materialSlotBindings, updates);
        }

        if (IsBakedAspectGraphActive()
            && _baked != null
            && !_baked.BakeInFlight
            && ReferenceEquals(_bakedGeometry, geometry))
        {
            bool ok = _baked.UpdateBindingsForQuads(_materialSlotBindings, changedQuads);
            if (ok)
            {
                _bakedMaterialsToken = _materialSlotToken;
                return;
            }
        }

        if (_spritePool != null && ReferenceEquals(_spritePoolGeometry, geometry))
        {
            foreach (var quadIndex in changedQuads)
            {
                if ((uint)quadIndex < (uint)_spritePool.Length && _spritePool[quadIndex] is { } sprite)
                {
                    var newBrush = _materialSlotBindings.Bindings[quadIndex].Brush;
                    sprite.Brush = newBrush;
                }
            }
            _spritePoolBindings = _materialSlotBindings;
            _spritePoolPackKey = _materialSlotToken;
            return;
        }

        Rebuild();
    }

    private ObjMaterialPack? ResolveMaterialPack(ObjModel model)
    {
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
        return pack;
    }

    /// <summary>
    /// Single source of truth for resolving an <see cref="ObjModel"/> to
    /// the <see cref="ObjGeometry"/> this control should render. Honours
    /// <see cref="SubdivideForPainter"/> by routing to the subdivided cache
    /// variant when enabled. Both variants are cached separately by
    /// <see cref="ObjCache"/> on the ObjModel weak table, so live toggles
    /// of the DP cost one cache hit after the first build.
    /// </summary>
    private ObjGeometry ResolveGeometryForModel(ObjModel model)
    {
        return SubdivideForPainter
            ? ObjCache.ForModelSubdivided(model)
            : ObjCache.ForModel(model);
    }

    private ResolvedQuadMaterials ResolveCurrentMaterials(ObjGeometry geometry, ObjMaterialPack? pack)
    {
        if (!_materialSlotsActive)
            return MaterialResolver.Resolve(_compositor!, geometry, pack);

        if (_materialSlotBindings == null || !ReferenceEquals(_materialSlotGeometry, geometry))
        {
            _materialSlotBindings = MaterialResolver.ResolveUnique(_compositor!, geometry, BuildEffectiveMaterialPack(pack));
            _materialSlotGeometry = geometry;
        }
        return _materialSlotBindings;
    }

    private object? CurrentMaterialToken(ObjMaterialPack? pack) => _materialSlotsActive ? _materialSlotToken : pack;

    private ObjMaterialPack BuildEffectiveMaterialPack(ObjMaterialPack? pack)
    {
        var materials = pack?.Materials is null
            ? new Dictionary<string, ObjMaterial>(StringComparer.Ordinal)
            : new Dictionary<string, ObjMaterial>(pack.Materials, StringComparer.Ordinal);
        foreach (var pair in _materialSlotMaterials)
            materials[pair.Key] = pair.Value;
        return new ObjMaterialPack(materials, pack?.Fallback);
    }

    private static int[] FindMaterialSlotQuads(ObjGeometry geometry, IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        var changed = new List<int>();
        var quads = geometry.Quads;
        for (int i = 0; i < quads.Length; i++)
        {
            var materialName = quads[i].MaterialName;
            if (materialName != null && set.Contains(materialName))
                changed.Add(i);
        }
        return changed.ToArray();
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
            // ObjCache.Resolve returns the base (non-subdivided) variant —
            // the keyed/file caches don't carry a subdivide flag. Run the
            // result through the same subdivide selector the Model-driven
            // path uses so XAML <c>Source="..."</c> behaves identically.
            var baseGeometry = ObjCache.Resolve(newValue!);
            geometry = SubdivideForPainter
                ? ObjCache.ForModelSubdivided(baseGeometry.Model)
                : baseGeometry;
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

    private readonly Dictionary<string, ObjMaterial> _materialSlotMaterials = new(StringComparer.Ordinal);
    private bool _materialSlotsActive;
    private ObjGeometry? _materialSlotGeometry;
    private ResolvedQuadMaterials? _materialSlotBindings;
    private object _materialSlotToken = new();

    // --- Per-instance render-state cache, keyed off the active geometry. -----
    // Sprites are created once per quad and reused across rebuilds; rotation only
    // toggles IsVisible and (when needed) sibling order.
    private SpriteVisual?[]? _spritePool;
    private bool[]? _lastVisible;
    private int[] _lastOrder = Array.Empty<int>();
    private int _lastOrderCount;
    private global::Combobulate.Sorting.IFaceSorter? _sorter;
    private bool[]? _visScratchBool;
    private int[]? _orderScratch;
    private ObjGeometry? _spritePoolGeometry;
    private float _spritePoolScale;
    private float _spritePoolHostW;
    // ---- ConvexLiveCull state ----
    // When ConvexLiveCull is active each sprite's Opacity is driven by a
    // compositor ExpressionAnimation off the rotation-matrix buffer. These
    // track the install so the expensive per-face StartAnimation is only
    // (re)issued when the pool, projection, or matrix buffer identity change.
    private bool _liveCullInstalled;
    private float _liveCullCamZ;
    private bool _liveCullPerspective;
    private CompositionPropertySet? _liveCullBuffer;
    // ---- BakedAspectGraph state ----
    private global::Combobulate.Rendering.BakedAspectGraphRenderer? _baked;
    private ObjGeometry? _bakedGeometry;
    private float _bakedScale, _bakedHostW, _bakedHostH;
    private global::Combobulate.Sorting.SortAlgorithm _bakedAlgorithm;
    /// <summary>
    /// Snapshot of <see cref="ComputeCullMarginCos"/> at the time of the last
    /// bake. Lets <see cref="UpdateBakeIfNeeded"/> trigger a fresh bake when
    /// the <see cref="CullMarginDegrees"/> DP changes — without this, the DP
    /// only widens the runtime predicate but the signature set still reflects
    /// the old, narrower cull, so the renderer would expect signatures that
    /// don't exist for newly-visible faces.
    /// </summary>
    private float _bakedCullMarginCosCache = -1f;
    /// <summary>
    /// Reference identity of the <see cref="ObjMaterialPack"/> the last bake's
    /// sprite trees were built against. When the consumer assigns a new
    /// <see cref="Materials"/> instance (e.g. cover image fades in after
    /// async download) this token differs from the live one and we trigger
    /// a fresh bake so the new brushes propagate to the materialised cells.
    /// </summary>
    private object? _bakedMaterialsToken;

    // ---- BakedAspectGraph: typed transform animation state ----
    // Set via SetTransformAnimation; consumed when RenderingMode == BakedAspectGraph.
    private Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node? _transformNode;
    private global::Combobulate.Rendering.TransformAnimationAxis[]? _transformAxes;
    // Snapshot of (transformNode, hostW, hostH) for which the GPU
    // TransformMatrix animation has been installed. Avoids reinstalling
    // the animation every Update tick (which leaked composition objects
    // and destabilised the visual tree until it crashed).
    private Matrix4x4Node? _bakedInstalledTransform;
    private float _bakedInstalledW;
    private float _bakedInstalledH;
    // Snapshot of (EnablePerspective, effective focal distance) for the
    // currently-installed BAG TransformMatrix animation. Tracked alongside
    // the host-size snapshot so that PerspectiveDistance / EnablePerspective
    // DP changes can re-install the perspective matrix in ApplyBakedTransformAnimation
    // (without these the cached _bakedInstalledTransform == _transformNode check
    // would silently keep the old projection).
    private bool _bakedInstalledEnablePerspective;
    private float _bakedInstalledFocalDistance;
    // Snapshot of the perspective-derived sort camera distance and the
    // EnablePerspective flag used by the last bake. Included in the
    // needRebake comparison so changing PerspectiveDistance / EnablePerspective
    // at runtime invalidates the painter ordering (the sort uses these to
    // pick the eye ray for the front-face cull and depth comparator).
    private float _bakedCameraDistanceCache = float.NaN;
    private bool _bakedEnablePerspectiveCache;
    // Secondary-input change detection: snapshots of the transform matrix
    // sampled at known primary-axis probe points captured during the last
    // bake. Compared against fresh probes (throttled) on each Update tick.
    private Matrix4x4[]? _secondaryProbeSnapshots;
    private long _lastSecondaryProbeTicks;
    private static readonly float[] s_secondaryProbeFractions = { 0.0f, 0.27f, 0.61f };

    private void CaptureSecondaryProbe()
    {
        if (_transformNode is null || _transformAxes is null || _transformAxes.Length == 0)
        {
            _secondaryProbeSnapshots = null;
            return;
        }
        _secondaryProbeSnapshots = SampleSecondaryProbe();
    }

    private bool SecondaryProbeChanged()
    {
        if (_secondaryProbeSnapshots is null) return false;
        var fresh = SampleSecondaryProbe();
        if (fresh.Length != _secondaryProbeSnapshots.Length) return true;
        for (int i = 0; i < fresh.Length; i++)
        {
            if (fresh[i] != _secondaryProbeSnapshots[i]) return true;
        }
        return false;
    }

    private Matrix4x4[] SampleSecondaryProbe()
    {
        // For each fraction f in [0,1), set every primary axis to its
        // (Min + f * Length) value and Evaluate. Captures how the
        // transform's "fixed" inputs (everything not in _transformAxes)
        // contribute by overriding the dynamic ones.
        var axes = _transformAxes!;
        var sweep = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            int idx = i;
            axes[i].Scalar.SetLiveValueProvider(() => sweep[idx]);
        }
        try
        {
            var probes = new Matrix4x4[s_secondaryProbeFractions.Length];
            for (int p = 0; p < probes.Length; p++)
            {
                float f = s_secondaryProbeFractions[p];
                for (int i = 0; i < axes.Length; i++)
                    sweep[i] = axes[i].Min + f * axes[i].Length;
                probes[p] = _transformNode!.Evaluate();
            }
            return probes;
        }
        finally
        {
            for (int i = 0; i < axes.Length; i++)
                axes[i].Scalar.SetLiveValueProvider(null);
        }
    }

    /// <summary>
    /// Configures Combobulate with a typed transform expression tree and
    /// the periodic scalar input that the analytical aspect-graph bake
    /// should sweep over. When <see cref="RenderingMode"/> is
    /// <see cref="global::Combobulate.Rendering.RenderingMode.BakedAspectGraph"/>,
    /// Combobulate will:
    ///
    /// <list type="bullet">
    ///   <item>Compile <paramref name="transformNode"/> via
    ///         <c>ToExpressionString()</c> and start that animation on
    ///         <c>_root.TransformMatrix</c>, so the GPU paints the same
    ///         transform the bake reasons about.</item>
    ///   <item>Bake every constant-painter-order cell across
    ///         <c>[0, primaryAxisPeriod)</c> by sweeping
    ///         <paramref name="primaryAxis"/> with its
    ///         <c>LiveValueProvider</c> overridden, calling
    ///         <c>transformNode.Evaluate()</c> at each sweep point.</item>
    ///   <item>Detect changes to any "secondary" input contributing to the
    ///         tree and re-bake automatically.</item>
    /// </list>
    ///
    /// <para>The contract is that <paramref name="transformNode"/> represents
    /// the FULL transform applied to the model — orientation, perspective,
    /// translation-to-host-center, all of it. Combobulate will replace its
    /// internal rotation pipeline with the supplied expression for the
    /// duration of the BakedAspectGraph mode.</para>
    /// </summary>
    public void SetTransformAnimation(
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node transformNode,
        global::Combobulate.Rendering.TransformAnimationAxis[] axes)
    {
        if (transformNode is null) throw new ArgumentNullException(nameof(transformNode));
        if (axes is null || axes.Length == 0) throw new ArgumentException("At least one axis required.", nameof(axes));

        _transformNode = transformNode;
        _transformAxes = axes;

        if (_baked != null)
        {
            _baked.Dispose();
            _baked = null;
            _bakedGeometry = null;
        }
        _bakedInstalledTransform = null;

        if (_root != null && RenderingMode == global::Combobulate.Rendering.RenderingMode.BakedAspectGraph)
        {
            // Mode-switch hygiene: any SpritePainter visuals
            // that were parented to _root before BakedAspectGraph took
            // over MUST be cleared here, otherwise they sit under the
            // bake's per-cell ContainerVisuals at Opacity=1 and the
            // compositor renders them as ghost geometry beneath the
            // (mostly-Opacity=0) bake. Found via DumpBakedAspectGraph
            // dump showing parent.Children.Count = _trees.Length + 6
            // (6 cube faces from the SpritePainter pool).
            _root.Children.RemoveAll();
            _spritePool = null;
            _spritePoolGeometry = null;
            _lastVisible = null;
            _lastOrder = Array.Empty<int>();
            ApplyBakedTransformAnimation();
        }
        // Trigger a rebuild so Update enters the BakedAspectGraph dispatch
        // branch (which now has _transformNode set) and kicks off the bake.
        Rebuild();
    }

    /// <summary>
    /// Convenience overload: auto-discover live scalar inputs by walking
    /// <paramref name="transformNode"/>'s AST and treating every property-
    /// reference scalar leaf as a full-circle (0..360°) periodic axis with
    /// 24 grid samples. Callers needing custom ranges, periodicity, or
    /// sample counts should use the explicit-axes overload.
    /// </summary>
    public void SetTransformAnimation(
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node transformNode)
    {
        if (transformNode is null) throw new ArgumentNullException(nameof(transformNode));
        var leaves = CompositionExpressions.LiveValueOverride
            .EnumerateAnimatableScalarLeaves(transformNode)
            .ToArray();
        if (leaves.Length == 0)
        {
            throw new ArgumentException(
                "transformNode contains no animatable scalar leaves; baked aspect graph cannot be built. " +
                "Reference at least one CompositionPropertySet scalar from the expression.",
                nameof(transformNode));
        }
        var axes = new global::Combobulate.Rendering.TransformAnimationAxis[leaves.Length];
        for (int i = 0; i < leaves.Length; i++)
        {
            axes[i] = global::Combobulate.Rendering.TransformAnimationAxis.FullCircleDeg(leaves[i]);
        }
        SetTransformAnimation(transformNode, axes);
    }

    /// <summary>
    /// Single-axis convenience overload of
    /// <see cref="SetTransformAnimation(Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node, Combobulate.Rendering.TransformAnimationAxis[])"/>.
    /// Equivalent to passing one axis with the given period and full periodic flag.
    /// </summary>
    public void SetTransformAnimation(
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node transformNode,
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode primaryAxis,
        float primaryAxisPeriod = 360f)
    {
        if (primaryAxis is null) throw new ArgumentNullException(nameof(primaryAxis));
        SetTransformAnimation(
            transformNode,
            new[] { new global::Combobulate.Rendering.TransformAnimationAxis(
                primaryAxis, min: 0f, length: primaryAxisPeriod, periodic: true) });
    }

    /// <summary>
    /// Force a fresh BakedAspectGraph bake on the next UI tick. No-op
    /// outside <see cref="Rendering.RenderingMode.BakedAspectGraph"/>
    /// mode or before <see cref="SetTransformAnimation(Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node, Combobulate.Rendering.TransformAnimationAxis[])"/>
    /// has been called. Useful for diagnostic UI ("Rebake" button) and
    /// for callers that mutate model geometry / axis sample density at
    /// runtime in ways the secondary-input probe wouldn't detect.
    /// </summary>
    public void ForceRebakeAspectGraph()
    {
        if (_baked != null)
        {
            _baked.Dispose();
            _baked = null;
            _bakedGeometry = null;
        }
        if (IsBakedAspectGraphActive())
            UpdateBakeIfNeeded();
    }

    private void ApplyBakedTransformAnimation()
    {
        if (_root is null || _transformNode is null || !HasHost) return;
        var w = (float)HostWidth;
        var h = (float)HostHeight;
        if (w <= 0 || h <= 0) return;

        // Resolve the effective focal distance the same way the legacy
        // SpritePainter path does: user PerspectiveDistance when > 0,
        // otherwise the host width (legacy default). When EnablePerspective
        // is false the focal distance is irrelevant — record 0 so the
        // idempotency guard treats every distance as equivalent.
        bool enablePersp = EnablePerspective;
        float focal = 0f;
        if (enablePersp)
        {
            float pd = (float)PerspectiveDistance;
            focal = pd > 0f ? pd : (w > 0 ? w : 1f);
        }

        // Idempotent guard: only (re)install when transform identity OR
        // host size OR perspective parameters changed. Reinstalling a
        // TransformMatrix animation per frame leaks composition objects
        // and crashes the app.
        if (ReferenceEquals(_bakedInstalledTransform, _transformNode)
            && _bakedInstalledW == w && _bakedInstalledH == h
            && _bakedInstalledEnablePerspective == enablePersp
            && _bakedInstalledFocalDistance == focal)
        {
            return;
        }

        _root.Size = new Vector2(w, h);
        _root.StopAnimation("TransformMatrix");

        // Compose toOrigin * userRotation [ * perspective ] * fromOrigin so the
        // user-supplied transformNode is interpreted as a rotation around the
        // host center, matching the convention used by the legacy SpritePainter
        // UpdateRootTransform path. Perspective is applied AFTER rotation (in
        // model-to-screen order) so that rotated-into-depth quads exhibit the
        // expected foreshortening; the focal-distance formula M34 = -1/d
        // mirrors the SpritePainter perspective matrix exactly.
        var toOriginM = Matrix4x4.CreateTranslation(-w / 2f, -h / 2f, 0);
        var fromOriginM = Matrix4x4.CreateTranslation(w / 2f, h / 2f, 0);
        Matrix4x4Node centered;
        if (enablePersp)
        {
            var perspM = new Matrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1f / focal,
                0, 0, 0, 1);
            centered = (Matrix4x4Node)toOriginM * _transformNode * (Matrix4x4Node)perspM * (Matrix4x4Node)fromOriginM;
        }
        else
        {
            centered = (Matrix4x4Node)toOriginM * _transformNode * (Matrix4x4Node)fromOriginM;
        }

        _root.StartAnimation("TransformMatrix", centered);
        _bakedInstalledTransform = _transformNode;
        _bakedInstalledW = w;
        _bakedInstalledH = h;
        _bakedInstalledEnablePerspective = enablePersp;
        _bakedInstalledFocalDistance = focal;
    }

    private void OnRenderingModeChanged()
    {
        if (_baked != null)
        {
            _baked.Dispose();
            _baked = null;
            _bakedGeometry = null;
        }
        _bakedInstalledTransform = null;
        if (_root != null) _root.Children.RemoveAll();
        _spritePool = null;
        _spritePoolGeometry = null;
        _lastVisible = null;
        _lastOrder = Array.Empty<int>();
        _lastOrderCount = 0;
        _sorter = null;
        Rebuild();
    }
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
    private Func<TimeSpan, Vector3>? _autoRefreshSamplerWithTime;
    private Func<Matrix4x4>? _autoRefreshMatrixSampler;
    private Func<TimeSpan, Matrix4x4>? _autoRefreshMatrixSamplerWithTime;
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
        // BakedAspectGraph owns rotation entirely via the typed AST + GPU
        // expression; the legacy CPU rotation matrix path does nothing.
        if (IsBakedAspectGraphActive()) return;
        const float deg2rad = MathF.PI / 180f;
        var rotation = Matrix4x4.CreateFromYawPitchRoll(
            rotationDegrees.Y * deg2rad,
            rotationDegrees.X * deg2rad,
            rotationDegrees.Z * deg2rad);
        Rebuild(rotation);
    }

    /// <summary>
    /// Matrix variant of <see cref="RebuildForExternalRotation(Vector3)"/> for callers
    /// driving <see cref="SetExternalRotationMatrix(ExpressionAnimation)"/>. Re-runs the
    /// back-face cull and painter sort against the supplied rotation matrix — the same
    /// matrix the caller feeds the composition expression, so CPU cull and GPU draw stay
    /// in lock-step with no Euler round-trip. UI thread only.
    /// </summary>
    /// <param name="rotation">Current rotation matrix (same convention as
    /// <see cref="SetExternalRotationMatrix(ExpressionAnimation)"/>).</param>
    public void RebuildForExternalRotation(Matrix4x4 rotation)
    {
        // BakedAspectGraph owns rotation entirely via the typed AST + GPU
        // expression; the legacy CPU rotation matrix path does nothing.
        if (IsBakedAspectGraphActive()) return;
        Rebuild(rotation);
    }
    /// Records the latest rotation and schedules a single rebuild on the UI thread; if
    /// further calls arrive before that rebuild runs, only the most recent value is used.
    /// Useful when feeding rotation samples from a non-UI thread or at a higher rate than
    /// the UI can absorb.
    /// </summary>
    public void RequestRebuildForExternalRotation(Vector3 rotationDegrees)
    {
        _pendingExternalRotation = rotationDegrees;
#if COMBOBULATE_NO_XAML
        RebuildForExternalRotation(_pendingExternalRotation);
        return;
#else
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
#if !COMBOBULATE_NO_XAML
        _renderingHandler = OnRenderingTick;
#if WINAPPSDK
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#else
        Windows.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#endif
#endif
    }

    /// <summary>
    /// Variant of <see cref="EnableAutoRefresh(Func{Vector3})"/> that gives the
    /// sampler the compositor's <c>RenderingEventArgs.RenderingTime</c> for the
    /// frame being prepared. Use this when the caller derives its rotation from
    /// a <c>ScalarKeyFrameAnimation</c> driven by the compositor clock — sampling
    /// from the same clock keeps the CPU-computed rotation (used for cull/sort)
    /// and the GPU-evaluated rotation (used for drawing) in lock-step. Sampling
    /// from <c>DateTime.UtcNow</c> or a wall-clock <c>Stopwatch</c> drifts whenever
    /// the compositor stalls, which produces visible cull/order glitches after the
    /// drift accumulates past one frame's worth of yaw.
    /// </summary>
    public void EnableAutoRefresh(Func<TimeSpan, Vector3> rotationSampler)
    {
        if (rotationSampler is null) throw new ArgumentNullException(nameof(rotationSampler));
        DisableAutoRefresh();
        _autoRefreshSamplerWithTime = rotationSampler;
#if !COMBOBULATE_NO_XAML
        _renderingHandler = OnRenderingTick;
#if WINAPPSDK
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#else
        Windows.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#endif
#endif
    }

    /// <summary>
    /// Matrix variant of <see cref="EnableAutoRefresh(System.Func{Vector3})"/> for callers
    /// driving <see cref="SetExternalRotationMatrix(ExpressionAnimation)"/>. The sampler
    /// returns the current rotation matrix (same value fed to the composition expression)
    /// and the per-frame cull/sort runs against it directly — no Euler round-trip.
    /// </summary>
    public void EnableAutoRefresh(Func<Matrix4x4> rotationSampler)
    {
        if (rotationSampler is null) throw new ArgumentNullException(nameof(rotationSampler));
        DisableAutoRefresh();
        _autoRefreshMatrixSampler = rotationSampler;
#if !COMBOBULATE_NO_XAML
        _renderingHandler = OnRenderingTick;
#if WINAPPSDK
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#else
        Windows.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#endif
#endif
    }

    /// <summary>
    /// Compositor-clock matrix variant of
    /// <see cref="EnableAutoRefresh(System.Func{System.TimeSpan,Vector3})"/>. Samples the
    /// rotation matrix from the frame's <c>RenderingTime</c> so the CPU cull/sort and the
    /// GPU-evaluated matrix stay in lock-step even when the compositor stalls.
    /// </summary>
    public void EnableAutoRefresh(Func<TimeSpan, Matrix4x4> rotationSampler)
    {
        if (rotationSampler is null) throw new ArgumentNullException(nameof(rotationSampler));
        DisableAutoRefresh();
        _autoRefreshMatrixSamplerWithTime = rotationSampler;
#if !COMBOBULATE_NO_XAML
        _renderingHandler = OnRenderingTick;
#if WINAPPSDK
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#else
        Windows.UI.Xaml.Media.CompositionTarget.Rendering += _renderingHandler;
#endif
#endif
    }
    /// child-order updates for every quad, regardless of the per-frame skip
    /// optimisations (<c>_lastVisible</c> / <c>_lastOrder</c>). If a known-broken
    /// view heals after calling this, the bug lives in the skip caches.
    /// </summary>
    public void InvalidateRenderCaches()
    {
        if (_lastVisible != null)
            for (int i = 0; i < _lastVisible.Length; i++) _lastVisible[i] = false;
        _lastOrder = Array.Empty<int>();
        _lastOrderCount = 0;
    }

    /// <summary>
    /// Diagnostic snapshot of the per-frame skip caches. Returns the visible
    /// flag for every quad (in cached-quad index order) and the painter order
    /// (sequence of cached-quad indices, back to front). UI thread only.
    /// </summary>
    public (bool[] visible, int[] order) GetRenderCacheSnapshot()
    {
        var v = _lastVisible == null ? Array.Empty<bool>() : (bool[])_lastVisible.Clone();
        var o = (int[])_lastOrder.Clone();
        return (v, o);
    }

    /// <summary>
    /// Allocation-free variant of <see cref="GetRenderCacheSnapshot"/> for
    /// per-frame instrumentation. Packs the first <paramref name="maxBits"/>
    /// entries of <c>_lastVisible</c> into <paramref name="mask"/> (LSB =
    /// quad 0) and reports the popcount and total cached quad count. Safe
    /// to call on the UI thread; performs no allocations.
    /// </summary>
    public void CopyVisibleMaskByte(int maxBits, out byte mask, out byte count, out int totalQuads)
    {
        mask = 0;
        count = 0;
        var lv = _lastVisible;
        totalQuads = lv?.Length ?? 0;
        if (lv == null || maxBits <= 0) return;
        int n = Math.Min(Math.Min(maxBits, 8), lv.Length);
        for (int i = 0; i < n; i++)
        {
            if (lv[i])
            {
                mask |= (byte)(1 << i);
                count++;
            }
        }
    }

    /// <summary>
    /// Diagnostic: returns the actual <c>_root.Children</c> stack as cached-quad
    /// indices, bottom to top. This is the ground truth of what compositor will
    /// render and includes hidden sprites still parented in the collection.
    /// Compare against <see cref="GetRenderCacheSnapshot"/>.order to confirm the
    /// visible subset's relative ordering matches what the painter sort decided.
    /// </summary>
    public int[] GetActualChildrenOrder()
    {
        if (_root == null || _spritePool == null) return Array.Empty<int>();
        var children = _root.Children;
        var pool = _spritePool;
        var result = new int[children.Count];
        int k = 0;
        foreach (var child in children)
        {
            int idx = -1;
            for (int i = 0; i < pool.Length; i++)
            {
                if (ReferenceEquals(pool[i], child)) { idx = i; break; }
            }
            result[k++] = idx;
        }
        return result;
    }

    /// <summary>
    /// Diagnostic: returns the LIVE <c>SpriteVisual.IsVisible</c> for every pooled
    /// sprite (in cached-quad index order). Compare against
    /// <see cref="GetRenderCacheSnapshot"/>.visible to detect a divergence between
    /// what the cull code thinks it pushed and what the sprite property actually holds.
    /// </summary>
    public bool[] GetLiveSpriteVisibility()
    {
        if (_spritePool == null) return Array.Empty<bool>();
        var result = new bool[_spritePool.Length];
        for (int i = 0; i < _spritePool.Length; i++)
        {
            var s = _spritePool[i];
            result[i] = s != null && s.IsVisible;
        }
        return result;
    }

    /// <summary>
    /// Diagnostic: replaces every triangle sprite's brush with a solid color
    /// brush. Quad sprites are unchanged. Use to determine whether triangle
    /// sprites/clips render at all (isolates brush issues from clip/sprite
    /// transform issues). Returns the count replaced.
    /// </summary>
    public int ForceTriangleColorBrush(Windows.UI.Color color)
    {
        if (_spritePool == null || _spritePoolGeometry == null || _compositor == null) return 0;
        int n = Math.Min(_spritePool.Length, _spritePoolGeometry.Quads.Length);
        int replaced = 0;
        for (int i = 0; i < n; i++)
        {
            if (!_spritePoolGeometry.Quads[i].IsTriangle) continue;
            var s = _spritePool[i];
            if (s == null) continue;
            s.Brush = _compositor.CreateColorBrush(color);
            replaced++;
        }
        return replaced;
    }

    /// <summary>
    /// Diagnostic: forces every triangle sprite's CompositionSurfaceBrush to
    /// have TransformMatrix=Identity. Use to determine whether the
    /// BuildTriangleAffine math is the cause of blank rendering (vs. broken
    /// surface assignment or sprite/clip setup). Returns count modified.
    /// </summary>
    public int ForceTriangleIdentityBrushTransform()
    {
        if (_spritePool == null || _spritePoolGeometry == null) return 0;
        int n = Math.Min(_spritePool.Length, _spritePoolGeometry.Quads.Length);
        int modified = 0;
        for (int i = 0; i < n; i++)
        {
            if (!_spritePoolGeometry.Quads[i].IsTriangle) continue;
            var s = _spritePool[i];
            if (s?.Brush is CompositionSurfaceBrush sb)
            {
                sb.TransformMatrix = System.Numerics.Matrix3x2.Identity;
                modified++;
            }
        }
        return modified;
    }

    /// <summary>
    /// Diagnostic: forces every triangle sprite's CompositionSurfaceBrush to
    /// have a specific TransformMatrix. Use to test which kinds of matrices
    /// Composition will render vs. silently drop.
    /// </summary>
    public int ForceTriangleBrushTransform(float m11, float m12, float m21, float m22, float m31, float m32)
    {
        if (_spritePool == null || _spritePoolGeometry == null) return 0;
        int n = Math.Min(_spritePool.Length, _spritePoolGeometry.Quads.Length);
        int modified = 0;
        var mat = new System.Numerics.Matrix3x2(m11, m12, m21, m22, m31, m32);
        for (int i = 0; i < n; i++)
        {
            if (!_spritePoolGeometry.Quads[i].IsTriangle) continue;
            var s = _spritePool[i];
            if (s?.Brush is CompositionSurfaceBrush sb)
            {
                sb.TransformMatrix = mat;
                modified++;
            }
        }
        return modified;
    }

    /// <summary>
    /// Diagnostic: sets every triangle sprite's CompositionSurfaceBrush.Scale,
    /// Offset, RotationAngleInDegrees. Tests whether these accept negative
    /// scales differently from TransformMatrix.
    /// </summary>
    public int ForceTriangleBrushScale(float sx, float sy, float ox, float oy, float rotDeg)
    {
        if (_spritePool == null || _spritePoolGeometry == null) return 0;
        int n = Math.Min(_spritePool.Length, _spritePoolGeometry.Quads.Length);
        int modified = 0;
        for (int i = 0; i < n; i++)
        {
            if (!_spritePoolGeometry.Quads[i].IsTriangle) continue;
            var s = _spritePool[i];
            if (s?.Brush is CompositionSurfaceBrush sb)
            {
                sb.TransformMatrix = System.Numerics.Matrix3x2.Identity;
                sb.Scale = new System.Numerics.Vector2(sx, sy);
                sb.Offset = new System.Numerics.Vector2(ox, oy);
                sb.RotationAngleInDegrees = rotDeg;
                sb.CenterPoint = new System.Numerics.Vector2(0.5f, 0.5f);
                modified++;
            }
        }
        return modified;
    }

    /// <summary>
    /// Diagnostic: removes the GeometricClip from every triangle sprite so
    /// the full rectangular sprite renders. Use to determine whether the
    /// TriangleClip is the cause of blank rendering. Returns count cleared.
    /// </summary>
    public int ClearTriangleClips()
    {
        if (_spritePool == null || _spritePoolGeometry == null) return 0;
        int n = Math.Min(_spritePool.Length, _spritePoolGeometry.Quads.Length);
        int cleared = 0;
        for (int i = 0; i < n; i++)
        {
            if (!_spritePoolGeometry.Quads[i].IsTriangle) continue;
            var s = _spritePool[i];
            if (s?.Clip != null) { s.Clip = null; cleared++; }
        }
        return cleared;
    }

    /// <summary>
    /// Diagnostic: returns per-sprite human-readable dump of Size, TransformMatrix
    /// translation (v0), basis lengths, and IsTriangle flag for the first
    /// <paramref name="count"/> sprites in the pool. Used to diagnose why subdivided
    /// triangle fragments don't render even though IsVisible=true.
    /// </summary>
    public string[] GetSpriteGeometryDump(int count)
    {
        if (_spritePool == null) return Array.Empty<string>();
        var geo = _spritePoolGeometry;
        var n = Math.Min(count, _spritePool.Length);
        var lines = new string[n];
        for (int i = 0; i < n; i++)
        {
            var s = _spritePool[i];
            if (s == null) { lines[i] = $"#{i}: null"; continue; }
            var tm = s.TransformMatrix;
            var size = s.Size;
            var v0 = new System.Numerics.Vector3(tm.M41, tm.M42, tm.M43);
            var nx = new System.Numerics.Vector3(tm.M11, tm.M12, tm.M13);
            var ny = new System.Numerics.Vector3(tm.M21, tm.M22, tm.M23);
            var nz = new System.Numerics.Vector3(tm.M31, tm.M32, tm.M33);
            string triFlag = "?";
            string uvInfo = "";
            if (geo != null && i < geo.Quads.Length)
            {
                var q = geo.Quads[i];
                triFlag = q.IsTriangle ? "T" : "Q";
                if (q.IsTriangle)
                {
                    uvInfo = $" uv0=({q.Uv0.X:F3},{q.Uv0.Y:F3}) uv1=({q.Uv1.X:F3},{q.Uv1.Y:F3}) uv2=({q.Uv2.X:F3},{q.Uv2.Y:F3})";
                }
                else
                {
                    uvInfo = $" uv0=({q.Uv0.X:F3},{q.Uv0.Y:F3}) uv1=({q.Uv1.X:F3},{q.Uv1.Y:F3}) uv2=({q.Uv2.X:F3},{q.Uv2.Y:F3}) uv3=({q.Uv3.X:F3},{q.Uv3.Y:F3})";
                }
            }
            string clip = s.Clip == null ? "no-clip" : "clipped";
            string brush = s.Brush == null ? "no-brush" : s.Brush.GetType().Name;
            string surfaceInfo = "";
            if (s.Brush is CompositionSurfaceBrush sb)
            {
                surfaceInfo = sb.Surface == null
                    ? " surface=NULL"
                    : $" surface={sb.Surface.GetType().Name}";
                var bt = sb.TransformMatrix;
                float det = bt.M11 * bt.M22 - bt.M12 * bt.M21;
                surfaceInfo += $" bxform=[{bt.M11:F3},{bt.M12:F3},{bt.M21:F3},{bt.M22:F3},{bt.M31:F2},{bt.M32:F2}] det={det:F4}";
            }
            lines[i] = $"#{i} {triFlag} vis={s.IsVisible} size=({size.X:F1},{size.Y:F1}) v0=({v0.X:F1},{v0.Y:F1},{v0.Z:F1}){uvInfo} {clip} {brush}{surfaceInfo}";
        }
        return lines;
    }

    /// <summary>
    /// Diagnostic: produces a human-readable report of the BakedAspectGraph
    /// renderer's state — cell count, _root.Children leak count, current
    /// axis live values, and which cell(s) the compositor currently has at
    /// non-zero opacity. Returns an explanatory string when the renderer is
    /// inactive.
    /// </summary>
    public string GetBakedAspectGraphDiagnostics()
    {
        if (_baked == null) return "BakedAspectGraph: renderer is null (mode not active or not yet baked).";
        if (_transformAxes == null) return "BakedAspectGraph: transform axes not configured.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Combobulate.BakedAspectGraph diagnostics:");
        sb.AppendLine($"  RenderingMode={RenderingMode}");
        sb.AppendLine($"  _root.Size={_root?.Size}");
        sb.AppendLine($"  host ActualSize=({HostWidth},{HostHeight})");
        sb.AppendLine($"  bakedHost=({_bakedHostW},{_bakedHostH}) bakedScale={_bakedScale}");
        sb.Append(_baked.GetDiagnosticReport(_transformAxes, _transformNode));
        return sb.ToString();
    }

    /// <summary>Stops the auto-refresh loop installed by <see cref="EnableAutoRefresh"/>.</summary>
    public void DisableAutoRefresh()
    {
#if !COMBOBULATE_NO_XAML
        if (_renderingHandler != null)
        {
#if WINAPPSDK
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _renderingHandler;
#else
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= _renderingHandler;
#endif
            _renderingHandler = null;
        }
#endif
        _autoRefreshSampler = null;
        _autoRefreshSamplerWithTime = null;
        _autoRefreshMatrixSampler = null;
        _autoRefreshMatrixSamplerWithTime = null;
    }

#if !COMBOBULATE_NO_XAML
    private void OnRenderingTick(object? sender, object e)
    {
        var samplerT = _autoRefreshSamplerWithTime;
        var sampler = _autoRefreshSampler;
        var mSamplerT = _autoRefreshMatrixSamplerWithTime;
        var mSampler = _autoRefreshMatrixSampler;
        if (samplerT == null && sampler == null && mSamplerT == null && mSampler == null) return;
        try
        {
            // Pull the compositor's RenderingTime out of the event args so
            // time-based samplers clock off the same source the compositor uses
            // to evaluate ScalarKeyFrameAnimations. The runtime types differ
            // between WinUI3 and UWP but both expose a RenderingTime TimeSpan.
            TimeSpan ts = default;
            if (samplerT != null || mSamplerT != null)
            {
#if WINAPPSDK
                if (e is Microsoft.UI.Xaml.Media.RenderingEventArgs rea)
                    ts = rea.RenderingTime;
#else
                if (e is Windows.UI.Xaml.Media.RenderingEventArgs rea)
                    ts = rea.RenderingTime;
#endif
            }

            if (mSamplerT != null) RebuildForExternalRotation(mSamplerT(ts));
            else if (mSampler != null) RebuildForExternalRotation(mSampler());
            else if (samplerT != null) RebuildForExternalRotation(samplerT(ts));
            else RebuildForExternalRotation(sampler!());
        }
        catch
        {
            // Sampler errors must not tear down the per-frame callback; the next tick
            // re-tries. Silent by design — diagnostics belong in the caller's sampler.
        }
    }
#endif

    private void Rebuild() => Rebuild(GetRotationMatrix());

    /// <summary>
    /// True when BakedAspectGraph mode owns the rendering pipeline. In
    /// this state Combobulate's legacy per-frame Update path, the
    /// SpritePainter pool, the dual-tree renderer, and the external-rotation
    /// composition animations are all suppressed: the bake fully owns
    /// _root.TransformMatrix and the visual tree, and the only ongoing
    /// work is the BakedAspectGraphRenderer's own background bake +
    /// materialise. This guard prevents accidental per-frame UI thread
    /// activity (e.g. EnableAutoRefresh ticks, slider-driven rebuild
    /// requests) from racing the bake's primary-axis LiveValueProvider
    /// overrides and freezing the compositor.
    /// </summary>
    private bool IsBakedAspectGraphActive() =>
        RenderingMode == global::Combobulate.Rendering.RenderingMode.BakedAspectGraph
        && _transformNode is not null
        && _transformAxes is not null;

    /// <summary>
    /// Idempotent update for the BakedAspectGraph path: ensures the GPU
    /// transform animation is installed, kicks off an async bake if any
    /// stable input changed (geometry, scale, host size, sort algorithm,
    /// or — when not currently baking — a probed secondary input). Safe
    /// to call from any UI-thread entry point; per-call cost is one set
    /// of cheap field comparisons plus, occasionally, a few AST evaluates
    /// for the secondary-input probe (throttled to once per 150ms).
    /// </summary>
    private void UpdateBakeIfNeeded()
    {
        if (!IsBakedAspectGraphActive()) return;
        if (_compositor == null || _root == null) return;
        var hostW = (float)HostWidth;
        var hostH = (float)HostHeight;
        if (hostW <= 0 || hostH <= 0) return;

        var model = Model;
        if (model == null || model.IsEmpty) return;

        ApplyBakedTransformAnimation();

        var geometry = ResolveGeometryForModel(model);
        var pack = ResolveMaterialPack(model);
        var materialToken = CurrentMaterialToken(pack);
        var scale = (float)ModelScale;

        // Detect when secondary inputs (anything in the AST other than the
        // primary axes) have changed since the last bake. Throttled and
        // skipped while a bake is in flight to avoid racing the background
        // task on shared primary-axis LiveValueProviders.
        bool secondaryChanged = false;
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long elapsedMs = _baked != null
            ? (nowTicks - _lastSecondaryProbeTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency
            : long.MaxValue;
        if (_baked != null && !_baked.BakeInFlight && elapsedMs >= 150)
        {
            _lastSecondaryProbeTicks = nowTicks;
            secondaryChanged = SecondaryProbeChanged();
        }

        bool needRebake = _baked == null
            || !ReferenceEquals(_bakedGeometry, geometry)
            || _bakedScale != scale
            || _bakedHostW != hostW
            || _bakedHostH != hostH
            || _bakedAlgorithm != SortAlgorithm
            || _bakedCullMarginCosCache != ComputeCullMarginCos()
            || secondaryChanged;
        if (_baked != null && _baked.BakeInFlight) needRebake = false;

        // Hot brush-swap fast-path: only Materials changed (geometry,
        // scale, host size, sort algorithm, transform AST and axes are
        // all unchanged). The bake's signature set + cell predicates +
        // sprite tree topology are still valid; only the per-quad
        // CompositionBrush needs to swap. No teardown / no compositor
        // expression reinstall.
        if (!needRebake && _baked != null && !_baked.BakeInFlight
            && !ReferenceEquals(_bakedMaterialsToken, materialToken))
        {
            var hotBindings = ResolveCurrentMaterials(geometry, pack);
            if (_baked.UpdateBindings(hotBindings))
            {
                _bakedMaterialsToken = materialToken;
                return;
            }
            // UpdateBindings refused (quad-count mismatch or no current
            // trees); fall through to a full rebake.
            needRebake = true;
        }

        if (!needRebake) return;

        if (_baked == null)
        {
            _baked = new global::Combobulate.Rendering.BakedAspectGraphRenderer(_compositor, _root);
        }
        var bakedResolved = ResolveCurrentMaterials(geometry, pack);
        var cullMarginCosNow = ComputeCullMarginCos();
        // BAG bake MUST use cameraDistance=0 (orthographic) regardless of
        // EnablePerspective. Why: the runtime predicate in PredicateCompiler
        // tests `TransformedDirectionZ(M, normal) > threshold` — an orthographic
        // face-front test. If the bake classifies faces using the perspective
        // IsFrontFacingPerspective(viewNormal, viewCentroid, cameraZ) test,
        // boundary faces visible under perspective but back-facing under
        // orthographic disagree with the runtime predicate, so their signature
        // never matches and the entire book renders invisible (only the drop
        // shadow remains because it has its own transform). The visual
        // perspective applied to _root.TransformMatrix in
        // ApplyBakedTransformAnimation is independent of this — perspective
        // foreshortening still renders correctly; only the painter sort
        // remains orthographic. Combine with SubdivideForPainter for the
        // non-convex-overhang painter-order correctness at extreme tilts.
        var cameraDistanceNow = 0f;
        _baked.RequestBake(
            transformNode: _transformNode!,
            axes: _transformAxes!,
            geometry: geometry,
            bindings: bakedResolved,
            scale: scale,
            hostW: hostW,
            hostH: hostH,
            cullMarginCos: cullMarginCosNow,
            cameraDistance: cameraDistanceNow,
            sortAlgorithm: SortAlgorithm);
        _bakedGeometry = geometry;
        _bakedScale = scale;
        _bakedHostW = hostW;
        _bakedHostH = hostH;
        _bakedAlgorithm = SortAlgorithm;
        _bakedCullMarginCosCache = cullMarginCosNow;
        _bakedCameraDistanceCache = cameraDistanceNow;
        _bakedEnablePerspectiveCache = EnablePerspective;
        _bakedMaterialsToken = materialToken;
        CaptureSecondaryProbe();
    }

    private void Rebuild(Matrix4x4 rotation)
    {
        if (_compositor == null || _root == null) return;

        // BakedAspectGraph short-circuit: when the typed transform owns
        // the visual tree, the legacy Rebuild path (SpritePainter pool,
        // external-rotation TransformMatrix install, per-frame painter
        // sort) does nothing. The bake schedules its own work via
        // Combobulate.UpdateBakeIfNeeded.
        if (IsBakedAspectGraphActive())
        {
            UpdateBakeIfNeeded();
            return;
        }

        var model = Model;
        if (model == null || model.IsEmpty)
        {
            ClearSpritePool();
            return;
        }

        // Resolve cached geometry. ObjCache.ForModel(/Subdivided) is a
        // ConditionalWeakTable lookup — O(1) amortised — so reusing the
        // same ObjModel across many controls and across every rotation
        // tick costs nothing beyond the first build.
        var geometry = _geometry;
        if (geometry == null || !ReferenceEquals(geometry.Model, model))
        {
            geometry = ResolveGeometryForModel(model);
            _geometry = geometry;
        }

        var pack = ResolveMaterialPack(model);

        // (Re)materialise the per-instance sprite pool keyed off geometry + scale + host
        // size + material pack. Anything in here is rotation-invariant; the rotation
        // affects only IsVisible (cull) and sibling order (sort).
        var scale = (float)ModelScale;
        // Divide only the model SCALE by the rasterization scale so the die keeps
        // a stable on-screen pixel size at >100% display scaling (the composition
        // child visual magnifies authored coordinates by rasterScale). The host
        // dimensions used for the sprite ORIGIN stay in full DIP units so the die
        // is centred on the element (matching UpdateRootTransform and the
        // external-rotation path); dividing the origin by rs put the die at 1/rs
        // of the way to centre and desynced it from the rotation centre.
        var spriteRs = RasterScale();
        scale /= spriteRs;
        var hostW = (float)HostWidth;
        var hostH = (float)HostHeight;

        // ---- BakedAspectGraph path ----
        // Already handled by UpdateBakeIfNeeded called from the
        // IsBakedAspectGraphActive() short-circuit above. This branch is
        // kept only as a safety net if Update is called via a path that
        // bypasses that guard (e.g. early lifecycle); it just delegates.
        if (IsBakedAspectGraphActive() && hostW > 0 && hostH > 0)
        {
            UpdateBakeIfNeeded();
            return;
        }

        // ---- SpritePainter path ----

        var packKey = CurrentMaterialToken(pack) ?? NoPackSentinel;
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
            _lastOrderCount = 0;
            _sorter = null;
        }

        ResolvedQuadMaterials? resolved = packChanged
            ? ResolveCurrentMaterials(geometry, pack)
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
                sprite.IsVisible = false;
                pool[i] = sprite;
                // New sprites need transform + brush regardless of cached flags.
                _root.Children.InsertAtTop(sprite);
            }

            if (isNew || transformChanged)
            {
                var cq = cachedQuads[i].WithCanonicalAxisAlignedUv();
                var v0 = cq.V0 * scale + origin;
                var v1 = cq.V1 * scale + origin;
                var v3 = cq.V3 * scale + origin;
                var xAxis = v1 - v0;
                var yAxis = v3 - v0;
                // See BakedAspectGraphRenderer.BuildTreeContent for the root-cause
                // explanation: SpriteVisuals with Size=(1,1) and a TransformMatrix
                // that scales by N× sample their brush at native ~1×1 resolution
                // and lose all detail once the source surface exceeds ~768 px
                // on either axis. Set Size to the actual cell dimensions and
                // keep only rotation+translate in the matrix.
                var lenX = xAxis.Length();
                var lenY = yAxis.Length();
                sprite!.Size = new Vector2(lenX > 0f ? lenX : 1f, lenY > 0f ? lenY : 1f);
                var nx = lenX > 0f ? xAxis / lenX : Vector3.UnitX;
                var ny = lenY > 0f ? yAxis / lenY : Vector3.UnitY;
                // Guard against degenerate cross (sliver triangles where xAxis ∥ yAxis):
                // Vector3.Normalize divides by Length() which underflows to 0 for
                // very thin slivers, producing NaN basis vectors that break the
                // entire TransformMatrix. Fall back to a unit Z so the (already
                // sub-pixel) sprite renders harmlessly off-axis instead of NaN.
                var crossVec = Vector3.Cross(xAxis, yAxis);
                var crossLen = crossVec.Length();
                var zAxis = crossLen > 1e-6f ? crossVec / crossLen : Vector3.UnitZ;
                sprite.TransformMatrix = new Matrix4x4(
                    nx.X, nx.Y, nx.Z, 0,
                    ny.X, ny.Y, ny.Z, 0,
                    zAxis.X, zAxis.Y, zAxis.Z, 0,
                    v0.X, v0.Y, v0.Z, 1);
                // Triangle fragments (V3==V2) render their (V0,V1,V2) area as a
                // right-triangle inscribed in the rectangular sprite: the renderer
                // sets xAxis=V1-V0, yAxis=V2-V0 so the sprite spans (0..lenX, 0..lenY)
                // in local space and the triangle's three corners land at (0,0),
                // (lenX,0), (0,lenY). A GeometricClip referencing the shared
                // unit-triangle path (scaled by Matrix3x2.CreateScale(lenX,lenY))
                // masks the other half of the rectangle. Quads (V3!=V2) keep
                // sprite.Clip == null and render the full parallelogram.
                if (cq.IsTriangle && lenX > 0f && lenY > 0f)
                {
                    sprite.Clip = TriangleClipFactory.CreateTriangleClip(_compositor, lenX, lenY);
                }
                else if (sprite.Clip != null)
                {
                    sprite.Clip = null;
                }
            }

            if ((isNew || packChanged) && resolved != null)
            {
                sprite!.Brush = resolved.Bindings[i].Brush;
            }

            // CompositionBrush.TransformMatrix translations are in sprite
            // pixels, not normalised UV — so the matrix must be rebuilt
            // every time the sprite is created or resized. It must ALSO be
            // rebuilt whenever the brush is (re)assigned (packChanged), because
            // a freshly resolved/updated binding starts at identity
            // (ApplySurfaceBrush) and would otherwise sample the whole atlas.
            // The condition here therefore mirrors the brush-assignment
            // condition above (isNew || packChanged) plus transformChanged.
            if ((isNew || transformChanged || packChanged) && resolved != null)
            {
                var binding = resolved.Bindings[i];
                MaterialResolver.UpdateBrushTransformForSprite(
                    binding.Brush, cachedQuads[i].WithCanonicalAxisAlignedUv(), binding.Material, sprite!.Size);
            }
        }

        // ---- ConvexLiveCull path ----
        // For a convex solid the set of front-facing faces never overlap in the
        // projected image, so painter re-sorting is unnecessary — only per-face
        // visibility matters. Drive each face's Opacity from a compositor
        // ExpressionAnimation that evaluates the exact GeometryPredicates
        // front-face test against the live rotation-matrix buffer (buf.M) on the
        // composition thread: zero UI-thread cull work per frame and zero
        // CPU/GPU sync lag (cull and draw read the same matrix on the same
        // frame, so no cull margin is needed). Requires a matrix external
        // rotation; without one buf.M is Identity, so fall through to the CPU
        // sorter below.
        if (RenderingMode == global::Combobulate.Rendering.RenderingMode.ConvexLiveCull
            && _externalRotationIsMatrix && _externalRotationBuffer != null)
        {
            EnsureLiveCullOpacity(geometry, scale);
            return;
        }
        // Switched away from live-cull (mode change or lost the matrix buffer):
        // tear the per-face Opacity animations down so the CPU sorter path owns
        // visibility again.
        if (_liveCullInstalled) TeardownLiveCullOpacity();

        // Cull + paint-order: delegate to the configured face sorter.
        // The sorter writes a per-quad bool[] cull buffer and a back-to-front
        // permutation of cached-quad indices ([0..n) valid).
        if (_sorter == null || !ReferenceEquals(_spritePoolGeometry, geometry))
        {
            _sorter = global::Combobulate.Sorting.FaceSorterFactory.Create(SortAlgorithm, geometry);
        }
        if (_visScratchBool == null || _visScratchBool.Length < cachedQuads.Length)
            _visScratchBool = new bool[cachedQuads.Length];
        if (_orderScratch == null || _orderScratch.Length < cachedQuads.Length)
            _orderScratch = new int[cachedQuads.Length];

        // Convert the user-facing CullMarginDegrees DP into the cosine-scale
        // value the sorter expects (sin(margin) is the amount of cosine "slack"
        // we add to the front-facing test). 0 reproduces the strict cull.
        float cullMarginCos = 0f;
        var marginDeg = CullMarginDegrees;
        if (marginDeg > 0)
        {
            cullMarginCos = MathF.Sin((float)(marginDeg * Math.PI / 180.0));
        }

        // ---- diagnostics: time the sort and reorder phases separately ----
        long t0 = global::Combobulate.Diagnostics.SpinDiagnostics.IsEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        int orderCount = _sorter.Sort(rotation, _orderScratch, _visScratchBool, ComputeSortCameraDistance(scale), cullMarginCos);

        long t1 = global::Combobulate.Diagnostics.SpinDiagnostics.IsEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        // Diff IsVisible flips against last frame.
        long visMask = 0L;
        long prevVisMask = 0L;
        for (int i = 0; i < cachedQuads.Length; i++)
        {
            bool visible = _visScratchBool[i];
            if (i < 64)
            {
                if (visible) visMask |= 1L << i;
                if (lastVisible[i]) prevVisMask |= 1L << i;
            }
            if (lastVisible[i] != visible)
            {
                pool[i]!.IsVisible = visible;
                lastVisible[i] = visible;
            }
        }

        // Reorder children only if the painter order actually changed. Sprites already
        // live under _root; VisualCollection.InsertAtTop throws E_INVALIDARG when the
        // child still has a parent (even if it's the same parent), so we must Remove
        // first. Walking visible quads back-to-front and re-attaching each one leaves
        // the last-painted (frontmost) on top.
        bool orderChanged = !OrderEquals(_orderScratch, orderCount, _lastOrder, _lastOrderCount);
        int mutationsApplied = 0;
        if (orderChanged)
        {
            for (int i = 0; i < orderCount; i++)
            {
                var sprite = pool[_orderScratch[i]]!;
                _root.Children.Remove(sprite);
                _root.Children.InsertAtTop(sprite);
                mutationsApplied++;
            }
            if (_lastOrder.Length < orderCount) _lastOrder = new int[cachedQuads.Length];
            Array.Copy(_orderScratch, _lastOrder, orderCount);
            _lastOrderCount = orderCount;
        }

        // ---- diagnostics: emit one record per Rebuild call ----
        if (global::Combobulate.Diagnostics.SpinDiagnostics.IsEnabled)
        {
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            long ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000L;
            if (ticksPerUs <= 0) ticksPerUs = 1;
            var (yawDeg, pitchDeg) = global::Combobulate.Diagnostics.SpinDiagnostics.ExtractYawPitch(rotation);
            global::Combobulate.Diagnostics.SpinDiagnostics.Record(new global::Combobulate.Diagnostics.SpinDiagnostics.FrameRecord(
                frameId: _diagFrameCounter++,
                timestampMicros: global::Combobulate.Diagnostics.SpinDiagnostics.ElapsedMicros(),
                threadId: System.Environment.CurrentManagedThreadId,
                yawDeg: yawDeg,
                pitchDeg: pitchDeg,
                orderCount: orderCount,
                visibleMask: visMask,
                previousVisibleMask: prevVisMask,
                orderChanged: orderChanged,
                mutationsApplied: mutationsApplied,
                sortMicros: (t1 - t0) / ticksPerUs,
                reorderMicros: (t2 - t1) / ticksPerUs,
                order: global::Combobulate.Diagnostics.SpinDiagnostics.OrderSnapshot.From(_orderScratch, orderCount)));
        }
    }

    /// <summary>Per-control monotonic frame counter used by <see cref="Combobulate.Diagnostics.SpinDiagnostics"/>.</summary>
    private long _diagFrameCounter;
    private static readonly object NoPackSentinel = new();

    /// <summary>
    /// ConvexLiveCull: (re)installs a per-face <c>Opacity</c> ExpressionAnimation
    /// on every pooled sprite so the compositor culls back faces from the live
    /// rotation-matrix buffer (<c>buf.M</c>). Idempotent — the (expensive) per-face
    /// <c>StartAnimation</c> is only reissued when the sprite pool was rebuilt, the
    /// projection (ortho/perspective + camera distance) changed, or the matrix
    /// buffer identity changed. Otherwise the compositor keeps evaluating the
    /// already-installed expressions each frame with no UI-thread involvement.
    /// </summary>
    private void EnsureLiveCullOpacity(ObjGeometry geometry, float scale)
    {
        var pool = _spritePool;
        if (pool == null || _compositor == null || _externalRotationBuffer == null) return;

        float camZ = ComputeSortCameraDistance(scale);
        bool persp = camZ > 0f;

        bool reinstall = !_liveCullInstalled
            || !ReferenceEquals(_liveCullBuffer, _externalRotationBuffer)
            || _liveCullCamZ != camZ
            || _liveCullPerspective != persp;

        if (!reinstall)
        {
            // Pool may have grown new sprites this Rebuild (transformChanged with
            // isNew==true creates them at IsVisible=false); make sure any freshly
            // created ones are visible + driven. New sprites are rare here (pool
            // is created up front), so this stays O(n) with no StartAnimation.
            for (int i = 0; i < pool.Length; i++)
            {
                var s = pool[i];
                if (s != null && !s.IsVisible) s.IsVisible = true;
            }
            return;
        }

        var quads = geometry.Quads;
        int count = Math.Min(pool.Length, quads.Length);
        for (int i = 0; i < count; i++)
        {
            var sprite = pool[i];
            if (sprite == null) continue;
            sprite.IsVisible = true;
            // Build a FRESH matrix reference node per face: the ExpressionsFork
            // caches a single ExpressionAnimation on each typed node and reuses it
            // across StartAnimation calls, so a node subtree shared between two
            // independent per-face animations collides (StartAnimation throws
            // E_INVALIDARG). The baked path does the same — GetBakedMatrixReference()
            // is called once per cell. Reuse WITHIN one face's expression is fine.
            var mNode = _externalRotationBuffer.GetReference()
                .GetMatrix4x4Property(ExternalRotationMatrixKey);
            var opacity = BuildLiveCullOpacity(mNode, quads[i].Normal, quads[i].Centroid, camZ, persp);
            sprite.StartAnimation("Opacity", opacity);
        }

        _liveCullInstalled = true;
        _liveCullBuffer = _externalRotationBuffer;
        _liveCullCamZ = camZ;
        _liveCullPerspective = persp;
    }

    /// <summary>
    /// Stops any per-face ConvexLiveCull Opacity animations and restores full
    /// opacity so the CPU sorter (SpritePainter) path owns visibility again.
    /// Called when the rendering mode leaves ConvexLiveCull or the matrix
    /// external rotation is cleared.
    /// </summary>
    private void TeardownLiveCullOpacity()
    {
        var pool = _spritePool;
        if (pool != null)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                var s = pool[i];
                if (s == null) continue;
                s.StopAnimation("Opacity");
                s.Opacity = 1f;
            }
        }
        _liveCullInstalled = false;
        _liveCullBuffer = null;
        _liveCullCamZ = 0f;
        _liveCullPerspective = false;
    }

    /// <summary>Property-set key holding the buffered external rotation matrix (<c>buf.M</c>).</summary>
    private const string ExternalRotationMatrixKey = "M";

    /// <summary>
    /// Builds the per-face Opacity ScalarNode (1 = front-facing, 0 = back-facing)
    /// for ConvexLiveCull, replicating <see cref="Combobulate.Sorting.GeometryPredicates"/>
    /// exactly on the compositor thread. <paramref name="m"/> is the pure rotation
    /// matrix (translation-free), so the centroid is transformed as a direction —
    /// identical to the sorter's <c>Vector3.Transform(centroid, rotation)</c> when
    /// the matrix has no translation.
    /// </summary>
    private static Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode BuildLiveCullOpacity(
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node m,
        Vector3 normal, Vector3 centroid, float camZ, bool persp)
    {
        const float eps = global::Combobulate.Sorting.GeometryPredicates.CosineEpsilon;

        var vnz = TransformedComponent(m, normal, 2);
        if (!persp)
        {
            // Orthographic: front-facing iff (M·n).z > CosineEpsilon.
            var orthoPred = vnz > (ScalarNode)eps;
            return ExpressionFunctions.Conditional(orthoPred, (ScalarNode)1f, (ScalarNode)0f);
        }

        // Perspective (exact match to GeometryPredicates.IsFrontFacingPerspective,
        // no cull margin): front-facing iff dot > 0 AND dot² > eps²·|ray|², where
        // ray = cameraPos − vc, cameraPos = (0,0,camZ). Since buf.M is a pure
        // rotation it preserves dot products and lengths, so:
        //   dot   = (M·n)·(cam − M·vc) = camZ·(M·n).z − (n·vc)
        //   |ray|²= camZ² − 2·camZ·(M·vc).z + |vc|²
        // where (n·vc) and |vc|² are model-space CONSTANTS. This collapses the
        // whole test onto the two z-row transforms vnz/vcz (Channel13/23/33 only),
        // making the compositor string a fraction of the naïve per-component form
        // (which blew past Composition's expression-length cap → E_INVALIDARG).
        // The signed-square test dot²>eps²·lenSq with dot>0 is equivalent to
        // dot > eps·√lenSq (eps>0, lenSq≥0), so the sqrt form matches exactly.
        var vcz = TransformedComponent(m, centroid, 2);
        float k = Vector3.Dot(normal, centroid);       // (n·vc), constant
        float cc = centroid.LengthSquared();           // |vc|², constant
        var dot = (ScalarNode)camZ * vnz - (ScalarNode)k;
        var lenSq = (ScalarNode)(camZ * camZ + cc) - (ScalarNode)(2f * camZ) * vcz;
        var perspPred = dot > (ScalarNode)eps * ExpressionFunctions.Sqrt(lenSq);
        return ExpressionFunctions.Conditional(perspPred, (ScalarNode)1f, (ScalarNode)0f);
    }

    /// <summary>
    /// One component (0=x, 1=y, 2=z) of <c>Vector3.TransformNormal(v, M)</c> as a
    /// ScalarNode, row-vector convention (result.x = v·column0 = v.x·M11 + v.y·M21 +
    /// v.z·M31, etc.), matching <see cref="Combobulate.Rendering.EventFunctions"/>.
    /// Zero-coefficient terms are skipped to keep the emitted expression short.
    /// </summary>
    private static Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode TransformedComponent(
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node m, Vector3 v, int comp)
    {
        ScalarNode c1, c2, c3;
        switch (comp)
        {
            case 0: c1 = m.Channel11; c2 = m.Channel21; c3 = m.Channel31; break;
            case 1: c1 = m.Channel12; c2 = m.Channel22; c3 = m.Channel32; break;
            default: c1 = m.Channel13; c2 = m.Channel23; c3 = m.Channel33; break;
        }
        ScalarNode? acc = null;
        if (v.X != 0f) acc = c1 * (ScalarNode)v.X;
        if (v.Y != 0f) acc = acc is null ? c2 * (ScalarNode)v.Y : acc + c2 * (ScalarNode)v.Y;
        if (v.Z != 0f) acc = acc is null ? c3 * (ScalarNode)v.Z : acc + c3 * (ScalarNode)v.Z;
        return acc ?? (ScalarNode)0f;
    }


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
        _lastOrderCount = 0;
        _sorter = null;
        _spritePoolGeometry = null;
        _spritePoolBindings = null;
        _spritePoolPackKey = null;
        _spritePoolScale = 0;
        _spritePoolHostW = 0;
        _spritePoolHostH = 0;
        // Sprites (and their per-face Opacity animations) are disposed above;
        // just reset the ConvexLiveCull install tracking so a later rebuild
        // reinstalls cleanly.
        _liveCullInstalled = false;
        _liveCullBuffer = null;
        _liveCullCamZ = 0f;
        _liveCullPerspective = false;
    }

    private static bool OrderEquals(int[] a, int aCount, int[] b, int bCount)
    {
        if (aCount != bCount) return false;
        for (int i = 0; i < aCount; i++) if (a[i] != b[i]) return false;
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

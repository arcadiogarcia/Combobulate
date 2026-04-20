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

    private void OnLoaded(object sender, RoutedEventArgs e) => TryAttachVisuals();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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

    private void UpdateRootTransform()
    {
        if (_root == null || _host == null) return;

        var w = (float)_host.ActualWidth;
        var h = (float)_host.ActualHeight;
        if (w <= 0 || h <= 0) return;

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
        _root.Size = new Vector2(w, h);
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
            // Surface failures by clearing the model. Callers can listen to Model changes
            // or check ObjCache directly for richer diagnostics.
            _sourceKey = null;
            _sourceDirectory = null;
            _geometry = null;
            Model = null;
            return;
        }

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

    private void Rebuild()
    {
        if (_compositor == null || _root == null) return;

        // Tear down previous quads.
        var existing = new List<Visual>(_root.Children.Count);
        foreach (var child in _root.Children) existing.Add(child);
        _root.Children.RemoveAll();
        foreach (var v in existing) v.Dispose();

        var model = Model;
        if (model == null || model.Quads.Count == 0) return;

        // Resolve cached geometry. ObjCache.ForModel is a ConditionalWeakTable lookup —
        // O(1) amortized — so reusing the same ObjModel across many controls and across
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

        var resolved = MaterialResolver.Resolve(_compositor!, geometry, pack);

        var scale = (float)ModelScale;

        var hostOriginX = (float)(_host?.ActualWidth ?? 0) / 2f;
        var hostOriginY = (float)(_host?.ActualHeight ?? 0) / 2f;
        var origin = new Vector3(hostOriginX, hostOriginY, 0);

        // For back-face culling: a face is front-facing if its model-space normal,
        // after the current rotation, points toward the viewer. Our perspective
        // (M34 = -1/d) places the viewer at +Z, so front-facing means viewN.Z > 0.
        var rotation = GetRotationMatrix();

        // Composition has no z-buffer for SpriteVisuals — sibling order is paint order.
        // Collect visible quads with their geometry so we can sort back-to-front
        // using a topology-aware test (centroid sort alone fails for perpendicular
        // adjacent faces with very different centroids — e.g. a book's page-top
        // edge whose y=±0.7 centroid swings further in view-Z than a cover's
        // z=±0.1 centroid under any non-trivial pitch).
        var cachedQuads = geometry.Quads;
        var visible = new List<VisibleQuad>(cachedQuads.Length);

        for (int i = 0; i < cachedQuads.Length; i++)
        {
            var cq = cachedQuads[i];

            // Back-face cull using the cached model-space normal — no per-render normal math.
            var viewNormal = Vector3.TransformNormal(cq.Normal, rotation);
            if (viewNormal.Z <= 0) continue;

            var v0 = cq.V0 * scale + origin;
            var v1 = cq.V1 * scale + origin;
            var v3 = cq.V3 * scale + origin;

            var xAxis = v1 - v0;
            var yAxis = v3 - v0;
            var zAxis = Vector3.Normalize(Vector3.Cross(xAxis, yAxis));

            // Maps unit-square local point (x, y, z, 1) → world via row-major basis.
            var transform = new Matrix4x4(
                xAxis.X, xAxis.Y, xAxis.Z, 0,
                yAxis.X, yAxis.Y, yAxis.Z, 0,
                zAxis.X, zAxis.Y, zAxis.Z, 0,
                v0.X,    v0.Y,    v0.Z,    1);

            var sprite = _compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(1f, 1f);
            sprite.Brush = resolved.Bindings[i].Brush;
            sprite.TransformMatrix = transform;

            var viewCentroidZ = Vector3.Transform(cq.Centroid, rotation).Z;
            visible.Add(new VisibleQuad(sprite, cq.V0, cq.V1, cq.V2, cq.V3, cq.Centroid, cq.Normal, viewCentroidZ));
        }

        // Topology-aware painter's sort. For each pair of visible quads (a, b),
        // if every vertex of a lies on the back side of b's plane (negative
        // signed distance with respect to b's outward normal), then a must be
        // drawn before b. This holds regardless of rotation because each
        // remaining quad already passed back-face culling, so its outward model
        // normal points generally toward the viewer in view space — its plane's
        // negative side is the far side from the camera.
        var ordered = TopologicalPainterSort(visible);
        foreach (var q in ordered)
        {
            _root.Children.InsertAtTop(q.Sprite);
        }
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
    /// Returns the visible quads in painter's order (back to front) using a
    /// topology-aware partial order combined with a view-Z tiebreaker.
    /// </summary>
    private static List<VisibleQuad> TopologicalPainterSort(List<VisibleQuad> quads)
    {
        int n = quads.Count;
        if (n <= 1) return quads;

        // edge[a,b] == true means a must be drawn before b.
        var edge = new bool[n, n];
        var inDegree = new int[n];
        const float eps = 1e-4f;

        for (int b = 0; b < n; b++)
        {
            var qb = quads[b];
            for (int a = 0; a < n; a++)
            {
                if (a == b) continue;
                var qa = quads[a];

                // a is "behind" b iff every vertex of a lies on or behind b's
                // plane (signed distance <= +eps) AND at least one vertex is
                // strictly behind (< -eps). The "<=" tolerance is essential
                // for adjacent perpendicular faces that share an edge — those
                // shared vertices have signed distance ≈ 0, which would fail a
                // strict "< -eps" test and leave the pair unordered, causing
                // the centroid-Z fallback to mis-order them (e.g. a book's
                // page-bottom edge vs its spine).
                var d0 = Vector3.Dot(qa.V0 - qb.Centroid, qb.Normal);
                var d1 = Vector3.Dot(qa.V1 - qb.Centroid, qb.Normal);
                var d2 = Vector3.Dot(qa.V2 - qb.Centroid, qb.Normal);
                var d3 = Vector3.Dot(qa.V3 - qb.Centroid, qb.Normal);
                if (d0 <= eps && d1 <= eps && d2 <= eps && d3 <= eps &&
                    (d0 < -eps || d1 < -eps || d2 < -eps || d3 < -eps))
                {
                    if (!edge[a, b])
                    {
                        edge[a, b] = true;
                        inDegree[b]++;
                    }
                }
            }
        }

        // Kahn's algorithm with a "farthest first" tiebreaker among nodes with
        // no remaining predecessors.
        var result = new List<VisibleQuad>(n);
        var emitted = new bool[n];
        for (int step = 0; step < n; step++)
        {
            int pick = -1;
            float pickZ = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (emitted[i] || inDegree[i] != 0) continue;
                if (quads[i].ViewCentroidZ < pickZ)
                {
                    pickZ = quads[i].ViewCentroidZ;
                    pick = i;
                }
            }

            if (pick < 0)
            {
                // Cycle (e.g. interpenetrating geometry). Fall back to view-Z
                // ordering for whatever remains.
                for (int i = 0; i < n; i++)
                {
                    if (!emitted[i])
                    {
                        if (quads[i].ViewCentroidZ < pickZ)
                        {
                            pickZ = quads[i].ViewCentroidZ;
                            pick = i;
                        }
                    }
                }
                if (pick < 0) break;
            }

            emitted[pick] = true;
            result.Add(quads[pick]);
            for (int j = 0; j < n; j++)
            {
                if (edge[pick, j]) inDegree[j]--;
            }
        }

        return result;
    }
}

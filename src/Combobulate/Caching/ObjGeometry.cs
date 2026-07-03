using System;
using System.Numerics;
using Combobulate.Parsing;

#if WINAPPSDK
using Windows.UI;
#else
using Windows.UI;
#endif

namespace Combobulate.Caching;

/// <summary>
/// Precomputed, render-ready geometry derived from an <see cref="ObjModel"/>.
///
/// <para>
/// All per-quad data here is rotation-, scale-, and host-size-independent, so it can be
/// computed once and reused across many <c>Combobulate</c> controls and across every
/// re-render triggered by rotation or resize.
/// </para>
///
/// <para>
/// Vertex positions are stored relative to <see cref="Center"/>, the centroid of all
/// referenced positions — exactly the offset the renderer applies before scaling. That
/// removes a per-vertex subtract from the hot path.
/// </para>
/// </summary>
public sealed class ObjGeometry
{
    /// <summary>The source model.</summary>
    public ObjModel Model { get; }

    /// <summary>Centroid of all referenced positions; subtracted from each cached vertex.</summary>
    public Vector3 Center { get; }

    /// <summary>Per-quad render data. May be shorter than <c>Model.Quads</c> if some quads referenced invalid indices.</summary>
    public CachedQuad[] Quads { get; }

    private int[][]? _predecessors;
    private static readonly object PredecessorsLock = new();

    private int[]? _coplanarGroups;
    private static readonly object CoplanarGroupsLock = new();

    /// <summary>
    /// Per-quad coplanar group index. Two quads in the same group lie on
    /// the same plane (within the decomposer's coplanarity thresholds)
    /// and therefore can never occlude each other from any view angle —
    /// downstream painter-order machinery can skip pairwise depth tests
    /// between them. Computed lazily; reuse-stable for the lifetime of
    /// the geometry.
    /// </summary>
    public int[] CoplanarGroups
    {
        get
        {
            var g = _coplanarGroups;
            if (g != null) return g;
            lock (CoplanarGroupsLock)
            {
                g = _coplanarGroups;
                if (g != null) return g;
                g = global::Combobulate.Sorting.MeshDecomposer.ComputeCoplanarGroups(Quads);
                _coplanarGroups = g;
                return g;
            }
        }
    }

    /// <summary>
    /// Topological "must paint before" relation, computed once per geometry and reused by
    /// every renderer instance and every rotation. <c>Predecessors[b]</c> is the array of
    /// quad indices <c>a</c> such that every vertex of <c>a</c> lies on the back side of
    /// <c>b</c>'s plane (in model space). Because the test uses only model-space centroids
    /// and normals, the partial order is rotation-invariant.
    /// </summary>
    public int[][] Predecessors
    {
        get
        {
            // Double-checked locking; lazy + thread-safe.
            var p = _predecessors;
            if (p != null) return p;
            lock (PredecessorsLock)
            {
                p = _predecessors;
                if (p != null) return p;
                p = ComputePredecessors(Quads);
                _predecessors = p;
                return p;
            }
        }
    }

    private static int[][] ComputePredecessors(CachedQuad[] quads)
    {
        int n = quads.Length;
        var result = new int[n][];
        if (n <= 1)
        {
            for (int i = 0; i < n; i++) result[i] = Array.Empty<int>();
            return result;
        }

        // Scale: signed distance in object space — anchored to the central predicate
        // constant so all distance-scale tolerances move together.
        const float eps = global::Combobulate.Sorting.GeometryPredicates.DistanceEpsilon;
        var lists = new System.Collections.Generic.List<int>[n];
        for (int i = 0; i < n; i++) lists[i] = new System.Collections.Generic.List<int>();

        for (int b = 0; b < n; b++)
        {
            var qb = quads[b];
            for (int a = 0; a < n; a++)
            {
                if (a == b) continue;
                var qa = quads[a];
                var d0 = Vector3.Dot(qa.V0 - qb.Centroid, qb.Normal);
                var d1 = Vector3.Dot(qa.V1 - qb.Centroid, qb.Normal);
                var d2 = Vector3.Dot(qa.V2 - qb.Centroid, qb.Normal);
                var d3 = Vector3.Dot(qa.V3 - qb.Centroid, qb.Normal);
                if (d0 <= eps && d1 <= eps && d2 <= eps && d3 <= eps &&
                    (d0 < -eps || d1 < -eps || d2 < -eps || d3 < -eps))
                {
                    lists[b].Add(a);
                }
            }
        }

        for (int i = 0; i < n; i++) result[i] = lists[i].ToArray();
        return result;
    }

    private ObjGeometry(ObjModel model, Vector3 center, CachedQuad[] quads)
    {
        Model = model;
        Center = center;
        Quads = quads;
    }

    /// <summary>
    /// Returns a new <see cref="ObjGeometry"/> whose <see cref="Quads"/>
    /// have been pre-split for correct per-fragment painter ordering. See
    /// <see cref="global::Combobulate.Sorting.MeshDecomposer"/> for the
    /// algorithm and what is preserved (material, UVs, normal) vs not
    /// (fragments are always triangle-shaped).
    ///
    /// <para>Returns <c>this</c> unchanged when the mesh is already
    /// painter-ready (e.g. any convex closed polyhedron) — a cheap
    /// fast-path that avoids producing a redundant copy.</para>
    /// </summary>
    /// <param name="options">Optional tuning. Pass <c>null</c> for the
    /// production defaults documented on
    /// <see cref="global::Combobulate.Sorting.MeshDecomposer.DecomposerOptions.Default"/>.</param>
    public ObjGeometry WithPainterSubdivision(
        global::Combobulate.Sorting.MeshDecomposer.DecomposerOptions? options = null)
    {
        // Fast path: convex meshes (and most closed boxes) already satisfy
        // the per-face painter-readiness invariant. Skipping the rebuild
        // preserves CachedQuad identity for the rendering hot path and
        // avoids spending a few microseconds re-triangulating cubes.
        if (global::Combobulate.Sorting.MeshDecomposer.IsAlreadyPainterReady(Quads, options))
            return this;

        var fragments = global::Combobulate.Sorting.MeshDecomposer
            .DecomposeForPainterOrder(Quads, options)
            .ToArray();
        return new ObjGeometry(Model, Center, fragments);
    }

    /// <summary>Builds (or returns the canonical) cached geometry for <paramref name="model"/>.
    /// <para>
    /// Faces in <see cref="ObjModel.Quads"/> are added directly. Faces in
    /// <see cref="ObjModel.Triangles"/> are first converted to triangle
    /// CachedQuads, then passed through <see cref="QuadRecovery"/> which fuses
    /// coplanar adjacent pairs back into single quads where it is safe to do
    /// so. Unmatched triangles remain as triangle CachedQuads
    /// (<see cref="CachedQuad.IsTriangle"/> == true) and the renderer applies
    /// a triangle-shaped composition clip plus a 3-point affine brush
    /// transform when materialising them.
    /// </para>
    /// </summary>
    public static ObjGeometry Build(ObjModel model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        var center = ComputeCenter(model);
        var list = new System.Collections.Generic.List<CachedQuad>(
            model.Quads.Count + model.Triangles.Count);

        // Quads first — preserve historical ordering.
        for (int i = 0; i < model.Quads.Count; i++)
        {
            var quad = model.Quads[i];
            if (!TryGetCorner(model, quad.V0, out var p0) ||
                !TryGetCorner(model, quad.V1, out var p1) ||
                !TryGetCorner(model, quad.V2, out var p2) ||
                !TryGetCorner(model, quad.V3, out var p3))
            {
                continue;
            }

            // Degenerate quads are dropped at build time so the renderer never sees them.
            var crossLenSq = Vector3.Cross(p1 - p0, p3 - p0).LengthSquared();
            if (crossLenSq <= 0) continue;

            var mc0 = p0 - center;
            var mc1 = p1 - center;
            var mc2 = p2 - center;
            var mc3 = p3 - center;
            var centroid = (mc0 + mc1 + mc2 + mc3) * 0.25f;
            var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p3 - p0));

            // Default UVs: unit square (mirrors the SpriteVisual unit-square local space).
            var uv0 = GetUv(model, quad.V0, new Vector2(0, 0));
            var uv1 = GetUv(model, quad.V1, new Vector2(1, 0));
            var uv2 = GetUv(model, quad.V2, new Vector2(1, 1));
            var uv3 = GetUv(model, quad.V3, new Vector2(0, 1));
            bool hasExplicitUv =
                quad.V0.TexCoordIndex.HasValue || quad.V1.TexCoordIndex.HasValue ||
                quad.V2.TexCoordIndex.HasValue || quad.V3.TexCoordIndex.HasValue;

            list.Add(new CachedQuad(i, mc0, mc1, mc2, mc3, centroid, normal,
                ColorForIndex(list.Count), quad.Material, uv0, uv1, uv2, uv3,
                isTriangle: false, hasExplicitUv: hasExplicitUv));
        }

        // Triangles — build a transient list, then run quad recovery on it.
        var triBuf = new System.Collections.Generic.List<CachedQuad>(model.Triangles.Count);
        for (int i = 0; i < model.Triangles.Count; i++)
        {
            var tri = model.Triangles[i];
            if (!TryGetCorner(model, tri.V0, out var p0) ||
                !TryGetCorner(model, tri.V1, out var p1) ||
                !TryGetCorner(model, tri.V2, out var p2))
            {
                continue;
            }

            // Degenerate sliver: zero-area triangles are dropped.
            var crossLenSq = Vector3.Cross(p1 - p0, p2 - p0).LengthSquared();
            if (crossLenSq <= 0) continue;

            var mc0 = p0 - center;
            var mc1 = p1 - center;
            var mc2 = p2 - center;
            var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));

            // Default UVs: parser-default unit-triangle mapping aligned with
            // the triangle's local coords. (0,0) at V0, (1,0) at V1, (0,1) at V2.
            var uv0 = GetUv(model, tri.V0, new Vector2(0, 0));
            var uv1 = GetUv(model, tri.V1, new Vector2(1, 0));
            var uv2 = GetUv(model, tri.V2, new Vector2(0, 1));
            bool hasExplicitUv =
                tri.V0.TexCoordIndex.HasValue || tri.V1.TexCoordIndex.HasValue ||
                tri.V2.TexCoordIndex.HasValue;

            // SourceIndex set later (recovered quads may renumber); use the
            // OBJ source-triangle index as a stable transient key.
            triBuf.Add(CachedQuad.Triangle(i, mc0, mc1, mc2, normal,
                ColorForIndex(0), tri.Material, uv0, uv1, uv2, hasExplicitUv: hasExplicitUv));
        }

        QuadRecovery.Recover(triBuf,
            out var recoveredQuads, out var leftoverTriangles);

        // Append recovered quads + leftover triangles, assigning a fresh
        // SourceIndex per CachedQuad that's stable across builds.
        int sourceCursor = list.Count;
        foreach (var q in recoveredQuads)
        {
            list.Add(new CachedQuad(sourceCursor, q.V0, q.V1, q.V2, q.V3,
                q.Centroid, q.Normal, ColorForIndex(sourceCursor), q.MaterialName,
                q.Uv0, q.Uv1, q.Uv2, q.Uv3, isTriangle: false, hasExplicitUv: q.HasExplicitUv));
            sourceCursor++;
        }
        foreach (var t in leftoverTriangles)
        {
            list.Add(new CachedQuad(sourceCursor, t.V0, t.V1, t.V2, t.V3,
                t.Centroid, t.Normal, ColorForIndex(sourceCursor), t.MaterialName,
                t.Uv0, t.Uv1, t.Uv2, t.Uv3, isTriangle: true, hasExplicitUv: t.HasExplicitUv));
            sourceCursor++;
        }

        return new ObjGeometry(model, center, list.ToArray());
    }

    private static Vector3 ComputeCenter(ObjModel model)
    {
        if (model.Positions.Count == 0) return Vector3.Zero;

        var sum = Vector3.Zero;
        var count = 0;
        foreach (var quad in model.Quads)
        {
            Accumulate(model, quad.V0, ref sum, ref count);
            Accumulate(model, quad.V1, ref sum, ref count);
            Accumulate(model, quad.V2, ref sum, ref count);
            Accumulate(model, quad.V3, ref sum, ref count);
        }
        foreach (var tri in model.Triangles)
        {
            Accumulate(model, tri.V0, ref sum, ref count);
            Accumulate(model, tri.V1, ref sum, ref count);
            Accumulate(model, tri.V2, ref sum, ref count);
        }
        return count == 0 ? Vector3.Zero : sum / count;
    }

    private static void Accumulate(ObjModel model, ObjVertex v, ref Vector3 sum, ref int count)
    {
        if (v.PositionIndex < 0 || v.PositionIndex >= model.Positions.Count) return;
        var p = model.Positions[v.PositionIndex];
        sum += new Vector3(p.X, p.Y, p.Z);
        count++;
    }

    private static Vector2 GetUv(ObjModel model, ObjVertex v, Vector2 fallback)
    {
        if (v.TexCoordIndex is not int idx) return fallback;
        if (idx < 0 || idx >= model.TexCoords.Count) return fallback;
        var t = model.TexCoords[idx];
        return new Vector2(t.X, t.Y);
    }

    private static bool TryGetCorner(ObjModel model, ObjVertex vertex, out Vector3 position)
    {
        var idx = vertex.PositionIndex;
        if (idx < 0 || idx >= model.Positions.Count)
        {
            position = default;
            return false;
        }

        var p = model.Positions[idx];
        position = new Vector3(p.X, p.Y, p.Z);
        return true;
    }

    private static Color ColorForIndex(int i)
    {
        var hue = (i * 0.61803398875f) % 1f;
        return HsvToRgb(hue, 0.65f, 0.95f);
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        var i = (int)(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);
        float r, g, b;
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

/// <summary>Per-quad cached data in model space, centered on <see cref="ObjGeometry.Center"/>.
/// <para>
/// For triangle faces, <see cref="IsTriangle"/> is <c>true</c>, <see cref="V3"/> is
/// set equal to <see cref="V2"/>, <see cref="Uv3"/> equals <see cref="Uv2"/>, and
/// <see cref="Centroid"/> is the triangle centroid <c>(V0+V1+V2)/3</c>. The renderer
/// installs a triangle-shaped <c>CompositionGeometricClip</c> and a 3-point affine
/// brush transform for these faces.
/// </para>
/// </summary>
public readonly struct CachedQuad
{
    public CachedQuad(int sourceIndex, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
        Vector3 centroid, Vector3 normal, Color fallbackColor,
        string? materialName, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        bool isTriangle = false, bool hasExplicitUv = false)
    {
        SourceIndex = sourceIndex;
        V0 = v0; V1 = v1; V2 = v2; V3 = v3;
        Centroid = centroid;
        Normal = normal;
        FallbackColor = fallbackColor;
        MaterialName = materialName;
        Uv0 = uv0; Uv1 = uv1; Uv2 = uv2; Uv3 = uv3;
        IsTriangle = isTriangle;
        HasExplicitUv = hasExplicitUv;
    }

    /// <summary>Index into <see cref="ObjModel.Quads"/> (or <see cref="ObjModel.Triangles"/>
    /// when <see cref="IsTriangle"/> is true). Source-format indices are
    /// disambiguated by <see cref="IsTriangle"/>; the renderer keys its sprite
    /// pool by position in the geometry's <see cref="ObjGeometry.Quads"/> array
    /// rather than by this field.</summary>
    public int SourceIndex { get; }

    public Vector3 V0 { get; }
    public Vector3 V1 { get; }
    public Vector3 V2 { get; }

    /// <summary>For quads, the fourth corner; for triangles equals <see cref="V2"/>.</summary>
    public Vector3 V3 { get; }

    /// <summary>Centroid (model space, centered). Quad: average of four corners.
    /// Triangle: average of three corners.</summary>
    public Vector3 Centroid { get; }

    /// <summary>Outward face normal (model space, normalized). Computed as
    /// <c>normalize((V1-V0) × (V3-V0))</c> for quads (which collapses to the
    /// triangle normal when V3=V2, so the same formula works for both).</summary>
    public Vector3 Normal { get; }

    /// <summary>Per-quad fallback color used when no material is bound.</summary>
    public Color FallbackColor { get; }

    /// <summary>Original Color accessor preserved for back-compat.</summary>
    [Obsolete("Use FallbackColor.")]
    public Color Color => FallbackColor;

    /// <summary>The <c>usemtl</c> name in effect for this quad, or null.</summary>
    public string? MaterialName { get; }

    public Vector2 Uv0 { get; }
    public Vector2 Uv1 { get; }
    public Vector2 Uv2 { get; }

    /// <summary>For quads, UV at V3; for triangles equals <see cref="Uv2"/>.</summary>
    public Vector2 Uv3 { get; }

    /// <summary>True when this face was authored as (or recovered to) a triangle.
    /// Triangles have <see cref="V3"/>==<see cref="V2"/> and use a triangle-clip
    /// rendering path instead of the parallelogram path.</summary>
    public bool IsTriangle { get; }

    /// <summary>True when at least one of the source face's vertices had an
    /// explicit <c>vt</c> reference. False means the per-corner
    /// <c>Uv0..Uv3</c> values are parser-assigned defaults (e.g. unit square
    /// for quads, unit right-triangle for triangles) and should not be
    /// trusted for UV-continuity checks across faces.</summary>
    public bool HasExplicitUv { get; }

    /// <summary>
    /// Returns a copy whose vertices and UVs are re-labelled so that
    /// <see cref="Uv0"/>=<c>(minU,minV)</c>, <see cref="Uv1"/>=<c>(maxU,minV)</c>,
    /// <see cref="Uv2"/>=<c>(maxU,maxV)</c>, <see cref="Uv3"/>=<c>(minU,maxV)</c>,
    /// <em>iff</em> the four UVs form an axis-aligned rectangle (each U is one
    /// of two values, each V is one of two values, and the four corners are
    /// distinct). For any other UV layout — explicit non-rectangular UVs,
    /// degenerate rects, or triangles — the quad is returned unchanged.
    ///
    /// <para><strong>Why.</strong> After painter subdivision,
    /// <see cref="Combobulate.Sorting.PolygonClipper"/> emits sub-quad
    /// vertices in walk order, so a sub-fragment's <c>V0</c> can land on any
    /// of the four UV corners and the winding may be CW or CCW. Any layout
    /// other than the canonical one forces the brush affine
    /// (<see cref="BrushTransformMath.BuildQuadAxisAlignedCrop"/>) to carry
    /// off-diagonal cross-terms, and Composition's
    /// <c>CompositionSurfaceBrush.TransformMatrix</c> applies those cross-terms
    /// with a convention that does <em>not</em> match the naive
    /// "sampleUV = brushPixel / spriteSize" model — the texture renders
    /// rotated 90° (cyclically-rotated windings) or mirrored (reversed
    /// windings).</para>
    ///
    /// <para>Re-labelling each corner by its UV value (not by cyclic shift)
    /// makes the brush affine strictly <em>diagonal</em> regardless of the
    /// source winding, which Composition handles unambiguously (and which
    /// matches the original, validated bbox-crop formula). The on-screen
    /// rendering of a SpriteVisual depends only on its X/Y basis vectors and
    /// origin (the Z basis row never multiplies the z=0 brush content), so
    /// re-labelling — even when it flips the sprite's geometric normal for a
    /// reversed-winding face — does not mirror or hide the sprite. Centroid
    /// and face normal are carried through unchanged, so the externally
    /// computed painter order and per-face visibility are unaffected.</para>
    /// </summary>
    public CachedQuad WithCanonicalAxisAlignedUv()
    {
        if (IsTriangle)
            return this;

        Span<Vector2> uvs = stackalloc Vector2[4] { Uv0, Uv1, Uv2, Uv3 };

        float minU = uvs[0].X, maxU = uvs[0].X, minV = uvs[0].Y, maxV = uvs[0].Y;
        for (int i = 1; i < 4; i++)
        {
            if (uvs[i].X < minU) minU = uvs[i].X;
            if (uvs[i].X > maxU) maxU = uvs[i].X;
            if (uvs[i].Y < minV) minV = uvs[i].Y;
            if (uvs[i].Y > maxV) maxV = uvs[i].Y;
        }

        float du = maxU - minU;
        float dv = maxV - minV;
        if (du <= 1e-6f || dv <= 1e-6f)
            return this; // degenerate / collinear UVs — leave as-is.

        // Identify, by UV value, which source corner sits at each rectangle
        // corner. corner index: 0=(minU,minV) 1=(maxU,minV) 2=(maxU,maxV)
        // 3=(minU,maxV). Each must be present exactly once for an axis-aligned
        // rectangle; otherwise the UVs are not a clean rect and we bail out.
        const float eps = 1e-4f;
        Span<int> cornerSrc = stackalloc int[4] { -1, -1, -1, -1 };
        for (int i = 0; i < 4; i++)
        {
            bool uMin = System.Math.Abs(uvs[i].X - minU) <= eps;
            bool uMax = System.Math.Abs(uvs[i].X - maxU) <= eps;
            bool vMin = System.Math.Abs(uvs[i].Y - minV) <= eps;
            bool vMax = System.Math.Abs(uvs[i].Y - maxV) <= eps;
            if (!(uMin ^ uMax) || !(vMin ^ vMax))
                return this; // a coord that is neither extreme → not axis-aligned rect.
            int corner = (uMax ? 1 : 0) | (vMax ? 2 : 0);
            // Map bit (uMax|vMax<<1) → canonical slot: 0,1,3,2.
            int slot = corner switch { 0 => 0, 1 => 1, 3 => 2, _ => 3 };
            if (cornerSrc[slot] >= 0)
                return this; // duplicate corner → not four distinct corners.
            cornerSrc[slot] = i;
        }
        if (cornerSrc[0] < 0 || cornerSrc[1] < 0 || cornerSrc[2] < 0 || cornerSrc[3] < 0)
            return this;

        if (cornerSrc[0] == 0 && cornerSrc[1] == 1 && cornerSrc[2] == 2 && cornerSrc[3] == 3)
            return this; // already canonical.

        Span<Vector3> vs = stackalloc Vector3[4] { V0, V1, V2, V3 };
        return new CachedQuad(
            SourceIndex,
            vs[cornerSrc[0]], vs[cornerSrc[1]], vs[cornerSrc[2]], vs[cornerSrc[3]],
            Centroid, Normal, FallbackColor, MaterialName,
            new Vector2(minU, minV), new Vector2(maxU, minV),
            new Vector2(maxU, maxV), new Vector2(minU, maxV),
            isTriangle: false, hasExplicitUv: HasExplicitUv);
    }

    /// <summary>Factory for triangle faces. Computes centroid as
    /// <c>(v0+v1+v2)/3</c> and sets V3=V2, Uv3=Uv2.</summary>
    public static CachedQuad Triangle(int sourceIndex, Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3 normal, Color fallbackColor, string? materialName,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, bool hasExplicitUv = false)
    {
        var centroid = (v0 + v1 + v2) * (1f / 3f);
        return new CachedQuad(sourceIndex, v0, v1, v2, v2,
            centroid, normal, fallbackColor, materialName,
            uv0, uv1, uv2, uv2, isTriangle: true, hasExplicitUv: hasExplicitUv);
    }
}

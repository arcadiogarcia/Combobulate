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

        const float eps = 1e-4f;
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

    /// <summary>Builds (or returns the canonical) cached geometry for <paramref name="model"/>.</summary>
    public static ObjGeometry Build(ObjModel model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        var center = ComputeCenter(model);
        var list = new System.Collections.Generic.List<CachedQuad>(model.Quads.Count);

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

            list.Add(new CachedQuad(i, mc0, mc1, mc2, mc3, centroid, normal,
                ColorForIndex(i), quad.Material, uv0, uv1, uv2, uv3));
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

/// <summary>Per-quad cached data in model space, centered on <see cref="ObjGeometry.Center"/>.</summary>
public readonly struct CachedQuad
{
    public CachedQuad(int sourceIndex, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
        Vector3 centroid, Vector3 normal, Color fallbackColor,
        string? materialName, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        SourceIndex = sourceIndex;
        V0 = v0; V1 = v1; V2 = v2; V3 = v3;
        Centroid = centroid;
        Normal = normal;
        FallbackColor = fallbackColor;
        MaterialName = materialName;
        Uv0 = uv0; Uv1 = uv1; Uv2 = uv2; Uv3 = uv3;
    }

    /// <summary>Index into <see cref="ObjModel.Quads"/>.</summary>
    public int SourceIndex { get; }

    public Vector3 V0 { get; }
    public Vector3 V1 { get; }
    public Vector3 V2 { get; }
    public Vector3 V3 { get; }

    /// <summary>Average of the four corners (model space, centered).</summary>
    public Vector3 Centroid { get; }

    /// <summary>Outward face normal (model space, normalized).</summary>
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
    public Vector2 Uv3 { get; }
}

using System.Numerics;

namespace Combobulate.Sorting;

/// <summary>
/// Mutable working-set element for the quad-preserving decomposition
/// pipeline. Holds a single convex polygon (3+ vertices, in CCW ring
/// order) with per-vertex UVs, the parent source-quad index, and the
/// polygon's plane.
///
/// <para>The decomposer's working set is a list of these polygons.
/// Splitting a polygon by a plane produces 0, 1, or 2 new
/// <c>PolygonFragment</c>s. Polygons can grow vertex counts during
/// sequential cuts: a triangle cut by an adjacent-edge plane becomes a
/// triangle + quadrilateral; further cuts can produce pentagons etc.
/// At emission time fragments with 3 vertices become triangle
/// <see cref="Combobulate.Caching.CachedQuad"/>s, 4-vertex polygons
/// become quad CachedQuads, and 5+ vertex polygons are fan-triangulated.
/// </para>
///
/// <para>The plane is preserved across splits (a cut produces an
/// in-plane subset whose plane equals the parent's). The
/// <see cref="SourceQuadIndex"/> is also preserved so material, normal,
/// and color can be looked up at emission time.</para>
/// </summary>
internal readonly struct PolygonFragment
{
    /// <summary>Vertices in CCW ring order (model space, centred).</summary>
    public readonly Vector3[] Vertices;

    /// <summary>UV coordinates per vertex, same length as <see cref="Vertices"/>.</summary>
    public readonly Vector2[] Uvs;

    /// <summary>Index of the parent <see cref="Combobulate.Caching.CachedQuad"/> in the input list.</summary>
    public readonly int SourceQuadIndex;

    /// <summary>Plane this polygon lies in (normal + offset). Cuts preserve the plane.</summary>
    public readonly Plane3 Plane;

    public PolygonFragment(Vector3[] vertices, Vector2[] uvs, int sourceQuadIndex, Plane3 plane)
    {
        Vertices = vertices;
        Uvs = uvs;
        SourceQuadIndex = sourceQuadIndex;
        Plane = plane;
    }

    /// <summary>Vertex count. <c>3</c> = triangle, <c>4</c> = quad, <c>5+</c> = general convex polygon.</summary>
    public int Count => Vertices?.Length ?? 0;

    public bool IsTriangle => Count == 3;
    public bool IsQuad => Count == 4;
}

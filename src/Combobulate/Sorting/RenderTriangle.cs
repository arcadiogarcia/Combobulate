using System.Numerics;

namespace Combobulate.Sorting;

/// <summary>
/// Internal triangle representation used by the BSP/Newell pipelines.
/// Triangles carry positions, optional UVs (kept for future per-fragment
/// rendering), a precomputed plane, and a back-pointer to the source quad
/// they came from. The renderer paints whole source quads, so the
/// <see cref="SourceQuadIndex"/> is what flows back out to determine
/// SpriteVisual order.
///
/// <para>Triangles are immutable. The splitter produces new triangles
/// rather than mutating; the BSP builder owns the lifetime.</para>
/// </summary>
public readonly struct RenderTriangle
{
    /// <summary>Vertex 0 (model space, centered).</summary>
    public Vector3 A { get; }

    /// <summary>Vertex 1.</summary>
    public Vector3 B { get; }

    /// <summary>Vertex 2.</summary>
    public Vector3 C { get; }

    /// <summary>UV at vertex A in the source quad's UV space.</summary>
    public Vector2 UvA { get; }

    /// <summary>UV at vertex B.</summary>
    public Vector2 UvB { get; }

    /// <summary>UV at vertex C.</summary>
    public Vector2 UvC { get; }

    /// <summary>Index into the original <c>CachedQuad[]</c>; multiple triangles share this when split.</summary>
    public int SourceQuadIndex { get; }

    /// <summary>Plane of this triangle (normal points away from the back face).</summary>
    public Plane3 Plane { get; }

    /// <summary>Centroid of the three vertices.</summary>
    public Vector3 Centroid { get; }

    public RenderTriangle(
        Vector3 a, Vector3 b, Vector3 c,
        Vector2 uvA, Vector2 uvB, Vector2 uvC,
        int sourceQuadIndex,
        Plane3 plane)
    {
        A = a; B = b; C = c;
        UvA = uvA; UvB = uvB; UvC = uvC;
        SourceQuadIndex = sourceQuadIndex;
        Plane = plane;
        Centroid = (a + b + c) * (1f / 3f);
    }

    /// <summary>
    /// Construct from three vertices, recomputing the plane from their
    /// winding (CCW input → outward-pointing normal).
    /// </summary>
    public static RenderTriangle Create(
        Vector3 a, Vector3 b, Vector3 c,
        Vector2 uvA, Vector2 uvB, Vector2 uvC,
        int sourceQuadIndex)
    {
        return new RenderTriangle(a, b, c, uvA, uvB, uvC, sourceQuadIndex,
            Plane3.FromTriangle(a, b, c));
    }

    /// <summary>True when the cross product of the two edges has zero length (degenerate sliver).</summary>
    public bool IsDegenerate
    {
        get
        {
            var cross = Vector3.Cross(B - A, C - A);
            return cross.LengthSquared() < 1e-14f;
        }
    }
}

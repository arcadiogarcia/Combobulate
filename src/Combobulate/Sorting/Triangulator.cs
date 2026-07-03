using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Sorting;

/// <summary>
/// Decomposes <see cref="CachedQuad"/>s into <see cref="RenderTriangle"/>
/// pairs using the canonical V0–V1–V2 / V0–V2–V3 split. UVs are taken
/// directly from the cached quad's per-corner UVs, which the parser
/// interpolates for the V0→(0,0) V1→(1,0) V2→(1,1) V3→(0,1) per-face
/// default mapping or from explicit <c>vt</c> entries.
///
/// <para>The triangulator is the bridge between Combobulate's quad-based
/// source format and the triangle-based BSP/Newell algorithms. It runs
/// once per <see cref="ObjGeometry"/>; the resulting triangle list is
/// owned by the sorter (BSP keeps it permanently in tree leaves; Newell
/// re-uses it as its working set per frame).</para>
/// </summary>
public static class Triangulator
{
    /// <summary>Triangulate every quad in <paramref name="quads"/>; degenerate quads are skipped.</summary>
    public static List<RenderTriangle> Triangulate(IReadOnlyList<CachedQuad> quads)
    {
        var result = new List<RenderTriangle>(quads.Count * 2);
        for (int i = 0; i < quads.Count; i++)
        {
            TriangulateInto(quads[i], i, result);
        }
        return result;
    }

    /// <summary>Triangulate a single quad and append the (up to two) triangles to <paramref name="dest"/>.
    /// If <paramref name="quad"/> is a triangle face (<see cref="CachedQuad.IsTriangle"/>),
    /// only the single (V0,V1,V2) triangle is emitted — V3 equals V2 and the
    /// duplicate would be degenerate.</summary>
    public static void TriangulateInto(CachedQuad quad, int sourceQuadIndex, List<RenderTriangle> dest)
    {
        if (quad.IsTriangle)
        {
            // Triangle face: V3==V2, so the second triangle (V0,V2,V3) is degenerate. Emit only one.
            TryAdd(dest, quad.V0, quad.V1, quad.V2, quad.Uv0, quad.Uv1, quad.Uv2, sourceQuadIndex);
            return;
        }

        // Quad winding: V0, V1, V2, V3. Diagonal split V0→V2 produces
        // triangles (V0,V1,V2) and (V0,V2,V3) — both CCW if the quad was
        // CCW. The two triangles share an edge along the diagonal, so the
        // splitter never has to special-case shared edges across a quad.
        TryAdd(dest, quad.V0, quad.V1, quad.V2, quad.Uv0, quad.Uv1, quad.Uv2, sourceQuadIndex);
        TryAdd(dest, quad.V0, quad.V2, quad.V3, quad.Uv0, quad.Uv2, quad.Uv3, sourceQuadIndex);
    }

    private static void TryAdd(List<RenderTriangle> dest,
        Vector3 a, Vector3 b, Vector3 c,
        Vector2 uvA, Vector2 uvB, Vector2 uvC,
        int sourceQuadIndex)
    {
        var cross = Vector3.Cross(b - a, c - a);
        if (cross.LengthSquared() < 1e-14f) return;
        var t = RenderTriangle.Create(a, b, c, uvA, uvB, uvC, sourceQuadIndex);
        dest.Add(t);
    }
}

using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Polygon splitter — the most error-prone primitive. Tests cover:
///   - vertex classification thresholds
///   - whole-triangle classification (Front, Back, On, Spanning)
///   - 1-front/2-back, 2-front/1-back, vertex-on-plane configurations
///   - UV interpolation correctness along the cut edge
///   - winding preservation
///   - degenerate inputs (sliver triangles, all-on-plane)
/// </summary>
public class PolygonSplitterTests
{
    private static RenderTriangle Tri(Vector3 a, Vector3 b, Vector3 c) =>
        RenderTriangle.Create(a, b, c, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), sourceQuadIndex: 7);

    private static RenderTriangle TriUv(Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC) =>
        RenderTriangle.Create(a, b, c, uvA, uvB, uvC, sourceQuadIndex: 7);

    [Fact]
    public void ClassifyVertex_RespectsEpsilon()
    {
        Assert.Equal(PlaneSide.Front, PolygonSplitter.ClassifyVertex(+1f));
        Assert.Equal(PlaneSide.Back,  PolygonSplitter.ClassifyVertex(-1f));
        Assert.Equal(PlaneSide.On,    PolygonSplitter.ClassifyVertex(0f));
        Assert.Equal(PlaneSide.On,    PolygonSplitter.ClassifyVertex(+PolygonSplitter.Epsilon * 0.5f));
        Assert.Equal(PlaneSide.On,    PolygonSplitter.ClassifyVertex(-PolygonSplitter.Epsilon * 0.5f));
        Assert.Equal(PlaneSide.Front, PolygonSplitter.ClassifyVertex(+PolygonSplitter.Epsilon * 2));
    }

    [Fact]
    public void ClassifyTriangle_AllFront_ReturnsFront()
    {
        var t = Tri(new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1));
        var c = PolygonSplitter.ClassifyTriangle(t, new Plane3(new Vector3(0, 0, 1), 0));
        Assert.Equal(PlaneSide.Front, c.Side);
    }

    [Fact]
    public void ClassifyTriangle_AllBack_ReturnsBack()
    {
        var t = Tri(new Vector3(0, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 1, -1));
        var c = PolygonSplitter.ClassifyTriangle(t, new Plane3(new Vector3(0, 0, 1), 0));
        Assert.Equal(PlaneSide.Back, c.Side);
    }

    [Fact]
    public void ClassifyTriangle_AllOnPlane_ReturnsOn()
    {
        var t = Tri(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        var c = PolygonSplitter.ClassifyTriangle(t, new Plane3(new Vector3(0, 0, 1), 0));
        Assert.Equal(PlaneSide.On, c.Side);
    }

    [Fact]
    public void ClassifyTriangle_OneFrontTwoBack_ReturnsSpanning()
    {
        var t = Tri(new Vector3(0, 0, +1), new Vector3(1, 0, -1), new Vector3(0, 1, -1));
        var c = PolygonSplitter.ClassifyTriangle(t, new Plane3(new Vector3(0, 0, 1), 0));
        Assert.Equal(PlaneSide.Spanning, c.Side);
    }

    [Fact]
    public void Split_AllFront_PassesThrough()
    {
        var t = Tri(new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        Assert.Single(f);
        Assert.Empty(b);
    }

    [Fact]
    public void Split_AllBack_PassesThrough()
    {
        var t = Tri(new Vector3(0, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 1, -1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        Assert.Empty(f);
        Assert.Single(b);
    }

    [Fact]
    public void Split_OneFrontTwoBack_ProducesOneFrontTwoBackTriangles()
    {
        // Triangle with A in front (+Z), B and C behind (-Z). The cut should produce:
        //   front side: 1 triangle (A + intersections on AB and CA)
        //   back side: 2 triangles fan from B (or C) through intersections + remaining vertex
        var t = Tri(new Vector3(0, 0, +1), new Vector3(1, 0, -1), new Vector3(-1, 0, -1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        Assert.Equal(1, f.Count);
        Assert.Equal(2, b.Count);

        // The single front triangle's vertices should all be at z >= -ε.
        foreach (var tri in f)
        {
            Assert.True(tri.A.Z >= -PolygonSplitter.Epsilon);
            Assert.True(tri.B.Z >= -PolygonSplitter.Epsilon);
            Assert.True(tri.C.Z >= -PolygonSplitter.Epsilon);
        }
        // Back triangles all at z <= +ε.
        foreach (var tri in b)
        {
            Assert.True(tri.A.Z <= +PolygonSplitter.Epsilon);
            Assert.True(tri.B.Z <= +PolygonSplitter.Epsilon);
            Assert.True(tri.C.Z <= +PolygonSplitter.Epsilon);
        }
    }

    [Fact]
    public void Split_TwoFrontOneBack_ProducesTwoFrontOneBackTriangles()
    {
        var t = Tri(new Vector3(0, 0, -1), new Vector3(1, 0, +1), new Vector3(-1, 0, +1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        Assert.Equal(2, f.Count);
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void Split_PreservesArea()
    {
        // Area conservation: the sum of fragment areas must equal the original area
        // (modulo floating-point noise). A reliable correctness check.
        var t = Tri(new Vector3(0, 0, +1), new Vector3(2, 0, -1), new Vector3(0, 2, -1));
        var origArea = TriArea(t.A, t.B, t.C);
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        float sum = 0;
        foreach (var x in f) sum += TriArea(x.A, x.B, x.C);
        foreach (var x in b) sum += TriArea(x.A, x.B, x.C);
        Assert.Equal(origArea, sum, 4);
    }

    [Fact]
    public void Split_PreservesWinding()
    {
        // Original normal is +Z. Every fragment must have a normal in the same half-space (+Z direction).
        var t = Tri(new Vector3(0, 0, +1), new Vector3(2, 0, -1), new Vector3(0, 2, -1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        foreach (var x in f.Concat(b))
        {
            var n = Vector3.Cross(x.B - x.A, x.C - x.A);
            Assert.True(Vector3.Dot(n, t.Plane.Normal) > 0, "Winding flipped on fragment");
        }
    }

    [Fact]
    public void Split_InterpolatesUvLinearly()
    {
        // Triangle A(0,0,+1) UV(0,0), B(1,0,-1) UV(1,0), C(0,1,-1) UV(0,1).
        // Cut by plane z=0. The intersection on AB is at midpoint → UV (0.5, 0).
        // The intersection on AC is at midpoint → UV (0, 0.5).
        var t = TriUv(
            new Vector3(0, 0, +1), new Vector3(1, 0, -1), new Vector3(0, 1, -1),
            new Vector2(0, 0),     new Vector2(1, 0),     new Vector2(0, 1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);

        // Find the UV at any cut point — search all fragments for a vertex at the
        // intersection (z≈0, x≈0.5, y≈0) and verify its UV.
        bool found1 = false, found2 = false;
        foreach (var frag in f.Concat(b))
        {
            CheckIntersectionUv(frag.A, frag.UvA, ref found1, ref found2);
            CheckIntersectionUv(frag.B, frag.UvB, ref found1, ref found2);
            CheckIntersectionUv(frag.C, frag.UvC, ref found1, ref found2);
        }
        Assert.True(found1, "Did not find AB-edge intersection with UV (0.5, 0)");
        Assert.True(found2, "Did not find AC-edge intersection with UV (0, 0.5)");

        static void CheckIntersectionUv(Vector3 p, Vector2 uv, ref bool found1, ref bool found2)
        {
            if (System.MathF.Abs(p.Z) > 1e-3f) return;
            if (System.MathF.Abs(p.X - 0.5f) < 1e-3f && System.MathF.Abs(p.Y) < 1e-3f)
            {
                Assert.Equal(0.5f, uv.X, 3);
                Assert.Equal(0.0f, uv.Y, 3);
                found1 = true;
            }
            else if (System.MathF.Abs(p.X) < 1e-3f && System.MathF.Abs(p.Y - 0.5f) < 1e-3f)
            {
                Assert.Equal(0.0f, uv.X, 3);
                Assert.Equal(0.5f, uv.Y, 3);
                found2 = true;
            }
        }
    }

    [Fact]
    public void Split_VertexOnPlane_HandledWithoutCrash()
    {
        // One vertex lies exactly on the plane; splitter must classify it as On
        // and route the resulting fragments cleanly.
        var t = Tri(new Vector3(0, 0, 0), new Vector3(1, 0, +1), new Vector3(0, 1, -1));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        Assert.True(f.Count >= 1);
        Assert.True(b.Count >= 1);
        // Area conservation still holds.
        var orig = TriArea(t.A, t.B, t.C);
        float sum = 0;
        foreach (var x in f) sum += TriArea(x.A, x.B, x.C);
        foreach (var x in b) sum += TriArea(x.A, x.B, x.C);
        Assert.Equal(orig, sum, 4);
    }

    [Fact]
    public void Split_CoplanarSameNormal_RoutesToCoplanarFront()
    {
        var t = Tri(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        var cf = new List<RenderTriangle>(); var cb = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b, cf, cb);
        Assert.Empty(f);
        Assert.Empty(b);
        Assert.Single(cf);
        Assert.Empty(cb);
    }

    [Fact]
    public void Split_CoplanarOppositeNormal_RoutesToCoplanarBack()
    {
        // Reversed winding → normal is -Z, opposite to the splitter plane.
        var t = Tri(new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 0, 0));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        var cf = new List<RenderTriangle>(); var cb = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b, cf, cb);
        Assert.Empty(f);
        Assert.Empty(b);
        Assert.Empty(cf);
        Assert.Single(cb);
    }

    [Fact]
    public void Split_DegenerateInputProducesNoSilvers()
    {
        // Triangle entirely On the plane should be classified On, not produce slivers.
        var t = Tri(new Vector3(0, 0, 1e-6f), new Vector3(1, 0, 1e-6f), new Vector3(0, 1, 1e-6f));
        var f = new List<RenderTriangle>(); var b = new List<RenderTriangle>();
        PolygonSplitter.Split(t, new Plane3(new Vector3(0, 0, 1), 0), f, b);
        Assert.Equal(1, f.Count + b.Count);
    }

    private static float TriArea(Vector3 a, Vector3 b, Vector3 c) =>
        Vector3.Cross(b - a, c - a).Length() * 0.5f;
}

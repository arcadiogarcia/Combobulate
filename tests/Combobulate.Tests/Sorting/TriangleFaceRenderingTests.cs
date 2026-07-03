using Combobulate.Caching;
using Combobulate.Sorting;
using Combobulate.Parsing;
using System.Numerics;
using Windows.UI;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Triangle-face coverage for the rendering pipeline below
/// <see cref="ObjGeometry"/>: <see cref="Triangulator"/> emits the right
/// number of <see cref="RenderTriangle"/>s, and each sorter
/// (BSP / Newell / Topological) produces a correct painter order on
/// triangle-only and mixed meshes.
/// </summary>
public class TriangleFaceRenderingTests
{
    private static CachedQuad Tri(int srcIdx, Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return CachedQuad.Triangle(srcIdx, a, b, c, normal, Color.FromArgb(255, 0, 0, 0),
            null, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
    }

    /// <summary>Build an ObjGeometry from raw triangle vertices (positions only).</summary>
    private static ObjGeometry GeomFromTriangles(params Vector3[][] tris)
    {
        var model = new ObjModel();
        foreach (var t in tris)
        {
            int baseIdx = model.Positions.Count;
            foreach (var v in t) model.Positions.Add(new Vector4(v.X, v.Y, v.Z, 1f));
            model.Triangles.Add(new ObjTriangle(
                new ObjVertex(baseIdx + 0, null, null),
                new ObjVertex(baseIdx + 1, null, null),
                new ObjVertex(baseIdx + 2, null, null),
                null, new[] { "default" }, null, 0));
        }
        return ObjGeometry.Build(model);
    }

    [Fact]
    public void Triangulator_TriangleFaceProducesSingleTriangle()
    {
        var t = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        var dest = new System.Collections.Generic.List<RenderTriangle>();
        Triangulator.TriangulateInto(t, sourceQuadIndex: 0, dest);
        Assert.Single(dest);
        Assert.Equal(new Vector3(0, 0, 0), dest[0].A);
        Assert.Equal(new Vector3(1, 0, 0), dest[0].B);
        Assert.Equal(new Vector3(0, 1, 0), dest[0].C);
    }

    [Fact]
    public void Triangulator_QuadFaceStillProducesTwoTriangles()
    {
        // Regression: triangle path must not affect the quad path.
        var quad = new CachedQuad(0,
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0.5f, 0.5f, 0), new Vector3(0, 0, 1),
            Color.FromArgb(255, 0, 0, 0), null,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1));
        var dest = new System.Collections.Generic.List<RenderTriangle>();
        Triangulator.TriangulateInto(quad, sourceQuadIndex: 0, dest);
        Assert.Equal(2, dest.Count);
    }

    [Fact]
    public void Triangulator_MixedGeometryEmitsCorrectTriangleCounts()
    {
        // Build a mesh where:
        //   - one face is a real quad → 2 triangles
        //   - one face is an unpaired triangle → 1 triangle
        var model = new ObjModel();
        // Quad
        var qb = model.Positions.Count;
        model.Positions.Add(new Vector4(0, 0, 0, 1));
        model.Positions.Add(new Vector4(1, 0, 0, 1));
        model.Positions.Add(new Vector4(1, 1, 0, 1));
        model.Positions.Add(new Vector4(0, 1, 0, 1));
        model.Quads.Add(new ObjQuad(
            new ObjVertex(qb + 0, null, null),
            new ObjVertex(qb + 1, null, null),
            new ObjVertex(qb + 2, null, null),
            new ObjVertex(qb + 3, null, null),
            null, new[] { "default" }, null, 0));
        // Triangle (far from quad so it can't be paired)
        var tb = model.Positions.Count;
        model.Positions.Add(new Vector4(10, 0, 0, 1));
        model.Positions.Add(new Vector4(11, 0, 0, 1));
        model.Positions.Add(new Vector4(10, 1, 0, 1));
        model.Triangles.Add(new ObjTriangle(
            new ObjVertex(tb + 0, null, null),
            new ObjVertex(tb + 1, null, null),
            new ObjVertex(tb + 2, null, null),
            null, new[] { "default" }, null, 0));

        var geom = ObjGeometry.Build(model);
        Assert.Equal(2, geom.Quads.Length); // quad + 1 unpaired triangle

        var allTris = Triangulator.Triangulate(geom.Quads);
        Assert.Equal(3, allTris.Count); // 2 from quad + 1 from triangle
    }

    [Theory]
    [InlineData(SortAlgorithm.Bsp)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Topological)]
    public void Sort_TriangleOnlyMesh_FrontFacesAreVisible(SortAlgorithm algo)
    {
        // Two triangles forming the front face of a unit square (z=+0.5) and
        // two forming the back face (z=-0.5, wound CCW from -Z so they
        // back-face cull from the +Z viewer).
        var geom = GeomFromTriangles(
            // Front (z=+0.5), CCW from +Z
            new[] { new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f) },
            new[] { new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f), new Vector3(-0.5f, +0.5f, +0.5f) },
            // Back (z=-0.5), CCW from -Z (so from +Z viewer they're CW = back-facing)
            new[] { new Vector3(+0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, +0.5f, -0.5f) },
            new[] { new Vector3(+0.5f, -0.5f, -0.5f), new Vector3(-0.5f, +0.5f, -0.5f), new Vector3(+0.5f, +0.5f, -0.5f) }
        );
        // Recovery may pair front pairs / back pairs into quads. Either is fine
        // for this test; we care that 2 faces (one front, one back) survive
        // cull and the front are emitted.
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var vis = new bool[geom.Quads.Length];
        int n = sorter.Sort(Matrix4x4.Identity, order, vis);

        // From identity rotation, only the +Z faces are visible.
        for (int i = 0; i < geom.Quads.Length; i++)
        {
            if (geom.Quads[i].Normal.Z > 0)
                Assert.True(vis[i], $"Front face {i} (normal +Z) should be visible");
            else
                Assert.False(vis[i], $"Back face {i} (normal -Z) should be culled");
        }
        Assert.True(n > 0);
    }

    [Theory]
    [InlineData(SortAlgorithm.Bsp)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Topological)]
    public void Sort_MixedQuadAndTriangle_BothPaintInPainterOrder(SortAlgorithm algo)
    {
        // A z=0 quad behind a z=1 triangle, both front-facing. From identity
        // rotation the triangle (closer) must paint AFTER (i.e. on top of)
        // the quad (further).
        var model = new ObjModel();
        // Quad at z=0 (back)
        for (int i = 0; i < 4; i++) model.Positions.Add(default);
        model.Positions[0] = new Vector4(-1, -1, 0, 1);
        model.Positions[1] = new Vector4(+1, -1, 0, 1);
        model.Positions[2] = new Vector4(+1, +1, 0, 1);
        model.Positions[3] = new Vector4(-1, +1, 0, 1);
        model.Quads.Add(new ObjQuad(
            new ObjVertex(0, null, null), new ObjVertex(1, null, null),
            new ObjVertex(2, null, null), new ObjVertex(3, null, null),
            null, new[] { "default" }, null, 0));
        // Triangle at z=1 (front), well within the quad's screen bounds
        model.Positions.Add(new Vector4(-0.5f, -0.5f, 1, 1));
        model.Positions.Add(new Vector4(+0.5f, -0.5f, 1, 1));
        model.Positions.Add(new Vector4(0, +0.5f, 1, 1));
        model.Triangles.Add(new ObjTriangle(
            new ObjVertex(4, null, null), new ObjVertex(5, null, null), new ObjVertex(6, null, null),
            null, new[] { "default" }, null, 0));

        var geom = ObjGeometry.Build(model);
        Assert.Equal(2, geom.Quads.Length);

        // Identify which CachedQuad is the triangle.
        int triIdx = geom.Quads[0].IsTriangle ? 0 : 1;
        int quadIdx = 1 - triIdx;

        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[2];
        var vis = new bool[2];
        int n = sorter.Sort(Matrix4x4.Identity, order, vis);

        Assert.Equal(2, n);
        Assert.True(vis[triIdx]);
        Assert.True(vis[quadIdx]);
        // Painter order: back first, front last. Quad at z=0 < triangle at z=1.
        Assert.Equal(quadIdx, order[0]);
        Assert.Equal(triIdx, order[1]);
    }

    [Fact]
    public void Predecessors_TriangleVsQuadGivesCorrectPartialOrder()
    {
        // Triangle behind a quad (in model space along +Z). The quad's plane
        // (z=0, normal +Z) should report the triangle (z=-1) as a predecessor.
        var model = new ObjModel();
        // Quad at z=0
        model.Positions.Add(new Vector4(-1, -1, 0, 1));
        model.Positions.Add(new Vector4(+1, -1, 0, 1));
        model.Positions.Add(new Vector4(+1, +1, 0, 1));
        model.Positions.Add(new Vector4(-1, +1, 0, 1));
        model.Quads.Add(new ObjQuad(
            new ObjVertex(0, null, null), new ObjVertex(1, null, null),
            new ObjVertex(2, null, null), new ObjVertex(3, null, null),
            null, new[] { "default" }, null, 0));
        // Triangle at z=-1, in front of nothing
        model.Positions.Add(new Vector4(-0.3f, -0.3f, -1, 1));
        model.Positions.Add(new Vector4(+0.3f, -0.3f, -1, 1));
        model.Positions.Add(new Vector4(0, +0.3f, -1, 1));
        model.Triangles.Add(new ObjTriangle(
            new ObjVertex(4, null, null), new ObjVertex(5, null, null), new ObjVertex(6, null, null),
            null, new[] { "default" }, null, 0));

        var geom = ObjGeometry.Build(model);
        int quadIdx = geom.Quads[0].IsTriangle ? 1 : 0;
        int triIdx = 1 - quadIdx;

        var pred = geom.Predecessors;
        // Triangle must be a predecessor of the quad.
        Assert.Contains(triIdx, pred[quadIdx]);
        // The quad isn't a predecessor of the triangle (quad is in front).
        Assert.DoesNotContain(quadIdx, pred[triIdx]);
    }
}

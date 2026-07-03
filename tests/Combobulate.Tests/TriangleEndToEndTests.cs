using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Sorting;
using System.Numerics;

namespace Combobulate.Tests;

/// <summary>
/// End-to-end tests: parse triangle-bearing OBJ text → build
/// <see cref="ObjGeometry"/> → triangulate → sort. Catches integration
/// regressions across the parser/geometry/sorter boundaries.
/// </summary>
public class TriangleEndToEndTests
{
    [Fact]
    public void TriangulatedQuadObjRecoversBackToSingleQuad()
    {
        // OBJ representation of a single quad authored as two coplanar
        // triangles sharing the diagonal (0,0)→(1,1). Quad recovery should
        // fold them back into one quad — the lossless round-trip.
        var src = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            f 1 2 3
            f 1 3 4
            """;
        var r = ObjParser.Parse(src);
        Assert.True(r.Success);
        Assert.Empty(r.Model.Quads);
        Assert.Equal(2, r.Model.Triangles.Count);

        var geom = ObjGeometry.Build(r.Model);
        var c = Assert.Single(geom.Quads);
        Assert.False(c.IsTriangle); // recovered as a quad
    }

    [Fact]
    public void NonRecoverableTriangleStaysAsTriangleAndIsTriangulatedToOneTri()
    {
        var src = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            f 1 2 3
            """;
        var geom = ObjGeometry.Build(ObjParser.Parse(src).Model);
        var c = Assert.Single(geom.Quads);
        Assert.True(c.IsTriangle);

        var tris = Triangulator.Triangulate(geom.Quads);
        Assert.Single(tris);
        Assert.Equal(c.SourceIndex, tris[0].SourceQuadIndex);
    }

    [Fact]
    public void BspSorterOnTriangleOnlyMeshDoesNotThrow()
    {
        // Build a small triangle-only mesh (an icosahedral cap won't recover
        // to quads). Just verify the BSP construction doesn't crash and the
        // sort returns a permutation of visible-face indices.
        var src = """
            v  0  0  1
            v  1  0  0
            v  0  1  0
            v -1  0  0
            v  0 -1  0
            f 1 2 3
            f 1 3 4
            f 1 4 5
            f 1 5 2
            """;
        var geom = ObjGeometry.Build(ObjParser.Parse(src).Model);
        Assert.Equal(4, geom.Quads.Length);
        Assert.All(geom.Quads, q => Assert.True(q.IsTriangle));

        var sorter = new BspSorter(geom);
        var order = new int[geom.Quads.Length];
        var vis = new bool[geom.Quads.Length];
        int n = sorter.Sort(Matrix4x4.Identity, order, vis);

        // From +Z view all four +Z-half triangles are visible.
        Assert.Equal(4, n);
        for (int i = 0; i < n; i++) Assert.InRange(order[i], 0, geom.Quads.Length - 1);
    }

    [Fact]
    public void MixedQuadAndUnpairedTriangleProduceCorrectFaceCount()
    {
        var src = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            v 5 0 0
            v 6 0 0
            v 5 1 0
            f 1 2 3 4
            f 5 6 7
            """;
        var geom = ObjGeometry.Build(ObjParser.Parse(src).Model);
        Assert.Equal(2, geom.Quads.Length);
        int triCount = 0, quadCount = 0;
        foreach (var c in geom.Quads) { if (c.IsTriangle) triCount++; else quadCount++; }
        Assert.Equal(1, quadCount);
        Assert.Equal(1, triCount);
    }

    [Fact]
    public void OctahedronSampleParsesAndBuildsAsEightTriangles()
    {
        // The samples/octahedron.obj fixture is the canonical "worst case"
        // for triangle support: no two triangles are coplanar, so quad
        // recovery cannot fuse any of them. All 8 should remain as
        // triangle CachedQuads in the final geometry.
        var path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Samples", "octahedron.obj");
        var src = System.IO.File.ReadAllText(path);
        var r = ObjParser.Parse(src);
        Assert.True(r.Success, $"Parse errors: {string.Join("; ", r.Errors)}");
        Assert.Empty(r.Model.Quads);
        Assert.Equal(8, r.Model.Triangles.Count);

        var geom = ObjGeometry.Build(r.Model);
        Assert.Equal(8, geom.Quads.Length);
        Assert.All(geom.Quads, q => Assert.True(q.IsTriangle));
    }

    [Fact]
    public void TriangulatedCubeRecoversAllSixFacesToQuads()
    {
        // A cube authored as 12 triangles (2 per face). Recovery should fuse
        // each pair into 6 quads — no triangles left over.
        var src = """
            # 8 cube corners
            v -1 -1 -1
            v  1 -1 -1
            v  1  1 -1
            v -1  1 -1
            v -1 -1  1
            v  1 -1  1
            v  1  1  1
            v -1  1  1
            # +Z (front), split along 5->7 diagonal
            f 5 6 7
            f 5 7 8
            # -Z (back), split along 1->3 diagonal — wound CCW from -Z
            f 1 4 3
            f 1 3 2
            # +X (right)
            f 2 3 7
            f 2 7 6
            # -X (left)
            f 1 5 8
            f 1 8 4
            # +Y (top)
            f 4 8 7
            f 4 7 3
            # -Y (bottom)
            f 1 2 6
            f 1 6 5
            """;
        var r = ObjParser.Parse(src);
        Assert.True(r.Success);
        Assert.Equal(12, r.Model.Triangles.Count);

        var geom = ObjGeometry.Build(r.Model);
        Assert.Equal(6, geom.Quads.Length);
        Assert.All(geom.Quads, q => Assert.False(q.IsTriangle));
    }

    [Fact]
    public void TetrahedronSampleParsesAndAllFacesAreFrontFacing()
    {
        // samples/tetrahedron.obj is the visual demo asset for the sample
        // apps — the simplest possible triangle mesh. Every face must be
        // wound CCW with its normal pointing outward (away from the
        // centroid at the origin), so that from far enough away in any
        // direction at least one face is visible.
        var path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Samples", "tetrahedron.obj");
        var src = System.IO.File.ReadAllText(path);
        var r = ObjParser.Parse(src);
        Assert.True(r.Success, $"Parse errors: {string.Join("; ", r.Errors)}");
        Assert.Equal(4, r.Model.Triangles.Count);
        Assert.Empty(r.Model.Quads);

        var geom = ObjGeometry.Build(r.Model);
        Assert.Equal(4, geom.Quads.Length);
        Assert.All(geom.Quads, q => Assert.True(q.IsTriangle));

        // Centroid is the origin (vertices average to zero), so each face's
        // outward normal must have positive dot product with its face
        // centroid (which is the vector from origin to the face's centre).
        foreach (var face in geom.Quads)
        {
            float outwardness = Vector3.Dot(face.Normal, face.Centroid);
            Assert.True(outwardness > 0,
                $"Face normal {face.Normal} should point outward from centroid {face.Centroid}");
        }
    }

    [Fact]
    public void ObjModel_IsEmpty_FalseForTriangleOnlyModel()
    {
        // Regression test for the bug where Combobulate.Rebuild short-circuited
        // on `Model.Quads.Count == 0`, hiding triangle-only meshes. The IsEmpty
        // helper must return false whenever the model has any face at all.
        var triangleOnly = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            f 1 2 3
            """;
        var model = ObjParser.Parse(triangleOnly).Model;
        Assert.Empty(model.Quads);
        Assert.Single(model.Triangles);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void ObjModel_IsEmpty_TrueForCompletelyEmptyModel()
    {
        var model = new ObjModel();
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void ObjModel_IsEmpty_TrueWhenOnlyPositionsAreParsed()
    {
        // Just positions, no faces of either kind → no rendering work to do.
        var src = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            """;
        var model = ObjParser.Parse(src).Model;
        Assert.True(model.IsEmpty);
    }
}

using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

public class TriangulatorTests
{
    [Fact]
    public void Triangulate_QuadProducesTwoTrianglesAlongV0V2Diagonal()
    {
        var geom = TestGeometries.FrontAndBackQuads();
        var tris = Triangulator.Triangulate(geom.Quads).ToList();
        Assert.Equal(4, tris.Count); // 2 quads * 2 triangles
        // First two triangles belong to source quad 0.
        Assert.Equal(0, tris[0].SourceQuadIndex);
        Assert.Equal(0, tris[1].SourceQuadIndex);
        Assert.Equal(1, tris[2].SourceQuadIndex);
        Assert.Equal(1, tris[3].SourceQuadIndex);
    }

    [Fact]
    public void Triangulate_Cube_Produces12Triangles()
    {
        var cube = TestGeometries.UnitCube();
        var tris = Triangulator.Triangulate(cube.Quads).ToList();
        Assert.Equal(12, tris.Count);
        // Every face's two triangles share the source index.
        for (int q = 0; q < 6; q++)
        {
            Assert.Equal(q, tris[q * 2 + 0].SourceQuadIndex);
            Assert.Equal(q, tris[q * 2 + 1].SourceQuadIndex);
        }
    }

    [Fact]
    public void Triangulate_OutwardNormalsMatchFaceNormal()
    {
        var cube = TestGeometries.UnitCube();
        var tris = Triangulator.Triangulate(cube.Quads).ToList();
        var expected = new[]
        {
            new Vector3(0, 0, +1),
            new Vector3(0, 0, -1),
            new Vector3(-1, 0, 0),
            new Vector3(+1, 0, 0),
            new Vector3(0, +1, 0),
            new Vector3(0, -1, 0),
        };
        for (int q = 0; q < 6; q++)
        {
            for (int t = 0; t < 2; t++)
            {
                var n = tris[q * 2 + t].Plane.Normal;
                Assert.Equal(expected[q].X, n.X, 3);
                Assert.Equal(expected[q].Y, n.Y, 3);
                Assert.Equal(expected[q].Z, n.Z, 3);
            }
        }
    }
}

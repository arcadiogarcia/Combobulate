using Combobulate.Caching;
using Combobulate.Parsing;
using System.Numerics;

namespace Combobulate.Tests.Caching;

/// <summary>
/// Tests for <see cref="ObjGeometry.Build"/> with triangle faces — verifying
/// the triangle path produces correct CachedQuads (IsTriangle, centroid,
/// normal, V3=V2, Uv3=Uv2) and that the quad-recovery preprocess fires on
/// adjacent triangles in the model.
/// </summary>
public class ObjGeometryTriangleTests
{
    private static ObjModel ModelFromQuadsAndTriangles(Vector3[][]? quads, Vector3[][]? triangles)
    {
        var model = new ObjModel();
        if (quads != null)
        {
            foreach (var q in quads)
            {
                int baseIdx = model.Positions.Count;
                foreach (var v in q) model.Positions.Add(new Vector4(v.X, v.Y, v.Z, 1f));
                model.Quads.Add(new ObjQuad(
                    new ObjVertex(baseIdx + 0, null, null),
                    new ObjVertex(baseIdx + 1, null, null),
                    new ObjVertex(baseIdx + 2, null, null),
                    new ObjVertex(baseIdx + 3, null, null),
                    null, new[] { "default" }, null, 0));
            }
        }
        if (triangles != null)
        {
            foreach (var t in triangles)
            {
                int baseIdx = model.Positions.Count;
                foreach (var v in t) model.Positions.Add(new Vector4(v.X, v.Y, v.Z, 1f));
                model.Triangles.Add(new ObjTriangle(
                    new ObjVertex(baseIdx + 0, null, null),
                    new ObjVertex(baseIdx + 1, null, null),
                    new ObjVertex(baseIdx + 2, null, null),
                    null, new[] { "default" }, null, 0));
            }
        }
        return model;
    }

    [Fact]
    public void Build_TriangleProducesCachedQuadWithIsTriangleFlag()
    {
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
        });
        var geom = ObjGeometry.Build(model);

        var c = Assert.Single(geom.Quads);
        Assert.True(c.IsTriangle);
        Assert.Equal(c.V2, c.V3);
        Assert.Equal(c.Uv2, c.Uv3);
    }

    [Fact]
    public void Build_TriangleCentroidIsCornerAverage()
    {
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0), new Vector3(0, 3, 0) },
        });
        var geom = ObjGeometry.Build(model);
        var c = geom.Quads[0];
        // Center is the average of the three corners = (1,1,0). After centering:
        //   V0 - center = (-1,-1,0)
        //   V1 - center = ( 2,-1,0)
        //   V2 - center = (-1, 2,0)
        // Triangle centroid = (V0+V1+V2)/3 = (0,0,0).
        Assert.True((c.Centroid - Vector3.Zero).Length() < 1e-5f);
    }

    [Fact]
    public void Build_TriangleNormalPointsByCrossOfFirstTwoEdges()
    {
        // CCW triangle in the +Z plane: normal should be +Z.
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
        });
        var c = ObjGeometry.Build(model).Quads[0];
        Assert.True(Vector3.Dot(c.Normal, new Vector3(0, 0, 1)) > 0.999f);
    }

    [Fact]
    public void Build_DegenerateTriangleIsDropped()
    {
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0) }, // colinear
        });
        var geom = ObjGeometry.Build(model);
        Assert.Empty(geom.Quads);
    }

    [Fact]
    public void Build_RecoversTwoAdjacentTrianglesToQuad()
    {
        // Two coplanar triangles sharing the diagonal (0,0)-(1,1) — exactly the
        // pattern a quad-export-then-triangulate would produce. Quad recovery
        // should fold them back into a single quad.
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0) },
            new[] { new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
        });
        var geom = ObjGeometry.Build(model);

        var c = Assert.Single(geom.Quads);
        Assert.False(c.IsTriangle);
    }

    [Fact]
    public void Build_LeavesUnpairedTrianglesAsTriangles()
    {
        // A single triangle: nothing to pair with.
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
        });
        var c = Assert.Single(ObjGeometry.Build(model).Quads);
        Assert.True(c.IsTriangle);
    }

    [Fact]
    public void Build_MixedQuadsAndTrianglesCoexist()
    {
        // One quad + one isolated triangle. The geometry should contain both.
        var model = ModelFromQuadsAndTriangles(
            new[]
            {
                new[]
                {
                    new Vector3(0, 0, 0),
                    new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0),
                    new Vector3(0, 1, 0),
                },
            },
            new[]
            {
                new[] { new Vector3(3, 0, 0), new Vector3(4, 0, 0), new Vector3(3, 1, 0) },
            });
        var geom = ObjGeometry.Build(model);
        Assert.Equal(2, geom.Quads.Length);
        Assert.Contains(geom.Quads, q => !q.IsTriangle);
        Assert.Contains(geom.Quads, q => q.IsTriangle);
    }

    [Fact]
    public void Build_TriangleCenterIncludesTrianglePositions()
    {
        // Centroid (model.Center) must account for triangle positions, not
        // just quad positions. Place a triangle far off-origin; the centered
        // CachedQuad coords should sum to roughly zero.
        var model = ModelFromQuadsAndTriangles(null, new[]
        {
            new[] { new Vector3(10, 10, 10), new Vector3(11, 10, 10), new Vector3(10, 11, 10) },
        });
        var geom = ObjGeometry.Build(model);
        // ComputeCenter averages each triangle's three vertices:
        //   ((10,10,10)+(11,10,10)+(10,11,10))/3 = (31/3, 31/3, 10)
        var expectedCenter = new Vector3(31f / 3f, 31f / 3f, 10f);
        Assert.True((geom.Center - expectedCenter).Length() < 1e-4f);
        // After centering, the centroid of the CachedQuad should be near zero.
        Assert.True(geom.Quads[0].Centroid.Length() < 1e-4f);
    }

    [Fact]
    public void Build_TriangleWithInvalidIndexIsSkipped()
    {
        var model = new ObjModel();
        model.Positions.Add(new Vector4(0, 0, 0, 1));
        model.Positions.Add(new Vector4(1, 0, 0, 1));
        // Triangle references position index 99 (out of range).
        model.Triangles.Add(new ObjTriangle(
            new ObjVertex(0, null, null),
            new ObjVertex(1, null, null),
            new ObjVertex(99, null, null),
            null, new[] { "default" }, null, 0));

        var geom = ObjGeometry.Build(model);
        Assert.Empty(geom.Quads);
    }

    [Fact]
    public void CoplanarGroups_HasOneEntryPerQuad()
    {
        // Two coplanar quads in the +Z plane + one quad in the +Y plane.
        var model = ModelFromQuadsAndTriangles(
            new[]
            {
                new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                },
                new[]
                {
                    new Vector3(2, 0, 0), new Vector3(3, 0, 0),
                    new Vector3(3, 1, 0), new Vector3(2, 1, 0),
                },
                new[]
                {
                    new Vector3(0, 5, 0), new Vector3(1, 5, 0),
                    new Vector3(1, 5, 1), new Vector3(0, 5, 1),
                },
            },
            null);
        var geom = ObjGeometry.Build(model);

        var groups = geom.CoplanarGroups;
        Assert.Equal(geom.Quads.Length, groups.Length);
        // The two +Z-plane quads share a group; the +Y-plane quad is its own.
        Assert.Equal(groups[0], groups[1]);
        Assert.NotEqual(groups[0], groups[2]);
    }

    [Fact]
    public void CoplanarGroups_IsLazyAndCachedAcrossReads()
    {
        var model = ModelFromQuadsAndTriangles(
            new[]
            {
                new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                },
            },
            null);
        var geom = ObjGeometry.Build(model);

        var first = geom.CoplanarGroups;
        var second = geom.CoplanarGroups;
        Assert.Same(first, second);
    }
}

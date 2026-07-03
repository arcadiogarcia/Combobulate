namespace Combobulate.Tests;

/// <summary>
/// Triangle face parsing tests — mirror of <see cref="ObjParserFaceTests"/>
/// for the <c>f a b c</c> three-vertex form. Triangles are first-class faces
/// since the Phase 1 triangle-rendering work; previously the parser rejected
/// them as unsupported arity.
/// </summary>
public class ObjParserTriangleTests
{
    private const string Cube4Verts = """
        v 0 0 0
        v 1 0 0
        v 1 1 0
        v 0 1 0
        """;

    [Fact]
    public void ParsesTriangleWithPositionsOnly()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 3");

        Assert.True(r.Success);
        Assert.Empty(r.Model.Quads);
        var t = Assert.Single(r.Model.Triangles);
        Assert.Equal(0, t.V0.PositionIndex);
        Assert.Equal(1, t.V1.PositionIndex);
        Assert.Equal(2, t.V2.PositionIndex);
        Assert.Null(t.V0.TexCoordIndex);
        Assert.Null(t.V0.NormalIndex);
    }

    [Fact]
    public void ParsesTriangleWithPositionAndTexCoord()
    {
        var src = Cube4Verts + """

            vt 0 0
            vt 1 0
            vt 0 1
            f 1/1 2/2 3/3
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var t = r.Model.Triangles[0];
        Assert.Equal(0, t.V0.TexCoordIndex);
        Assert.Equal(1, t.V1.TexCoordIndex);
        Assert.Equal(2, t.V2.TexCoordIndex);
        Assert.Null(t.V0.NormalIndex);
    }

    [Fact]
    public void ParsesTriangleWithPositionAndNormal()
    {
        var src = Cube4Verts + """

            vn 0 0 1
            f 1//1 2//1 3//1
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var t = r.Model.Triangles[0];
        Assert.Null(t.V0.TexCoordIndex);
        Assert.Equal(0, t.V0.NormalIndex);
        Assert.Equal(0, t.V2.NormalIndex);
    }

    [Fact]
    public void ParsesTriangleWithFullVertexFormat()
    {
        var src = Cube4Verts + """

            vt 0 0
            vt 1 0
            vt 0 1
            vn 0 0 1
            f 1/1/1 2/2/1 3/3/1
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var t = r.Model.Triangles[0];
        Assert.Equal(0, t.V0.PositionIndex);
        Assert.Equal(0, t.V0.TexCoordIndex);
        Assert.Equal(0, t.V0.NormalIndex);
    }

    [Fact]
    public void AcceptsMixedFaceVertexFormatsOnSameTriangle()
    {
        var src = Cube4Verts + """

            vt 0 0
            vn 0 0 1
            f 1 2/1 3//1
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var t = r.Model.Triangles[0];
        Assert.Null(t.V0.TexCoordIndex); Assert.Null(t.V0.NormalIndex);
        Assert.Equal(0, t.V1.TexCoordIndex); Assert.Null(t.V1.NormalIndex);
        Assert.Null(t.V2.TexCoordIndex); Assert.Equal(0, t.V2.NormalIndex);
    }

    [Fact]
    public void NegativeIndicesResolveForTriangle()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf -3 -2 -1");
        Assert.True(r.Success);
        var t = r.Model.Triangles[0];
        Assert.Equal(1, t.V0.PositionIndex);
        Assert.Equal(2, t.V1.PositionIndex);
        Assert.Equal(3, t.V2.PositionIndex);
    }

    [Fact]
    public void TrianglesAndQuadsCanCoexistInSameFile()
    {
        var src = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            v 2 0 0
            f 1 2 3 4
            f 1 2 5
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        Assert.Single(r.Model.Quads);
        Assert.Single(r.Model.Triangles);
        // Source order preserved within each kind:
        Assert.Equal(0, r.Model.Quads[0].V0.PositionIndex);
        Assert.Equal(0, r.Model.Triangles[0].V0.PositionIndex);
        Assert.Equal(4, r.Model.Triangles[0].V2.PositionIndex);
    }

    [Fact]
    public void TriangleContextIsCaptured()
    {
        var src = Cube4Verts + """

            o cube
            g topcap underside
            usemtl green
            s 1
            f 1 2 3
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var t = r.Model.Triangles[0];
        Assert.Equal("cube", t.ObjectName);
        Assert.Equal(new[] { "topcap", "underside" }, t.Groups);
        Assert.Equal("green", t.Material);
        Assert.Equal(1, t.SmoothingGroup);
    }

    [Fact]
    public void OutOfRangePositionIndexInTriangleProducesError()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 99");
        Assert.False(r.Success);
        Assert.Empty(r.Model.Triangles);
        Assert.Equal(ObjParseErrorKind.InvalidIndex, r.Errors[0].Kind);
    }

    [Fact]
    public void ZeroIndexInTriangleIsInvalid()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 0 1 2");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.InvalidIndex, r.Errors[0].Kind);
    }

    [Fact]
    public void MalformedTriangleVertexReferenceIsRejected()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1/1/1/1 2 3");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.InvalidVertexReference, r.Errors[0].Kind);
        Assert.Empty(r.Model.Triangles);
    }
}

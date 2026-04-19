namespace Combobulate.Tests;

public class ObjParserFaceTests
{
    private const string Cube4Verts = """
        v 0 0 0
        v 1 0 0
        v 1 1 0
        v 0 1 0
        """;

    [Fact]
    public void ParsesQuadWithPositionsOnly()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 3 4");

        Assert.True(r.Success);
        var q = Assert.Single(r.Model.Quads);
        Assert.Equal(0, q.V0.PositionIndex);
        Assert.Equal(1, q.V1.PositionIndex);
        Assert.Equal(2, q.V2.PositionIndex);
        Assert.Equal(3, q.V3.PositionIndex);
        Assert.Null(q.V0.TexCoordIndex);
        Assert.Null(q.V0.NormalIndex);
    }

    [Fact]
    public void ParsesQuadWithPositionAndTexCoord()
    {
        var src = Cube4Verts + """

            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            f 1/1 2/2 3/3 4/4
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var q = r.Model.Quads[0];
        Assert.Equal(0, q.V0.TexCoordIndex);
        Assert.Equal(3, q.V3.TexCoordIndex);
        Assert.Null(q.V0.NormalIndex);
    }

    [Fact]
    public void ParsesQuadWithPositionAndNormal()
    {
        var src = Cube4Verts + """

            vn 0 0 1
            f 1//1 2//1 3//1 4//1
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var q = r.Model.Quads[0];
        Assert.Null(q.V0.TexCoordIndex);
        Assert.Equal(0, q.V0.NormalIndex);
        Assert.Equal(0, q.V3.NormalIndex);
    }

    [Fact]
    public void ParsesQuadWithFullVertexFormat()
    {
        var src = Cube4Verts + """

            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            vn 0 0 1
            f 1/1/1 2/2/1 3/3/1 4/4/1
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var q = r.Model.Quads[0];
        Assert.Equal(0, q.V0.PositionIndex);
        Assert.Equal(0, q.V0.TexCoordIndex);
        Assert.Equal(0, q.V0.NormalIndex);
    }

    [Fact]
    public void AcceptsMixedFaceVertexFormatsOnSameFace()
    {
        // Real-world OBJs sometimes mix v, v/vt, v//vn, v/vt/vn within one face.
        var src = Cube4Verts + """

            vt 0 0
            vn 0 0 1
            f 1 2/1 3//1 4/1/1
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var q = r.Model.Quads[0];
        Assert.Null(q.V0.TexCoordIndex); Assert.Null(q.V0.NormalIndex);
        Assert.Equal(0, q.V1.TexCoordIndex); Assert.Null(q.V1.NormalIndex);
        Assert.Null(q.V2.TexCoordIndex); Assert.Equal(0, q.V2.NormalIndex);
        Assert.Equal(0, q.V3.TexCoordIndex); Assert.Equal(0, q.V3.NormalIndex);
    }

    [Fact]
    public void NegativeIndicesResolveRelativeToCurrentArraySize()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf -4 -3 -2 -1");

        Assert.True(r.Success);
        var q = r.Model.Quads[0];
        Assert.Equal(0, q.V0.PositionIndex);
        Assert.Equal(1, q.V1.PositionIndex);
        Assert.Equal(2, q.V2.PositionIndex);
        Assert.Equal(3, q.V3.PositionIndex);
    }

    [Fact]
    public void NegativeIndicesResolveAgainstSizeAtFaceTime()
    {
        // After face is parsed, we add more vertices. Negative resolution must
        // be relative to the count *at the moment* of the face declaration.
        var src = """
            v 0 0 0
            v 1 0 0
            v 2 0 0
            v 3 0 0
            f -1 -2 -3 -4
            v 9 9 9
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        var q = r.Model.Quads[0];
        Assert.Equal(3, q.V0.PositionIndex);
        Assert.Equal(0, q.V3.PositionIndex);
    }

    [Theory]
    [InlineData("f 1 2 3", 3)]
    [InlineData("f 1 2 3 4 5", 5)]
    [InlineData("f 1 2 3 4 5 6 7 8", 8)]
    public void NonQuadFaceProducesUnsupportedFaceArityError(string face, int arity)
    {
        var r = ObjParser.Parse(Cube4Verts + "\n" + face);

        Assert.False(r.Success);
        Assert.Empty(r.Model.Quads);
        var err = Assert.Single(r.Errors);
        Assert.Equal(ObjParseErrorKind.UnsupportedFaceArity, err.Kind);
        Assert.Contains(arity.ToString(), err.Message);
    }

    [Fact]
    public void ContinuesParsingAfterUnsupportedFaceArity()
    {
        var src = Cube4Verts + """

            f 1 2 3
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.False(r.Success);
        Assert.Single(r.Errors);
        Assert.Single(r.Model.Quads); // Quad after the bad triangle still parsed.
    }

    [Fact]
    public void OutOfRangePositionIndexProducesErrorAndSkipsFace()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 3 99");

        Assert.False(r.Success);
        Assert.Empty(r.Model.Quads);
        var err = Assert.Single(r.Errors);
        Assert.Equal(ObjParseErrorKind.InvalidIndex, err.Kind);
    }

    [Fact]
    public void ZeroIndexIsInvalid()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 0 1 2 3");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.InvalidIndex, r.Errors[0].Kind);
    }

    [Fact]
    public void OutOfRangeNegativeIndexProducesError()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf -5 -4 -3 -2");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.InvalidIndex, r.Errors[0].Kind);
    }

    [Fact]
    public void MalformedVertexReferenceWithTooManySlashesIsRejected()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1/1/1/1 2 3 4");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.InvalidVertexReference, r.Errors[0].Kind);
        Assert.Empty(r.Model.Quads);
    }

    [Fact]
    public void NonNumericVertexIndexIsRejected()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf foo 2 3 4");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.InvalidIndex, r.Errors[0].Kind);
    }
}

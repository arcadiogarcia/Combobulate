namespace Combobulate.Tests;

public class ObjParserVertexTests
{
    [Fact]
    public void ParsesSinglePosition()
    {
        var r = ObjParser.Parse("v 1 2 3");
        Assert.True(r.Success);
        Assert.Equal(new Vector4(1, 2, 3, 1), Assert.Single(r.Model.Positions));
    }

    [Fact]
    public void ParsesPositionWithExplicitW()
    {
        var r = ObjParser.Parse("v 1 2 3 0.5");
        Assert.True(r.Success);
        Assert.Equal(new Vector4(1, 2, 3, 0.5f), r.Model.Positions[0]);
    }

    [Fact]
    public void ParsesNegativeAndDecimalCoordinates()
    {
        var r = ObjParser.Parse("v -1.5 0.0 +2.25");
        Assert.True(r.Success);
        Assert.Equal(new Vector4(-1.5f, 0f, 2.25f, 1f), r.Model.Positions[0]);
    }

    [Fact]
    public void ParsesScientificNotation()
    {
        var r = ObjParser.Parse("v 1e2 -2.5e-1 3E0");
        Assert.True(r.Success);
        Assert.Equal(new Vector4(100f, -0.25f, 3f, 1f), r.Model.Positions[0]);
    }

    [Fact]
    public void ParsesMultipleVerticesAcrossLines()
    {
        var r = ObjParser.Parse("v 0 0 0\nv 1 1 1\nv 2 2 2");
        Assert.True(r.Success);
        Assert.Equal(3, r.Model.Positions.Count);
        Assert.Equal(new Vector4(2, 2, 2, 1), r.Model.Positions[2]);
    }

    [Fact]
    public void EmitsErrorOnMissingComponents()
    {
        var r = ObjParser.Parse("v 1 2");
        Assert.False(r.Success);
        Assert.Empty(r.Model.Positions);
        Assert.Equal(ObjParseErrorKind.MissingArgument, r.Errors[0].Kind);
        Assert.Equal(1, r.Errors[0].LineNumber);
    }

    [Fact]
    public void EmitsErrorOnNonNumericComponent()
    {
        var r = ObjParser.Parse("v 1 oops 3");
        Assert.False(r.Success);
        Assert.Empty(r.Model.Positions);
        Assert.Equal(ObjParseErrorKind.InvalidNumber, r.Errors[0].Kind);
    }

    [Fact]
    public void ParsesTexCoordWithDefaults()
    {
        var r = ObjParser.Parse("vt 0.5");
        Assert.True(r.Success);
        Assert.Equal(new Vector3(0.5f, 0f, 0f), r.Model.TexCoords[0]);
    }

    [Fact]
    public void ParsesTexCoordWithAllComponents()
    {
        var r = ObjParser.Parse("vt 0.1 0.2 0.3");
        Assert.True(r.Success);
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), r.Model.TexCoords[0]);
    }

    [Fact]
    public void ParsesNormal()
    {
        var r = ObjParser.Parse("vn 0 1 0");
        Assert.True(r.Success);
        Assert.Equal(new Vector3(0, 1, 0), r.Model.Normals[0]);
    }

    [Fact]
    public void DoesNotNormalizeNormals()
    {
        // Spec: should be normalized, but parser must not assume.
        var r = ObjParser.Parse("vn 2 0 0");
        Assert.True(r.Success);
        Assert.Equal(new Vector3(2, 0, 0), r.Model.Normals[0]);
    }

    [Fact]
    public void EmitsErrorOnNormalMissingComponent()
    {
        var r = ObjParser.Parse("vn 1 2");
        Assert.False(r.Success);
        Assert.Equal(ObjParseErrorKind.MissingArgument, r.Errors[0].Kind);
    }
}

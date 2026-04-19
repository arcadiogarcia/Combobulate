namespace Combobulate.Tests;

public class ObjParserLenienceTests
{
    [Fact]
    public void IgnoresBlankLines()
    {
        var r = ObjParser.Parse("\n\nv 1 2 3\n\n\nv 4 5 6\n");
        Assert.True(r.Success);
        Assert.Equal(2, r.Model.Positions.Count);
    }

    [Fact]
    public void IgnoresLineComments()
    {
        var r = ObjParser.Parse("# header comment\nv 1 2 3 # trailing comment\n");
        Assert.True(r.Success);
        Assert.Equal(new Vector4(1, 2, 3, 1), r.Model.Positions[0]);
    }

    [Fact]
    public void HandlesLeadingAndTrailingWhitespaceAndTabs()
    {
        var r = ObjParser.Parse("   \t v\t1   2 \t  3   \n");
        Assert.True(r.Success);
        Assert.Equal(new Vector4(1, 2, 3, 1), r.Model.Positions[0]);
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        var r = ObjParser.Parse("v 1 2 3\r\nv 4 5 6\r\n");
        Assert.True(r.Success);
        Assert.Equal(2, r.Model.Positions.Count);
    }

    [Fact]
    public void IgnoresUnknownDirectives()
    {
        var src = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            wibblywobble whatever 1 2 3
            curv 0 1 1 2 3 4
            surf 0 1 0 1 1 2 3 4
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);
        Assert.True(r.Success);
        Assert.Single(r.Model.Quads);
    }

    [Fact]
    public void IgnoresLineAndPointPrimitives()
    {
        var src = """
            v 0 0 0
            v 1 0 0
            l 1 2
            p 1
            """;
        var r = ObjParser.Parse(src);
        Assert.True(r.Success);
    }

    [Fact]
    public void EmptyInputProducesEmptyModel()
    {
        var r = ObjParser.Parse(string.Empty);
        Assert.True(r.Success);
        Assert.Empty(r.Model.Positions);
        Assert.Empty(r.Model.Quads);
    }

    [Fact]
    public void OnlyCommentsProducesEmptyModel()
    {
        var r = ObjParser.Parse("# nothing\n# but\n# comments\n");
        Assert.True(r.Success);
        Assert.Empty(r.Model.Positions);
    }

    [Fact]
    public void DuplicateDefinitionsArePreservedInOrder()
    {
        var r = ObjParser.Parse("v 1 0 0\nv 1 0 0\n");
        Assert.True(r.Success);
        Assert.Equal(2, r.Model.Positions.Count);
    }

    [Fact]
    public void LineNumbersInErrorsAreOneBased()
    {
        var src = "v 0 0 0\nv 1 0 0\nv 2 0 0\nv bad 0 0\n";
        var r = ObjParser.Parse(src);
        Assert.False(r.Success);
        Assert.Equal(4, r.Errors[0].LineNumber);
    }

    [Fact]
    public void ErrorsAccumulateAndDoNotStopParsing()
    {
        var src = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            f 1 2 3
            v not a number
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.Equal(2, r.Errors.Count);
        Assert.Single(r.Model.Quads); // The valid quad after both errors still parsed.
    }

    [Fact]
    public void ParseRejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(() => ObjParser.Parse((string)null!));
    }

    [Fact]
    public void ParsesNontrivialFileEndToEnd()
    {
        // Two quads, one with a material/group/smoothing context, one without.
        var src = """
            # cube-ish fragment
            mtllib cube.mtl

            v -1 -1 0
            v  1 -1 0
            v  1  1 0
            v -1  1 0
            v -1 -1 1
            v  1 -1 1
            v  1  1 1
            v -1  1 1

            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1

            vn 0 0 -1
            vn 0 0  1

            o cube
            g front
            usemtl red
            s 1
            f 1/1/1 2/2/1 3/3/1 4/4/1

            g back
            usemtl blue
            s off
            f 5/1/2 6/2/2 7/3/2 8/4/2
            """;

        var r = ObjParser.Parse(src);

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal(8, r.Model.Positions.Count);
        Assert.Equal(4, r.Model.TexCoords.Count);
        Assert.Equal(2, r.Model.Normals.Count);
        Assert.Equal(2, r.Model.Quads.Count);
        Assert.Equal(new[] { "cube.mtl" }, r.Model.MaterialLibraries);

        var front = r.Model.Quads[0];
        Assert.Equal("cube", front.ObjectName);
        Assert.Equal(new[] { "front" }, front.Groups);
        Assert.Equal("red", front.Material);
        Assert.Equal(1, front.SmoothingGroup);
        Assert.Equal(0, front.V0.PositionIndex);
        Assert.Equal(0, front.V0.NormalIndex);

        var back = r.Model.Quads[1];
        Assert.Equal(new[] { "back" }, back.Groups);
        Assert.Equal("blue", back.Material);
        Assert.Equal(0, back.SmoothingGroup);
        Assert.Equal(4, back.V0.PositionIndex);
        Assert.Equal(1, back.V0.NormalIndex);
    }
}

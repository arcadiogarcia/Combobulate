namespace Combobulate.Tests;

public class ObjParserContextTests
{
    private const string Cube4Verts = """
        v 0 0 0
        v 1 0 0
        v 1 1 0
        v 0 1 0
        """;

    [Fact]
    public void DefaultGroupAppliedWhenNoGDirectiveSeen()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 3 4");
        Assert.True(r.Success);
        Assert.Equal(new[] { "default" }, r.Model.Quads[0].Groups);
    }

    [Fact]
    public void GroupAppliesToSubsequentFaces()
    {
        var src = Cube4Verts + """

            g first
            f 1 2 3 4
            g second
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        Assert.Equal(new[] { "first" }, r.Model.Quads[0].Groups);
        Assert.Equal(new[] { "second" }, r.Model.Quads[1].Groups);
    }

    [Fact]
    public void MultipleGroupNamesAreSupported()
    {
        var src = Cube4Verts + """

            g a b c
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.Equal(new[] { "a", "b", "c" }, r.Model.Quads[0].Groups);
    }

    [Fact]
    public void GWithNoArgsResetsToDefault()
    {
        var src = Cube4Verts + """

            g named
            f 1 2 3 4
            g
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.Equal(new[] { "named" }, r.Model.Quads[0].Groups);
        Assert.Equal(new[] { "default" }, r.Model.Quads[1].Groups);
    }

    [Fact]
    public void ObjectNameAppliesToSubsequentFaces()
    {
        var src = Cube4Verts + """

            o cube
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.Equal("cube", r.Model.Quads[0].ObjectName);
    }

    [Fact]
    public void UseMtlAppliesToSubsequentFaces()
    {
        var src = Cube4Verts + """

            usemtl red
            f 1 2 3 4
            usemtl blue
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.Equal("red", r.Model.Quads[0].Material);
        Assert.Equal("blue", r.Model.Quads[1].Material);
    }

    [Fact]
    public void MaterialDefaultsToNullBeforeFirstUseMtl()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 3 4");
        Assert.Null(r.Model.Quads[0].Material);
    }

    [Fact]
    public void MtllibCollectsAllReferencedFiles()
    {
        var src = "mtllib a.mtl b.mtl\nmtllib c.mtl";
        var r = ObjParser.Parse(src);
        Assert.Equal(new[] { "a.mtl", "b.mtl", "c.mtl" }, r.Model.MaterialLibraries);
    }

    [Fact]
    public void SmoothingGroupIsTrackedPerFace()
    {
        var src = Cube4Verts + """

            s 1
            f 1 2 3 4
            s off
            f 1 2 3 4
            s 5
            f 1 2 3 4
            s 0
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.True(r.Success);
        Assert.Equal(1, r.Model.Quads[0].SmoothingGroup);
        Assert.Equal(0, r.Model.Quads[1].SmoothingGroup);
        Assert.Equal(5, r.Model.Quads[2].SmoothingGroup);
        Assert.Equal(0, r.Model.Quads[3].SmoothingGroup);
    }

    [Fact]
    public void SmoothingGroupDefaultsToZero()
    {
        var r = ObjParser.Parse(Cube4Verts + "\nf 1 2 3 4");
        Assert.Equal(0, r.Model.Quads[0].SmoothingGroup);
    }

    [Fact]
    public void GroupListIsSnapshotPerFace()
    {
        // Changing the active group later must not mutate prior face's recorded groups.
        var src = Cube4Verts + """

            g first
            f 1 2 3 4
            g second
            f 1 2 3 4
            """;
        var r = ObjParser.Parse(src);

        Assert.Equal(new[] { "first" }, r.Model.Quads[0].Groups);
        Assert.NotSame(r.Model.Quads[0].Groups, r.Model.Quads[1].Groups);
    }
}

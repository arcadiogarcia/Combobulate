namespace Combobulate.Tests;

public class MtlParserTests
{
    [Fact]
    public void ParsesKdOnly()
    {
        var r = MtlParser.Parse("newmtl red\nKd 1 0 0\n");
        Assert.True(r.Success);
        var mat = r.Materials["red"];
        Assert.Equal(new Vector3(1, 0, 0), mat.DiffuseColor!.Value);
        Assert.Null(mat.DiffuseTexture);
    }

    [Fact]
    public void ParsesGreyscaleKd()
    {
        var r = MtlParser.Parse("newmtl grey\nKd 0.5\n");
        Assert.True(r.Success);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), r.Materials["grey"].DiffuseColor!.Value);
    }

    [Fact]
    public void ParsesMapKdOnly()
    {
        var r = MtlParser.Parse("newmtl m\nmap_Kd cover.png\n");
        Assert.True(r.Success);
        var tex = r.Materials["m"].DiffuseTexture!;
        Assert.Equal("cover.png", tex.Path);
        Assert.Equal(Vector2.One, tex.Scale);
        Assert.Equal(Vector2.Zero, tex.Offset);
        Assert.False(tex.Clamp);
    }

    [Fact]
    public void ParsesMapKdWithScaleOffsetAndClamp()
    {
        var r = MtlParser.Parse("newmtl m\nmap_Kd -s 2 3 -o 0.1 0.2 -clamp on tex.png\n");
        Assert.True(r.Success);
        var tex = r.Materials["m"].DiffuseTexture!;
        Assert.Equal("tex.png", tex.Path);
        Assert.Equal(new Vector2(2, 3), tex.Scale);
        Assert.Equal(new Vector2(0.1f, 0.2f), tex.Offset);
        Assert.True(tex.Clamp);
    }

    [Fact]
    public void ParsesKdAndMapKdTogether()
    {
        var r = MtlParser.Parse("newmtl m\nKd 0.4 0.2 0.1\nmap_Kd cover.png\n");
        Assert.True(r.Success);
        var mat = r.Materials["m"];
        Assert.NotNull(mat.DiffuseColor);
        Assert.Equal("cover.png", mat.DiffuseTexture!.Path);
    }

    [Fact]
    public void ParsesMultipleMaterialsInOneFile()
    {
        var text = "newmtl a\nKd 1 0 0\n\nnewmtl b\nKd 0 1 0\nmap_Kd b.png\n";
        var r = MtlParser.Parse(text);
        Assert.True(r.Success);
        Assert.Equal(2, r.Materials.Count);
        Assert.Equal(new Vector3(1, 0, 0), r.Materials["a"].DiffuseColor!.Value);
        Assert.Equal("b.png", r.Materials["b"].DiffuseTexture!.Path);
    }

    [Fact]
    public void OrphanedDirectiveBeforeNewmtlReportsError()
    {
        var r = MtlParser.Parse("Kd 1 0 0\nnewmtl m\n");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Kind == MtlParseErrorKind.OrphanedDirective);
        Assert.True(r.Materials.ContainsKey("m"));
    }

    [Fact]
    public void MissingNewmtlNameReportsError()
    {
        var r = MtlParser.Parse("newmtl\n");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Kind == MtlParseErrorKind.MissingArgument);
    }

    [Fact]
    public void InvalidNumericValueReportsError()
    {
        var r = MtlParser.Parse("newmtl m\nKd red green blue\n");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Kind == MtlParseErrorKind.InvalidNumber);
    }

    [Fact]
    public void TrInvertsToOpacity()
    {
        var r = MtlParser.Parse("newmtl m\nTr 0.25\n");
        Assert.True(r.Success);
        Assert.Equal(0.75f, r.Materials["m"].Opacity, 5);
    }

    [Fact]
    public void DPreservesAsOpacity()
    {
        var r = MtlParser.Parse("newmtl m\nd 0.5\n");
        Assert.True(r.Success);
        Assert.Equal(0.5f, r.Materials["m"].Opacity, 5);
    }

    [Fact]
    public void IllumIsParsed()
    {
        var r = MtlParser.Parse("newmtl m\nillum 2\n");
        Assert.True(r.Success);
        Assert.Equal(2, r.Materials["m"].IlluminationModel);
    }

    [Fact]
    public void NsAndNiAreParsed()
    {
        var r = MtlParser.Parse("newmtl m\nNs 12\nNi 1.45\n");
        Assert.True(r.Success);
        Assert.Equal(12f, r.Materials["m"].SpecularExponent);
        Assert.Equal(1.45f, r.Materials["m"].OpticalDensity);
    }

    [Fact]
    public void UnknownKeywordsAreCaptured()
    {
        var r = MtlParser.Parse("newmtl m\nfresnel 1.5\nrefl_map foo.png\n");
        Assert.True(r.Success);
        var extras = r.Materials["m"].ExtraKeywords;
        Assert.Equal("1.5", extras["fresnel"]);
        Assert.Equal("foo.png", extras["refl_map"]);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var text = "# top comment\n\nnewmtl m  # inline\nKd 1 1 1\n# trailing\n";
        var r = MtlParser.Parse(text);
        Assert.True(r.Success);
        Assert.Single(r.Materials);
    }

    [Fact]
    public void BumpAliasesPopulateBumpTexture()
    {
        var r1 = MtlParser.Parse("newmtl m\nbump n.png\n");
        var r2 = MtlParser.Parse("newmtl m\nmap_Bump n.png\n");
        Assert.Equal("n.png", r1.Materials["m"].BumpTexture!.Path);
        Assert.Equal("n.png", r2.Materials["m"].BumpTexture!.Path);
    }

    [Fact]
    public void MapWithMmExtraOptionConsumesTwoArgs()
    {
        var r = MtlParser.Parse("newmtl m\nmap_Kd -mm 0.1 0.9 -clamp on tex.png\n");
        Assert.True(r.Success);
        var tex = r.Materials["m"].DiffuseTexture!;
        Assert.Equal("tex.png", tex.Path);
        Assert.True(tex.Clamp);
        Assert.Equal("0.1 0.9", tex.ExtraOptions["-mm"]);
    }
}

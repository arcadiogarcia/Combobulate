using System.Collections.Generic;

namespace Combobulate.Parsing;

/// <summary>
/// A quad face. Vertex order from the source file is preserved.
/// </summary>
public sealed class ObjQuad
{
    public ObjQuad(
        ObjVertex v0,
        ObjVertex v1,
        ObjVertex v2,
        ObjVertex v3,
        string? objectName,
        IReadOnlyList<string> groups,
        string? material,
        int smoothingGroup)
    {
        V0 = v0;
        V1 = v1;
        V2 = v2;
        V3 = v3;
        ObjectName = objectName;
        Groups = groups;
        Material = material;
        SmoothingGroup = smoothingGroup;
    }

    public ObjVertex V0 { get; }
    public ObjVertex V1 { get; }
    public ObjVertex V2 { get; }
    public ObjVertex V3 { get; }

    /// <summary>Active <c>o</c> name when this face was declared, or null.</summary>
    public string? ObjectName { get; }

    /// <summary>Active <c>g</c> names. Defaults to a single "default" entry if none set.</summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>Active <c>usemtl</c> name when this face was declared, or null.</summary>
    public string? Material { get; }

    /// <summary>Active smoothing group ID. <c>0</c> means smoothing off.</summary>
    public int SmoothingGroup { get; }
}

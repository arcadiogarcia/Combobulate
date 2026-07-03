using System.Collections.Generic;
using System.Numerics;

namespace Combobulate.Parsing;

/// <summary>
/// In-memory representation of a parsed OBJ file.
/// All indices on <see cref="ObjVertex"/> are zero-based and pre-resolved
/// (negative/relative indices have already been converted).
/// </summary>
public sealed class ObjModel
{
    /// <summary>Vertex positions (x, y, z, w). w defaults to 1.</summary>
    public List<Vector4> Positions { get; } = new();

    /// <summary>Texture coordinates (u, v, w). v defaults to 0, w defaults to 0.</summary>
    public List<Vector3> TexCoords { get; } = new();

    /// <summary>Normals (x, y, z). Not necessarily unit length.</summary>
    public List<Vector3> Normals { get; } = new();

    /// <summary>All quad faces, in source order.</summary>
    public List<ObjQuad> Quads { get; } = new();

    /// <summary>All triangle faces, in source order. Source order is preserved
    /// independently of <see cref="Quads"/>; the renderer treats both lists
    /// as first-class face sources.</summary>
    public List<ObjTriangle> Triangles { get; } = new();

    /// <summary>True when this model contributes no renderable faces — both
    /// <see cref="Quads"/> and <see cref="Triangles"/> are empty. Use this
    /// instead of <c>Quads.Count == 0</c> to early-return correctly for
    /// triangle-only meshes.</summary>
    public bool IsEmpty => Quads.Count == 0 && Triangles.Count == 0;

    /// <summary>Material library file names referenced via <c>mtllib</c>.</summary>
    public List<string> MaterialLibraries { get; } = new();
}

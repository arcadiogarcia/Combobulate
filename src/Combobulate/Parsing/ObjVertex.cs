namespace Combobulate.Parsing;

/// <summary>
/// A single corner of a face: one position index plus optional uv / normal indices.
/// All indices are zero-based.
/// </summary>
public readonly struct ObjVertex
{
    public ObjVertex(int positionIndex, int? texCoordIndex, int? normalIndex)
    {
        PositionIndex = positionIndex;
        TexCoordIndex = texCoordIndex;
        NormalIndex = normalIndex;
    }

    public int PositionIndex { get; }
    public int? TexCoordIndex { get; }
    public int? NormalIndex { get; }
}

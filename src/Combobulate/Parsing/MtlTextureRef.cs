using System.Collections.Generic;
using System.Numerics;

namespace Combobulate.Parsing;

/// <summary>
/// A texture reference parsed from a <c>map_*</c> line in an MTL file.
/// <see cref="Path"/> is preserved exactly as written; resolution against the
/// MTL file's directory happens at the cache layer.
/// </summary>
public sealed class MtlTextureRef
{
    public string Path { get; init; } = "";
    public Vector2 Scale { get; init; } = Vector2.One;
    public Vector2 Offset { get; init; } = Vector2.Zero;
    public bool Clamp { get; init; }

    public IReadOnlyDictionary<string, string> ExtraOptions { get; init; } =
        new Dictionary<string, string>();
}

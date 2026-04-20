using System.Numerics;

#if WINAPPSDK
using Windows.UI;
#else
using Windows.UI;
#endif

namespace Combobulate.Caching;

/// <summary>
/// Render-ready material descriptor consumed by <c>Combobulate</c>.
/// </summary>
public sealed class ObjMaterial
{
    public string? Name { get; init; }
    public Color? DiffuseColor { get; init; }
    public ObjTextureSource? DiffuseTexture { get; init; }
    public Vector2 UvScale { get; init; } = Vector2.One;
    public Vector2 UvOffset { get; init; } = Vector2.Zero;
    public bool ClampUv { get; init; }
}

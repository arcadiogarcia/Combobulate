using System.Numerics;

#if WINAPPSDK
using Windows.UI;
using Microsoft.UI.Composition;
#else
using Windows.UI;
using Windows.UI.Composition;
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

    /// <summary>
    /// Optional tangent-space normal map surface for this face. When non-null,
    /// MaterialResolver wraps the diffuse brush in a <c>SceneLightingEffect</c>
    /// graph so <c>CompositionLight</c>s produce per-pixel diffuse + specular
    /// illumination. When null (default), the face uses a flat brush with no
    /// lighting overhead.
    /// </summary>
    public ICompositionSurface? NormalMap { get; init; }

    /// <summary>
    /// Per-material lighting coefficients. When null, the face uses
    /// <see cref="LightingDefaults"/> (process-wide shared values).
    /// </summary>
    public LightingParams? Lighting { get; init; }
}

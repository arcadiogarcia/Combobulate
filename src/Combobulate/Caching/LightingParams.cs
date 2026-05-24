namespace Combobulate.Caching;

/// <summary>
/// Per-material lighting coefficients for <c>SceneLightingEffect</c>.
/// All values are optional; nulls fall through to <see cref="LightingDefaults"/>.
/// </summary>
public sealed class LightingParams
{
    /// <summary>Ambient light contribution [0..1]. Default via <see cref="LightingDefaults"/>: 0.6.</summary>
    public float? AmbientAmount { get; init; }

    /// <summary>Diffuse (Lambertian) contribution [0..∞]. Default: 1.0.</summary>
    public float? DiffuseAmount { get; init; }

    /// <summary>Specular (Phong/Blinn) highlight contribution [0..∞]. Default: 0.2.</summary>
    public float? SpecularAmount { get; init; }

    /// <summary>Specular exponent (shininess). Higher = tighter highlight. Default: 16.</summary>
    public float? SpecularShine { get; init; }
}

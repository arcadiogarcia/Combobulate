namespace Combobulate;

/// <summary>
/// Controls how <see cref="Combobulate"/> resolves materials for each quad.
/// </summary>
public enum MaterialMode
{
    /// <summary>Standard resolution: explicit Materials DP, then ObjCache.TryGetMaterials(Source),
    /// then auto-loaded mtllib, then per-quad fallback color.</summary>
    Auto,

    /// <summary>Ignore all materials and use the per-quad golden-angle palette (v0.2.0 behavior).</summary>
    UseFallback,

    /// <summary>Use materials but ignore textures — only solid DiffuseColor is honored.</summary>
    UseDiffuse,
}

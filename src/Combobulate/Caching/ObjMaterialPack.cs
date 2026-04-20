using System;
using System.Collections.Generic;

namespace Combobulate.Caching;

/// <summary>
/// Bundle of materials addressable by <c>usemtl</c> name. Misses fall back to
/// <see cref="Fallback"/>, then to the per-quad palette baked into <see cref="ObjGeometry"/>.
/// </summary>
public sealed class ObjMaterialPack
{
    public ObjMaterialPack(IReadOnlyDictionary<string, ObjMaterial> materials, ObjMaterial? fallback = null)
    {
        Materials = materials ?? throw new ArgumentNullException(nameof(materials));
        Fallback = fallback;
    }

    public ObjMaterialPack() : this(new Dictionary<string, ObjMaterial>(StringComparer.Ordinal), null) { }

    public IReadOnlyDictionary<string, ObjMaterial> Materials { get; }
    public ObjMaterial? Fallback { get; }
}

public sealed class ObjMaterialPackBuilder
{
    private readonly Dictionary<string, ObjMaterial> _materials = new(StringComparer.Ordinal);

    public ObjMaterial? Fallback { get; set; }

    public ObjMaterialPackBuilder Add(string name, ObjMaterial material)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        if (material == null) throw new ArgumentNullException(nameof(material));
        _materials[name] = material;
        return this;
    }

    public ObjMaterialPack Build() => new(new Dictionary<string, ObjMaterial>(_materials), Fallback);
}

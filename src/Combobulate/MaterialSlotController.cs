using System;
using System.Collections.Generic;
using Combobulate.Caching;

#if WINAPPSDK
using Windows.UI;
using Microsoft.UI.Composition;
#else
using Windows.UI;
using Windows.UI.Composition;
#endif

namespace Combobulate;

/// <summary>
/// Hot-path API for changing named materials without replacing the whole
/// <see cref="ObjMaterialPack"/> dependency property.
/// </summary>
public sealed class MaterialSlotController
{
    private readonly Combobulate _owner;
    private readonly Dictionary<string, ObjMaterial> _pending = new(StringComparer.Ordinal);

    internal MaterialSlotController(Combobulate owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public void SetMaterial(string name, ObjMaterial material)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        if (material == null) throw new ArgumentNullException(nameof(material));
        _pending[name] = material;
    }

    public void SetTexture(string name, ObjTextureSource? texture)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        _pending[name] = new ObjMaterial
        {
            Name = name,
            DiffuseTexture = texture,
            ClampUv = true,
        };
    }

    public void SetColor(string name, Color color)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        _pending[name] = new ObjMaterial
        {
            Name = name,
            DiffuseColor = color,
            ClampUv = true,
        };
    }

    public void SetSurface(string name, ICompositionSurface? surface, Color? diffuseColor = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        _pending[name] = new ObjMaterial
        {
            Name = name,
            DiffuseColor = diffuseColor,
            DiffuseTexture = surface is null ? null : ObjTextureSource.FromSurface(surface),
            ClampUv = true,
        };
    }

    public void Commit()
    {
        if (_pending.Count == 0) return;
        var updates = new Dictionary<string, ObjMaterial>(_pending, StringComparer.Ordinal);
        _pending.Clear();
        _owner.ApplyMaterialSlotUpdates(updates);
    }
}

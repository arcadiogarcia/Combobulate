using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;
using Windows.Graphics.Effects;

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

    /// <summary>
    /// Paint the named slot's faces with a host-supplied <see cref="CompositionBrush"/>
    /// instead of a texture or color. The <paramref name="brushFactory"/> is
    /// invoked with Combobulate's own <see cref="Compositor"/>. Combobulate keeps
    /// doing face sorting, triangle clipping and the 3D sprite transform, but the
    /// host owns the brush (Combobulate never disposes it).
    ///
    /// <para>With <see cref="BrushMapping.ScreenSpace"/> (the default) the brush
    /// samples in screen space — ideal for a <c>CompositionBackdropBrush</c> so
    /// faces become "see-through" and show whatever is composited behind the die
    /// — and one brush instance is shared across all faces. With
    /// <see cref="BrushMapping.PerFaceUv"/> the factory is invoked per face so
    /// each can carry its own UV transform.</para>
    /// </summary>
    public void SetBrush(string name, Func<Compositor, CompositionBrush> brushFactory,
        BrushMapping mapping = BrushMapping.ScreenSpace)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        if (brushFactory == null) throw new ArgumentNullException(nameof(brushFactory));
        _pending[name] = new ObjMaterial
        {
            Name = name,
            CustomBrushFactory = brushFactory,
            Mapping = mapping,
            ClampUv = true,
        };
    }

    /// <summary>
    /// Convenience over <see cref="SetBrush"/>: paint the named slot's faces with
    /// a screen-space <c>CompositionBackdropBrush</c> so each face shows the app
    /// content composited behind the die at that face's on-screen position
    /// (glass / see-through faces that stay screen-aligned as the die rolls).
    /// The host decides what shows through by placing a visual behind the die.
    ///
    /// <para>Supply <paramref name="effect"/> to wrap the raw backdrop (e.g. blur
    /// / tint / lighting for a frosted-glass or acrylic look); it receives the
    /// compositor and the backdrop brush and returns the brush to paint with.</para>
    /// </summary>
    public void SetBackdrop(string name,
        Func<Compositor, CompositionBackdropBrush, CompositionBrush>? effect = null)
    {
        SetBrush(name, compositor =>
        {
            var backdrop = compositor.CreateBackdropBrush();
            return effect is null ? backdrop : effect(compositor, backdrop);
        }, BrushMapping.ScreenSpace);
    }

    /// <summary>
    /// Paint the named slot's faces with a host-supplied <paramref name="surface"/>
    /// PROJECTED into screen space: Combobulate gives each face its own
    /// <c>CompositionSurfaceBrush</c> over the surface and drives its
    /// <c>TransformMatrix</c> with a composition <see cref="ExpressionAnimation"/>
    /// off the live die rotation, so every face reveals the texel that sits under
    /// it on screen. Nothing is composited behind the die, so the surroundings
    /// stay the app's own background and only the faces show the surface — a true
    /// "glass" die (e.g. over the desktop wallpaper).
    ///
    /// <para><paramref name="screenToSurface"/> maps Combobulate's on-screen
    /// (host-element) coordinates to a texel in <paramref name="surface"/>; supply
    /// it to fold in the surface scale and the die window's desktop origin so each
    /// die reveals the actual pixels beneath it. Combobulate never disposes the
    /// surface. The transform is affine, so it ignores the perspective divide (see
    /// <see cref="BrushMapping.ScreenProjected"/>).</para>
    /// </summary>
    public void SetScreenProjectedSurface(string name, ICompositionSurface surface,
        Matrix3x2 screenToSurface)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        _pending[name] = new ObjMaterial
        {
            Name = name,
            ScreenProjectedSurface = surface,
            ScreenToSurface = screenToSurface,
            Mapping = BrushMapping.ScreenProjected,
            ClampUv = true,
        };
    }

    /// <summary>
    /// Like <see cref="SetScreenProjectedSurface(string, ICompositionSurface, Matrix3x2)"/>
    /// but takes a <em>live</em> on-screen→texel map: Combobulate references the
    /// Matrix3x2 property <paramref name="propertyName"/> on the host-owned
    /// <paramref name="screenToSurfaceSet"/> directly from each face's brush
    /// expression. The host animates that property (typically off the window's live
    /// desktop origin and the die's own translation), so every face keeps revealing
    /// the wallpaper texel beneath its <em>current</em> screen position as the die or
    /// its window moves — entirely on the compositor thread, with no re-apply. The
    /// property set must already carry a Matrix3x2 named <paramref name="propertyName"/>.
    /// Combobulate never disposes the property set.
    /// </summary>
    public void SetScreenProjectedSurface(string name, ICompositionSurface surface,
        CompositionPropertySet screenToSurfaceSet, string propertyName = "ScreenToSurface")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        if (screenToSurfaceSet == null) throw new ArgumentNullException(nameof(screenToSurfaceSet));
        if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException("Property name is required.", nameof(propertyName));
        _pending[name] = new ObjMaterial
        {
            Name = name,
            ScreenProjectedSurface = surface,
            ScreenToSurfaceSet = screenToSurfaceSet,
            ScreenToSurfaceProperty = propertyName,
            Mapping = BrushMapping.ScreenProjected,
            ClampUv = true,
        };
    }

    /// <summary>
    /// Paint the named slot's faces with a host-supplied <paramref name="effectGraph"/>
    /// (any Win2D <c>IGraphicsEffect</c> graph) whose sampled inputs are named
    /// <c>CompositionEffectSourceParameter</c>s. Each named source is mapped, via
    /// <paramref name="sources"/>, to a <see cref="MaterialLayer"/> that says whether it
    /// is a per-face UV atlas, a screen-projected surface, or a screen-space brush.
    /// Combobulate builds one <c>CompositionEffectBrush</c> per face from a cached factory,
    /// wires each source, and drives PerFaceUv sources' UV crop and ScreenProjected
    /// sources' rotation expression per face — so the host gets the full versatility of
    /// composition effect brushes while Combobulate owns per-face instantiation and
    /// transforms.
    ///
    /// <para>This is the general primitive behind the other setters: e.g. a glass die
    /// whose faces reveal the wallpaper AND carry a numeral decal is a
    /// <c>CompositeEffect(SourceOver)</c> of a <see cref="MaterialLayer.ScreenProjected(ICompositionSurface, CompositionPropertySet, string)"/>
    /// base and a <see cref="MaterialLayer.PerFaceUv(ObjTextureSource, System.Numerics.Vector2?, System.Numerics.Vector2?)"/>
    /// overlay.</para>
    ///
    /// <para>Optionally supply <paramref name="sharedProperties"/> + <paramref name="boundProperties"/>
    /// to animate effect scalars (blur amount, tint strength, …) across every face at
    /// once: pass the animatable property names to <c>CreateEffectFactory</c> via
    /// <paramref name="boundProperties"/>, and Combobulate binds each one live to the
    /// like-named property (its last dotted segment) on <paramref name="sharedProperties"/>.
    /// Combobulate never disposes the graph, surfaces, brushes or property set.</para>
    /// </summary>
    public void SetEffect(string name, IGraphicsEffect effectGraph,
        IReadOnlyDictionary<string, MaterialLayer> sources,
        CompositionPropertySet? sharedProperties = null,
        IReadOnlyList<string>? boundProperties = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Material slot name is required.", nameof(name));
        if (effectGraph == null) throw new ArgumentNullException(nameof(effectGraph));
        if (sources == null) throw new ArgumentNullException(nameof(sources));
        if (sources.Count == 0) throw new ArgumentException("At least one effect source is required.", nameof(sources));
        foreach (var kv in sources)
        {
            if (kv.Value == null) throw new ArgumentException($"Effect source '{kv.Key}' is null.", nameof(sources));
        }
        _pending[name] = new ObjMaterial
        {
            Name = name,
            EffectGraph = effectGraph,
            EffectSources = new Dictionary<string, MaterialLayer>(sources, StringComparer.Ordinal),
            SharedEffectProperties = sharedProperties,
            BoundEffectProperties = boundProperties == null ? null : new List<string>(boundProperties),
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

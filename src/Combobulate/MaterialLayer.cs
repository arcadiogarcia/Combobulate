using System;
using System.Numerics;
using Combobulate.Caching;

#if WINAPPSDK
using Microsoft.UI.Composition;
#else
using Windows.UI.Composition;
#endif

namespace Combobulate;

/// <summary>
/// Describes how ONE named sampled source of a host-supplied effect graph (see
/// <see cref="MaterialSlotController.SetEffect"/>) is bound onto each face.
/// Combobulate owns the per-face brush instantiation and transform-driving; the
/// host only declares, per named <c>CompositionEffectSourceParameter</c>, which
/// kind of source it is and where the pixels come from.
///
/// <para>The three kinds mirror <see cref="BrushMapping"/>:
/// <list type="bullet">
///   <item><see cref="PerFaceUv"/> — a texture atlas cropped to each face's UV
///   cell (e.g. a per-face numeral / decal layer). Combobulate creates one
///   surface brush per face and drives its UV crop.</item>
///   <item><see cref="ScreenProjected"/> — a host surface projected into screen
///   space so each face reveals the texel beneath it (e.g. the desktop
///   wallpaper). Combobulate drives the brush transform from the live die
///   rotation.</item>
///   <item><see cref="ScreenSpace"/> — an arbitrary host-supplied brush sampled
///   in screen space (e.g. a <c>CompositionBackdropBrush</c>), shared across every
///   face.</item>
/// </list></para>
/// </summary>
public sealed class MaterialLayer
{
    private MaterialLayer(BrushMapping mapping)
    {
        Mapping = mapping;
    }

    /// <summary>Which of the three source kinds this layer is.</summary>
    public BrushMapping Mapping { get; }

    // ── PerFaceUv ────────────────────────────────────────────────────────────
    /// <summary>Atlas texture for a <see cref="BrushMapping.PerFaceUv"/> layer.</summary>
    public ObjTextureSource? Texture { get; private init; }
    /// <summary>Per-layer UV scale for a <see cref="BrushMapping.PerFaceUv"/> layer.</summary>
    public Vector2 UvScale { get; private init; } = Vector2.One;
    /// <summary>Per-layer UV offset for a <see cref="BrushMapping.PerFaceUv"/> layer.</summary>
    public Vector2 UvOffset { get; private init; } = Vector2.Zero;

    // ── ScreenProjected ──────────────────────────────────────────────────────
    /// <summary>Host surface for a <see cref="BrushMapping.ScreenProjected"/> layer.</summary>
    public ICompositionSurface? ProjectedSurface { get; private init; }
    /// <summary>Constant on-screen→texel map for a <see cref="BrushMapping.ScreenProjected"/> layer.</summary>
    public Matrix3x2 ScreenToSurface { get; private init; } = Matrix3x2.Identity;
    /// <summary>Optional live on-screen→texel property set (supersedes <see cref="ScreenToSurface"/>).</summary>
    public CompositionPropertySet? ScreenToSurfaceSet { get; private init; }
    /// <summary>Name of the Matrix3x2 property on <see cref="ScreenToSurfaceSet"/>.</summary>
    public string? ScreenToSurfaceProperty { get; private init; }

    // ── ScreenSpace ──────────────────────────────────────────────────────────
    /// <summary>Host brush factory for a <see cref="BrushMapping.ScreenSpace"/> layer.</summary>
    public Func<Compositor, CompositionBrush>? BrushFactory { get; private init; }

    /// <summary>
    /// A per-face texture-atlas source cropped to each face's UV cell — Combobulate
    /// creates one surface brush per face and drives its UV crop from the sprite size.
    /// Ideal for a numeral / decal layer that shares the die's UVs.
    /// </summary>
    public static MaterialLayer PerFaceUv(ObjTextureSource texture, Vector2? uvScale = null, Vector2? uvOffset = null)
    {
        if (texture == null) throw new ArgumentNullException(nameof(texture));
        return new MaterialLayer(BrushMapping.PerFaceUv)
        {
            Texture = texture,
            UvScale = uvScale ?? Vector2.One,
            UvOffset = uvOffset ?? Vector2.Zero,
        };
    }

    /// <summary>
    /// A host surface projected into screen space by a constant on-screen→texel map:
    /// each face samples the surface texel under it on screen, driven from the live
    /// die rotation on the compositor thread. See <see cref="BrushMapping.ScreenProjected"/>.
    /// </summary>
    public static MaterialLayer ScreenProjected(ICompositionSurface surface, Matrix3x2 screenToSurface)
    {
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        return new MaterialLayer(BrushMapping.ScreenProjected)
        {
            ProjectedSurface = surface,
            ScreenToSurface = screenToSurface,
        };
    }

    /// <summary>
    /// A host surface projected into screen space by a <em>live</em> on-screen→texel
    /// map: Combobulate references the Matrix3x2 property <paramref name="propertyName"/>
    /// on the host-owned <paramref name="screenToSurfaceSet"/> from each face's brush
    /// expression, so the host can animate the map (e.g. off the window's live desktop
    /// origin) with no re-apply. See <see cref="BrushMapping.ScreenProjected"/>.
    /// </summary>
    public static MaterialLayer ScreenProjected(ICompositionSurface surface,
        CompositionPropertySet screenToSurfaceSet, string propertyName = "ScreenToSurface")
    {
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        if (screenToSurfaceSet == null) throw new ArgumentNullException(nameof(screenToSurfaceSet));
        if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException("Property name is required.", nameof(propertyName));
        return new MaterialLayer(BrushMapping.ScreenProjected)
        {
            ProjectedSurface = surface,
            ScreenToSurfaceSet = screenToSurfaceSet,
            ScreenToSurfaceProperty = propertyName,
        };
    }

    /// <summary>
    /// An arbitrary host-supplied brush sampled in screen space and shared across every
    /// face (e.g. a <c>CompositionBackdropBrush</c> for a "see-through" source). The
    /// factory is invoked once with Combobulate's own <see cref="Compositor"/>;
    /// Combobulate never disposes the brush. See <see cref="BrushMapping.ScreenSpace"/>.
    /// </summary>
    public static MaterialLayer ScreenSpace(Func<Compositor, CompositionBrush> brushFactory)
    {
        if (brushFactory == null) throw new ArgumentNullException(nameof(brushFactory));
        return new MaterialLayer(BrushMapping.ScreenSpace)
        {
            BrushFactory = brushFactory,
        };
    }
}

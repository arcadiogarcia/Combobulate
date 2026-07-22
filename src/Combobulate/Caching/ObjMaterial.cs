using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Graphics.Effects;

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
    [Obsolete("The built-in lit path is deprecated. Build your own lighting graph " +
        "(e.g. ArithmeticCompositeEffect(diffuse × SceneLightingEffect(normal))) and pass " +
        "it to MaterialSlotController.SetEffect instead. See docs/migration-lit-to-effect.md.")]
    public ICompositionSurface? NormalMap { get; init; }

    /// <summary>
    /// Per-material lighting coefficients. When null, the face uses
    /// <see cref="LightingDefaults"/> (process-wide shared values).
    /// </summary>
    [Obsolete("The built-in lit path is deprecated. Drive lighting scalars via " +
        "ObjMaterial.SharedEffectProperties / BoundEffectProperties on a host SetEffect graph " +
        "instead. See docs/migration-lit-to-effect.md.")]
    public LightingParams? Lighting { get; init; }

    /// <summary>
    /// Optional host-supplied brush factory. When non-null, faces using this
    /// material are painted with the brush this returns instead of a texture /
    /// color brush — Combobulate keeps doing face sorting, clipping and the 3D
    /// sprite transform, but does not own the brush. The factory is invoked
    /// with Combobulate's own <see cref="Compositor"/>. See <see cref="Mapping"/>
    /// for how the brush is mapped onto faces, and note that host-supplied
    /// brushes are never disposed by Combobulate.
    /// </summary>
    public Func<Compositor, CompositionBrush>? CustomBrushFactory { get; init; }

    /// <summary>
    /// How <see cref="CustomBrushFactory"/>'s brush is mapped onto faces.
    /// Ignored when <see cref="CustomBrushFactory"/> is null.
    /// </summary>
    public BrushMapping Mapping { get; init; } = BrushMapping.PerFaceUv;

    /// <summary>
    /// Host-supplied surface sampled per face when <see cref="Mapping"/> is
    /// <see cref="BrushMapping.ScreenProjected"/>. Combobulate creates one
    /// <c>CompositionSurfaceBrush</c> per face over this surface and drives its
    /// <c>TransformMatrix</c> from the live rotation so each face reveals the
    /// texel under it on screen. Combobulate does not dispose this surface.
    /// </summary>
    public ICompositionSurface? ScreenProjectedSurface { get; init; }

    /// <summary>
    /// Affine map from Combobulate's on-screen (host-element) coordinate space to
    /// a texel in <see cref="ScreenProjectedSurface"/>. Supplied by the host so it
    /// can fold in the surface's scale and the die window's desktop origin,
    /// letting each die reveal the actual wallpaper pixels beneath it. Only used
    /// for <see cref="BrushMapping.ScreenProjected"/>.
    /// </summary>
    public Matrix3x2 ScreenToSurface { get; init; } = Matrix3x2.Identity;

    /// <summary>
    /// Optional host-owned property set carrying a <em>live</em> Matrix3x2 that
    /// supersedes the constant <see cref="ScreenToSurface"/>. When non-null,
    /// Combobulate references the property named <see cref="ScreenToSurfaceProperty"/>
    /// on this set from the screen-projected brush expression, so the host can
    /// animate the on-screen→texel map (e.g. off the window's live desktop origin
    /// and the die's own translation) and every face re-samples the wallpaper
    /// beneath its <em>current</em> screen position on the compositor thread — no
    /// per-frame CPU push, no brush rebuild. Combobulate never disposes this set.
    /// Only used for <see cref="BrushMapping.ScreenProjected"/>.
    /// </summary>
    public CompositionPropertySet? ScreenToSurfaceSet { get; init; }

    /// <summary>
    /// Name of the Matrix3x2 property on <see cref="ScreenToSurfaceSet"/> that
    /// Combobulate reads live. Ignored when <see cref="ScreenToSurfaceSet"/> is null.
    /// </summary>
    public string? ScreenToSurfaceProperty { get; init; }

    /// <summary>
    /// Optional host-supplied effect graph whose sampled inputs are named
    /// <c>CompositionEffectSourceParameter</c>s. When non-null, Combobulate builds a
    /// per-material <c>CompositionEffectFactory</c> from this graph, creates one
    /// <c>CompositionEffectBrush</c> per face, and binds each named source per its
    /// <see cref="MaterialLayer"/> in <see cref="EffectSources"/> — driving PerFaceUv
    /// sources' UV crop and ScreenProjected sources' rotation expression per face while
    /// the host owns the topology of the graph. Combobulate never disposes the graph or
    /// its host-supplied surfaces/brushes. See <see cref="MaterialSlotController.SetEffect"/>.
    /// </summary>
    public IGraphicsEffect? EffectGraph { get; init; }

    /// <summary>
    /// Maps each named <c>CompositionEffectSourceParameter</c> in <see cref="EffectGraph"/>
    /// to the source that feeds it per face. Required (and only used) when
    /// <see cref="EffectGraph"/> is non-null.
    /// </summary>
    public IReadOnlyDictionary<string, MaterialLayer>? EffectSources { get; init; }

    /// <summary>
    /// Optional host-owned property set carrying scalars that Combobulate binds into
    /// every per-face effect brush (via the names in <see cref="BoundEffectProperties"/>),
    /// so the host can animate effect parameters across all faces at once. Combobulate
    /// never disposes it. Only used when <see cref="EffectGraph"/> is non-null.
    /// </summary>
    public CompositionPropertySet? SharedEffectProperties { get; init; }

    /// <summary>
    /// Names of the animatable effect properties (as passed to
    /// <c>Compositor.CreateEffectFactory</c>) to bind live to
    /// <see cref="SharedEffectProperties"/>. Each name's last dotted segment is the
    /// property read from the shared set (e.g. <c>"blur.BlurAmount"</c> reads
    /// <c>BlurAmount</c>). Only used when <see cref="EffectGraph"/> is non-null.
    /// </summary>
    public IReadOnlyList<string>? BoundEffectProperties { get; init; }
}

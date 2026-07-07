using System;
using System.Numerics;
using System.Runtime.CompilerServices;
#if !COMBOBULATE_NO_XAML
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
#endif

#if WINAPPSDK
using Microsoft.UI.Composition;
#else
using Windows.UI.Composition;
#endif

namespace Combobulate.Rendering;

/// <summary>
/// Builds the shared unit-triangle <see cref="CompositionPathGeometry"/> used
/// by every per-triangle <see cref="CompositionGeometricClip"/>.
///
/// <para>
/// The triangle is the unit right-triangle <c>(0,0)→(1,0)→(0,1)→(0,0)</c> in
/// the visual's local space, matching the convention the renderer uses for
/// triangle faces: a SpriteVisual with <c>xAxis = V1-V0, yAxis = V2-V0</c> and
/// <c>Size = (lenX, lenY)</c>. Each per-face <see cref="CompositionGeometricClip"/>
/// references this single shared geometry and carries its own
/// <c>TransformMatrix = Scale(lenX, lenY)</c> to fit the sprite's pixel bounds.
/// </para>
///
/// <para>
/// Win2D's <see cref="CanvasGeometry"/> is used purely to author the path
/// shape; the resulting <see cref="CompositionPath"/> is a composition-native
/// object and the Win2D handle is disposed immediately. Combobulate already
/// pulls Win2D in for texture decode and lit-material effects, so no new
/// dependency is introduced.
/// </para>
///
/// <para><b>Per-Compositor cache.</b> The unit-triangle PathGeometry is
/// cached per <see cref="Compositor"/> via a <see cref="ConditionalWeakTable{TKey, TValue}"/>
/// so every triangle CachedQuad in a given Combobulate scene reuses the
/// same composition geometry (one ID3D11 path object, many clips). The
/// cache entry is held by the Compositor itself so it is collected
/// automatically when the Compositor is disposed.</para>
/// </summary>
internal static class TriangleClipFactory
{
    private static readonly ConditionalWeakTable<Compositor, CompositionPathGeometry>
        UnitTriCache = new();

    /// <summary>Returns the cached unit-triangle path geometry for
    /// <paramref name="compositor"/>, creating it on first call.</summary>
    public static CompositionPathGeometry GetOrCreateUnitTrianglePath(Compositor compositor)
    {
        if (compositor == null) throw new ArgumentNullException(nameof(compositor));
        return UnitTriCache.GetValue(compositor, CreateUnitTrianglePath);
    }

    /// <summary>Builds a fresh unit-triangle path geometry on
    /// <paramref name="compositor"/>. Callers should normally use
    /// <see cref="GetOrCreateUnitTrianglePath"/>.</summary>
    public static CompositionPathGeometry CreateUnitTrianglePath(Compositor compositor)
    {
#if COMBOBULATE_NO_XAML
        // Win2D is unavailable here; author the unit triangle with raw Direct2D
        // interop and wrap it as a system CompositionPath. The path itself is a
        // shared constant; only this per-compositor PathGeometry is unique.
        var compositionPath = D2DTriangleGeometry.GetOrCreateUnitTriangleCompositionPath();
        var pathGeo = compositor.CreatePathGeometry();
        pathGeo.Path = compositionPath;
        return pathGeo;
#else
        var device = CanvasDevice.GetSharedDevice();
        using var canvasGeo = CanvasGeometry.CreatePolygon(
            device,
            new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
            });
        var compositionPath = new CompositionPath(canvasGeo);
        var pathGeo = compositor.CreatePathGeometry();
        pathGeo.Path = compositionPath;
        return pathGeo;
#endif
    }

    /// <summary>Builds a per-sprite <see cref="CompositionGeometricClip"/>
    /// that masks the rectangular sprite (size <paramref name="lenX"/> by
    /// <paramref name="lenY"/>) to the right-triangle with corners
    /// <c>(0,0), (lenX,0), (0,lenY)</c>. The returned clip references the
    /// shared cached unit-triangle path; only the per-sprite scale matrix
    /// is unique. Pass <paramref name="lenX"/> and <paramref name="lenY"/>
    /// both &gt; 0; zero/negative scales would collapse the clip and hide
    /// the sprite entirely.</summary>
    public static CompositionGeometricClip CreateTriangleClip(
        Compositor compositor, float lenX, float lenY)
    {
        if (compositor == null) throw new ArgumentNullException(nameof(compositor));
        var clip = compositor.CreateGeometricClip();
        clip.Geometry = GetOrCreateUnitTrianglePath(compositor);
        clip.TransformMatrix = Matrix3x2.CreateScale(lenX, lenY);
        return clip;
    }
}


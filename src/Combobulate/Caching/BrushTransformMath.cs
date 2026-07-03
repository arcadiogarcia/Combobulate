using System.Numerics;

namespace Combobulate.Caching;

/// <summary>
/// Pure-math construction of the per-face <see cref="Matrix3x2"/> brush
/// transform applied to <c>CompositionSurfaceBrush.TransformMatrix</c>.
///
/// <para>
/// Factored out of <c>MaterialResolver</c> so the math is unit-testable in a
/// plain .NET assembly without Composition / Windows.UI dependencies.
/// </para>
///
/// <para>
/// The brush transform maps a sprite-pixel input coordinate (<c>(px, py)</c>
/// in <c>[0, spriteWidth] × [0, spriteHeight]</c>) to the brush-coordinate
/// from which the surface is sampled. With <c>Stretch=Fill</c> a brush
/// coordinate of <c>(b.x, b.y)</c> samples the surface at UV
/// <c>(b.x / spriteWidth, b.y / spriteHeight)</c>, so the matrix produced
/// here is composed so that sprite pixel <c>(px, py)</c> samples surface UV
/// <c>M_uv × (px / spriteWidth, py / spriteHeight)</c> for the UV-space
/// matrix <c>M_uv</c> that describes the face's logical UV layout.
/// </para>
///
/// <para>
/// <strong>Critical unit detail.</strong> <c>CompositionBrush.TransformMatrix</c>
/// translations are expressed in <em>sprite pixels</em>, not normalised UV
/// space — see
/// https://learn.microsoft.com/uwp/api/windows.ui.composition.compositionbrush.transformmatrix.
/// A logical UV-space translation of <c>0.25</c> therefore must be passed to
/// the brush as <c>0.25 × spriteSize</c>; supplying it raw produces a
/// sub-pixel offset and makes the matrix degenerate. No V-flip is applied:
/// the surface loader (<c>ObjTextureSource</c>) orients decoded images v-up,
/// matching the OBJ texture-coordinate convention, so surface V equals objUV.y
/// directly.
/// </para>
///
/// <para>
/// Sub-pixel translation also explains why negative-diagonal UV transforms
/// rendered blank: a matrix like <c>(-0.5, 0, 0, -0.5, 0.75, 0.75)</c>
/// applied to a 158-px sprite samples brush coordinates from <c>(0.75,
/// 0.75)</c> (the sprite's <c>(0,0)</c>) all the way to negative coordinates
/// off the surface; scaling the translation by sprite size moves the entire
/// sampled rectangle back into <c>[0, spriteSize]</c> and the sprite renders
/// correctly. Both <see cref="BuildQuadAxisAlignedCrop"/> and
/// <see cref="BuildTriangleAffine"/> apply the same conversion so callers
/// only need to pass <paramref name="spriteSize"/>.
/// </para>
/// </summary>
internal static class BrushTransformMath
{
    /// <summary>
    /// Build the per-quad sprite-to-UV affine. The returned matrix maps
    /// sprite-pixel coordinates onto the brush-pixel coordinates that, with
    /// <c>Stretch=Fill</c>, sample the surface at the UVs assigned to the
    /// face's four vertices.
    ///
    /// <para>The sprite is laid out by the renderer so that
    /// sprite-local <c>(0, 0)</c> sits at <c>V0</c>,
    /// <c>(spriteSize.X, 0)</c> at <c>V1</c>, and
    /// <c>(0, spriteSize.Y)</c> at <c>V3</c>. The affine is therefore
    /// uniquely determined by <paramref name="uv0"/>, <paramref name="uv1"/>,
    /// and <paramref name="uv3"/>; <paramref name="uv2"/> is accepted for
    /// API symmetry with the four-vertex caller but is implied by the other
    /// three on a parallelogram (the sprite's geometric domain).</para>
    ///
    /// <para>The old implementation derived an axis-aligned bounding-box
    /// crop from the UVs (taking <c>uMin..uMax × vMin..vMax</c> and
    /// stretching it across the sprite). That was correct ONLY when the
    /// sub-quad's V0..V3 winding matched the canonical "V0 at UV-minimum,
    /// V2 at UV-maximum" convention. After subdivision, sub-fragments
    /// commonly emerge with UVs rotated 90° (or 180°/270°) relative to that
    /// convention because <see cref="Combobulate.Sorting.PolygonClipper"/>
    /// emits vertices in walk order — whichever input vertex was visited
    /// first becomes the new V0. The bbox approach silently rotated the
    /// texture under those sub-fragments, producing the "rotated cover text"
    /// visual on subdivided book meshes. This rotation-aware affine fixes
    /// the bug while staying numerically identical to the bbox formula in
    /// the canonical/unrotated case.</para>
    /// </summary>
    /// <param name="spriteSize">
    /// The owning <c>SpriteVisual.Size</c> in pixels. The translation
    /// components of the returned matrix are multiplied by this vector.
    /// </param>
    public static Matrix3x2 BuildQuadAxisAlignedCrop(
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        Vector2 uvScale, Vector2 uvOffset, Vector2 spriteSize)
    {
        _ = uv2; // implied by uv0+uv1+uv3-uv0 on a parallelogram sprite.

        // Derive the affine in normalized UV space from the three known
        // sprite→vertex mappings. NO V-flip: Combobulate's surface loader
        // (ObjTextureSource) orients decoded images v-UP (row 0 = image bottom
        // = objUV.y 0), matching the OBJ texture-coordinate convention, so the
        // sampled surface V equals the interpolated objUV.y directly. This is
        // byte-identical to the committed MaterialResolver.BuildBrushTransform,
        // whose "ty = (1 - vMax)" term vanishes for a full-cover face (vMax=1)
        // and only ever operated on whole faces (subdivision was off).
        //
        // An earlier WIP revision negated the V basis (m22 = -(uv3.Y-uv0.Y),
        // m32 = 1 - uv0.Y); that double-flipped the axis and rendered every
        // cover VERTICALLY MIRRORED (verified empirically via diag3d + pixel-MSE
        // against the source cover). Removing the flip restores the shipped
        // behavior for full faces AND makes subdivided sub-fragments sample
        // surfaceV = objUV.y at the same world point, so subdivided and
        // non-subdivided covers reassemble seamlessly.
        //
        // Mapped points (surface-UV space, before brush-pixel scaling):
        //   sprite-(0, 0) → interpolated objUV at V0
        //   sprite-(1, 0) → interpolated objUV at V1
        //   sprite-(0, 1) → interpolated objUV at V3
        var m11 = uv1.X - uv0.X;
        var m12 = uv1.Y - uv0.Y;
        var m21 = uv3.X - uv0.X;
        var m22 = uv3.Y - uv0.Y;
        var m31 = uv0.X;
        var m32 = uv0.Y;

        // Convert to brush-pixel units. CompositionBrush.TransformMatrix's
        // INPUT is sprite-pixels and OUTPUT is brush-pixels — both axes
        // scale to spriteSize via Stretch=Fill, but the X and Y axes can
        // have independent sizes, so cross-axis linear terms (M12, M21)
        // need an aspect-ratio correction or the rotated case (e.g. a
        // subdivided sub-quad whose UVs are 90° relative to V0..V3
        // winding) emits brush coordinates outside [0, spriteSize] and
        // samples blank pixels off the surface.
        var sx = spriteSize.X;
        var sy = spriteSize.Y;
        var ratioYoverX = sx > 0f ? sy / sx : 1f;
        var ratioXoverY = sy > 0f ? sx / sy : 1f;
        return new Matrix3x2(
            m11 * uvScale.X,                 // sx → bx contribution
            m12 * uvScale.Y * ratioYoverX,   // sx → by contribution (needs sy/sx)
            m21 * uvScale.X * ratioXoverY,   // sy → bx contribution (needs sx/sy)
            m22 * uvScale.Y,                 // sy → by contribution
            (m31 + uvOffset.X) * sx,
            (m32 + uvOffset.Y) * sy);
    }

    /// <summary>
    /// Build the exact 3-point affine that maps the SpriteVisual's
    /// brush-rectangle corners to the triangle's UV space, expressed in
    /// brush-pixel coordinates as required by
    /// <c>CompositionBrush.TransformMatrix</c>. The three brush-space
    /// corners <c>(0,0), (spriteSize.X, 0), (0, spriteSize.Y)</c> correspond
    /// to the triangle's <c>V0, V1, V2</c> on screen, so mapping them to the
    /// V-flipped UVs at those vertices fully determines the affine.
    /// </summary>
    /// <remarks>
    /// Material <paramref name="uvScale"/> post-multiplies the linear part
    /// (zooming the visible UV span). Material <paramref name="uvOffset"/>
    /// post-translates the UV origin. Both match the quad path's
    /// interpretation of the same material parameters. <paramref
    /// name="spriteSize"/> scales the translation into brush-pixel units.
    /// </remarks>
    public static Matrix3x2 BuildTriangleAffine(
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        Vector2 uvScale, Vector2 uvOffset, Vector2 spriteSize)
    {
        var m11 = uv1.X - uv0.X;
        var m12 = uv1.Y - uv0.Y;        // NO V-flip (surface loader is v-up).
        var m21 = uv2.X - uv0.X;
        var m22 = uv2.Y - uv0.Y;
        var m31 = uv0.X;
        var m32 = uv0.Y;
        return new Matrix3x2(
            m11 * uvScale.X, m12 * uvScale.Y,
            m21 * uvScale.X, m22 * uvScale.Y,
            (m31 + uvOffset.X) * spriteSize.X,
            (m32 + uvOffset.Y) * spriteSize.Y);
    }
}

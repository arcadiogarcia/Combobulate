using System;
using System.Numerics;

namespace Combobulate.Rendering;

/// <summary>
/// Shared "conservative outset" geometry used to bridge the thin background
/// hairline seams that appear between adjacent per-face <c>SpriteVisual</c>s.
///
/// <para><b>Why the seam exists.</b> Every die face is drawn as an independent
/// <c>SpriteVisual</c> (quads render the full parallelogram; triangles are
/// masked by a <c>CompositionGeometricClip</c>). Neighbouring faces meet at a
/// shared, anti-aliased edge. The compositor anti-aliases <em>each</em> sprite's
/// edge to ~50% coverage, and two abutting 50%-covered edges composite to only
/// ~75% opacity, so the window background bleeds through as a ~1px dark
/// hairline — most visible on the many-faced triangle dice (d8/d12/d20) whose
/// interior fold edges are viewed obliquely.</para>
///
/// <para><b>The fix.</b> Grow every face outward by a few pixels in the sprite's
/// LOCAL space so neighbours physically OVERLAP: the nearer face's fully-opaque
/// interior then covers the farther face's anti-aliased rim, making the union
/// opaque with no gap. The brush UVs are expanded by the same amount so the
/// inflated interior texels stay exactly registered; only the thin grown rim
/// extrapolates (same-face texels → invisible) or edge-clamps (cross-face → a
/// ~1px overhang, far better than a background gap).</para>
///
/// <para><b>Coordinate space.</b> The outset is expressed in the SAME space the
/// sprite geometry is built in — i.e. post-projection screen pixels
/// (<c>vertex * scale + origin</c>), where <c>lenX</c>/<c>lenY</c> are the
/// sprite's on-screen pixel extents. A value of a couple of pixels therefore
/// bridges a ~1px seam directly, independent of the die's model scale.</para>
/// </summary>
internal static class FaceEdgeOutset
{
    /// <summary>
    /// Default outset, in on-screen pixels, applied to every face edge.
    /// Empirically, ~0.2 px is enough to close the sub-pixel anti-aliased seam
    /// (the two neighbouring faces each contribute this much overlap) while
    /// staying well below the point where the outermost boundary faces of
    /// high-face-count dice (d12/d20) visibly scallop the silhouette. Exposed
    /// to consumers as the default of <see cref="Combobulate.FaceEdgeOutsetPx"/>,
    /// which they can override per control.
    /// </summary>
    public const float DefaultPx = 0.2f;

    /// <summary>
    /// Inflates a QUAD face by <paramref name="outsetPx"/> on all four sides.
    /// Shifts the sprite origin <paramref name="v0"/> by <c>-outsetPx</c> along
    /// both local axes and grows <paramref name="lenX"/>/<paramref name="lenY"/>
    /// by <c>2*outsetPx</c>; emits the correspondingly expanded, [0,1]-clamped
    /// UVs so the interior stays registered.
    /// </summary>
    /// <param name="uv0">Quad UV at local (0,0).</param>
    /// <param name="uv1">Quad UV at local (lenX,0).</param>
    /// <param name="uv2">Quad UV at local (lenX,lenY).</param>
    /// <param name="uv3">Quad UV at local (0,lenY).</param>
    /// <param name="outsetPx">Outset in on-screen pixels.</param>
    /// <param name="nx">Unit local X axis in world space.</param>
    /// <param name="ny">Unit local Y axis in world space.</param>
    /// <param name="v0">Sprite origin (mutated to the inflated origin).</param>
    /// <param name="lenX">Sprite X extent (mutated to the inflated extent).</param>
    /// <param name="lenY">Sprite Y extent (mutated to the inflated extent).</param>
    /// <param name="euv0">Expanded UV at the inflated (0,0) corner.</param>
    /// <param name="euv1">Expanded UV at the inflated (legX,0) corner.</param>
    /// <param name="euv2">Expanded UV at the inflated (legX,legY) corner.</param>
    /// <param name="euv3">Expanded UV at the inflated (0,legY) corner.</param>
    public static void InflateQuad(
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        float outsetPx, Vector3 nx, Vector3 ny,
        ref Vector3 v0, ref float lenX, ref float lenY,
        out Vector2 euv0, out Vector2 euv1, out Vector2 euv2, out Vector2 euv3)
    {
        float fu = outsetPx / lenX;   // UV fraction of one pad along U
        float fv = outsetPx / lenY;   // UV fraction of one pad along V
        var duX = uv1 - uv0;          // U-axis UV delta across lenX
        var duY = uv3 - uv0;          // V-axis UV delta across lenY
        euv0 = uv0 - fu * duX - fv * duY;
        euv1 = uv1 + fu * duX - fv * duY;
        euv3 = uv3 - fu * duX + fv * duY;
        euv2 = uv2 + fu * duX + fv * duY;
        euv0 = Vector2.Clamp(euv0, Vector2.Zero, Vector2.One);
        euv1 = Vector2.Clamp(euv1, Vector2.Zero, Vector2.One);
        euv2 = Vector2.Clamp(euv2, Vector2.Zero, Vector2.One);
        euv3 = Vector2.Clamp(euv3, Vector2.Zero, Vector2.One);
        v0 = v0 - outsetPx * nx - outsetPx * ny;
        lenX += 2f * outsetPx;
        lenY += 2f * outsetPx;
    }

    /// <summary>
    /// Inflates a TRIANGLE face by <paramref name="outsetPx"/> on all three
    /// edges. A triangle sprite renders the right-triangle inscribed in its
    /// rectangle with corners A(0,0)=V0, B(lenX,0)=V1, C(0,lenY)=V2. All three
    /// edges are pushed outward by <paramref name="outsetPx"/> in sprite-local
    /// space (bottom leg y=0 → y=-d, left leg x=0 → x=-d, hypotenuse outward
    /// along its local normal). The legs stay axis-aligned so the inflated
    /// triangle is still a right triangle with its right angle at A'=(-d,-d);
    /// the sprite origin shifts by -d on both axes and the legs grow to
    /// legX/legY, so the shared unit-triangle clip is simply rebuilt at the new
    /// size. UVs at the three inflated corners are emitted (a 3-point affine,
    /// [0,1]-clamped) so the interior stays registered.
    /// </summary>
    /// <param name="uv0">Triangle UV at corner A (V0).</param>
    /// <param name="uv1">Triangle UV at corner B (V1).</param>
    /// <param name="uv2">Triangle UV at corner C (V2).</param>
    /// <param name="outsetPx">Outset in on-screen pixels.</param>
    /// <param name="nx">Unit local X axis in world space.</param>
    /// <param name="ny">Unit local Y axis in world space.</param>
    /// <param name="v0">Sprite origin (mutated to the inflated origin A').</param>
    /// <param name="lenX">Sprite X extent (mutated to the inflated leg legX).</param>
    /// <param name="lenY">Sprite Y extent (mutated to the inflated leg legY).</param>
    /// <param name="euv0">Expanded UV at inflated corner A'.</param>
    /// <param name="euv1">Expanded UV at inflated corner B'.</param>
    /// <param name="euv2">Expanded UV at inflated corner C'.</param>
    public static void InflateTriangle(
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        float outsetPx, Vector3 nx, Vector3 ny,
        ref Vector3 v0, ref float lenX, ref float lenY,
        out Vector2 euv0, out Vector2 euv1, out Vector2 euv2)
    {
        float d = outsetPx;
        float invLenX = 1f / lenX;
        float invLenY = 1f / lenY;
        // Hypotenuse x/lenX + y/lenY = 1 has unit normal (1/lenX, 1/lenY)/g.
        float g = MathF.Sqrt(invLenX * invLenX + invLenY * invLenY);

        var du = uv1 - uv0;               // U-axis UV delta across lenX
        var dv = uv2 - uv0;               // V-axis UV delta across lenY
        float aX = d * invLenX;           // -d along local x, in du units
        float aY = d * invLenY;           // -d along local y, in dv units
        float hB = d * (g + invLenY);     // B''s push past lenX (hypotenuse+leg)
        float hC = d * (g + invLenX);     // C''s push past lenY (hypotenuse+leg)
        euv0 = uv0 - aX * du - aY * dv;                 // A'(-d,-d)
        euv1 = uv0 + (1f + hB) * du - aY * dv;          // B'
        euv2 = uv0 - aX * du + (1f + hC) * dv;          // C'
        euv0 = Vector2.Clamp(euv0, Vector2.Zero, Vector2.One);
        euv1 = Vector2.Clamp(euv1, Vector2.Zero, Vector2.One);
        euv2 = Vector2.Clamp(euv2, Vector2.Zero, Vector2.One);

        v0 = v0 - d * nx - d * ny;
        lenX = lenX + d + d * lenX * (g + invLenY);
        lenY = lenY + d + d * lenY * (g + invLenX);
    }
}

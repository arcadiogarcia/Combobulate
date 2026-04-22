using System;
using System.Numerics;

namespace Combobulate.Sorting;

/// <summary>
/// Centralized geometric sign/equality predicates. Every place in the
/// sort pipeline that makes a back-vs-front, behind-vs-in-front, or
/// degenerate-vs-real decision on a floating-point quantity should go
/// through one of these methods rather than testing <c>&gt; 0</c>,
/// <c>== 0</c>, etc. directly.
///
/// <para>This file is the single place to audit when "what's the
/// epsilon for X?" comes up. Each predicate documents the <b>scale</b>
/// of the quantity it consumes (cosine, signed distance, squared
/// length, …) so a future maintainer cannot accidentally apply a
/// distance-scale tolerance to a cosine, or vice versa.</para>
///
/// <para><b>Why this matters.</b> Geometric code is full of expressions
/// that mathematically equal zero on a non-empty subset of valid
/// inputs (camera perpendicular to face, segment parallel to plane,
/// triangle collinear). At those inputs the value computed in single
/// precision is some small noise of unpredictable sign — e.g.
/// <c>cos(π/2)</c> returns ~|4.4e-8| on .NET, with the actual sign
/// depending on the math library. A naive <c>x &gt; 0</c> test then
/// makes a fp-random decision; downstream amplifiers (perspective
/// divides, UV interpolation across a tiny edge, paint-order swaps)
/// can blow that noise up to a visible artifact — see the yaw=90
/// cover smear bug. Going through these named predicates makes the
/// classification explicit, scale-aware, and grep-able.</para>
/// </summary>
public static class GeometryPredicates
{
    // ---------- Cosine-scale tolerances (dimensionless, unit-vector dot products) ----------

    /// <summary>
    /// Cosine-scale epsilon for back-face cull tests. ≈ cos(89.94°),
    /// i.e. quads within ~0.06° of edge-on are culled. Comfortably
    /// above the fp noise produced by <c>cos(π/2)</c> in single
    /// precision (~|4.4e-8| on .NET; sign and magnitude vary across
    /// math libraries) and well below anything human-perceptible (a
    /// sub-pixel-wide sliver).
    /// </summary>
    public const float CosineEpsilon = 1e-3f;

    /// <summary>
    /// Decides whether a face is visible to the camera, given the
    /// view-space Z component of its (already-rotated, unit-length)
    /// normal. Combobulate's view convention has +Z pointing toward
    /// the camera, so a face is front-facing iff <paramref name="viewNormalZ"/>
    /// exceeds <see cref="CosineEpsilon"/>.
    /// </summary>
    /// <remarks>Scale: <b>cosine</b> (dimensionless dot product of two unit vectors).</remarks>
    public static bool IsFrontFacing(float viewNormalZ) => viewNormalZ > CosineEpsilon;

    /// <summary>
    /// Perspective-aware front-face test. Use when the renderer applies a
    /// perspective divide so view rays diverge from a finite camera point
    /// at <c>(0, 0, cameraZ)</c> in view space. A face is visible iff its
    /// rotated normal points back toward that camera point relative to its
    /// own (rotated) centroid:
    /// <c>dot(viewNormal, cameraPos − viewCentroid) &gt; CosineEpsilon · |cameraPos − viewCentroid|</c>.
    /// <para>The Combobulate convention places the camera at <c>+Z</c> in view
    /// space (perspective formula <c>w' = 1 − z/d</c>). Under orthographic the test
    /// uses a constant <c>(0,0,1)</c> ray and a face whose normal is perpendicular
    /// to that axis is always culled — even if a back-side companion quad with the
    /// inverted normal would also be edge-on. Under perspective the per-face ray
    /// from camera to (off-centre) centroid has a non-zero in-plane component, so
    /// e.g. the inside face of a book cover at pitch=±90° correctly tests as
    /// front-facing while the outside face tests as back-facing. Falls back to the
    /// orthographic <see cref="IsFrontFacing"/> rule when <paramref name="cameraZ"/> ≤ 0
    /// or the camera-to-centroid ray is degenerate.</para>
    /// </summary>
    /// <remarks>Scale: <b>cosine</b> after a length normalisation.</remarks>
    public static bool IsFrontFacingPerspective(Vector3 viewNormal, Vector3 viewCentroid, float cameraZ)
    {
        if (cameraZ <= 0f) return IsFrontFacing(viewNormal.Z);
        var ray = new Vector3(-viewCentroid.X, -viewCentroid.Y, cameraZ - viewCentroid.Z);
        var lenSq = ray.LengthSquared();
        if (lenSq < DivisorEpsilon) return IsFrontFacing(viewNormal.Z);
        // Compare dot to eps·|ray| without an actual sqrt: dot/|ray| > eps  ⇔  dot² > eps²·lenSq (with sign of dot retained).
        var dot = Vector3.Dot(viewNormal, ray);
        if (dot <= 0f) return false;
        return dot * dot > (CosineEpsilon * CosineEpsilon) * lenSq;
    }

    /// <summary>
    /// Decides which side of a partitioning plane the camera lies on,
    /// given <c>dot(plane.Normal, cameraDirection)</c>. Returns
    /// <c>+1</c> for the +Normal hemisphere, <c>-1</c> for the −Normal
    /// hemisphere. Returns <c>0</c> if the camera is grazing the plane
    /// to within <see cref="CosineEpsilon"/> — in which case BSP
    /// traversal order genuinely doesn't matter (the splitter's projected
    /// width is ~zero), and the caller should pick a deterministic
    /// fallback rather than letting fp noise choose.
    /// </summary>
    /// <remarks>Scale: <b>cosine</b>.</remarks>
    public static int CameraHemisphere(float planeNormalDotCameraDir)
    {
        if (planeNormalDotCameraDir >  CosineEpsilon) return +1;
        if (planeNormalDotCameraDir < -CosineEpsilon) return -1;
        return 0;
    }

    // ---------- Distance-scale tolerances (object-space, unit-cube assumption) ----------

    /// <summary>
    /// Distance-scale epsilon: signed distance of a vertex from a plane,
    /// in object space. 1e-4 in unit-cube object space corresponds to
    /// sub-pixel noise on a 1000-pixel render and is well above
    /// single-precision rounding error for centred meshes.
    /// </summary>
    public const float DistanceEpsilon = 1e-4f;

    /// <summary>
    /// Classifies a signed distance as <c>+1</c> (front), <c>-1</c> (back),
    /// or <c>0</c> (on plane within ±<see cref="DistanceEpsilon"/>).
    /// </summary>
    /// <remarks>Scale: <b>signed distance</b> in object space.</remarks>
    public static int SignedDistanceSide(float signedDistance)
    {
        if (signedDistance >  DistanceEpsilon) return +1;
        if (signedDistance < -DistanceEpsilon) return -1;
        return 0;
    }

    // ---------- Divide-safety tolerances (denominator scale) ----------

    /// <summary>
    /// Epsilon used to guard divisions in geometric formulae. Smaller
    /// than <see cref="DistanceEpsilon"/> because it gates a divide,
    /// not a classify: a tiny-but-nonzero denom multiplied by 10⁶ can
    /// still produce a usable result, but a denom of 10⁻³⁰ explodes.
    /// </summary>
    public const float DivisorEpsilon = 1e-6f;

    /// <summary>
    /// Computes the segment parameter <c>t</c> at which a segment whose
    /// endpoints have signed distances <paramref name="da"/> and
    /// <paramref name="db"/> from a plane crosses that plane. Returns
    /// <c>true</c> iff the divide was numerically safe; <c>false</c>
    /// (with <paramref name="t"/> = 0.5) when the denominator's
    /// magnitude is below <see cref="DivisorEpsilon"/>, indicating a
    /// near-parallel or degenerate segment for which the caller should
    /// treat the result as a degenerate fallback rather than a true
    /// intersection.
    /// </summary>
    /// <remarks>
    /// Scale: <b>signed distance difference</b>. The result is clamped
    /// to <c>[0,1]</c> in either branch so callers can use it as an
    /// interpolation weight without further bounds-checking.
    /// </remarks>
    public static bool TryComputeSegmentParam(float da, float db, out float t)
    {
        var denom = da - db;
        if (MathF.Abs(denom) < DivisorEpsilon)
        {
            t = 0.5f;
            return false;
        }
        t = da / denom;
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        return true;
    }

    // ---------- Squared-length tolerances (degeneracy filters) ----------

    /// <summary>
    /// Squared-length epsilon for 3D cross products that detect
    /// collinear-vertex degenerate triangles. Squared scale, so 1e-14
    /// in linear length is ≈ 1e-7. Tight because false positives here
    /// merely skip emitting a sliver, but false negatives propagate a
    /// genuinely degenerate triangle into the splitter.
    /// </summary>
    public const float CrossLengthSquaredEpsilon3D = 1e-14f;

    /// <summary>
    /// Squared-length epsilon for 2D edge normals (Separating Axis Theorem
    /// degenerate-edge guard). One order of magnitude looser than
    /// <see cref="CrossLengthSquaredEpsilon3D"/> because 2D projected
    /// edges can shrink under foreshortening but never to absolute zero
    /// for non-degenerate input.
    /// </summary>
    public const float EdgeLengthSquaredEpsilon2D = 1e-12f;

    /// <summary>True when a 3D cross product is below the degenerate-triangle threshold.</summary>
    /// <remarks>Scale: <b>squared length</b>.</remarks>
    public static bool IsDegenerateCross3D(Vector3 cross)
        => cross.LengthSquared() < CrossLengthSquaredEpsilon3D;

    /// <summary>True when a 2D edge is too short to define a meaningful SAT axis.</summary>
    /// <remarks>Scale: <b>squared length</b>.</remarks>
    public static bool IsDegenerateEdge2D(Vector2 edge)
        => edge.LengthSquared() < EdgeLengthSquaredEpsilon2D;
}

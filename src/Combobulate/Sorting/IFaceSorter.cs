using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Sorting;

/// <summary>
/// A back-to-front polygon sorter. Implementations are constructed from
/// an <see cref="ObjGeometry"/>'s cached quads (typically once at
/// geometry-cache time), then queried per frame with the current view
/// rotation. Each implementation produces an output permutation of
/// source-quad indices in painter's order: the index at position 0 is
/// drawn first (back), the index at the last position drawn last (front).
///
/// <para>Sorters are responsible for back-face culling — they write
/// <see cref="bool"/> flags into the supplied visible-buffer alongside
/// the order array. Quads marked invisible are skipped by the renderer
/// regardless of their position in the order.</para>
///
/// <para>Implementations:
/// <list type="bullet">
///   <item><see cref="TopologicalSorter"/> — original O(n²) plane test + Kahn's sort.</item>
///   <item><see cref="NewellSorter"/> — Newell's 5-test cascade with on-demand splits.</item>
///   <item><see cref="BspSorter"/> — pre-built BSP tree, O(n) per-frame walk.</item>
/// </list>
/// </para>
/// </summary>
public interface IFaceSorter
{
    /// <summary>
    /// Back-face cull threshold. Re-exports
    /// <see cref="GeometryPredicates.CosineEpsilon"/> so existing call
    /// sites and tests that reference <c>IFaceSorter.CullEpsilon</c>
    /// continue to work; new code should prefer
    /// <see cref="GeometryPredicates.IsFrontFacing"/> directly.
    ///
    /// <para>See <see cref="GeometryPredicates"/> for the full epsilon
    /// hierarchy (cosine / distance / divisor / squared-length scales).</para>
    /// </summary>
    public const float CullEpsilon = GeometryPredicates.CosineEpsilon;
    /// <summary>
    /// Number of source quads this sorter was built for. The caller's
    /// <paramref name="orderBuffer"/> and <paramref name="visibleBuffer"/>
    /// passed to <see cref="Sort"/> must be at least this large.
    /// </summary>
    int QuadCount { get; }

    /// <summary>
    /// Compute the painter's order for the given view rotation. Writes
    /// the back-to-front permutation of cached-quad indices into
    /// <paramref name="orderBuffer"/> (the first <c>visibleCount</c>
    /// entries are valid on return) and per-quad cull flags into
    /// <paramref name="visibleBuffer"/>.
    /// </summary>
    /// <param name="rotation">Object-to-view rotation (object-space normals are transformed by this for cull).</param>
    /// <param name="orderBuffer">Output: back-to-front cached-quad indices. Length ≥ <see cref="QuadCount"/>.</param>
    /// <param name="visibleBuffer">Output: cull flag per cached-quad. Length ≥ <see cref="QuadCount"/>.</param>
    /// <param name="cameraDistance">
    /// Optional perspective camera distance in view-space units (positive = perspective with
    /// camera at <c>(0, 0, cameraDistance)</c>; <c>0</c> = orthographic, default). When
    /// non-zero the cull uses <see cref="GeometryPredicates.IsFrontFacingPerspective"/> so
    /// off-centre faces whose normals are perpendicular to the global view axis can still
    /// be visible (e.g. the inside face of a book cover at pitch=±90°).
    /// </param>
    /// <param name="cullMarginCos">
    /// Optional widening of the front-facing cone in cosine units (= <c>sin(maxAngleError)</c>).
    /// <c>0</c> (default) reproduces the strict back-face cull. Positive values keep faces
    /// visible up to <c>asin(cullMarginCos)</c> past the back-cull boundary — useful when
    /// the CPU-supplied <paramref name="rotation"/> may lag the GPU-drawn rotation by a
    /// known maximum angle (e.g. one frame of an animation), so cull/draw cannot disagree
    /// at boundary faces.
    /// </param>
    /// <returns>Number of valid entries written into <paramref name="orderBuffer"/>.</returns>
    int Sort(Matrix4x4 rotation, int[] orderBuffer, bool[] visibleBuffer, float cameraDistance = 0f, float cullMarginCos = 0f);
}

/// <summary>
/// Factory: builds a sorter for the given geometry + algorithm choice.
/// </summary>
public static class FaceSorterFactory
{
    public static IFaceSorter Create(SortAlgorithm algorithm, ObjGeometry geometry)
    {
        return algorithm switch
        {
            SortAlgorithm.Topological => new TopologicalSorter(geometry),
            SortAlgorithm.Newell      => new NewellSorter(geometry),
            SortAlgorithm.Bsp         => new BspSorter(geometry),
            _                         => new BspSorter(geometry),
        };
    }
}

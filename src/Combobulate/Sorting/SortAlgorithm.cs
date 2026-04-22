namespace Combobulate.Sorting;

/// <summary>
/// Selects the back-to-front polygon ordering algorithm used by a
/// <see cref="Combobulate"/> control.
///
/// <para>All three algorithms produce a permutation of source-quad indices
/// (back to front), so they can be swapped without changing the renderer.
/// They differ in correctness, per-frame cost, and tolerance for "weird"
/// geometry.</para>
/// </summary>
public enum SortAlgorithm
{
    /// <summary>
    /// Original O(n²) plane-test + topological-sort. Cheap, but produces
    /// incorrect order whenever two quads mutually straddle each other's
    /// planes (a 2-cycle in the predecessor graph), at which point it falls
    /// back to centroid-Z ordering. Kept for backward compatibility.
    /// </summary>
    Topological = 0,

    /// <summary>
    /// Newell's algorithm with the classic 5-test cascade. Splits polygons
    /// against each other on demand when a real cycle is exposed. Per-frame
    /// cost: O(n²) compare worst case, near-O(n) typical. Handles
    /// deforming/animated geometry naturally because it keeps no persistent
    /// per-geometry data structure.
    /// </summary>
    Newell = 1,

    /// <summary>
    /// Binary Space Partitioning tree built once per geometry from the
    /// triangulated input mesh, with build-time splits that make the tree
    /// cycle-free for any view direction. Per-frame cost: O(n) tree walk.
    /// Recommended default for static rigid geometry.
    /// </summary>
    Bsp = 2,
}

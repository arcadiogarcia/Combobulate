namespace Combobulate;

/// <summary>
/// Controls how a host-supplied custom brush (see
/// <see cref="Combobulate.Caching.ObjMaterial.CustomBrushFactory"/>) is mapped
/// onto a face.
/// </summary>
public enum BrushMapping
{
    /// <summary>
    /// The brush is cropped to each face's UV cell (the default for atlas
    /// textures): every face samples its own sub-rectangle of the source.
    /// A separate brush instance is created per face so each can carry its
    /// own UV <c>TransformMatrix</c>.
    /// </summary>
    PerFaceUv,

    /// <summary>
    /// The brush samples in screen (global) space, ignoring each face's 3D
    /// transform. One brush instance is shared across every face of the
    /// material. This is the mode a <c>CompositionBackdropBrush</c>
    /// (or an effect wrapping one) wants: every face shows the app content
    /// composited behind the die at that face's on-screen position, giving
    /// "see-through" / glass faces that stay screen-aligned as the die rolls.
    /// </summary>
    ScreenSpace,

    /// <summary>
    /// The brush is a normal <c>CompositionSurfaceBrush</c> over a host-supplied
    /// surface, but Combobulate PROJECTS it into screen space for you: each face
    /// gets its own brush instance whose <c>TransformMatrix</c> is driven by a
    /// composition <see cref="Microsoft.UI.Composition.ExpressionAnimation"/> off
    /// the live die rotation, so the face samples the surface texel that sits
    /// under it on screen — no source layer is composited behind the die, so the
    /// surroundings stay the app's own background and only the faces reveal the
    /// surface (a true "glass" die over the desktop wallpaper). Unlike
    /// <see cref="ScreenSpace"/> (which needs a backdrop brush and a visible
    /// source layer), this needs only a surface + a host screen→texel matrix.
    ///
    /// <para>The transform is affine (<c>Matrix3x2</c>), so it ignores the
    /// perspective divide; steeply-tilted faces may let the surface "swim" a
    /// pixel or two versus the face edge when perspective is enabled.</para>
    /// </summary>
    ScreenProjected,
}

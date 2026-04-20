using System.Collections.Generic;
using System.Numerics;

namespace Combobulate.Parsing;

/// <summary>
/// A single material parsed from an MTL file. Only the diffuse channel
/// (<see cref="DiffuseColor"/>, <see cref="DiffuseTexture"/>) is consumed by the
/// sprite renderer; all other fields are captured for downstream use.
///
/// <para>Colors are represented as linear RGB triples (each component 0..1) so this
/// type stays free of any UI-platform dependency and is testable against a portable
/// <c>net10.0</c> TFM.</para>
/// </summary>
public sealed class MtlMaterial
{
    public string Name { get; init; } = "";
    public Vector3? DiffuseColor { get; set; }
    public Vector3? AmbientColor { get; set; }
    public Vector3? SpecularColor { get; set; }
    public Vector3? EmissiveColor { get; set; }
    public float? SpecularExponent { get; set; }
    public float? OpticalDensity { get; set; }
    /// <summary>Parsed but not consumed by renderer (transparency is out of scope).</summary>
    public float Opacity { get; set; } = 1f;
    public int IlluminationModel { get; set; }
    public MtlTextureRef? DiffuseTexture { get; set; }
    public MtlTextureRef? AmbientTexture { get; set; }
    public MtlTextureRef? SpecularTexture { get; set; }
    public MtlTextureRef? OpacityTexture { get; set; }
    public MtlTextureRef? BumpTexture { get; set; }

    /// <summary>Any unknown / extra keywords encountered (keyword -&gt; raw remainder).</summary>
    public IDictionary<string, string> ExtraKeywords { get; } = new Dictionary<string, string>();
}

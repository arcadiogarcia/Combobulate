#if WINAPPSDK
using Microsoft.UI.Composition;
#else
using Windows.UI.Composition;
#endif

namespace Combobulate.Caching;

/// <summary>
/// Process-wide default lighting parameters backed by a
/// <see cref="CompositionPropertySet"/>. Changes propagate to every lit
/// Combobulate face immediately via composition expression animation —
/// no effect-graph rebuild needed.
///
/// <para>Lazily initialized on the first lit material resolve — apps that
/// never set <see cref="ObjMaterial.NormalMap"/> pay zero cost.</para>
/// </summary>
public static class LightingDefaults
{
    private static CompositionPropertySet? _props;

    /// <summary>The shared property set that all lit effect brushes reference.
    /// Null until the first lit material is resolved.</summary>
    public static CompositionPropertySet? PropertySet => _props;

    // Defaults
    public const float DefaultAmbient  = 0.4f;
    public const float DefaultDiffuse  = 1.0f;
    public const float DefaultSpecular = 0.2f;
    public const float DefaultShine    = 16f;

    /// <summary>
    /// Lazily creates and returns the shared property set, seeded with
    /// default lighting values. Called from <c>MaterialResolver</c> on the
    /// first lit material.
    /// </summary>
    internal static CompositionPropertySet GetOrCreate(Compositor compositor)
    {
        if (_props is not null) return _props;

        var ps = compositor.CreatePropertySet();
        ps.InsertScalar("AmbientAmount",  DefaultAmbient);
        ps.InsertScalar("DiffuseAmount",  DefaultDiffuse);
        ps.InsertScalar("SpecularAmount", DefaultSpecular);
        ps.InsertScalar("SpecularShine",  DefaultShine);
        _props = ps;
        return ps;
    }

    /// <summary>Write a named scalar into the shared property set.</summary>
    public static void Set(string name, float value)
        => _props?.InsertScalar(name, value);

    /// <summary>Read a named scalar from the shared property set.</summary>
    public static float Get(string name)
    {
        if (_props is null) return name switch
        {
            "AmbientAmount"  => DefaultAmbient,
            "DiffuseAmount"  => DefaultDiffuse,
            "SpecularAmount" => DefaultSpecular,
            "SpecularShine"  => DefaultShine,
            _ => 0f,
        };
        _props.TryGetScalar(name, out float v);
        return v;
    }
}

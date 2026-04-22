// Polyfill: lets us link Combobulate.Caching.ObjGeometry into the plain net10.0 test
// assembly. The real Windows.UI.Color comes from Windows.Foundation.UniversalApiContract,
// which we don't reference here. We only need the shape — none of the sort tests
// inspect colour values.
namespace Windows.UI;

public readonly struct Color
{
    public byte A { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    private Color(byte a, byte r, byte g, byte b) { A = a; R = r; G = g; B = b; }
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
}

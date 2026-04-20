using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace Combobulate.Parsing;

/// <summary>
/// Parser for a practical subset of the Wavefront MTL format. Mirrors
/// <see cref="ObjParser"/>'s leniency: unknown keywords are captured into
/// <see cref="MtlMaterial.ExtraKeywords"/> rather than failing.
/// </summary>
public static class MtlParser
{
    private static readonly char[] Whitespace = new[] { ' ', '\t' };

    public static MtlParseResult Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    public static MtlParseResult Parse(TextReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));

        var materials = new Dictionary<string, MtlMaterial>(StringComparer.Ordinal);
        var errors = new List<MtlParseError>();
        MtlMaterial? current = null;

        int lineNumber = 0;
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            lineNumber++;

            var hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
            if (line.Length == 0) continue;

            var firstWs = IndexOfWhitespace(line);
            string keyword;
            string rest;
            if (firstWs < 0) { keyword = line; rest = ""; }
            else { keyword = line.Substring(0, firstWs); rest = line.Substring(firstWs + 1).Trim(); }

            switch (keyword)
            {
                case "newmtl":
                    if (string.IsNullOrWhiteSpace(rest))
                    {
                        errors.Add(new MtlParseError(lineNumber, MtlParseErrorKind.MissingArgument,
                            "'newmtl' requires a name."));
                        current = null;
                        break;
                    }
                    current = new MtlMaterial { Name = rest };
                    materials[rest] = current;
                    break;

                case "Kd":
                    AssignColor(rest, lineNumber, current, errors, (m, c) => m.DiffuseColor = c, "Kd");
                    break;
                case "Ka":
                    AssignColor(rest, lineNumber, current, errors, (m, c) => m.AmbientColor = c, "Ka");
                    break;
                case "Ks":
                    AssignColor(rest, lineNumber, current, errors, (m, c) => m.SpecularColor = c, "Ks");
                    break;
                case "Ke":
                    AssignColor(rest, lineNumber, current, errors, (m, c) => m.EmissiveColor = c, "Ke");
                    break;

                case "Ns":
                    AssignFloat(rest, lineNumber, current, errors, (m, v) => m.SpecularExponent = v, "Ns");
                    break;
                case "Ni":
                    AssignFloat(rest, lineNumber, current, errors, (m, v) => m.OpticalDensity = v, "Ni");
                    break;
                case "d":
                    AssignFloat(rest, lineNumber, current, errors, (m, v) => m.Opacity = v, "d");
                    break;
                case "Tr":
                    AssignFloat(rest, lineNumber, current, errors, (m, v) => m.Opacity = 1f - v, "Tr");
                    break;

                case "illum":
                    if (current == null) { errors.Add(Orphaned(lineNumber, "illum")); break; }
                    if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var illum))
                        current.IlluminationModel = illum;
                    else
                        errors.Add(new MtlParseError(lineNumber, MtlParseErrorKind.InvalidNumber,
                            $"'illum' value '{rest}' is not an integer."));
                    break;

                case "map_Kd":
                    AssignTexture(rest, lineNumber, current, errors, (m, t) => m.DiffuseTexture = t, "map_Kd");
                    break;
                case "map_Ka":
                    AssignTexture(rest, lineNumber, current, errors, (m, t) => m.AmbientTexture = t, "map_Ka");
                    break;
                case "map_Ks":
                    AssignTexture(rest, lineNumber, current, errors, (m, t) => m.SpecularTexture = t, "map_Ks");
                    break;
                case "map_d":
                    AssignTexture(rest, lineNumber, current, errors, (m, t) => m.OpacityTexture = t, "map_d");
                    break;
                case "map_Bump":
                case "bump":
                case "map_Kn":
                    AssignTexture(rest, lineNumber, current, errors, (m, t) => m.BumpTexture = t, keyword);
                    break;

                default:
                    if (current != null)
                        current.ExtraKeywords[keyword] = rest;
                    break;
            }
        }

        return new MtlParseResult(materials, errors);
    }

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ' ' || s[i] == '\t') return i;
        }
        return -1;
    }

    private static MtlParseError Orphaned(int line, string keyword) =>
        new(line, MtlParseErrorKind.OrphanedDirective, $"'{keyword}' appeared before any 'newmtl'.");

    private static void AssignColor(
        string rest, int line, MtlMaterial? current, List<MtlParseError> errors,
        Action<MtlMaterial, Vector3> set, string keyword)
    {
        if (current == null) { errors.Add(Orphaned(line, keyword)); return; }
        var tokens = rest.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 1)
        {
            errors.Add(new MtlParseError(line, MtlParseErrorKind.MissingArgument,
                $"'{keyword}' requires at least one component."));
            return;
        }
        if (tokens[0] == "spectral" || tokens[0] == "xyz") return;
        if (!TryFloat(tokens[0], out var r))
        {
            errors.Add(new MtlParseError(line, MtlParseErrorKind.InvalidNumber,
                $"'{keyword}' has a non-numeric component."));
            return;
        }
        float g = r, b = r;
        if (tokens.Length >= 3)
        {
            if (!TryFloat(tokens[1], out g) || !TryFloat(tokens[2], out b))
            {
                errors.Add(new MtlParseError(line, MtlParseErrorKind.InvalidNumber,
                    $"'{keyword}' has a non-numeric component."));
                return;
            }
        }
        set(current, new Vector3(r, g, b));
    }

    private static void AssignFloat(
        string rest, int line, MtlMaterial? current, List<MtlParseError> errors,
        Action<MtlMaterial, float> set, string keyword)
    {
        if (current == null) { errors.Add(Orphaned(line, keyword)); return; }
        if (!TryFloat(rest.Trim(), out var f))
        {
            errors.Add(new MtlParseError(line, MtlParseErrorKind.InvalidNumber,
                $"'{keyword}' value '{rest}' is not numeric."));
            return;
        }
        set(current, f);
    }

    private static void AssignTexture(
        string rest, int line, MtlMaterial? current, List<MtlParseError> errors,
        Action<MtlMaterial, MtlTextureRef> set, string keyword)
    {
        if (current == null) { errors.Add(Orphaned(line, keyword)); return; }
        if (string.IsNullOrWhiteSpace(rest))
        {
            errors.Add(new MtlParseError(line, MtlParseErrorKind.MissingArgument,
                $"'{keyword}' requires a filename."));
            return;
        }

        var scale = Vector2.One;
        var offset = Vector2.Zero;
        var clamp = false;
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);

        var tokens = rest.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < tokens.Length && tokens[i].StartsWith("-", StringComparison.Ordinal))
        {
            var opt = tokens[i];
            switch (opt)
            {
                case "-s":
                    if (TryReadVec2(tokens, ref i, out var s)) scale = s;
                    break;
                case "-o":
                    if (TryReadVec2(tokens, ref i, out var o)) offset = o;
                    break;
                case "-clamp":
                    if (i + 1 < tokens.Length)
                    {
                        clamp = tokens[i + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
                        i += 2;
                    }
                    else { i++; }
                    break;
                case "-blendu":
                case "-blendv":
                case "-cc":
                case "-bm":
                case "-mm":
                case "-imfchan":
                case "-texres":
                case "-t":
                    {
                        var argCount = opt == "-mm" ? 2 : 1;
                        var sb = new System.Text.StringBuilder();
                        for (int j = 0; j < argCount && i + 1 + j < tokens.Length && !tokens[i + 1 + j].StartsWith("-"); j++)
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(tokens[i + 1 + j]);
                        }
                        extra[opt] = sb.ToString();
                        i += 1 + argCount;
                        break;
                    }
                default:
                    extra[opt] = "";
                    i++;
                    break;
            }
        }

        if (i >= tokens.Length)
        {
            errors.Add(new MtlParseError(line, MtlParseErrorKind.MissingArgument,
                $"'{keyword}' has options but no filename."));
            return;
        }

        var path = string.Join(" ", tokens, i, tokens.Length - i);
        set(current, new MtlTextureRef
        {
            Path = path,
            Scale = scale,
            Offset = offset,
            Clamp = clamp,
            ExtraOptions = extra,
        });
    }

    private static bool TryReadVec2(string[] tokens, ref int i, out Vector2 vec)
    {
        vec = Vector2.Zero;
        float u = 1f, v = 1f;
        int consumed = 0;
        for (int k = 0; k < 3 && i + 1 + k < tokens.Length; k++)
        {
            var t = tokens[i + 1 + k];
            if (t.StartsWith("-", StringComparison.Ordinal) || !TryFloat(t, out var f))
                break;
            if (k == 0) u = f;
            else if (k == 1) v = f;
            consumed++;
        }
        i += 1 + consumed;
        if (consumed == 0) return false;
        vec = new Vector2(u, consumed >= 2 ? v : u);
        return true;
    }

    private static bool TryFloat(string s, out float f) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
}

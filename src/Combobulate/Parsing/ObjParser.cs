using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace Combobulate.Parsing;

/// <summary>
/// Parser for a practical, interoperable subset of the Wavefront OBJ format.
///
/// Supports:
///   v, vt, vn (with optional w / defaulted components)
///   f with v, v/vt, v//vn, v/vt/vn references (quads only)
///   negative (relative) indices
///   o, g (multi-name), usemtl, mtllib, s
/// Ignores (without error):
///   l, p, vp, curv, curv2, surf, parm, trim, hole, scrv, sp, end, and any unknown keyword
/// Recoverable errors (collected, parsing continues):
///   invalid numbers, out-of-range indices, malformed vertex refs, non-quad faces, missing args
///
/// This parser intentionally accepts only quads. Triangles or n-gons in <c>f</c> directives
/// produce an <see cref="ObjParseErrorKind.UnsupportedFaceArity"/> error and the face is skipped.
/// </summary>
public static class ObjParser
{
    private const string DefaultGroup = "default";

    private static readonly char[] Whitespace = new[] { ' ', '\t' };

    public static ObjParseResult Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    public static ObjParseResult Parse(TextReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));

        var model = new ObjModel();
        var errors = new List<ObjParseError>();

        string? currentObject = null;
        IReadOnlyList<string> currentGroups = new[] { DefaultGroup };
        string? currentMaterial = null;
        int currentSmoothing = 0;

        int lineNumber = 0;
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            lineNumber++;

            // Strip comments. '#' anywhere starts a comment to end of line.
            var hashIdx = raw.IndexOf('#');
            var line = hashIdx >= 0 ? raw.Substring(0, hashIdx) : raw;
            line = line.Trim();
            if (line.Length == 0) continue;

            var tokens = line.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var keyword = tokens[0];
            var args = new ArraySegment<string>(tokens, 1, tokens.Length - 1);

            switch (keyword)
            {
                case "v":
                    ParseVertex(args, lineNumber, model, errors);
                    break;
                case "vt":
                    ParseTexCoord(args, lineNumber, model, errors);
                    break;
                case "vn":
                    ParseNormal(args, lineNumber, model, errors);
                    break;
                case "f":
                    ParseFace(
                        args, lineNumber, model, errors,
                        currentObject, currentGroups, currentMaterial, currentSmoothing);
                    break;
                case "o":
                    currentObject = args.Count > 0
                        ? string.Join(" ", args.ToArray())
                        : null;
                    break;
                case "g":
                    currentGroups = args.Count > 0
                        ? (IReadOnlyList<string>)args.ToArray()
                        : new[] { DefaultGroup };
                    break;
                case "usemtl":
                    currentMaterial = args.Count > 0
                        ? string.Join(" ", args.ToArray())
                        : null;
                    break;
                case "mtllib":
                    foreach (var lib in args)
                        model.MaterialLibraries.Add(lib);
                    break;
                case "s":
                    currentSmoothing = ParseSmoothing(args, lineNumber, errors);
                    break;

                // Explicitly recognized but ignored.
                case "l":
                case "p":
                case "vp":
                case "curv":
                case "curv2":
                case "surf":
                case "parm":
                case "trim":
                case "hole":
                case "scrv":
                case "sp":
                case "end":
                    break;

                default:
                    // Unknown keyword: skip silently per spec.
                    break;
            }
        }

        return new ObjParseResult(model, errors);
    }

    private static void ParseVertex(
        ArraySegment<string> args, int line, ObjModel model, List<ObjParseError> errors)
    {
        if (args.Count < 3)
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.MissingArgument,
                $"'v' requires at least 3 components, got {args.Count}."));
            return;
        }

        if (!TryParseFloat(args.Array![args.Offset + 0], out var x) ||
            !TryParseFloat(args.Array![args.Offset + 1], out var y) ||
            !TryParseFloat(args.Array![args.Offset + 2], out var z))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
                "'v' contains a non-numeric component."));
            return;
        }

        var w = 1f;
        if (args.Count >= 4 && !TryParseFloat(args.Array![args.Offset + 3], out w))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
                "'v' has a non-numeric w component."));
            return;
        }

        model.Positions.Add(new Vector4(x, y, z, w));
    }

    private static void ParseTexCoord(
        ArraySegment<string> args, int line, ObjModel model, List<ObjParseError> errors)
    {
        if (args.Count < 1)
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.MissingArgument,
                "'vt' requires at least 1 component."));
            return;
        }

        if (!TryParseFloat(args.Array![args.Offset + 0], out var u))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
                "'vt' has a non-numeric u component."));
            return;
        }

        var v = 0f;
        if (args.Count >= 2 && !TryParseFloat(args.Array![args.Offset + 1], out v))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
                "'vt' has a non-numeric v component."));
            return;
        }

        var w = 0f;
        if (args.Count >= 3 && !TryParseFloat(args.Array![args.Offset + 2], out w))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
                "'vt' has a non-numeric w component."));
            return;
        }

        model.TexCoords.Add(new Vector3(u, v, w));
    }

    private static void ParseNormal(
        ArraySegment<string> args, int line, ObjModel model, List<ObjParseError> errors)
    {
        if (args.Count < 3)
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.MissingArgument,
                $"'vn' requires 3 components, got {args.Count}."));
            return;
        }

        if (!TryParseFloat(args.Array![args.Offset + 0], out var x) ||
            !TryParseFloat(args.Array![args.Offset + 1], out var y) ||
            !TryParseFloat(args.Array![args.Offset + 2], out var z))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
                "'vn' contains a non-numeric component."));
            return;
        }

        model.Normals.Add(new Vector3(x, y, z));
    }

    private static int ParseSmoothing(
        ArraySegment<string> args, int line, List<ObjParseError> errors)
    {
        if (args.Count == 0)
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.MissingArgument,
                "'s' requires an argument."));
            return 0;
        }

        var token = args.Array![args.Offset];
        if (token.Equals("off", StringComparison.OrdinalIgnoreCase) || token == "0")
            return 0;

        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return id;

        errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidNumber,
            $"'s' has non-numeric argument '{token}'."));
        return 0;
    }

    private static void ParseFace(
        ArraySegment<string> args, int line, ObjModel model, List<ObjParseError> errors,
        string? currentObject, IReadOnlyList<string> currentGroups,
        string? currentMaterial, int currentSmoothing)
    {
        if (args.Count != 4)
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.UnsupportedFaceArity,
                $"Only quads are supported; face has {args.Count} vertices."));
            return;
        }

        var verts = new ObjVertex[4];
        for (int i = 0; i < 4; i++)
        {
            if (!TryParseFaceVertex(
                    args.Array![args.Offset + i],
                    line,
                    model.Positions.Count,
                    model.TexCoords.Count,
                    model.Normals.Count,
                    errors,
                    out verts[i]))
            {
                return; // Skip the whole face if any corner is bad.
            }
        }

        model.Quads.Add(new ObjQuad(
            verts[0], verts[1], verts[2], verts[3],
            currentObject, currentGroups, currentMaterial, currentSmoothing));
    }

    private static bool TryParseFaceVertex(
        string token, int line,
        int positionCount, int texCoordCount, int normalCount,
        List<ObjParseError> errors,
        out ObjVertex vertex)
    {
        vertex = default;

        if (string.IsNullOrEmpty(token))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidVertexReference,
                "Empty face vertex reference."));
            return false;
        }

        // Split on '/', preserving empty middle slot for "v//vn".
        var parts = token.Split('/');
        if (parts.Length < 1 || parts.Length > 3)
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidVertexReference,
                $"Malformed face vertex reference '{token}'."));
            return false;
        }

        if (!TryResolveIndex(parts[0], positionCount, out var posIdx))
        {
            errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidIndex,
                $"Position index '{parts[0]}' is invalid or out of range (have {positionCount})."));
            return false;
        }

        int? uvIdx = null;
        if (parts.Length >= 2 && parts[1].Length > 0)
        {
            if (!TryResolveIndex(parts[1], texCoordCount, out var u))
            {
                errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidIndex,
                    $"Texcoord index '{parts[1]}' is invalid or out of range (have {texCoordCount})."));
                return false;
            }
            uvIdx = u;
        }

        int? nIdx = null;
        if (parts.Length == 3 && parts[2].Length > 0)
        {
            if (!TryResolveIndex(parts[2], normalCount, out var n))
            {
                errors.Add(new ObjParseError(line, ObjParseErrorKind.InvalidIndex,
                    $"Normal index '{parts[2]}' is invalid or out of range (have {normalCount})."));
                return false;
            }
            nIdx = n;
        }

        vertex = new ObjVertex(posIdx, uvIdx, nIdx);
        return true;
    }

    /// <summary>
    /// Resolves an OBJ 1-based or negative index against the current array size to a 0-based index.
    /// Returns false if the token isn't an integer or the resolved index is out of range.
    /// </summary>
    private static bool TryResolveIndex(string token, int count, out int zeroBased)
    {
        zeroBased = -1;
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
            return false;

        if (raw == 0) return false; // OBJ indices are never 0.

        var resolved = raw > 0 ? raw - 1 : count + raw; // -1 => count-1
        if (resolved < 0 || resolved >= count) return false;

        zeroBased = resolved;
        return true;
    }

    private static bool TryParseFloat(string token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

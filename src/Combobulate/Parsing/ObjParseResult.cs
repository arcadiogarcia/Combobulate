using System.Collections.Generic;

namespace Combobulate.Parsing;

/// <summary>Result of an <see cref="ObjParser"/> invocation.</summary>
public sealed class ObjParseResult
{
    public ObjParseResult(ObjModel model, IReadOnlyList<ObjParseError> errors)
    {
        Model = model;
        Errors = errors;
    }

    /// <summary>The parsed model. Always non-null; partial on error.</summary>
    public ObjModel Model { get; }

    /// <summary>Recoverable errors collected during parsing. Empty means clean parse.</summary>
    public IReadOnlyList<ObjParseError> Errors { get; }

    /// <summary>True when no errors were collected.</summary>
    public bool Success => Errors.Count == 0;
}

/// <summary>A single recoverable error encountered during parsing.</summary>
public sealed class ObjParseError
{
    public ObjParseError(int lineNumber, ObjParseErrorKind kind, string message)
    {
        LineNumber = lineNumber;
        Kind = kind;
        Message = message;
    }

    /// <summary>1-based line number where the error occurred.</summary>
    public int LineNumber { get; }

    public ObjParseErrorKind Kind { get; }

    public string Message { get; }

    public override string ToString() => $"[line {LineNumber}] {Kind}: {Message}";
}

public enum ObjParseErrorKind
{
    InvalidNumber,
    InvalidIndex,
    InvalidVertexReference,
    UnsupportedFaceArity,
    MissingArgument,
}

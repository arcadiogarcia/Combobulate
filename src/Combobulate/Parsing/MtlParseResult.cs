using System.Collections.Generic;

namespace Combobulate.Parsing;

/// <summary>Result of an <see cref="MtlParser"/> invocation.</summary>
public sealed class MtlParseResult
{
    public MtlParseResult(IReadOnlyDictionary<string, MtlMaterial> materials, IReadOnlyList<MtlParseError> errors)
    {
        Materials = materials;
        Errors = errors;
    }

    public IReadOnlyDictionary<string, MtlMaterial> Materials { get; }
    public IReadOnlyList<MtlParseError> Errors { get; }
    public bool Success => Errors.Count == 0;
}

public sealed class MtlParseError
{
    public MtlParseError(int lineNumber, MtlParseErrorKind kind, string message)
    {
        LineNumber = lineNumber;
        Kind = kind;
        Message = message;
    }

    public int LineNumber { get; }
    public MtlParseErrorKind Kind { get; }
    public string Message { get; }

    public override string ToString() => $"[mtl line {LineNumber}] {Kind}: {Message}";
}

public enum MtlParseErrorKind
{
    InvalidNumber,
    MissingArgument,
    OrphanedDirective,
}

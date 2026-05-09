namespace DotnetCel.Types;

/// <summary>
/// CEL primitive scalar kinds. Mirrors <c>cel.expr.Type.PrimitiveType</c> but lives in pure C# space.
/// </summary>
public enum PrimitiveKind
{
    Bool,
    Int,
    Uint,
    Double,
    String,
    Bytes,
}

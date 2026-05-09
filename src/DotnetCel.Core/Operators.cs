namespace DotnetCel;

/// <summary>
/// Canonical CEL operator function names. CEL represents every operator as a Call to a
/// well-known function, so <c>1 + 2</c> parses to <c>Call("_+_", [1, 2])</c>. These constants
/// are referenced by the parser, checker, and runtime.
/// </summary>
public static class Operators
{
    public const string LogicalNot = "!_";
    public const string Negate = "-_";
    public const string Add = "_+_";
    public const string Subtract = "_-_";
    public const string Multiply = "_*_";
    public const string Divide = "_/_";
    public const string Modulo = "_%_";
    public const string Equal = "_==_";
    public const string NotEqual = "_!=_";
    public const string Less = "_<_";
    public const string LessEqual = "_<=_";
    public const string Greater = "_>_";
    public const string GreaterEqual = "_>=_";
    public const string In = "@in";
    public const string LogicalOr = "_||_";
    public const string LogicalAnd = "_&&_";
    public const string Conditional = "_?_:_";
    public const string Index = "_[_]";
    public const string OptIndex = "_[?_]";
    public const string OptSelect = "_?._";

    /// <summary>
    /// Internal helper used by <c>all</c>/<c>exists</c> macros: returns false only when its
    /// argument is the boolean <c>false</c>; errors and other non-bool values are treated as
    /// "not strictly false" so the comprehension keeps iterating.
    /// </summary>
    public const string NotStrictlyFalse = "@not_strictly_false";

    /// <summary>The synthetic accumulator name used by macro expansion.</summary>
    public const string AccumulatorName = "__result__";
}

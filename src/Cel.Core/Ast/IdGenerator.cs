namespace Cel.Ast;

/// <summary>
/// Issues monotonically increasing AST node ids. Not thread-safe; one instance per parse.
/// </summary>
public sealed class IdGenerator
{
    private long _next;

    public long Next() => ++_next;

    public long Peek() => _next + 1;
}

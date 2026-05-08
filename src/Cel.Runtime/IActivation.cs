namespace Cel.Runtime;

/// <summary>
/// The lookup surface for an evaluation. A CEL identifier is resolved by asking the activation
/// for its name; the activation may return any CLR value (including <c>null</c>), which the
/// runtime wraps via <see cref="ValueAdapter"/> on demand.
/// </summary>
/// <remarks>
/// Implementations should be conservative: <c>TryResolve</c> returns <c>true</c> only when the
/// activation is responsible for the name. Returning <c>true</c> with <c>value=null</c> means
/// "I have a binding for this name and its value is null"; returning <c>false</c> means "I do
/// not provide this name." Chained activations rely on this distinction.
/// </remarks>
public interface IActivation
{
    bool TryResolve(string name, out object? value);
}

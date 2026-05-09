using Cel.Values;

namespace Cel;

/// <summary>
/// Implementation of one CEL overload. Receives the call's actual arguments (already evaluated
/// and adapted to <see cref="CelValue"/>) and returns the result. Errors are returned as
/// <see cref="ErrorValue"/>; throwing should be reserved for programmer errors.
/// </summary>
public delegate CelValue OverloadFn(ReadOnlySpan<CelValue> args);

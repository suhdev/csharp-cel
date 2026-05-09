using System.Collections.Immutable;
using DotnetCel.Types;
using DotnetCel.Values;

namespace DotnetCel.Extensions;

/// <summary>
/// Port of cel-go's <c>ext/encoders</c>: <c>base64.encode(bytes) → string</c> and
/// <c>base64.decode(string) → bytes</c>. Both are namespaced under <c>base64</c> and reached
/// via the checker's namespaced-call resolution.
/// </summary>
public sealed class EncodersExtension : ICelExtension
{
    public static readonly EncodersExtension Instance = new();
    private EncodersExtension() { }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        b.Function("base64.encode",
            new OverloadDecl("base64_encode_bytes", [CelTypes.Bytes], CelTypes.String));
        b.Function("base64.decode",
            new OverloadDecl("base64_decode_string", [CelTypes.String], CelTypes.Bytes));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        bind("base64_encode_bytes", static a =>
        {
            var bytes = ((BytesValue)a[0]).Value;
            return CelValue.Of(Convert.ToBase64String(bytes.AsSpan()));
        });
        bind("base64_decode_string", static a =>
        {
            try
            {
                var bytes = Convert.FromBase64String(((StringValue)a[0]).Value);
                return new BytesValue(ImmutableArray.Create(bytes));
            }
            catch (FormatException)
            {
                return CelValue.Error("invalid base64 string");
            }
        });
    }
}

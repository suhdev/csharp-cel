using DotnetCel.Types;
using DotnetCel.Values;
using Google.Protobuf.Reflection;

namespace DotnetCel.Conformance;

/// <summary>
/// Registers proto enum types as 1-arg constructor functions: <c>EnumName(int)</c> validates
/// the int fits in int32 (returning it as-is), and <c>EnumName(string)</c> looks up an enum
/// value by name and returns its numeric value. Both surface as plain CEL <c>int</c>, which
/// matches what cel-go's "legacy" enum mode produces and what the conformance corpus expects
/// once the call has been resolved.
/// </summary>
internal sealed class ProtoEnumExtension : ICelExtension
{
    private readonly IReadOnlyList<EnumDescriptor> _enums;

    public ProtoEnumExtension(IReadOnlyList<EnumDescriptor> enums)
    {
        _enums = enums;
    }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        foreach (var enumDesc in _enums)
        {
            b.Function(enumDesc.FullName,
                new OverloadDecl(IntId(enumDesc), [CelTypes.Int], CelTypes.Int),
                new OverloadDecl(StringId(enumDesc), [CelTypes.String], CelTypes.Int));
        }
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        foreach (var enumDesc in _enums)
        {
            var captured = enumDesc;
            bind(IntId(captured), a =>
            {
                var i = ((IntValue)a[0]).Value;
                if (i < int.MinValue || i > int.MaxValue)
                {
                    return CelValue.Error($"{captured.FullName}: value {i} out of int32 range");
                }
                return new EnumValue(captured.FullName, i);
            });
            bind(StringId(captured), a =>
            {
                var s = ((StringValue)a[0]).Value;
                var v = captured.FindValueByName(s);
                if (v is null)
                {
                    return CelValue.Error($"unknown enum value: {captured.FullName}.{s}");
                }
                return new EnumValue(captured.FullName, v.Number);
            });
        }
    }

    private static string IntId(EnumDescriptor d) => $"{d.FullName.Replace('.', '_')}_from_int";
    private static string StringId(EnumDescriptor d) => $"{d.FullName.Replace('.', '_')}_from_string";
}

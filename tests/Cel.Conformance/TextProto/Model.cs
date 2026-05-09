using System.Collections.Generic;
using System.Linq;

namespace Cel.Conformance.TextProto;

/// <summary>
/// Generic tree representation of a parsed textproto file. Field types stay loosely typed
/// (string / number / bool / ident / nested message / list of values) so the conformance
/// runner can interpret them per-field-type without needing generated proto bindings.
/// </summary>
internal abstract record TextProtoValue;

internal sealed record TextProtoString(string Value) : TextProtoValue;

internal sealed record TextProtoInt(long Value) : TextProtoValue;

internal sealed record TextProtoUint(ulong Value) : TextProtoValue;

internal sealed record TextProtoDouble(double Value) : TextProtoValue;

internal sealed record TextProtoBool(bool Value) : TextProtoValue
{
    public static readonly TextProtoBool True = new(true);
    public static readonly TextProtoBool False = new(false);
}

/// <summary>An unquoted identifier — used by enum values (<c>NULL_VALUE</c>, <c>INT64</c>) and bool keywords.</summary>
internal sealed record TextProtoIdent(string Value) : TextProtoValue;

internal sealed record TextProtoMessageValue(TextProtoMessage Message) : TextProtoValue;

internal sealed record TextProtoListValue(IReadOnlyList<TextProtoValue> Items) : TextProtoValue;

internal sealed record TextProtoField(string Name, TextProtoValue Value);

internal sealed record TextProtoMessage(IReadOnlyList<TextProtoField> Fields)
{
    public IEnumerable<TextProtoField> All(string name)
    {
        foreach (var f in Fields)
        {
            if (f.Name == name)
            {
                yield return f;
            }
        }
    }

    public TextProtoField? FirstOrNull(string name)
    {
        foreach (var f in Fields)
        {
            if (f.Name == name)
            {
                return f;
            }
        }
        return null;
    }

    public TextProtoMessage? Sub(string name) =>
        FirstOrNull(name)?.Value is TextProtoMessageValue m ? m.Message : null;

    public string? Str(string name) => (FirstOrNull(name)?.Value as TextProtoString)?.Value;
    public long? Int(string name) => (FirstOrNull(name)?.Value as TextProtoInt)?.Value;
    public bool? Bool(string name) => (FirstOrNull(name)?.Value as TextProtoBool)?.Value;

    public IEnumerable<TextProtoMessage> SubAll(string name)
    {
        foreach (var f in All(name))
        {
            if (f.Value is TextProtoMessageValue m)
            {
                yield return m.Message;
            }
        }
    }
}

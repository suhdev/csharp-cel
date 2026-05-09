namespace DotnetCel.Conformance.TextProto;

/// <summary>
/// Generic tree representation of a parsed textproto file. Field types stay loosely typed
/// (string / number / bool / ident / nested message / list of values) so the conformance
/// runner can interpret them per-field-type without needing generated proto bindings.
/// </summary>
internal abstract record TextProtoValue;

internal sealed record TextProtoString(string Value) : TextProtoValue
{
    /// <summary>
    /// Decode the textproto string as a proto-text byte sequence and interpret as UTF-8. The
    /// parser stores each byte from the source as one .NET char (via Latin-1 read of the file
    /// + escape-as-byte handling); this method re-encodes those low bytes and decodes them as
    /// UTF-8 so the resulting <see cref="string"/> is an idiomatic .NET Unicode string.
    /// </summary>
    public string Decoded
    {
        get
        {
            var bytes = new byte[Value.Length];
            for (var i = 0; i < Value.Length; i++)
            {
                bytes[i] = (byte)Value[i];
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }
}

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

    public string? Str(string name) => (FirstOrNull(name)?.Value as TextProtoString)?.Decoded;
    public string? StrRaw(string name) => (FirstOrNull(name)?.Value as TextProtoString)?.Value;
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

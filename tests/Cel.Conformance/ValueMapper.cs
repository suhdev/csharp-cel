using System.Collections.Immutable;
using Cel.Conformance.TextProto;
using Cel.Types;
using Cel.Values;

namespace Cel.Conformance;

/// <summary>
/// Converts cel-spec textproto fragments — <c>cel.expr.Value</c>, <c>cel.expr.ExprValue</c>,
/// <c>cel.expr.Type</c>, <c>cel.expr.Decl</c> — into the corresponding runtime types in this
/// implementation.
/// </summary>
internal static class ValueMapper
{
    /// <summary>Parse a <c>cel.expr.Value</c> message.</summary>
    public static CelValue ParseValue(TextProtoMessage msg)
    {
        foreach (var f in msg.Fields)
        {
            switch (f.Name)
            {
                case "null_value":
                    return CelValue.Null;
                case "bool_value":
                    return CelValue.Of(((TextProtoBool)f.Value).Value);
                case "int64_value":
                    return CelValue.Of(AsLong(f.Value));
                case "uint64_value":
                    return CelValue.Of(AsUlong(f.Value));
                case "double_value":
                    return CelValue.Of(AsDouble(f.Value));
                case "string_value":
                    return CelValue.Of(((TextProtoString)f.Value).Decoded);
                case "bytes_value":
                    return ParseBytes((TextProtoString)f.Value);
                case "list_value":
                    return ParseList(((TextProtoMessageValue)f.Value).Message);
                case "map_value":
                    return ParseMap(((TextProtoMessageValue)f.Value).Message);
                case "type_value":
                    return new TypeValue(NamedType(((TextProtoString)f.Value).Value));
                case "enum_value":
                {
                    var enumMsg = ((TextProtoMessageValue)f.Value).Message;
                    return CelValue.Of(enumMsg.Int("value") ?? 0);
                }
            }
        }
        // An entirely empty Value message represents the default — bool false in proto3.
        return CelValue.False;
    }

    /// <summary>Parse a <c>cel.expr.ExprValue</c> wrapper. Returns null if the wrapped value is an unknown.</summary>
    public static (CelValue? Value, bool IsUnknown, bool IsError) ParseExprValue(TextProtoMessage msg)
    {
        if (msg.Sub("value") is { } valueMsg)
        {
            return (ParseValue(valueMsg), false, false);
        }
        if (msg.FirstOrNull("error") is not null)
        {
            return (null, false, true);
        }
        if (msg.FirstOrNull("unknown") is not null)
        {
            return (null, true, false);
        }
        return (CelValue.False, false, false);
    }

    /// <summary>Parse a <c>cel.expr.Type</c> message.</summary>
    public static CelType? ParseType(TextProtoMessage msg)
    {
        foreach (var f in msg.Fields)
        {
            switch (f.Name)
            {
                case "primitive":
                    return PrimitiveFromIdent(((TextProtoIdent)f.Value).Value);
                case "wrapper":
                    return WrapperFromIdent(((TextProtoIdent)f.Value).Value);
                case "list_type":
                    var l = ((TextProtoMessageValue)f.Value).Message;
                    var elemMsg = l.Sub("elem_type");
                    return CelTypes.List(elemMsg is null ? CelTypes.Dyn : ParseType(elemMsg) ?? CelTypes.Dyn);
                case "map_type":
                    var m = ((TextProtoMessageValue)f.Value).Message;
                    var keyMsg = m.Sub("key_type");
                    var valMsg = m.Sub("value_type");
                    return CelTypes.Map(
                        keyMsg is null ? CelTypes.Dyn : ParseType(keyMsg) ?? CelTypes.Dyn,
                        valMsg is null ? CelTypes.Dyn : ParseType(valMsg) ?? CelTypes.Dyn);
                case "message_type":
                    return CelTypes.Object(((TextProtoString)f.Value).Value);
                case "type_param":
                    return CelTypes.TypeParam(((TextProtoString)f.Value).Value);
                case "type":
                    var inner = ((TextProtoMessageValue)f.Value).Message;
                    return CelTypes.TypeOf(ParseType(inner) ?? CelTypes.Dyn);
                case "dyn":
                    return CelTypes.Dyn;
                case "null":
                    return CelTypes.Null;
                case "error":
                    return CelTypes.Error;
                case "well_known":
                    return WellKnownFromIdent(((TextProtoIdent)f.Value).Value);
            }
        }
        return null;
    }

    /// <summary>Build a <see cref="VariableDecl"/> from a textproto <c>cel.expr.Decl</c> message.</summary>
    public static VariableDecl? ParseVariableDecl(TextProtoMessage msg)
    {
        var name = msg.Str("name");
        if (name is null)
        {
            return null;
        }
        if (msg.Sub("ident") is { } identMsg && identMsg.Sub("type") is { } typeMsg)
        {
            return new VariableDecl(name, ParseType(typeMsg) ?? CelTypes.Dyn);
        }
        return null;
    }

    public static bool IsFunctionDecl(TextProtoMessage msg) => msg.Sub("function") is not null;

    // ── helpers ──

    private static CelValue ParseList(TextProtoMessage msg)
    {
        var builder = ImmutableArray.CreateBuilder<CelValue>();
        foreach (var v in msg.SubAll("values"))
        {
            builder.Add(ParseValue(v));
        }
        return new ListValue(builder.ToImmutable());
    }

    private static CelValue ParseMap(TextProtoMessage msg)
    {
        var builder = ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        foreach (var entry in msg.SubAll("entries"))
        {
            var k = entry.Sub("key");
            var v = entry.Sub("value");
            if (k is null || v is null)
            {
                continue;
            }
            builder[ParseValue(k)] = ParseValue(v);
        }
        return new MapValue(builder.ToImmutable());
    }

    private static CelValue ParseBytes(TextProtoString s)
    {
        var src = s.Value;
        var bytes = new byte[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            bytes[i] = (byte)src[i];
        }
        return new BytesValue(ImmutableArray.Create(bytes));
    }

    private static long AsLong(TextProtoValue v) => v switch
    {
        TextProtoInt i => i.Value,
        TextProtoUint u => (long)u.Value,
        TextProtoDouble d => (long)d.Value,
        _ => throw new FormatException($"expected int, got {v.GetType().Name}"),
    };

    private static ulong AsUlong(TextProtoValue v) => v switch
    {
        TextProtoUint u => u.Value,
        TextProtoInt i => i.Value < 0 ? unchecked((ulong)i.Value) : (ulong)i.Value,
        _ => throw new FormatException($"expected uint, got {v.GetType().Name}"),
    };

    private static double AsDouble(TextProtoValue v) => v switch
    {
        TextProtoDouble d => d.Value,
        TextProtoInt i => i.Value,
        TextProtoUint u => u.Value,
        // proto text format also accepts the bare keywords `inf`, `nan` for double values.
        TextProtoIdent id when id.Value.Equals("inf", StringComparison.OrdinalIgnoreCase)
            || id.Value.Equals("infinity", StringComparison.OrdinalIgnoreCase) => double.PositiveInfinity,
        TextProtoIdent id when id.Value.Equals("nan", StringComparison.OrdinalIgnoreCase) => double.NaN,
        _ => throw new FormatException($"expected double, got {v.GetType().Name}"),
    };

    private static CelType PrimitiveFromIdent(string name) => name switch
    {
        "BOOL" => CelTypes.Bool,
        "INT64" => CelTypes.Int,
        "UINT64" => CelTypes.Uint,
        "DOUBLE" => CelTypes.Double,
        "STRING" => CelTypes.String,
        "BYTES" => CelTypes.Bytes,
        _ => CelTypes.Dyn,
    };

    private static CelType WrapperFromIdent(string name) => name switch
    {
        "BOOL" => CelTypes.BoolWrapper,
        "INT64" => CelTypes.IntWrapper,
        "UINT64" => CelTypes.UintWrapper,
        "DOUBLE" => CelTypes.DoubleWrapper,
        "STRING" => CelTypes.StringWrapper,
        "BYTES" => CelTypes.BytesWrapper,
        _ => CelTypes.Dyn,
    };

    private static CelType WellKnownFromIdent(string name) => name switch
    {
        "ANY" => CelTypes.Object("google.protobuf.Any"),
        "TIMESTAMP" => CelTypes.Timestamp,
        "DURATION" => CelTypes.Duration,
        _ => CelTypes.Dyn,
    };

    /// <summary>Build a <see cref="CelType"/> from a string type name (used for type_value).</summary>
    private static CelType NamedType(string name) => name switch
    {
        "bool" => CelTypes.Bool,
        "int" => CelTypes.Int,
        "uint" => CelTypes.Uint,
        "double" => CelTypes.Double,
        "string" => CelTypes.String,
        "bytes" => CelTypes.Bytes,
        "null_type" => CelTypes.Null,
        "list" => CelTypes.List(CelTypes.Dyn),
        "map" => CelTypes.Map(CelTypes.Dyn, CelTypes.Dyn),
        "type" => CelTypes.Type,
        "google.protobuf.Duration" => CelTypes.Duration,
        "google.protobuf.Timestamp" => CelTypes.Timestamp,
        _ => CelTypes.Object(name),
    };
}

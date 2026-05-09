using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cel;
using Cel.Types;
using Cel.Values;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using PbAny = Google.Protobuf.WellKnownTypes.Any;
using PbBoolValue = Google.Protobuf.WellKnownTypes.BoolValue;
using PbBytesValue = Google.Protobuf.WellKnownTypes.BytesValue;
using PbDoubleValue = Google.Protobuf.WellKnownTypes.DoubleValue;
using PbDuration = Google.Protobuf.WellKnownTypes.Duration;
using PbFloatValue = Google.Protobuf.WellKnownTypes.FloatValue;
using PbInt32Value = Google.Protobuf.WellKnownTypes.Int32Value;
using PbInt64Value = Google.Protobuf.WellKnownTypes.Int64Value;
using PbStringValue = Google.Protobuf.WellKnownTypes.StringValue;
using PbTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using PbUInt32Value = Google.Protobuf.WellKnownTypes.UInt32Value;
using PbUInt64Value = Google.Protobuf.WellKnownTypes.UInt64Value;

namespace Cel.Conformance;

/// <summary>
/// <see cref="ITypeProvider"/> backed by Google.Protobuf reflection. Knows about a fixed set of
/// message descriptors (registered at construction) and uses <see cref="IMessage"/> reflection
/// to read and write fields on instances of those types.
/// </summary>
/// <remarks>
/// <para>
/// Field reading projects proto values into shapes the rest of the runtime understands:
/// <see cref="RepeatedField{T}"/> / <see cref="MapField{TKey, TValue}"/> are <see cref="IList"/> /
/// <see cref="IDictionary"/> already, so <see cref="ValueAdapter"/> handles them as is. Wrapper
/// types (<c>google.protobuf.{Bool,Int32,...}Value</c>) unwrap to their inner value when set
/// and surface as <c>null</c> when the field is unset.
/// </para>
/// <para>
/// <c>has(msg.field)</c> uses each field's protocol presence rules: explicit presence for
/// proto2 / proto3 <c>optional</c> / oneof / message / wrapper fields, non-empty for repeated
/// and map fields, and "differs from default" for plain proto3 scalars.
/// </para>
/// </remarks>
public sealed class ProtoTypeProvider : ITypeProvider
{
    private readonly Dictionary<string, MessageDescriptor> _byName;

    public ProtoTypeProvider(IEnumerable<MessageDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _byName = descriptors.ToDictionary(static d => d.FullName, StringComparer.Ordinal);
    }

    public bool KnowsType(string typeName) => _byName.ContainsKey(typeName);

    public CelType? ResolveType(string typeName) =>
        _byName.ContainsKey(typeName) ? CelTypes.Object(typeName) : null;

    public bool IsManagedInstance(object instance) => instance is IMessage;

    public string? TypeNameOf(object instance) =>
        instance is IMessage msg ? msg.Descriptor.FullName : null;

    /// <summary>
    /// Project a managed instance to a CEL primitive when applicable. Wrapper messages
    /// (<c>google.protobuf.{Bool,Int32,...}Value</c>), <c>Timestamp</c>, and <c>Duration</c>
    /// all unwrap to native CEL values; arbitrary other messages keep their <see cref="ObjectValue"/>
    /// wrapping by returning null here.
    /// </summary>
    public CelValue? Project(object instance)
    {
        if (instance is not IMessage msg)
        {
            return null;
        }
        return msg switch
        {
            PbBoolValue x => CelValue.Of(x.Value),
            PbInt32Value x => CelValue.Of((long)x.Value),
            PbInt64Value x => CelValue.Of(x.Value),
            PbUInt32Value x => CelValue.Of((ulong)x.Value),
            PbUInt64Value x => CelValue.Of(x.Value),
            PbFloatValue x => CelValue.Of((double)x.Value),
            PbDoubleValue x => CelValue.Of(x.Value),
            PbStringValue x => CelValue.Of(x.Value),
            PbBytesValue x => CelValue.Of(System.Collections.Immutable.ImmutableArray.Create(x.Value.ToByteArray())),
            PbTimestamp ts => CelValue.Of(CelTimestamp.FromDateTimeOffset(ts.ToDateTimeOffset())),
            PbDuration d => CelValue.Of(new CelDuration(d.Seconds * CelDuration.NanosPerSecond + d.Nanos)),
            _ => null,
        };
    }

    /// <summary>
    /// Build an <see cref="IMessage"/> instance from a textproto body. Used by the conformance
    /// harness to parse expected-value proto messages out of <c>object_value { ... }</c>
    /// fragments.
    /// </summary>
    internal IMessage? BuildFromTextProto(string typeName, TextProto.TextProtoMessage body)
    {
        if (!_byName.TryGetValue(typeName, out var desc))
        {
            return null;
        }
        var msg = (IMessage)Activator.CreateInstance(desc.ClrType)!;
        foreach (var f in body.Fields)
        {
            var fd = FindField(desc, f.Name);
            if (fd is null)
            {
                continue;
            }
            ApplyTextProtoToField(msg, fd, f.Value);
        }
        return msg;
    }

    private void ApplyTextProtoToField(IMessage msg, FieldDescriptor fd, TextProto.TextProtoValue value)
    {
        if (fd.IsMap)
        {
            // Map entries arrive as nested messages with `key:` and `value:` fields. Each entry
            // is its own TextProtoMessageValue at this level (repeated map entry).
            return; // TODO: map support
        }
        if (fd.IsRepeated)
        {
            var list = (IList)fd.Accessor.GetValue(msg);
            list.Add(ConvertTextProto(fd, value));
            return;
        }
        var cv = ConvertTextProto(fd, value);
        if (cv is null)
        {
            return;
        }
        try { fd.Accessor.SetValue(msg, cv); }
        catch
        {
            // Incompatible value or unsupported field shape — leave at default.
        }
    }

    private object? ConvertTextProto(FieldDescriptor fd, TextProto.TextProtoValue v)
    {
        // Wrapper-typed message fields are codegen'd as nullable primitives, so a textproto
        // value like `single_int32_wrapper: 432` should produce a boxed int rather than a
        // Int32Value instance.
        if (fd.FieldType == FieldType.Message && fd.MessageType is { } mt && IsWrapperType(mt.FullName))
        {
            return ConvertWrapperPrimitive(mt.FullName, v);
        }
        return (fd.FieldType, v) switch
        {
            (FieldType.Bool, TextProto.TextProtoBool b) => b.Value,
            (FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32, TextProto.TextProtoInt i) => (int)i.Value,
            (FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64, TextProto.TextProtoInt i) => i.Value,
            (FieldType.UInt32 or FieldType.Fixed32, TextProto.TextProtoInt i) when i.Value >= 0 => (uint)i.Value,
            (FieldType.UInt64 or FieldType.Fixed64, TextProto.TextProtoInt i) when i.Value >= 0 => (ulong)i.Value,
            (FieldType.UInt32 or FieldType.Fixed32, TextProto.TextProtoUint u) => (uint)u.Value,
            (FieldType.UInt64 or FieldType.Fixed64, TextProto.TextProtoUint u) => u.Value,
            (FieldType.Float, TextProto.TextProtoDouble d) => (float)d.Value,
            (FieldType.Float, TextProto.TextProtoInt i) => (float)i.Value,
            (FieldType.Double, TextProto.TextProtoDouble d) => d.Value,
            (FieldType.Double, TextProto.TextProtoInt i) => (double)i.Value,
            (FieldType.String, TextProto.TextProtoString s) => s.Decoded,
            (FieldType.Bytes, TextProto.TextProtoString s) => ByteString.CopyFrom(EncodeBytes(s.Value)),
            (FieldType.Enum, TextProto.TextProtoIdent id) => fd.EnumType.FindValueByName(id.Value)?.Number ?? 0,
            (FieldType.Enum, TextProto.TextProtoInt i) => (int)i.Value,
            (FieldType.Message, TextProto.TextProtoMessageValue inner) =>
                BuildFromTextProto(fd.MessageType.FullName, inner.Message),
            _ => null,
        };
    }

    private static bool IsWrapperType(string typeName) => typeName switch
    {
        "google.protobuf.BoolValue" or "google.protobuf.Int32Value" or "google.protobuf.Int64Value"
            or "google.protobuf.UInt32Value" or "google.protobuf.UInt64Value"
            or "google.protobuf.FloatValue" or "google.protobuf.DoubleValue"
            or "google.protobuf.StringValue" or "google.protobuf.BytesValue" => true,
        _ => false,
    };

    private static object? ConvertWrapperPrimitive(string typeName, TextProto.TextProtoValue v)
    {
        // textproto for `single_int32_wrapper { value: 432 }` arrives as a TextProtoMessageValue;
        // unwrap to its inner `value` field before matching the typed primitive shapes.
        if (v is TextProto.TextProtoMessageValue mv)
        {
            var inner = mv.Message.FirstOrNull("value")?.Value;
            if (inner is null)
            {
                return null;
            }
            v = inner;
        }
        return (typeName, v) switch
        {
            ("google.protobuf.BoolValue", TextProto.TextProtoBool b) => b.Value,
            ("google.protobuf.Int32Value", TextProto.TextProtoInt i) => (int)i.Value,
            ("google.protobuf.Int64Value", TextProto.TextProtoInt i) => i.Value,
            ("google.protobuf.UInt32Value", TextProto.TextProtoUint u) => (uint)u.Value,
            ("google.protobuf.UInt32Value", TextProto.TextProtoInt i) when i.Value >= 0 => (uint)i.Value,
            ("google.protobuf.UInt64Value", TextProto.TextProtoUint u) => u.Value,
            ("google.protobuf.UInt64Value", TextProto.TextProtoInt i) when i.Value >= 0 => (ulong)i.Value,
            ("google.protobuf.FloatValue", TextProto.TextProtoDouble d) => (float)d.Value,
            ("google.protobuf.FloatValue", TextProto.TextProtoInt i) => (float)i.Value,
            ("google.protobuf.DoubleValue", TextProto.TextProtoDouble d) => d.Value,
            ("google.protobuf.DoubleValue", TextProto.TextProtoInt i) => (double)i.Value,
            ("google.protobuf.StringValue", TextProto.TextProtoString s) => s.Decoded,
            ("google.protobuf.BytesValue", TextProto.TextProtoString s) => ByteString.CopyFrom(EncodeBytes(s.Value)),
            _ => null,
        };
    }

    private static byte[] EncodeBytes(string raw)
    {
        var bytes = new byte[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            bytes[i] = (byte)raw[i];
        }
        return bytes;
    }

    // ── reads ──

    public bool TryReadField(object instance, string field, out object? value)
    {
        if (instance is not IMessage msg)
        {
            value = null;
            return false;
        }
        var fd = FindField(msg.Descriptor, field);
        if (fd is null)
        {
            value = null;
            return false;
        }
        var raw = fd.Accessor.GetValue(msg);
        value = ProjectValue(fd, raw);
        return true;
    }

    public bool HasField(object instance, string field)
    {
        if (instance is not IMessage msg)
        {
            return false;
        }
        var fd = FindField(msg.Descriptor, field);
        if (fd is null)
        {
            return false;
        }
        if (fd.IsMap)
        {
            return fd.Accessor.GetValue(msg) is ICollection c && c.Count > 0;
        }
        if (fd.IsRepeated)
        {
            return fd.Accessor.GetValue(msg) is ICollection c && c.Count > 0;
        }
        if (fd.HasPresence)
        {
            return fd.Accessor.HasValue(msg);
        }
        // Implicit-presence (plain proto3 scalar): present iff differs from default.
        var v = fd.Accessor.GetValue(msg);
        return !IsDefaultScalar(fd, v);
    }

    /// <summary>
    /// Project a proto field value to a form the rest of the runtime operates on.
    /// Repeated / map fields pass through as <see cref="IList"/> / <see cref="IDictionary"/>;
    /// enums become their numeric int value; message fields stay as <see cref="IMessage"/>
    /// instances and are projected to wrappers / well-known types lazily by
    /// <see cref="Project"/> in the evaluator.
    /// </summary>
    private static object? ProjectValue(FieldDescriptor fd, object? raw)
    {
        if (raw is null) { return null; }
        if (fd.IsMap || fd.IsRepeated) { return raw; }
        if (fd.FieldType == FieldType.Enum)
        {
            return Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static bool IsDefaultScalar(FieldDescriptor fd, object? v) => fd.FieldType switch
    {
        FieldType.Bool => v is bool b && !b,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => v is int i && i == 0,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => v is long l && l == 0,
        FieldType.UInt32 or FieldType.Fixed32 => v is uint u && u == 0,
        FieldType.UInt64 or FieldType.Fixed64 => v is ulong u && u == 0,
        FieldType.Float => v is float f && f == 0f,
        FieldType.Double => v is double d && d == 0.0,
        FieldType.String => v is string s && s.Length == 0,
        FieldType.Bytes => v is ByteString b && b.Length == 0,
        FieldType.Enum => v is null || (v is int i && i == 0) || (v is Enum e && Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture) == 0),
        _ => v is null,
    };

    // ── construction ──

    public object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields)
    {
        if (!_byName.TryGetValue(typeName, out var desc))
        {
            return null;
        }
        var msg = (IMessage)Activator.CreateInstance(desc.ClrType)!;
        foreach (var (name, value) in fields)
        {
            var fd = FindField(desc, name);
            if (fd is null)
            {
                return null;
            }
            if (!TrySetField(msg, fd, value))
            {
                return null;
            }
        }
        return msg;
    }

    private static bool TrySetField(IMessage msg, FieldDescriptor fd, CelValue value)
    {
        try
        {
            if (fd.IsMap)
            {
                var dict = (IDictionary)fd.Accessor.GetValue(msg);
                if (value is not MapValue mv)
                {
                    return false;
                }
                foreach (var (k, v) in mv.Entries)
                {
                    dict[ToClrFor(fd.MessageType.FindFieldByNumber(1), k)!] = ToClrFor(fd.MessageType.FindFieldByNumber(2), v);
                }
                return true;
            }
            if (fd.IsRepeated)
            {
                var list = (IList)fd.Accessor.GetValue(msg);
                if (value is not ListValue lv)
                {
                    return false;
                }
                foreach (var elem in lv.Elements)
                {
                    var converted = ToClrForElement(fd, elem);
                    if (converted is null && elem is not NullValue)
                    {
                        return false;
                    }
                    list.Add(converted);
                }
                return true;
            }
            var clrValue = ToClrForElement(fd, value);
            if (clrValue is null && fd.FieldType != FieldType.Message && value is not NullValue)
            {
                return false;
            }
            fd.Accessor.SetValue(msg, clrValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? ToClrFor(FieldDescriptor? fd, CelValue value)
    {
        if (fd is null)
        {
            return Cel.Runtime.ValueAdapter.ToClr(value);
        }
        return ToClrForElement(fd, value);
    }

    /// <summary>Convert a CelValue to the CLR shape the protobuf accessor expects for one element.</summary>
    private static object? ToClrForElement(FieldDescriptor fd, CelValue value) => (fd.FieldType, value) switch
    {
        (FieldType.Bool, BoolValue b) => b.Value,
        (FieldType.Int32, IntValue i) => (int)i.Value,
        (FieldType.Int64, IntValue i) => i.Value,
        (FieldType.SInt32, IntValue i) => (int)i.Value,
        (FieldType.SInt64, IntValue i) => i.Value,
        (FieldType.SFixed32, IntValue i) => (int)i.Value,
        (FieldType.SFixed64, IntValue i) => i.Value,
        (FieldType.UInt32, UintValue u) => (uint)u.Value,
        (FieldType.UInt64, UintValue u) => u.Value,
        (FieldType.Fixed32, UintValue u) => (uint)u.Value,
        (FieldType.Fixed64, UintValue u) => u.Value,
        // Allow int → uint coercion for literals like `0`, `1`.
        (FieldType.UInt32, IntValue i) when i.Value >= 0 => (uint)i.Value,
        (FieldType.UInt64, IntValue i) when i.Value >= 0 => (ulong)i.Value,
        (FieldType.Fixed32, IntValue i) when i.Value >= 0 => (uint)i.Value,
        (FieldType.Fixed64, IntValue i) when i.Value >= 0 => (ulong)i.Value,
        (FieldType.Float, DoubleValue d) => (float)d.Value,
        (FieldType.Double, DoubleValue d) => d.Value,
        (FieldType.String, StringValue s) => s.Value,
        (FieldType.Bytes, BytesValue b) => ByteString.CopyFrom(b.Value.ToArray()),
        (FieldType.Enum, IntValue i) => (int)i.Value,
        (FieldType.Message, _) => MessageOrWrapperFor(fd, value)!,
        (_, NullValue) => null,
        _ => Cel.Runtime.ValueAdapter.ToClr(value),
    };

    /// <summary>
    /// Convert a CEL value to the CLR shape that the field's <c>SetValue</c> accessor expects.
    /// For wrapper-typed fields (BoolValue, Int32Value, ...) Google.Protobuf C# generates
    /// the property as a nullable primitive — passing the unwrapped primitive matches that
    /// shape. For other message types we either pass the existing IMessage or build the
    /// well-known type instance from a CEL value.
    /// </summary>
    private static object? MessageOrWrapperFor(FieldDescriptor fd, CelValue value)
    {
        if (value is NullValue)
        {
            return null;
        }
        if (value is ObjectValue o && o.Native is IMessage msg)
        {
            return msg;
        }
        return fd.MessageType.FullName switch
        {
            // Generated as nullable primitives — pass the unwrapped CLR value directly.
            "google.protobuf.BoolValue" => value is BoolValue b ? (object?)b.Value : null,
            "google.protobuf.Int32Value" => value is IntValue i ? (int)i.Value : null,
            "google.protobuf.Int64Value" => value is IntValue i ? i.Value : null,
            "google.protobuf.UInt32Value" => value switch
            {
                UintValue u => (uint)u.Value,
                IntValue i when i.Value >= 0 => (uint)i.Value,
                _ => null,
            },
            "google.protobuf.UInt64Value" => value switch
            {
                UintValue u => u.Value,
                IntValue i when i.Value >= 0 => (ulong)i.Value,
                _ => null,
            },
            "google.protobuf.FloatValue" => value is DoubleValue d ? (float)d.Value : null,
            "google.protobuf.DoubleValue" => value is DoubleValue d ? d.Value : null,
            "google.protobuf.StringValue" => value is StringValue s ? s.Value : null,
            "google.protobuf.BytesValue" => value is BytesValue b ? ByteString.CopyFrom(b.Value.ToArray()) : null,
            // Timestamp / Duration kept as IMessage by codegen.
            "google.protobuf.Timestamp" => value is TimestampValue t
                ? PbTimestamp.FromDateTimeOffset(t.Value.ToDateTimeOffset())
                : null,
            "google.protobuf.Duration" => value is DurationValue d
                ? PbDuration.FromTimeSpan(d.Value.ToTimeSpan())
                : null,
            _ => null,
        };
    }

    private static FieldDescriptor? FindField(MessageDescriptor desc, string name)
    {
        // Try the proto-style name first (e.g. "single_int32"); fall back to the C# property
        // form ("SingleInt32") that callers might pass.
        var fd = desc.FindFieldByName(name);
        if (fd is not null)
        {
            return fd;
        }
        // Convert PascalCase → snake_case as a fallback heuristic.
        var snake = ToSnakeCase(name);
        return desc.FindFieldByName(snake);
    }

    private static string ToSnakeCase(string s)
    {
        if (s.Length == 0)
        {
            return s;
        }
        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (char.IsAsciiLetterUpper(c))
            {
                if (sb.Length > 0)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

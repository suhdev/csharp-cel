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

    /// <summary>
    /// Every <see cref="EnumDescriptor"/> reachable from registered message descriptors
    /// (their nested enums) and the file-level top-level enums. Used by the conformance
    /// harness to register enum-constructor functions and constants.
    /// </summary>
    public IEnumerable<EnumDescriptor> EnumDescriptors()
    {
        var seen = new HashSet<EnumDescriptor>();
        foreach (var desc in _byName.Values)
        {
            foreach (var e in desc.EnumTypes)
            {
                if (seen.Add(e))
                {
                    yield return e;
                }
            }
        }
        var seenFiles = new HashSet<FileDescriptor>();
        foreach (var desc in _byName.Values)
        {
            if (!seenFiles.Add(desc.File))
            {
                continue;
            }
            foreach (var e in desc.File.EnumTypes)
            {
                if (seen.Add(e))
                {
                    yield return e;
                }
            }
        }
    }

    /// <summary>
    /// Enumerate every (qualified-name, numeric-value) pair for the enum constants reachable
    /// from registered message descriptors. Used to declare enum constants as integer
    /// variables in the env so <c>GlobalEnum.GAZ</c> resolves like any qualified variable.
    /// </summary>
    public IEnumerable<(string Name, long Value)> EnumConstants()
    {
        foreach (var desc in _byName.Values)
        {
            foreach (var enumDesc in desc.EnumTypes)
            {
                foreach (var v in enumDesc.Values)
                {
                    yield return ($"{enumDesc.FullName}.{v.Name}", v.Number);
                }
            }
        }
        // Also walk top-level enums by inspecting descriptor file enum types.
        var seenFiles = new HashSet<FileDescriptor>();
        foreach (var desc in _byName.Values)
        {
            if (!seenFiles.Add(desc.File)) { continue; }
            foreach (var enumDesc in desc.File.EnumTypes)
            {
                foreach (var v in enumDesc.Values)
                {
                    yield return ($"{enumDesc.FullName}.{v.Name}", v.Number);
                }
            }
        }
    }

    public bool IsManagedInstance(object instance) => instance is IMessage;

    public string? TypeNameOf(object instance) =>
        instance is IMessage msg ? msg.Descriptor.FullName : null;

    /// <summary>
    /// CEL-flavoured proto message equality. Differs from the auto-generated <see cref="IMessage.Equals(object?)"/>
    /// in one important way: floating-point fields containing NaN make the whole comparison
    /// false (NaN propagates through messages per CEL spec, where the auto-generated Equals
    /// would use bit-equality and treat NaN == NaN as true).
    /// </summary>
    public bool? AreEqual(object a, object b)
    {
        if (a is not IMessage am || b is not IMessage bm)
        {
            return null;
        }
        if (!ReferenceEquals(am.Descriptor, bm.Descriptor))
        {
            return false;
        }
        return MessagesEqual(am, bm);
    }

    private static bool MessagesEqual(IMessage a, IMessage b)
    {
        foreach (var fd in a.Descriptor.Fields.InFieldNumberOrder())
        {
            var av = fd.Accessor.GetValue(a);
            var bv = fd.Accessor.GetValue(b);
            if (!FieldsEqual(fd, av, bv))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FieldsEqual(FieldDescriptor fd, object? av, object? bv)
    {
        if (av is null && bv is null)
        {
            return true;
        }
        if (av is null || bv is null)
        {
            return false;
        }
        if (fd.IsMap)
        {
            return MapFieldsEqual((IDictionary)av, (IDictionary)bv,
                fd.MessageType.FindFieldByNumber(2));
        }
        if (fd.IsRepeated)
        {
            return RepeatedFieldsEqual((IList)av, (IList)bv, fd);
        }
        if (fd.FieldType == FieldType.Double)
        {
            var ad = (double)av;
            var bd = (double)bv;
            if (double.IsNaN(ad) || double.IsNaN(bd))
            {
                return false;
            }
            return ad == bd;
        }
        if (fd.FieldType == FieldType.Float)
        {
            var ad = (float)av;
            var bd = (float)bv;
            if (float.IsNaN(ad) || float.IsNaN(bd))
            {
                return false;
            }
            return ad == bd;
        }
        if (fd.FieldType == FieldType.Message && av is IMessage am && bv is IMessage bm)
        {
            return ReferenceEquals(am.Descriptor, bm.Descriptor) && MessagesEqual(am, bm);
        }
        return av.Equals(bv);
    }

    private static bool RepeatedFieldsEqual(IList a, IList b, FieldDescriptor element)
    {
        if (a.Count != b.Count) { return false; }
        for (var i = 0; i < a.Count; i++)
        {
            if (!FieldsEqual(element, a[i], b[i])) { return false; }
        }
        return true;
    }

    private static bool MapFieldsEqual(IDictionary a, IDictionary b, FieldDescriptor valueField)
    {
        if (a.Count != b.Count) { return false; }
        foreach (DictionaryEntry e in a)
        {
            if (!b.Contains(e.Key)) { return false; }
            if (!FieldsEqual(valueField, e.Value, b[e.Key])) { return false; }
        }
        return true;
    }

    /// <summary>
    /// Project a managed instance to a CEL primitive when applicable. Wrapper messages
    /// (<c>google.protobuf.{Bool,Int32,...}Value</c>), <c>Timestamp</c>, <c>Duration</c>,
    /// <c>Any</c> (unpacked recursively), and <c>Value</c> (oneof case dispatched) all
    /// unwrap to native CEL values; arbitrary other messages keep their <see cref="ObjectValue"/>
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
            PbAny any => UnpackAny(any),
            global::Google.Protobuf.WellKnownTypes.Value v => ProjectValue(v),
            global::Google.Protobuf.WellKnownTypes.ListValue lv => ProjectListValue(lv),
            global::Google.Protobuf.WellKnownTypes.Struct st => ProjectStruct(st),
            _ => null,
        };
    }

    private CelValue? UnpackAny(PbAny any)
    {
        var typeUrl = any.TypeUrl;
        if (string.IsNullOrEmpty(typeUrl))
        {
            return CelValue.Null;
        }
        var slash = typeUrl.LastIndexOf('/');
        var typeName = slash >= 0 ? typeUrl[(slash + 1)..] : typeUrl;
        if (!_byName.TryGetValue(typeName, out var desc))
        {
            return null; // unknown — keep as ObjectValue
        }
        var unpacked = (IMessage)Activator.CreateInstance(desc.ClrType)!;
        unpacked.MergeFrom(any.Value);
        // Recurse so a packed wrapper unwraps to its primitive.
        return Project(unpacked) ?? new ObjectValue(typeName, unpacked);
    }

    private CelValue ProjectValue(global::Google.Protobuf.WellKnownTypes.Value v) => v.KindCase switch
    {
        global::Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue => CelValue.Null,
        global::Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue => CelValue.Of(v.BoolValue),
        global::Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => CelValue.Of(v.NumberValue),
        global::Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue => CelValue.Of(v.StringValue),
        global::Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StructValue => ProjectStruct(v.StructValue),
        global::Google.Protobuf.WellKnownTypes.Value.KindOneofCase.ListValue => ProjectListValue(v.ListValue),
        _ => CelValue.Null,
    };

    private CelValue ProjectListValue(global::Google.Protobuf.WellKnownTypes.ListValue lv)
    {
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<CelValue>(lv.Values.Count);
        foreach (var item in lv.Values)
        {
            builder.Add(ProjectValue(item));
        }
        return new ListValue(builder.ToImmutable());
    }

    private CelValue ProjectStruct(global::Google.Protobuf.WellKnownTypes.Struct st)
    {
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        foreach (var (k, val) in st.Fields)
        {
            builder[CelValue.Of(k)] = ProjectValue(val);
        }
        return new MapValue(builder.ToImmutable());
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
            // Map entries arrive as repeated nested messages — each `field_name { key: ... value: ... }`
            // becomes one ApplyTextProtoToField call with a TextProtoMessageValue.
            if (value is TextProto.TextProtoMessageValue entry)
            {
                var dict = (IDictionary)fd.Accessor.GetValue(msg);
                var keyFd = fd.MessageType.FindFieldByNumber(1);
                var valueFd = fd.MessageType.FindFieldByNumber(2);
                var keyText = entry.Message.FirstOrNull("key")?.Value;
                var valueText = entry.Message.FirstOrNull("value")?.Value;
                if (keyText is null || valueText is null)
                {
                    return;
                }
                var k = ConvertTextProto(keyFd, keyText);
                var v = ConvertTextProto(valueFd, valueText);
                if (k is null || v is null)
                {
                    return;
                }
                dict[k] = v;
            }
            return;
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
                // Empty body — the wrapper is set to its default (zero/empty) value.
                return typeName switch
                {
                    "google.protobuf.BoolValue" => (object)false,
                    "google.protobuf.Int32Value" => 0,
                    "google.protobuf.Int64Value" => 0L,
                    "google.protobuf.UInt32Value" => 0u,
                    "google.protobuf.UInt64Value" => 0ul,
                    "google.protobuf.FloatValue" => 0f,
                    "google.protobuf.DoubleValue" => 0.0,
                    "google.protobuf.StringValue" => "",
                    "google.protobuf.BytesValue" => ByteString.Empty,
                    _ => null,
                };
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
    /// <see cref="Project"/> in the evaluator. An unset non-wrapper message field returns a
    /// default-constructed empty message so further field access yields zero values per CEL.
    /// </summary>
    private static object? ProjectValue(FieldDescriptor fd, object? raw)
    {
        if (raw is null)
        {
            if (fd.FieldType == FieldType.Message && !fd.IsRepeated && !fd.IsMap
                && fd.MessageType is { } mt && !IsWrapperType(mt.FullName))
            {
                return Activator.CreateInstance(mt.ClrType);
            }
            return null;
        }
        if (fd.IsMap || fd.IsRepeated) { return raw; }
        if (fd.FieldType == FieldType.Enum)
        {
            var num = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
            return new EnumValue(fd.EnumType.FullName, num);
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
                    // Null elements in a repeated message field are pruned per CEL spec
                    // (proto repeated fields can't hold null).
                    if (elem is NullValue)
                    {
                        continue;
                    }
                    var converted = ToClrForElement(fd, elem);
                    if (converted is null)
                    {
                        return false;
                    }
                    list.Add(converted);
                }
                return true;
            }
            var clrValue = ToClrForElement(fd, value);
            // null from a non-null input means conversion failed (range error, type
            // mismatch, ...) — surface as a construct failure rather than silently leaving
            // the field at its default.
            if (clrValue is null && value is not NullValue)
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
        // Enum values are stored as int32 in proto C#. Reject CEL ints outside int32 range
        // rather than silently truncating.
        (FieldType.Enum, IntValue i) when i.Value >= int.MinValue && i.Value <= int.MaxValue => (int)i.Value,
        (FieldType.Enum, EnumValue e) when e.Number >= int.MinValue && e.Number <= int.MaxValue => (int)e.Number,
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

        var typeName = fd.MessageType.FullName;
        // For Any/Value/Struct/ListValue fields, always project — even if the CelValue already
        // wraps an IMessage of some specific type, we need to repack/route through the
        // well-known shape.
        switch (typeName)
        {
            case "google.protobuf.Any": return PackToAny(value);
            case "google.protobuf.Value": return ToWellKnownValue(value);
            case "google.protobuf.ListValue":
                return value is ListValue lv ? ToWellKnownListValue(lv)
                     : value is ObjectValue { Native: global::Google.Protobuf.WellKnownTypes.ListValue plv } ? plv
                     : null;
            case "google.protobuf.Struct":
                return value is MapValue mv ? ToWellKnownStruct(mv)
                     : value is ObjectValue { Native: global::Google.Protobuf.WellKnownTypes.Struct ps } ? ps
                     : null;
        }

        // For other message fields, an exact-type wrapped IMessage passes through.
        if (value is ObjectValue o && o.Native is IMessage msg
            && string.Equals(msg.Descriptor.FullName, typeName, StringComparison.Ordinal))
        {
            return msg;
        }
        return typeName switch
        {
            // Generated as nullable primitives — pass the unwrapped CLR value directly.
            // Range-check int32/uint32 so out-of-range CEL ints surface as construct failures
            // (which the runner reports as runtime errors per test expectations).
            "google.protobuf.BoolValue" => value is BoolValue b ? (object?)b.Value : null,
            "google.protobuf.Int32Value" => value is IntValue i
                && i.Value >= int.MinValue && i.Value <= int.MaxValue
                ? (int)i.Value : null,
            "google.protobuf.Int64Value" => value is IntValue i ? i.Value : null,
            "google.protobuf.UInt32Value" => value switch
            {
                UintValue u when u.Value <= uint.MaxValue => (uint)u.Value,
                IntValue i when i.Value >= 0 && i.Value <= uint.MaxValue => (uint)i.Value,
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
            // Any: pack the supplied value into a well-known wrapper if it's a CEL primitive,
            // then wrap that in google.protobuf.Any.
            "google.protobuf.Any" => PackToAny(value),
            // Value: route to the matching oneof case.
            "google.protobuf.Value" => ToWellKnownValue(value),
            "google.protobuf.ListValue" => value is ListValue lv ? ToWellKnownListValue(lv) : null,
            "google.protobuf.Struct" => value is MapValue mv ? ToWellKnownStruct(mv) : null,
            _ => null,
        };
    }

    private static PbAny? PackToAny(CelValue value)
    {
        IMessage? toPack = value switch
        {
            BoolValue b => new PbBoolValue { Value = b.Value },
            IntValue i => new PbInt64Value { Value = i.Value },
            UintValue u => new PbUInt64Value { Value = u.Value },
            DoubleValue d => new PbDoubleValue { Value = d.Value },
            StringValue s => new PbStringValue { Value = s.Value },
            BytesValue b => new PbBytesValue { Value = ByteString.CopyFrom(b.Value.ToArray()) },
            // CEL list / map assigned to an Any pack as the JSON-shaped well-known types.
            ListValue lv => ToWellKnownListValue(lv),
            MapValue mv => ToWellKnownStruct(mv),
            ObjectValue o when o.Native is IMessage im => im,
            _ => null,
        };
        return toPack is null ? null : PbAny.Pack(toPack);
    }

    /// <summary>Largest int64 / uint64 that round-trips through double without loss (2^53).</summary>
    private const long JsonSafeIntegerLimit = 1L << 53;

    private static global::Google.Protobuf.WellKnownTypes.Value ToWellKnownValue(CelValue value) => value switch
    {
        NullValue => new global::Google.Protobuf.WellKnownTypes.Value { NullValue = global::Google.Protobuf.WellKnownTypes.NullValue.NullValue },
        BoolValue b => new global::Google.Protobuf.WellKnownTypes.Value { BoolValue = b.Value },
        // Per proto3 JSON canonical mapping: ints / uints whose magnitude exceeds 2^53 (i.e.
        // can't be exactly represented in a double) encode as quoted strings to preserve
        // precision through the JSON intermediate.
        IntValue i => Math.Abs(i.Value) > JsonSafeIntegerLimit
            ? new global::Google.Protobuf.WellKnownTypes.Value { StringValue = i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = i.Value },
        UintValue u => u.Value > (ulong)JsonSafeIntegerLimit
            ? new global::Google.Protobuf.WellKnownTypes.Value { StringValue = u.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = u.Value },
        DoubleValue d => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = d.Value },
        StringValue s => new global::Google.Protobuf.WellKnownTypes.Value { StringValue = s.Value },
        // Bytes encode as base64 strings in JSON.
        BytesValue b => new global::Google.Protobuf.WellKnownTypes.Value { StringValue = Convert.ToBase64String(b.Value.AsSpan()) },
        ListValue lv => new global::Google.Protobuf.WellKnownTypes.Value { ListValue = ToWellKnownListValue(lv) },
        MapValue mv => new global::Google.Protobuf.WellKnownTypes.Value { StructValue = ToWellKnownStruct(mv) },
        ObjectValue o when o.Native is IMessage im => UnwrapToValue(im),
        _ => new global::Google.Protobuf.WellKnownTypes.Value { NullValue = global::Google.Protobuf.WellKnownTypes.NullValue.NullValue },
    };

    private static global::Google.Protobuf.WellKnownTypes.Value UnwrapToValue(IMessage msg) => msg switch
    {
        PbBoolValue x => new global::Google.Protobuf.WellKnownTypes.Value { BoolValue = x.Value },
        PbInt32Value x => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = x.Value },
        PbInt64Value x => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = x.Value },
        PbUInt32Value x => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = x.Value },
        PbUInt64Value x => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = x.Value },
        PbFloatValue x => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = x.Value },
        PbDoubleValue x => new global::Google.Protobuf.WellKnownTypes.Value { NumberValue = x.Value },
        PbStringValue x => new global::Google.Protobuf.WellKnownTypes.Value { StringValue = x.Value },
        _ => new global::Google.Protobuf.WellKnownTypes.Value { NullValue = global::Google.Protobuf.WellKnownTypes.NullValue.NullValue },
    };

    private static global::Google.Protobuf.WellKnownTypes.ListValue ToWellKnownListValue(ListValue lv)
    {
        var pblv = new global::Google.Protobuf.WellKnownTypes.ListValue();
        foreach (var item in lv.Elements)
        {
            pblv.Values.Add(ToWellKnownValue(item));
        }
        return pblv;
    }

    private static global::Google.Protobuf.WellKnownTypes.Struct ToWellKnownStruct(MapValue mv)
    {
        var pbst = new global::Google.Protobuf.WellKnownTypes.Struct();
        foreach (var (k, val) in mv.Entries)
        {
            if (k is StringValue sk)
            {
                pbst.Fields[sk.Value] = ToWellKnownValue(val);
            }
        }
        return pbst;
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

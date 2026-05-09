using System.Collections.Immutable;

namespace DotnetCel.Types;

/// <summary>
/// Singleton instances and factory helpers for the built-in CEL types. Use these instead of
/// constructing <see cref="CelType"/> records directly so that singletons stay reference-equal.
/// </summary>
public static class CelTypes
{
    public static readonly CelType Bool = new PrimitiveType(PrimitiveKind.Bool);
    public static readonly CelType Int = new PrimitiveType(PrimitiveKind.Int);
    public static readonly CelType Uint = new PrimitiveType(PrimitiveKind.Uint);
    public static readonly CelType Double = new PrimitiveType(PrimitiveKind.Double);
    public static readonly CelType String = new PrimitiveType(PrimitiveKind.String);
    public static readonly CelType Bytes = new PrimitiveType(PrimitiveKind.Bytes);

    public static readonly CelType Null = new NullType();
    public static readonly CelType Dyn = new DynType();
    public static readonly CelType Error = new ErrorType();
    public static readonly CelType Duration = new DurationType();
    public static readonly CelType Timestamp = new TimestampType();
    public static readonly CelType Type = new TypeType();

    public static readonly CelType BoolWrapper = new WrapperType(PrimitiveKind.Bool);
    public static readonly CelType IntWrapper = new WrapperType(PrimitiveKind.Int);
    public static readonly CelType UintWrapper = new WrapperType(PrimitiveKind.Uint);
    public static readonly CelType DoubleWrapper = new WrapperType(PrimitiveKind.Double);
    public static readonly CelType StringWrapper = new WrapperType(PrimitiveKind.String);
    public static readonly CelType BytesWrapper = new WrapperType(PrimitiveKind.Bytes);

    public static ListType List(CelType element) => new(element);
    public static MapType Map(CelType key, CelType value) => new(key, value);
    public static ObjectType Object(string typeName) => new(typeName);
    public static ObjectType Object(string typeName, params CelType[] typeArgs) =>
        new(typeName, [.. typeArgs]);
    public static EnumType Enum(string typeName) => new(typeName);
    public static OptionalType Optional(CelType inner) => new(inner);
    public static TypeParamType TypeParam(string name) => new(name);
    public static TypeType TypeOf(CelType inner) => new(inner);
    public static FunctionType Function(CelType result, params CelType[] args) =>
        new(result, [.. args]);
    public static AbstractType Abstract(string typeName, params CelType[] parameters) =>
        new(typeName, [.. parameters]);
}

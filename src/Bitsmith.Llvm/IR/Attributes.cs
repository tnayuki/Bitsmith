using System;
using System.Collections.Generic;
using System.Linq;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// A single attribute. Three flavors: enum (no value), int (integer-valued, e.g. <c>align</c>),
/// or type (typed attribute referencing an <see cref="LlvmType"/>, e.g. <c>byval(T)</c>).
/// </summary>
public sealed class Attribute : IEquatable<Attribute>
{
    public enum Form { Enum, Int, Type, String, StringValue }

    /// <summary>Bitcode attr-kind code (see <see cref="Bitsmith.Llvm.Codes.AttrKindCodes"/>).
    /// Unused for <see cref="Form.String"/> / <see cref="Form.StringValue"/>.</summary>
    public uint Kind { get; }
    public Form Shape { get; }
    public ulong IntValue { get; }
    public LlvmType? TypeValue { get; }
    public string? StringKey { get; }
    public string? StringValue { get; }

    private Attribute(uint kind, Form shape, ulong intValue, LlvmType? typeValue,
        string? stringKey = null, string? stringValue = null)
    {
        Kind = kind; Shape = shape; IntValue = intValue; TypeValue = typeValue;
        StringKey = stringKey; StringValue = stringValue;
    }

    public static Attribute Enum(uint kind) => new(kind, Form.Enum, 0, null);
    public static Attribute Int(uint kind, ulong value) => new(kind, Form.Int, value, null);
    public static Attribute Type(uint kind, LlvmType type) => new(kind, Form.Type,
        0, type ?? throw new ArgumentNullException(nameof(type)));
    public static Attribute String(string key) =>
        new(0, Form.String, 0, null, key ?? throw new ArgumentNullException(nameof(key)), null);
    public static Attribute StringKeyValue(string key, string value) =>
        new(0, Form.StringValue, 0, null,
            key ?? throw new ArgumentNullException(nameof(key)),
            value ?? throw new ArgumentNullException(nameof(value)));

    public bool Equals(Attribute? other)
    {
        if (other is null) return false;
        if (Kind != other.Kind || Shape != other.Shape) return false;
        return Shape switch
        {
            Form.Int => IntValue == other.IntValue,
            Form.Type => ReferenceEquals(TypeValue, other.TypeValue),
            Form.String => StringKey == other.StringKey,
            Form.StringValue => StringKey == other.StringKey && StringValue == other.StringValue,
            _ => true,
        };
    }
    public override bool Equals(object? obj) => obj is Attribute a && Equals(a);
    public override int GetHashCode() => HashCode.Combine(Kind, Shape, IntValue,
        TypeValue is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(TypeValue),
        StringKey, StringValue);
}

/// <summary>
/// Ordered, deduplicated set of attributes attached to a single index
/// (function, return value, or one parameter).
/// </summary>
public sealed class AttributeSet
{
    private readonly List<Attribute> _attrs = new();

    public IReadOnlyList<Attribute> Attributes => _attrs;
    public int Count => _attrs.Count;
    public bool IsEmpty => _attrs.Count == 0;

    public AttributeSet Add(Attribute attr)
    {
        if (attr is null) throw new ArgumentNullException(nameof(attr));
        if (!_attrs.Contains(attr)) _attrs.Add(attr);
        return this;
    }

    /// <summary>Structural equality on the attribute list (order-insensitive).</summary>
    public bool StructurallyEquals(AttributeSet other)
    {
        if (other._attrs.Count != _attrs.Count) return false;
        return _attrs.All(other._attrs.Contains);
    }
}

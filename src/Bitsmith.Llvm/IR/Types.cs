using System;
using System.Collections.Generic;
using System.Linq;

namespace Bitsmith.Llvm.IR;

public abstract class LlvmType
{
    /// <summary>Index in the module's type table. Assigned by <see cref="TypeContext"/>.</summary>
    public int Id { get; internal set; } = -1;
}

public sealed class VoidType : LlvmType { internal VoidType() { } }
public sealed class FloatType : LlvmType { internal FloatType() { } }
public sealed class DoubleType : LlvmType { internal DoubleType() { } }
public sealed class LabelType : LlvmType { internal LabelType() { } }
public sealed class MetadataType : LlvmType { internal MetadataType() { } }
public sealed class HalfType : LlvmType { internal HalfType() { } }
public sealed class BFloatType : LlvmType { internal BFloatType() { } }
public sealed class X86Fp80Type : LlvmType { internal X86Fp80Type() { } }
public sealed class Fp128Type : LlvmType { internal Fp128Type() { } }
public sealed class PpcFp128Type : LlvmType { internal PpcFp128Type() { } }
public sealed class X86MmxType : LlvmType { internal X86MmxType() { } }
public sealed class X86AmxType : LlvmType { internal X86AmxType() { } }
public sealed class TokenType : LlvmType { internal TokenType() { } }

public sealed class IntegerType : LlvmType
{
    public int BitWidth { get; }
    internal IntegerType(int bitWidth)
    {
        if (bitWidth < 1 || bitWidth > (1 << 23))
            throw new ArgumentOutOfRangeException(nameof(bitWidth));
        BitWidth = bitWidth;
    }
}

/// <summary>Opaque pointer (LLVM 15+). Distinguished only by address space.</summary>
public sealed class PointerType : LlvmType
{
    public int AddressSpace { get; }
    internal PointerType(int addressSpace) { AddressSpace = addressSpace; }
}

public sealed class ArrayType : LlvmType
{
    public ulong NumElements { get; }
    public LlvmType ElementType { get; }
    internal ArrayType(ulong numElements, LlvmType elementType)
    {
        NumElements = numElements;
        ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
    }
}

public sealed class VectorType : LlvmType
{
    public uint NumElements { get; }
    public LlvmType ElementType { get; }
    /// <summary>If true, this is a scalable vector (`<vscale x N x T>`); <see cref="NumElements"/>
    /// is then the minimum element count.</summary>
    public bool IsScalable { get; }
    internal VectorType(uint numElements, LlvmType elementType, bool isScalable = false)
    {
        if (numElements == 0) throw new ArgumentOutOfRangeException(nameof(numElements));
        NumElements = numElements;
        ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        IsScalable = isScalable;
    }
}

public sealed class FunctionType : LlvmType
{
    public LlvmType ReturnType { get; }
    public IReadOnlyList<LlvmType> ParameterTypes { get; }
    public bool IsVarArg { get; }
    internal FunctionType(LlvmType returnType, IReadOnlyList<LlvmType> parameterTypes, bool isVarArg)
    {
        ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
        ParameterTypes = parameterTypes ?? Array.Empty<LlvmType>();
        IsVarArg = isVarArg;
    }
}

public sealed class StructType : LlvmType
{
    public string? Name { get; }
    private LlvmType[] _elementTypes;
    public IReadOnlyList<LlvmType> ElementTypes => _elementTypes;
    private bool _isPacked;
    public bool IsPacked => _isPacked;
    public bool IsLiteral => Name is null;
    /// <summary>True for an opaque named struct that has no body yet
    /// (created via <see cref="TypeContext.CreateOpaqueNamedStruct"/>; resolved by <see cref="SetBody"/>).</summary>
    public bool IsOpaque { get; private set; }

    internal StructType(string? name, IReadOnlyList<LlvmType> elementTypes, bool isPacked, bool isOpaque = false)
    {
        Name = name;
        _elementTypes = elementTypes is null ? Array.Empty<LlvmType>() : elementTypes.ToArray();
        _isPacked = isPacked;
        IsOpaque = isOpaque;
    }

    /// <summary>Resolve an opaque named struct's body. Only valid on opaque structs.</summary>
    public void SetBody(IReadOnlyList<LlvmType> elementTypes, bool isPacked = false)
    {
        if (!IsOpaque)
            throw new InvalidOperationException("SetBody only valid on opaque named structs");
        _elementTypes = (elementTypes ?? throw new ArgumentNullException(nameof(elementTypes))).ToArray();
        _isPacked = isPacked;
        IsOpaque = false;
    }
}

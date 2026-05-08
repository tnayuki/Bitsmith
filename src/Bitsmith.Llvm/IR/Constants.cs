using System;
using System.Collections.Generic;
using System.Numerics;

namespace Bitsmith.Llvm.IR;

/// <summary>Base class for compile-time constant values.</summary>
public abstract class Constant : Value { }

/// <summary>Integer constant. Stored as a signed two's-complement <see cref="BigInteger"/>.</summary>
public sealed class IntegerConstant : Constant
{
    private readonly IntegerType _type;
    public BigInteger Value { get; }
    public override LlvmType Type => _type;

    public IntegerConstant(IntegerType type, long value) : this(type, new BigInteger(value)) { }

    public IntegerConstant(IntegerType type, BigInteger value)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        Value = value;
    }
}

/// <summary>Null/zero-initializer constant of the given type.</summary>
public sealed class NullConstant : Constant
{
    private readonly LlvmType _type;
    public override LlvmType Type => _type;
    public NullConstant(LlvmType type) { _type = type ?? throw new ArgumentNullException(nameof(type)); }
}

/// <summary>Undef constant of the given type.</summary>
public sealed class UndefConstant : Constant
{
    private readonly LlvmType _type;
    public override LlvmType Type => _type;
    public UndefConstant(LlvmType type) { _type = type ?? throw new ArgumentNullException(nameof(type)); }
}

/// <summary>Poison constant — like undef but propagates through all uses.</summary>
public sealed class PoisonConstant : Constant
{
    private readonly LlvmType _type;
    public override LlvmType Type => _type;
    public PoisonConstant(LlvmType type) { _type = type ?? throw new ArgumentNullException(nameof(type)); }
}

/// <summary>Floating-point constant. Each subclass encodes the exact bit pattern
/// expected by LLVM's <c>CST_CODE_FLOAT</c> record.</summary>
public abstract class FloatingPointConstant : Constant
{
    private readonly LlvmType _type;
    public override LlvmType Type => _type;
    protected FloatingPointConstant(LlvmType type)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }
    /// <summary>Pushes the operand list expected by <c>CST_CODE_FLOAT</c>.</summary>
    public abstract void EncodeOperands(List<ulong> operands);
}

public sealed class FloatConstant : FloatingPointConstant
{
    public uint BitPattern { get; }
    public FloatConstant(FloatType type, float value) : base(type)
    {
        BitPattern = unchecked((uint)BitConverter.SingleToInt32Bits(value));
    }
    public FloatConstant(FloatType type, uint bits) : base(type) { BitPattern = bits; }
    public override void EncodeOperands(List<ulong> ops) => ops.Add(BitPattern);
}

public sealed class DoubleConstant : FloatingPointConstant
{
    public ulong BitPattern { get; }
    public DoubleConstant(DoubleType type, double value) : base(type)
    {
        BitPattern = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
    }
    public DoubleConstant(DoubleType type, ulong bits) : base(type) { BitPattern = bits; }
    public override void EncodeOperands(List<ulong> ops) => ops.Add(BitPattern);
}

public sealed class HalfConstant : FloatingPointConstant
{
    public ushort BitPattern { get; }
    public HalfConstant(HalfType type, ushort bits) : base(type) { BitPattern = bits; }
    public override void EncodeOperands(List<ulong> ops) => ops.Add(BitPattern);
}

public sealed class BFloatConstant : FloatingPointConstant
{
    public ushort BitPattern { get; }
    public BFloatConstant(BFloatType type, ushort bits) : base(type) { BitPattern = bits; }
    public override void EncodeOperands(List<ulong> ops) => ops.Add(BitPattern);
}

/// <summary>x86 80-bit extended: 64-bit mantissa + 16-bit sign/exponent.</summary>
public sealed class X86Fp80Constant : FloatingPointConstant
{
    public ulong Mantissa { get; }
    public ushort Exponent { get; }
    public X86Fp80Constant(X86Fp80Type type, ulong mantissa, ushort exponent) : base(type)
    {
        Mantissa = mantissa;
        Exponent = exponent;
    }
    public override void EncodeOperands(List<ulong> ops) { ops.Add(Mantissa); ops.Add(Exponent); }
}

public sealed class Fp128Constant : FloatingPointConstant
{
    public ulong Low { get; }
    public ulong High { get; }
    public Fp128Constant(Fp128Type type, ulong low, ulong high) : base(type)
    {
        Low = low;
        High = high;
    }
    public override void EncodeOperands(List<ulong> ops) { ops.Add(Low); ops.Add(High); }
}

public sealed class PpcFp128Constant : FloatingPointConstant
{
    public ulong High { get; }
    public ulong Low { get; }
    public PpcFp128Constant(PpcFp128Type type, ulong high, ulong low) : base(type)
    {
        High = high;
        Low = low;
    }
    public override void EncodeOperands(List<ulong> ops) { ops.Add(High); ops.Add(Low); }
}

/// <summary>
/// Aggregate constant — used for struct, array, and vector initializers.
/// Elements must be other <see cref="Constant"/>s already registered in the module.
/// </summary>
public sealed class AggregateConstant : Constant
{
    private readonly LlvmType _type;
    public IReadOnlyList<Constant> Elements { get; }
    public override LlvmType Type => _type;
    public AggregateConstant(LlvmType type, IReadOnlyList<Constant> elements)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        Elements = elements ?? Array.Empty<Constant>();
    }
}

/// <summary>
/// String constant — i8 array literal. <see cref="IsCString"/> selects the
/// null-terminated <c>CST_CODE_CSTRING</c> variant.
/// </summary>
public sealed class StringConstant : Constant
{
    private readonly ArrayType _type;
    public byte[] Bytes { get; }
    public bool IsCString { get; }
    public override LlvmType Type => _type;
    public StringConstant(ArrayType type, byte[] bytes, bool isCString = false)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        IsCString = isCString;
    }
}

/// <summary>Constant expression — cast (bitcast/inttoptr/ptrtoint/sext/zext/...).
/// <paramref name="opcode"/> matches <c>Bitsmith.Llvm.Codes.CastCodes</c>.</summary>
public sealed class ConstantCast : Constant
{
    private readonly LlvmType _destType;
    public uint Opcode { get; }
    public Constant Operand { get; }
    public override LlvmType Type => _destType;
    public ConstantCast(uint opcode, Constant operand, LlvmType destType)
    {
        Opcode = opcode;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        _destType = destType ?? throw new ArgumentNullException(nameof(destType));
    }
}

/// <summary>Constant expression — getelementptr on a constant pointer.</summary>
public sealed class ConstantGep : Constant
{
    private readonly PointerType _resultType;
    public LlvmType SourceElementType { get; }
    public Constant Pointer { get; }
    public IReadOnlyList<Constant> Indices { get; }
    public bool IsInBounds { get; }
    public override LlvmType Type => _resultType;
    public ConstantGep(LlvmType sourceElementType, Constant pointer,
        IReadOnlyList<Constant> indices, PointerType resultType, bool isInBounds = false)
    {
        SourceElementType = sourceElementType ?? throw new ArgumentNullException(nameof(sourceElementType));
        Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        Indices = indices ?? Array.Empty<Constant>();
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        IsInBounds = isInBounds;
    }
}

/// <summary>Inline assembly value. Modeled as a Constant for ID-table purposes —
/// LLVM 15 emits it in CONSTANTS_BLOCK with code 32 and the wrapped function type.
/// Used as the Callee of a CallInstruction or InvokeInstruction.</summary>
public sealed class InlineAsm : Constant
{
    private readonly LlvmType _ptrType;
    public FunctionType FunctionType { get; }
    public string AsmString { get; }
    public string Constraints { get; }
    public bool HasSideEffects { get; }
    public bool IsAlignStack { get; }
    public Bitsmith.Llvm.Codes.InlineAsmDialect Dialect { get; }
    public bool CanThrow { get; }
    public override LlvmType Type => _ptrType;
    public InlineAsm(FunctionType fnType, LlvmType ptrType, string asmString, string constraints,
        bool hasSideEffects = false, bool isAlignStack = false,
        Bitsmith.Llvm.Codes.InlineAsmDialect dialect = Bitsmith.Llvm.Codes.InlineAsmDialect.ATT,
        bool canThrow = false)
    {
        FunctionType = fnType ?? throw new ArgumentNullException(nameof(fnType));
        _ptrType = ptrType ?? throw new ArgumentNullException(nameof(ptrType));
        AsmString = asmString ?? throw new ArgumentNullException(nameof(asmString));
        Constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        HasSideEffects = hasSideEffects;
        IsAlignStack = isAlignStack;
        Dialect = dialect;
        CanThrow = canThrow;
    }
}

/// <summary>The address of a basic block: <c>blockaddress(@fn, %label)</c>.
/// Used as the address operand of <c>indirectbr</c> (computed goto / threaded
/// dispatch). Type is a pointer.</summary>
public sealed class BlockAddress : Constant
{
    private readonly LlvmType _ptrType;
    public Function Function { get; }
    public BasicBlock Block { get; }
    public override LlvmType Type => _ptrType;
    public BlockAddress(Function function, BasicBlock block, LlvmType ptrType)
    {
        Function = function ?? throw new ArgumentNullException(nameof(function));
        Block = block ?? throw new ArgumentNullException(nameof(block));
        _ptrType = ptrType ?? throw new ArgumentNullException(nameof(ptrType));
    }
}

/// <summary>Constant expression — icmp/fcmp on two constants. Predicate matches
/// <c>Bitsmith.Llvm.Codes.CmpPredicates</c>.</summary>
public sealed class ConstantCmp : Constant
{
    private readonly LlvmType _resultType;
    public uint Predicate { get; }
    public Constant Left { get; }
    public Constant Right { get; }
    public override LlvmType Type => _resultType;
    public ConstantCmp(uint predicate, Constant left, Constant right, LlvmType resultType)
    {
        Predicate = predicate;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
    }
}

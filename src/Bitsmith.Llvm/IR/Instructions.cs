using System;
using System.Collections.Generic;
using System.Linq;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// LLVM binary integer/float opcode encoding (FUNC_CODE_INST_BINOP).
/// Numeric values match GetEncodedBinaryOpcode in BitcodeWriter.cpp.
/// </summary>
public enum BinaryOpcode
{
    Add = 0,
    Sub = 1,
    Mul = 2,
    UDiv = 3,
    SDiv = 4,
    URem = 5,
    SRem = 6,
    Shl = 7,
    LShr = 8,
    AShr = 9,
    And = 10,
    Or = 11,
    Xor = 12,
}

public abstract class Instruction : Value
{
    public BasicBlock? Parent { get; internal set; }
    public string? Name { get; set; }
    /// <summary>Value operands referenced by this instruction. Used by the
    /// <c>ValueEnumerator</c> to discover function-local constants.</summary>
    public virtual IEnumerable<Value> Operands => Enumerable.Empty<Value>();
}

/// <summary>
/// Fast-math flag bitmask — matches LLVM's <c>FastMathMap</c> in LLVMBitCodes.h.
/// Valid only on floating-point binary operators / fcmp / call.
/// </summary>
[Flags]
public enum FastMathFlags : uint
{
    None            = 0,
    AllowReassoc    = 1u << 0,
    NoNaNs          = 1u << 1,
    NoInfs          = 1u << 2,
    NoSignedZeros   = 1u << 3,
    AllowReciprocal = 1u << 4,
    AllowContract   = 1u << 5,
    ApproxFunc      = 1u << 6,
    Fast = AllowReassoc | NoNaNs | NoInfs | NoSignedZeros | AllowReciprocal | AllowContract | ApproxFunc,
}

public sealed class BinaryOperator : Instruction
{
    public BinaryOpcode Opcode { get; }
    public Value Left { get; }
    public Value Right { get; }

    /// <summary>nuw — no unsigned wrap. Valid on Add/Sub/Mul/Shl integer opcodes.</summary>
    public bool IsNuw { get; set; }
    /// <summary>nsw — no signed wrap. Valid on Add/Sub/Mul/Shl integer opcodes.</summary>
    public bool IsNsw { get; set; }
    /// <summary>exact — division/shift produces an exact result. Valid on UDiv/SDiv/LShr/AShr.</summary>
    public bool IsExact { get; set; }
    /// <summary>Fast-math flags; valid only on floating-point operands.</summary>
    public FastMathFlags Fmf { get; set; }

    public BinaryOperator(BinaryOpcode opcode, Value left, Value right)
    {
        if (left is null) throw new ArgumentNullException(nameof(left));
        if (right is null) throw new ArgumentNullException(nameof(right));
        if (!ReferenceEquals(left.Type, right.Type))
            throw new ArgumentException("binop operands must have matching types");
        Opcode = opcode;
        Left = left;
        Right = right;
    }

    public override LlvmType Type => Left.Type;
    public override IEnumerable<Value> Operands { get { yield return Left; yield return Right; } }
}

public sealed class ReturnInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value? ReturnValue { get; }

    public ReturnInstruction(LlvmType voidType, Value? returnValue = null)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        ReturnValue = returnValue;
    }

    public override LlvmType Type => _voidType;
    public override IEnumerable<Value> Operands
    {
        get { if (ReturnValue is not null) yield return ReturnValue; }
    }
}

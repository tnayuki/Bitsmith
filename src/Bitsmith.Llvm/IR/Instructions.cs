using System;
using System.Collections.Generic;

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
    public virtual ReadOnlySpan<Value> Operands => ReadOnlySpan<Value>.Empty;
    /// <summary>Optional !dbg attachment.</summary>
    public DiLocation? DebugLocation { get; set; }

    /// <summary>
    /// Non-<c>!dbg</c> metadata attachments (LLVM <c>!invariant.load</c>,
    /// <c>!invariant.group</c>, <c>!range</c>, <c>!nonnull</c>,
    /// <c>!alias.scope</c>, <c>!noalias</c>, ...). Allocated lazily on
    /// the first attachment so unannotated instructions stay zero-cost.
    /// Use <see cref="AddAttachment"/> to populate. Kind IDs are
    /// allocated by <see cref="Bitsmith.Llvm.Writer.MetadataWriter"/>
    /// when the module is serialised.
    /// </summary>
    public List<(string KindName, Metadata Md)>? Attachments { get; private set; }

    /// <summary>
    /// Attach a metadata node under the given kind name (e.g.
    /// <c>"invariant.load"</c>, <c>"range"</c>, <c>"invariant.group"</c>,
    /// <c>"nonnull"</c>, <c>"alias.scope"</c>, <c>"noalias"</c>). The
    /// name is mapped to a numeric kind id by the bitcode writer.
    /// <c>!dbg</c> attachments go through <see cref="DebugLocation"/>
    /// and must not be added through this method.
    /// </summary>
    public void AddAttachment(string kindName, Metadata md)
    {
        if (kindName is null) throw new ArgumentNullException(nameof(kindName));
        if (md is null) throw new ArgumentNullException(nameof(md));
        if (kindName == "dbg")
            throw new ArgumentException(
                "Use Instruction.DebugLocation for the 'dbg' kind.",
                nameof(kindName));
        Attachments ??= new List<(string, Metadata)>(1);
        Attachments.Add((kindName, md));
    }
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

    private readonly Value[] _operands;

    public BinaryOperator(BinaryOpcode opcode, Value left, Value right)
    {
        if (left is null) throw new ArgumentNullException(nameof(left));
        if (right is null) throw new ArgumentNullException(nameof(right));
        if (!ReferenceEquals(left.Type, right.Type))
            throw new ArgumentException("binop operands must have matching types");
        Opcode = opcode;
        Left = left;
        Right = right;
        _operands = new[] { left, right };
    }

    public override LlvmType Type => Left.Type;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class ReturnInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value? ReturnValue { get; }

    private readonly Value[] _operands;

    public ReturnInstruction(LlvmType voidType, Value? returnValue = null)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        ReturnValue = returnValue;
        _operands = returnValue is null ? Array.Empty<Value>() : new[] { returnValue };
    }

    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class BranchInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public BasicBlock TrueTarget { get; }
    public BasicBlock? FalseTarget { get; }
    public Value? Condition { get; }
    public bool IsConditional => FalseTarget is not null;

    private readonly Value[] _operands;

    public BranchInstruction(LlvmType voidType, BasicBlock target)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        TrueTarget = target ?? throw new ArgumentNullException(nameof(target));
        _operands = Array.Empty<Value>();
    }

    public BranchInstruction(LlvmType voidType, Value condition, BasicBlock trueTarget, BasicBlock falseTarget)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        TrueTarget = trueTarget ?? throw new ArgumentNullException(nameof(trueTarget));
        FalseTarget = falseTarget ?? throw new ArgumentNullException(nameof(falseTarget));
        _operands = new[] { condition };
    }

    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class SwitchInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value Condition { get; }
    public BasicBlock DefaultDest { get; }
    public List<(IntegerConstant Value, BasicBlock Dest)> Cases { get; } = new();

    public SwitchInstruction(LlvmType voidType, Value condition, BasicBlock defaultDest)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        DefaultDest = defaultDest ?? throw new ArgumentNullException(nameof(defaultDest));
    }

    private Value[]? _operandsCache;

    public SwitchInstruction AddCase(IntegerConstant caseValue, BasicBlock dest)
    {
        Cases.Add((caseValue, dest));
        _operandsCache = null;
        return this;
    }

    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands
    {
        get
        {
            if (_operandsCache is null)
            {
                var arr = new Value[1 + Cases.Count];
                arr[0] = Condition;
                for (int i = 0; i < Cases.Count; i++) arr[i + 1] = Cases[i].Value;
                _operandsCache = arr;
            }
            return _operandsCache;
        }
    }
}

/// <summary><c>indirectbr ptr %addr, [label %a, label %b, ...]</c> — computed goto.
/// The address is typically a <see cref="BlockAddress"/> loaded through some
/// indirection. <see cref="PossibleTargets"/> is the (non-exhaustive) hint of
/// labels for control-flow analysis; the runtime address can be any of them.</summary>
public sealed class IndirectBrInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value Address { get; }
    public IReadOnlyList<BasicBlock> PossibleTargets { get; }
    private readonly Value[] _operands;
    public override ReadOnlySpan<Value> Operands => _operands;
    public override LlvmType Type => _voidType;
    public IndirectBrInstruction(LlvmType voidType, Value address,
        IReadOnlyList<BasicBlock> possibleTargets)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        Address = address ?? throw new ArgumentNullException(nameof(address));
        PossibleTargets = possibleTargets ?? throw new ArgumentNullException(nameof(possibleTargets));
        _operands = new[] { address };
    }
}

public sealed class UnreachableInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public UnreachableInstruction(LlvmType voidType)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
    }
    public override LlvmType Type => _voidType;
}

public sealed class AllocaInstruction : Instruction
{
    public LlvmType AllocatedType { get; }
    public Value NumElements { get; }
    public uint Alignment { get; set; }
    public bool IsInAlloca { get; set; }
    public bool IsSwiftError { get; set; }
    private readonly PointerType _ptrType;

    private readonly Value[] _operands;

    public AllocaInstruction(LlvmType allocatedType, Value numElements, PointerType pointerType, uint alignment = 0)
    {
        AllocatedType = allocatedType ?? throw new ArgumentNullException(nameof(allocatedType));
        NumElements = numElements ?? throw new ArgumentNullException(nameof(numElements));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));
        Alignment = alignment;
        _operands = new[] { numElements };
    }

    public override LlvmType Type => _ptrType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class LoadInstruction : Instruction
{
    private readonly LlvmType _resultType;
    public Value Pointer { get; }
    public uint Alignment { get; set; }
    public bool IsVolatile { get; set; }
    public uint Ordering { get; set; }                // 0 = not atomic
    public uint SyncScope { get; set; } = Codes.SyncScope.System;

    private readonly Value[] _operands;

    public LoadInstruction(LlvmType resultType, Value pointer, uint alignment = 0)
    {
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        Alignment = alignment;
        _operands = new[] { pointer };
    }

    public bool IsAtomic => Ordering != 0;
    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class StoreInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value Pointer { get; }
    public Value StoredValue { get; }
    public uint Alignment { get; set; }
    public bool IsVolatile { get; set; }
    public uint Ordering { get; set; }
    public uint SyncScope { get; set; } = Codes.SyncScope.System;

    private readonly Value[] _operands;

    public StoreInstruction(LlvmType voidType, Value pointer, Value storedValue, uint alignment = 0)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        StoredValue = storedValue ?? throw new ArgumentNullException(nameof(storedValue));
        Alignment = alignment;
        _operands = new[] { pointer, storedValue };
    }

    public bool IsAtomic => Ordering != 0;
    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CastInstruction : Instruction
{
    private readonly LlvmType _destType;
    public uint Opcode { get; }
    public Value Operand { get; }

    private readonly Value[] _operands;

    public CastInstruction(uint opcode, Value operand, LlvmType destType)
    {
        Opcode = opcode;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        _destType = destType ?? throw new ArgumentNullException(nameof(destType));
        _operands = new[] { operand };
    }

    public override LlvmType Type => _destType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CompareInstruction : Instruction
{
    private readonly LlvmType _resultType;
    public uint Predicate { get; }
    public Value Left { get; }
    public Value Right { get; }

    private readonly Value[] _operands;

    public CompareInstruction(uint predicate, Value left, Value right, LlvmType resultType)
    {
        Predicate = predicate;
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        _operands = new[] { left, right };
    }

    /// <summary>fast-math flags; only meaningful for fcmp.</summary>
    public FastMathFlags Fmf { get; set; }

    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class GetElementPtrInstruction : Instruction
{
    private readonly PointerType _ptrType;
    public LlvmType SourceElementType { get; }
    public Value Pointer { get; }
    public IReadOnlyList<Value> Indices { get; }
    public bool IsInBounds { get; set; }

    private readonly Value[] _operands;

    public GetElementPtrInstruction(LlvmType sourceElementType, Value pointer, IReadOnlyList<Value> indices, PointerType pointerType)
    {
        SourceElementType = sourceElementType ?? throw new ArgumentNullException(nameof(sourceElementType));
        Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));
        var arr = new Value[1 + Indices.Count];
        arr[0] = pointer;
        for (int i = 0; i < Indices.Count; i++) arr[i + 1] = Indices[i];
        _operands = arr;
    }

    public override LlvmType Type => _ptrType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class PhiInstruction : Instruction
{
    private readonly LlvmType _type;
    public List<(Value Value, BasicBlock Block)> Incomings { get; } = new();

    public PhiInstruction(LlvmType type)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    private Value[]? _operandsCache;

    public PhiInstruction AddIncoming(Value value, BasicBlock block)
    {
        if (!ReferenceEquals(value.Type, _type))
            throw new ArgumentException("phi incoming value type must match phi type");
        Incomings.Add((value, block));
        _operandsCache = null;
        return this;
    }

    public override LlvmType Type => _type;
    public override ReadOnlySpan<Value> Operands
    {
        get
        {
            if (_operandsCache is null)
            {
                var arr = new Value[Incomings.Count];
                for (int i = 0; i < Incomings.Count; i++) arr[i] = Incomings[i].Value;
                _operandsCache = arr;
            }
            return _operandsCache;
        }
    }
}

public sealed class SelectInstruction : Instruction
{
    public Value Condition { get; }
    public Value TrueValue { get; }
    public Value FalseValue { get; }

    private readonly Value[] _operands;

    public SelectInstruction(Value condition, Value trueValue, Value falseValue)
    {
        if (!ReferenceEquals(trueValue.Type, falseValue.Type))
            throw new ArgumentException("select arms must have matching types");
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        TrueValue = trueValue;
        FalseValue = falseValue;
        _operands = new[] { condition, trueValue, falseValue };
    }

    public override LlvmType Type => TrueValue.Type;
    public override ReadOnlySpan<Value> Operands => _operands;
}

/// <summary>Operand bundle attached to a call/invoke. The runtime semantics depend on the tag
/// (e.g. "deopt", "funclet", "gc-live"). Inputs are emitted in a preceding <c>OPERAND_BUNDLE</c>
/// record; the tag is interned in the module's <c>OPERAND_BUNDLE_TAGS_BLOCK</c>.</summary>
public sealed class OperandBundle
{
    public string Tag { get; }
    public IReadOnlyList<Value> Inputs { get; }
    public OperandBundle(string tag, IReadOnlyList<Value> inputs)
    {
        Tag = tag ?? throw new ArgumentNullException(nameof(tag));
        Inputs = inputs ?? Array.Empty<Value>();
    }
}

public sealed class CallInstruction : Instruction
{
    public FunctionType FunctionType { get; }
    public Value Callee { get; }
    public IReadOnlyList<Value> Arguments { get; }
    public bool IsTailCall { get; set; }
    public bool IsMustTail { get; set; }
    public uint CallingConv { get; set; }
    public FastMathFlags Fmf { get; set; }

    /// <summary>Function-level attributes attached at the call site
    /// (e.g. <c>call nounwind ...</c>).</summary>
    public AttributeSet FunctionAttributes { get; } = new();
    /// <summary>Attributes attached to the call's return value.</summary>
    public AttributeSet ReturnAttributes { get; } = new();
    private readonly AttributeSet[] _paramAttrs;
    public AttributeSet GetParameterAttributes(int index) => _paramAttrs[index];

    public List<OperandBundle> Bundles { get; } = new();

    private Value[]? _operandsCache;
    private int _operandsCacheBundleStamp = -1;

    public CallInstruction(FunctionType functionType, Value callee, IReadOnlyList<Value> arguments)
    {
        FunctionType = functionType ?? throw new ArgumentNullException(nameof(functionType));
        Callee = callee ?? throw new ArgumentNullException(nameof(callee));
        Arguments = arguments ?? Array.Empty<Value>();
        // The bitcode reader for INST_CALL trusts FunctionType.NumParams to
        // size the argument list; an arity mismatch surfaces deep inside
        // llvm-dis as a generic "Invalid record" with no source locator.
        // Catch it eagerly here so the offending Build*Call site is the
        // one in the stack trace, not a Bitsmith writer routine.
        if (!functionType.IsVarArg && arguments is { Count: var n } && n != functionType.ParameterTypes.Count)
            throw new ArgumentException(
                $"CallInstruction arity mismatch: FunctionType has {functionType.ParameterTypes.Count} parameter(s), but {n} argument(s) supplied (callee={DescribeCallee(callee)}).",
                nameof(arguments));
        if (functionType.IsVarArg && arguments is { Count: var nv } && nv < functionType.ParameterTypes.Count)
            throw new ArgumentException(
                $"CallInstruction arity mismatch: vararg FunctionType has {functionType.ParameterTypes.Count} fixed parameter(s), but only {nv} argument(s) supplied (callee={DescribeCallee(callee)}).",
                nameof(arguments));

        // Per-argument type check: the LLVM 15 bitcode reader resolves each
        // fixed call argument with `getValueFwdRef(idx, FTy->getParamType(i),
        // ...)`, which returns nullptr (and the reader bails with a generic
        // "Invalid record") whenever the existing value-table slot's type
        // doesn't match the requested type. Failing at construction time
        // pins the bug to the BuildCall site instead of llvm-dis.
        for (int i = 0; i < Arguments.Count; i++)
        {
            if (i >= functionType.ParameterTypes.Count) break;
            var got = Arguments[i].Type;
            var want = functionType.ParameterTypes[i];
            if (!ReferenceEquals(got, want))
                throw new ArgumentException(
                    $"CallInstruction arg{i} type mismatch: FunctionType.ParameterTypes[{i}] expects type id={want.Id} ({want.GetType().Name}), but argument has type id={got.Id} ({got.GetType().Name}). callee={DescribeCallee(callee)}, arg.GetType()={Arguments[i].GetType().Name}.",
                    nameof(arguments));
        }

        _paramAttrs = new AttributeSet[Arguments.Count];
        for (int i = 0; i < _paramAttrs.Length; i++) _paramAttrs[i] = new AttributeSet();
    }

    private static string DescribeCallee(Value v) => v switch
    {
        Function f => "@" + f.Name,
        GlobalVariable g => "@" + g.Name,
        _ => v.GetType().Name,
    };

    public override LlvmType Type => FunctionType.ReturnType;
    public override ReadOnlySpan<Value> Operands
    {
        get
        {
            int stamp = Bundles.Count;
            if (_operandsCache is null || _operandsCacheBundleStamp != stamp)
            {
                int total = 1 + Arguments.Count;
                for (int i = 0; i < Bundles.Count; i++) total += Bundles[i].Inputs.Count;
                var arr = new Value[total];
                int idx = 0;
                arr[idx++] = Callee;
                for (int i = 0; i < Arguments.Count; i++) arr[idx++] = Arguments[i];
                for (int i = 0; i < Bundles.Count; i++)
                {
                    var inputs = Bundles[i].Inputs;
                    for (int j = 0; j < inputs.Count; j++) arr[idx++] = inputs[j];
                }
                _operandsCache = arr;
                _operandsCacheBundleStamp = stamp;
            }
            return _operandsCache;
        }
    }
}

public sealed class ExtractElementInstruction : Instruction
{
    public Value Vector { get; }
    public Value Index { get; }
    private readonly LlvmType _elementType;

    private readonly Value[] _operands;

    public ExtractElementInstruction(Value vector, Value index)
    {
        Vector = vector ?? throw new ArgumentNullException(nameof(vector));
        Index = index ?? throw new ArgumentNullException(nameof(index));
        if (vector.Type is not VectorType vt)
            throw new ArgumentException("extractelement requires a vector operand");
        _elementType = vt.ElementType;
        _operands = new[] { vector, index };
    }

    public override LlvmType Type => _elementType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class InsertElementInstruction : Instruction
{
    public Value Vector { get; }
    public Value Element { get; }
    public Value Index { get; }

    private readonly Value[] _operands;

    public InsertElementInstruction(Value vector, Value element, Value index)
    {
        Vector = vector ?? throw new ArgumentNullException(nameof(vector));
        Element = element ?? throw new ArgumentNullException(nameof(element));
        Index = index ?? throw new ArgumentNullException(nameof(index));
        if (vector.Type is not VectorType)
            throw new ArgumentException("insertelement requires a vector operand");
        _operands = new[] { vector, element, index };
    }

    public override LlvmType Type => Vector.Type;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class ShuffleVectorInstruction : Instruction
{
    private readonly VectorType _resultType;
    public Value Vector1 { get; }
    public Value Vector2 { get; }
    public Value Mask { get; }

    private readonly Value[] _operands;

    public ShuffleVectorInstruction(Value v1, Value v2, Value mask, VectorType resultType)
    {
        Vector1 = v1 ?? throw new ArgumentNullException(nameof(v1));
        Vector2 = v2 ?? throw new ArgumentNullException(nameof(v2));
        Mask = mask ?? throw new ArgumentNullException(nameof(mask));
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        _operands = new[] { v1, v2, mask };
    }

    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class FenceInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public uint Ordering { get; }
    public uint SyncScope { get; set; } = Codes.SyncScope.System;

    public FenceInstruction(LlvmType voidType, uint ordering)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        Ordering = ordering;
    }

    public override LlvmType Type => _voidType;
}

public sealed class AtomicRmwInstruction : Instruction
{
    private readonly LlvmType _resultType;
    public uint Operation { get; }
    public Value Pointer { get; }
    public Value Value { get; }
    public uint Ordering { get; set; }
    public uint SyncScope { get; set; } = Codes.SyncScope.System;
    public bool IsVolatile { get; set; }
    public uint Alignment { get; set; }

    private readonly Value[] _operands;

    public AtomicRmwInstruction(uint operation, Value pointer, Value value, uint ordering, LlvmType resultType)
    {
        Operation = operation;
        Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Ordering = ordering;
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        _operands = new[] { pointer, value };
    }

    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CmpXchgInstruction : Instruction
{
    private readonly StructType _resultType;
    public Value Pointer { get; }
    public Value Compare { get; }
    public Value New { get; }
    public uint SuccessOrdering { get; set; }
    public uint FailureOrdering { get; set; }
    public uint SyncScope { get; set; } = Codes.SyncScope.System;
    public bool IsVolatile { get; set; }
    public bool IsWeak { get; set; }
    public uint Alignment { get; set; }

    private readonly Value[] _operands;

    public CmpXchgInstruction(Value pointer, Value compare, Value newVal, uint successOrdering, uint failureOrdering, StructType resultType)
    {
        Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        Compare = compare ?? throw new ArgumentNullException(nameof(compare));
        New = newVal ?? throw new ArgumentNullException(nameof(newVal));
        SuccessOrdering = successOrdering;
        FailureOrdering = failureOrdering;
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        _operands = new[] { pointer, compare, newVal };
    }

    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public enum UnaryOpcode
{
    FNeg = 0,
}

public sealed class UnaryOperator : Instruction
{
    public UnaryOpcode Opcode { get; }
    public Value Operand { get; }
    public FastMathFlags Fmf { get; set; }

    private readonly Value[] _operands;

    public UnaryOperator(UnaryOpcode opcode, Value operand)
    {
        Opcode = opcode;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        _operands = new[] { operand };
    }

    public override LlvmType Type => Operand.Type;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class FreezeInstruction : Instruction
{
    public Value Operand { get; }
    private readonly Value[] _operands;
    public FreezeInstruction(Value operand)
    {
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        _operands = new[] { operand };
    }
    public override LlvmType Type => Operand.Type;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class VaArgInstruction : Instruction
{
    private readonly LlvmType _resultType;
    public Value ValistType { get; }
    public Value Valist => ValistType;
    public LlvmType ListType { get; }

    private readonly Value[] _operands;
    public VaArgInstruction(LlvmType listType, Value valist, LlvmType resultType)
    {
        ListType = listType ?? throw new ArgumentNullException(nameof(listType));
        ValistType = valist ?? throw new ArgumentNullException(nameof(valist));
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        _operands = new[] { valist };
    }
    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class ExtractValueInstruction : Instruction
{
    private readonly LlvmType _resultType;
    public Value Aggregate { get; }
    public IReadOnlyList<uint> Indices { get; }

    private readonly Value[] _operands;
    public ExtractValueInstruction(Value aggregate, IReadOnlyList<uint> indices, LlvmType resultType)
    {
        Aggregate = aggregate ?? throw new ArgumentNullException(nameof(aggregate));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
        _operands = new[] { aggregate };
    }
    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class InsertValueInstruction : Instruction
{
    public Value Aggregate { get; }
    public Value Element { get; }
    public IReadOnlyList<uint> Indices { get; }
    private readonly Value[] _operands;
    public InsertValueInstruction(Value aggregate, Value element, IReadOnlyList<uint> indices)
    {
        Aggregate = aggregate ?? throw new ArgumentNullException(nameof(aggregate));
        Element = element ?? throw new ArgumentNullException(nameof(element));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        _operands = new[] { aggregate, element };
    }
    public override LlvmType Type => Aggregate.Type;
    public override ReadOnlySpan<Value> Operands => _operands;
}

/// <summary>
/// Invoke — like a call, but with explicit normal/unwind successors.
/// Used by Itanium-style exception handling along with landingpad/resume.
/// </summary>
public sealed class InvokeInstruction : Instruction
{
    public FunctionType FunctionType { get; }
    public Value Callee { get; }
    public IReadOnlyList<Value> Arguments { get; }
    public BasicBlock NormalDest { get; }
    public BasicBlock UnwindDest { get; }
    public uint CallingConv { get; set; }

    public AttributeSet FunctionAttributes { get; } = new();
    public AttributeSet ReturnAttributes { get; } = new();
    private readonly AttributeSet[] _paramAttrs;
    public AttributeSet GetParameterAttributes(int index) => _paramAttrs[index];

    public List<OperandBundle> Bundles { get; } = new();

    private Value[]? _operandsCache;
    private int _operandsCacheBundleStamp = -1;

    public InvokeInstruction(FunctionType functionType, Value callee, IReadOnlyList<Value> arguments,
        BasicBlock normalDest, BasicBlock unwindDest)
    {
        FunctionType = functionType ?? throw new ArgumentNullException(nameof(functionType));
        Callee = callee ?? throw new ArgumentNullException(nameof(callee));
        Arguments = arguments ?? Array.Empty<Value>();
        NormalDest = normalDest ?? throw new ArgumentNullException(nameof(normalDest));
        UnwindDest = unwindDest ?? throw new ArgumentNullException(nameof(unwindDest));

        // Mirror the arity / type checks added to CallInstruction —
        // INVOKE shares the same `getValue(... FTy->getParamType(i))`
        // reader contract so the same shape mismatches surface as the
        // generic "Invalid record" error otherwise.
        if (!functionType.IsVarArg && arguments is { Count: var n } && n != functionType.ParameterTypes.Count)
            throw new ArgumentException(
                $"InvokeInstruction arity mismatch: FunctionType has {functionType.ParameterTypes.Count} parameter(s), but {n} argument(s) supplied (callee={DescribeCalleeStatic(callee)}).",
                nameof(arguments));
        if (functionType.IsVarArg && arguments is { Count: var nv } && nv < functionType.ParameterTypes.Count)
            throw new ArgumentException(
                $"InvokeInstruction arity mismatch: vararg FunctionType has {functionType.ParameterTypes.Count} fixed parameter(s), but only {nv} argument(s) supplied (callee={DescribeCalleeStatic(callee)}).",
                nameof(arguments));
        for (int i = 0; i < Arguments.Count; i++)
        {
            if (i >= functionType.ParameterTypes.Count) break;
            var got = Arguments[i].Type;
            var want = functionType.ParameterTypes[i];
            if (!ReferenceEquals(got, want))
                throw new ArgumentException(
                    $"InvokeInstruction arg{i} type mismatch: FunctionType.ParameterTypes[{i}] expects type id={want.Id} ({want.GetType().Name}), but argument has type id={got.Id} ({got.GetType().Name}). callee={DescribeCalleeStatic(callee)}, arg.GetType()={Arguments[i].GetType().Name}.",
                    nameof(arguments));
        }

        _paramAttrs = new AttributeSet[Arguments.Count];
        for (int i = 0; i < _paramAttrs.Length; i++) _paramAttrs[i] = new AttributeSet();
    }

    private static string DescribeCalleeStatic(Value v) => v switch
    {
        Function f => "@" + f.Name,
        GlobalVariable g => "@" + g.Name,
        _ => v.GetType().Name,
    };

    public override LlvmType Type => FunctionType.ReturnType;
    public override ReadOnlySpan<Value> Operands
    {
        get
        {
            int stamp = Bundles.Count;
            if (_operandsCache is null || _operandsCacheBundleStamp != stamp)
            {
                int total = 1 + Arguments.Count;
                for (int i = 0; i < Bundles.Count; i++) total += Bundles[i].Inputs.Count;
                var arr = new Value[total];
                int idx = 0;
                arr[idx++] = Callee;
                for (int i = 0; i < Arguments.Count; i++) arr[idx++] = Arguments[i];
                for (int i = 0; i < Bundles.Count; i++)
                {
                    var inputs = Bundles[i].Inputs;
                    for (int j = 0; j < inputs.Count; j++) arr[idx++] = inputs[j];
                }
                _operandsCache = arr;
                _operandsCacheBundleStamp = stamp;
            }
            return _operandsCache;
        }
    }
}

public sealed class ResumeInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value Value { get; }
    private readonly Value[] _operands;
    public ResumeInstruction(LlvmType voidType, Value value)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        _operands = new[] { value };
    }
    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

/// <summary>Landing pad clause kind: a catch handler or a filter list.</summary>
public enum LandingpadClauseKind { Catch = 0, Filter = 1 }

public sealed class LandingpadClause
{
    public LandingpadClauseKind Kind { get; }
    /// <summary>For <see cref="LandingpadClauseKind.Catch"/>: the type/global being caught.
    /// For <see cref="LandingpadClauseKind.Filter"/>: an array constant of the allowed types.</summary>
    public Value Operand { get; }
    public LandingpadClause(LandingpadClauseKind kind, Value operand)
    {
        Kind = kind;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }
}

public sealed class LandingpadInstruction : Instruction
{
    private readonly LlvmType _resultType;
    public bool IsCleanup { get; set; }
    public List<LandingpadClause> Clauses { get; } = new();

    private Value[]? _operandsCache;
    private int _operandsCacheStamp = -1;

    public LandingpadInstruction(LlvmType resultType)
    {
        _resultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
    }
    public override LlvmType Type => _resultType;
    public override ReadOnlySpan<Value> Operands
    {
        get
        {
            int stamp = Clauses.Count;
            if (_operandsCache is null || _operandsCacheStamp != stamp)
            {
                var arr = new Value[Clauses.Count];
                for (int i = 0; i < Clauses.Count; i++) arr[i] = Clauses[i].Operand;
                _operandsCache = arr;
                _operandsCacheStamp = stamp;
            }
            return _operandsCache;
        }
    }
}

/// <summary>callbr — indirect branch via inline asm. The result type is the
/// callee function's return type; targets list the possible labels.</summary>
public sealed class CallBrInstruction : Instruction
{
    public FunctionType FunctionType { get; }
    public Value Callee { get; }
    public IReadOnlyList<Value> Arguments { get; }
    public BasicBlock DefaultDest { get; }
    public IReadOnlyList<BasicBlock> IndirectDests { get; }
    public uint CallingConv { get; set; }

    private readonly Value[] _operands;

    public CallBrInstruction(FunctionType ft, Value callee, IReadOnlyList<Value> args,
        BasicBlock defaultDest, IReadOnlyList<BasicBlock> indirectDests)
    {
        FunctionType = ft ?? throw new ArgumentNullException(nameof(ft));
        Callee = callee ?? throw new ArgumentNullException(nameof(callee));
        Arguments = args ?? Array.Empty<Value>();
        DefaultDest = defaultDest ?? throw new ArgumentNullException(nameof(defaultDest));
        IndirectDests = indirectDests ?? Array.Empty<BasicBlock>();
        var arr = new Value[1 + Arguments.Count];
        arr[0] = callee;
        for (int i = 0; i < Arguments.Count; i++) arr[i + 1] = Arguments[i];
        _operands = arr;
    }

    public override LlvmType Type => FunctionType.ReturnType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

/// <summary>catchswitch — dispatches to one of the catchpad handlers based on the unwound exception.
/// Result is a token consumed by catchpad.</summary>
public sealed class CatchSwitchInstruction : Instruction
{
    private readonly TokenType _tokenType;
    public Value? ParentPad { get; }
    public List<BasicBlock> Handlers { get; } = new();
    public BasicBlock? UnwindDest { get; set; }   // null => unwind to caller

    private readonly Value[] _operands;
    public CatchSwitchInstruction(TokenType tokenType, Value? parentPad)
    {
        _tokenType = tokenType ?? throw new ArgumentNullException(nameof(tokenType));
        ParentPad = parentPad;
        _operands = parentPad is null ? Array.Empty<Value>() : new[] { parentPad };
    }
    public override LlvmType Type => _tokenType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CatchPadInstruction : Instruction
{
    private readonly TokenType _tokenType;
    public Value CatchSwitch { get; }
    public IReadOnlyList<Value> Args { get; }
    private readonly Value[] _operands;
    public CatchPadInstruction(TokenType tokenType, Value catchSwitch, IReadOnlyList<Value> args)
    {
        _tokenType = tokenType ?? throw new ArgumentNullException(nameof(tokenType));
        CatchSwitch = catchSwitch ?? throw new ArgumentNullException(nameof(catchSwitch));
        Args = args ?? Array.Empty<Value>();
        var arr = new Value[1 + Args.Count];
        arr[0] = catchSwitch;
        for (int i = 0; i < Args.Count; i++) arr[i + 1] = Args[i];
        _operands = arr;
    }
    public override LlvmType Type => _tokenType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CleanupPadInstruction : Instruction
{
    private readonly TokenType _tokenType;
    public Value? ParentPad { get; }
    public IReadOnlyList<Value> Args { get; }
    private readonly Value[] _operands;
    public CleanupPadInstruction(TokenType tokenType, Value? parentPad, IReadOnlyList<Value> args)
    {
        _tokenType = tokenType ?? throw new ArgumentNullException(nameof(tokenType));
        ParentPad = parentPad;
        Args = args ?? Array.Empty<Value>();
        int n = (parentPad is null ? 0 : 1) + Args.Count;
        var arr = new Value[n];
        int idx = 0;
        if (parentPad is not null) arr[idx++] = parentPad;
        for (int i = 0; i < Args.Count; i++) arr[idx++] = Args[i];
        _operands = arr;
    }
    public override LlvmType Type => _tokenType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CatchRetInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value CatchPad { get; }
    public BasicBlock SuccessorBlock { get; }
    private readonly Value[] _operands;
    public CatchRetInstruction(LlvmType voidType, Value catchPad, BasicBlock successor)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        CatchPad = catchPad ?? throw new ArgumentNullException(nameof(catchPad));
        SuccessorBlock = successor ?? throw new ArgumentNullException(nameof(successor));
        _operands = new[] { catchPad };
    }
    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

public sealed class CleanupRetInstruction : Instruction
{
    private readonly LlvmType _voidType;
    public Value CleanupPad { get; }
    /// <summary>null => unwind to caller</summary>
    public BasicBlock? UnwindDest { get; }
    private readonly Value[] _operands;
    public CleanupRetInstruction(LlvmType voidType, Value cleanupPad, BasicBlock? unwindDest)
    {
        _voidType = voidType ?? throw new ArgumentNullException(nameof(voidType));
        CleanupPad = cleanupPad ?? throw new ArgumentNullException(nameof(cleanupPad));
        UnwindDest = unwindDest;
        _operands = new[] { cleanupPad };
    }
    public override LlvmType Type => _voidType;
    public override ReadOnlySpan<Value> Operands => _operands;
}

using System;
using System.Collections.Generic;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

internal sealed class FunctionWriter
{
    private const int FunctionAbbrevWidth = 4;

    private readonly BitstreamWriter _w;
    private readonly ValueEnumerator _ve;
    private readonly TypeContext _types;

    public FunctionWriter(BitstreamWriter w, ValueEnumerator ve, TypeContext types)
    {
        _w = w;
        _ve = ve;
        _types = types;
    }

    public void Write(Function fn)
    {
        _w.EnterSubBlock(BlockIds.Function, FunctionAbbrevWidth);
        _ve.IncorporateFunction(fn);

        try
        {
            // Block-local abbrevs for the most frequent function-block records.
            //   declareBlocksAbbrev: [Literal(DeclareBlocks), Vbr(6) numblocks]
            //   retVoidAbbrev:       [Literal(InstRet)]
            //   retValAbbrev:        [Literal(InstRet), Vbr(6) opval]
            //   binOpAbbrev:         [Literal(InstBinOp), Vbr(6) left, Vbr(6) right, Vbr(4) opcode]
            _declareBlocksAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(FunctionCodes.DeclareBlocks), AbbrevOp.Vbr(6));
            _retVoidAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(FunctionCodes.InstRet));
            _retValAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(FunctionCodes.InstRet), AbbrevOp.Vbr(6));
            _binOpAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstBinOp),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Vbr(4));

            _w.WriteAbbrevRecord(_declareBlocksAbbrev, (ulong)fn.BasicBlocks.Count);

            // No function-local CONSTANTS_BLOCK or METADATA_BLOCK yet (none needed for the m4 example).

            // InstID starts at NumModuleValues + NumArgs + NumLocalConsts.
            // It increments only for instructions whose type is not void.
            int instId = _ve.ModuleValueCount + fn.Parameters.Count;

            foreach (var bb in fn.BasicBlocks)
            {
                foreach (var inst in bb.Instructions)
                {
                    WriteInstruction(inst, instId);
                    if (inst.Type is not VoidType)
                        instId++;
                }
            }
        }
        finally
        {
            _ve.DeincorporateFunction();
            _w.ExitBlock();
        }
    }

    private uint _declareBlocksAbbrev, _retVoidAbbrev, _retValAbbrev, _binOpAbbrev;

    private void WriteInstruction(Instruction inst, int instId)
    {
        switch (inst)
        {
            case BinaryOperator b:
                WriteBinaryOp(b, instId);
                return;
            case ReturnInstruction r:
                WriteRet(r, instId);
                return;
        }
        throw new NotSupportedException($"unsupported instruction {inst.GetType().Name}");
    }

    private void WriteBinaryOp(BinaryOperator b, int instId)
    {
        // [opval, (ty?), opval, opcode, (flags?)]
        ulong flags = EncodeBinaryOpFlags(b);
        int leftId = _ve.GetValueId(b.Left);
        bool leftForward = leftId >= instId;

        // Fast path: no flags, no forward-ref → 3-op abbrev.
        if (!leftForward && flags == 0)
        {
            _w.WriteAbbrevRecord(_binOpAbbrev,
                (ulong)(uint)(instId - leftId),
                (ulong)(uint)(instId - _ve.GetValueId(b.Right)),
                (ulong)b.Opcode);
            return;
        }

        var ops = new List<ulong>(5);
        PushValueAndType(b.Left, instId, ops);
        PushValue(b.Right, instId, ops);
        ops.Add((ulong)b.Opcode);
        if (flags != 0) ops.Add(flags);
        _w.WriteUnabbrevRecord(FunctionCodes.InstBinOp, ops.ToArray());
    }

    /// <summary>
    /// Reproduces <c>BitcodeWriter::getOptimizationFlags</c>. Bit positions:
    ///   OBO ops (Add/Sub/Mul/Shl, integer): nuw=bit0, nsw=bit1
    ///   PEO ops (UDiv/SDiv/LShr/AShr, integer): exact=bit0
    ///   FP ops: <see cref="FastMathFlags"/> raw bits
    /// </summary>
    private static ulong EncodeBinaryOpFlags(BinaryOperator b)
    {
        bool isFloat = IsFloatLike(b.Left.Type);
        switch (b.Opcode)
        {
            case BinaryOpcode.Add:
            case BinaryOpcode.Sub:
            case BinaryOpcode.Mul:
            case BinaryOpcode.Shl:
                if (isFloat) return (ulong)b.Fmf;
                return (b.IsNuw ? 1UL : 0UL) | (b.IsNsw ? 2UL : 0UL);

            case BinaryOpcode.UDiv:
            case BinaryOpcode.SDiv:
            case BinaryOpcode.LShr:
            case BinaryOpcode.AShr:
                if (isFloat) return (ulong)b.Fmf;
                return b.IsExact ? 1UL : 0UL;

            case BinaryOpcode.URem:
            case BinaryOpcode.SRem:
                return isFloat ? (ulong)b.Fmf : 0UL;

            default:
                return 0UL;
        }
    }

    private static bool IsFloatLike(LlvmType t)
    {
        if (t is VectorType v) t = v.ElementType;
        return t is FloatType or DoubleType or HalfType or BFloatType
            or X86Fp80Type or Fp128Type or PpcFp128Type;
    }

    private void WriteRet(ReturnInstruction r, int instId)
    {
        if (r.ReturnValue is null)
        {
            _w.WriteAbbrevRecord(_retVoidAbbrev);
            return;
        }
        int valId = _ve.GetValueId(r.ReturnValue);
        if (valId < instId)
        {
            // Backward reference — single-op abbrev path.
            _w.WriteAbbrevRecord(_retValAbbrev, (ulong)(uint)(instId - valId));
            return;
        }
        var ops = new List<ulong>(2);
        PushValueAndType(r.ReturnValue, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstRet, ops.ToArray());
    }

    /// <summary>
    /// Pushes a relative value reference. If the operand is a forward reference
    /// (ValID >= InstID), also pushes the operand's type id so the reader can
    /// resolve it without yet having seen the producing instruction.
    /// </summary>
    private void PushValueAndType(Value v, int instId, List<ulong> ops)
    {
        int valId = _ve.GetValueId(v);
        ops.Add((ulong)(uint)(instId - valId));
        if (valId >= instId)
            ops.Add((ulong)v.Type.Id);
    }

    private void PushValue(Value v, int instId, List<ulong> ops)
    {
        int valId = _ve.GetValueId(v);
        ops.Add((ulong)(uint)(instId - valId));
    }
}

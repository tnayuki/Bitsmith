using System;
using System.Collections.Generic;
using System.Numerics;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

internal static class ConstantWriter
{
    private const int ConstantsAbbrevWidth = 4;

    public static void WriteModuleConstants(BitstreamWriter w, IReadOnlyList<Constant> constants, Func<Value, int>? getValueId = null)
    {
        if (constants.Count == 0) return;

        w.EnterSubBlock(BlockIds.Constants, ConstantsAbbrevWidth);

        // Block-local abbrevs for the most frequent CONSTANTS_BLOCK records:
        //   setTypeAbbrev:  [Literal(SetType), Vbr(6) typeId]
        //   nullAbbrev:     [Literal(Null)]
        //   undefAbbrev:    [Literal(Undef)]
        //   integerAbbrev:  [Literal(Integer), Vbr(8) signRotated]
        //   aggregateAbbrev:[Literal(Aggregate), Array, Vbr(8) elemId]
        var setTypeAbbrev = w.DefineAbbrev(AbbrevOp.Literal(ConstantCodes.SetType), AbbrevOp.Vbr(6));
        var nullAbbrev = w.DefineAbbrev(AbbrevOp.Literal(ConstantCodes.Null));
        var undefAbbrev = w.DefineAbbrev(AbbrevOp.Literal(ConstantCodes.Undef));
        var integerAbbrev = w.DefineAbbrev(AbbrevOp.Literal(ConstantCodes.Integer), AbbrevOp.Vbr(8));
        var aggregateAbbrev = w.DefineAbbrev(AbbrevOp.Literal(ConstantCodes.Aggregate), AbbrevOp.Array(), AbbrevOp.Vbr(8));

        LlvmType? currentType = null;
        foreach (var c in constants)
        {
            if (!ReferenceEquals(c.Type, currentType))
            {
                w.WriteAbbrevRecord(setTypeAbbrev, (ulong)c.Type.Id);
                currentType = c.Type;
            }
            WriteConstant(w, c, getValueId, nullAbbrev, undefAbbrev, integerAbbrev, aggregateAbbrev);
        }

        w.ExitBlock();
    }

    private static void WriteConstant(BitstreamWriter w, Constant c, Func<Value, int>? getValueId,
        uint nullAbbrev, uint undefAbbrev, uint integerAbbrev, uint aggregateAbbrev)
    {
        switch (c)
        {
            case NullConstant: w.WriteAbbrevRecord(nullAbbrev); return;
            case UndefConstant: w.WriteAbbrevRecord(undefAbbrev); return;
            case PoisonConstant: w.WriteUnabbrevRecord(ConstantCodes.Poison); return;
            case IntegerConstant i: WriteInteger(w, i, integerAbbrev); return;
            case FloatingPointConstant fp: WriteFloat(w, fp); return;
            case AggregateConstant agg: WriteAggregate(w, agg, getValueId, aggregateAbbrev); return;
            case StringConstant s: WriteString(w, s); return;
            case ConstantCast cc: WriteConstantCast(w, cc, getValueId); return;
            case ConstantGep cg: WriteConstantGep(w, cg, getValueId); return;
            case ConstantCmp ccmp: WriteConstantCmp(w, ccmp, getValueId); return;
            case InlineAsm ia: WriteInlineAsm(w, ia); return;
            case BlockAddress ba: WriteBlockAddress(w, ba, getValueId); return;
        }
        throw new NotSupportedException($"unsupported constant {c.GetType().Name}");
    }

    private static int RequireValueId(Func<Value, int>? f, Value v)
    {
        if (f is null)
            throw new InvalidOperationException("ConstExpr requires a value-id resolver");
        return f(v);
    }

    private static void WriteConstantCast(BitstreamWriter w, ConstantCast cc, Func<Value, int>? getValueId)
    {
        // [opcode, opty, opval]
        w.WriteUnabbrevRecord(ConstantCodes.Cast,
            cc.Opcode,
            (ulong)cc.Operand.Type.Id,
            (ulong)RequireValueId(getValueId, cc.Operand));
    }

    private static void WriteConstantGep(BitstreamWriter w, ConstantGep cg, Func<Value, int>? getValueId)
    {
        // [srcty, ptr_ty, ptr_val, idx_ty, idx_val, ...]
        var ops = new List<ulong>(3 + cg.Indices.Count * 2)
        {
            (ulong)cg.SourceElementType.Id,
            (ulong)cg.Pointer.Type.Id,
            (ulong)RequireValueId(getValueId, cg.Pointer),
        };
        foreach (var idx in cg.Indices)
        {
            ops.Add((ulong)idx.Type.Id);
            ops.Add((ulong)RequireValueId(getValueId, idx));
        }
        w.WriteUnabbrevRecord(cg.IsInBounds ? ConstantCodes.InboundsGep : ConstantCodes.Gep, ops.ToArray());
    }

    private static void WriteConstantCmp(BitstreamWriter w, ConstantCmp cc, Func<Value, int>? getValueId)
    {
        // [opty, opval1, opval2, predicate]
        w.WriteUnabbrevRecord(ConstantCodes.Cmp,
            (ulong)cc.Left.Type.Id,
            (ulong)RequireValueId(getValueId, cc.Left),
            (ulong)RequireValueId(getValueId, cc.Right),
            cc.Predicate);
    }

    private static void WriteBlockAddress(BitstreamWriter w, BlockAddress ba, Func<Value, int>? getValueId)
    {
        // CST_CODE_BLOCKADDRESS: [fnTypeId, fnValueId, bbIndex]
        // bbIndex is the 0-based position of the BB within fn.BasicBlocks.
        int bbIndex = -1;
        for (int i = 0; i < ba.Function.BasicBlocks.Count; i++)
            if (ReferenceEquals(ba.Function.BasicBlocks[i], ba.Block)) { bbIndex = i; break; }
        if (bbIndex < 0)
            throw new InvalidOperationException("BlockAddress refers to a basic block not in its function");
        w.WriteUnabbrevRecord(ConstantCodes.BlockAddress,
            (ulong)ba.Function.Type.Id,
            (ulong)RequireValueId(getValueId, ba.Function),
            (ulong)bbIndex);
    }

    private static void WriteInlineAsm(BitstreamWriter w, InlineAsm ia)
    {
        // CST_CODE_INLINEASM (LLVM 15, opaque pointers):
        //   [fnTypeId, flags, asmStrLen, asmStrChars..., conStrLen, conStrChars...]
        // flags: bit0=hasSideEffects, bit1=isAlignStack, bit2=dialect, bit3=canThrow
        ulong flags = (ia.HasSideEffects ? 1UL : 0UL)
                    | (ia.IsAlignStack ? 2UL : 0UL)
                    | ((ulong)ia.Dialect << 2)
                    | (ia.CanThrow ? 8UL : 0UL);
        var asmBytes = System.Text.Encoding.UTF8.GetBytes(ia.AsmString);
        var conBytes = System.Text.Encoding.UTF8.GetBytes(ia.Constraints);
        var ops = new List<ulong>(4 + asmBytes.Length + conBytes.Length)
        {
            (ulong)ia.FunctionType.Id,
            flags,
            (ulong)asmBytes.Length,
        };
        foreach (var b in asmBytes) ops.Add(b);
        ops.Add((ulong)conBytes.Length);
        foreach (var b in conBytes) ops.Add(b);
        w.WriteUnabbrevRecord(ConstantCodes.InlineAsm, ops.ToArray());
    }

    private static void WriteFloat(BitstreamWriter w, FloatingPointConstant fp)
    {
        var ops = new List<ulong>(2);
        fp.EncodeOperands(ops);
        w.WriteUnabbrevRecord(ConstantCodes.Float, ops.ToArray());
    }

    private static void WriteAggregate(BitstreamWriter w, AggregateConstant agg, Func<Value, int>? getValueId, uint aggregateAbbrev)
    {
        if (getValueId is null)
            throw new InvalidOperationException("AggregateConstant requires a value-id resolver");
        var ops = new ulong[agg.Elements.Count];
        for (int i = 0; i < agg.Elements.Count; i++)
            ops[i] = (ulong)getValueId(agg.Elements[i]);
        w.WriteAbbrevRecord(aggregateAbbrev, ops);
    }

    private static void WriteString(BitstreamWriter w, StringConstant s)
    {
        var ops = new ulong[s.Bytes.Length];
        for (int i = 0; i < s.Bytes.Length; i++) ops[i] = s.Bytes[i];
        w.WriteUnabbrevRecord(s.IsCString ? ConstantCodes.CString : ConstantCodes.String, ops);
    }

    private static void WriteInteger(BitstreamWriter w, IntegerConstant i, uint integerAbbrev)
    {
        var v = i.Value;
        if (v >= long.MinValue && v <= long.MaxValue)
        {
            ulong rotated = SignRotate((long)v);
            w.WriteAbbrevRecord(integerAbbrev, rotated);
            return;
        }
        // Wide integer: split into 64-bit limbs (sign-rotated each).
        var bytes = v.ToByteArray();
        int limbs = (bytes.Length + 7) / 8;
        var ops = new ulong[limbs];
        for (int li = 0; li < limbs; li++)
        {
            ulong limb = 0;
            for (int b = 0; b < 8; b++)
            {
                int idx = li * 8 + b;
                if (idx < bytes.Length) limb |= (ulong)bytes[idx] << (b * 8);
                else if (v.Sign < 0) limb |= 0xFFUL << (b * 8);
            }
            ops[li] = SignRotate(unchecked((long)limb));
        }
        w.WriteUnabbrevRecord(ConstantCodes.WideInteger, ops);
    }

    private static ulong SignRotate(long v) =>
        v < 0 ? ((unchecked((ulong)-v)) << 1) | 1UL : ((ulong)v) << 1;
}

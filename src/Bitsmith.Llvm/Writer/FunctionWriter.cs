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
    private readonly IReadOnlyDictionary<Instruction, uint>? _callAttrIds;
    private readonly IReadOnlyDictionary<string, uint>? _bundleTagIds;
    private readonly MetadataEnumerator? _me;
    private readonly MetadataWriter? _mw;
    private Dictionary<BasicBlock, int> _bbIds = new();

    public FunctionWriter(BitstreamWriter w, ValueEnumerator ve, TypeContext types,
        IReadOnlyDictionary<Instruction, uint>? callAttrIds = null,
        IReadOnlyDictionary<string, uint>? bundleTagIds = null,
        MetadataEnumerator? me = null,
        MetadataWriter? mw = null)
    {
        _w = w;
        _ve = ve;
        _types = types;
        _callAttrIds = callAttrIds;
        _bundleTagIds = bundleTagIds;
        _me = me;
        _mw = mw;
    }

    private uint GetCallAttrId(Instruction inst) =>
        _callAttrIds is not null && _callAttrIds.TryGetValue(inst, out var id) ? id : 0u;

    /// <summary>Emits FUNC_CODE_OPERAND_BUNDLE records preceding a call/invoke. Each
    /// record references the tag's 0-based id in OPERAND_BUNDLE_TAGS_BLOCK and lists
    /// the bundle inputs as relative value+type pairs.</summary>
    private void WriteOperandBundles(IReadOnlyList<OperandBundle> bundles, int instId)
    {
        if (_bundleTagIds is null || bundles.Count == 0) return;
        foreach (var b in bundles)
        {
            var ops = new List<ulong>(1 + b.Inputs.Count * 2);
            ops.Add(_bundleTagIds[b.Tag]);
            foreach (var v in b.Inputs)
                PushValueAndType(v, instId, ops);
            _w.WriteUnabbrevRecord(FunctionCodes.OperandBundle, ops.ToArray());
        }
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
            // [Literal(InstLoad), Vbr(6) ptrRel, Vbr(6) tyId, Fixed(7) align, Fixed(1) vol]
            _loadAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstLoad),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Fixed(7), AbbrevOp.Fixed(1));
            // [Literal(InstStore), Vbr(6) ptrRel, Vbr(6) valRel, Fixed(7) align, Fixed(1) vol]
            _storeAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstStore),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Fixed(7), AbbrevOp.Fixed(1));
            // [Literal(InstCmp2), Vbr(6) leftRel, Vbr(6) rightRel, Vbr(6) predicate]
            _cmpAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstCmp2),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Vbr(6));
            // [Literal(InstCast), Vbr(6) opRel, Vbr(6) destTyId, Fixed(4) opcode]
            _castAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstCast),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Fixed(4));

            _w.WriteAbbrevRecord(_declareBlocksAbbrev, (ulong)fn.BasicBlocks.Count);

            _bbIds = new Dictionary<BasicBlock, int>(ReferenceComparer<BasicBlock>.Instance);
            for (int i = 0; i < fn.BasicBlocks.Count; i++)
                _bbIds[fn.BasicBlocks[i]] = i;

            // Function-local CONSTANTS_BLOCK — operand-side constants discovered by
            // ValueEnumerator. Instructions reference these via their assigned IDs.
            ConstantWriter.WriteModuleConstants(_w, _ve.FunctionConstants, _ve.GetValueId);

            // Function-local METADATA_BLOCK — MdValue/DiArgList referencing args
            // or instructions of this function. IDs continue from ModuleMetadataCount.
            EmitFunctionLocalMetadata(fn);

            // InstID starts at NumModuleValues + NumArgs + NumLocalConsts.
            // It increments only for instructions whose type is not void.
            int instId = _ve.ModuleValueCount + fn.Parameters.Count + _ve.FunctionConstants.Count;

            DiLocation? prevLoc = null;
            foreach (var bb in fn.BasicBlocks)
            {
                foreach (var inst in bb.Instructions)
                {
                    WriteInstruction(inst, instId);
                    EmitDebugLoc(inst.DebugLocation, ref prevLoc);
                    if (inst.Type is not VoidType)
                        instId++;
                }
            }

            WriteFunctionValueSymtab(fn);

            // Combine the function-level subprogram attachment (the
            // !dbg attached to the function itself) and per-instruction
            // attachments (!invariant.load, !range, !invariant.group,
            // !nonnull, !alias.scope, ...) into the single METADATA_
            // ATTACHMENT_BLOCK LLVM expects per function.
            if (_me is not null
                && (fn.Subprogram is not null || HasAnyInstructionAttachment(fn)))
                WriteFunctionAttachments(fn);
        }
        finally
        {
            _ve.DeincorporateFunction();
            _w.ExitBlock();
        }
    }

    private uint _declareBlocksAbbrev, _retVoidAbbrev, _retValAbbrev, _binOpAbbrev;
    private uint _loadAbbrev, _storeAbbrev, _cmpAbbrev, _castAbbrev;

    /// <summary>
    /// Emits a function-local VALUE_SYMTAB_BLOCK with names for any named basic blocks
    /// or named (non-void) instructions. The reader uses these to reconstruct
    /// <c>%entry:</c>, <c>%result = ...</c> etc. in the disassembly.
    /// </summary>
    private void WriteFunctionValueSymtab(Function fn)
    {
        bool any = false;
        foreach (var bb in fn.BasicBlocks)
        {
            if (!string.IsNullOrEmpty(bb.Name)) { any = true; break; }
            foreach (var inst in bb.Instructions)
                if (!string.IsNullOrEmpty(inst.Name) && inst.Type is not VoidType) { any = true; break; }
            if (any) break;
        }
        if (!any) return;

        _w.EnterSubBlock(BlockIds.ValueSymtab, 4);

        for (int bi = 0; bi < fn.BasicBlocks.Count; bi++)
        {
            var bb = fn.BasicBlocks[bi];
            if (string.IsNullOrEmpty(bb.Name)) continue;
            var bytes = System.Text.Encoding.UTF8.GetBytes(bb.Name!);
            var ops = new ulong[1 + bytes.Length];
            ops[0] = (ulong)bi;
            for (int i = 0; i < bytes.Length; i++) ops[1 + i] = bytes[i];
            _w.WriteUnabbrevRecord(2 /* VST_CODE_BBENTRY */, ops);
        }

        foreach (var bb in fn.BasicBlocks)
        {
            foreach (var inst in bb.Instructions)
            {
                if (string.IsNullOrEmpty(inst.Name) || inst.Type is VoidType) continue;
                int valId = _ve.GetValueId(inst);
                var bytes = System.Text.Encoding.UTF8.GetBytes(inst.Name!);
                var ops = new ulong[1 + bytes.Length];
                ops[0] = (ulong)valId;
                for (int i = 0; i < bytes.Length; i++) ops[1 + i] = bytes[i];
                _w.WriteUnabbrevRecord(1 /* VST_CODE_ENTRY */, ops);
            }
        }

        _w.ExitBlock();
    }

    private void EmitDebugLoc(DiLocation? loc, ref DiLocation? prev)
    {
        if (_me is null) return;
        if (loc is null) { prev = null; return; }
        if (ReferenceEquals(loc, prev))
        {
            _w.WriteUnabbrevRecord(DebugLocCodes.DebugLocAgain);
            return;
        }
        _w.WriteUnabbrevRecord(DebugLocCodes.DebugLoc,
            loc.Line,
            loc.Column,
            (ulong)_me.GetIdRequired(loc.Scope),
            (ulong)_me.GetIdOrNull(loc.InlinedAt),
            loc.IsImplicitCode ? 1UL : 0UL);
        prev = loc;
    }

    private static bool HasAnyInstructionAttachment(Function fn)
    {
        foreach (var bb in fn.BasicBlocks)
            foreach (var inst in bb.Instructions)
                if (inst.Attachments is { Count: > 0 }) return true;
        return false;
    }

    /// <summary>
    /// Emit the function's METADATA_ATTACHMENT_BLOCK. Per LLVM's
    /// bitcode format, a single block per function holds:
    ///   1. Optional function-level record <c>[kind, mdId]</c> (the
    ///      <c>!dbg</c> attached to the subprogram).
    ///   2. Per-instruction records <c>[instIndex, kind, mdId, ...]</c>
    ///      where <c>instIndex</c> is the 0-based position of the
    ///      instruction within the function's BB stream (NOT the
    ///      value-id used by SSA references).
    /// </summary>
    private void WriteFunctionAttachments(Function fn)
    {
        _w.EnterSubBlock(BlockIds.MetadataAttachment, 3);
        // METADATA_ATTACHMENT operand IDs are 0-based — distinct from the 1-based
        // encoding used by getMetadataOrNullID elsewhere.
        if (fn.Subprogram is not null)
        {
            _w.WriteUnabbrevRecord(MetadataCodes.Attachment,
                MetadataWriter.DbgKind,
                (ulong)fn.Subprogram.Id);
        }

        // Per-instruction attachments (non-dbg). instIndex is a flat 0-based
        // index over all instructions in BB iteration order; matches
        // BitcodeWriter::writeMetadataAttachment in LLVM. Record layout:
        //   [instIndex, kind_id, md_id, kind_id, md_id, ...]
        int instIndex = 0;
        foreach (var bb in fn.BasicBlocks)
        {
            foreach (var inst in bb.Instructions)
            {
                var atts = inst.Attachments;
                if (atts is { Count: > 0 } && _mw is not null)
                {
                    var ops = new ulong[1 + atts.Count * 2];
                    ops[0] = (ulong)instIndex;
                    int o = 1;
                    foreach (var (kindName, md) in atts)
                    {
                        ops[o++] = _mw.GetOrAllocateKindId(kindName);
                        ops[o++] = (ulong)md.Id;
                    }
                    _w.WriteUnabbrevRecord(MetadataCodes.Attachment, ops);
                }
                instIndex++;
            }
        }
        _w.ExitBlock();
    }

    private void WriteInstruction(Instruction inst, int instId)
    {
        switch (inst)
        {
            case BinaryOperator b: WriteBinaryOp(b, instId); return;
            case ReturnInstruction r: WriteRet(r, instId); return;
            case BranchInstruction br: WriteBr(br, instId); return;
            case SwitchInstruction sw: WriteSwitch(sw, instId); return;
            case UnreachableInstruction: _w.WriteUnabbrevRecord(FunctionCodes.InstUnreachable); return;
            case IndirectBrInstruction ibr: WriteIndirectBr(ibr, instId); return;
            case AllocaInstruction a: WriteAlloca(a); return;
            case LoadInstruction l: WriteLoad(l, instId); return;
            case StoreInstruction s: WriteStore(s, instId); return;
            case CastInstruction c: WriteCast(c, instId); return;
            case CompareInstruction cmp: WriteCmp(cmp, instId); return;
            case GetElementPtrInstruction g: WriteGep(g, instId); return;
            case PhiInstruction p: WritePhi(p, instId); return;
            case SelectInstruction sel: WriteSelect(sel, instId); return;
            case CallInstruction call: WriteCall(call, instId); return;
            case ExtractElementInstruction ee: WriteExtractElement(ee, instId); return;
            case InsertElementInstruction ie: WriteInsertElement(ie, instId); return;
            case ShuffleVectorInstruction sv: WriteShuffleVector(sv, instId); return;
            case FenceInstruction f: WriteFence(f); return;
            case AtomicRmwInstruction arw: WriteAtomicRmw(arw, instId); return;
            case CmpXchgInstruction cx: WriteCmpXchg(cx, instId); return;
            case UnaryOperator u: WriteUnaryOp(u, instId); return;
            case FreezeInstruction fz: WriteFreeze(fz, instId); return;
            case VaArgInstruction va: WriteVaArg(va, instId); return;
            case ExtractValueInstruction ev: WriteExtractValue(ev, instId); return;
            case InsertValueInstruction iv: WriteInsertValue(iv, instId); return;
            case InvokeInstruction inv: WriteInvoke(inv, instId); return;
            case ResumeInstruction res: WriteResume(res, instId); return;
            case LandingpadInstruction lp: WriteLandingpad(lp, instId); return;
            case CallBrInstruction cb: WriteCallBr(cb, instId); return;
            case CatchSwitchInstruction cs: WriteCatchSwitch(cs, instId); return;
            case CatchPadInstruction cp: WriteCatchPad(cp, instId); return;
            case CleanupPadInstruction clp: WriteCleanupPad(clp, instId); return;
            case CatchRetInstruction cr: WriteCatchRet(cr, instId); return;
            case CleanupRetInstruction clr: WriteCleanupRet(clr, instId); return;
        }
        throw new NotSupportedException($"unsupported instruction {inst.GetType().Name}");
    }

    private void WriteUnaryOp(UnaryOperator u, int instId)
    {
        // [opval+ty, opcode, (flags?)]
        var ops = new List<ulong>(4);
        PushValueAndType(u.Operand, instId, ops);
        ops.Add((ulong)u.Opcode);
        if (u.Fmf != FastMathFlags.None) ops.Add((ulong)u.Fmf);
        _w.WriteUnabbrevRecord(FunctionCodes.InstUnOp, ops.ToArray());
    }

    private void WriteFreeze(FreezeInstruction fz, int instId)
    {
        var ops = new List<ulong>(2);
        PushValueAndType(fz.Operand, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstFreeze, ops.ToArray());
    }

    private void WriteVaArg(VaArgInstruction va, int instId)
    {
        // [valisttype, valist, resulttype]
        var ops = new List<ulong>(3)
        {
            (ulong)va.ListType.Id,
            (ulong)(uint)(instId - _ve.GetValueId(va.ValistType)),
            (ulong)va.Type.Id,
        };
        _w.WriteUnabbrevRecord(FunctionCodes.InstVaArg, ops.ToArray());
    }

    private void WriteExtractValue(ExtractValueInstruction ev, int instId)
    {
        // [opval+ty, n x indices]
        var ops = new List<ulong>(2 + ev.Indices.Count);
        PushValueAndType(ev.Aggregate, instId, ops);
        foreach (var i in ev.Indices) ops.Add(i);
        _w.WriteUnabbrevRecord(FunctionCodes.InstExtractVal, ops.ToArray());
    }

    private void WriteInsertValue(InsertValueInstruction iv, int instId)
    {
        // [aggval+ty, eltval+ty, n x indices]
        var ops = new List<ulong>(4 + iv.Indices.Count);
        PushValueAndType(iv.Aggregate, instId, ops);
        PushValueAndType(iv.Element, instId, ops);
        foreach (var i in iv.Indices) ops.Add(i);
        _w.WriteUnabbrevRecord(FunctionCodes.InstInsertVal, ops.ToArray());
    }

    private void WriteInvoke(InvokeInstruction inv, int instId)
    {
        WriteOperandBundles(inv.Bundles, instId);
        // [paramattrs, callFlags, normalBB, unwindBB, FTy, callee+ty, args...]
        // callFlags: bits 0..12 calling conv, bit 13 reserved (unused), bit 14 = explicit FT.
        ulong flags = ((ulong)(inv.CallingConv & 0x1FFF))
                      | (1UL << 13);  // ExplicitType bit (LLVM 15 invoke encoding)
        int fixedParamCount = inv.FunctionType.ParameterTypes.Count;
        var ops = new List<ulong>(6 + 2 + fixedParamCount + (inv.Arguments.Count - fixedParamCount) * 2);
        ops.Add(GetCallAttrId(inv));
        ops.Add(flags);
        ops.Add((ulong)BbId(inv.NormalDest));
        ops.Add((ulong)BbId(inv.UnwindDest));
        ops.Add((ulong)inv.FunctionType.Id);
        PushValueAndType(inv.Callee, instId, ops);

        // LLVM 15 INVOKE / CALL writers emit *fixed* parameters with
        // pushValue() (1 op per arg; type derived from the FunctionType
        // slot at read time) and varargs with pushValueAndType() (1 op
        // for backref / 2 for forward ref + explicit type). Mismatching
        // the fixed-arg shape — for instance by always writing the type
        // — causes the reader's post-arg `if (Record.size() != OpNum)
        // return error("Invalid record")` check to bail when any of the
        // args was a forward reference (since that would have written 2
        // ops while the reader consumes only 1). Mirror the spec exactly.
        for (int i = 0; i < fixedParamCount && i < inv.Arguments.Count; i++)
            PushValue(inv.Arguments[i], instId, ops);
        if (inv.FunctionType.IsVarArg)
        {
            for (int i = fixedParamCount; i < inv.Arguments.Count; i++)
                PushValueAndType(inv.Arguments[i], instId, ops);
        }

        _w.WriteUnabbrevRecord(FunctionCodes.InstInvoke, ops.ToArray());
    }

    private void WriteResume(ResumeInstruction res, int instId)
    {
        var ops = new List<ulong>(2);
        PushValueAndType(res.Value, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstResume, ops.ToArray());
    }

    private void WriteLandingpad(LandingpadInstruction lp, int instId)
    {
        // [resty, cleanup, num_clauses, kind+typeId+value...]
        var ops = new List<ulong>(4 + lp.Clauses.Count * 3);
        ops.Add((ulong)lp.Type.Id);
        ops.Add(lp.IsCleanup ? 1UL : 0UL);
        ops.Add((ulong)lp.Clauses.Count);
        foreach (var c in lp.Clauses)
        {
            ops.Add((uint)c.Kind);
            PushValueAndType(c.Operand, instId, ops);
        }
        _w.WriteUnabbrevRecord(FunctionCodes.InstLandingpad, ops.ToArray());
    }

    private void WriteCallBr(CallBrInstruction cb, int instId)
    {
        // [paramattrs, callFlags, defaultBB, num_indirect, indirect_bb..., FTy, callee+ty, args...]
        ulong flags = ((ulong)(cb.CallingConv & 0x1FFF));
        var ops = new List<ulong>(8 + cb.IndirectDests.Count + cb.Arguments.Count * 2);
        ops.Add(0UL);
        ops.Add(flags);
        ops.Add((ulong)BbId(cb.DefaultDest));
        ops.Add((ulong)cb.IndirectDests.Count);
        foreach (var d in cb.IndirectDests) ops.Add((ulong)BbId(d));
        ops.Add((ulong)cb.FunctionType.Id);
        PushValueAndType(cb.Callee, instId, ops);
        foreach (var a in cb.Arguments)
            PushValueAndType(a, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCallBr, ops.ToArray());
    }

    private void WriteCatchSwitch(CatchSwitchInstruction cs, int instId)
    {
        // [parent, num_handlers, handlers..., (unwind_dest|unwind_to_caller)]
        var ops = new List<ulong>(3 + cs.Handlers.Count);
        if (cs.ParentPad is null) ops.Add(0UL);
        else PushValue(cs.ParentPad, instId, ops);
        ops.Add((ulong)cs.Handlers.Count);
        foreach (var h in cs.Handlers) ops.Add((ulong)BbId(h));
        if (cs.UnwindDest is not null) ops.Add((ulong)BbId(cs.UnwindDest));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCatchSwitch, ops.ToArray());
    }

    private void WriteCatchPad(CatchPadInstruction cp, int instId)
    {
        var ops = new List<ulong>(2 + cp.Args.Count * 2);
        PushValue(cp.CatchSwitch, instId, ops);
        ops.Add((ulong)cp.Args.Count);
        foreach (var a in cp.Args) PushValueAndType(a, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCatchPad, ops.ToArray());
    }

    private void WriteCleanupPad(CleanupPadInstruction clp, int instId)
    {
        // [num_args, parent_or_none, args...]
        var ops = new List<ulong>(2 + clp.Args.Count * 2);
        ops.Add((ulong)clp.Args.Count);
        if (clp.ParentPad is null) ops.Add(0UL);
        else PushValue(clp.ParentPad, instId, ops);
        foreach (var a in clp.Args) PushValueAndType(a, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCleanupPad, ops.ToArray());
    }

    private void WriteCatchRet(CatchRetInstruction cr, int instId)
    {
        var ops = new List<ulong>(2);
        PushValue(cr.CatchPad, instId, ops);
        ops.Add((ulong)BbId(cr.SuccessorBlock));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCatchRet, ops.ToArray());
    }

    private void WriteCleanupRet(CleanupRetInstruction clr, int instId)
    {
        var ops = new List<ulong>(2);
        PushValue(clr.CleanupPad, instId, ops);
        if (clr.UnwindDest is not null) ops.Add((ulong)BbId(clr.UnwindDest));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCleanupRet, ops.ToArray());
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

    private void WriteBr(BranchInstruction br, int instId)
    {
        if (!br.IsConditional)
        {
            _w.WriteUnabbrevRecord(FunctionCodes.InstBr, (ulong)BbId(br.TrueTarget));
            return;
        }
        var ops = new List<ulong>(3);
        ops.Add((ulong)BbId(br.TrueTarget));
        ops.Add((ulong)BbId(br.FalseTarget!));
        PushValue(br.Condition!, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstBr, ops.ToArray());
    }

    private void WriteIndirectBr(IndirectBrInstruction ibr, int instId)
    {
        // [ptrTypeId, addrRel, bb1Idx, bb2Idx, ...]
        var ops = new List<ulong>(2 + ibr.PossibleTargets.Count);
        ops.Add((ulong)ibr.Address.Type.Id);
        PushValue(ibr.Address, instId, ops);
        foreach (var bb in ibr.PossibleTargets) ops.Add((ulong)BbId(bb));
        _w.WriteUnabbrevRecord(FunctionCodes.InstIndirectBr, ops.ToArray());
    }

    private void WriteSwitch(SwitchInstruction sw, int instId)
    {
        var ops = new List<ulong>(4 + sw.Cases.Count * 2);
        ops.Add((ulong)sw.Condition.Type.Id);
        PushValue(sw.Condition, instId, ops);
        ops.Add((ulong)BbId(sw.DefaultDest));
        foreach (var (cv, dest) in sw.Cases)
        {
            ops.Add((ulong)_ve.GetValueId(cv));
            ops.Add((ulong)BbId(dest));
        }
        _w.WriteUnabbrevRecord(FunctionCodes.InstSwitch, ops.ToArray());
    }

    private void WriteAlloca(AllocaInstruction a)
    {
        // [instty, opty, op, alignRecord]
        // alignRecord layout (AllocaPackedValues):
        //   bits 0..4  AlignLower(5)
        //   bit  5     UsedWithInAlloca
        //   bit  6     ExplicitType    (always 1 — opaque pointers)
        //   bit  7     SwiftError
        //   bits 8..   AlignUpper
        uint encodedAlign = a.Alignment == 0 ? 0u : (uint)(Log2(a.Alignment) + 1);
        uint record = (encodedAlign & 0x1Fu)
                      | (a.IsInAlloca ? (1u << 5) : 0u)
                      | (1u << 6)
                      | (a.IsSwiftError ? (1u << 7) : 0u)
                      | ((encodedAlign >> 5) << 8);

        // ALLOCA's size operand is encoded as an *absolute* valueID
        // (LLVM 15 writer: VE.getValueID(I.getOperand(0)); reader:
        // getFnValueByID(Record[2], OpTy, ...)). Do NOT switch this
        // to relative encoding — both reader and writer use absolute.
        var ops = new ulong[]
        {
            (ulong)a.AllocatedType.Id,
            (ulong)a.NumElements.Type.Id,
            (ulong)_ve.GetValueId(a.NumElements),
            record,
        };
        _w.WriteUnabbrevRecord(FunctionCodes.InstAlloca, ops);
    }

    private void WriteLoad(LoadInstruction l, int instId)
    {
        // Fast path: non-atomic, backward-ref pointer → 4-op load abbrev.
        int ptrId = _ve.GetValueId(l.Pointer);
        if (!l.IsAtomic && ptrId < instId && EncodedAlign(l.Alignment) < (1u << 7))
        {
            _w.WriteAbbrevRecord(_loadAbbrev,
                (ulong)(uint)(instId - ptrId),
                (ulong)l.Type.Id,
                EncodedAlign(l.Alignment),
                l.IsVolatile ? 1UL : 0UL);
            return;
        }

        var ops = new List<ulong>(8);
        PushValueAndType(l.Pointer, instId, ops);
        ops.Add((ulong)l.Type.Id);
        ops.Add((ulong)EncodedAlign(l.Alignment));
        ops.Add(l.IsVolatile ? 1UL : 0UL);
        if (l.IsAtomic)
        {
            ops.Add(l.Ordering);
            ops.Add(l.SyncScope);
            _w.WriteUnabbrevRecord(FunctionCodes.InstLoadAtomic, ops.ToArray());
        }
        else
        {
            _w.WriteUnabbrevRecord(FunctionCodes.InstLoad, ops.ToArray());
        }
    }

    private void WriteStore(StoreInstruction s, int instId)
    {
        // Fast path: non-atomic, both backward-ref → 4-op store abbrev.
        int ptrId = _ve.GetValueId(s.Pointer);
        int valId = _ve.GetValueId(s.StoredValue);
        if (!s.IsAtomic && ptrId < instId && valId < instId && EncodedAlign(s.Alignment) < (1u << 7))
        {
            _w.WriteAbbrevRecord(_storeAbbrev,
                (ulong)(uint)(instId - ptrId),
                (ulong)(uint)(instId - valId),
                EncodedAlign(s.Alignment),
                s.IsVolatile ? 1UL : 0UL);
            return;
        }

        var ops = new List<ulong>(8);
        PushValueAndType(s.Pointer, instId, ops);
        PushValueAndType(s.StoredValue, instId, ops);
        ops.Add((ulong)EncodedAlign(s.Alignment));
        ops.Add(s.IsVolatile ? 1UL : 0UL);
        if (s.IsAtomic)
        {
            ops.Add(s.Ordering);
            ops.Add(s.SyncScope);
            _w.WriteUnabbrevRecord(FunctionCodes.InstStoreAtomic, ops.ToArray());
        }
        else
        {
            _w.WriteUnabbrevRecord(FunctionCodes.InstStore, ops.ToArray());
        }
    }

    private void WriteCast(CastInstruction c, int instId)
    {
        // Fast path: backward-ref operand & opcode fits in 4 bits → 3-op cast abbrev.
        int opId = _ve.GetValueId(c.Operand);
        if (opId < instId && c.Opcode < (1u << 4))
        {
            _w.WriteAbbrevRecord(_castAbbrev,
                (ulong)(uint)(instId - opId),
                (ulong)c.Type.Id,
                c.Opcode);
            return;
        }

        var ops = new List<ulong>(4);
        PushValueAndType(c.Operand, instId, ops);
        ops.Add((ulong)c.Type.Id);
        ops.Add(c.Opcode);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCast, ops.ToArray());
    }

    private void WriteCmp(CompareInstruction cmp, int instId)
    {
        // Fast path: backward-ref operands and no FMF → 3-op cmp abbrev.
        int leftId = _ve.GetValueId(cmp.Left);
        int rightId = _ve.GetValueId(cmp.Right);
        bool noFmf = cmp.Fmf == FastMathFlags.None || !IsFloatLike(cmp.Left.Type);
        if (leftId < instId && rightId < instId && noFmf)
        {
            _w.WriteAbbrevRecord(_cmpAbbrev,
                (ulong)(uint)(instId - leftId),
                (ulong)(uint)(instId - rightId),
                cmp.Predicate);
            return;
        }

        var ops = new List<ulong>(5);
        PushValueAndType(cmp.Left, instId, ops);
        PushValue(cmp.Right, instId, ops);
        ops.Add(cmp.Predicate);
        // FCMP can carry FMF flags; emit only when non-zero (matches LLVM).
        if (cmp.Fmf != FastMathFlags.None && IsFloatLike(cmp.Left.Type))
            ops.Add((ulong)cmp.Fmf);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCmp2, ops.ToArray());
    }

    private void WriteGep(GetElementPtrInstruction g, int instId)
    {
        var ops = new List<ulong>(4 + g.Indices.Count * 2);
        ops.Add(g.IsInBounds ? 1UL : 0UL);
        ops.Add((ulong)g.SourceElementType.Id);
        PushValueAndType(g.Pointer, instId, ops);
        foreach (var idx in g.Indices)
            PushValueAndType(idx, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstGep, ops.ToArray());
    }

    private void WritePhi(PhiInstruction p, int instId)
    {
        var ops = new List<ulong>(1 + p.Incomings.Count * 2);
        ops.Add((ulong)p.Type.Id);
        foreach (var (v, bb) in p.Incomings)
        {
            int valId = _ve.GetValueId(v);
            int diff = instId - valId;
            ops.Add(SignRotate(diff));
            ops.Add((ulong)BbId(bb));
        }
        _w.WriteUnabbrevRecord(FunctionCodes.InstPhi, ops.ToArray());
    }

    private void WriteSelect(SelectInstruction sel, int instId)
    {
        var ops = new List<ulong>(5);
        if (sel.Condition.Type is VectorType)
        {
            // INST_VSELECT: [opval+ty(true), opval(false), opval+ty(cond)]
            PushValueAndType(sel.TrueValue, instId, ops);
            PushValue(sel.FalseValue, instId, ops);
            PushValueAndType(sel.Condition, instId, ops);
            _w.WriteUnabbrevRecord(FunctionCodes.InstVSelect, ops.ToArray());
        }
        else
        {
            // INST_SELECT: [opval+ty(true), opval(false), opval(cond:i1)]
            PushValueAndType(sel.TrueValue, instId, ops);
            PushValue(sel.FalseValue, instId, ops);
            PushValue(sel.Condition, instId, ops);
            _w.WriteUnabbrevRecord(FunctionCodes.InstSelect, ops.ToArray());
        }
    }

    private void WriteCall(CallInstruction call, int instId)
    {
        WriteOperandBundles(call.Bundles, instId);
        // [paramattrs, callFlags, FTy, callee+ty, args..., (FMF if Fmf bit set)]
        ulong flags = (1UL << CallFlags.ExplicitType)
                      | ((ulong)(call.CallingConv & 0x7FF) << CallFlags.Cconv)
                      | (call.IsTailCall ? 1UL << CallFlags.Tail : 0UL)
                      | (call.IsMustTail ? 1UL << CallFlags.MustTail : 0UL)
                      | (call.Fmf != FastMathFlags.None ? 1UL << CallFlags.Fmf : 0UL);

        int fixedParamCount = call.FunctionType.ParameterTypes.Count;
        var ops = new List<ulong>(4 + 2 + fixedParamCount + (call.Arguments.Count - fixedParamCount) * 2 + 1);
        ops.Add(GetCallAttrId(call));
        ops.Add(flags);
        ops.Add((ulong)call.FunctionType.Id);
        PushValueAndType(call.Callee, instId, ops);

        // LLVM 15 CALL writer (BitcodeWriter.cpp::writeInstruction case
        // Instruction::Call) emits *fixed* parameters via pushValue() (1
        // op) and varargs via pushValueAndType() (1 or 2 ops). The
        // reader's INST_CALL handler mirrors this with getValue() for
        // fixed args and a strict `if (Record.size() != OpNum)` check
        // after the loop, so any extra op produced by writing a
        // forward-ref fixed arg as a type/value pair surfaces as the
        // generic "Invalid record" error during disassembly. Match the
        // spec exactly so forward-ref-into-call sites round-trip.
        for (int i = 0; i < fixedParamCount && i < call.Arguments.Count; i++)
            PushValue(call.Arguments[i], instId, ops);
        if (call.FunctionType.IsVarArg)
        {
            for (int i = fixedParamCount; i < call.Arguments.Count; i++)
                PushValueAndType(call.Arguments[i], instId, ops);
        }

        if (call.Fmf != FastMathFlags.None) ops.Add((ulong)call.Fmf);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCall, ops.ToArray());
    }

    private void WriteExtractElement(ExtractElementInstruction ee, int instId)
    {
        var ops = new List<ulong>(4);
        PushValueAndType(ee.Vector, instId, ops);
        PushValueAndType(ee.Index, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstExtractElt, ops.ToArray());
    }

    private void WriteInsertElement(InsertElementInstruction ie, int instId)
    {
        var ops = new List<ulong>(5);
        PushValueAndType(ie.Vector, instId, ops);
        PushValue(ie.Element, instId, ops);
        PushValueAndType(ie.Index, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstInsertElt, ops.ToArray());
    }

    private void WriteShuffleVector(ShuffleVectorInstruction sv, int instId)
    {
        var ops = new List<ulong>(5);
        PushValueAndType(sv.Vector1, instId, ops);
        PushValue(sv.Vector2, instId, ops);
        PushValueAndType(sv.Mask, instId, ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstShuffleVec, ops.ToArray());
    }

    private void WriteFence(FenceInstruction f)
    {
        _w.WriteUnabbrevRecord(FunctionCodes.InstFence, f.Ordering, f.SyncScope);
    }

    private void WriteAtomicRmw(AtomicRmwInstruction arw, int instId)
    {
        // [ptr+ty, val, op, vol, ordering, synchscope, align]
        var ops = new List<ulong>(8);
        PushValueAndType(arw.Pointer, instId, ops);
        PushValueAndType(arw.Value, instId, ops);
        ops.Add(arw.Operation);
        ops.Add(arw.IsVolatile ? 1UL : 0UL);
        ops.Add(arw.Ordering);
        ops.Add(arw.SyncScope);
        ops.Add(EncodedAlign(arw.Alignment));
        _w.WriteUnabbrevRecord(FunctionCodes.InstAtomicRmw, ops.ToArray());
    }

    private void WriteCmpXchg(CmpXchgInstruction cx, int instId)
    {
        // [ptr+ty, cmp+ty, new, vol, success_ordering, synchscope, failure_ordering, weak, align]
        var ops = new List<ulong>(10);
        PushValueAndType(cx.Pointer, instId, ops);
        PushValueAndType(cx.Compare, instId, ops);
        PushValue(cx.New, instId, ops);
        ops.Add(cx.IsVolatile ? 1UL : 0UL);
        ops.Add(cx.SuccessOrdering);
        ops.Add(cx.SyncScope);
        ops.Add(cx.FailureOrdering);
        ops.Add(cx.IsWeak ? 1UL : 0UL);
        ops.Add(EncodedAlign(cx.Alignment));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCmpXchg, ops.ToArray());
    }

    private int BbId(BasicBlock bb)
    {
        if (!_bbIds.TryGetValue(bb, out int id))
            throw new InvalidOperationException("basic block reference is not part of the current function");
        return id;
    }

    /// <summary>Walks every instruction operand of <paramref name="fn"/> looking for
    /// MetadataAsValue wrappers around function-local metadata (MdValue/DiArgList that
    /// reference an Argument or Instruction of this function). Assigns IDs to them
    /// starting at <see cref="MetadataEnumerator.ModuleMetadataCount"/> in topological
    /// order (MdValues before any DiArgList that references them) and emits the
    /// function-local METADATA_BLOCK via the shared <see cref="MetadataWriter"/>.</summary>
    private void EmitFunctionLocalMetadata(Function fn)
    {
        if (_me is null || _mw is null) return;

        var ordered = new List<Metadata>();
        var seen = new HashSet<Metadata>(ReferenceComparer<Metadata>.Instance);

        void Visit(Metadata? md)
        {
            if (md is null || !MetadataEnumerator.IsFunctionLocal(md)) return;
            if (!seen.Add(md)) return;
            if (md is DiArgList al)
                foreach (var arg in al.Args) Visit(arg);
            md.Id = _me.ModuleMetadataCount + ordered.Count;
            ordered.Add(md);
        }

        foreach (var bb in fn.BasicBlocks)
            foreach (var inst in bb.Instructions)
                foreach (var op in inst.Operands)
                    if (op is MetadataAsValue mav) Visit(mav.Metadata);

        _mw.WriteFunctionLocalMetadataBlock(ordered);
    }

    private void PushValueAndType(Value v, int instId, List<ulong> ops)
    {
        // MetadataAsValue routes through the metadata table — only the 1-based
        // metadata id is pushed; the reader uses the surrounding signature to
        // recognize the operand as metadata (no type id).
        if (v is MetadataAsValue mav)
        {
            if (_me is null)
                throw new InvalidOperationException("MetadataAsValue requires a metadata enumerator");
            ops.Add((ulong)_me.GetIdRequired(mav.Metadata));
            return;
        }

        int valId = _ve.GetValueId(v);
        ops.Add((ulong)(uint)(instId - valId));
        if (valId >= instId)
            ops.Add((ulong)v.Type.Id);
    }

    private void PushValue(Value v, int instId, List<ulong> ops)
    {
        if (v is MetadataAsValue mav)
        {
            if (_me is null)
                throw new InvalidOperationException("MetadataAsValue requires a metadata enumerator");
            ops.Add((ulong)_me.GetIdRequired(mav.Metadata));
            return;
        }

        int valId = _ve.GetValueId(v);
        ops.Add((ulong)(uint)(instId - valId));
    }

    private static uint EncodedAlign(uint align) => align == 0 ? 0u : (uint)(Log2(align) + 1);

    private static int Log2(uint v)
    {
        int r = 0;
        while ((v >>= 1) != 0) r++;
        return r;
    }

    private static ulong SignRotate(int v) =>
        v < 0 ? (((ulong)(uint)(-v)) << 1) | 1UL : ((ulong)(uint)v) << 1;
}

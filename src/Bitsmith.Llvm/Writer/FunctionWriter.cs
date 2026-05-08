using System;
using System.Collections.Generic;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

internal sealed class FunctionWriter
{
    private const int FunctionAbbrevWidth = 4;
    private const int StackOpsBudget = 64;

    private readonly BitstreamWriter _w;
    private readonly ValueEnumerator _ve;
    private readonly TypeContext _types;
    private readonly IReadOnlyDictionary<Instruction, uint>? _callAttrIds;
    private readonly IReadOnlyDictionary<string, uint>? _bundleTagIds;
    private readonly MetadataEnumerator? _me;
    private readonly MetadataWriter? _mw;
    private Dictionary<BasicBlock, int> _bbIds = new();

    private ref struct OpsWriter
    {
        private readonly Span<ulong> _buf;
        public int Count;
        public OpsWriter(Span<ulong> buf) { _buf = buf; Count = 0; }
        public void Add(ulong v) => _buf[Count++] = v;
        public ReadOnlySpan<ulong> Span => _buf.Slice(0, Count);
    }

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
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        foreach (var b in bundles)
        {
            int n = 1 + b.Inputs.Count * 2;
            Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
            var ops = new OpsWriter(buf);
            ops.Add(_bundleTagIds[b.Tag]);
            for (int i = 0; i < b.Inputs.Count; i++)
                PushValueAndType(b.Inputs[i], instId, ref ops);
            _w.WriteUnabbrevRecord(FunctionCodes.OperandBundle, ops.Span);
        }
    }

    public void Write(Function fn)
    {
        _w.EnterSubBlock(BlockIds.Function, FunctionAbbrevWidth);
        _ve.IncorporateFunction(fn);

        try
        {
            _declareBlocksAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(FunctionCodes.DeclareBlocks), AbbrevOp.Vbr(6));
            _retVoidAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(FunctionCodes.InstRet));
            _retValAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(FunctionCodes.InstRet), AbbrevOp.Vbr(6));
            _binOpAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstBinOp),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Vbr(4));
            _loadAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstLoad),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Fixed(7), AbbrevOp.Fixed(1));
            _storeAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstStore),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Fixed(7), AbbrevOp.Fixed(1));
            _cmpAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstCmp2),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Vbr(6));
            _castAbbrev = _w.DefineAbbrev(
                AbbrevOp.Literal(FunctionCodes.InstCast),
                AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Fixed(4));

            _w.WriteAbbrevRecord(_declareBlocksAbbrev, (ulong)fn.BasicBlocks.Count);

            _bbIds = new Dictionary<BasicBlock, int>(ReferenceComparer<BasicBlock>.Instance);
            for (int i = 0; i < fn.BasicBlocks.Count; i++)
                _bbIds[fn.BasicBlocks[i]] = i;

            ConstantWriter.WriteModuleConstants(_w, _ve.FunctionConstants, _ve.GetValueId);

            EmitFunctionLocalMetadata(fn);

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
    /// or named (non-void) instructions.
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
            EmitNameRecord(2 /* VST_CODE_BBENTRY */, (ulong)bi, bb.Name!);
        }

        foreach (var bb in fn.BasicBlocks)
        {
            foreach (var inst in bb.Instructions)
            {
                if (string.IsNullOrEmpty(inst.Name) || inst.Type is VoidType) continue;
                int valId = _ve.GetValueId(inst);
                EmitNameRecord(1 /* VST_CODE_ENTRY */, (ulong)valId, inst.Name!);
            }
        }

        _w.ExitBlock();
    }

    private void EmitNameRecord(uint code, ulong idOp, string name)
    {
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> bytesStack = stackalloc byte[256];
        Span<byte> bytes = byteCount <= 256 ? bytesStack : new byte[byteCount];
        int written = System.Text.Encoding.UTF8.GetBytes(name, bytes);
        Span<ulong> opsStack = stackalloc ulong[StackOpsBudget];
        int n = 1 + written;
        Span<ulong> ops = n <= StackOpsBudget ? opsStack.Slice(0, n) : new ulong[n];
        ops[0] = idOp;
        for (int i = 0; i < written; i++) ops[1 + i] = bytes[i];
        _w.WriteUnabbrevRecord(code, ops);
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
        Span<ulong> ops = stackalloc ulong[5];
        ops[0] = loc.Line;
        ops[1] = loc.Column;
        ops[2] = (ulong)_me.GetIdRequired(loc.Scope);
        ops[3] = (ulong)_me.GetIdOrNull(loc.InlinedAt);
        ops[4] = loc.IsImplicitCode ? 1UL : 0UL;
        _w.WriteUnabbrevRecord(DebugLocCodes.DebugLoc, ops);
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
        if (fn.Subprogram is not null)
        {
            Span<ulong> ops = stackalloc ulong[2];
            ops[0] = MetadataWriter.DbgKind;
            ops[1] = (ulong)fn.Subprogram.Id;
            _w.WriteUnabbrevRecord(MetadataCodes.Attachment, ops);
        }

        // Per-instruction attachments (non-dbg). instIndex is a flat 0-based
        // index over all instructions in BB iteration order; matches
        // BitcodeWriter::writeMetadataAttachment in LLVM. Record layout:
        //   [instIndex, kind_id, md_id, kind_id, md_id, ...]
        Span<ulong> opsStack = stackalloc ulong[StackOpsBudget];
        int instIndex = 0;
        foreach (var bb in fn.BasicBlocks)
        {
            foreach (var inst in bb.Instructions)
            {
                var atts = inst.Attachments;
                if (atts is { Count: > 0 } && _mw is not null)
                {
                    int n = 1 + atts.Count * 2;
                    Span<ulong> ops = n <= StackOpsBudget ? opsStack.Slice(0, n) : new ulong[n];
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
        Span<ulong> buf = stackalloc ulong[4];
        var ops = new OpsWriter(buf);
        PushValueAndType(u.Operand, instId, ref ops);
        ops.Add((ulong)u.Opcode);
        if (u.Fmf != FastMathFlags.None) ops.Add((ulong)u.Fmf);
        _w.WriteUnabbrevRecord(FunctionCodes.InstUnOp, ops.Span);
    }

    private void WriteFreeze(FreezeInstruction fz, int instId)
    {
        Span<ulong> buf = stackalloc ulong[2];
        var ops = new OpsWriter(buf);
        PushValueAndType(fz.Operand, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstFreeze, ops.Span);
    }

    private void WriteVaArg(VaArgInstruction va, int instId)
    {
        Span<ulong> ops = stackalloc ulong[3];
        ops[0] = (ulong)va.ListType.Id;
        ops[1] = (ulong)(uint)(instId - _ve.GetValueId(va.ValistType));
        ops[2] = (ulong)va.Type.Id;
        _w.WriteUnabbrevRecord(FunctionCodes.InstVaArg, ops);
    }

    private void WriteExtractValue(ExtractValueInstruction ev, int instId)
    {
        int n = 2 + ev.Indices.Count;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        PushValueAndType(ev.Aggregate, instId, ref ops);
        for (int i = 0; i < ev.Indices.Count; i++) ops.Add(ev.Indices[i]);
        _w.WriteUnabbrevRecord(FunctionCodes.InstExtractVal, ops.Span);
    }

    private void WriteInsertValue(InsertValueInstruction iv, int instId)
    {
        int n = 4 + iv.Indices.Count;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        PushValueAndType(iv.Aggregate, instId, ref ops);
        PushValueAndType(iv.Element, instId, ref ops);
        for (int i = 0; i < iv.Indices.Count; i++) ops.Add(iv.Indices[i]);
        _w.WriteUnabbrevRecord(FunctionCodes.InstInsertVal, ops.Span);
    }

    private void WriteInvoke(InvokeInstruction inv, int instId)
    {
        WriteOperandBundles(inv.Bundles, instId);
        ulong flags = ((ulong)(inv.CallingConv & 0x1FFF))
                      | (1UL << 13);  // ExplicitType bit (LLVM 15 invoke encoding)
        int fixedParamCount = inv.FunctionType.ParameterTypes.Count;
        // Worst-case sizing: callee may need 2 ops (forward ref + type),
        // each fixed arg always 1 op (LLVM 15 spec — see comment below),
        // each vararg 2 ops.
        int n = 6 + 2 + fixedParamCount + (inv.Arguments.Count - fixedParamCount) * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add(GetCallAttrId(inv));
        ops.Add(flags);
        ops.Add((ulong)BbId(inv.NormalDest));
        ops.Add((ulong)BbId(inv.UnwindDest));
        ops.Add((ulong)inv.FunctionType.Id);
        PushValueAndType(inv.Callee, instId, ref ops);

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
            PushValue(inv.Arguments[i], instId, ref ops);
        if (inv.FunctionType.IsVarArg)
        {
            for (int i = fixedParamCount; i < inv.Arguments.Count; i++)
                PushValueAndType(inv.Arguments[i], instId, ref ops);
        }

        _w.WriteUnabbrevRecord(FunctionCodes.InstInvoke, ops.Span);
    }

    private void WriteResume(ResumeInstruction res, int instId)
    {
        Span<ulong> buf = stackalloc ulong[2];
        var ops = new OpsWriter(buf);
        PushValueAndType(res.Value, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstResume, ops.Span);
    }

    private void WriteLandingpad(LandingpadInstruction lp, int instId)
    {
        int n = 4 + lp.Clauses.Count * 3;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add((ulong)lp.Type.Id);
        ops.Add(lp.IsCleanup ? 1UL : 0UL);
        ops.Add((ulong)lp.Clauses.Count);
        for (int i = 0; i < lp.Clauses.Count; i++)
        {
            var c = lp.Clauses[i];
            ops.Add((uint)c.Kind);
            PushValueAndType(c.Operand, instId, ref ops);
        }
        _w.WriteUnabbrevRecord(FunctionCodes.InstLandingpad, ops.Span);
    }

    private void WriteCallBr(CallBrInstruction cb, int instId)
    {
        ulong flags = ((ulong)(cb.CallingConv & 0x1FFF));
        int n = 8 + cb.IndirectDests.Count + cb.Arguments.Count * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add(0UL);
        ops.Add(flags);
        ops.Add((ulong)BbId(cb.DefaultDest));
        ops.Add((ulong)cb.IndirectDests.Count);
        for (int i = 0; i < cb.IndirectDests.Count; i++) ops.Add((ulong)BbId(cb.IndirectDests[i]));
        ops.Add((ulong)cb.FunctionType.Id);
        PushValueAndType(cb.Callee, instId, ref ops);
        for (int i = 0; i < cb.Arguments.Count; i++)
            PushValueAndType(cb.Arguments[i], instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCallBr, ops.Span);
    }

    private void WriteCatchSwitch(CatchSwitchInstruction cs, int instId)
    {
        int n = 3 + cs.Handlers.Count;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        if (cs.ParentPad is null) ops.Add(0UL);
        else PushValue(cs.ParentPad, instId, ref ops);
        ops.Add((ulong)cs.Handlers.Count);
        for (int i = 0; i < cs.Handlers.Count; i++) ops.Add((ulong)BbId(cs.Handlers[i]));
        if (cs.UnwindDest is not null) ops.Add((ulong)BbId(cs.UnwindDest));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCatchSwitch, ops.Span);
    }

    private void WriteCatchPad(CatchPadInstruction cp, int instId)
    {
        int n = 2 + cp.Args.Count * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        PushValue(cp.CatchSwitch, instId, ref ops);
        ops.Add((ulong)cp.Args.Count);
        for (int i = 0; i < cp.Args.Count; i++) PushValueAndType(cp.Args[i], instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCatchPad, ops.Span);
    }

    private void WriteCleanupPad(CleanupPadInstruction clp, int instId)
    {
        int n = 2 + clp.Args.Count * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add((ulong)clp.Args.Count);
        if (clp.ParentPad is null) ops.Add(0UL);
        else PushValue(clp.ParentPad, instId, ref ops);
        for (int i = 0; i < clp.Args.Count; i++) PushValueAndType(clp.Args[i], instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCleanupPad, ops.Span);
    }

    private void WriteCatchRet(CatchRetInstruction cr, int instId)
    {
        Span<ulong> buf = stackalloc ulong[2];
        var ops = new OpsWriter(buf);
        PushValue(cr.CatchPad, instId, ref ops);
        ops.Add((ulong)BbId(cr.SuccessorBlock));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCatchRet, ops.Span);
    }

    private void WriteCleanupRet(CleanupRetInstruction clr, int instId)
    {
        Span<ulong> buf = stackalloc ulong[2];
        var ops = new OpsWriter(buf);
        PushValue(clr.CleanupPad, instId, ref ops);
        if (clr.UnwindDest is not null) ops.Add((ulong)BbId(clr.UnwindDest));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCleanupRet, ops.Span);
    }

    private void WriteBinaryOp(BinaryOperator b, int instId)
    {
        ulong flags = EncodeBinaryOpFlags(b);
        int leftId = _ve.GetValueId(b.Left);
        bool leftForward = leftId >= instId;

        if (!leftForward && flags == 0)
        {
            Span<ulong> abbrev = stackalloc ulong[3];
            abbrev[0] = (ulong)(uint)(instId - leftId);
            abbrev[1] = (ulong)(uint)(instId - _ve.GetValueId(b.Right));
            abbrev[2] = (ulong)b.Opcode;
            _w.WriteAbbrevRecord(_binOpAbbrev, abbrev);
            return;
        }

        Span<ulong> buf = stackalloc ulong[5];
        var ops = new OpsWriter(buf);
        PushValueAndType(b.Left, instId, ref ops);
        PushValue(b.Right, instId, ref ops);
        ops.Add((ulong)b.Opcode);
        if (flags != 0) ops.Add(flags);
        _w.WriteUnabbrevRecord(FunctionCodes.InstBinOp, ops.Span);
    }

    /// <summary>
    /// Reproduces <c>BitcodeWriter::getOptimizationFlags</c>.
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
            _w.WriteAbbrevRecord(_retValAbbrev, (ulong)(uint)(instId - valId));
            return;
        }
        Span<ulong> buf = stackalloc ulong[2];
        var ops = new OpsWriter(buf);
        PushValueAndType(r.ReturnValue, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstRet, ops.Span);
    }

    private void WriteBr(BranchInstruction br, int instId)
    {
        if (!br.IsConditional)
        {
            _w.WriteUnabbrevRecord(FunctionCodes.InstBr, (ulong)BbId(br.TrueTarget));
            return;
        }
        Span<ulong> buf = stackalloc ulong[3];
        var ops = new OpsWriter(buf);
        ops.Add((ulong)BbId(br.TrueTarget));
        ops.Add((ulong)BbId(br.FalseTarget!));
        PushValue(br.Condition!, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstBr, ops.Span);
    }

    private void WriteIndirectBr(IndirectBrInstruction ibr, int instId)
    {
        int n = 2 + ibr.PossibleTargets.Count;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add((ulong)ibr.Address.Type.Id);
        PushValue(ibr.Address, instId, ref ops);
        for (int i = 0; i < ibr.PossibleTargets.Count; i++) ops.Add((ulong)BbId(ibr.PossibleTargets[i]));
        _w.WriteUnabbrevRecord(FunctionCodes.InstIndirectBr, ops.Span);
    }

    private void WriteSwitch(SwitchInstruction sw, int instId)
    {
        int n = 4 + sw.Cases.Count * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add((ulong)sw.Condition.Type.Id);
        PushValue(sw.Condition, instId, ref ops);
        ops.Add((ulong)BbId(sw.DefaultDest));
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            var (cv, dest) = sw.Cases[i];
            ops.Add((ulong)_ve.GetValueId(cv));
            ops.Add((ulong)BbId(dest));
        }
        _w.WriteUnabbrevRecord(FunctionCodes.InstSwitch, ops.Span);
    }

    private void WriteAlloca(AllocaInstruction a)
    {
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
        Span<ulong> ops = stackalloc ulong[4];
        ops[0] = (ulong)a.AllocatedType.Id;
        ops[1] = (ulong)a.NumElements.Type.Id;
        ops[2] = (ulong)_ve.GetValueId(a.NumElements);
        ops[3] = record;
        _w.WriteUnabbrevRecord(FunctionCodes.InstAlloca, ops);
    }

    private void WriteLoad(LoadInstruction l, int instId)
    {
        int ptrId = _ve.GetValueId(l.Pointer);
        if (!l.IsAtomic && ptrId < instId && EncodedAlign(l.Alignment) < (1u << 7))
        {
            Span<ulong> abbrev = stackalloc ulong[4];
            abbrev[0] = (ulong)(uint)(instId - ptrId);
            abbrev[1] = (ulong)l.Type.Id;
            abbrev[2] = EncodedAlign(l.Alignment);
            abbrev[3] = l.IsVolatile ? 1UL : 0UL;
            _w.WriteAbbrevRecord(_loadAbbrev, abbrev);
            return;
        }

        Span<ulong> buf = stackalloc ulong[8];
        var ops = new OpsWriter(buf);
        PushValueAndType(l.Pointer, instId, ref ops);
        ops.Add((ulong)l.Type.Id);
        ops.Add((ulong)EncodedAlign(l.Alignment));
        ops.Add(l.IsVolatile ? 1UL : 0UL);
        if (l.IsAtomic)
        {
            ops.Add(l.Ordering);
            ops.Add(l.SyncScope);
            _w.WriteUnabbrevRecord(FunctionCodes.InstLoadAtomic, ops.Span);
        }
        else
        {
            _w.WriteUnabbrevRecord(FunctionCodes.InstLoad, ops.Span);
        }
    }

    private void WriteStore(StoreInstruction s, int instId)
    {
        int ptrId = _ve.GetValueId(s.Pointer);
        int valId = _ve.GetValueId(s.StoredValue);
        if (!s.IsAtomic && ptrId < instId && valId < instId && EncodedAlign(s.Alignment) < (1u << 7))
        {
            Span<ulong> abbrev = stackalloc ulong[4];
            abbrev[0] = (ulong)(uint)(instId - ptrId);
            abbrev[1] = (ulong)(uint)(instId - valId);
            abbrev[2] = EncodedAlign(s.Alignment);
            abbrev[3] = s.IsVolatile ? 1UL : 0UL;
            _w.WriteAbbrevRecord(_storeAbbrev, abbrev);
            return;
        }

        Span<ulong> buf = stackalloc ulong[8];
        var ops = new OpsWriter(buf);
        PushValueAndType(s.Pointer, instId, ref ops);
        PushValueAndType(s.StoredValue, instId, ref ops);
        ops.Add((ulong)EncodedAlign(s.Alignment));
        ops.Add(s.IsVolatile ? 1UL : 0UL);
        if (s.IsAtomic)
        {
            ops.Add(s.Ordering);
            ops.Add(s.SyncScope);
            _w.WriteUnabbrevRecord(FunctionCodes.InstStoreAtomic, ops.Span);
        }
        else
        {
            _w.WriteUnabbrevRecord(FunctionCodes.InstStore, ops.Span);
        }
    }

    private void WriteCast(CastInstruction c, int instId)
    {
        int opId = _ve.GetValueId(c.Operand);
        if (opId < instId && c.Opcode < (1u << 4))
        {
            Span<ulong> abbrev = stackalloc ulong[3];
            abbrev[0] = (ulong)(uint)(instId - opId);
            abbrev[1] = (ulong)c.Type.Id;
            abbrev[2] = c.Opcode;
            _w.WriteAbbrevRecord(_castAbbrev, abbrev);
            return;
        }

        Span<ulong> buf = stackalloc ulong[4];
        var ops = new OpsWriter(buf);
        PushValueAndType(c.Operand, instId, ref ops);
        ops.Add((ulong)c.Type.Id);
        ops.Add(c.Opcode);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCast, ops.Span);
    }

    private void WriteCmp(CompareInstruction cmp, int instId)
    {
        int leftId = _ve.GetValueId(cmp.Left);
        int rightId = _ve.GetValueId(cmp.Right);
        bool noFmf = cmp.Fmf == FastMathFlags.None || !IsFloatLike(cmp.Left.Type);
        if (leftId < instId && rightId < instId && noFmf)
        {
            Span<ulong> abbrev = stackalloc ulong[3];
            abbrev[0] = (ulong)(uint)(instId - leftId);
            abbrev[1] = (ulong)(uint)(instId - rightId);
            abbrev[2] = cmp.Predicate;
            _w.WriteAbbrevRecord(_cmpAbbrev, abbrev);
            return;
        }

        Span<ulong> buf = stackalloc ulong[5];
        var ops = new OpsWriter(buf);
        PushValueAndType(cmp.Left, instId, ref ops);
        PushValue(cmp.Right, instId, ref ops);
        ops.Add(cmp.Predicate);
        if (cmp.Fmf != FastMathFlags.None && IsFloatLike(cmp.Left.Type))
            ops.Add((ulong)cmp.Fmf);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCmp2, ops.Span);
    }

    private void WriteGep(GetElementPtrInstruction g, int instId)
    {
        int n = 4 + g.Indices.Count * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add(g.IsInBounds ? 1UL : 0UL);
        ops.Add((ulong)g.SourceElementType.Id);
        PushValueAndType(g.Pointer, instId, ref ops);
        for (int i = 0; i < g.Indices.Count; i++)
            PushValueAndType(g.Indices[i], instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstGep, ops.Span);
    }

    private void WritePhi(PhiInstruction p, int instId)
    {
        int n = 1 + p.Incomings.Count * 2;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add((ulong)p.Type.Id);
        for (int i = 0; i < p.Incomings.Count; i++)
        {
            var (v, bb) = p.Incomings[i];
            int valId = _ve.GetValueId(v);
            int diff = instId - valId;
            ops.Add(SignRotate(diff));
            ops.Add((ulong)BbId(bb));
        }
        _w.WriteUnabbrevRecord(FunctionCodes.InstPhi, ops.Span);
    }

    private void WriteSelect(SelectInstruction sel, int instId)
    {
        Span<ulong> buf = stackalloc ulong[5];
        var ops = new OpsWriter(buf);
        if (sel.Condition.Type is VectorType)
        {
            PushValueAndType(sel.TrueValue, instId, ref ops);
            PushValue(sel.FalseValue, instId, ref ops);
            PushValueAndType(sel.Condition, instId, ref ops);
            _w.WriteUnabbrevRecord(FunctionCodes.InstVSelect, ops.Span);
        }
        else
        {
            PushValueAndType(sel.TrueValue, instId, ref ops);
            PushValue(sel.FalseValue, instId, ref ops);
            PushValue(sel.Condition, instId, ref ops);
            _w.WriteUnabbrevRecord(FunctionCodes.InstSelect, ops.Span);
        }
    }

    private void WriteCall(CallInstruction call, int instId)
    {
        WriteOperandBundles(call.Bundles, instId);
        ulong flags = (1UL << CallFlags.ExplicitType)
                      | ((ulong)(call.CallingConv & 0x7FF) << CallFlags.Cconv)
                      | (call.IsTailCall ? 1UL << CallFlags.Tail : 0UL)
                      | (call.IsMustTail ? 1UL << CallFlags.MustTail : 0UL)
                      | (call.Fmf != FastMathFlags.None ? 1UL << CallFlags.Fmf : 0UL);

        int fixedParamCount = call.FunctionType.ParameterTypes.Count;
        int n = 4 + 2 + fixedParamCount + (call.Arguments.Count - fixedParamCount) * 2 + 1;
        Span<ulong> stack = stackalloc ulong[StackOpsBudget];
        Span<ulong> buf = n <= StackOpsBudget ? stack : new ulong[n];
        var ops = new OpsWriter(buf);
        ops.Add(GetCallAttrId(call));
        ops.Add(flags);
        ops.Add((ulong)call.FunctionType.Id);
        PushValueAndType(call.Callee, instId, ref ops);

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
            PushValue(call.Arguments[i], instId, ref ops);
        if (call.FunctionType.IsVarArg)
        {
            for (int i = fixedParamCount; i < call.Arguments.Count; i++)
                PushValueAndType(call.Arguments[i], instId, ref ops);
        }

        if (call.Fmf != FastMathFlags.None) ops.Add((ulong)call.Fmf);
        _w.WriteUnabbrevRecord(FunctionCodes.InstCall, ops.Span);
    }

    private void WriteExtractElement(ExtractElementInstruction ee, int instId)
    {
        Span<ulong> buf = stackalloc ulong[4];
        var ops = new OpsWriter(buf);
        PushValueAndType(ee.Vector, instId, ref ops);
        PushValueAndType(ee.Index, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstExtractElt, ops.Span);
    }

    private void WriteInsertElement(InsertElementInstruction ie, int instId)
    {
        Span<ulong> buf = stackalloc ulong[5];
        var ops = new OpsWriter(buf);
        PushValueAndType(ie.Vector, instId, ref ops);
        PushValue(ie.Element, instId, ref ops);
        PushValueAndType(ie.Index, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstInsertElt, ops.Span);
    }

    private void WriteShuffleVector(ShuffleVectorInstruction sv, int instId)
    {
        Span<ulong> buf = stackalloc ulong[5];
        var ops = new OpsWriter(buf);
        PushValueAndType(sv.Vector1, instId, ref ops);
        PushValue(sv.Vector2, instId, ref ops);
        PushValueAndType(sv.Mask, instId, ref ops);
        _w.WriteUnabbrevRecord(FunctionCodes.InstShuffleVec, ops.Span);
    }

    private void WriteFence(FenceInstruction f)
    {
        _w.WriteUnabbrevRecord(FunctionCodes.InstFence, f.Ordering, f.SyncScope);
    }

    private void WriteAtomicRmw(AtomicRmwInstruction arw, int instId)
    {
        Span<ulong> buf = stackalloc ulong[8];
        var ops = new OpsWriter(buf);
        PushValueAndType(arw.Pointer, instId, ref ops);
        PushValueAndType(arw.Value, instId, ref ops);
        ops.Add(arw.Operation);
        ops.Add(arw.IsVolatile ? 1UL : 0UL);
        ops.Add(arw.Ordering);
        ops.Add(arw.SyncScope);
        ops.Add(EncodedAlign(arw.Alignment));
        _w.WriteUnabbrevRecord(FunctionCodes.InstAtomicRmw, ops.Span);
    }

    private void WriteCmpXchg(CmpXchgInstruction cx, int instId)
    {
        Span<ulong> buf = stackalloc ulong[10];
        var ops = new OpsWriter(buf);
        PushValueAndType(cx.Pointer, instId, ref ops);
        PushValueAndType(cx.Compare, instId, ref ops);
        PushValue(cx.New, instId, ref ops);
        ops.Add(cx.IsVolatile ? 1UL : 0UL);
        ops.Add(cx.SuccessOrdering);
        ops.Add(cx.SyncScope);
        ops.Add(cx.FailureOrdering);
        ops.Add(cx.IsWeak ? 1UL : 0UL);
        ops.Add(EncodedAlign(cx.Alignment));
        _w.WriteUnabbrevRecord(FunctionCodes.InstCmpXchg, ops.Span);
    }

    private int BbId(BasicBlock bb)
    {
        if (!_bbIds.TryGetValue(bb, out int id))
            throw new InvalidOperationException("basic block reference is not part of the current function");
        return id;
    }

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

    private void PushValueAndType(Value v, int instId, ref OpsWriter ops)
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
        if (valId >= instId)
            ops.Add((ulong)v.Type.Id);
    }

    private void PushValue(Value v, int instId, ref OpsWriter ops)
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

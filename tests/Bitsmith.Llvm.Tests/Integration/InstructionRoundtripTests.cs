using System.IO;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

/// <summary>
/// Round-trips for milestone 6: control flow, memory, cast, cmp, call, GEP, phi,
/// select, vector ops, and atomics. Each test builds a small module, writes it,
/// and asserts that <c>llvm-dis</c> reproduces the expected textual form.
/// </summary>
public class InstructionRoundtripTests
{
    private static Module NewModule(string sourceName) => new()
    {
        SourceFileName = sourceName,
        TargetTriple = "x86_64-unknown-linux-gnu",
        DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
    };

    private static string Disassemble(Module module)
    {
        LlvmTools.Require("llvm-dis");
        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            return File.ReadAllText(llPath);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void ControlFlow_BranchesPhiAndUnreachable()
    {
        var module = NewModule("ctrlflow.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var i1 = t.Int1;
        var voidT = t.Void;

        var fnType = t.GetFunction(i32, new LlvmType[] { i1, i32, i32 });
        var fn = module.CreateFunction("pick", fnType);
        var entry = fn.AppendBlock("entry");
        var thenBb = fn.AppendBlock("then");
        var elseBb = fn.AppendBlock("else");
        var join = fn.AppendBlock("join");

        entry.Append(new BranchInstruction(voidT, fn.Parameters[0], thenBb, elseBb));

        var addThen = thenBb.Append(new BinaryOperator(BinaryOpcode.Add, fn.Parameters[1], fn.Parameters[2]));
        thenBb.Append(new BranchInstruction(voidT, join));

        var subElse = elseBb.Append(new BinaryOperator(BinaryOpcode.Sub, fn.Parameters[1], fn.Parameters[2]));
        elseBb.Append(new BranchInstruction(voidT, join));

        var phi = join.Append(new PhiInstruction(i32));
        phi.AddIncoming(addThen, thenBb).AddIncoming(subElse, elseBb);
        join.Append(new ReturnInstruction(voidT, phi));

        var ll = Disassemble(module);
        Assert.Contains("br i1", ll);
        Assert.Contains("br label", ll);
        Assert.Contains("phi i32", ll);
    }

    [SkippableFact]
    public void Switch_AndUnreachable()
    {
        var module = NewModule("switch.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        var fnType = t.GetFunction(i32, new LlvmType[] { i32 });
        var fn = module.CreateFunction("classify", fnType);
        var entry = fn.AppendBlock("entry");
        var caseA = fn.AppendBlock("a");
        var caseB = fn.AppendBlock("b");
        var def = fn.AppendBlock("default");

        var sw = new SwitchInstruction(voidT, fn.Parameters[0], def);
        sw.AddCase(new IntegerConstant(i32, 1), caseA);
        sw.AddCase(new IntegerConstant(i32, 2), caseB);
        entry.Append(sw);

        caseA.Append(new ReturnInstruction(voidT, new IntegerConstant(i32, 10)));
        caseB.Append(new ReturnInstruction(voidT, new IntegerConstant(i32, 20)));
        def.Append(new UnreachableInstruction(voidT));

        var ll = Disassemble(module);
        Assert.Contains("switch i32", ll);
        Assert.Contains("unreachable", ll);
    }

    [SkippableFact]
    public void Memory_AllocaLoadStore()
    {
        var module = NewModule("memory.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var voidT = t.Void;

        var fnType = t.GetFunction(i32, new LlvmType[] { i32 });
        var fn = module.CreateFunction("identity", fnType);
        var entry = fn.AppendBlock("entry");
        var slot = entry.Append(new AllocaInstruction(i32, new IntegerConstant(i32, 1), ptr, alignment: 4));
        entry.Append(new StoreInstruction(voidT, slot, fn.Parameters[0], alignment: 4));
        var loaded = entry.Append(new LoadInstruction(i32, slot, alignment: 4));
        entry.Append(new ReturnInstruction(voidT, loaded));

        var ll = Disassemble(module);
        Assert.Contains("alloca i32", ll);
        Assert.Contains("store i32", ll);
        Assert.Contains("load i32", ll);
    }

    [SkippableFact]
    public void Cast_Sext()
    {
        var module = NewModule("cast.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var i64 = t.Int64;
        var voidT = t.Void;

        var fn = module.CreateFunction("widen", t.GetFunction(i64, new LlvmType[] { i32 }));
        var entry = fn.AppendBlock("entry");
        var widened = entry.Append(new CastInstruction(CastCodes.SExt, fn.Parameters[0], i64));
        entry.Append(new ReturnInstruction(voidT, widened));

        var ll = Disassemble(module);
        Assert.Contains("sext i32", ll);
        Assert.Contains("to i64", ll);
    }

    [SkippableFact]
    public void Compare_IcmpAndSelect()
    {
        var module = NewModule("cmp.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var i1 = t.Int1;
        var voidT = t.Void;

        var fn = module.CreateFunction("max", t.GetFunction(i32, new LlvmType[] { i32, i32 }));
        var entry = fn.AppendBlock("entry");
        var cmp = entry.Append(new CompareInstruction(CmpPredicates.IcmpSgt, fn.Parameters[0], fn.Parameters[1], i1));
        var sel = entry.Append(new SelectInstruction(cmp, fn.Parameters[0], fn.Parameters[1]));
        entry.Append(new ReturnInstruction(voidT, sel));

        var ll = Disassemble(module);
        Assert.Contains("icmp sgt", ll);
        Assert.Contains("select i1", ll);
    }

    [SkippableFact]
    public void Gep_InBoundsIntoArray()
    {
        var module = NewModule("gep.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var voidT = t.Void;
        var arr10 = t.GetArray(10, i32);

        var fn = module.CreateFunction("at", t.GetFunction(ptr, new LlvmType[] { ptr, i32 }));
        var entry = fn.AppendBlock("entry");
        var gep = entry.Append(new GetElementPtrInstruction(arr10, fn.Parameters[0],
            new Value[] { new IntegerConstant(i32, 0), fn.Parameters[1] }, ptr) { IsInBounds = true });
        entry.Append(new ReturnInstruction(voidT, gep));

        var ll = Disassemble(module);
        Assert.Contains("getelementptr inbounds", ll);
        Assert.Contains("[10 x i32]", ll);
    }

    [SkippableFact]
    public void Call_DirectFunctionCall()
    {
        var module = NewModule("call.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        var calleeType = t.GetFunction(i32, new LlvmType[] { i32, i32 });
        var callee = module.CreateFunction("inner", calleeType);
        var inEntry = callee.AppendBlock("entry");
        var sum = inEntry.Append(new BinaryOperator(BinaryOpcode.Add, callee.Parameters[0], callee.Parameters[1]));
        inEntry.Append(new ReturnInstruction(voidT, sum));

        var caller = module.CreateFunction("outer", t.GetFunction(i32, new LlvmType[] { i32 }));
        var entry = caller.AppendBlock("entry");
        var call = entry.Append(new CallInstruction(calleeType, callee,
            new Value[] { caller.Parameters[0], new IntegerConstant(i32, 1) }));
        entry.Append(new ReturnInstruction(voidT, call));

        var ll = Disassemble(module);
        Assert.Contains("call i32 @inner", ll);
    }

    [SkippableFact]
    public void Vector_ExtractInsertShuffle()
    {
        var module = NewModule("vector.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var v4i32 = t.GetVector(4, i32);
        var v2i32 = t.GetVector(2, i32);
        var voidT = t.Void;

        var fn = module.CreateFunction("v", t.GetFunction(i32, new LlvmType[] { v4i32 }));
        var entry = fn.AppendBlock("entry");

        // %1 = extractelement <4 x i32> %0, i32 0
        var ee = entry.Append(new ExtractElementInstruction(fn.Parameters[0], new IntegerConstant(i32, 0)));
        // %2 = insertelement <4 x i32> %0, i32 %1, i32 1
        var ie = entry.Append(new InsertElementInstruction(fn.Parameters[0], ee, new IntegerConstant(i32, 1)));
        // %3 = shufflevector <4 x i32> %2, <4 x i32> %0, <2 x i32> <i32 0, i32 1>
        var maskType = t.GetVector(2, i32);
        var mask = new NullConstant(maskType);
        entry.Append(new ShuffleVectorInstruction(ie, fn.Parameters[0], mask, v2i32));
        entry.Append(new ReturnInstruction(voidT, ee));

        var ll = Disassemble(module);
        Assert.Contains("extractelement", ll);
        Assert.Contains("insertelement", ll);
        Assert.Contains("shufflevector", ll);
    }

    [SkippableFact]
    public void Atomic_FenceOnly()
    {
        var module = NewModule("fence.ll");
        var t = module.Types;
        var voidT = t.Void;
        var fn = module.CreateFunction("f", t.GetFunction(voidT, new LlvmType[] { }));
        var entry = fn.AppendBlock("entry");
        entry.Append(new FenceInstruction(voidT, AtomicOrdering.SequentiallyConsistent));
        entry.Append(new ReturnInstruction(voidT));
        var ll = Disassemble(module);
        Assert.Contains("fence", ll);
    }

    [SkippableFact]
    public void Atomic_AtomicRmwOnly()
    {
        var module = NewModule("rmw.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var voidT = t.Void;
        var fn = module.CreateFunction("f", t.GetFunction(i32, new LlvmType[] { ptr, i32 }));
        var entry = fn.AppendBlock("entry");
        var rmw = entry.Append(new AtomicRmwInstruction(
            AtomicRmwOps.Add, fn.Parameters[0], fn.Parameters[1],
            AtomicOrdering.SequentiallyConsistent, i32) { Alignment = 4 });
        entry.Append(new ReturnInstruction(voidT, rmw));
        var ll = Disassemble(module);
        Assert.Contains("atomicrmw add", ll);
    }

    [SkippableFact]
    public void Atomics_FenceAtomicRmwCmpXchg()
    {
        var module = NewModule("atomics.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var i1 = t.Int1;
        var ptr = t.GetPointer();
        var voidT = t.Void;
        var pair = t.GetStruct(new LlvmType[] { i32, i1 });

        var fn = module.CreateFunction("atomic_ops", t.GetFunction(i32, new LlvmType[] { ptr, i32 }));
        var entry = fn.AppendBlock("entry");

        entry.Append(new FenceInstruction(voidT, AtomicOrdering.SequentiallyConsistent));

        var rmw = entry.Append(new AtomicRmwInstruction(
            AtomicRmwOps.Add, fn.Parameters[0], fn.Parameters[1],
            AtomicOrdering.SequentiallyConsistent, i32) { Alignment = 4 });

        entry.Append(new CmpXchgInstruction(fn.Parameters[0], rmw, fn.Parameters[1],
            AtomicOrdering.SequentiallyConsistent,
            AtomicOrdering.SequentiallyConsistent, pair) { Alignment = 4 });

        entry.Append(new ReturnInstruction(voidT, rmw));

        var ll = Disassemble(module);
        Assert.Contains("fence", ll);
        Assert.Contains("atomicrmw add", ll);
        Assert.Contains("cmpxchg", ll);
    }

    [SkippableFact]
    public void FNeg_Freeze_ExtractInsertValue_RoundTrip()
    {
        var module = NewModule("unops.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var f32 = t.Float;
        var sty = t.GetStruct(new LlvmType[] { i32, i32 });

        // float @neg(float %0) { %1 = fneg float %0; ret float %1 }
        var neg = module.CreateFunction("neg", t.GetFunction(f32, new LlvmType[] { f32 }));
        var nbb = neg.AppendBlock("entry");
        var n = nbb.Append(new UnaryOperator(UnaryOpcode.FNeg, neg.Parameters[0]));
        nbb.Append(new ReturnInstruction(t.Void, n));

        // i32 @frz(i32 %0) { %1 = freeze i32 %0; ret i32 %1 }
        var fz = module.CreateFunction("frz", t.GetFunction(i32, new LlvmType[] { i32 }));
        var fbb = fz.AppendBlock("entry");
        var f = fbb.Append(new FreezeInstruction(fz.Parameters[0]));
        fbb.Append(new ReturnInstruction(t.Void, f));

        // i32 @first({i32,i32} %0) { %1 = extractvalue {i32,i32} %0, 0; ret i32 %1 }
        var first = module.CreateFunction("first", t.GetFunction(i32, new LlvmType[] { sty }));
        var fbb2 = first.AppendBlock("entry");
        var ev = fbb2.Append(new ExtractValueInstruction(first.Parameters[0], new uint[] { 0 }, i32));
        fbb2.Append(new ReturnInstruction(t.Void, ev));

        // {i32,i32} @set0({i32,i32} %0, i32 %1) { %2 = insertvalue {i32,i32} %0, i32 %1, 0; ret {i32,i32} %2 }
        var set0 = module.CreateFunction("set0", t.GetFunction(sty, new LlvmType[] { sty, i32 }));
        var sbb = set0.AppendBlock("entry");
        var iv = sbb.Append(new InsertValueInstruction(set0.Parameters[0], set0.Parameters[1], new uint[] { 0 }));
        sbb.Append(new ReturnInstruction(t.Void, iv));

        var ll = Disassemble(module);
        Assert.Contains("fneg", ll);
        Assert.Contains("freeze", ll);
        Assert.Contains("extractvalue", ll);
        Assert.Contains("insertvalue", ll);
    }

    [SkippableFact]
    public void Invoke_Landingpad_Resume_RoundTrip()
    {
        var module = NewModule("eh.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var voidT = t.Void;

        // declare void @__gxx_personality_v0(...) — we just declare a no-arg variadic personality.
        var personalityType = t.GetFunction(i32, System.Array.Empty<LlvmType>(), isVarArg: true);
        var personality = module.CreateFunction("__gxx_personality_v0", personalityType);

        // declare i32 @callee()
        var calleeFt = t.GetFunction(i32, System.Array.Empty<LlvmType>());
        var callee = module.CreateFunction("callee", calleeFt);

        // The {ptr, i32} struct is the standard landingpad result for Itanium EH.
        var lpResultType = t.GetStruct(new LlvmType[] { ptr, i32 });

        // define i32 @caller() personality ptr @__gxx_personality_v0 {
        //   %r = invoke i32 @callee() to label %cont unwind label %lpad
        // cont:
        //   ret i32 %r
        // lpad:
        //   %lp = landingpad {ptr, i32} cleanup
        //   resume {ptr, i32} %lp
        // }
        var caller = module.CreateFunction("caller", t.GetFunction(i32, System.Array.Empty<LlvmType>()));
        caller.Personality = personality;

        var entry = caller.AppendBlock("entry");
        var cont = caller.AppendBlock("cont");
        var lpadBb = caller.AppendBlock("lpad");
        var inv = entry.Append(new InvokeInstruction(calleeFt, callee, System.Array.Empty<Value>(), cont, lpadBb));
        cont.Append(new ReturnInstruction(voidT, inv));
        var lp = lpadBb.Append(new LandingpadInstruction(lpResultType) { IsCleanup = true });
        lpadBb.Append(new ResumeInstruction(voidT, lp));

        var ll = Disassemble(module);
        Assert.Contains("invoke", ll);
        Assert.Contains("landingpad", ll);
        Assert.Contains("cleanup", ll);
        Assert.Contains("resume", ll);
        Assert.Contains("personality", ll);
        Assert.Contains("@__gxx_personality_v0", ll);
    }

    [SkippableFact]
    public void NamedBlocksAndInstructions_AppearInLlvmDis()
    {
        var module = NewModule("named.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var fn = module.CreateFunction("named", t.GetFunction(i32, new LlvmType[] { i32, i32 }));
        var entry = fn.AppendBlock("entry");
        var sum = entry.Append(new BinaryOperator(BinaryOpcode.Add, fn.Parameters[0], fn.Parameters[1]));
        sum.Name = "sum";
        entry.Append(new ReturnInstruction(t.Void, sum));

        var ll = Disassemble(module);
        // The disassembler reproduces local names verbatim when the function-local
        // VALUE_SYMTAB_BLOCK is present.
        Assert.Contains("%sum", ll);
        Assert.Contains("entry:", ll);
    }

    [SkippableFact]
    public void FCmpFastAndCallFmf_RoundTrip()
    {
        var module = NewModule("fmf.ll");
        var t = module.Types;
        var i1 = t.Int1;
        var f32 = t.Float;

        // i1 @flt(float %0, float %1) { %2 = fcmp fast olt float %0, %1; ret i1 %2 }
        var flt = module.CreateFunction("flt", t.GetFunction(i1, new LlvmType[] { f32, f32 }));
        var bb = flt.AppendBlock("entry");
        var cmp = bb.Append(new CompareInstruction(CmpPredicates.FcmpOlt, flt.Parameters[0], flt.Parameters[1], i1)
        { Fmf = FastMathFlags.Fast });
        bb.Append(new ReturnInstruction(t.Void, cmp));

        var ll = Disassemble(module);
        Assert.Contains("fcmp fast olt", ll);
    }

    [SkippableFact]
    public void CallsiteAttributes_RoundTrip()
    {
        var module = NewModule("callattrs.ll");
        var t = module.Types;
        var i32 = t.Int32;

        var calleeFt = t.GetFunction(i32, new LlvmType[] { i32 });
        var callee = module.CreateFunction("callee", calleeFt);

        var caller = module.CreateFunction("caller", t.GetFunction(i32, new LlvmType[] { i32 }));
        var bb = caller.AppendBlock("entry");
        var call = new CallInstruction(calleeFt, callee, new Value[] { caller.Parameters[0] });
        call.FunctionAttributes.Add(IR.Attribute.Enum(AttrKindCodes.NoUnwind));
        call.GetParameterAttributes(0).Add(IR.Attribute.Enum(AttrKindCodes.NoUndef));
        bb.Append(call);
        bb.Append(new ReturnInstruction(t.Void, call));

        var ll = Disassemble(module);
        Assert.Contains("nounwind", ll);
        Assert.Contains("noundef", ll);
        Assert.Contains("@callee", ll);
    }

    [SkippableFact]
    public void OperandBundles_Deopt_RoundTrip()
    {
        var module = NewModule("bundles.ll");
        var t = module.Types;
        var i32 = t.Int32;

        var calleeFt = t.GetFunction(t.Void, System.Array.Empty<LlvmType>());
        var callee = module.CreateFunction("sink", calleeFt);

        var caller = module.CreateFunction("caller", t.GetFunction(t.Void, new LlvmType[] { i32 }));
        var bb = caller.AppendBlock("entry");
        var call = new CallInstruction(calleeFt, callee, System.Array.Empty<Value>());
        call.Bundles.Add(new OperandBundle("deopt", new Value[] { caller.Parameters[0] }));
        bb.Append(call);
        bb.Append(new ReturnInstruction(t.Void));

        var ll = Disassemble(module);
        Assert.Contains("\"deopt\"", ll);
        Assert.Contains("@sink", ll);
    }

    [SkippableFact]
    public void Funclet_CatchSwitchCatchPadCatchRet_RoundTrip()
    {
        var module = NewModule("funclet.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();

        // declare i32 @__C_specific_handler(...)
        var personalityType = t.GetFunction(i32, System.Array.Empty<LlvmType>(), isVarArg: true);
        var personality = module.CreateFunction("__C_specific_handler", personalityType);

        var calleeFt = t.GetFunction(t.Void, System.Array.Empty<LlvmType>());
        var callee = module.CreateFunction("callee", calleeFt);

        // define void @caller() personality ptr @__C_specific_handler {
        // entry:
        //   invoke void @callee() to label %cont unwind label %dispatch
        // cont:
        //   ret void
        // dispatch:
        //   %cs = catchswitch within none [label %handler] unwind to caller
        // handler:
        //   %cp = catchpad within %cs []
        //   catchret from %cp to label %cont
        // }
        var caller = module.CreateFunction("caller", t.GetFunction(t.Void, System.Array.Empty<LlvmType>()));
        caller.Personality = personality;

        var entry = caller.AppendBlock("entry");
        var cont = caller.AppendBlock("cont");
        var dispatch = caller.AppendBlock("dispatch");
        var handler = caller.AppendBlock("handler");

        entry.Append(new InvokeInstruction(calleeFt, callee, System.Array.Empty<Value>(), cont, dispatch));
        cont.Append(new ReturnInstruction(t.Void));

        var cs = new CatchSwitchInstruction(t.Token, parentPad: null);
        cs.Handlers.Add(handler);
        // unwind to caller (UnwindDest stays null)
        dispatch.Append(cs);

        var cp = new CatchPadInstruction(t.Token, cs, System.Array.Empty<Value>());
        handler.Append(cp);
        handler.Append(new CatchRetInstruction(t.Void, cp, cont));

        var ll = Disassemble(module);
        Assert.Contains("catchswitch", ll);
        Assert.Contains("catchpad", ll);
        Assert.Contains("catchret", ll);
        Assert.Contains("personality", ll);
        Assert.Contains("@__C_specific_handler", ll);
    }

    [SkippableFact]
    public void IndirectBr_BlockAddress_RoundTrip()
    {
        var module = NewModule("indirectbr.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();

        // define i32 @dispatch(ptr %p) {
        //   indirectbr ptr %p, [label %a, label %b]
        // a: ret i32 1
        // b: ret i32 2
        // }
        var fn = module.CreateFunction("dispatch",
            t.GetFunction(i32, new LlvmType[] { ptr }));
        var entry = fn.AppendBlock("entry");
        var a = fn.AppendBlock("a");
        var b = fn.AppendBlock("b");

        entry.Append(new IndirectBrInstruction(t.Void, fn.Parameters[0],
            new BasicBlock[] { a, b }));
        a.Append(new ReturnInstruction(i32, new IntegerConstant(i32, 1)));
        b.Append(new ReturnInstruction(i32, new IntegerConstant(i32, 2)));

        // Module-level user that takes blockaddress(@dispatch, %a) — just keep it alive.
        // define ptr @addr_of_a() { ret ptr blockaddress(@dispatch, %a) }
        var addrFn = module.CreateFunction("addr_of_a",
            t.GetFunction(ptr, System.Array.Empty<LlvmType>()));
        var ae = addrFn.AppendBlock("entry");
        ae.Append(new ReturnInstruction(ptr, new BlockAddress(fn, a, ptr)));

        var ll = Disassemble(module);
        Assert.Contains("indirectbr", ll);
        Assert.Contains("blockaddress", ll);
    }

    // ----------------------------------------------------------------------
    // Scale tests — large modules can surface "Invalid record" reader errors
    // even when every record *kind* round-trips cleanly. The tests above
    // cover record kinds exhaustively, so any remaining bug must be in
    // *quantity* — VBR overflow, fixed-width abbrev limit, value-id forward
    // references crossing some threshold, etc. The tests below stress the
    // dimensions that grow the most in real-world inputs.
    // ----------------------------------------------------------------------

    [SkippableFact]
    public void Scale_ManyBasicBlocks_RoundTrip()
    {
        var module = NewModule("scale_bbs.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        // Single function with N blocks chained tail-to-head, each block
        // adds 1 to a phi value flowed from the predecessor. Exercises
        // PHI forward references and large per-function value-id space.
        const int NumBlocks = 4096;
        var fn = module.CreateFunction("chain",
            t.GetFunction(i32, new LlvmType[] { i32 }));
        var entry = fn.AppendBlock("entry");

        var bbs = new BasicBlock[NumBlocks];
        for (int i = 0; i < NumBlocks; i++) bbs[i] = fn.AppendBlock("bb" + i);

        entry.Append(new BranchInstruction(voidT, bbs[0]));

        Value prev = fn.Parameters[0];
        BasicBlock prevBb = entry;
        for (int i = 0; i < NumBlocks; i++)
        {
            var phi = bbs[i].Append(new PhiInstruction(i32));
            phi.AddIncoming(prev, prevBb);
            var add = bbs[i].Append(
                new BinaryOperator(BinaryOpcode.Add, phi, new IntegerConstant(i32, 1)));
            if (i + 1 < NumBlocks)
                bbs[i].Append(new BranchInstruction(voidT, bbs[i + 1]));
            else
                bbs[i].Append(new ReturnInstruction(voidT, add));
            prev = add;
            prevBb = bbs[i];
        }

        var ll = Disassemble(module);
        Assert.Contains("define i32 @chain", ll);
    }

    [SkippableFact]
    public void Scale_ManyAllocasInOneBlock_RoundTrip()
    {
        var module = NewModule("scale_alloca.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        // Thousands of allocas in a single entry block — typical for
        // frontends that translate every source local into an alloca.
        const int N = 8192;
        var fn = module.CreateFunction("alloc_storm",
            t.GetFunction(voidT, System.Array.Empty<LlvmType>()));
        var entry = fn.AppendBlock("entry");
        var one = new IntegerConstant(i32, 1);
        for (int i = 0; i < N; i++)
            entry.Append(new AllocaInstruction(i32, one, t.GetPointer(), 4));
        entry.Append(new ReturnInstruction(voidT));

        var ll = Disassemble(module);
        Assert.Contains("alloca i32", ll);
    }

    [SkippableFact]
    public void Scale_ManyGlobalsAndStringTable_RoundTrip()
    {
        var module = NewModule("scale_globals.ll");
        var t = module.Types;
        var i32 = t.Int32;

        // Tens of thousands of globals — stress the strtab + GLOBALVAR
        // records together.
        const int N = 16384;
        for (int i = 0; i < N; i++)
        {
            var gv = module.CreateGlobal("g" + i, i32);
            gv.Initializer = new IntegerConstant(i32, i);
            gv.Linkage = Linkage.Internal;
        }

        // One function so the result is recognisably a "module" (not just
        // an empty type table) and string-table records have to coexist
        // with the function record.
        var fn = module.CreateFunction("noop",
            t.GetFunction(t.Void, System.Array.Empty<LlvmType>()));
        var entry = fn.AppendBlock("entry");
        entry.Append(new ReturnInstruction(t.Void));

        var ll = Disassemble(module);
        Assert.Contains("@g0 ", ll);
        Assert.Contains("@g16383 ", ll);
    }

    [SkippableFact]
    public void Scale_ManyCallsInOneBlock_RoundTrip()
    {
        var module = NewModule("scale_calls.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        // Very long entry block packed with call instructions — stress
        // per-instruction value ids and call abbrev encoding.
        var calleeFt = t.GetFunction(i32, new LlvmType[] { i32 });
        var callee = module.CreateFunction("callee", calleeFt);

        const int N = 4096;
        var fn = module.CreateFunction("caller",
            t.GetFunction(i32, new LlvmType[] { i32 }));
        var entry = fn.AppendBlock("entry");
        Value last = fn.Parameters[0];
        for (int i = 0; i < N; i++)
        {
            last = entry.Append(new CallInstruction(calleeFt, callee, new[] { last }));
        }
        entry.Append(new ReturnInstruction(voidT, last));

        var ll = Disassemble(module);
        Assert.Contains("define i32 @caller", ll);
    }

    [SkippableFact]
    public void Attachment_InvariantLoad_RoundTrip()
    {
        var module = NewModule("attach_invariant.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var voidT = t.Void;

        var fn = module.CreateFunction("load_invariant",
            t.GetFunction(i32, new LlvmType[] { ptr }));
        var entry = fn.AppendBlock("entry");
        var load = entry.Append(new LoadInstruction(i32, fn.Parameters[0], 4));
        load.AddAttachment("invariant.load", MdTuple.Empty);
        entry.Append(new ReturnInstruction(voidT, load));

        var ll = Disassemble(module);
        Assert.Contains("!invariant.load", ll);
    }

    [SkippableFact]
    public void Attachment_Range_RoundTrip()
    {
        var module = NewModule("attach_range.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var voidT = t.Void;

        var fn = module.CreateFunction("load_range",
            t.GetFunction(i32, new LlvmType[] { ptr }));
        var entry = fn.AppendBlock("entry");
        var load = entry.Append(new LoadInstruction(i32, fn.Parameters[0], 4));
        // !range !{i32 0, i32 256}: load returns a value in [0,256).
        // MdValue-wrapped IntegerConstants exercise ValueEnumerator's
        // attachment-recursion path added alongside this feature.
        load.AddAttachment("range", new MdTuple(
            new MdValue(new IntegerConstant(i32, 0)),
            new MdValue(new IntegerConstant(i32, 256))));
        entry.Append(new ReturnInstruction(voidT, load));

        var ll = Disassemble(module);
        Assert.Contains("!range", ll);
    }

    [SkippableFact]
    public void Attachment_RejectsDbgKind()
    {
        var i32 = new Module().Types.Int32;
        var inst = new BinaryOperator(
            BinaryOpcode.Add, new IntegerConstant(i32, 1), new IntegerConstant(i32, 2));
        Assert.Throws<System.ArgumentException>(
            () => inst.AddAttachment("dbg", MdTuple.Empty));
    }

    [SkippableFact]
    public void TypeTable_NamedStructForwardRef_RoundTrip()
    {
        // Self-referential named struct: %Node = type { %Node*, i32 }.
        // Under opaque pointers the cycle isn't visible at the type level
        // (the pointer is just `ptr`), so this primarily exercises that
        // the topological sort tolerates a CreateOpaqueNamedStruct ->
        // SetBody pattern and that the struct's id is renumbered to
        // precede any literal struct that mentions it.
        var module = NewModule("named_struct_cycle.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();

        var node = t.CreateOpaqueNamedStruct("Node");
        node.SetBody(new LlvmType[] { ptr, i32 });

        // Literal struct that references the named struct, so the sort
        // has to place %Node before { %Node, i32 }.
        var pair = t.GetStruct(new LlvmType[] { node, i32 });

        var fn = module.CreateFunction("alloc_node",
            t.GetFunction(ptr, System.Array.Empty<LlvmType>()));
        var entry = fn.AppendBlock("entry");
        // alloca %Node ensures the named struct is referenced from a
        // function body and isn't dropped by llvm-dis as unused.
        var slot = entry.Append(new AllocaInstruction(node, new IntegerConstant(i32, 1), ptr, 8));
        entry.Append(new ReturnInstruction(t.Void, slot));

        var ll = Disassemble(module);
        Assert.Contains("%Node = type", ll);
        Assert.Contains("alloca %Node", ll);
    }
}

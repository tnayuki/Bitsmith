using System.IO;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

public class GlobalsAndAttributesRoundtripTests
{
    private static Module BuildModule()
    {
        var module = new Module
        {
            SourceFileName = "globals.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();

        // @answer = global i32 42
        var answer = module.CreateGlobal("answer", i32);
        answer.Initializer = new IntegerConstant(i32, 42);

        // @ext_int = external global i32
        module.CreateGlobal("ext_int", i32);

        // @zero_ptr = global ptr null
        var zeroPtr = module.CreateGlobal("zero_ptr", ptr);
        zeroPtr.Initializer = new NullConstant(ptr);

        // declare void @ext_fn(ptr byval(i32) %p) noundef-on-return
        var voidT = t.Void;
        var declType = t.GetFunction(voidT, new LlvmType[] { ptr });
        var decl = module.CreateFunction("ext_fn", declType);
        decl.GetParameterAttributes(0).Add(IR.Attribute.Type(AttrKindCodes.ByVal, i32));

        // define i32 @echo(i32 noundef %0) nounwind { ret i32 %0 }
        // Exercises a function-level enum attr and a parameter enum attr.
        var echoType = t.GetFunction(i32, new LlvmType[] { i32 });
        var echo = module.CreateFunction("echo", echoType);
        echo.FunctionAttributes.Add(IR.Attribute.Enum(AttrKindCodes.NoUnwind));
        echo.GetParameterAttributes(0).Add(IR.Attribute.Enum(AttrKindCodes.NoUndef));
        var bb = echo.AppendBlock("entry");
        bb.Append(new ReturnInstruction(voidT, echo.Parameters[0]));

        return module;
    }

    [SkippableFact]
    public void Globals_BcAnalyzerDumpsExpectedBlocks()
    {
        LlvmTools.Require("llvm-bcanalyzer");

        var path = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(BuildModule()).WriteToFile(path);
            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", path);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            Assert.Contains("PARAMATTR_GROUP_BLOCK", r.StdOut);
            Assert.Contains("PARAMATTR_BLOCK", r.StdOut);
            Assert.Contains("CONSTANTS_BLOCK", r.StdOut);
            Assert.Contains("GLOBALVAR", r.StdOut);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void Globals_LlvmDisRoundtripsSurfaceText()
    {
        LlvmTools.Require("llvm-dis");

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(BuildModule()).WriteToFile(bcPath);

            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");

            var ll = File.ReadAllText(llPath);
            Assert.Contains("@answer", ll);
            Assert.Contains("42", ll);
            Assert.Contains("@ext_int", ll);
            Assert.Contains("external", ll);
            Assert.Contains("@zero_ptr", ll);
            Assert.Contains("null", ll);
            Assert.Contains("declare", ll);
            Assert.Contains("@ext_fn", ll);
            Assert.Contains("byval(i32)", ll);
            Assert.Contains("nounwind", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void NewConstants_FloatAggregateStringPoison_RoundTrip()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "consts.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;

        var fpi = module.CreateGlobal("pi", t.Float);
        fpi.Initializer = new FloatConstant(t.Float, 3.14f);

        var dpi = module.CreateGlobal("dpi", t.Double);
        dpi.Initializer = new DoubleConstant(t.Double, 3.141592653589793);

        var arrType = t.GetArray(3, t.Int32);
        var arr = module.CreateGlobal("arr", arrType);
        arr.Initializer = new AggregateConstant(arrType, new Constant[]
        {
            new IntegerConstant(t.Int32, 1),
            new IntegerConstant(t.Int32, 2),
            new IntegerConstant(t.Int32, 3),
        });

        var msgBytes = System.Text.Encoding.ASCII.GetBytes("hi\0");
        var strType = t.GetArray((ulong)msgBytes.Length, t.Int8);
        var str = module.CreateGlobal("msg", strType);
        str.IsConstant = true;
        str.Initializer = new StringConstant(strType, msgBytes, isCString: true);

        var poison = module.CreateGlobal("p", t.Int32);
        poison.Initializer = new PoisonConstant(t.Int32);

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("@pi", ll);
            Assert.Contains("@dpi", ll);
            Assert.Contains("@arr", ll);
            Assert.Contains("@msg", ll);
            Assert.Contains("@p", ll);
            Assert.Contains("poison", ll);
            // Aggregate prints as `[ i32 1, i32 2, i32 3 ]`
            Assert.Contains("i32 1", ll);
            Assert.Contains("i32 2", ll);
            Assert.Contains("i32 3", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void GlobalAliasIFuncComdat_RoundTrip()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "alias.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();

        // @answer = global i32 42, comdat ($cd), section ".bitsmith"
        var cd = module.CreateComdat("cd");
        var answer = module.CreateGlobal("answer", i32);
        answer.Initializer = new IntegerConstant(i32, 42);
        answer.Comdat = cd;
        answer.Section = ".bitsmith";
        answer.IsDsoLocal = true;

        // @answer_alias = alias i32, ptr @answer
        module.CreateAlias("answer_alias", i32, answer);

        // define i32 @target() { ret i32 7 }
        var fnType = t.GetFunction(i32, System.Array.Empty<LlvmType>());
        var target = module.CreateFunction("target", fnType);
        var bb = target.AppendBlock("entry");
        bb.Append(new ReturnInstruction(t.Void, new IntegerConstant(i32, 7)));

        // define ptr @resolver() { ret ptr @target }
        var rType = t.GetFunction(ptr, System.Array.Empty<LlvmType>());
        var resolver = module.CreateFunction("resolver", rType);
        var rbb = resolver.AppendBlock("entry");
        rbb.Append(new ReturnInstruction(t.Void, target));

        // @do_target = ifunc i32 (), ptr @resolver
        module.CreateIFunc("do_target", fnType, resolver);

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("@answer_alias", ll);
            Assert.Contains("alias", ll);
            Assert.Contains("@do_target", ll);
            Assert.Contains("ifunc", ll);
            Assert.Contains("comdat", ll);
            Assert.Contains(".bitsmith", ll);
            Assert.Contains("dso_local", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void ConstantExpr_GepCastCmp_RoundTrip()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "constexpr.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var i64 = t.Int64;
        var i1 = t.Int1;
        var ptr = t.GetPointer();

        var arrTy = t.GetArray(4, i32);
        var arr = module.CreateGlobal("arr", arrTy);
        arr.Initializer = new AggregateConstant(arrTy, new Constant[]
        {
            new IntegerConstant(i32, 1),
            new IntegerConstant(i32, 2),
            new IntegerConstant(i32, 3),
            new IntegerConstant(i32, 4),
        });

        // @arr_2nd = global ptr getelementptr inbounds ([4 x i32], ptr @arr, i64 0, i64 1)
        var arr2nd = module.CreateGlobal("arr_2nd", ptr);
        arr2nd.Initializer = new ConstantGep(arrTy, arr, new Constant[]
        {
            new IntegerConstant(i64, 0),
            new IntegerConstant(i64, 1),
        }, ptr, isInBounds: true);

        // @as_int = global i64 ptrtoint (ptr @arr to i64)
        // CastCodes.PtrToInt = 9 (defined in M6 amend; use raw value here).
        var asInt = module.CreateGlobal("as_int", i64);
        asInt.Initializer = new ConstantCast(9 /* PtrToInt */, arr, i64);

        // @cmp = global i1 icmp eq (ptr @arr, ptr null)
        // CmpPredicates.IcmpEq = 32 (added in M6 amend).
        var cmp = module.CreateGlobal("cmp", i1);
        cmp.Initializer = new ConstantCmp(32 /* IcmpEq */, arr, new NullConstant(ptr), i1);

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("getelementptr inbounds", ll);
            Assert.Contains("ptrtoint", ll);
            // icmp constant folds to `false` (a global address is never null) when
            // the reader resolves it; the bitcode round-trip is what we exercise.
            Assert.Contains("@arr", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void AllocKind_IntAttribute_RoundTrip()
    {
        // Regression: AttrKindCodes.AllocKind was 78 (LLVM 15 wire = DISABLE_SANITIZER_INSTRUMENTATION,
        // Enum-form). Writing it as Int-form produced bitcode that the reader rejects with
        // "Not an int attribute". Correct LLVM 15 wire value is 82.
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "alloc_kind.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var ptr = t.GetPointer();
        var fnTy = t.GetFunction(ptr, new LlvmType[] { t.Int64 });
        var fn = module.CreateFunction("my_alloc", fnTy);
        // allockind("alloc,uninitialized") -> bits Alloc(1) | Uninitialized(8) = 9
        fn.FunctionAttributes.Add(IR.Attribute.Int(AttrKindCodes.AllocKind, 1UL | 8UL));

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("allockind(\"alloc,uninitialized\")", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void AllocAlign_EnumAttribute_RoundTrip()
    {
        // Regression: AllocAlign was 73 (LLVM 15 wire = NO_PROFILE). Silent miscompile —
        // bitcode loads but the parameter gets tagged with the wrong attribute. Correct = 80.
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "alloc_align.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var ptr = t.GetPointer();
        var fnTy = t.GetFunction(ptr, new LlvmType[] { t.Int64 });
        var fn = module.CreateFunction("aligned_alloc_wrap", fnTy);
        fn.GetParameterAttributes(0).Add(IR.Attribute.Enum(AttrKindCodes.AllocAlign));

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("allocalign", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void AllocSize_IntAttribute_RoundTrip()
    {
        // allocsize(N) / allocsize(N, M) — packs the argument indices into a 64-bit value.
        // Encoding: (sizeArg << 32) | numArg ; numArg absent == 0xFFFFFFFF sentinel.
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "alloc_size.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var ptr = t.GetPointer();
        var fnTy = t.GetFunction(ptr, new LlvmType[] { t.Int64 });
        var fn = module.CreateFunction("my_malloc", fnTy);
        // allocsize(0): sizeArg=0, numArg absent. Encoded as (0 << 32) | 0xFFFFFFFF.
        fn.FunctionAttributes.Add(IR.Attribute.Int(AttrKindCodes.AllocSize, 0xFFFFFFFFUL));

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("allocsize(0)", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void StringAttributes_RoundTrip()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "strattrs.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var fn = module.CreateFunction("noop", t.GetFunction(t.Void, System.Array.Empty<LlvmType>()));
        fn.FunctionAttributes.Add(IR.Attribute.String("frame-pointer"));
        fn.FunctionAttributes.Add(IR.Attribute.StringKeyValue("target-cpu", "x86-64"));
        fn.AppendBlock("entry").Append(new ReturnInstruction(t.Void));

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("\"frame-pointer\"", ll);
            Assert.Contains("\"target-cpu\"", ll);
            Assert.Contains("x86-64", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }
}

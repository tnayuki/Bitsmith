using System.IO;
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

public class MinimalFunctionRoundtripTests
{
    private static Module BuildAddModule()
    {
        var module = new Module
        {
            SourceFileName = "add.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var fnType = t.GetFunction(i32, new LlvmType[] { i32, i32 });

        var fn = module.CreateFunction("add", fnType);
        var bb = fn.AppendBlock("entry");
        var sum = bb.Append(new BinaryOperator(BinaryOpcode.Add, fn.Parameters[0], fn.Parameters[1]));
        bb.Append(new ReturnInstruction(t.Void, sum));
        return module;
    }

    [SkippableFact]
    public void AddFunction_BcAnalyzerDumpsFunctionBlock()
    {
        LlvmTools.Require("llvm-bcanalyzer");

        var path = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(BuildAddModule()).WriteToFile(path);

            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", path);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            Assert.Contains("FUNCTION_BLOCK", r.StdOut);
            Assert.Contains("DECLAREBLOCKS", r.StdOut);
            Assert.Contains("INST_BINOP", r.StdOut);
            Assert.Contains("INST_RET", r.StdOut);
            Assert.Contains("STRTAB_BLOCK", r.StdOut);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void AddFunction_LlvmDisRoundtripsAddDefinition()
    {
        LlvmTools.Require("llvm-dis");

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(BuildAddModule()).WriteToFile(bcPath);

            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");

            var ll = File.ReadAllText(llPath);
            Assert.Contains("define", ll);
            Assert.Contains("@add", ll);
            Assert.Contains("i32", ll);
            Assert.Contains("add", ll);
            Assert.Contains("ret", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void BinaryOpFlags_NswNuwExactAndFmf_RoundTrip()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "flags.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var f32 = t.Float;

        // i32 @iflags(i32 %0, i32 %1) { %2 = add nsw nuw i32 %0, %1; ret i32 %2 }
        var iFn = module.CreateFunction("iflags", t.GetFunction(i32, new LlvmType[] { i32, i32 }));
        var iBb = iFn.AppendBlock("entry");
        var iAdd = iBb.Append(new BinaryOperator(BinaryOpcode.Add, iFn.Parameters[0], iFn.Parameters[1])
        { IsNsw = true, IsNuw = true });
        iBb.Append(new ReturnInstruction(t.Void, iAdd));

        // i32 @udivexact(i32 %0, i32 %1) { %2 = udiv exact i32 %0, %1; ret i32 %2 }
        var uFn = module.CreateFunction("udivexact", t.GetFunction(i32, new LlvmType[] { i32, i32 }));
        var uBb = uFn.AppendBlock("entry");
        var uDiv = uBb.Append(new BinaryOperator(BinaryOpcode.UDiv, uFn.Parameters[0], uFn.Parameters[1])
        { IsExact = true });
        uBb.Append(new ReturnInstruction(t.Void, uDiv));

        // float @ffast(float %0, float %1) { %2 = fadd fast float %0, %1; ret float %2 }
        var fFn = module.CreateFunction("ffast", t.GetFunction(f32, new LlvmType[] { f32, f32 }));
        var fBb = fFn.AppendBlock("entry");
        var fAdd = fBb.Append(new BinaryOperator(BinaryOpcode.Add, fFn.Parameters[0], fFn.Parameters[1])
        { Fmf = FastMathFlags.Fast });
        fBb.Append(new ReturnInstruction(t.Void, fAdd));

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");
            var ll = File.ReadAllText(llPath);
            Assert.Contains("nsw", ll);
            Assert.Contains("nuw", ll);
            Assert.Contains("udiv exact", ll);
            Assert.Contains("fadd fast", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }
}

using System.IO;
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

public class EmptyModuleRoundtripTests
{
    [SkippableFact]
    public void EmptyModule_BcAnalyzerAcceptsFile()
    {
        LlvmTools.Require("llvm-bcanalyzer");

        var path = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(new Module
            {
                SourceFileName = "empty.ll",
                TargetTriple = "x86_64-unknown-linux-gnu",
                DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
            }).WriteToFile(path);

            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", path);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            Assert.Contains("MODULE_BLOCK", r.StdOut);
            Assert.Contains("IDENTIFICATION_BLOCK", r.StdOut);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void EmptyModule_LlvmDisRoundtripsSourceFilename()
    {
        LlvmTools.Require("llvm-dis");

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(new Module
            {
                SourceFileName = "hello.c",
                TargetTriple = "x86_64-unknown-linux-gnu",
                DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
            }).WriteToFile(bcPath);

            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");

            var ll = File.ReadAllText(llPath);
            Assert.Contains("hello.c", ll);
            Assert.Contains("x86_64-unknown-linux-gnu", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }

    [SkippableFact]
    public void ModuleAsm_RoundTrips()
    {
        LlvmTools.Require("llvm-dis");

        var bcPath = Path.GetTempFileName() + ".bc";
        var llPath = bcPath + ".ll";
        try
        {
            new ModuleWriter(new Module
            {
                SourceFileName = "asm.ll",
                TargetTriple = "x86_64-unknown-linux-gnu",
                DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
                InlineAsm = ".globl _start\n_start: nop\n",
            }).WriteToFile(bcPath);

            var r = LlvmTools.Run("llvm-dis", bcPath, "-o", llPath);
            Assert.True(r.ExitCode == 0, $"llvm-dis failed: {r.StdErr}");

            var ll = File.ReadAllText(llPath);
            Assert.Contains("module asm", ll);
            Assert.Contains("_start", ll);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
            if (File.Exists(llPath)) File.Delete(llPath);
        }
    }
}

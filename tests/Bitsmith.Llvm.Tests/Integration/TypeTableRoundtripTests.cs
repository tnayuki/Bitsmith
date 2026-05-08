using System.IO;
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

public class TypeTableRoundtripTests
{
    [SkippableFact]
    public void TypeTable_BcAnalyzerEnumeratesAllTypes()
    {
        LlvmTools.Require("llvm-bcanalyzer");

        var module = new Module
        {
            SourceFileName = "types.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;
        var i64 = t.Int64;
        var ptr = t.GetPointer();
        t.GetFunction(i32, new[] { i32, i32 });
        t.GetArray(4, i64);
        t.GetVector(4, i32);
        t.GetStruct(new[] { (LlvmType)i32, ptr });
        t.CreateNamedStruct("Pair", new[] { (LlvmType)i32, i32 });
        // Extra primitive types added in M3.
        _ = t.Half;
        _ = t.BFloat;
        _ = t.X86Fp80;
        _ = t.Fp128;
        _ = t.PpcFp128;
        _ = t.X86Mmx;
        _ = t.X86Amx;
        _ = t.Token;

        var path = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(module).WriteToFile(path);
            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", path);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            Assert.Contains("TYPE_BLOCK_ID", r.StdOut);
            Assert.Contains("INTEGER", r.StdOut);
            // llvm-bcanalyzer 15 lacks a friendly name for code 25 and prints "UnknownCode25".
            Assert.True(r.StdOut.Contains("OPAQUE_POINTER") || r.StdOut.Contains("UnknownCode25"),
                "expected an opaque pointer record (code 25) in the type block");
            Assert.Contains("FUNCTION", r.StdOut);
            Assert.Contains("ARRAY", r.StdOut);
            Assert.Contains("VECTOR", r.StdOut);
            Assert.Contains("STRUCT_ANON", r.StdOut);
            Assert.Contains("STRUCT_NAMED", r.StdOut);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void ScalableVector_RoundTrip()
    {
        LlvmTools.Require("llvm-bcanalyzer");

        var module = new Module
        {
            SourceFileName = "svec.ll",
            TargetTriple = "aarch64-unknown-linux-gnu",
            DataLayout = "e-m:e-i8:8:32-i16:16:32-i64:64-i128:128-n32:64-S128",
        };
        var t = module.Types;
        var svec = t.GetScalableVector(4, t.Int32);
        // Reference from a function signature so it survives the type table.
        t.GetFunction(svec, new[] { svec });

        var bcPath = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            // bcanalyzer accepts the file (so the scalable bit is well-formed)
            // and exposes the VECTOR record; M5+ functions are needed before
            // llvm-dis can render the type textually, hence the bcanalyzer check.
            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", bcPath);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            Assert.Contains("VECTOR", r.StdOut);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
        }
    }

    [SkippableFact]
    public void OpaqueNamedStruct_ForwardDeclResolvesToBody()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "opaque.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;

        // Pre-register element types so the named struct's body can reference
        // them backward (LLVM only allows forward references to named structs).
        var i32 = t.Int32;
        var ptr = t.GetPointer();
        var node = t.CreateOpaqueNamedStruct("Node");
        node.SetBody(new LlvmType[] { i32, ptr });

        t.GetFunction(t.Void, new LlvmType[] { ptr });

        var bcPath = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            // The reader must accept the OPAQUE-then-STRUCT_NAMED encoding. With
            // opaque pointers the struct may not appear in textual IR (no use site),
            // so we verify acceptance via bcanalyzer.
            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", bcPath);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            Assert.Contains("STRUCT_NAME", r.StdOut);
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
        }
    }

    [SkippableFact]
    public void OpaqueNamedStruct_MutualForwardReferenceBetweenNamedStructs()
    {
        LlvmTools.Require("llvm-dis");

        var module = new Module
        {
            SourceFileName = "mutual.ll",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        };
        var t = module.Types;
        var i32 = t.Int32;

        // Two mutually-referencing named structs. We forward-declare both so
        // their bodies can reference each other regardless of declaration order.
        // (Element types are pointers in LLVM 15 opaque-ptr land, but the named
        // struct itself can also forward-ref another named struct.)
        var a = t.CreateOpaqueNamedStruct("A");
        var b = t.CreateOpaqueNamedStruct("B");
        a.SetBody(new LlvmType[] { i32, b });
        b.SetBody(new LlvmType[] { i32, a });

        var bcPath = Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", bcPath);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            // Two STRUCT_NAME records (one per named struct).
            int count = 0;
            int idx = 0;
            while ((idx = r.StdOut.IndexOf("STRUCT_NAME", idx)) >= 0) { count++; idx++; }
            Assert.True(count >= 2, $"expected ≥2 STRUCT_NAME records, got {count}");
        }
        finally
        {
            if (File.Exists(bcPath)) File.Delete(bcPath);
        }
    }
}

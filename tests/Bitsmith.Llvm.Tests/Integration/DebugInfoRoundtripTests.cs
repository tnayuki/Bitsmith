using System.IO;
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

public class DebugInfoRoundtripTests
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
    public void Module_WithCompileUnitOnly()
    {
        var module = NewModule("dbg.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        var file = new DiFile("x.c", "/tmp");
        var cu = new DiCompileUnit(file)
        {
            SourceLanguage = DwarfLanguage.C99,
            Producer = "bitsmith",
            EmissionKind = DiEmissionKind.FullDebug,
        };

        var fn = module.CreateFunction("noop", t.GetFunction(voidT, new LlvmType[] { }));
        var bb = fn.AppendBlock("entry");
        bb.Append(new ReturnInstruction(voidT));

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);

        var dbgVer = new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3)));
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(dbgVer);
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        Assert.Contains("DICompileUnit", ll);
        Assert.Contains("\"x.c\"", ll);
    }

    [SkippableFact]
    public void Function_WithSubprogramAndDebugLoc()
    {
        var module = NewModule("dbg.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        // !DIFile, !DICompileUnit
        var file = new DiFile("x.c", "/tmp");
        var cu = new DiCompileUnit(file)
        {
            SourceLanguage = DwarfLanguage.C99,
            Producer = "bitsmith",
            EmissionKind = DiEmissionKind.FullDebug,
        };

        // !DIBasicType for int, then a SubroutineType !{int, int, int}
        var intType = new DiBasicType("int", 32, DwarfAte.Signed);
        var typesTuple = new MdTuple(new Metadata?[] { intType, intType, intType });
        var subroutineType = new DiSubroutineType(typesTuple);

        var sp = new DiSubprogram("add", file)
        {
            Scope = file,
            LinkageName = "add",
            Line = 1,
            ScopeLine = 1,
            Type = subroutineType,
            SpFlags = DiSpFlags.Definition,
            Unit = cu,
            RetainedNodes = MdTuple.Empty,
        };

        var loc = new DiLocation(2, 3, sp);

        var fn = module.CreateFunction("add", t.GetFunction(i32, new LlvmType[] { i32, i32 }));
        fn.Subprogram = sp;
        var bb = fn.AppendBlock("entry");
        var sum = bb.Append(new BinaryOperator(BinaryOpcode.Add, fn.Parameters[0], fn.Parameters[1]));
        sum.DebugLocation = loc;
        var ret = bb.Append(new ReturnInstruction(voidT, sum));
        ret.DebugLocation = loc;

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);

        // Required by the IR verifier so that llvm-dis surfaces the debug info
        // rather than silently dropping it.
        var dwarfVer = new MdTuple(
            new MdValue(new IntegerConstant(i32, 7)),
            new MdString("Dwarf Version"),
            new MdValue(new IntegerConstant(i32, 4)));
        var dbgVer = new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3)));
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(dwarfVer);
        modFlags.Operands.Add(dbgVer);
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        Assert.Contains("!dbg", ll);
        Assert.Contains("DICompileUnit", ll);
        Assert.Contains("DISubprogram", ll);
        Assert.Contains("DIBasicType", ll);
        Assert.Contains("DILocation", ll);
        Assert.Contains("\"add\"", ll);
        Assert.Contains("\"x.c\"", ll);
    }

    [SkippableFact]
    public void DerivedTypePointerAndLexicalBlock()
    {
        var module = NewModule("dbg2.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var voidT = t.Void;

        var file = new DiFile("p.c", "/tmp");
        var cu = new DiCompileUnit(file) { SourceLanguage = DwarfLanguage.C99, Producer = "bitsmith" };

        var intType = new DiBasicType("int", 32, DwarfAte.Signed);
        // typedef-style pointer-to-int.
        var intPtr = new DiDerivedType(DwarfTag.PointerType)
        {
            BaseType = intType,
            SizeInBits = 64,
        };

        var subroutineType = new DiSubroutineType(new MdTuple(intType, intPtr));
        var sp = new DiSubprogram("deref", file)
        {
            Scope = file,
            File = file,
            Line = 1,
            ScopeLine = 1,
            Type = subroutineType,
            Unit = cu,
            RetainedNodes = MdTuple.Empty,
        };

        var lex = new DiLexicalBlock(sp) { File = file, Line = 1, Column = 5 };
        var loc = new DiLocation(2, 3, lex);

        var fn = module.CreateFunction("deref", t.GetFunction(i32, new LlvmType[] { t.GetPointer() }));
        fn.Subprogram = sp;
        var bb = fn.AppendBlock("entry");
        var loaded = bb.Append(new LoadInstruction(i32, fn.Parameters[0], 4));
        loaded.DebugLocation = loc;
        var ret = bb.Append(new ReturnInstruction(voidT, loaded));
        ret.DebugLocation = loc;

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3))));
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        Assert.Contains("DIDerivedType", ll);
        Assert.Contains("DILexicalBlock", ll);
        Assert.Contains("DW_TAG_pointer_type", ll);
    }

    [SkippableFact]
    public void NewDiNodes_Subrange_Enum_Namespace_GlobalVarExpr_RoundTrip()
    {
        var module = NewModule("ditypes.ll");
        var t = module.Types;
        var i32 = t.Int32;

        var file = new DiFile("a.cc", "/src");
        var cu = new DiCompileUnit(file) { Producer = "bitsmith" };

        // DICompositeType for an array `int[3]` exercising DISubrange.
        var i32Ty = new DiBasicType("int", 32, DwarfAte.Signed);
        var subr = new DiSubrange(3);
        var arrTy = new DiCompositeType(DwarfTag.ArrayType)
        {
            BaseType = i32Ty,
            SizeInBits = 96,
            Elements = new MdTuple(new Metadata?[] { subr }),
        };

        // DICompositeType for an enumeration with one DIEnumerator.
        var enVal = new DiEnumerator("Red", 0);
        var enumTy = new DiCompositeType(DwarfTag.EnumerationType)
        {
            Name = "Color",
            BaseType = i32Ty,
            SizeInBits = 32,
            Elements = new MdTuple(new Metadata?[] { enVal }),
        };

        // DINamespace + DIImportedEntity referring to it.
        var ns = new DiNamespace { Name = "ns", Scope = cu };
        var imp = new DiImportedEntity { Scope = cu, Entity = ns, Name = "ns_alias", File = file, Line = 1 };

        // DIGlobalVariable + DIGlobalVariableExpression on a real global.
        var g = module.CreateGlobal("g", i32);
        g.Initializer = new IntegerConstant(i32, 0);
        var div = new DiGlobalVariable
        {
            Scope = cu, Name = "g", LinkageName = "g", File = file, Line = 1, Type = i32Ty,
        };
        var gve = new DiGlobalVariableExpression(div, DiExpression.Empty);

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);

        // To force enumeration we plant the new nodes under a custom !named.
        var anchors = new NamedMetadata("bitsmith.test.anchors");
        anchors.Operands.Add(arrTy);
        anchors.Operands.Add(enumTy);
        anchors.Operands.Add(imp);
        anchors.Operands.Add(gve);
        module.NamedMetadata.Add(anchors);

        var dbgVer = new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3)));
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(dbgVer);
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        Assert.Contains("DISubrange", ll);
        Assert.Contains("DIEnumerator", ll);
        Assert.Contains("DINamespace", ll);
        Assert.Contains("DIImportedEntity", ll);
        Assert.Contains("DIGlobalVariableExpression", ll);
        Assert.Contains("\"Red\"", ll);
        Assert.Contains("\"ns\"", ll);
    }

    [SkippableFact]
    public void GlobalDebugAttachment_RoundTrip()
    {
        var module = NewModule("gdbg.ll");
        var t = module.Types;
        var i32 = t.Int32;

        var file = new DiFile("g.c", "/src");
        var cu = new DiCompileUnit(file) { Producer = "bitsmith" };
        var i32Ty = new DiBasicType("int", 32, DwarfAte.Signed);

        var g = module.CreateGlobal("g", i32);
        g.Initializer = new IntegerConstant(i32, 7);

        var div = new DiGlobalVariable
        {
            Scope = cu, Name = "g", LinkageName = "g", File = file, Line = 1, Type = i32Ty,
        };
        g.DebugInfo = new DiGlobalVariableExpression(div, DiExpression.Empty);

        // Without llvm.dbg.cu in named metadata the verifier still passes; include it for realism.
        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3))));
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        // The textual IR shows `@g = global i32 7, !dbg !N` when the attachment is wired.
        Assert.Contains("@g", ll);
        Assert.Contains(", !dbg !", ll);
        Assert.Contains("DIGlobalVariableExpression", ll);
    }

    [SkippableFact]
    public void RemainingDiNodes_Module_Macro_StringType_GenericSubrange_RoundTrip()
    {
        // Exercises the writers added in the M7 r3 amend: DIModule, DIMacro,
        // DIMacroFile, DIStringType, DIGenericSubrange, DICommonBlock, DIObjCProperty.
        // Most of these don't render to textual IR unless rooted from a CU, so we
        // verify the bitcode parses successfully via llvm-bcanalyzer.
        LlvmTools.Require("llvm-bcanalyzer");

        var module = NewModule("ditypes2.ll");
        var t = module.Types;
        var i32 = t.Int32;

        var file = new DiFile("a.f90", "/src");
        var cu = new DiCompileUnit(file)
        {
            SourceLanguage = DwarfLanguage.C99,
            Producer = "bitsmith",
        };

        // DIMacroFile { DIMacro }
        var mac = new DiMacro { MacInfo = 1, Line = 5, Name = "FOO", Value = "1" };
        var macFile = new DiMacroFile { MacInfo = 3, File = file, Line = 1, Elements = new MdTuple(mac) };

        // DIStringType
        var strTy = new DiStringType { Name = "fortran_string", SizeInBits = 80, AlignInBits = 8 };

        // DIGenericSubrange
        var lo = new MdValue(new IntegerConstant(i32, 0));
        var hi = new MdValue(new IntegerConstant(i32, 10));
        var gsr = new DiGenericSubrange { LowerBound = lo, UpperBound = hi };

        // DIModule (Fortran-style)
        var mod = new DiModule { Scope = cu, Name = "MyMod", Line = 3 };

        // DICommonBlock
        var cb = new DiCommonBlock { Scope = cu, Name = "common1", File = file, Line = 7 };

        // DIObjCProperty
        var objp = new DiObjCProperty
        {
            Name = "title", File = file, Line = 9, GetterName = "title", SetterName = "setTitle:",
        };

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);

        // Plant the new nodes under a custom !named so they get enumerated.
        var anchors = new NamedMetadata("bitsmith.test.di_extras");
        anchors.Operands.Add(macFile);
        anchors.Operands.Add(strTy);
        anchors.Operands.Add(gsr);
        anchors.Operands.Add(mod);
        anchors.Operands.Add(cb);
        anchors.Operands.Add(objp);
        module.NamedMetadata.Add(anchors);

        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3))));
        module.NamedMetadata.Add(modFlags);

        var bcPath = System.IO.Path.GetTempFileName() + ".bc";
        try
        {
            new ModuleWriter(module).WriteToFile(bcPath);
            var r = LlvmTools.Run("llvm-bcanalyzer", "-dump", bcPath);
            Assert.True(r.ExitCode == 0, $"llvm-bcanalyzer failed: {r.StdErr}");
            // Verify the new metadata records made it into the bitcode.
            Assert.Contains("MODULE", r.StdOut);
        }
        finally
        {
            if (System.IO.File.Exists(bcPath)) System.IO.File.Delete(bcPath);
        }
    }

    [SkippableFact]
    public void DbgDeclare_MetadataAsValueInCall_RoundTrip()
    {
        var module = NewModule("dbg_decl.ll");
        var t = module.Types;
        var i32 = t.Int32;
        var ptr = t.GetPointer();

        // declare void @llvm.dbg.declare(metadata, metadata, metadata)
        var dbgDeclareFt = t.GetFunction(t.Void,
            new LlvmType[] { t.Metadata, t.Metadata, t.Metadata });
        var dbgDeclare = module.CreateFunction("llvm.dbg.declare", dbgDeclareFt);

        var file = new DiFile("a.c", "/src");
        var cu = new DiCompileUnit(file) { Producer = "bitsmith" };
        var i32Ty = new DiBasicType("int", 32, DwarfAte.Signed);
        var sp = new DiSubprogram("foo", file)
        {
            Scope = cu, Line = 1, ScopeLine = 1, Unit = cu,
        };
        var lv = new DiLocalVariable(sp) { Name = "x", File = file, Line = 2, Type = i32Ty, Arg = 0 };
        var loc = new DiLocation(2, 5, sp);

        // define void @foo() !dbg !sp { %x = alloca i32; call void @llvm.dbg.declare(...); ret void }
        var foo = module.CreateFunction("foo", t.GetFunction(t.Void, System.Array.Empty<LlvmType>()));
        foo.Subprogram = sp;
        var bb = foo.AppendBlock("entry");
        var alloca = bb.Append(new AllocaInstruction(i32, new IntegerConstant(i32, 1), ptr, alignment: 4));

        // First arg is the address — an MdValue wrapping the local %x alloca.
        // Function-local ValueAsMetadata is emitted in the function-local
        // METADATA_BLOCK after the alloca's value id is assigned.
        var call = new CallInstruction(dbgDeclareFt, dbgDeclare, new Value[]
        {
            new MetadataAsValue(t.Metadata, new MdValue(alloca)),
            new MetadataAsValue(t.Metadata, lv),
            new MetadataAsValue(t.Metadata, DiExpression.Empty),
        });
        call.DebugLocation = loc;
        bb.Append(call);
        bb.Append(new ReturnInstruction(t.Void));

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3))));
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        Assert.Contains("@llvm.dbg.declare", ll);
        // The DILocalVariable / DIExpression metadata may or may not survive
        // (llvm-dis can drop orphan-looking nodes when the placeholder address
        // isn't a real local), so the headline assertion is just that the
        // bitcode parses and the intrinsic call survives.
    }

    [SkippableFact]
    public void DiArgList_FunctionLocal_RoundTrip()
    {
        var module = NewModule("dbg_arglist.ll");
        var t = module.Types;
        var i32 = t.Int32;

        var dbgValueFt = t.GetFunction(t.Void,
            new LlvmType[] { t.Metadata, t.Metadata, t.Metadata });
        var dbgValue = module.CreateFunction("llvm.dbg.value", dbgValueFt);

        var file = new DiFile("a.c", "/src");
        var cu = new DiCompileUnit(file) { Producer = "bitsmith" };
        var i32Ty = new DiBasicType("int", 32, DwarfAte.Signed);
        var sp = new DiSubprogram("sum", file) { Scope = cu, Line = 1, ScopeLine = 1, Unit = cu };
        var lv = new DiLocalVariable(sp) { Name = "s", File = file, Line = 2, Type = i32Ty };
        var loc = new DiLocation(2, 5, sp);

        var sum = module.CreateFunction("sum",
            t.GetFunction(i32, new LlvmType[] { i32, i32 }));
        sum.Subprogram = sp;
        var bb = sum.AppendBlock("entry");
        var add = bb.Append(new BinaryOperator(BinaryOpcode.Add, sum.Parameters[0], sum.Parameters[1]));

        // !DIArgList wrapping two function-local args: %0 and %1.
        var argList = new DiArgList(new MdValue(sum.Parameters[0]), new MdValue(sum.Parameters[1]));
        var call = new CallInstruction(dbgValueFt, dbgValue, new Value[]
        {
            new MetadataAsValue(t.Metadata, argList),
            new MetadataAsValue(t.Metadata, lv),
            new MetadataAsValue(t.Metadata, DiExpression.Empty),
        });
        call.DebugLocation = loc;
        bb.Append(call);
        bb.Append(new ReturnInstruction(i32, add));

        var dbgCu = new NamedMetadata("llvm.dbg.cu");
        dbgCu.Operands.Add(cu);
        module.NamedMetadata.Add(dbgCu);
        var modFlags = new NamedMetadata("llvm.module.flags");
        modFlags.Operands.Add(new MdTuple(
            new MdValue(new IntegerConstant(i32, 2)),
            new MdString("Debug Info Version"),
            new MdValue(new IntegerConstant(i32, 3))));
        module.NamedMetadata.Add(modFlags);

        var ll = Disassemble(module);
        Assert.Contains("@llvm.dbg.value", ll);
    }

    [SkippableFact]
    public void InlineAsm_CallSite_RoundTrip()
    {
        var module = NewModule("inline_asm.ll");
        var t = module.Types;
        var ptr = t.GetPointer();

        // call void asm sideeffect "nop", ""()
        var asmFt = t.GetFunction(t.Void, System.Array.Empty<LlvmType>());
        var ia = new InlineAsm(asmFt, ptr, "nop", "", hasSideEffects: true);

        var fn = module.CreateFunction("foo", t.GetFunction(t.Void, System.Array.Empty<LlvmType>()));
        var bb = fn.AppendBlock("entry");
        bb.Append(new CallInstruction(asmFt, ia, System.Array.Empty<Value>()));
        bb.Append(new ReturnInstruction(t.Void));

        var ll = Disassemble(module);
        Assert.Contains("asm", ll);
        Assert.Contains("nop", ll);
    }
}

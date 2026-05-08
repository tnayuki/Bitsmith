# Bitsmith

Pure C# LLVM bitcode writer. No `libllvm` dependency.

- Targets **LLVM 15** (opaque pointers).
- .NET 8 / .NET Standard 2.1.
- Generates `.bc` files readable by `llvm-dis`, `lli`, `llc`.

## Install

```sh
dotnet add package Bitsmith.Llvm
```

## Usage

```csharp
using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;

var module = new Module
{
    SourceFileName = "add.ll",
    TargetTriple = "x86_64-unknown-linux-gnu",
    DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
};
var t = module.Types;
var i32 = t.Int32;

// define i32 @add(i32 %a, i32 %b) { ret i32 add %a, %b }
var fn = module.CreateFunction("add", t.GetFunction(i32, new LlvmType[] { i32, i32 }));
var bb = fn.AppendBlock("entry");
var sum = bb.Append(new BinaryOperator(BinaryOpcode.Add, fn.Parameters[0], fn.Parameters[1]));
bb.Append(new ReturnInstruction(t.Void, sum));

new ModuleWriter(module).WriteToFile("add.bc");
```

`llvm-dis add.bc` reproduces the IR.

## Coverage

The IR model and writer cover the typical LLVM 15 surface for hand-written
or compiler-emitted modules:

- **Types**: all primitives (i1..i64, half/bfloat/float/double/fp128/x86_fp80/ppc_fp128, x86_mmx/x86_amx, token), opaque pointers with address spaces, structs (literal + identified, with forward declaration), arrays, fixed and scalable vectors, function types with vararg.
- **Constants**: integer (incl. wide `ApInt`), float (all precisions), null/undef/poison, aggregate, string/cstring, constant cast expressions (`trunc`/`zext`/`sext`/`fptrunc`/`fpext`/`fptoui`/`fptosi`/`uitofp`/`sitofp`/`ptrtoint`/`inttoptr`/`bitcast`/`addrspacecast`), GEP (`inbounds`), icmp/fcmp, `blockaddress`, inline asm.
- **Globals**: `GlobalVariable`, `GlobalAlias`, `GlobalIFunc`, `comdat`, sections, `dso_local`, `dllstorageclass`, `thread_local`, `unnamed_addr`, prefix/prologue data, personality functions, GC strategy.
- **Attributes**: enum / int / typed (`byval(T)`, `sret(T)`, `elementtype(T)`) / string-form, on parameters, return values, function bodies, and call sites.
- **Instructions**: full set including `fneg`, `freeze`, `va_arg`, `extractvalue`/`insertvalue`, `invoke`/`landingpad`/`resume`, `callbr`, `indirectbr`, funclet EH (`catchpad`/`catchret`/`catchswitch`/`cleanuppad`/`cleanupret`), atomics (`fence`/`atomicrmw`/`cmpxchg`/atomic load+store), GEP, phi, select, vector ops, casts, cmp.
- **Calls**: calling conventions, fast-math flags, callsite attributes, operand bundles, inline asm callees.
- **Debug info**: `DICompileUnit`, `DIFile`, `DISubprogram`, `DISubroutineType`, `DIBasicType`/`DIDerivedType`/`DICompositeType`/`DIStringType`, `DILocation`, `DILexicalBlock`/`DILexicalBlockFile`, `DILocalVariable`/`DIGlobalVariable`/`DIGlobalVariableExpression`, `DISubrange`/`DIGenericSubrange`, `DIEnumerator`, `DITemplateTypeParameter`/`DITemplateValueParameter`, `DINamespace`/`DIImportedEntity`, `DIObjCProperty`, `DIMacro`/`DIMacroFile`, `DIModule`/`DICommonBlock`, `DILabel`, `DIArgList`, `DIExpression`. `llvm.dbg.declare` / `llvm.dbg.value` / `llvm.dbg.label` intrinsics with function-local `ValueAsMetadata`. `METADATA_STRINGS` bundle encoding.
- **Instruction metadata attachments**: `!invariant.load`, `!invariant.group`, `!range`, `!nonnull`, `!alias.scope`, `!noalias`, and other arbitrary kinds via `Instruction.AddAttachment(kindName, md)`. Kind ids are allocated on demand and the per-function `METADATA_ATTACHMENT_BLOCK` mixes the function-level `!dbg` with per-instruction attachments.
- **Bitstream**: full abbreviation support (Fixed/VBR/Array/Char6/Blob), `BLOCKINFO_BLOCK` for shared abbrevs, function-local `VALUE_SYMTAB_BLOCK` and `CONSTANTS_BLOCK`.

## Out of scope

LTO-only and CFI-only features are intentionally not implemented. They aren't
required to produce valid LLVM 15 bitcode that round-trips through
`llvm-dis` / `lli` / `llc`:

- `SYMTAB_BLOCK` / `SUMMARY_BLOCK` / `METADATA_INDEX` (LTO indexing)
- `USELIST_BLOCK` (use-list ordering preservation; only matters if a downstream consumer re-serializes the IR)
- `SYNC_SCOPE_NAMES_BLOCK` (custom named synchscopes)
- `DSOLocalEquivalent` / `NoCFIValue` / `ConstantPtrAuth` constants

There is also no bitcode reader; Bitsmith is writer-only by design.

## Testing

```sh
dotnet test
```

Round-trip integration tests require `llvm-dis` / `llvm-bcanalyzer` from
LLVM 15 on `PATH`. They skip cleanly when the binaries are absent.

## Release

Versioned via [MinVer](https://github.com/adamralph/minver). Pushing a tag
`vX.Y.Z` triggers `.github/workflows/release.yml`, which packs and publishes
to NuGet (`NUGET_API_KEY` secret required).

## License

MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

/// <summary>
/// Serializes a <see cref="Module"/> to LLVM bitcode bytes.
/// </summary>
public sealed class ModuleWriter
{
    /// <summary>
    /// MODULE_CODE_VERSION value. Version 2 corresponds to LLVM IR with
    /// module-level type table (and, in LLVM 15, opaque pointers).
    /// </summary>
    public const uint ModuleVersion = 2;

    private const int IdentificationAbbrevWidth = 5;
    private const int ModuleAbbrevWidth = 3;
    private const int StrtabAbbrevWidth = 3;

    private readonly Module _module;
    private readonly StrtabBuilder _strtab = new();

    public ModuleWriter(Module module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public byte[] Write()
    {
        var w = new BitstreamWriter();
        w.WriteMagicHeader();

        WriteIdentificationBlock(w);
        WriteModuleBlock(w);
        WriteStrtabBlock(w);

        return w.ToArray();
    }

    public void WriteToFile(string path) => File.WriteAllBytes(path, Write());

    private void WriteIdentificationBlock(BitstreamWriter w)
    {
        w.EnterSubBlock(BlockIds.Identification, IdentificationAbbrevWidth);

        // String-record abbrev: [Vbr(6) code, Array, Fixed(8) byte].
        // Used for the Producer string; the reader expands the array length itself.
        var strAbbrev = w.DefineAbbrev(AbbrevOp.Vbr(6), AbbrevOp.Array(), AbbrevOp.Fixed(8));
        WriteAbbrevString(w, strAbbrev, IdentificationCodes.Producer, _module.ProducerString);

        w.WriteUnabbrevRecord(IdentificationCodes.Epoch, IdentificationCodes.CurrentEpoch);
        w.ExitBlock();
    }

    private void WriteModuleBlock(BitstreamWriter w)
    {
        w.EnterSubBlock(BlockIds.Module, ModuleAbbrevWidth);

        // Reuse the same string-record abbrev shape for DataLayout/Triple/SourceFilename/Asm.
        var strAbbrev = w.DefineAbbrev(AbbrevOp.Vbr(6), AbbrevOp.Array(), AbbrevOp.Fixed(8));

        w.WriteUnabbrevRecord(ModuleCodes.Version, ModuleVersion);

        if (!string.IsNullOrEmpty(_module.DataLayout))
            WriteAbbrevString(w, strAbbrev, ModuleCodes.DataLayout, _module.DataLayout);

        if (!string.IsNullOrEmpty(_module.TargetTriple))
            WriteAbbrevString(w, strAbbrev, ModuleCodes.Triple, _module.TargetTriple);

        if (!string.IsNullOrEmpty(_module.SourceFileName))
            WriteAbbrevString(w, strAbbrev, ModuleCodes.SourceFilename, _module.SourceFileName);

        if (!string.IsNullOrEmpty(_module.InlineAsm))
            WriteAbbrevString(w, strAbbrev, ModuleCodes.Asm, _module.InlineAsm);

        TypeTableWriter.Write(w, _module.Types);

        var ve = new ValueEnumerator(_module);

        // PARAMATTR_GROUP_BLOCK and PARAMATTR_BLOCK come before module records
        // so the function/global records can reference attribute-list indices.
        var attrs = new AttributeTableWriter();
        var fnAttrIds = new uint[_module.Functions.Count];
        for (int i = 0; i < _module.Functions.Count; i++)
            fnAttrIds[i] = attrs.Record(_module.Functions[i]);

        // Walk every Call/Invoke in every function and register their callsite
        // attribute sets too, so the function-block writer can reference the
        // resulting list ids.
        var callAttrIds = new Dictionary<Instruction, uint>(ReferenceComparer<Instruction>.Instance);
        foreach (var fn in _module.Functions)
            foreach (var bb in fn.BasicBlocks)
                foreach (var inst in bb.Instructions)
                {
                    uint id = inst switch
                    {
                        CallInstruction c => attrs.Record(c),
                        InvokeInstruction inv => attrs.Record(inv),
                        _ => 0u,
                    };
                    if (id != 0) callAttrIds[inst] = id;
                }

        if (attrs.HasAny) attrs.Write(w);

        // OPERAND_BUNDLE_TAGS_BLOCK — assign 0-based ids to every unique tag
        // seen on any call/invoke. Ids are referenced by the OPERAND_BUNDLE
        // record inside the function block.
        var bundleTagIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var fn in _module.Functions)
            foreach (var bb in fn.BasicBlocks)
                foreach (var inst in bb.Instructions)
                {
                    var bundles = inst switch
                    {
                        CallInstruction c => c.Bundles,
                        InvokeInstruction inv => inv.Bundles,
                        _ => null,
                    };
                    if (bundles is null) continue;
                    foreach (var b in bundles)
                        if (!bundleTagIds.ContainsKey(b.Tag))
                            bundleTagIds[b.Tag] = (uint)bundleTagIds.Count;
                }
        if (bundleTagIds.Count > 0)
        {
            w.EnterSubBlock(BlockIds.OperandBundleTags, 3);
            foreach (var kv in bundleTagIds)
            {
                // OPERAND_BUNDLE_TAG = 1
                var bytes = Encoding.UTF8.GetBytes(kv.Key);
                var ops = new ulong[bytes.Length];
                for (int i = 0; i < bytes.Length; i++) ops[i] = bytes[i];
                w.WriteUnabbrevRecord(1, ops);
            }
            w.ExitBlock();
        }

        // Comdats are referenced from globals/functions by 1-based index.
        var comdatIds = new Dictionary<Comdat, uint>(ReferenceComparer<Comdat>.Instance);
        for (int i = 0; i < _module.Comdats.Count; i++)
        {
            var c = _module.Comdats[i];
            comdatIds[c] = (uint)(i + 1);
            WriteComdatRecord(w, c);
        }

        // Section names and GC strategy names get unique 1-based indices.
        var sectionIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        var gcIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        InternSection(w, sectionIds, _module.Globals.Select(g => g.Section));
        InternSection(w, sectionIds, _module.Functions.Select(f => f.Section));
        InternGc(w, gcIds, _module.Functions.Select(f => f.Gc));

        // GLOBALVAR / FUNCTION / ALIAS / IFUNC records first; the reader assigns
        // value IDs in file order, so the constants block must follow these records
        // to match the writer's enumerator order.
        foreach (var gv in _module.Globals)
            WriteGlobalVarRecord(w, gv, ve, sectionIds, comdatIds);

        for (int i = 0; i < _module.Functions.Count; i++)
            WriteFunctionRecord(w, _module.Functions[i], fnAttrIds[i], ve, sectionIds, gcIds, comdatIds);

        foreach (var a in _module.Aliases)
            WriteAliasRecord(w, a, ve);

        foreach (var ifn in _module.IFuncs)
            WriteIFuncRecord(w, ifn, ve);

        ConstantWriter.WriteModuleConstants(w, ve.ModuleConstants, ve.GetValueId);

        // Function bodies for definitions.
        var fnWriter = new FunctionWriter(w, ve, _module.Types, callAttrIds, bundleTagIds: bundleTagIds);
        foreach (var fn in _module.Functions)
            if (!fn.IsDeclaration)
                fnWriter.Write(fn);

        w.ExitBlock();
    }

    private void WriteComdatRecord(BitstreamWriter w, Comdat c)
    {
        var nameBytes = Encoding.UTF8.GetBytes(c.Name);
        var ops = new ulong[2 + nameBytes.Length];
        ops[0] = (ulong)c.Kind;
        ops[1] = (ulong)nameBytes.Length;
        for (int i = 0; i < nameBytes.Length; i++) ops[2 + i] = nameBytes[i];
        w.WriteUnabbrevRecord(ModuleCodes.Comdat, ops);
    }

    private static void InternSection(BitstreamWriter w, Dictionary<string, uint> table,
        IEnumerable<string?> names)
    {
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n) || table.ContainsKey(n!)) continue;
            table[n!] = (uint)(table.Count + 1);
            WriteStringRecord(w, ModuleCodes.SectionName, n!);
        }
    }

    private static void InternGc(BitstreamWriter w, Dictionary<string, uint> table,
        IEnumerable<string?> names)
    {
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n) || table.ContainsKey(n!)) continue;
            table[n!] = (uint)(table.Count + 1);
            WriteStringRecord(w, ModuleCodes.GcName, n!);
        }
    }

    private void WriteAliasRecord(BitstreamWriter w, GlobalAlias a, ValueEnumerator ve)
    {
        var (offset, size) = _strtab.Add(a.Name);
        var ops = new ulong[]
        {
            (ulong)offset,
            (ulong)size,
            (ulong)a.ValueType.Id,
            (ulong)a.AddressSpace,
            (ulong)ve.GetValueId(a.Aliasee),
            LinkageEncoding.Encode(a.Linkage),
            (ulong)a.Visibility,
            (ulong)a.DllStorageClass,
            (ulong)a.ThreadLocal,
            (ulong)a.UnnamedAddr,
            a.IsDsoLocal ? 1UL : 0UL,
        };
        w.WriteUnabbrevRecord(ModuleCodes.Alias, ops);
    }

    private void WriteIFuncRecord(BitstreamWriter w, GlobalIFunc ifn, ValueEnumerator ve)
    {
        var (offset, size) = _strtab.Add(ifn.Name);
        var ops = new ulong[]
        {
            (ulong)offset,
            (ulong)size,
            (ulong)ifn.ValueType.Id,
            (ulong)ifn.AddressSpace,
            (ulong)ve.GetValueId(ifn.Resolver),
            LinkageEncoding.Encode(ifn.Linkage),
            (ulong)ifn.Visibility,
        };
        w.WriteUnabbrevRecord(ModuleCodes.IFunc, ops);
    }

    /// <summary>Unabbrev fallback for string records (e.g. SectionName/GcName) emitted
    /// outside the local string-record abbrev's scope.</summary>
    private static void WriteStringRecord(BitstreamWriter w, uint code, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ops = new ulong[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) ops[i] = bytes[i];
        w.WriteUnabbrevRecord(code, ops);
    }

    private void WriteGlobalVarRecord(BitstreamWriter w, GlobalVariable gv, ValueEnumerator ve,
        Dictionary<string, uint> sectionIds, Dictionary<Comdat, uint> comdatIds)
    {
        var (offset, size) = _strtab.Add(gv.Name);

        ulong rawFlags = (gv.IsConstant ? 1UL : 0UL)
            | 2UL
            | ((ulong)((PointerType)gv.Type).AddressSpace << 2);

        ulong initId = gv.Initializer is null ? 0UL : (ulong)(ve.GetValueId(gv.Initializer) + 1);
        ulong align = gv.Alignment == 0 ? 0UL : (ulong)(Log2(gv.Alignment) + 1);
        ulong sectionId = (gv.Section is not null && sectionIds.TryGetValue(gv.Section, out var sid)) ? sid : 0UL;
        ulong comdatId = (gv.Comdat is not null && comdatIds.TryGetValue(gv.Comdat, out var cid)) ? cid : 0UL;

        var ops = new ulong[]
        {
            (ulong)offset,
            (ulong)size,
            (ulong)gv.ValueType.Id,
            rawFlags,
            initId,
            LinkageEncoding.Encode(gv.Linkage),
            align,
            sectionId,
            (ulong)gv.Visibility,
            (ulong)gv.ThreadLocal,
            (ulong)gv.UnnamedAddr,
            gv.ExternallyInitialized ? 1UL : 0UL,
            (ulong)gv.DllStorageClass,
            comdatId,
            0,                                  // attributes (not yet wired)
            gv.IsDsoLocal ? 1UL : 0UL,
        };
        w.WriteUnabbrevRecord(ModuleCodes.GlobalVar, ops);
    }

    private static int Log2(uint v)
    {
        int r = 0;
        while ((v >>= 1) != 0) r++;
        return r;
    }

    private void WriteFunctionRecord(BitstreamWriter w, Function fn, uint attrListId,
        ValueEnumerator ve, Dictionary<string, uint> sectionIds, Dictionary<string, uint> gcIds,
        Dictionary<Comdat, uint> comdatIds)
    {
        var (offset, size) = _strtab.Add(fn.Name);

        ulong align = fn.Alignment == 0 ? 0UL : (ulong)(Log2(fn.Alignment) + 1);
        ulong sectionId = (fn.Section is not null && sectionIds.TryGetValue(fn.Section, out var sid)) ? sid : 0UL;
        ulong gcId = (fn.Gc is not null && gcIds.TryGetValue(fn.Gc, out var gid)) ? gid : 0UL;
        ulong comdatId = (fn.Comdat is not null && comdatIds.TryGetValue(fn.Comdat, out var cid)) ? cid : 0UL;
        ulong prefixId = fn.PrefixData is null ? 0UL : (ulong)(ve.GetValueId(fn.PrefixData) + 1);
        ulong prologueId = fn.PrologueData is null ? 0UL : (ulong)(ve.GetValueId(fn.PrologueData) + 1);
        ulong personalityId = fn.Personality is null ? 0UL : (ulong)(ve.GetValueId(fn.Personality) + 1);

        var ops = new ulong[]
        {
            (ulong)offset,
            (ulong)size,
            (ulong)fn.FunctionType.Id,
            fn.CallingConv,
            fn.IsDeclaration ? 1u : 0u,
            LinkageEncoding.Encode(fn.Linkage),
            attrListId,
            align,
            sectionId,
            (ulong)fn.Visibility,
            gcId,
            (ulong)fn.UnnamedAddr,
            prologueId,
            (ulong)fn.DllStorageClass,
            comdatId,
            prefixId,
            personalityId,
            fn.IsDsoLocal ? 1UL : 0UL,
            (ulong)((PointerType)fn.Type).AddressSpace,
        };
        w.WriteUnabbrevRecord(ModuleCodes.Function, ops);
    }

    private void WriteStrtabBlock(BitstreamWriter w)
    {
        w.EnterSubBlock(BlockIds.Strtab, StrtabAbbrevWidth);

        // Define abbrev: [LITERAL STRTAB_BLOB, BLOB]; this is required because
        // the reader fills the Blob field via the abbrev and rejects unabbrev form.
        var blobAbbrev = w.DefineAbbrev(
            AbbrevOp.Literal(StrtabCodes.Blob),
            AbbrevOp.Blob());

        var bytes = _strtab.GetBytes();
        w.WriteBlobAbbrevRecord(blobAbbrev, bytes);

        w.ExitBlock();
    }

    /// <summary>Encodes a string as <c>[code, byte0, byte1, ...]</c> using a
    /// pre-defined <c>[Vbr(6), Array, Fixed(8)]</c> abbrev.</summary>
    private static void WriteAbbrevString(BitstreamWriter w, uint abbrevId, uint code, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var ops = new ulong[bytes.Length + 1];
        ops[0] = code;
        for (int i = 0; i < bytes.Length; i++) ops[i + 1] = bytes[i];
        w.WriteAbbrevRecord(abbrevId, ops);
    }

    private sealed class StrtabBuilder
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, (int offset, int size)> _interned = new();

        public (int offset, int size) Add(string name)
        {
            if (_interned.TryGetValue(name, out var existing))
                return existing;
            var bytes = Encoding.UTF8.GetBytes(name);
            var entry = (offset: _bytes.Count, size: bytes.Length);
            _bytes.AddRange(bytes);
            _interned[name] = entry;
            return entry;
        }

        public byte[] GetBytes() => _bytes.ToArray();
    }
}

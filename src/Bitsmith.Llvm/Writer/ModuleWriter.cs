using System;
using System.Collections.Generic;
using System.IO;
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

        // Module-level value declarations (functions only for m4).
        foreach (var fn in _module.Functions)
            WriteFunctionRecord(w, fn);

        // Function bodies for definitions.
        var ve = new ValueEnumerator(_module);
        var fnWriter = new FunctionWriter(w, ve, _module.Types);
        foreach (var fn in _module.Functions)
            if (!fn.IsDeclaration)
                fnWriter.Write(fn);

        w.ExitBlock();
    }

    private void WriteFunctionRecord(BitstreamWriter w, Function fn)
    {
        var (offset, size) = _strtab.Add(fn.Name);

        // FUNCTION:
        //   [strtab_offset, strtab_size, type, callingconv, isproto, linkage,
        //    paramattr, alignment, section, visibility, gc, unnamed_addr,
        //    prologuedata, dllstorageclass, comdat, prefixdata, personalityfn,
        //    DSO_Local, addrspace]
        var ops = new ulong[]
        {
            (ulong)offset,
            (ulong)size,
            (ulong)fn.FunctionType.Id,
            0,                          // callingconv (C)
            fn.IsDeclaration ? 1u : 0u, // isproto
            LinkageCodes.External,      // linkage
            0,                          // paramattr
            0,                          // alignment
            0,                          // section
            0,                          // visibility
            0,                          // gc
            0,                          // unnamed_addr
            0,                          // prologuedata
            0,                          // dllstorageclass
            0,                          // comdat
            0,                          // prefixdata
            0,                          // personalityfn
            0,                          // DSO_Local
            0,                          // addrspace
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

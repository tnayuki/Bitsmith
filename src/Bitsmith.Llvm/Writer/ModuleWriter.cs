using System;
using System.IO;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

/// <summary>
/// Serializes a <see cref="Module"/> to LLVM bitcode bytes.
/// Currently emits only the file header and an empty module shell
/// (version + datalayout + triple + source_filename).
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

    private readonly Module _module;

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
}

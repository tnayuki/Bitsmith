namespace Bitsmith.Llvm.Codes;

/// <summary>Record codes inside MODULE_BLOCK.</summary>
public static class ModuleCodes
{
    public const uint Version = 1;
    public const uint Triple = 2;
    public const uint DataLayout = 3;
    public const uint Asm = 4;
    public const uint SectionName = 5;
    public const uint DepLib = 6;
    public const uint GlobalVar = 7;
    public const uint Function = 8;
    public const uint AliasOld = 9;
    public const uint GcName = 11;
    public const uint Comdat = 12;
    public const uint VstOffset = 13;
    public const uint Alias = 14;
    public const uint MetadataValuesUnused = 15;
    public const uint SourceFilename = 16;
    public const uint Hash = 17;
    public const uint IFunc = 18;
}

/// <summary>Record codes inside IDENTIFICATION_BLOCK.</summary>
public static class IdentificationCodes
{
    public const uint Producer = 1;
    public const uint Epoch = 2;

    /// <summary>Current bitcode epoch as of LLVM 15.</summary>
    public const uint CurrentEpoch = 0;
}

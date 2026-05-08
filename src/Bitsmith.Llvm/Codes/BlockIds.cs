namespace Bitsmith.Llvm.Codes;

/// <summary>
/// LLVM bitstream block IDs. See llvm/include/llvm/Bitcode/LLVMBitCodes.h.
/// </summary>
public static class BlockIds
{
    public const uint BlockInfo = 0;

    public const uint Module = 8;
    public const uint ParamAttr = 9;
    public const uint ParamAttrGroup = 10;
    public const uint Constants = 11;
    public const uint Function = 12;
    public const uint Identification = 13;
    public const uint ValueSymtab = 14;
    public const uint Metadata = 15;
    public const uint MetadataAttachment = 16;
    public const uint TypeNew = 17;
    public const uint UseList = 18;
    public const uint ModuleStrtab = 19;
    public const uint GlobalvalSummary = 20;
    public const uint OperandBundleTags = 21;
    public const uint MetadataKind = 22;
    public const uint Strtab = 23;
    public const uint FullLtoGlobalvarSummary = 24;
    public const uint Symtab = 25;
    public const uint SyncScopeNames = 26;
}

namespace Bitsmith.Llvm.Codes;

/// <summary>Record codes inside CONSTANTS_BLOCK.</summary>
public static class ConstantCodes
{
    public const uint SetType = 1;       // [typeid]
    public const uint Null = 2;          // [] - null/zeroinitializer of current type
    public const uint Undef = 3;         // []
    public const uint Integer = 4;       // [sign-rotated VBR]
    public const uint WideInteger = 5;   // [n x sign-rotated VBR limbs]
    public const uint Float = 6;         // [bits]
    public const uint Aggregate = 7;     // [n x value-id]
    public const uint String = 8;
    public const uint CString = 9;
    public const uint Cast = 11;
    public const uint Gep = 12;
    public const uint Cmp = 17;
    public const uint InboundsGep = 20;
    public const uint BlockAddress = 21; // [fnTypeId, fnValueId, bbIndex]
    public const uint Poison = 26;
    public const uint InlineAsm = 30;    // [fnTypeId, flags, asmStrLen, asmStr..., conStrLen, conStr...]
}

public enum InlineAsmDialect
{
    ATT = 0,
    Intel = 1,
}

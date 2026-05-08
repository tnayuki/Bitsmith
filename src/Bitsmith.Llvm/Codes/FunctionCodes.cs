namespace Bitsmith.Llvm.Codes;

/// <summary>Record codes inside FUNCTION_BLOCK.</summary>
public static class FunctionCodes
{
    public const uint DeclareBlocks = 1;
    public const uint InstBinOp = 2;
    public const uint InstCast = 3;
    public const uint InstGepOld = 4;
    public const uint InstSelect = 5;
    public const uint InstExtractElt = 6;
    public const uint InstInsertElt = 7;
    public const uint InstShuffleVec = 8;
    public const uint InstCmp = 9;
    public const uint InstRet = 10;
    public const uint InstBr = 11;
    public const uint InstSwitch = 12;
    public const uint InstInvoke = 13;
    public const uint InstUnreachable = 15;
    public const uint InstPhi = 16;
    public const uint InstAlloca = 19;
    public const uint InstLoad = 20;
    public const uint InstStore = 24;
    public const uint InstCall = 34;
    public const uint InstGep = 43;
}

/// <summary>Encoded linkage values from BitcodeWriter.cpp getEncodedLinkage.</summary>
public static class LinkageCodes
{
    public const uint External = 0;
    public const uint WeakAny = 16;
    public const uint Appending = 2;
    public const uint Internal = 3;
    public const uint LinkOnceAny = 18;
    public const uint Private = 9;
}

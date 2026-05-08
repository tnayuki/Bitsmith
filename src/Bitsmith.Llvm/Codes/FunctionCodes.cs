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
    public const uint InstIndirectBr = 31;        // [ptrTy, addrRel, bb1Idx, bb2Idx, ...]
    public const uint InstPhi = 16;
    public const uint InstAlloca = 19;
    public const uint InstLoad = 20;            // [ptr, ty, align, vol] — explicit result type
    public const uint InstStoreOld = 24;
    public const uint InstCmp2 = 28;
    public const uint InstVSelect = 29;
    public const uint InstCall = 34;
    public const uint InstFence = 36;
    public const uint InstLoadAtomic = 41;
    public const uint InstGep = 43;             // [inbounds, srcty, ptr+ty, idx+ty...]
    public const uint InstStore = 44;           // [ptr+ty, val+ty, align, vol]
    public const uint InstStoreAtomic = 45;
    public const uint InstCmpXchg = 46;
    public const uint InstAtomicRmw = 59;

    // Added in M6 amend (codes per LLVM 15 LLVMBitCodes.h).
    public const uint InstVaArg = 23;            // [valisttype, valist, resulttype]
    public const uint InstExtractVal = 26;       // [opval+ty, n x indices]
    public const uint InstInsertVal = 27;        // [aggval+ty, eltval+ty, n x indices]
    public const uint InstResume = 39;           // [opval+ty]
    public const uint InstLandingpad = 47;       // [resty, cleanup, num_clauses, ...] (new form)
    public const uint InstCleanupRet = 48;       // [pad, (bb#)]
    public const uint InstCatchRet = 49;         // [pad, bb#]
    public const uint InstCatchPad = 50;         // [catchswitch, num_args, args...]
    public const uint InstCleanupPad = 51;       // [num_args, args...]
    public const uint InstCatchSwitch = 52;      // [parent, num_handlers, handlers..., (unwind_dest|unwind_to_caller)]
    public const uint OperandBundle = 55;        // [name_id, n x (input+ty)]
    public const uint InstUnOp = 56;             // [opval+ty, opcode, (flags?)]
    public const uint InstCallBr = 57;           // [paramattrs, callFlags, FTy, callee+ty, ...]
    public const uint InstFreeze = 58;           // [opval+ty]
}

/// <summary>Encoded unary opcodes (BitcodeWriter::getEncodedUnaryOpcode).</summary>
public static class UnaryCodes
{
    public const uint FNeg = 0;
}

/// <summary>Encoded cast opcodes (BitcodeWriter::getEncodedCastOpcode).</summary>
public static class CastCodes
{
    public const uint Trunc = 0;
    public const uint ZExt = 1;
    public const uint SExt = 2;
    public const uint FPToUI = 3;
    public const uint FPToSI = 4;
    public const uint UIToFP = 5;
    public const uint SIToFP = 6;
    public const uint FPTrunc = 7;
    public const uint FPExt = 8;
    public const uint PtrToInt = 9;
    public const uint IntToPtr = 10;
    public const uint BitCast = 11;
    public const uint AddrSpaceCast = 12;
}

/// <summary>LLVM CmpInst::Predicate values (compatible with bitcode encoding).</summary>
public static class CmpPredicates
{
    public const uint FcmpFalse = 0;
    public const uint FcmpOeq = 1;
    public const uint FcmpOgt = 2;
    public const uint FcmpOge = 3;
    public const uint FcmpOlt = 4;
    public const uint FcmpOle = 5;
    public const uint FcmpOne = 6;
    public const uint FcmpOrd = 7;
    public const uint FcmpUno = 8;
    public const uint FcmpUeq = 9;
    public const uint FcmpUgt = 10;
    public const uint FcmpUge = 11;
    public const uint FcmpUlt = 12;
    public const uint FcmpUle = 13;
    public const uint FcmpUne = 14;
    public const uint FcmpTrue = 15;
    public const uint IcmpEq = 32;
    public const uint IcmpNe = 33;
    public const uint IcmpUgt = 34;
    public const uint IcmpUge = 35;
    public const uint IcmpUlt = 36;
    public const uint IcmpUle = 37;
    public const uint IcmpSgt = 38;
    public const uint IcmpSge = 39;
    public const uint IcmpSlt = 40;
    public const uint IcmpSle = 41;
}

/// <summary>Atomic memory ordering encoding.</summary>
public static class AtomicOrdering
{
    public const uint NotAtomic = 0;
    public const uint Unordered = 1;
    public const uint Monotonic = 2;
    public const uint Acquire = 3;
    public const uint Release = 4;
    public const uint AcquireRelease = 5;
    public const uint SequentiallyConsistent = 6;
}

/// <summary>Atomic synchronization scope encoding.</summary>
public static class SyncScope
{
    public const uint SingleThread = 0;
    public const uint System = 1;
}

/// <summary>AtomicRMW operations (RMW_*).</summary>
public static class AtomicRmwOps
{
    public const uint Xchg = 0;
    public const uint Add = 1;
    public const uint Sub = 2;
    public const uint And = 3;
    public const uint Nand = 4;
    public const uint Or = 5;
    public const uint Xor = 6;
    public const uint Max = 7;
    public const uint Min = 8;
    public const uint UMax = 9;
    public const uint UMin = 10;
    public const uint FAdd = 11;
    public const uint FSub = 12;
}

/// <summary>Bit positions for INST_CALL flags word.</summary>
public static class CallFlags
{
    public const int Tail = 0;
    public const int Cconv = 1;          // 5 bits
    public const int MustTail = 14;
    public const int ExplicitType = 15;  // must be set with opaque pointers
    public const int NoTail = 16;
    public const int Fmf = 17;
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

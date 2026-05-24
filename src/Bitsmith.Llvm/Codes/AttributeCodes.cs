namespace Bitsmith.Llvm.Codes;

/// <summary>Record codes for PARAMATTR_BLOCK and PARAMATTR_GROUP_BLOCK.</summary>
public static class ParamAttrCodes
{
    public const uint EntryOld = 1;
    public const uint Entry = 2;       // PARAMATTR_BLOCK: list of group ids
    public const uint GrpEntry = 3;    // PARAMATTR_GROUP_BLOCK: group definition

    // Per-attribute "kind tags" emitted inside a group entry record.
    public const uint AttrKindTagEnum = 0;       // [0, attrkind]
    public const uint AttrKindTagInt = 1;        // [1, attrkind, value]
    public const uint AttrKindTagString = 3;     // [3, key..0]
    public const uint AttrKindTagStringValue = 4; // [4, key..0, val..0]
    public const uint AttrKindTagTypeBare = 5;   // [5, attrkind] (rare)
    public const uint AttrKindTagType = 6;       // [6, attrkind, typeid]
}

/// <summary>
/// Stable bitcode encoding for LLVM attribute kinds. Values come from
/// llvm/include/llvm/Bitcode/LLVMBitCodes.h (AttributeKindCodes).
/// </summary>
public static class AttrKindCodes
{
    public const uint Alignment = 1;
    public const uint AlwaysInline = 2;
    public const uint ByVal = 3;
    public const uint InlineHint = 4;
    public const uint InReg = 5;
    public const uint MinSize = 6;
    public const uint Naked = 7;
    public const uint Nest = 8;
    public const uint NoAlias = 9;
    public const uint NoBuiltin = 10;
    public const uint NoCapture = 11;
    public const uint NoDuplicate = 12;
    public const uint NoImplicitFloat = 13;
    public const uint NoInline = 14;
    public const uint NonLazyBind = 15;
    public const uint NoRedZone = 16;
    public const uint NoReturn = 17;
    public const uint NoUnwind = 18;
    public const uint OptimizeForSize = 19;
    public const uint ReadNone = 20;
    public const uint ReadOnly = 21;
    public const uint Returned = 22;
    public const uint ReturnsTwice = 23;
    public const uint SExt = 24;
    public const uint StackAlignment = 25;
    public const uint StackProtect = 26;
    public const uint StackProtectReq = 27;
    public const uint StackProtectStrong = 28;
    public const uint StructRet = 29;
    public const uint SanitizeAddress = 30;
    public const uint SanitizeThread = 31;
    public const uint SanitizeMemory = 32;
    public const uint UwTable = 33;
    public const uint ZExt = 34;
    public const uint Builtin = 35;
    public const uint Cold = 36;
    public const uint OptimizeNone = 37;
    public const uint InAlloca = 38;
    public const uint NonNull = 39;
    public const uint JumpTable = 40;
    public const uint Dereferenceable = 41;
    public const uint DereferenceableOrNull = 42;
    public const uint Convergent = 43;
    public const uint SafeStack = 44;
    public const uint ArgMemOnly = 45;
    public const uint SwiftSelf = 46;
    public const uint SwiftError = 47;
    public const uint NoRecurse = 48;
    public const uint InaccessibleMemOnly = 49;
    public const uint InaccessibleMemOrArgMemOnly = 50;
    public const uint AllocSize = 51;
    public const uint WriteOnly = 52;
    public const uint Speculatable = 53;
    public const uint StrictFp = 54;
    public const uint SanitizeHwAddress = 55;
    public const uint NoCfCheck = 56;
    public const uint OptForFuzzing = 57;
    public const uint ShadowCallStack = 58;
    public const uint SpeculativeLoadHardening = 59;
    public const uint ImmArg = 60;
    public const uint WillReturn = 61;
    public const uint NoFree = 62;
    public const uint NoSync = 63;
    public const uint SanitizeMemTag = 64;
    public const uint Preallocated = 65;
    public const uint NoMerge = 66;
    public const uint NullPointerIsValid = 67;
    public const uint NoUndef = 68;
    public const uint ByRef = 69;
    public const uint MustProgress = 70;
    public const uint NoCallback = 71;
    public const uint Hot = 72;
    public const uint NoProfile = 73;
    public const uint VScaleRange = 74;
    public const uint SwiftAsync = 75;
    public const uint NoSanitizeCoverage = 76;
    public const uint ElementType = 77;
    public const uint DisableSanitizerInstrumentation = 78;
    public const uint NoSanitizeBounds = 79;
    public const uint AllocAlign = 80;
    public const uint AllocatedPointer = 81;
    public const uint AllocKind = 82;
    public const uint PresplitCoroutine = 83;
    public const uint FnretthunkExtern = 84;
}

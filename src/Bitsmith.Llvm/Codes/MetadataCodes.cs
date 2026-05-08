namespace Bitsmith.Llvm.Codes;

/// <summary>Record codes inside METADATA_BLOCK.</summary>
public static class MetadataCodes
{
    public const uint StringOld = 1;
    public const uint Value = 2;
    public const uint Node = 3;
    public const uint Name = 4;
    public const uint DistinctNode = 5;
    public const uint Kind = 6;
    public const uint Location = 7;
    public const uint OldNode = 8;
    public const uint OldFnNode = 9;
    public const uint NamedNode = 10;
    public const uint Attachment = 11;
    public const uint GenericDebug = 12;
    public const uint Subrange = 13;
    public const uint Enumerator = 14;
    public const uint BasicType = 15;
    public const uint File = 16;
    public const uint DerivedType = 17;
    public const uint CompositeType = 18;
    public const uint SubroutineType = 19;
    public const uint CompileUnit = 20;
    public const uint Subprogram = 21;
    public const uint LexicalBlock = 22;
    public const uint LexicalBlockFile = 23;
    public const uint Namespace = 24;
    public const uint TemplateType = 25;
    public const uint TemplateValue = 26;
    public const uint GlobalVar = 27;
    public const uint LocalVar = 28;
    public const uint Expression = 29;
    public const uint ObjcProperty = 30;
    public const uint ImportedEntity = 31;
    public const uint Module = 32;
    public const uint Macro = 33;
    public const uint MacroFile = 34;
    public const uint Strings = 35;
    public const uint GlobalDeclAttachment = 36;
    public const uint GlobalVarExpr = 37;
    public const uint IndexOffset = 38;
    public const uint Index = 39;
    public const uint Label = 40;
    public const uint StringType = 41;
    public const uint CommonBlock = 44;
    public const uint GenericSubrange = 45;
    public const uint ArgList = 46;
}

/// <summary>FUNC_CODE_DEBUG_LOC and friends inside FUNCTION_BLOCK.</summary>
public static class DebugLocCodes
{
    public const uint DebugLoc = 35;        // [line, col, scope, inlinedAt, isImplicitCode]
    public const uint DebugLocAgain = 33;   // []
}

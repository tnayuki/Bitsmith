using System;
using System.Collections.Generic;

namespace Bitsmith.Llvm.IR;

/// <summary>Base class for all metadata nodes (DI nodes, MDStrings, MDTuples, ValueAsMetadata).</summary>
public abstract class Metadata
{
    /// <summary>Index inside the module-level METADATA_BLOCK; assigned during writing.</summary>
    public int Id { get; internal set; } = -1;
}

public sealed class MdString : Metadata
{
    public string Value { get; }
    public MdString(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
}

/// <summary>An <see cref="IR.Value"/> wrapped as metadata operand (ValueAsMetadata).</summary>
public sealed class MdValue : Metadata
{
    public Value Value { get; }
    public MdValue(Value value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
}

/// <summary>Multi-value metadata for `dbg.value(!DIArgList(%a, %b), ...)`.
/// Each argument is a <see cref="MdValue"/> wrapping a SSA value (typically function-local).
/// </summary>
public sealed class DiArgList : Metadata
{
    public IReadOnlyList<MdValue> Args { get; }
    public DiArgList(IReadOnlyList<MdValue> args)
    {
        Args = args ?? throw new ArgumentNullException(nameof(args));
    }
    public DiArgList(params MdValue[] args) : this((IReadOnlyList<MdValue>)args) { }
}

/// <summary>Generic metadata tuple — `!{ ..., ..., ... }`.</summary>
public sealed class MdTuple : Metadata
{
    public bool IsDistinct { get; }
    public IReadOnlyList<Metadata?> Operands { get; }

    public MdTuple(IReadOnlyList<Metadata?> operands, bool isDistinct = false)
    {
        Operands = operands ?? throw new ArgumentNullException(nameof(operands));
        IsDistinct = isDistinct;
    }
    public MdTuple(params Metadata?[] operands) : this((IReadOnlyList<Metadata?>)operands, false) { }

    public static readonly MdTuple Empty = new MdTuple(Array.Empty<Metadata?>());
}

public sealed class DiFile : Metadata
{
    public string Filename { get; }
    public string Directory { get; }
    public DiFile(string filename, string directory)
    {
        Filename = filename ?? throw new ArgumentNullException(nameof(filename));
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }
}

/// <summary>DWARF DW_LANG_* values used in DICompileUnit.</summary>
public static class DwarfLanguage
{
    public const uint C89 = 0x0001;
    public const uint C = 0x0002;
    public const uint C99 = 0x000C;
    public const uint C11 = 0x001D;
    public const uint Cpp = 0x0004;
    public const uint Cpp14 = 0x0021;
}

/// <summary>DICompileUnit::DebugEmissionKind.</summary>
public enum DiEmissionKind { NoDebug = 0, FullDebug = 1, LineTablesOnly = 2, DebugDirectivesOnly = 3 }

/// <summary>DICompileUnit::DebugNameTableKind.</summary>
public enum DiNameTableKind { Default = 0, GNU = 1, None = 2, Apple = 3 }

public sealed class DiCompileUnit : Metadata
{
    public uint SourceLanguage { get; set; } = DwarfLanguage.C99;
    public DiFile File { get; set; }
    public string? Producer { get; set; }
    public bool IsOptimized { get; set; }
    public string? Flags { get; set; }
    public uint RuntimeVersion { get; set; }
    public DiEmissionKind EmissionKind { get; set; } = DiEmissionKind.FullDebug;
    public bool SplitDebugInlining { get; set; }
    public bool DebugInfoForProfiling { get; set; }
    public DiNameTableKind NameTableKind { get; set; } = DiNameTableKind.Default;
    public string? SplitDebugFilename { get; set; }
    public MdTuple? EnumTypes { get; set; }
    public MdTuple? RetainedTypes { get; set; }
    public MdTuple? Globals { get; set; }
    public MdTuple? Imports { get; set; }
    public MdTuple? Macros { get; set; }
    public string? Sysroot { get; set; }
    public string? Sdk { get; set; }
    public bool RangesBaseAddress { get; set; }
    public ulong DwoId { get; set; }

    public DiCompileUnit(DiFile file)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
    }
}

/// <summary>DISubprogram flags (subset).</summary>
[Flags]
public enum DiSpFlags : uint
{
    None = 0,
    Virtual = 1u << 0,
    PureVirtual = 1u << 1,
    LocalToUnit = 1u << 2,
    Definition = 1u << 3,
    Optimized = 1u << 4,
    Pure = 1u << 5,
    Elemental = 1u << 6,
    Recursive = 1u << 7,
}

public sealed class DiSubroutineType : Metadata
{
    /// <summary>Tuple of types. First element is the return type (null = void); remainder are parameter types.</summary>
    public MdTuple Types { get; }
    public DiSubroutineType(MdTuple types)
    {
        Types = types ?? throw new ArgumentNullException(nameof(types));
    }
}

public sealed class DiSubprogram : Metadata
{
    public Metadata? Scope { get; set; }
    public string Name { get; set; }
    public string? LinkageName { get; set; }
    public DiFile File { get; set; }
    public uint Line { get; set; }
    public DiSubroutineType? Type { get; set; }
    public uint ScopeLine { get; set; }
    public DiSpFlags SpFlags { get; set; } = DiSpFlags.Definition;
    public uint Flags { get; set; }
    public DiCompileUnit? Unit { get; set; }
    public MdTuple? RetainedNodes { get; set; }
    public DiSubprogram? Declaration { get; set; }
    public MdTuple? ThrownTypes { get; set; }
    public MdTuple? Annotations { get; set; }
    public string? TargetFuncName { get; set; }
    public Metadata? VirtualIndex { get; set; }
    public uint VirtualIndexValue { get; set; }
    public uint ThisAdjustment { get; set; }

    public DiSubprogram(string name, DiFile file)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        File = file ?? throw new ArgumentNullException(nameof(file));
    }
}

public sealed class DiLocation : Metadata
{
    public uint Line { get; }
    public uint Column { get; }
    public Metadata Scope { get; }
    public DiLocation? InlinedAt { get; set; }
    public bool IsImplicitCode { get; set; }

    public DiLocation(uint line, uint column, Metadata scope)
    {
        Line = line;
        Column = column;
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }
}

/// <summary>DWARF DW_ATE_* encoding for DIBasicType.</summary>
public static class DwarfAte
{
    public const uint Address = 0x01;
    public const uint Boolean = 0x02;
    public const uint Float = 0x04;
    public const uint Signed = 0x05;
    public const uint SignedChar = 0x06;
    public const uint Unsigned = 0x07;
    public const uint UnsignedChar = 0x08;
    public const uint Utf = 0x10;
}

/// <summary>DWARF DW_TAG_* values used by DI nodes.</summary>
public static class DwarfTag
{
    public const uint BaseType = 0x24;
    public const uint PointerType = 0x0F;
    public const uint StructureType = 0x13;
    public const uint UnionType = 0x17;
    public const uint ArrayType = 0x01;
    public const uint EnumerationType = 0x04;
    public const uint Member = 0x0D;
    public const uint Typedef = 0x16;
    public const uint Const = 0x26;
    public const uint LexicalBlock = 0x0B;
    public const uint Subprogram = 0x2E;
    public const uint CompileUnit = 0x11;
    public const uint Variable = 0x34;
}

public sealed class DiBasicType : Metadata
{
    public uint Tag { get; set; } = DwarfTag.BaseType;
    public string Name { get; set; }
    public ulong SizeInBits { get; set; }
    public uint AlignInBits { get; set; }
    public uint Encoding { get; set; }
    public uint Flags { get; set; }
    public DiBasicType(string name, ulong sizeInBits, uint encoding)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        SizeInBits = sizeInBits;
        Encoding = encoding;
    }
}

public sealed class DiDerivedType : Metadata
{
    public uint Tag { get; set; }
    public string? Name { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public Metadata? Scope { get; set; }
    public Metadata? BaseType { get; set; }
    public ulong SizeInBits { get; set; }
    public uint AlignInBits { get; set; }
    public ulong OffsetInBits { get; set; }
    public uint Flags { get; set; }
    public Metadata? ExtraData { get; set; }
    public uint? DwarfAddressSpace { get; set; }
    public DiDerivedType(uint tag) { Tag = tag; }
}

public sealed class DiCompositeType : Metadata
{
    public uint Tag { get; set; }
    public string? Name { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public Metadata? Scope { get; set; }
    public Metadata? BaseType { get; set; }
    public ulong SizeInBits { get; set; }
    public uint AlignInBits { get; set; }
    public ulong OffsetInBits { get; set; }
    public uint Flags { get; set; }
    public MdTuple? Elements { get; set; }
    public uint RuntimeLang { get; set; }
    public Metadata? VTableHolder { get; set; }
    public MdTuple? TemplateParams { get; set; }
    public string? Identifier { get; set; }
    public DiCompositeType(uint tag) { Tag = tag; }
}

public sealed class DiLexicalBlock : Metadata
{
    public Metadata Scope { get; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public uint Column { get; set; }
    public DiLexicalBlock(Metadata scope) { Scope = scope ?? throw new ArgumentNullException(nameof(scope)); }
}

public sealed class DiLocalVariable : Metadata
{
    public Metadata Scope { get; }
    public string? Name { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public Metadata? Type { get; set; }
    public uint Arg { get; set; }
    public uint Flags { get; set; }
    public uint AlignInBits { get; set; }
    public DiLocalVariable(Metadata scope) { Scope = scope ?? throw new ArgumentNullException(nameof(scope)); }
}

public sealed class DiExpression : Metadata
{
    public IReadOnlyList<ulong> Elements { get; }
    public DiExpression(params ulong[] elements) { Elements = elements ?? Array.Empty<ulong>(); }
    public static readonly DiExpression Empty = new DiExpression();
}

/// <summary>Named metadata node — `!llvm.dbg.cu = !{!0, ...}`.</summary>
public sealed class NamedMetadata
{
    public string Name { get; }
    public List<Metadata> Operands { get; } = new();
    public NamedMetadata(string name) { Name = name ?? throw new ArgumentNullException(nameof(name)); }
}

/// <summary>DISubrange — array dimension. Count and lower bound may be ints or DIVariable refs;
/// here we store the simple constant-int form.</summary>
public sealed class DiSubrange : Metadata
{
    public long Count { get; set; }
    public long LowerBound { get; set; }
    public DiSubrange(long count, long lowerBound = 0) { Count = count; LowerBound = lowerBound; }
}

public sealed class DiGenericSubrange : Metadata
{
    public Metadata? Count { get; set; }
    public Metadata? LowerBound { get; set; }
    public Metadata? UpperBound { get; set; }
    public Metadata? Stride { get; set; }
}

public sealed class DiEnumerator : Metadata
{
    public string Name { get; set; }
    public long Value { get; set; }
    public bool IsUnsigned { get; set; }
    public DiEnumerator(string name, long value, bool isUnsigned = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value;
        IsUnsigned = isUnsigned;
    }
}

public sealed class DiTemplateTypeParameter : Metadata
{
    public string? Name { get; set; }
    public Metadata? Type { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class DiTemplateValueParameter : Metadata
{
    public uint Tag { get; set; } = 0x30; // DW_TAG_template_value_parameter
    public string? Name { get; set; }
    public Metadata? Type { get; set; }
    public bool IsDefault { get; set; }
    public Metadata? Value { get; set; }
}

public sealed class DiNamespace : Metadata
{
    public string? Name { get; set; }
    public Metadata? Scope { get; set; }
    public bool ExportSymbols { get; set; }
}

public sealed class DiImportedEntity : Metadata
{
    public uint Tag { get; set; } = 0x3A; // DW_TAG_imported_declaration
    public Metadata? Scope { get; set; }
    public Metadata? Entity { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public string? Name { get; set; }
}

public sealed class DiLexicalBlockFile : Metadata
{
    public Metadata Scope { get; }
    public DiFile? File { get; set; }
    public uint Discriminator { get; set; }
    public DiLexicalBlockFile(Metadata scope) { Scope = scope ?? throw new ArgumentNullException(nameof(scope)); }
}

public sealed class DiLabel : Metadata
{
    public Metadata Scope { get; }
    public string Name { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public DiLabel(Metadata scope, string name)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

public sealed class DiGlobalVariable : Metadata
{
    public Metadata? Scope { get; set; }
    public string? Name { get; set; }
    public string? LinkageName { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public Metadata? Type { get; set; }
    public bool IsLocalToUnit { get; set; }
    public bool IsDefinition { get; set; } = true;
    public Metadata? StaticDataMember { get; set; }
    public Metadata? TemplateParams { get; set; }
    public uint AlignInBits { get; set; }
}

public sealed class DiGlobalVariableExpression : Metadata
{
    public DiGlobalVariable Variable { get; }
    public DiExpression Expression { get; }
    public DiGlobalVariableExpression(DiGlobalVariable variable, DiExpression expression)
    {
        Variable = variable ?? throw new ArgumentNullException(nameof(variable));
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }
}

public sealed class DiObjCProperty : Metadata
{
    public string? Name { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public string? GetterName { get; set; }
    public string? SetterName { get; set; }
    public uint Attributes { get; set; }
    public Metadata? Type { get; set; }
}

public sealed class DiMacro : Metadata
{
    public uint MacInfo { get; set; }
    public uint Line { get; set; }
    public string? Name { get; set; }
    public string? Value { get; set; }
}

public sealed class DiMacroFile : Metadata
{
    public uint MacInfo { get; set; }
    public uint Line { get; set; }
    public DiFile? File { get; set; }
    public Metadata? Elements { get; set; }
}

public sealed class DiModule : Metadata
{
    public Metadata? Scope { get; set; }
    public string? Name { get; set; }
    public string? ConfigMacros { get; set; }
    public string? IncludePath { get; set; }
    public string? ApiNotesFile { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
    public bool IsDecl { get; set; }
}

public sealed class DiCommonBlock : Metadata
{
    public Metadata? Scope { get; set; }
    public Metadata? Decl { get; set; }
    public string? Name { get; set; }
    public DiFile? File { get; set; }
    public uint Line { get; set; }
}

public sealed class DiStringType : Metadata
{
    public uint Tag { get; set; } = 0x12; // DW_TAG_string_type
    public string? Name { get; set; }
    public ulong SizeInBits { get; set; }
    public uint AlignInBits { get; set; }
    public uint Encoding { get; set; }
}

/// <summary>Wraps a metadata operand as an SSA Value. Its bitcode type is the
/// special <see cref="MetadataType"/> and the writer encodes call-site uses by
/// referring to the metadata ID rather than a value-table ID.</summary>
public sealed class MetadataAsValue : Value
{
    public Metadata Metadata { get; }
    private readonly MetadataType _type;
    public MetadataAsValue(MetadataType type, Metadata metadata)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }
    public override LlvmType Type => _type;
}


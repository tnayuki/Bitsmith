using System;

namespace Bitsmith.Llvm.IR;

public enum UnnamedAddrKind
{
    None = 0,
    Unnamed = 1,
    LocalUnnamed = 2,
}

public enum ThreadLocalMode
{
    NotThreadLocal = 0,
    GeneralDynamic = 1,
    LocalDynamic = 2,
    InitialExec = 3,
    LocalExec = 4,
}

public enum Visibility
{
    Default = 0,
    Hidden = 1,
    Protected = 2,
}

/// <summary>
/// Module-level global variable. As a value its type is <c>ptr</c> (in the
/// configured address space); the storage type is <see cref="ValueType"/>.
/// </summary>
public sealed class GlobalVariable : Constant
{
    public string Name { get; }
    public LlvmType ValueType { get; }
    public Constant? Initializer { get; set; }
    /// <summary>!dbg attachment — typically a <see cref="DiGlobalVariableExpression"/>.
    /// Emitted as <c>METADATA_GLOBAL_DECL_ATTACHMENT</c> in the metadata block.</summary>
    public DiGlobalVariableExpression? DebugInfo { get; set; }
    public bool IsConstant { get; set; }
    public Linkage Linkage { get; set; } = Linkage.External;
    public Visibility Visibility { get; set; } = Visibility.Default;
    public UnnamedAddrKind UnnamedAddr { get; set; } = UnnamedAddrKind.None;
    public ThreadLocalMode ThreadLocal { get; set; } = ThreadLocalMode.NotThreadLocal;
    public bool ExternallyInitialized { get; set; }
    /// <summary>Power-of-two alignment in bytes, or 0 to leave unspecified.</summary>
    public uint Alignment { get; set; }
    public DllStorageClass DllStorageClass { get; set; } = DllStorageClass.Default;
    public bool IsDsoLocal { get; set; }
    public string? Section { get; set; }
    public Comdat? Comdat { get; set; }

    private readonly PointerType _ptrType;
    public override LlvmType Type => _ptrType;

    public bool IsDeclaration => Initializer is null;

    public GlobalVariable(string name, LlvmType valueType, PointerType pointerType)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));
    }
}

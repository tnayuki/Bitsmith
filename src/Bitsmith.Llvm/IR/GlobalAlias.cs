using System;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// Module-level alias — gives a second name to an existing global value
/// (function or global variable). As a value its type is <c>ptr</c>.
/// </summary>
public sealed class GlobalAlias : Constant
{
    public string Name { get; }
    public LlvmType ValueType { get; }
    public Value Aliasee { get; set; }
    public Linkage Linkage { get; set; } = Linkage.External;
    public Visibility Visibility { get; set; } = Visibility.Default;
    public UnnamedAddrKind UnnamedAddr { get; set; } = UnnamedAddrKind.None;
    public ThreadLocalMode ThreadLocal { get; set; } = ThreadLocalMode.NotThreadLocal;
    public DllStorageClass DllStorageClass { get; set; } = DllStorageClass.Default;
    public bool IsDsoLocal { get; set; }

    private readonly PointerType _ptrType;
    public override LlvmType Type => _ptrType;
    public int AddressSpace => _ptrType.AddressSpace;

    public GlobalAlias(string name, LlvmType valueType, Value aliasee, PointerType pointerType)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        Aliasee = aliasee ?? throw new ArgumentNullException(nameof(aliasee));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));
    }
}

/// <summary>
/// Module-level indirect function — resolved at load time by calling the resolver.
/// </summary>
public sealed class GlobalIFunc : Constant
{
    public string Name { get; }
    public LlvmType ValueType { get; }
    public Value Resolver { get; set; }
    public Linkage Linkage { get; set; } = Linkage.External;
    public Visibility Visibility { get; set; } = Visibility.Default;
    public UnnamedAddrKind UnnamedAddr { get; set; } = UnnamedAddrKind.None;
    public DllStorageClass DllStorageClass { get; set; } = DllStorageClass.Default;
    public bool IsDsoLocal { get; set; }

    private readonly PointerType _ptrType;
    public override LlvmType Type => _ptrType;
    public int AddressSpace => _ptrType.AddressSpace;

    public GlobalIFunc(string name, LlvmType valueType, Value resolver, PointerType pointerType)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));
    }
}

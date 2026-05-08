using System;
using System.Collections.Generic;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// Module-level function. As a value it has pointer type (LLVM 15 opaque pointer);
/// the underlying function signature is exposed via <see cref="FunctionType"/>.
/// </summary>
public sealed class Function : Constant
{
    public string Name { get; }
    public FunctionType FunctionType { get; }
    public IReadOnlyList<Argument> Parameters { get; }
    public List<BasicBlock> BasicBlocks { get; } = new();

    public Linkage Linkage { get; set; } = Linkage.External;
    public Visibility Visibility { get; set; } = Visibility.Default;
    public UnnamedAddrKind UnnamedAddr { get; set; } = UnnamedAddrKind.None;
    public uint Alignment { get; set; }
    public DllStorageClass DllStorageClass { get; set; } = DllStorageClass.Default;
    public bool IsDsoLocal { get; set; }
    /// <summary>Calling convention encoded value (0 = C). Matches LLVM <c>CallingConv::ID</c>.</summary>
    public uint CallingConv { get; set; }
    public string? Section { get; set; }
    public string? Gc { get; set; }
    public Comdat? Comdat { get; set; }
    public Constant? PrefixData { get; set; }
    public Constant? PrologueData { get; set; }
    public Function? Personality { get; set; }

    /// <summary>Function-level attributes (apply to the function as a whole).</summary>
    public AttributeSet FunctionAttributes { get; } = new();
    /// <summary>Attributes attached to the function's return value.</summary>
    public AttributeSet ReturnAttributes { get; } = new();
    private readonly AttributeSet[] _paramAttrs;
    /// <summary>Attribute set for parameter <paramref name="index"/> (created lazily).</summary>
    public AttributeSet GetParameterAttributes(int index) => _paramAttrs[index];

    private readonly PointerType _ptrType;

    public bool IsDeclaration => BasicBlocks.Count == 0;

    /// <summary>!dbg attachment for the function itself (typically a <see cref="DiSubprogram"/>).</summary>
    public DiSubprogram? Subprogram { get; set; }

    public Function(string name, FunctionType functionType, PointerType pointerType)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        FunctionType = functionType ?? throw new ArgumentNullException(nameof(functionType));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));

        var args = new Argument[functionType.ParameterTypes.Count];
        _paramAttrs = new AttributeSet[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = new Argument(functionType.ParameterTypes[i], i);
            _paramAttrs[i] = new AttributeSet();
        }
        Parameters = args;
    }

    public override LlvmType Type => _ptrType;

    public BasicBlock AppendBlock(string? name = null)
    {
        var bb = new BasicBlock { Name = name, Parent = this };
        BasicBlocks.Add(bb);
        return bb;
    }
}

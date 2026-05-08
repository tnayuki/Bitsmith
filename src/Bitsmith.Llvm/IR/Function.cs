using System;
using System.Collections.Generic;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// Module-level function. As a value it has pointer type (LLVM 15 opaque pointer);
/// the underlying function signature is exposed via <see cref="FunctionType"/>.
/// </summary>
public sealed class Function : Value
{
    public string Name { get; }
    public FunctionType FunctionType { get; }
    public IReadOnlyList<Argument> Parameters { get; }
    public List<BasicBlock> BasicBlocks { get; } = new();

    private readonly PointerType _ptrType;

    public bool IsDeclaration => BasicBlocks.Count == 0;

    public Function(string name, FunctionType functionType, PointerType pointerType)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        FunctionType = functionType ?? throw new ArgumentNullException(nameof(functionType));
        _ptrType = pointerType ?? throw new ArgumentNullException(nameof(pointerType));

        var args = new Argument[functionType.ParameterTypes.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = new Argument(functionType.ParameterTypes[i], i);
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

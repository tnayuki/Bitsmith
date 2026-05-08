using System.Collections.Generic;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// Top-level LLVM module.
/// </summary>
public sealed class Module
{
    public string SourceFileName { get; set; } = "";
    public string TargetTriple { get; set; } = "";
    public string DataLayout { get; set; } = "";
    public string ProducerString { get; set; } = "Bitsmith";
    /// <summary>Module-level inline asm (`module asm "..."`). Empty = none.</summary>
    public string InlineAsm { get; set; } = "";

    public TypeContext Types { get; } = new();

    public List<Function> Functions { get; } = new();

    public Function CreateFunction(string name, FunctionType signature, int addressSpace = 0)
    {
        var fn = new Function(name, signature, Types.GetPointer(addressSpace));
        Functions.Add(fn);
        return fn;
    }
}

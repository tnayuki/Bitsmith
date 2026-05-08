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

    public List<GlobalVariable> Globals { get; } = new();
    public List<Function> Functions { get; } = new();
    public List<GlobalAlias> Aliases { get; } = new();
    public List<GlobalIFunc> IFuncs { get; } = new();
    public List<Comdat> Comdats { get; } = new();

    /// <summary>Module-level metadata. All <see cref="Metadata"/> reachable from any
    /// named metadata, function attachment, or instruction !dbg is included automatically.</summary>
    public List<NamedMetadata> NamedMetadata { get; } = new();

    public Function CreateFunction(string name, FunctionType signature, int addressSpace = 0)
    {
        var fn = new Function(name, signature, Types.GetPointer(addressSpace));
        Functions.Add(fn);
        return fn;
    }

    public GlobalVariable CreateGlobal(string name, LlvmType valueType, int addressSpace = 0)
    {
        var gv = new GlobalVariable(name, valueType, Types.GetPointer(addressSpace));
        Globals.Add(gv);
        return gv;
    }

    public GlobalAlias CreateAlias(string name, LlvmType valueType, Value aliasee, int addressSpace = 0)
    {
        var a = new GlobalAlias(name, valueType, aliasee, Types.GetPointer(addressSpace));
        Aliases.Add(a);
        return a;
    }

    public GlobalIFunc CreateIFunc(string name, LlvmType valueType, Value resolver, int addressSpace = 0)
    {
        var ifn = new GlobalIFunc(name, valueType, resolver, Types.GetPointer(addressSpace));
        IFuncs.Add(ifn);
        return ifn;
    }

    public Comdat CreateComdat(string name, ComdatSelectionKind kind = ComdatSelectionKind.Any)
    {
        var c = new Comdat(name, kind);
        Comdats.Add(c);
        return c;
    }
}

using System;

namespace Bitsmith.Llvm.IR;

/// <summary>Selection rule for comdat groups (LLVM <c>Comdat::SelectionKind</c>).</summary>
public enum ComdatSelectionKind
{
    Any = 0,
    ExactMatch = 1,
    Largest = 2,
    NoDuplicates = 3,
    SameSize = 4,
}

public sealed class Comdat
{
    public string Name { get; }
    public ComdatSelectionKind Kind { get; set; }
    public Comdat(string name, ComdatSelectionKind kind = ComdatSelectionKind.Any)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        Name = name;
        Kind = kind;
    }
}

public enum DllStorageClass
{
    Default = 0,
    DllImport = 1,
    DllExport = 2,
}

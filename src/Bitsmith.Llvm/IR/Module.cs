namespace Bitsmith.Llvm.IR;

/// <summary>
/// Top-level LLVM module. Currently holds only header-level fields.
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
}

namespace Bitsmith.Llvm.IR;

/// <summary>
/// Base type for anything that can be used as an SSA operand: arguments, instructions, constants, globals.
/// </summary>
public abstract class Value
{
    public abstract LlvmType Type { get; }
}

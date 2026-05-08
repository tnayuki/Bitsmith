using System.Collections.Generic;

namespace Bitsmith.Llvm.IR;

public sealed class BasicBlock
{
    public string? Name { get; set; }
    public Function? Parent { get; internal set; }
    public List<Instruction> Instructions { get; } = new();

    public T Append<T>(T instruction) where T : Instruction
    {
        instruction.Parent = this;
        Instructions.Add(instruction);
        return instruction;
    }
}

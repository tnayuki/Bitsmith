namespace Bitsmith.Llvm.IR;

/// <summary>
/// A function parameter.
/// </summary>
public sealed class Argument : Value
{
    private readonly LlvmType _type;
    public override LlvmType Type => _type;
    public string? Name { get; set; }
    public int Index { get; }

    internal Argument(LlvmType type, int index)
    {
        _type = type;
        Index = index;
    }
}

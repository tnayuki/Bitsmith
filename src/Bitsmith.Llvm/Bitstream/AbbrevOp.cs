namespace Bitsmith.Llvm.Bitstream;

public enum AbbrevOpKind { Literal, Fixed, Vbr, Array, Char6, Blob }

/// <summary>
/// Single operand descriptor in an abbreviation definition.
/// </summary>
public readonly struct AbbrevOp
{
    public AbbrevOpKind Kind { get; }
    /// <summary>Literal value (for Literal) or width in bits (for Fixed/Vbr).</summary>
    public ulong Value { get; }

    private AbbrevOp(AbbrevOpKind kind, ulong value) { Kind = kind; Value = value; }

    public static AbbrevOp Literal(ulong value) => new(AbbrevOpKind.Literal, value);
    public static AbbrevOp Fixed(int width) => new(AbbrevOpKind.Fixed, (ulong)width);
    public static AbbrevOp Vbr(int width) => new(AbbrevOpKind.Vbr, (ulong)width);
    public static AbbrevOp Array() => new(AbbrevOpKind.Array, 0);
    public static AbbrevOp Char6() => new(AbbrevOpKind.Char6, 0);
    public static AbbrevOp Blob() => new(AbbrevOpKind.Blob, 0);
}

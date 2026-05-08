using Bitsmith.Llvm.IR;
using Xunit;

namespace Bitsmith.Llvm.Tests;

/// <summary>
/// Unit tests for the eager arity / type-mismatch checks on
/// CallInstruction (no llvm-dis required).
/// </summary>
public class CallValidationTests
{
    [Fact]
    public void Call_ArityMismatch_TooFewArgs_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32, t.Int32 });
        var callee = mod.CreateFunction("callee", ft);

        var ex = Assert.Throws<ArgumentException>(() =>
            new CallInstruction(ft, callee, new Value[] { new IntegerConstant(t.Int32, 1) }));
        Assert.Contains("arity mismatch", ex.Message);
        Assert.Contains("@callee", ex.Message);
    }

    [Fact]
    public void Call_ArityMismatch_TooManyArgs_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32 });
        var callee = mod.CreateFunction("callee", ft);

        var ex = Assert.Throws<ArgumentException>(() =>
            new CallInstruction(ft, callee, new Value[]
            {
                new IntegerConstant(t.Int32, 1),
                new IntegerConstant(t.Int32, 2),
            }));
        Assert.Contains("arity mismatch", ex.Message);
    }

    [Fact]
    public void Call_VarArgWithFewerThanFixed_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32, t.Int32 }, isVarArg: true);
        var callee = mod.CreateFunction("printf_like", ft);

        var ex = Assert.Throws<ArgumentException>(() =>
            new CallInstruction(ft, callee, new Value[] { new IntegerConstant(t.Int32, 1) }));
        Assert.Contains("vararg", ex.Message);
    }

    [Fact]
    public void Call_VarArgWithExtraArgs_DoesNotThrow()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32 }, isVarArg: true);
        var callee = mod.CreateFunction("printf_like", ft);

        // Two args beyond the fixed param is legal for vararg.
        _ = new CallInstruction(ft, callee, new Value[]
        {
            new IntegerConstant(t.Int32, 1),
            new IntegerConstant(t.Int32, 2),
            new IntegerConstant(t.Int32, 3),
        });
    }

    [Fact]
    public void Call_TypeMismatch_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        // Callee wants i32 but we pass an i64.
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32 });
        var callee = mod.CreateFunction("callee", ft);

        var ex = Assert.Throws<ArgumentException>(() =>
            new CallInstruction(ft, callee, new Value[] { new IntegerConstant(t.Int64, 1) }));
        Assert.Contains("type mismatch", ex.Message);
    }
}

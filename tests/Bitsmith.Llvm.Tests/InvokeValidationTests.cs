using System;
using Bitsmith.Llvm.IR;
using Xunit;

namespace Bitsmith.Llvm.Tests;

/// <summary>
/// Unit tests for the eager arity / type-mismatch checks on
/// InvokeInstruction. Parallel to <see cref="CallValidationTests"/>.
/// </summary>
public class InvokeValidationTests
{
    [Fact]
    public void Invoke_ArityMismatch_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32, t.Int32 });
        var callee = mod.CreateFunction("callee", ft);

        var fn = mod.CreateFunction("caller",
            t.GetFunction(t.Void, Array.Empty<LlvmType>()));
        var normal = fn.AppendBlock("normal");
        var unwind = fn.AppendBlock("unwind");

        var ex = Assert.Throws<ArgumentException>(() =>
            new InvokeInstruction(ft, callee,
                new Value[] { new IntegerConstant(t.Int32, 1) },
                normal, unwind));
        Assert.Contains("arity mismatch", ex.Message);
        Assert.Contains("@callee", ex.Message);
    }

    [Fact]
    public void Invoke_VarArgBelowFixedCount_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32, t.Int32 }, isVarArg: true);
        var callee = mod.CreateFunction("printf_like", ft);

        var fn = mod.CreateFunction("caller",
            t.GetFunction(t.Void, Array.Empty<LlvmType>()));
        var normal = fn.AppendBlock("normal");
        var unwind = fn.AppendBlock("unwind");

        var ex = Assert.Throws<ArgumentException>(() =>
            new InvokeInstruction(ft, callee,
                new Value[] { new IntegerConstant(t.Int32, 1) },
                normal, unwind));
        Assert.Contains("vararg", ex.Message);
    }

    [Fact]
    public void Invoke_TypeMismatch_Throws()
    {
        var mod = new Module();
        var t = mod.Types;
        var ft = t.GetFunction(t.Int32, new LlvmType[] { t.Int32 });
        var callee = mod.CreateFunction("callee", ft);

        var fn = mod.CreateFunction("caller",
            t.GetFunction(t.Void, Array.Empty<LlvmType>()));
        var normal = fn.AppendBlock("normal");
        var unwind = fn.AppendBlock("unwind");

        var ex = Assert.Throws<ArgumentException>(() =>
            new InvokeInstruction(ft, callee,
                new Value[] { new IntegerConstant(t.Int64, 1) },
                normal, unwind));
        Assert.Contains("type mismatch", ex.Message);
    }
}

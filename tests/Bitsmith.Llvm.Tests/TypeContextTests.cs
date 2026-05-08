using Bitsmith.Llvm.IR;
using Xunit;

namespace Bitsmith.Llvm.Tests;

public class TypeContextTests
{
    [Fact]
    public void Primitives_AreInsertedFirstAndIndexed()
    {
        var ctx = new TypeContext();

        Assert.Equal(0, ctx.Void.Id);
        Assert.Equal(1, ctx.Float.Id);
        Assert.Equal(2, ctx.Double.Id);
        Assert.Equal(3, ctx.Label.Id);
        Assert.Equal(4, ctx.Metadata.Id);
    }

    [Fact]
    public void GetInteger_DeduplicatesByWidth()
    {
        var ctx = new TypeContext();
        var a = ctx.GetInteger(32);
        var b = ctx.GetInteger(32);
        Assert.Same(a, b);
        Assert.Equal(32, a.BitWidth);
    }

    [Fact]
    public void GetPointer_DistinctPerAddressSpace()
    {
        var ctx = new TypeContext();
        var p0 = ctx.GetPointer(0);
        var p1 = ctx.GetPointer(1);
        Assert.NotSame(p0, p1);
        Assert.Same(p0, ctx.GetPointer(0));
    }

    [Fact]
    public void GetFunction_DeduplicatesStructurallyEqualSignatures()
    {
        var ctx = new TypeContext();
        var i32 = ctx.Int32;
        var f1 = ctx.GetFunction(i32, new[] { i32, i32 });
        var f2 = ctx.GetFunction(i32, new[] { i32, i32 });
        Assert.Same(f1, f2);
    }

    [Fact]
    public void NamedStruct_IsAlwaysDistinct()
    {
        var ctx = new TypeContext();
        var s1 = ctx.CreateNamedStruct("S", new[] { (LlvmType)ctx.Int32 });
        var s2 = ctx.CreateNamedStruct("S", new[] { (LlvmType)ctx.Int32 });
        Assert.NotSame(s1, s2);
    }
}

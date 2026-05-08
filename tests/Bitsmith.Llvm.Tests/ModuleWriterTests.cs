using Bitsmith.Llvm.IR;
using Bitsmith.Llvm.Writer;
using Xunit;

namespace Bitsmith.Llvm.Tests;

public class ModuleWriterTests
{
    [Fact]
    public void Write_StartsWithMagicHeader()
    {
        var bytes = new ModuleWriter(new Module()).Write();

        Assert.True(bytes.Length >= 4);
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'C', bytes[1]);
        Assert.Equal(0xC0, bytes[2]);
        Assert.Equal(0xDE, bytes[3]);
    }

    [Fact]
    public void Write_OutputIsWordAligned()
    {
        var bytes = new ModuleWriter(new Module
        {
            SourceFileName = "hello.c",
            TargetTriple = "x86_64-unknown-linux-gnu",
            DataLayout = "e-m:e-p:64:64-i64:64-n8:16:32:64-S128",
        }).Write();

        Assert.Equal(0, bytes.Length % 4);
    }
}

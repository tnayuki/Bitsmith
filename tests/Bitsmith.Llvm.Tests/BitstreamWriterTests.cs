using Bitsmith.Llvm.Bitstream;
using Xunit;

namespace Bitsmith.Llvm.Tests;

public class BitstreamWriterTests
{
    [Fact]
    public void WriteBits_PacksLsbFirst()
    {
        var w = new BitstreamWriter();
        w.WriteBits(0b1, 1);
        w.WriteBits(0b10, 2);
        w.WriteBits(0b101, 3);
        w.WriteBits(0b11, 2);
        var bytes = w.ToArray();
        Assert.Single(bytes);
        Assert.Equal(0b11_101_10_1, bytes[0]);
    }

    [Fact]
    public void WriteVBR_SmallValueFitsInOneChunk()
    {
        var w = new BitstreamWriter();
        w.WriteVBR(3, 4);
        var bytes = w.ToArray();
        Assert.Equal(0b0011, bytes[0] & 0x0F);
    }

    [Fact]
    public void WriteVBR_LargeValueSpansChunks()
    {
        var w = new BitstreamWriter();
        // VBR4 encoding of 27 (0b11011): chunk1 = 1011 (1=cont, low3=011), chunk2 = 0011 (0=stop, 011)
        w.WriteVBR(27, 4);
        var bytes = w.ToArray();
        Assert.Equal(0b0011_1011, bytes[0]);
    }

    [Fact]
    public void DefineAbbrev_AssignsIdsStartingAtFour()
    {
        var w = new BitstreamWriter();
        w.WriteMagicHeader();
        w.EnterSubBlock(BitstreamWriter.FirstApplicationBlockId, 4);
        var id1 = w.DefineAbbrev(AbbrevOp.Fixed(8));
        var id2 = w.DefineAbbrev(AbbrevOp.Vbr(6));
        w.ExitBlock();

        Assert.Equal(4u, id1);
        Assert.Equal(5u, id2);
    }

    [Fact]
    public void Abbrev_FixedAndArrayRoundTripStructure()
    {
        // Define an abbrev: [Literal(7), Fixed(8), Array, Vbr(4)]
        // Then emit a record with 3 array elements. The output should be parseable
        // by stepping through the byte stream — we don't verify exact bits here,
        // just that the writer doesn't throw and produces a non-empty stream.
        var w = new BitstreamWriter();
        w.WriteMagicHeader();
        w.EnterSubBlock(BitstreamWriter.FirstApplicationBlockId, 4);
        var id = w.DefineAbbrev(AbbrevOp.Literal(7), AbbrevOp.Fixed(8), AbbrevOp.Array(), AbbrevOp.Vbr(4));
        w.WriteAbbrevRecord(id, 0xAB, 1, 2, 3);
        w.ExitBlock();

        var bytes = w.ToArray();
        Assert.True(bytes.Length > 4);
    }

    [Fact]
    public void BlockInfo_AbbrevAvailableInLaterBlocksOfSameId()
    {
        var w = new BitstreamWriter();
        w.WriteMagicHeader();

        const uint customBid = BitstreamWriter.FirstApplicationBlockId + 1;

        // Register an abbrev for `customBid` in BLOCKINFO. The id assigned is "next id"
        // at the moment of DefineAbbrev, which is 4 (no in-block-info abbrevs yet).
        w.EnterBlockInfoBlock();
        w.SetBlockInfoCurrentBlockId(customBid);
        var biAbbrevId = w.DefineAbbrev(AbbrevOp.Fixed(8));
        w.ExitBlock();

        // The same id should be available inside any later instance of customBid
        // (no need to re-DefineAbbrev locally).
        w.EnterSubBlock(customBid, 4);
        w.WriteAbbrevRecord(biAbbrevId, 0x42);
        w.ExitBlock();

        var bytes = w.ToArray();
        Assert.True(bytes.Length > 8);
    }
}

using System;
using System.Collections.Generic;

namespace Bitsmith.Llvm.Bitstream;

/// <summary>
/// LSB-first bit packer for LLVM bitstream output.
/// Supports nested blocks, abbreviation definitions, abbrev-driven record emission,
/// and the standard <c>BLOCKINFO</c> block (block id 0) for sharing abbreviations
/// across every instance of a given application block id.
/// </summary>
public sealed class BitstreamWriter
{
    // Standard built-in abbrev IDs (defined by the bitstream format).
    public const uint EndBlockAbbrevId = 0;
    public const uint EnterSubBlockAbbrevId = 1;
    public const uint DefineAbbrevAbbrevId = 2;
    public const uint UnabbrevRecordAbbrevId = 3;

    // Standard block IDs used at the top of the file.
    public const uint BlockInfoBlockId = 0;
    public const uint FirstApplicationBlockId = 8;

    // Standard BLOCKINFO record codes.
    public const uint BlockInfoCodeSetBid = 1;
    public const uint BlockInfoCodeBlockName = 2;
    public const uint BlockInfoCodeSetRecordName = 3;

    // Width of the abbrev id at the file's top-level scope.
    private const int TopLevelAbbrevWidth = 2;

    // First user-defined abbreviation ID (0..3 are built-in).
    private const uint FirstUserAbbrevId = 4;

    private readonly List<byte> _buffer = new();
    private ulong _current;
    private int _bitsInCurrent;

    private readonly Stack<BlockFrame> _blockStack = new();
    private int _abbrevWidth = TopLevelAbbrevWidth;
    private uint _nextAbbrevId = FirstUserAbbrevId;

    /// <summary>Abbrev definitions in scope for the current block. The first
    /// <see cref="FirstUserAbbrevId"/> slots are unused (built-in IDs); user-defined
    /// abbrevs follow in declaration order.</summary>
    private List<AbbrevOp[]> _currentAbbrevs = new();

    /// <summary>BLOCKINFO-defined abbrev tables keyed by block id. When a sub-block
    /// of that id is entered, its abbrev table is pre-populated from this map.</summary>
    private readonly Dictionary<uint, List<AbbrevOp[]>> _blockInfoAbbrevs = new();

    /// <summary>Block id targeted by abbrev definitions inside the BLOCKINFO block
    /// (set via <see cref="SetBlockInfoCurrentBlockId"/>).</summary>
    private uint? _blockInfoCurrentBid;

    public long BitPosition { get; private set; }

    public int CurrentAbbrevWidth => _abbrevWidth;

    public void WriteBits(ulong value, int numBits)
    {
        if (numBits <= 0 || numBits > 32)
            throw new ArgumentOutOfRangeException(nameof(numBits));
        if (numBits < 64 && (value >> numBits) != 0)
            throw new ArgumentOutOfRangeException(nameof(value), "value does not fit in numBits");

        _current |= value << _bitsInCurrent;
        _bitsInCurrent += numBits;
        BitPosition += numBits;

        while (_bitsInCurrent >= 8)
        {
            _buffer.Add((byte)(_current & 0xFF));
            _current >>= 8;
            _bitsInCurrent -= 8;
        }
    }

    /// <summary>
    /// Writes a 64-bit value by chunking into &lt;=32-bit pieces.
    /// </summary>
    public void WriteBits64(ulong value, int numBits)
    {
        if (numBits <= 32)
        {
            WriteBits(value, numBits);
            return;
        }
        WriteBits(value & 0xFFFFFFFFUL, 32);
        WriteBits(value >> 32, numBits - 32);
    }

    /// <summary>
    /// Variable Bit Rate encoding: emit chunks of (numBits-1) data bits + 1 continuation bit.
    /// </summary>
    public void WriteVBR(ulong value, int numBits)
    {
        if (numBits < 2 || numBits > 32)
            throw new ArgumentOutOfRangeException(nameof(numBits));

        ulong threshold = 1UL << (numBits - 1);
        while (value >= threshold)
        {
            ulong chunk = (value & (threshold - 1)) | threshold;
            WriteBits(chunk, numBits);
            value >>= numBits - 1;
        }
        WriteBits(value, numBits);
    }

    public void WriteVBR64(ulong value, int numBits)
    {
        if (numBits < 2 || numBits > 32)
            throw new ArgumentOutOfRangeException(nameof(numBits));

        ulong threshold = 1UL << (numBits - 1);
        while (value >= threshold)
        {
            ulong chunk = (value & (threshold - 1)) | threshold;
            WriteBits(chunk, numBits);
            value >>= numBits - 1;
        }
        WriteBits((uint)value, numBits);
    }

    public void FlushToByte()
    {
        if (_bitsInCurrent > 0)
        {
            _buffer.Add((byte)(_current & 0xFF));
            _current = 0;
            _bitsInCurrent = 0;
            BitPosition = (BitPosition + 7) & ~7L;
        }
    }

    public void FlushToWord()
    {
        FlushToByte();
        while (_buffer.Count % 4 != 0)
            _buffer.Add(0);
        BitPosition = _buffer.Count * 8L;
    }

    public byte[] ToArray()
    {
        FlushToByte();
        return _buffer.ToArray();
    }

    /// <summary>
    /// Writes the LLVM bitcode magic header: 'BC' 0xC0 0xDE.
    /// Must be called at byte/word boundary before any blocks.
    /// </summary>
    public void WriteMagicHeader()
    {
        if (BitPosition != 0)
            throw new InvalidOperationException("magic header must be at the start of the stream");
        WriteBits('B', 8);
        WriteBits('C', 8);
        WriteBits(0xC0, 8);
        WriteBits(0xDE, 8);
    }

    private void WriteAbbrevId(uint id) => WriteBits(id, _abbrevWidth);

    /// <summary>
    /// Enters a sub-block. Emits ENTER_SUBBLOCK [BlockID:VBR8, NewAbbrevWidth:VBR4, &lt;align32&gt;, BlockLen:32].
    /// The block length is back-patched when ExitBlock is called.
    /// </summary>
    public void EnterSubBlock(uint blockId, int newAbbrevWidth)
    {
        if (newAbbrevWidth < 1 || newAbbrevWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(newAbbrevWidth));

        WriteAbbrevId(EnterSubBlockAbbrevId);
        WriteVBR(blockId, 8);
        WriteVBR((uint)newAbbrevWidth, 4);
        FlushToWord();

        // Reserve a 32-bit slot for block length (in 32-bit words, excluding header).
        int lengthWordPos = _buffer.Count;
        WriteBits(0, 32);

        _blockStack.Push(new BlockFrame(blockId, _abbrevWidth, lengthWordPos, _nextAbbrevId, _currentAbbrevs));
        _abbrevWidth = newAbbrevWidth;

        // Seed this block's abbrev table from any BLOCKINFO entries for its id.
        // Built-in slots (0..3) are placeholders — never indexed.
        var seeded = new List<AbbrevOp[]>((int)FirstUserAbbrevId);
        for (int i = 0; i < FirstUserAbbrevId; i++) seeded.Add(Array.Empty<AbbrevOp>());
        if (_blockInfoAbbrevs.TryGetValue(blockId, out var biAbbrevs))
            seeded.AddRange(biAbbrevs);
        _currentAbbrevs = seeded;
        _nextAbbrevId = (uint)_currentAbbrevs.Count;
    }

    /// <summary>
    /// Enters the BLOCKINFO sub-block (id 0). Inside this block, call
    /// <see cref="SetBlockInfoCurrentBlockId"/> followed by <see cref="DefineAbbrev"/> to register
    /// abbrevs that will be available in every subsequent block of that id.
    /// </summary>
    public void EnterBlockInfoBlock(int abbrevWidth = 3)
    {
        EnterSubBlock(BlockInfoBlockId, abbrevWidth);
        _blockInfoCurrentBid = null;
    }

    /// <summary>
    /// Inside BLOCKINFO: switches the target block id for following <see cref="DefineAbbrev"/>
    /// calls. Emits a <c>SETBID</c> record.
    /// </summary>
    public void SetBlockInfoCurrentBlockId(uint blockId)
    {
        if (_blockStack.Count == 0 || _blockStack.Peek().BlockId != BlockInfoBlockId)
            throw new InvalidOperationException("SetBlockInfoCurrentBlockId only valid inside BLOCKINFO");
        WriteUnabbrevRecord(BlockInfoCodeSetBid, blockId);
        _blockInfoCurrentBid = blockId;
    }

    /// <summary>
    /// Exits the current sub-block, back-patching its length and restoring the parent abbrev width.
    /// </summary>
    public void ExitBlock()
    {
        if (_blockStack.Count == 0)
            throw new InvalidOperationException("ExitBlock called without matching EnterSubBlock");

        WriteAbbrevId(EndBlockAbbrevId);
        FlushToWord();

        var frame = _blockStack.Pop();
        _abbrevWidth = frame.ParentAbbrevWidth;
        _nextAbbrevId = frame.ParentNextAbbrevId;
        _currentAbbrevs = frame.ParentAbbrevs;

        if (frame.BlockId == BlockInfoBlockId)
            _blockInfoCurrentBid = null;

        // Block length = number of 32-bit words from end-of-header to current position.
        int blockLenWords = (_buffer.Count - (frame.LengthWordPos + 4)) / 4;
        BitConverterLE.WriteUInt32(_buffer, frame.LengthWordPos, (uint)blockLenWords);
    }

    /// <summary>
    /// Emits an UNABBREV_RECORD: [code, numops, op0, op1, ...] each VBR6.
    /// </summary>
    public void WriteUnabbrevRecord(uint code, ReadOnlySpan<ulong> operands)
    {
        WriteAbbrevId(UnabbrevRecordAbbrevId);
        WriteVBR(code, 6);
        WriteVBR((uint)operands.Length, 6);
        for (int i = 0; i < operands.Length; i++)
            WriteVBR64(operands[i], 6);
    }

    public void WriteUnabbrevRecord(uint code, params ulong[] operands)
        => WriteUnabbrevRecord(code, (ReadOnlySpan<ulong>)operands);

    public void WriteUnabbrevRecord(uint code) => WriteUnabbrevRecord(code, ReadOnlySpan<ulong>.Empty);

    public void WriteUnabbrevRecord(uint code, ulong op0)
    {
        WriteAbbrevId(UnabbrevRecordAbbrevId);
        WriteVBR(code, 6);
        WriteVBR(1u, 6);
        WriteVBR64(op0, 6);
    }

    /// <summary>
    /// Defines an abbreviation in the current block. Returns the new abbrev id
    /// (starting at <see cref="FirstUserAbbrevId"/> for the first user-defined abbrev within a block).
    ///
    /// Inside the BLOCKINFO block, the abbreviation is registered against the block id
    /// last set via <see cref="SetBlockInfoCurrentBlockId"/> and applies to every future
    /// instance of that block.
    /// </summary>
    public uint DefineAbbrev(params AbbrevOp[] ops)
    {
        if (ops is null || ops.Length == 0)
            throw new ArgumentException("abbreviation must have at least one operand", nameof(ops));

        WriteAbbrevId(DefineAbbrevAbbrevId);
        WriteVBR((uint)ops.Length, 5);
        foreach (var op in ops)
        {
            if (op.Kind == AbbrevOpKind.Literal)
            {
                WriteBits(1, 1);
                WriteVBR64(op.Value, 8);
            }
            else
            {
                WriteBits(0, 1);
                uint enc = op.Kind switch
                {
                    AbbrevOpKind.Fixed => 1,
                    AbbrevOpKind.Vbr => 2,
                    AbbrevOpKind.Array => 3,
                    AbbrevOpKind.Char6 => 4,
                    AbbrevOpKind.Blob => 5,
                    _ => throw new InvalidOperationException(),
                };
                WriteBits(enc, 3);
                if (op.Kind == AbbrevOpKind.Fixed || op.Kind == AbbrevOpKind.Vbr)
                    WriteVBR(op.Value, 5);
            }
        }

        // If we're inside BLOCKINFO with a SETBID, route this abbrev to the global table
        // for that block id; otherwise it's local to the current block.
        if (_blockStack.Count > 0 && _blockStack.Peek().BlockId == BlockInfoBlockId
            && _blockInfoCurrentBid is uint bid)
        {
            if (!_blockInfoAbbrevs.TryGetValue(bid, out var list))
                _blockInfoAbbrevs[bid] = list = new List<AbbrevOp[]>();
            list.Add(ops);
        }
        else
        {
            _currentAbbrevs.Add(ops);
        }

        return _nextAbbrevId++;
    }

    /// <summary>
    /// Emits a record using a previously-defined abbreviation. Operands are encoded
    /// according to the abbrev's <see cref="AbbrevOp"/> sequence; the sequence may
    /// expand or contract operand counts (Array consumes a length + element values,
    /// Blob consumes a byte buffer aligned to a 32-bit boundary).
    /// </summary>
    public void WriteAbbrevRecord(uint abbrevId, ReadOnlySpan<ulong> operands)
    {
        if (abbrevId < FirstUserAbbrevId || abbrevId >= _currentAbbrevs.Count)
            throw new ArgumentOutOfRangeException(nameof(abbrevId), $"unknown abbrev id {abbrevId}");
        var def = _currentAbbrevs[(int)abbrevId];

        WriteAbbrevId(abbrevId);

        int opCursor = 0;
        for (int i = 0; i < def.Length; i++)
        {
            var op = def[i];
            switch (op.Kind)
            {
                case AbbrevOpKind.Literal:
                    // Literals are not present in operand stream — they reconstruct on read.
                    break;
                case AbbrevOpKind.Fixed:
                    WriteBits64(operands[opCursor++], (int)op.Value);
                    break;
                case AbbrevOpKind.Vbr:
                    WriteVBR64(operands[opCursor++], (int)op.Value);
                    break;
                case AbbrevOpKind.Array:
                    {
                        // Array uses the next op's encoding for each element.
                        if (i + 1 >= def.Length)
                            throw new InvalidOperationException("Array abbrev op must be followed by element op");
                        var elt = def[i + 1];
                        int remaining = operands.Length - opCursor;
                        WriteVBR((uint)remaining, 6);
                        for (int j = 0; j < remaining; j++)
                        {
                            ulong v = operands[opCursor++];
                            switch (elt.Kind)
                            {
                                case AbbrevOpKind.Fixed: WriteBits64(v, (int)elt.Value); break;
                                case AbbrevOpKind.Vbr: WriteVBR64(v, (int)elt.Value); break;
                                case AbbrevOpKind.Char6: WriteBits(EncodeChar6((byte)v), 6); break;
                                default: throw new InvalidOperationException("invalid array element kind");
                            }
                        }
                        i++; // consumed the element op
                        break;
                    }
                case AbbrevOpKind.Char6:
                    WriteBits(EncodeChar6((byte)operands[opCursor++]), 6);
                    break;
                case AbbrevOpKind.Blob:
                    throw new InvalidOperationException("Use WriteBlobAbbrevRecord for Blob abbrevs");
            }
        }
    }

    public void WriteAbbrevRecord(uint abbrevId, params ulong[] operands)
        => WriteAbbrevRecord(abbrevId, (ReadOnlySpan<ulong>)operands);

    public void WriteAbbrevRecord(uint abbrevId, ulong op0)
    {
        Span<ulong> ops = stackalloc ulong[1];
        ops[0] = op0;
        WriteAbbrevRecord(abbrevId, (ReadOnlySpan<ulong>)ops);
    }

    public void WriteAbbrevRecord(uint abbrevId, ulong op0, ulong op1)
    {
        Span<ulong> ops = stackalloc ulong[2];
        ops[0] = op0; ops[1] = op1;
        WriteAbbrevRecord(abbrevId, (ReadOnlySpan<ulong>)ops);
    }

    /// <summary>
    /// Emits a record using an abbreviation whose final op is <see cref="AbbrevOpKind.Blob"/>.
    /// The blob payload is preceded by a VBR6 length and aligned to a 32-bit boundary.
    /// Preamble Fixed/Vbr ops are filled from <paramref name="preambleOperands"/> in order.
    /// </summary>
    public void WriteBlobAbbrevRecord(uint abbrevId, ReadOnlySpan<ulong> preambleOperands, byte[] blob)
    {
        if (abbrevId < FirstUserAbbrevId || abbrevId >= _currentAbbrevs.Count)
            throw new ArgumentOutOfRangeException(nameof(abbrevId), $"unknown abbrev id {abbrevId}");
        var def = _currentAbbrevs[(int)abbrevId];

        WriteAbbrevId(abbrevId);

        int blobOpIdx = -1;
        int preambleIdx = 0;
        for (int i = 0; i < def.Length; i++)
        {
            var op = def[i];
            if (op.Kind == AbbrevOpKind.Blob) { blobOpIdx = i; break; }
            switch (op.Kind)
            {
                case AbbrevOpKind.Literal: break;
                case AbbrevOpKind.Fixed:
                    WriteBits(preambleOperands[preambleIdx++], (int)op.Value); break;
                case AbbrevOpKind.Vbr:
                    WriteVBR64(preambleOperands[preambleIdx++], (int)op.Value); break;
                default:
                    throw new InvalidOperationException("Blob abbrev preamble may only contain Literal/Fixed/Vbr ops");
            }
        }
        if (blobOpIdx < 0)
            throw new InvalidOperationException("abbrev does not end with a Blob op");

        WriteVBR((uint)blob.Length, 6);
        FlushToWord();
        foreach (var b in blob)
            WriteBits(b, 8);
        FlushToWord();
    }

    public void WriteBlobAbbrevRecord(uint abbrevId, byte[] blob)
        => WriteBlobAbbrevRecord(abbrevId, ReadOnlySpan<ulong>.Empty, blob);

    private static uint EncodeChar6(byte b)
    {
        // 'a'..'z' -> 0..25, 'A'..'Z' -> 26..51, '0'..'9' -> 52..61, '.' -> 62, '_' -> 63
        if (b >= 'a' && b <= 'z') return (uint)(b - 'a');
        if (b >= 'A' && b <= 'Z') return (uint)(b - 'A' + 26);
        if (b >= '0' && b <= '9') return (uint)(b - '0' + 52);
        if (b == '.') return 62;
        if (b == '_') return 63;
        throw new ArgumentOutOfRangeException(nameof(b), $"char6 cannot encode 0x{b:X2}");
    }

    private readonly struct BlockFrame
    {
        public readonly uint BlockId;
        public readonly int ParentAbbrevWidth;
        public readonly int LengthWordPos;
        public readonly uint ParentNextAbbrevId;
        public readonly List<AbbrevOp[]> ParentAbbrevs;
        public BlockFrame(uint blockId, int parentAbbrevWidth, int lengthWordPos,
            uint parentNextAbbrevId, List<AbbrevOp[]> parentAbbrevs)
        {
            BlockId = blockId;
            ParentAbbrevWidth = parentAbbrevWidth;
            LengthWordPos = lengthWordPos;
            ParentNextAbbrevId = parentNextAbbrevId;
            ParentAbbrevs = parentAbbrevs;
        }
    }
}

internal static class BitConverterLE
{
    public static void WriteUInt32(List<byte> buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

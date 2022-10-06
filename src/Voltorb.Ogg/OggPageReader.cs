using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Nerdbank.Streams;

namespace Voltorb.Ogg;

public class OggPageReader
{
    public readonly record struct PageInfo(ulong GranulePosition, uint BitstreamSerial, uint PageSequenceNumber,
        HeaderType PacketType, long SeekPosition, int[] PacketLengths, bool FinalPacketIsComplete);

    /// <summary>
    /// Ogg page data, including metadata recovered from the header as well as a segmented buffer of page data bits
    /// allocated from the <see cref="MemoryPool{Byte}"/> the source <see cref="OggPageReader"/> was initialized with
    /// </summary>
    /// <param name="PageInfo">Retrieved page metadata</param>
    /// <param name="PageBits">The binary data associated with this page, which remains valid until this instance is disposed</param>
    public sealed record PageData(PageInfo PageInfo, Sequence<byte> PageBits) : IDisposable
    {
        public ulong GranulePosition => PageInfo.GranulePosition;
        public uint BitstreamSerial => PageInfo.BitstreamSerial;
        public uint PageSequenceNumber => PageInfo.PageSequenceNumber;
        public HeaderType PacketType => PageInfo.PacketType;
        public long SeekPosition => PageInfo.SeekPosition;
        public int[] PacketLengths => PageInfo.PacketLengths;
        public bool FinalPacketIsComplete => PageInfo.FinalPacketIsComplete;

        public void Dispose() {
            PageBits.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct PageHeader
    {
        internal readonly byte       Version;
        internal readonly HeaderType Flags;
        internal readonly ulong      GranulePosition;
        internal readonly uint       BitstreamSerial;
        internal readonly uint       PageSequence;
        internal readonly uint       CrcChecksum;
        internal readonly byte       PageSegmentCount;
    }


    private readonly Stream           _stream;
    private readonly MemoryPool<byte> _memoryPool;
    private readonly List<PageInfo>   _pageOffsetTable;
    private readonly Crc32            _crc;

    /// <summary>
    /// Allows access to metadata about all pages this reader has encountered.
    /// </summary>
    public IReadOnlyList<PageInfo> SeenPages { get; }

    public OggPageReader(Stream stream, MemoryPool<byte> memoryPool) {
        _stream = stream;
        _memoryPool = memoryPool;
        _pageOffsetTable = new List<PageInfo>();
        _crc = new Crc32();
        SeenPages = new ReadOnlyCollection<PageInfo>(_pageOffsetTable);
    }

    /// <inheritdoc cref="Stream.CanSeek"/>
    public bool CanSeek => _stream.CanSeek;

    /// <summary>
    /// Seek to and read the page of given index within the source stream.
    /// When this method completes, page data for all pages preceding the given page index will be available in
    /// <see cref="PageInfo"/>, in addition to data about the page seeked to.
    ///
    /// <see cref="ReadPageAsync"/> for more about page reading semantics.
    /// </summary>
    public async ValueTask<PageData> SeekAndReadPageAsync(int pageIndex, CancellationToken cancellationToken = default) {
        if (pageIndex < _pageOffsetTable.Count) {
            _stream.Seek(_pageOffsetTable[pageIndex].SeekPosition, SeekOrigin.Begin);
            return await ReadPageAsyncCore(pageIndex, cancellationToken);
        }

        _pageOffsetTable.EnsureCapacity(pageIndex);
        for (int i = _pageOffsetTable.Count; i < pageIndex; i++) {
            await ReadPageAsyncCore(i, cancellationToken);
        }

        return await ReadPageAsyncCore(pageIndex, cancellationToken);
    }


    /// <summary>
    /// Read through the next ogg page in the source stream, discarding any remaining data before the page starts.
    /// Page metadata and data bits are accessible through the returned <see cref="PageData"/> and will be checked
    /// with the ogg page CRC for corruption.
    ///
    /// If re-reading a previously read page after a seek operation, the page header will still be re-read and metadata
    /// updated, as well as the checksum re-verified for the page.
    /// </summary>
    public ValueTask<PageData> ReadPageAsync(CancellationToken cancellationToken = default) =>
        ReadPageAsyncCore(_pageOffsetTable.Count, cancellationToken);

    [SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods")]
    private async ValueTask<PageData> ReadPageAsyncCore(int idx, CancellationToken cancellationToken = default) {
        Sequence<byte> sequenceForCrc = new(_memoryPool);
        PageHeader header;
        using (IMemoryOwner<byte> rental = _memoryPool.Rent(23)) {
            int indexOf;
            int count;
            Memory<byte> memory = rental.Memory;
            do {
                count = await _stream.ReadAtLeastAsync(memory, OggConstants.CapturePattern.Length,
                    cancellationToken:
                    cancellationToken);
            } while ((indexOf = memory.Span[..count].IndexOf(OggConstants.CapturePattern)) < 0);

            Memory<byte> slice = memory[(indexOf + 4)..count];
            int offset = slice.Length;
            slice.CopyTo(memory);
            await _stream.ReadExactlyAsync(memory[offset..], cancellationToken);
            header = MemoryMarshal.Read<PageHeader>(memory.Span);
            memory.Span[22..25].Fill(0);
            sequenceForCrc.Write(memory.Span);
        }

        long streamPosition = _stream.Position - 27;

        byte[] lacingValues = new byte[header.PageSegmentCount];
        await _stream.ReadExactlyAsync(lacingValues, cancellationToken);

        sequenceForCrc.Write(lacingValues);

        GetPacketLengths(lacingValues, out int[] packets, out bool lastPacketIsComplete);
        int pageDataLength = packets.Sum();
        SequencePosition dataStartPos = sequenceForCrc.AsReadOnlySequence.End;
        await _stream.ReadSlice(pageDataLength).CopyToAsync(sequenceForCrc.AsStream(), cancellationToken);
        foreach (ReadOnlyMemory<byte> memory in sequenceForCrc.AsReadOnlySequence) {
            _crc.Append(memory.Span);
        }

        uint crc = 0;
        _crc.GetHashAndReset(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref crc, 1)));
        if (crc != header.CrcChecksum)
            throw new IOException();

        sequenceForCrc.AdvanceTo(dataStartPos);
        PageInfo pageInfo = new(header.GranulePosition, header.BitstreamSerial, header.PageSequence, header.Flags,
            streamPosition, packets, lastPacketIsComplete);
        if (idx < 0 || idx >= _pageOffsetTable.Count)
            _pageOffsetTable.Add(pageInfo);
        else
            _pageOffsetTable[idx] = pageInfo;

        return new PageData(pageInfo, sequenceForCrc);
    }


    /// <summary>
    /// Turns a list of segment lacing values into a list of (partial) packet lengths.
    /// The first packet value may be low if the packet is a continuation of a previous packet, and the final packet
    /// value may be low if the final packet is to be continued on the next page, indicated by <paramref name="lastPacketIsComplete"/>
    /// </summary>
    /// <param name="lacings"></param>
    /// <param name="packets"></param>
    /// <param name="lastPacketIsComplete"></param>
    private static void GetPacketLengths(IReadOnlyList<byte> lacings, out int[] packets,
        out bool lastPacketIsComplete) {
        static IEnumerable<int> Lengths(IEnumerable<byte> lacings) {
            int next = 0;
            foreach (byte lacing in lacings) {
                next += lacing;
                if (lacing == 255) continue;
                yield return next;
                next = 0;
            }

            if (next != 0)
                yield return next;
        }

        packets = Lengths(lacings).ToArray();
        lastPacketIsComplete = lacings[^1] != 255;
    }
}
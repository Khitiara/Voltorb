using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Nerdbank.Streams;

namespace Voltorb.Ogg;

/// <summary>
/// A raw ogg bitstream reader, capable of decoding pages from an ogg bitstream, but with no built-in support for
/// de-multiplexing multiple logical bitstreams from a physical bitstream. Seeking to a page by index is possible if
/// allowed by the underlying source <see cref="Stream"/>, though determining the page index for any given high-level
/// timing value (such as a timestamp or number of samples) is the responsibility of codecs built upon this type.
/// </summary>
public class OggPageReader : IDisposable
{
    /// <summary>
    /// Ogg page metadata, including both data recovered from the header as well as an absolute offset within the source
    /// <see cref="Stream"/> for seeking to specific pages.
    /// </summary>
    /// <param name="GranulePosition">
    /// The absolute granule position within the logical stream, the meaning of which is codec dependent
    /// </param>
    /// <param name="BitstreamSerial">The serial number of the logical bitstream this page belongs to</param>
    /// <param name="PageSequenceNumber">
    /// A strictly-increasing sequence number of the page within the logical stream, for recovering from errors
    /// </param>
    /// <param name="PacketType">
    /// Flags specifying whether this page continues a previous packet, as well as whether this page begins or ends a
    /// logical bitstream.
    /// </param>
    /// <param name="SeekPosition">
    /// The absolute position within the source stream of this page, for seeking to pages. This value should generally
    /// not be used by calling code, and exists to support <see cref="OggPageReader.SeekAndReadPageAsync"/>
    /// </param>
    /// <param name="PacketLengths">
    /// An array of packet lengths within the data of this page, which may be inaccurate to the size of a final packet
    /// if this page continues a previous page's packet or does not include the end of its final packet
    /// </param>
    /// <param name="FinalPacketIsComplete">
    /// Whether the final packet in this page is complete. If false, the next page for this logical stream will continue
    /// that packet's data, possibly requiring even further continuation.
    /// </param>
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
    public async ValueTask<PageData>
        SeekAndReadPageAsync(int pageIndex, CancellationToken cancellationToken = default) {
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
        // grab a buffer for the page data. this will be used for calculating the crc and then sliced 
        Sequence<byte> sequenceForCrc = new(_memoryPool);

        // get a segment for reading the header into
        Memory<byte> memory = sequenceForCrc.GetMemory(23);
        int indexOf;
        int count;

        // read until we have the capture pattern in the segment somewhere
        // in sequential operations, this is expected to exit after the first loop iteration
        do {
            count = await _stream.ReadAtLeastAsync(memory, OggConstants.CapturePattern.Length,
                cancellationToken: cancellationToken);
        } while ((indexOf = memory.Span[..count].IndexOf(OggConstants.CapturePattern)) < 0);

        // slice everything after the capture pattern. since indexof returned >0 on the capture pattern, this will not
        // overrun, but may return the empty segment. offset will be 0 in that case, and copyto will nop
        Memory<byte> slice = memory[(indexOf + OggConstants.CapturePattern.Length)..count];
        int offset = slice.Length;
        // and move the end back to the start
        slice.CopyTo(memory);

        // finish filling the buffer. if we cant fill the buffer thats an error so use ReadExactly
        await _stream.ReadExactlyAsync(memory[offset..], cancellationToken);
        // read into the struct (this is a copy)
        PageHeader header = MemoryMarshal.Read<PageHeader>(memory.Span);
        // clear the crc in the buffer now we have a copy in header
        memory.Span[18..21].Fill(0);
        // and commit the main header bytes
        sequenceForCrc.Advance(23);

        // figure out where the stream was at the start of capture pattern for seeking later
        long streamPosition = _stream.Position - 27;

        // read the lacing values - pagesegmentcount is an exact length
        // slice since getmemory can give an oversize buffer
        Memory<byte> lacingMemory = sequenceForCrc.GetMemory(header.PageSegmentCount)[..header.PageSegmentCount];
        await _stream.ReadExactlyAsync(lacingMemory, cancellationToken);
        sequenceForCrc.Advance(header.PageSegmentCount);

        // turn the lacing values (segment lengths) into packet lengths and figure out if we're going to continue
        // a packet on the next page
        GetPacketLengths(lacingMemory, out int[] packets, out bool lastPacketIsComplete);

        // save the position at the end of the header so we can omit the processed header from the returned data
        SequencePosition dataStartPos = sequenceForCrc.AsReadOnlySequence.End;

        // nerdbank.streams readslice guarantees the returned stream reads no more than the given number of bytes,
        // allows replacing a complicated loop with a single CopyToAsync. using the extension on IBufferWriter<byte>
        // from the same library for the destination stream. ReadExactly could work here but forces a possibly large
        // single array allocation, and this way we can pool stuff easily
        await _stream.ReadSlice(packets.Sum()).CopyToAsync(sequenceForCrc.AsStream(), cancellationToken);

        // calculate the Crc32 of the page, with the header crc field set to 0. the _crc should be always reset when
        // this block starts (reset in constructor and in GetHashAndReset), lock on it anyway
        lock (_crc) {
            foreach (ReadOnlyMemory<byte> segment in sequenceForCrc.AsReadOnlySequence) {
                _crc.Append(segment.Span);
            }

            uint crc = 0;
            _crc.GetHashAndReset(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref crc, 1)));
            if (crc != header.CrcChecksum)
                throw new IOException(); // TODO add an exception message here. is this the right exception type?
        }

        // now we've computed the Crc32 of the page, we dont need the header bytes in our sequence. use AdvanceTo to
        // skip past them and return segments to the memory pool
        sequenceForCrc.AdvanceTo(dataStartPos);

        // assembly page metadata and add to our offsets table. append is valid even for a strongly indexed list here
        // because SeekAndReadPageAsync reads in-between pages in order to find future pages, so unless the client
        // seeks behind our back this is OK
        PageInfo pageInfo = new(header.GranulePosition, header.BitstreamSerial, header.PageSequence, header.Flags,
            streamPosition, packets, lastPacketIsComplete);
        if (idx < 0 || idx >= _pageOffsetTable.Count)
            _pageOffsetTable.Add(pageInfo);
        else
            _pageOffsetTable[idx] = pageInfo;

        return new PageData(pageInfo, sequenceForCrc);
    }


    /// <summary>
    /// Turns a list of segment lacing values into a list of (partial) packet lengths. The first packet value may be
    /// low if the packet is a continuation of a previous packet (noted in header flags), and the final packet value
    /// may be low if the final packet is to be continued on the next page, indicated by <paramref name="lastPacketIsComplete"/>
    /// </summary>
    /// <param name="lacings"></param>
    /// <param name="packets"></param>
    /// <param name="lastPacketIsComplete"></param>
    private static void GetPacketLengths(Memory<byte> lacings, out int[] packets, out bool lastPacketIsComplete) {
        List<int> lengths = new();
        lastPacketIsComplete = true;

        // a contiguous packet of segments is denoted by a sequence of 0xFF followed by a byte < 0xFF
        // with the 0xFF bytes to be added together. an incomplete packet is denoted by the sequence ending without
        // a following smaller lacing byte (packets multiple of 0xFF bytes are denoted by a terminating 0x0 byte)
        int next = 0;
        for (int i = 0; i < lacings.Length; i++) {
            byte lacing = lacings.Span[i];
            next += lacing;
            if (lacing == 255) continue;
            lengths.Add(next);
            next = 0;
        }

        // if next is nonzero when the loop exits then the last lacing value is 0xFF and the last packet is incomplete
        if (next != 0) {
            lengths.Add(next);
            lastPacketIsComplete = false;
        }

        packets = lengths.ToArray();
    }

    protected virtual void Dispose(bool disposing) {
        if (disposing) {
            _stream.Dispose();
        }
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
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
    /// <param name="CrcChecksum">The CRC checksum of the Ogg page, per the ogg spec</param>
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
        HeaderType PacketType, uint CrcChecksum, long SeekPosition, int[] PacketLengths, int PageIndex,
        bool FinalPacketIsComplete);

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
        public uint CrcChecksum => PageInfo.CrcChecksum;
        public long SeekPosition => PageInfo.SeekPosition;
        public int[] PacketLengths => PageInfo.PacketLengths;
        public int PageIndex => PageInfo.PageIndex;
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
    /// the index of the next page to be read. updated during every call to <see cref="ReadPageAsync"/>
    /// </summary>
    private int _currentPageIndex;

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
    public async ValueTask<PageData> SeekAndReadPageAsync(int pageIndex, 
        CancellationToken cancellationToken = default) {
        if (pageIndex < _pageOffsetTable.Count) {
            // we already know where the page is, seek to the start of the capture pattern and read it
            _stream.Seek(_pageOffsetTable[pageIndex].SeekPosition, SeekOrigin.Begin);
            _currentPageIndex = pageIndex;
            return await ReadPageAsync(cancellationToken: cancellationToken);
        }

        // no record of the page, start reading pages
        _pageOffsetTable.EnsureCapacity(pageIndex + 1);
        // go to the last page we know (just in case), and bypass its header to prevent off-by-one error
        _stream.Seek(_pageOffsetTable[^1].SeekPosition + 27, SeekOrigin.Begin);
        _currentPageIndex = _pageOffsetTable.Count;
        // read in-between pages
        for (int i = _pageOffsetTable.Count; i < pageIndex; i++) {
            // make sure we dispose the data sequence when 
            (await ReadPageAsync(cancellationToken: cancellationToken)).Dispose();
        }

        return await ReadPageAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Raised when a page non-contiguity is detected (a page start not occurring immediately after the previous page
    /// or the start of the stream), to be used by calling code to detect and react to such errors and prevent possible
    /// packet corruption or data loss.
    /// </summary>
    public event Func<Task>? PageNonContiguity;


    /// <summary>
    /// Read through the next ogg page in the source stream, discarding any remaining data before the page starts.
    /// Page metadata and data bits are accessible through the returned <see cref="PageData"/> and will be checked
    /// with the ogg page CRC for corruption.
    ///
    /// If re-reading a previously read page after a seek operation, the page header will still be re-read and metadata
    /// updated, as well as the checksum re-verified for the page.
    /// </summary>
    public async ValueTask<PageData> ReadPageAsync(bool validateCrc = true,
        CancellationToken cancellationToken = default) {
        // grab a buffer for the page data. this will be used for calculating the crc and then sliced 
        Sequence<byte> sequenceForCrc = new(_memoryPool);
        // write in the capture pattern since its included in the CRC
        sequenceForCrc.Write(OggConstants.CapturePattern);

        // get a segment for reading the header into. minimum size 23 so once we get the capture pattern we can
        // overwrite that and get an exact-size buffer
        Memory<byte> memory = sequenceForCrc.GetMemory(23);
        memory.Span.Fill(0);

        // one of three scenarios can happen after each iteration of the loop:
        // - capture pattern found in full. copy moves later bytes over top of the capture pattern
        //      and a second read is required before decoding
        // - capture pattern not in read buffer at all. copy last three bytes to the start and loop again
        // - up to three bytes of capture pattern at end of read buffer but cut off. copy last three bytes to the start
        //      and remaining capture pattern bytes will be placed contiguously after. after one loop, capture pattern
        //      is de-fragmented, will be detected with index < 3, continue per first case with possible no second read
        // first scenario is most likely except when reading forward from mid-page (only possible on start,
        //      stream error, or manual backing-stream seek by calling code)

        // e.g. for each case
        // 1: [XXX.......] --Read-> [XXX.OggSYY] -> Break
        // 2: [XXX.......] --Read-> [XXX....YYY] --NotFound-> [YYY.......] -> Repeat
        // 3: [XXX.......] --Read-> [XXX.....Og] --NotFound-> [.Og.......] --Read-> [.OggSXXXXX] -> Break

        // read until we have the capture pattern in the segment somewhere
        // in sequential operations, this is expected to exit after the first loop iteration

        // the last read before breaking from the loop will always end with the capture pattern inside the buffer
        // taking up 4 bytes so in the best case the first three bytes are pre-filled from previous iteration
        // and we can read one more capture pattern byte plus 23 main header bytes for a total of a 27-byte buffer.
        // the initial 3 bytes will never be directly read into from the stream, being kept as holdover from a previous
        // iteration to detect when the read boundary occurs in the middle of a capture pattern.
        int cappedBufferSize = Math.Min(27, memory.Length);
        bool foundCapturePattern;
        // get a slice for safety on the end range
        Memory<byte> sliced = memory[..cappedBufferSize];
        int indexOf;
        int count = 0;
        do {
            // take the last three bytes and move them to the start. copies zeros on first iteration for no effect
            // first three bytes only exposed to later code if filled with capture pattern by prev. iteration so
            // the extra zeros will be discarded. use count for the range in case the stream reads less than the full
            // available buffer. this slice is the same as [3,(count+3)][^3] = [(count+0)..(count+3)]. on the first
            // run through the loop, count = 0 so this is copying in place and CopyTo will short-circuit cleanly.
            sliced.Slice(count, 3).CopyTo(memory);
            // read at least enough bytes for the capture pattern to make the memory shuffle a little easier
            count = await _stream.ReadAtLeastAsync(sliced[3..], OggConstants.CapturePattern.Length,
                cancellationToken: cancellationToken);
            // my new favourite framework method is ReadOnlySpan.IndexOf(ReadOnlySpan)
            foundCapturePattern = (indexOf = memory.Span[..(count + 3)].IndexOf(OggConstants.CapturePattern)) < 0;
        } while (!foundCapturePattern);

        if (indexOf != 3) {
            OnPageNonContiguityAsync();
        }

        // slice everything after the capture pattern. since indexof returned >=0 on the capture pattern, this will not
        // overrun, but may return an empty slice. offset will be 0 in that case, and we'll overwrite the capture
        // pattern with the second stream read rather than call into CopyTo
        Memory<byte> slice = memory[(indexOf + OggConstants.CapturePattern.Length)..count];
        int offset = slice.Length;
        // and move the extra back to the start if there is any
        if (offset > 0)
            slice.CopyTo(memory);

        // finish filling the header buffer if we already have enough (offset = remaining after capture pattern >= 23) then dont bother
        if (offset < 23)
            await _stream.ReadExactlyAsync(memory[offset..23], cancellationToken);
        // read into the struct (this makes a copy on the stack)
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
        GetPacketLengths(lacingMemory.Span, out int[] packets, out bool lastPacketIsComplete);

        // save the position at the end of the header so we can omit the processed header from the returned data
        SequencePosition dataStartPos = sequenceForCrc.AsReadOnlySequence.End;

        // nerdbank.streams readslice guarantees the returned stream reads no more than the given number of bytes,
        // allows replacing a complicated loop with a single CopyToAsync. using the extension on IBufferWriter<byte>
        // from the same library for the destination stream. ReadExactly could work here but forces a possibly large
        // single array allocation, and this way we can pool stuff easily
        await _stream.ReadSlice(packets.Sum()).CopyToAsync(sequenceForCrc.AsStream(), cancellationToken);

        // calculate the Crc32 of the page, with the header crc field set to 0. the _crc should be always reset when
        // this block starts (reset in constructor and in GetHashAndReset), lock on it anyway
        if (validateCrc) {
            lock (_crc) {
                foreach (ReadOnlyMemory<byte> segment in sequenceForCrc.AsReadOnlySequence) {
                    _crc.Append(segment.Span);
                }


                if (BinaryPrimitives.ReadUInt32BigEndian(_crc.GetHashAndReset()) != header.CrcChecksum)
                    throw new IOException(); // TODO add an exception message here. is this the right exception type?
            }
        }

        // now we've checked (or ignored) the Crc32 of the page, we dont need the header bytes in our sequence. use AdvanceTo to
        // skip past them and return segments to the memory pool
        sequenceForCrc.AdvanceTo(dataStartPos);

        // assembly page metadata and add to our offsets table. uses _currentPageIndex for indexing into the page info
        // list, adding to the end if needed. if the source stream has been seeked behind our back then this may corrupt
        // the page table, but manually seeking to before the corrupted entry and re-reading pages from there will
        // restore the setup
        PageInfo pageInfo = new(header.GranulePosition, header.BitstreamSerial, header.PageSequence, header.Flags,
            header.CrcChecksum, streamPosition, packets, _currentPageIndex, lastPacketIsComplete);
        if (_currentPageIndex < 0 || _currentPageIndex >= _pageOffsetTable.Count) {
            _pageOffsetTable.Add(pageInfo);
            _currentPageIndex++;
        } else
            _pageOffsetTable[_currentPageIndex++] = pageInfo;

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
    private static void GetPacketLengths(ReadOnlySpan<byte> lacings, out int[] packets, out bool lastPacketIsComplete) {
        List<int> lengths = new();
        lastPacketIsComplete = true;

        // a contiguous packet of segments is denoted by a sequence of 0xFF followed by a byte < 0xFF
        // with the 0xFF bytes to be added together. an incomplete packet is denoted by the sequence ending without
        // a following smaller lacing byte (packets multiple of 0xFF bytes are denoted by a terminating 0x0 byte)
        int next = 0;
        foreach (byte lacing in lacings) {
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

    protected virtual async ValueTask OnPageNonContiguityAsync() {
        await Task.WhenAll(PageNonContiguity?.GetInvocationList()
            ?.Cast<Func<Task>>()
            ?.Select(f => f()) ?? Enumerable.Empty<Task>());
    }
}
using System.Buffers;

namespace Voltorb.Ogg;

/// <summary>
/// Internal continuation state for <see cref="OggPageReader"/> to allow for reentrancy with incomplete data retrieved
/// by means of successive <see cref="ReadOnlySequence{T}"/>s
/// </summary>
public struct OggReaderState
{
    /// <summary>
    /// The stage of page reading
    /// </summary>
    internal enum Stage
    {
        Capture,
        Version,
        Type,
        GranulePosition,
        StreamSerialNumber,
        SequenceNumber,
        CrcChecksum,
        NumPageSegments,
        SegmentTable,
        Data,
    }

    /// <summary>
    /// Special ogg page header flags
    /// </summary>
    [Flags]
    internal enum HeaderType
    {
        None                = 0x00,
        ContinuesPacket     = 0x01,
        BeginsLogicalStream = 0x02,
        EndsLogicalStream   = 0x04,
    }

    internal Stage      CurrentStage;
    internal byte       StreamStructureVersion;
    internal HeaderType PageType;
    internal ulong      GranulePosition;
    internal uint       BitstreamSerialNumber;
    internal uint       PageSequenceNumber;
    internal uint       Checksum;
    /// <summary>
    /// When reading the segment table, is used for indexing into the page segments buffer for partial reads.
    /// When reading data, used to index which packet is being read at a given time.
    /// </summary>
    internal int        SegmentsIndex;
    internal int        SegmentsRemaining;
    internal byte[]     PageSegments;
    internal int[]      PacketLengths;
    internal bool       LastPacketIsComplete;
}
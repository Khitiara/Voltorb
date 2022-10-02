using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nerdbank.Streams;

namespace Voltorb.Ogg;

/// <summary>
/// Allows efficient direct reading of Ogg pages from a fixed sequence, with no built-in support for demultiplexing.
/// Read continuation is supported by passing an <see cref="OggReaderState"/> to the constructors of this type.
///
/// Most users should not expect to use this type directly; instead <see cref="OggDemultiplexingReader"/> should be
/// preferred for most use cases.
/// </summary>
public ref struct OggPageReader
{
    private SequenceReader<byte> _reader;
    private OggReaderState       _state;

    public OggPageReader(ReadOnlySequence<byte> seq, OggReaderState state = default) {
        _state = state;
        _reader = new SequenceReader<byte>(seq);
    }

    public OggPageReader(ReadOnlySpan<byte> span, OggReaderState state = default) {
        _state = state;
        Sequence<byte> seq = new();
        seq.Write(span);
        _reader = new SequenceReader<byte>(seq);
    }

    public readonly OggReaderState State => _state;

    public SequenceReader<byte> Reader => _reader;

    public IReadOnlyList<int> PacketLengths => new ReadOnlyCollection<int>(State.PacketLengths);

    /// <summary>
    /// Attempt to read an Ogg page header from the current sequence. A partially read page header will be filled out
    /// with available data in the case of a reader initialized with a <see cref="OggReaderState"/>, and any completely
    /// read page metadata will be discarded before searching for the next page.
    ///
    /// If this method returns false, <see cref="State"/> should be used to construct a new <see cref="OggPageReader"/>
    /// with additional data when available and this method called again.
    /// </summary>
    /// <returns>True if a new page header could be found and read fully, false otherwise</returns>
    /// <remarks>
    /// If this method returns true, <see cref="Reader"/> is guaranteed to be positioned at the start of the first
    /// segment of data within the current page and <see cref="PacketLengths"/> are guaranteed to return valid
    /// information about the current page.
    /// </remarks>
    public bool TryReadHeader() {
        if (_state.CurrentStage == OggReaderState.Stage.Data) {
            _state = default;
        }

        while (_state.CurrentStage != OggReaderState.Stage.Data) {
            switch (_state.CurrentStage) {
                case OggReaderState.Stage.Capture:
                    // Quick check here in the likely case that we are sitting right at the start of a valid ogg page
                    if (_reader.IsNext(OggConstants.CapturePattern))
                        _reader.Advance(OggConstants.CapturePattern.Length);
                    else if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, OggConstants.CapturePattern)) 
                        return false;

                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.Version:
                    if (!_reader.TryRead(out _state.StreamStructureVersion)) return false;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.Type:
                    // wait this unsafe shit is allowed???
                    if (!_reader.TryRead(out Unsafe.As<OggReaderState.HeaderType, byte>(ref _state.PageType)))
                        return false;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.GranulePosition:
                    if (!SequenceMarshal.TryRead(ref _reader, out _state.GranulePosition))
                        return false;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.StreamSerialNumber:
                    if (!SequenceMarshal.TryRead(ref _reader, out _state.BitstreamSerialNumber))
                        return false;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.SequenceNumber:
                    if (!SequenceMarshal.TryRead(ref _reader, out _state.PageSequenceNumber))
                        return false;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.CrcChecksum:
                    if (!SequenceMarshal.TryRead(ref _reader, out _state.Checksum))
                        return false;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.NumPageSegments:
                    if (!_reader.TryRead(out byte numPageSegments)) return false;
                    _state.PageSegments = new byte[numPageSegments];
                    _state.SegmentsRemaining = numPageSegments;
                    _state.CurrentStage++;
                    break;
                case OggReaderState.Stage.SegmentTable:
                    int toRead = Math.Min(_state.SegmentsRemaining, (int)_reader.Remaining);
                    _reader.TryCopyTo(_state.PageSegments.AsSpan(_state.SegmentsIndex, toRead));
                    _reader.Advance(_state.SegmentsIndex += toRead);
                    // if the final segmentsread value is nonzero then theres more to read still, return false and wait
                    // for more data. the segmentsindex value gives an offset to read to so no overwriting will happen
                    if ((_state.SegmentsRemaining -= toRead) > 0)
                        return false;
                    // reset index for use in packet indexing
                    _state.SegmentsIndex = 0;
                    _state.CurrentStage++;
                    GetPacketLengths(_state.PageSegments!, out _state.PacketLengths, out _state.LastPacketIsComplete);
                    break;
                case OggReaderState.Stage.Data:
                    throw new UnreachableException();
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return true;
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
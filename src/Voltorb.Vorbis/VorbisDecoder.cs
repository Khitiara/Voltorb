using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using CommunityToolkit.HighPerformance;
using Voltorb.Bits;
using Voltorb.Vorbis.Internal;

namespace Voltorb.Vorbis;

/// <summary>
/// Decodes vorbis packets retrieved from an external source
/// </summary>
public sealed class VorbisDecoder
{
    // read one bit on audio packets, read 8 bits on header packets
    private static VorbisPacketType DetectPacketType(ref BitReader packet) {
        if (packet.ReadBit()) {
            return (VorbisPacketType)(packet.ReadBits(7) * 2 + 1);
        }

        return VorbisPacketType.Audio;
    }

    private          uint                                  _vorbisVersion;
    private          byte                                  _channelCount;
    private          uint                                  _sampleRate;
    private          uint                                  _bitRateMax;
    private          uint                                  _bitRateNominal;
    private          uint                                  _bitRateMin;
    private          ushort                                _blockSize0;
    private          ushort                                _blockSize1;
    private          string                                _vendorString = null!;
    private          FrozenDictionary<string, string> _comments;
    private          bool                                  _hasIdent;
    private          bool                                  _hasComments;
    private          bool                                  _hasSetup;
    private          Huffman                               _huffman = null!;
    private          Mode[]                      _modes;
    private          int                                   _modeBits;
    private          float[,]                              _packetBuf     = null!;
    private          float[,]                              _prevPacketBuf = null!;
    private          bool                                  _eosFound;
    private          Range?                                _prevPacketRange;
    private          long                                  _samplePosition;
    private readonly IBufferWriter<float>                  _writer;
    private readonly bool                                  _clipSamples;
    private          bool                                  _hasClipped;

    public VorbisDecoder(IBufferWriter<float> writer, bool clipSamples) {
        _writer = writer;
        _clipSamples = clipSamples;
        _getPacketGranuleCount = GetPacketGranuleCount;
    }

    private void ReadPacket(ref BitReader reader, bool packetIsEndOfStream, ulong pageGranulePosition) {
        switch (DetectPacketType(ref reader)) {
            case VorbisPacketType.Audio:
                ReadAudio(ref reader, packetIsEndOfStream, pageGranulePosition);
                break;
            case VorbisPacketType.Identification:
                if (_hasIdent) throw new InvalidDataException();
                CheckHeaderPacketHeader(ref reader);
                ReadIdentHeader(ref reader);
                break;
            case VorbisPacketType.Comment:
                if (_hasComments) throw new InvalidDataException();
                CheckHeaderPacketHeader(ref reader);
                ReadCommentHeader(ref reader);
                break;
            case VorbisPacketType.Setup:
                if (_hasSetup) throw new InvalidDataException();
                CheckHeaderPacketHeader(ref reader);
                ReadSetupHeader(ref reader);
                break;
            default:
                throw new InvalidDataException();
        }
    }

    private static void CheckHeaderPacketHeader(ref BitReader reader) {
        if (reader.ReadBits(48) != VorbisConstants.VorbisHeaderBitstring)
            throw new InvalidDataException();
    }

    private void ReadAudio(ref BitReader reader, bool packetIsEndOfStream, ulong pageGranulePosition) {
        if (!_hasIdent || !_hasComments || !_hasSetup)
            throw new InvalidDataException();
        _eosFound |= packetIsEndOfStream;
        int prevStart;
        if (!DecodeNextPacket(ref reader, pageGranulePosition, out int packetStartIndex, out int packetValidLength,
                out int packetTotalLength, out ulong? samplePosition)) {
            packetValidLength = packetTotalLength;
            prevStart = _prevPacketRange?.GetOffsetAndLength(_blockSize1).Offset ?? 0;
        } else {
            // if we get a max sample position, back off our valid length to match
            if (samplePosition is { } pos && packetIsEndOfStream) {
                long actualEnd = _samplePosition / _channelCount + packetValidLength - packetStartIndex;
                int diff = (int)((long)pos - actualEnd);
                if (diff < 0) {
                    packetValidLength += diff;
                }
            }

            if (_prevPacketRange is { } r) {
                Span2D<float> prev2d = _prevPacketBuf.AsSpan2D()[.., r];
                Span2D<float> next2d = _packetBuf.AsSpan2D()[.., ..prev2d.Height];
                if (next2d.TryGetSpan(out Span<float> next) && prev2d.TryGetSpan(out Span<float> prev)) {
                    next.Add(prev);
                } else {
                    // couldn't do this with vectorization, fallback to nested for loops
                    for (int i = 0; i < next2d.Width; i++) {
                        for (int j = 0; j < next2d.Height; j++) {
                            next2d[i, j] += prev2d[i, j];
                        }
                    }
                }

                prevStart = packetStartIndex;
            } else {
                prevStart = packetValidLength;
            }

            _prevPacketRange = prevStart..packetTotalLength;
            (_packetBuf, _prevPacketBuf) = (_prevPacketBuf, _packetBuf);
        }

        if (packetValidLength - prevStart > 0)
            WriteSamples(packetValidLength - prevStart);
    }

    private void WriteSamples(int length) {
        Range prevPacketRange = _prevPacketRange!.Value;
        Index end = prevPacketRange.End;
        (int offset, int l) = prevPacketRange.GetOffsetAndLength(_blockSize1);
        length = int.Min(l, length);
        Span<float> span = _writer.GetSpan(length * _channelCount);
        Span2D<float> src = _prevPacketBuf.AsSpan2D()[.., offset..(offset + length)];
        int idx = 0;
        int i = 0;
        if (_clipSamples) {
            for (; i < src.Height; i++, offset++) {
                for (int j = 0; j < src.Width; j++) {
                    span[idx++] = MathExtensions.ClipValue(src[j, i], ref _hasClipped);
                }
            }
        } else {
            for (; i < src.Height; i++, offset++) {
                for (int j = 0; j < src.Width; j++) {
                    span[idx++] = src[j, i];
                }
            }
        }

        _writer.Advance(idx);

        _samplePosition += i;
        _prevPacketRange = offset..end;
    }

    private bool DecodeNextPacket(ref BitReader reader, ulong pageGranulePosition, out int packetStartIndex,
        out int packetValidLength, out int packetTotalLength, out ulong? samplePosition) {
        Mode mode = _modes[(int)reader.ReadBits(_modeBits)];
        if (mode.Decode(ref reader, _packetBuf, out packetStartIndex, out packetValidLength,
                out packetTotalLength)) {
            samplePosition = pageGranulePosition;
            return true;
        }

        samplePosition = null;
        return false;
    }

    private void ReadIdentHeader(ref BitReader reader) {
        _vorbisVersion = reader.ReadNumeric<uint>();
        _channelCount = reader.ReadNumeric<byte>();
        _sampleRate = reader.ReadNumeric<uint>();
        _bitRateMax = reader.ReadNumeric<uint>();
        _bitRateNominal = reader.ReadNumeric<uint>();
        _bitRateMin = reader.ReadNumeric<uint>();
        _blockSize0 = (ushort)(1 << (int)reader.ReadBits(4));
        _blockSize1 = (ushort)(1 << (int)reader.ReadBits(4));
        if (!reader.ReadBit())
            throw new InvalidDataException();
        _hasIdent = true;
    }

    private void ReadCommentHeader(ref BitReader reader) {
        static void ReadCommentVector(ref BitReader reader, out string key, out string value) {
            uint len = reader.ReadNumeric<uint>();
            Span<byte> octets = stackalloc byte[(int)len];
            int keyLen = 0;
            for (int i = 0; i < len; i++) {
                byte octet = reader.ReadNumeric<byte>();
                if (octet is 0x3D && keyLen is 0) {
                    keyLen = i;
                }

                octets[i] = octet;
            }

            key = Encoding.UTF8.GetString(octets[..keyLen]);
            value = Encoding.UTF8.GetString(octets[(keyLen + 1)..]);
        }

        uint vendorLen = reader.ReadNumeric<uint>();
        Span<byte> octets = stackalloc byte[(int)vendorLen];
        for (int i1 = 0; i1 < vendorLen; i1++) {
            octets[i1] = reader.ReadNumeric<byte>();
        }

        _vendorString = Encoding.UTF8.GetString(octets);

        uint count = reader.ReadNumeric<uint>();
        Dictionary<string, string> comments = new(StringComparer.InvariantCultureIgnoreCase);
        for (int i = 0; i < count; i++) {
            ReadCommentVector(ref reader, out string key, out string val);
            comments.Add(key, val);
        }

        _comments = comments.ToFrozenDictionary();

        if (!reader.ReadBit())
            throw new InvalidDataException();

        _hasComments = true;
    }

    private void ReadSetupHeader(ref BitReader reader) {
        _huffman = new Huffman();
        int codebookCount = (int)reader.ReadBits(8) + 1;
        Codebook[] codebooks = new Codebook[codebookCount];
        for (int i = 0; i < codebookCount; i++) {
            codebooks[i] = new Codebook(ref reader, _huffman);
        }

        reader.TryAdvance(16 * ((int)reader.ReadBits(6) + 1));

        int floorCount = (int)reader.ReadBits(6) + 1;
        IFloor[] floors = new IFloor[floorCount];
        for (int i = 0; i < floorCount; i++) {
            floors[i] = reader.ReadNumeric<uint>() switch {
                0 => new Floor0(ref reader, _blockSize0, _blockSize1, codebooks),
                1 => new Floor1(ref reader, codebooks),
                _ => throw new InvalidDataException(),
            };
        }

        Residue[] residues = new Residue[reader.ReadBits(6) + 1];
        for (int i = 0; i < residues.Length; i++) {
            residues[i] = new Residue(ref reader, _channelCount, codebooks);
        }

        Mapping[] mappings = new Mapping[reader.ReadBits(6) + 1];
        for (int i = 0; i < mappings.Length; i++) {
            mappings[i] = new Mapping(ref reader, _channelCount, floors, residues);
        }

        // read the modes
        Mode[] modes = new Mode[reader.ReadBits(6) + 1];
        for (int i = 0; i < modes.Length; i++) {
            modes[i] = new Mode(ref reader, _channelCount, _blockSize0, _blockSize1, mappings);
        }

        _modes = modes;

        // verify the closing bit
        if (!reader.ReadBit())
            throw new InvalidDataException("Book packet did not end on correct bit!");

        _modeBits = MathExtensions.ILog(_modes.Length - 1);

        _packetBuf = new float[_channelCount, _blockSize1];
        _prevPacketBuf = new float[_channelCount, _blockSize1];

        _hasSetup = true;
    }

    internal int GetPacketGranuleCount(ref BitReader reader, bool isLastInPage) {
        try {
            if (reader.ReadBit()) return 0;
            // first we need to know which mode...
            int modeIdx = (int)reader.ReadBits(_modeBits);

            // if we got an invalid mode value, we can't decode any audio data anyway...
            if (modeIdx < 0 || modeIdx >= _modes.Length) return 0;

            return _modes[modeIdx].GetPacketSampleCount(ref reader, isLastInPage);
        }
        finally {
            reader.Reset();
        }
    }

    private readonly PacketGranuleCountFunc _getPacketGranuleCount;
    private          bool                   _hasPosition;

    /// <summary>
    /// Seeks the stream by the specified duration.
    /// </summary>
    /// <param name="seek"></param>
    /// <param name="timePosition">The relative time to seek to.</param>
    /// <param name="seekOrigin">The reference point used to obtain the new position.</param>
    public void SeekTo(IGranuleSeekable seek, TimeSpan timePosition, SeekOrigin seekOrigin = SeekOrigin.Begin) {
        SeekTo(seek, (long)(_sampleRate * timePosition.TotalSeconds), seekOrigin);
    }

    /// <summary>
    /// Seeks the stream by the specified sample count.
    /// </summary>
    /// <param name="seek"></param>
    /// <param name="samplePosition">The relative sample position to seek to.</param>
    /// <param name="seekOrigin">The reference point used to obtain the new position.</param>
    public void SeekTo(IGranuleSeekable seek, long samplePosition, SeekOrigin seekOrigin = SeekOrigin.Begin) {
        long origSamplePos = samplePosition;
        switch (seekOrigin) {
            case SeekOrigin.Begin:
                // no-op
                break;
            case SeekOrigin.Current:
                samplePosition = _samplePosition - samplePosition;
                break;
            case SeekOrigin.End:
                samplePosition = (long)seek.TotalGranules - samplePosition;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(seekOrigin), seekOrigin, "");
        }

        if (samplePosition < 0) throw new ArgumentOutOfRangeException(nameof(samplePosition), origSamplePos, "");

        int rollForward;
        if (samplePosition == 0) {
            // seek to origin, short circuit
            seek.SeekTo(0, 0, _getPacketGranuleCount);
            rollForward = 0;
        } else {
            ulong pos = seek.SeekTo((ulong)samplePosition, 1, _getPacketGranuleCount);
            rollForward = (int)(samplePosition - (long)pos);
        }

        ResetDecoder();
        _hasPosition = true;
    }

    private void ResetDecoder() {
        _prevPacketRange = null;
        _hasClipped = false;
        _hasPosition = false;
    }
}
using System.Collections.ObjectModel;
using System.Text;
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

    private uint                                _vorbisVersion;
    private byte                                _channelCount;
    private uint                                _sampleRate;
    private uint                                _bitRateMax;
    private uint                                _bitRateNominal;
    private uint                                _bitRateMin;
    private ushort                              _blockSize0;
    private ushort                              _blockSize1;
    private string                              _vendorString = null!;
    private IReadOnlyDictionary<string, string> _comments     = null!;
    private bool                                _hasIdent;
    private bool                                _hasComments;
    private bool                                _hasSetup;
    private Codebook[]                          _codebooks = null!;

    private Huffman _huffman = null!;

    private void ReadPacket(ref BitReader reader) {
        switch (DetectPacketType(ref reader)) {
            case VorbisPacketType.Audio:
                ReadAudio(ref reader);
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

    private void ReadAudio(ref BitReader reader) {
        if (!_hasIdent || !_hasComments || !_hasSetup)
            throw new InvalidDataException();
        throw new NotImplementedException();
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

        _comments = new ReadOnlyDictionary<string, string>(comments);

        if (!reader.ReadBit())
            throw new InvalidDataException();

        _hasComments = true;
    }

    private void ReadSetupHeader(ref BitReader reader) {
        _huffman = new Huffman();
        int codebookCount = (int)reader.ReadBits(8) + 1;
        _codebooks = new Codebook[codebookCount];
        for (int i = 0; i < codebookCount; i++) {
            _codebooks[i] = new Codebook(ref reader, _huffman);
        }

        reader.TryAdvance(16 * ((int)reader.ReadBits(6) + 1));

        int floors = (int)reader.ReadBits(6) + 1;
        
        throw new NotImplementedException();

        _hasSetup = true;
    }
}
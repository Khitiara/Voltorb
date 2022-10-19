namespace Voltorb.Vorbis;

public class VorbisConstants
{
    // reminder that this compiles to a direct pointer to the .data block of the executable
    // and is both fast and allocation-free
    /// <summary>
    /// The bytes at positions 1..7 of any vorbis packet header
    /// </summary>
    public static ReadOnlySpan<byte> VorbisHeaderString => "vorbis"u8;

    public const ulong VorbisHeaderBitstring = 0x73_69_62_72_6F_76;
}
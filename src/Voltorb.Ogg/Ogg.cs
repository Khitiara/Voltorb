namespace Voltorb.Ogg;

public static class Ogg
{
    public static ReadOnlySpan<byte> CapturePattern => "OggS"u8;
}
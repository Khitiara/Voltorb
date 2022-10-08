namespace Voltorb.Ogg;

public static class OggConstants
{
    // This literal is compiled to a .data entry and no runtime conversion will take place
    // See https://github.com/dotnet/csharplang/blob/main/proposals/csharp-11.0/utf8-string-literals.md for more.
    /// <summary>
    /// The Ogg capture pattern bytes, which begin every ogg page
    /// </summary>
    public static ReadOnlySpan<byte> CapturePattern => "OggS"u8;
}
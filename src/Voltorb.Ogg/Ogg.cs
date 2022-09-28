namespace Voltorb.Ogg;

public static class Ogg
{
    // I'm sorry if your IDE or editor marks this as a syntax error. utf8 string literals are part of c# 11 but not
    // yet supported by RIDER at least, possibly others. This literal is compiled to a .data entry and no runtime
    // conversion will take place
    // See https://github.com/dotnet/csharplang/blob/main/proposals/csharp-11.0/utf8-string-literals.md for more.
    /// <summary>
    /// The Ogg capture pattern bytes, which begin every ogg page
    /// </summary>
    public static ReadOnlySpan<byte> CapturePattern => "OggS"u8;
}
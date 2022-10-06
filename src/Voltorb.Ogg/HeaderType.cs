namespace Voltorb.Ogg;

/// <summary>
/// Special ogg page header flags
/// </summary>
[Flags]
public enum HeaderType
{
    None                = 0x00,
    ContinuesPacket     = 0x01,
    BeginsLogicalStream = 0x02,
    EndsLogicalStream   = 0x04,
}

public static class HeaderTypeExtensions
{
    public static bool HasFlagFast(this HeaderType value, HeaderType flag) => (value & flag) != 0;
}
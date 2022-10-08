using System.Numerics;

namespace Voltorb.Bits;

internal class MathExtensions
{
    public static T OmniLeftShift<T>(T value, int by)
        where T : IComparisonOperators<T, T, bool>, IShiftOperators<T, int, T>
        => by > 0 ? value << by : value >> -by;
}
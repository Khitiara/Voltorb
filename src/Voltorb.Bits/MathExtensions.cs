using System.Numerics;
using System.Runtime.CompilerServices;

namespace Voltorb.Bits;

internal static class MathExtensions
{
    /// <summary>
    /// Left shift, but shifts right on negative shify-by instead of undefined behavior
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReversibleLeftShift<T>(T value, int by)
        where T : IComparisonOperators<T, T, bool>, IShiftOperators<T, int, T>
        => by > 0 ? value << by : value >> -by;
}
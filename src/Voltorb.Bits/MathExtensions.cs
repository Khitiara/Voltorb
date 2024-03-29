﻿using System.Numerics;
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

    public static int ILog(int i) => i switch {
        <= 0 => 0,
        _ => 1 + BitOperations.Log2((uint)i),
    };

    public static uint BitReverse(uint n) => BitReverse(n, 32);

    public static uint BitReverse(uint n, int bits) {
        n = ((n & 0xAAAAAAAA) >> 1) | ((n & 0x55555555) << 1);
        n = ((n & 0xCCCCCCCC) >> 2) | ((n & 0x33333333) << 2);
        n = ((n & 0xF0F0F0F0) >> 4) | ((n & 0x0F0F0F0F) << 4);
        n = ((n & 0xFF00FF00) >> 8) | ((n & 0x00FF00FF) << 8);
        return ((n >> 16) | (n << 16)) >> (32 - bits);
    }

    internal static float ConvertFromVorbisFloat32(uint bits) {
        // do as much as possible with bit tricks in integer math
        // sign-extend to the full 32-bits
        int sign = (int)bits >> 31;
        // grab the exponent, remove the bias, store as double (for the call to System.Math.Pow(...))
        float exponent = ((bits & 0x7fe00000) >> 21) - 788;
        // grab the mantissa and apply the sign bit.  store as float
        float mantissa = ((bits & 0x1fffff) ^ sign) + (sign & 1);

        // NB: We could use bit tricks to calc the exponent, but it can't be more than 63 in either direction.
        //     This creates an issue, since the exponent field allows for a *lot* more than that.
        //     On the flip side, larger exponent values don't seem to be used by the Vorbis codebooks...
        //     Either way, we'll play it safe and let the BCL calculate it.

        return mantissa * MathF.Pow(2.0f, exponent);
    }
    

    internal static float ClipValue(float value, ref bool clipped)
    {
        if (value > .99999994f)
        {
            clipped = true;
            return 0.99999994f;
        }
        if (value < -.99999994f)
        {
            clipped = true;
            return -0.99999994f;
        }
        return value;
    }
}
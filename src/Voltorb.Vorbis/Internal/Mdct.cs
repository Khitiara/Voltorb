using Voltorb.Bits;

namespace Voltorb.Vorbis.Internal;

internal static class Mdct 
{
    private static readonly Dictionary<int, MdctImpl> SetupCache = new();

    public static void Reverse(Span<float> samples, int sampleCount)
    {
        if (!SetupCache.TryGetValue(sampleCount, out MdctImpl? impl))
        {
            impl = new MdctImpl(sampleCount);
            SetupCache[sampleCount] = impl;
        }
        impl.CalcReverse(samples);
    }

    // TODO: I'm not smart enough to vectorize this
    private sealed class MdctImpl
    {
        private readonly int _n, _n2, _n4, _n8, _ld;

        private readonly float[]  _a, _b, _c;
        private readonly ushort[] _bitrev;

        public MdctImpl(int n)
        {
            _n = n;
            _n2 = n >> 1;
            _n4 = _n2 >> 1;
            _n8 = _n4 >> 1;

            _ld = MathExtensions.ILog(n) - 1;

            // first, calc the "twiddle factors"
            _a = new float[_n2];
            _b = new float[_n2];
            _c = new float[_n4];
            int k, k2;
            for (k = k2 = 0; k < _n4; ++k, k2 += 2)
            {
                _a[k2] = MathF.Cos(4 * k * MathF.PI / n);
                _a[k2 + 1] = -MathF.Sin(4 * k * MathF.PI / n);
                _b[k2] = MathF.Cos((k2 + 1) * MathF.PI / n / 2) * .5f;
                _b[k2 + 1] = MathF.Sin((k2 + 1) * MathF.PI / n / 2) * .5f;
            }
            for (k = k2 = 0; k < _n8; ++k, k2 += 2)
            {
                _c[k2] = MathF.Cos(2 * (k2 + 1) * MathF.PI / n);
                _c[k2 + 1] = -MathF.Sin(2 * (k2 + 1) * MathF.PI / n);
            }

            // now, calc the bit reverse table
            _bitrev = new ushort[_n8];
            for (int i = 0; i < _n8; ++i)
            {
                _bitrev[i] = (ushort)(MathExtensions.BitReverse((uint)i, _ld - 3) << 2);
            }
        }

        internal void CalcReverse(Span<float> buffer)
        {
            Span<float> buf2 = stackalloc float[_n2];

            // copy and reflect spectral data
            // step 0

            {
                int d = _n2 - 2; // buf2
                int aa = 0;     // A
                int e = 0;      // buffer
                while (e != _n2)
                {
                    buf2[d + 1] = (buffer[e] * _a[aa] - buffer[e + 2] * _a[aa + 1]);
                    buf2[d] = (buffer[e] * _a[aa + 1] + buffer[e + 2] * _a[aa]);
                    d -= 2;
                    aa += 2;
                    e += 4;
                }

                e = _n2 - 3;
                while (d >= 0)
                {
                    buf2[d + 1] = (-buffer[e + 2] * _a[aa] - -buffer[e] * _a[aa + 1]);
                    buf2[d] = (-buffer[e + 2] * _a[aa + 1] + -buffer[e] * _a[aa]);
                    d -= 2;
                    aa += 2;
                    e -= 4;
                }
            }

            // step 2
            {
                int aa = _n2 - 8;    // A

                int e0 = _n4;        // v
                int e1 = 0;         // v

                int d0 = _n4;        // u
                int d1 = 0;         // u

                while (aa >= 0)
                {
                    float v4121 = buf2[e0 + 1] - buf2[e1 + 1];
                    float v4020 = buf2[e0] - buf2[e1];
                    buffer[d0 + 1] = buf2[e0 + 1] + buf2[e1 + 1];
                    buffer[d0] = buf2[e0] + buf2[e1];
                    buffer[d1 + 1] = v4121 * _a[aa + 4] - v4020 * _a[aa + 5];
                    buffer[d1] = v4020 * _a[aa + 4] + v4121 * _a[aa + 5];

                    v4121 = buf2[e0 + 3] - buf2[e1 + 3];
                    v4020 = buf2[e0 + 2] - buf2[e1 + 2];
                    buffer[d0 + 3] = buf2[e0 + 3] + buf2[e1 + 3];
                    buffer[d0 + 2] = buf2[e0 + 2] + buf2[e1 + 2];
                    buffer[d1 + 3] = v4121 * _a[aa] - v4020 * _a[aa + 1];
                    buffer[d1 + 2] = v4020 * _a[aa] + v4121 * _a[aa + 1];

                    aa -= 8;

                    d0 += 4;
                    d1 += 4;
                    e0 += 4;
                    e1 += 4;
                }
            }

            // step 3

            // iteration 0
            step3_iter0_loop(_n >> 4, buffer, _n2 - 1 - _n4 * 0, -_n8);
            step3_iter0_loop(_n >> 4, buffer, _n2 - 1 - _n4 * 1, -_n8);

            // iteration 1
            step3_inner_r_loop(_n >> 5, buffer, _n2 - 1 - _n8 * 0, -(_n >> 4), 16);
            step3_inner_r_loop(_n >> 5, buffer, _n2 - 1 - _n8 * 1, -(_n >> 4), 16);
            step3_inner_r_loop(_n >> 5, buffer, _n2 - 1 - _n8 * 2, -(_n >> 4), 16);
            step3_inner_r_loop(_n >> 5, buffer, _n2 - 1 - _n8 * 3, -(_n >> 4), 16);

            // iterations 2 ... x
            int l = 2;
            for (; l < (_ld - 3) >> 1; ++l)
            {
                int k0 = _n >> (l + 2);
                int k02 = k0 >> 1;
                int lim = 1 << (l + 1);
                for (int i = 0; i < lim; ++i)
                {
                    step3_inner_r_loop(_n >> (l + 4), buffer, _n2 - 1 - k0 * i, -k02, 1 << (l + 3));
                }
            }

            // iterations x ... end
            for (; l < _ld - 6; ++l)
            {
                int k0 = _n >> (l + 2);
                int k1 = 1 << (l + 3);
                int k02 = k0 >> 1;
                int rLim = _n >> (l + 6);
                int lim = 1 << l + 1;
                int iOff = _n2 - 1;
                int a0 = 0;

                for (int r = rLim; r > 0; --r)
                {
                    step3_inner_s_loop(lim, buffer, iOff, -k02, a0, k1, k0);
                    a0 += k1 * 4;
                    iOff -= 8;
                }
            }

            // combine some iteration steps...
            step3_inner_s_loop_ld654(_n >> 5, buffer, _n2 - 1, _n);

            // steps 4, 5, and 6
            {
                int bit = 0;

                int d0 = _n4 - 4;    // v
                int d1 = _n2 - 4;    // v
                while (d0 >= 0)
                {
                    int k4 = _bitrev[bit];
                    buf2[d1 + 3] = buffer[k4];
                    buf2[d1 + 2] = buffer[k4 + 1];
                    buf2[d0 + 3] = buffer[k4 + 2];
                    buf2[d0 + 2] = buffer[k4 + 3];

                    k4 = _bitrev[bit + 1];
                    buf2[d1 + 1] = buffer[k4];
                    buf2[d1] = buffer[k4 + 1];
                    buf2[d0 + 1] = buffer[k4 + 2];
                    buf2[d0] = buffer[k4 + 3];

                    d0 -= 4;
                    d1 -= 4;
                    bit += 2;
                }
            }

            // step 7
            {
                int c = 0;      // C
                int d = 0;      // v
                int e = _n2 - 4; // v

                while (d < e)
                {
                    float a02 = buf2[d] - buf2[e + 2];
                    float a11 = buf2[d + 1] + buf2[e + 3];

                    float b0 = _c[c + 1] * a02 + _c[c] * a11;
                    float b1 = _c[c + 1] * a11 - _c[c] * a02;

                    float b2 = buf2[d] + buf2[e + 2];
                    float b3 = buf2[d + 1] - buf2[e + 3];

                    buf2[d] = b2 + b0;
                    buf2[d + 1] = b3 + b1;
                    buf2[e + 2] = b2 - b0;
                    buf2[e + 3] = b1 - b3;

                    a02 = buf2[d + 2] - buf2[e];
                    a11 = buf2[d + 3] + buf2[e + 1];

                    b0 = _c[c + 3] * a02 + _c[c + 2] * a11;
                    b1 = _c[c + 3] * a11 - _c[c + 2] * a02;

                    b2 = buf2[d + 2] + buf2[e];
                    b3 = buf2[d + 3] - buf2[e + 1];

                    buf2[d + 2] = b2 + b0;
                    buf2[d + 3] = b3 + b1;
                    buf2[e] = b2 - b0;
                    buf2[e + 1] = b1 - b3;

                    c += 4;
                    d += 4;
                    e -= 4;
                }
            }

            // step 8 + decode
            {
                int b = _n2 - 8; // B
                int e = _n2 - 8; // buf2
                int d0 = 0;     // buffer
                int d1 = _n2 - 4;// buffer
                int d2 = _n2;    // buffer
                int d3 = _n - 4; // buffer
                while (e >= 0)
                {
                    float p3 = buf2[e + 6] * _b[b + 7] - buf2[e + 7] * _b[b + 6];
                    float p2 = -buf2[e + 6] * _b[b + 6] - buf2[e + 7] * _b[b + 7];

                    buffer[d0] = p3;
                    buffer[d1 + 3] = -p3;
                    buffer[d2] = p2;
                    buffer[d3 + 3] = p2;

                    float p1 = buf2[e + 4] * _b[b + 5] - buf2[e + 5] * _b[b + 4];
                    float p0 = -buf2[e + 4] * _b[b + 4] - buf2[e + 5] * _b[b + 5];

                    buffer[d0 + 1] = p1;
                    buffer[d1 + 2] = -p1;
                    buffer[d2 + 1] = p0;
                    buffer[d3 + 2] = p0;


                    p3 = buf2[e + 2] * _b[b + 3] - buf2[e + 3] * _b[b + 2];
                    p2 = -buf2[e + 2] * _b[b + 2] - buf2[e + 3] * _b[b + 3];

                    buffer[d0 + 2] = p3;
                    buffer[d1 + 1] = -p3;
                    buffer[d2 + 2] = p2;
                    buffer[d3 + 1] = p2;

                    p1 = buf2[e] * _b[b + 1] - buf2[e + 1] * _b[b];
                    p0 = -buf2[e] * _b[b] - buf2[e + 1] * _b[b + 1];

                    buffer[d0 + 3] = p1;
                    buffer[d1] = -p1;
                    buffer[d2 + 3] = p0;
                    buffer[d3] = p0;

                    b -= 8;
                    e -= 8;
                    d0 += 4;
                    d2 += 4;
                    d1 -= 4;
                    d3 -= 4;
                }
            }
        }

        private void step3_iter0_loop(int n, Span<float> e, int iOff, int kOff)
        {
            int ee0 = iOff;        // e
            int ee2 = ee0 + kOff;  // e
            int a = 0;
            for (int i = n >> 2; i > 0; --i)
            {
                float k0020 = e[ee0] - e[ee2];
                float k0121 = e[ee0 - 1] - e[ee2 - 1];
                e[ee0] += e[ee2];
                e[ee0 - 1] += e[ee2 - 1];
                e[ee2] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[ee2 - 1] = k0121 * _a[a] + k0020 * _a[a + 1];
                a += 8;

                k0020 = e[ee0 - 2] - e[ee2 - 2];
                k0121 = e[ee0 - 3] - e[ee2 - 3];
                e[ee0 - 2] += e[ee2 - 2];
                e[ee0 - 3] += e[ee2 - 3];
                e[ee2 - 2] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[ee2 - 3] = k0121 * _a[a] + k0020 * _a[a + 1];
                a += 8;

                k0020 = e[ee0 - 4] - e[ee2 - 4];
                k0121 = e[ee0 - 5] - e[ee2 - 5];
                e[ee0 - 4] += e[ee2 - 4];
                e[ee0 - 5] += e[ee2 - 5];
                e[ee2 - 4] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[ee2 - 5] = k0121 * _a[a] + k0020 * _a[a + 1];
                a += 8;

                k0020 = e[ee0 - 6] - e[ee2 - 6];
                k0121 = e[ee0 - 7] - e[ee2 - 7];
                e[ee0 - 6] += e[ee2 - 6];
                e[ee0 - 7] += e[ee2 - 7];
                e[ee2 - 6] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[ee2 - 7] = k0121 * _a[a] + k0020 * _a[a + 1];
                a += 8;

                ee0 -= 8;
                ee2 -= 8;
            }
        }

        private void step3_inner_r_loop(int lim, Span<float> e, int d0, int kOff, int k1)
        {
            int e0 = d0;            // e
            int e2 = e0 + kOff;    // e
            int a = 0;

            for (int i = lim >> 2; i > 0; --i)
            {
                float k0020 = e[e0] - e[e2];
                float k0121 = e[e0 - 1] - e[e2 - 1];
                e[e0] += e[e2];
                e[e0 - 1] += e[e2 - 1];
                e[e2] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[e2 - 1] = k0121 * _a[a] + k0020 * _a[a + 1];

                a += k1;

                k0020 = e[e0 - 2] - e[e2 - 2];
                k0121 = e[e0 - 3] - e[e2 - 3];
                e[e0 - 2] += e[e2 - 2];
                e[e0 - 3] += e[e2 - 3];
                e[e2 - 2] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[e2 - 3] = k0121 * _a[a] + k0020 * _a[a + 1];

                a += k1;

                k0020 = e[e0 - 4] - e[e2 - 4];
                k0121 = e[e0 - 5] - e[e2 - 5];
                e[e0 - 4] += e[e2 - 4];
                e[e0 - 5] += e[e2 - 5];
                e[e2 - 4] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[e2 - 5] = k0121 * _a[a] + k0020 * _a[a + 1];

                a += k1;

                k0020 = e[e0 - 6] - e[e2 - 6];
                k0121 = e[e0 - 7] - e[e2 - 7];
                e[e0 - 6] += e[e2 - 6];
                e[e0 - 7] += e[e2 - 7];
                e[e2 - 6] = k0020 * _a[a] - k0121 * _a[a + 1];
                e[e2 - 7] = k0121 * _a[a] + k0020 * _a[a + 1];

                a += k1;

                e0 -= 8;
                e2 -= 8;
            }
        }

        private void step3_inner_s_loop(int n, Span<float> e, int iOff, int kOff, int a, int aOff, int k0)
        {
            float a0 = _a[a];
            float a1 = _a[a + 1];
            float a2 = _a[a + aOff];
            float a3 = _a[a + aOff + 1];
            float a4 = _a[a + aOff * 2];
            float a5 = _a[a + aOff * 2 + 1];
            float a6 = _a[a + aOff * 3];
            float a7 = _a[a + aOff * 3 + 1];

            int ee0 = iOff;        // e
            int ee2 = ee0 + kOff;  // e

            for (int i = n; i > 0; --i)
            {
                float k00 = e[ee0] - e[ee2];
                float k11 = e[ee0 - 1] - e[ee2 - 1];
                e[ee0] += e[ee2];
                e[ee0 - 1] += e[ee2 - 1];
                e[ee2] = k00 * a0 - k11 * a1;
                e[ee2 - 1] = k11 * a0 + k00 * a1;

                k00 = e[ee0 - 2] - e[ee2 - 2];
                k11 = e[ee0 - 3] - e[ee2 - 3];
                e[ee0 - 2] += e[ee2 - 2];
                e[ee0 - 3] += e[ee2 - 3];
                e[ee2 - 2] = k00 * a2 - k11 * a3;
                e[ee2 - 3] = k11 * a2 + k00 * a3;

                k00 = e[ee0 - 4] - e[ee2 - 4];
                k11 = e[ee0 - 5] - e[ee2 - 5];
                e[ee0 - 4] += e[ee2 - 4];
                e[ee0 - 5] += e[ee2 - 5];
                e[ee2 - 4] = k00 * a4 - k11 * a5;
                e[ee2 - 5] = k11 * a4 + k00 * a5;

                k00 = e[ee0 - 6] - e[ee2 - 6];
                k11 = e[ee0 - 7] - e[ee2 - 7];
                e[ee0 - 6] += e[ee2 - 6];
                e[ee0 - 7] += e[ee2 - 7];
                e[ee2 - 6] = k00 * a6 - k11 * a7;
                e[ee2 - 7] = k11 * a6 + k00 * a7;

                ee0 -= k0;
                ee2 -= k0;
            }
        }

        private void step3_inner_s_loop_ld654(int n, Span<float> e, int iOff, int baseN)
        {
            int aOff = baseN >> 3;
            float a2 = _a[aOff];
            int z = iOff;          // e
            int @base = z - 16 * n; // e

            while (z > @base)
            {
                float k00 = e[z] - e[z - 8];
                float k11 = e[z - 1] - e[z - 9];
                e[z] += e[z - 8];
                e[z - 1] += e[z - 9];
                e[z - 8] = k00;
                e[z - 9] = k11;

                k00 = e[z - 2] - e[z - 10];
                k11 = e[z - 3] - e[z - 11];
                e[z - 2] += e[z - 10];
                e[z - 3] += e[z - 11];
                e[z - 10] = (k00 + k11) * a2;
                e[z - 11] = (k11 - k00) * a2;

                k00 = e[z - 12] - e[z - 4];
                k11 = e[z - 5] - e[z - 13];
                e[z - 4] += e[z - 12];
                e[z - 5] += e[z - 13];
                e[z - 12] = k11;
                e[z - 13] = k00;

                k00 = e[z - 14] - e[z - 6];
                k11 = e[z - 7] - e[z - 15];
                e[z - 6] += e[z - 14];
                e[z - 7] += e[z - 15];
                e[z - 14] = (k00 + k11) * a2;
                e[z - 15] = (k00 - k11) * a2;

                iter_54(e, z);
                iter_54(e, z - 8);

                z -= 16;
            }
        }

        private static void iter_54(Span<float> e, int z)
        {
            float k00 = e[z] - e[z - 4];
            float y0 = e[z] + e[z - 4];
            float y2 = e[z - 2] + e[z - 6];
            float k22 = e[z - 2] - e[z - 6];

            e[z] = y0 + y2;
            e[z - 2] = y0 - y2;

            float k33 = e[z - 3] - e[z - 7];

            e[z - 4] = k00 + k33;
            e[z - 6] = k00 - k33;

            float k11 = e[z - 1] - e[z - 5];
            float y1 = e[z - 1] + e[z - 5];
            float y3 = e[z - 3] + e[z - 7];

            e[z - 1] = y1 + y3;
            e[z - 3] = y1 - y3;
            e[z - 5] = k11 - k22;
            e[z - 7] = k11 + k22;
        }
    }
}
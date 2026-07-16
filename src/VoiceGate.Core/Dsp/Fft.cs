namespace VoiceGate.Dsp;

/// <summary>
/// In-place iterative radix-2 complex FFT.
/// Forward transform applies no scaling; inverse scales by 1/N,
/// so Transform(forward) followed by Transform(inverse) is the identity.
/// </summary>
public static class Fft
{
    public static void Transform(float[] re, float[] im, bool forward)
    {
        int n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two and re/im must match.");

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j |= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2.0 * Math.PI / len * (forward ? -1.0 : 1.0);
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                float curR = 1f, curI = 0f;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = i + k + half;
                    float vr = re[b] * curR - im[b] * curI;
                    float vi = re[b] * curI + im[b] * curR;
                    re[b] = re[a] - vr;
                    im[b] = im[a] - vi;
                    re[a] += vr;
                    im[a] += vi;
                    float nr = curR * wr - curI * wi;
                    curI = curR * wi + curI * wr;
                    curR = nr;
                }
            }
        }

        if (!forward)
        {
            float inv = 1f / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= inv;
                im[i] *= inv;
            }
        }
    }
}

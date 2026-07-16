using NAudio.Wave;
using VoiceGate.Dsp;
using VoiceGate.Speech;
using Xunit;

namespace VoiceGate.Tests;

public class FftTests
{
    [Fact]
    public void RoundTrip_IsIdentity()
    {
        var rng = new Random(42);
        int n = 1024;
        var re = new float[n];
        var im = new float[n];
        var orig = new float[n];
        for (int i = 0; i < n; i++)
            orig[i] = re[i] = (float)(rng.NextDouble() * 2 - 1);

        Fft.Transform(re, im, forward: true);
        Fft.Transform(re, im, forward: false);

        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(re[i] - orig[i]) < 1e-4, $"re[{i}] diverged: {re[i]} vs {orig[i]}");
            Assert.True(Math.Abs(im[i]) < 1e-4, $"im[{i}] should be ~0: {im[i]}");
        }
    }

    [Fact]
    public void Sine_PeaksAtItsBin()
    {
        int n = 1024, bin = 10;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
            re[i] = (float)Math.Sin(2 * Math.PI * bin * i / n);

        Fft.Transform(re, im, forward: true);

        int maxBin = 0;
        double maxMag = 0;
        for (int k = 0; k < n / 2; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            if (mag > maxMag)
            {
                maxMag = mag;
                maxBin = k;
            }
        }
        Assert.Equal(bin, maxBin);
    }
}

public class FloatRingBufferTests
{
    [Fact]
    public void PreservesOrder()
    {
        var ring = new FloatRingBuffer(256);
        var data = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        ring.Write(data);
        var dest = new float[100];
        Assert.Equal(100, ring.Read(dest));
        Assert.Equal(data, dest);
        Assert.Equal(0, ring.Available);
    }

    [Fact]
    public void Overflow_DropsOldest()
    {
        var ring = new FloatRingBuffer(100);
        var data = Enumerable.Range(0, 150).Select(i => (float)i).ToArray();
        ring.Write(data);
        Assert.Equal(100, ring.Available);
        var dest = new float[100];
        ring.Read(dest);
        Assert.Equal(50f, dest[0]); // first 50 were overwritten
        Assert.Equal(149f, dest[99]);
    }
}

public class DelayLineTests
{
    [Fact]
    public void DelaysByExactSampleCount()
    {
        var delay = new DelayLine(20);
        var block = new float[100];
        block[5] = 1f;
        delay.Process(block);
        Assert.Equal(0f, block[5]);
        Assert.Equal(1f, block[25]);
    }

    [Fact]
    public void ZeroDelay_IsPassthrough()
    {
        var delay = new DelayLine(0);
        var block = new float[] { 1, 2, 3 };
        delay.Process(block);
        Assert.Equal(new float[] { 1, 2, 3 }, block);
    }
}

public class GateSmootherTests
{
    [Fact]
    public void OpensWithinAttackWindow()
    {
        var smoother = new GateSmoother(48000, attackMs: 5, releaseMs: 100);
        var block = new float[48000 / 10]; // 100 ms of ones
        Array.Fill(block, 1f);
        smoother.Process(block, target: 1f);
        Assert.True(smoother.CurrentGain > 0.99f, $"gain after 100ms: {smoother.CurrentGain}");
        Assert.True(block[^1] > 0.99f);
    }

    [Fact]
    public void ClosesTowardZeroOnRelease()
    {
        var smoother = new GateSmoother(48000, 5, 50);
        var block = new float[4800];
        Array.Fill(block, 1f);
        smoother.Process(block, 1f);
        var block2 = new float[48000 / 2]; // 500 ms >> 50 ms release
        Array.Fill(block2, 1f);
        smoother.Process(block2, 0f);
        Assert.True(smoother.CurrentGain < 0.01f, $"gain after release: {smoother.CurrentGain}");
    }
}

public class StreamResamplerTests
{
    [Fact]
    public void Produces_CorrectSampleRatio_AndPreservesFrequency()
    {
        var resampler = new StreamResampler(48000, 16000);
        int seconds = 2;
        var output = new List<float>();
        var outBuf = new float[8192];
        var block = new float[480];
        int total = 48000 * seconds;
        for (int start = 0; start < total; start += block.Length)
        {
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)Math.Sin(2 * Math.PI * 1000 * (start + i) / 48000.0);
            int n = resampler.Process(block, outBuf);
            for (int i = 0; i < n; i++)
                output.Add(outBuf[i]);
        }

        int expected = 16000 * seconds;
        Assert.True(Math.Abs(output.Count - expected) < expected * 0.02,
            $"expected ~{expected} samples, got {output.Count}");

        // 1 kHz tone -> ~2000 zero crossings per second at any sample rate.
        int crossings = 0;
        for (int i = 1 + output.Count / 4; i < output.Count; i++)
            if ((output[i - 1] < 0 && output[i] >= 0) || (output[i - 1] >= 0 && output[i] < 0))
                crossings++;
        double crossingsPerSecond = crossings / (output.Count * 0.75 / 16000.0);
        Assert.True(Math.Abs(crossingsPerSecond - 2000) < 120,
            $"expected ~2000 crossings/s, got {crossingsPerSecond:0}");
    }

    [Fact]
    public void SameRate_IsPassthrough()
    {
        var resampler = new StreamResampler(48000, 48000);
        var input = new float[] { 0.1f, 0.2f, 0.3f };
        var output = new float[8];
        int n = resampler.Process(input, output);
        Assert.Equal(3, n);
        Assert.Equal(0.1f, output[0]);
    }
}

public class SpectralDenoiserTests
{
    [Fact]
    public void AttenuatesStationaryNoise()
    {
        var denoiser = new SpectralDenoiser { ReductionDb = 30, VoiceLikely = false };
        var rng = new Random(7);
        var block = new float[480];

        double inEnergy = 0, outEnergy = 0;
        int blocks = 400; // 4 seconds
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(rng.NextDouble() * 0.2 - 0.1);
            if (b >= blocks / 2)
                foreach (float s in block)
                    inEnergy += s * s;
            denoiser.Process(block);
            if (b >= blocks / 2)
                foreach (float s in block)
                    outEnergy += s * s;
        }

        double reductionDb = 10 * Math.Log10(inEnergy / Math.Max(outEnergy, 1e-12));
        Assert.True(reductionDb > 8, $"expected > 8 dB reduction on stationary noise, got {reductionDb:0.0} dB");
    }

    [Fact]
    public void KeepsSampleCount()
    {
        var denoiser = new SpectralDenoiser();
        var block = new float[333]; // deliberately not a divisor of the hop size
        for (int b = 0; b < 50; b++)
            denoiser.Process(block); // must never throw or change length semantics
        Assert.Equal(333, block.Length);
    }

    [Fact]
    public void Bypass_IsIdentity_AtFixedLatency()
    {
        // Bypassed, the STFT round-trip must reconstruct the input exactly
        // (COLA check) with the documented constant latency.
        var denoiser = new SpectralDenoiser { Bypass = true };
        int n = 48000;
        var input = new float[n];
        for (int i = 0; i < n; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * 333 * i / 48000.0) * 0.5f;

        var output = new float[n];
        var block = new float[480];
        for (int start = 0; start < n; start += block.Length)
        {
            Array.Copy(input, start, block, 0, block.Length);
            denoiser.Process(block);
            Array.Copy(block, 0, output, start, block.Length);
        }

        int lat = SpectralDenoiser.LatencySamples;
        for (int i = lat + SpectralDenoiser.FftSize; i < n; i++)
        {
            Assert.True(Math.Abs(output[i] - input[i - lat]) < 1e-3,
                $"bypass not transparent at sample {i}: {output[i]} vs {input[i - lat]}");
        }
    }
}

public class RnNoiseDenoiserTests
{
    [Fact]
    public void NativeLibrary_LoadsAndAttenuatesNoise()
    {
        using var denoiser = new RnNoiseDenoiser { ReductionDb = 40 };
        var rng = new Random(3);
        var block = new float[480];

        double inEnergy = 0, outEnergy = 0;
        int blocks = 300; // 3 seconds; measure the last second after adaptation
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < block.Length; i++)
                block[i] = (float)(rng.NextDouble() * 0.1 - 0.05);
            if (b >= 200)
                foreach (float s in block)
                    inEnergy += s * s;
            denoiser.Process(block);
            if (b >= 200)
                foreach (float s in block)
                    outEnergy += s * s;
        }

        // The point of this test is that the NATIVE library loads and audibly
        // processes; RNNoise is conservative on pure synthetic white noise.
        double reductionDb = 10 * Math.Log10(inEnergy / Math.Max(outEnergy, 1e-12));
        Assert.True(reductionDb > 1.5, $"expected RNNoise to attenuate white noise by > 1.5 dB, got {reductionDb:0.0} dB");
    }
}

public class SampleConverterTests
{
    [Fact]
    public void Pcm16Stereo_AveragesToMono()
    {
        var format = new WaveFormat(48000, 16, 2);
        // Frame 0: L=16384 (0.5), R=0 -> 0.25 ; Frame 1: L=-32768, R=-32768 -> -1.0
        var bytes = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)0).CopyTo(bytes, 2);
        BitConverter.GetBytes(short.MinValue).CopyTo(bytes, 4);
        BitConverter.GetBytes(short.MinValue).CopyTo(bytes, 6);

        var dest = new float[2];
        int frames = SampleConverter.ToMonoFloat(bytes, 8, format, dest);
        Assert.Equal(2, frames);
        Assert.Equal(0.25f, dest[0], 3);
        Assert.Equal(-1f, dest[1], 3);
    }

    [Fact]
    public void FloatMono_PassesThrough()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var bytes = new byte[8];
        BitConverter.GetBytes(0.5f).CopyTo(bytes, 0);
        BitConverter.GetBytes(-0.25f).CopyTo(bytes, 4);
        var dest = new float[2];
        int frames = SampleConverter.ToMonoFloat(bytes, 8, format, dest);
        Assert.Equal(2, frames);
        Assert.Equal(0.5f, dest[0], 5);
        Assert.Equal(-0.25f, dest[1], 5);
    }
}

public class CosineTests
{
    [Fact]
    public void IdenticalVectors_ScoreOne()
    {
        var v = new float[] { 0.3f, -0.5f, 0.8f };
        Assert.Equal(1f, SpeakerVerifier.Cosine(v, v), 4);
    }

    [Fact]
    public void OrthogonalVectors_ScoreZero()
    {
        Assert.Equal(0f, SpeakerVerifier.Cosine([1f, 0f], [0f, 1f]), 4);
    }

    [Fact]
    public void MismatchedLengths_ReturnNaN()
    {
        Assert.True(float.IsNaN(SpeakerVerifier.Cosine([1f], [1f, 2f])));
    }
}

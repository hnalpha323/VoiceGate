using NAudio.Dsp;

namespace VoiceGate.Dsp;

/// <summary>
/// Streaming mono resampler wrapping NAudio's WDL windowed-sinc resampler.
/// Feed arbitrary-length input blocks; output length varies per call but the
/// long-run ratio equals outRate/inRate.
/// </summary>
public sealed class StreamResampler
{
    private const int Channels = 1;
    private readonly WdlResampler _resampler = new();

    public int InputRate { get; }
    public int OutputRate { get; }

    public StreamResampler(int inputRate, int outputRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true); // input-driven: push whatever is available
        _resampler.SetRates(inputRate, outputRate);
    }

    /// <summary>
    /// Resamples <paramref name="input"/> into <paramref name="output"/>.
    /// Returns the number of output samples produced.
    /// </summary>
    public int Process(ReadOnlySpan<float> input, float[] output)
    {
        if (InputRate == OutputRate)
        {
            input.CopyTo(output);
            return input.Length;
        }

        int inFrames = input.Length;
        _resampler.ResamplePrepare(inFrames, Channels, out float[] inBuffer, out int inBufferOffset);
        input.CopyTo(inBuffer.AsSpan(inBufferOffset, inFrames));
        int maxOut = output.Length;
        return _resampler.ResampleOut(output, 0, inFrames, maxOut, Channels);
    }

    /// <summary>Worst-case output samples for a given input block size (for buffer sizing).</summary>
    public int MaxOutputFor(int inputSamples)
        => (int)Math.Ceiling(inputSamples * (double)OutputRate / InputRate) + 64;
}

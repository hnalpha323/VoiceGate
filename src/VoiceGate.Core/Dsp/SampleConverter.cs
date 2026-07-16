using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoiceGate.Dsp;

/// <summary>Converts raw WASAPI capture buffers of any common format to mono float samples.</summary>
public static class SampleConverter
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    public static bool IsFloatFormat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return true;
        if (format is WaveFormatExtensible ext)
            return ext.SubFormat == SubtypeIeeeFloat;
        return false;
    }

    public static bool IsPcmFormat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Pcm)
            return true;
        if (format is WaveFormatExtensible ext)
            return ext.SubFormat == SubtypePcm;
        return false;
    }

    /// <summary>
    /// Converts <paramref name="byteCount"/> bytes from <paramref name="buffer"/> into
    /// mono float frames in <paramref name="dest"/>. Returns the number of frames written.
    /// </summary>
    public static int ToMonoFloat(byte[] buffer, int byteCount, WaveFormat format, float[] dest)
    {
        int channels = format.Channels;

        if (IsFloatFormat(format) && format.BitsPerSample == 32)
        {
            var src = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, byteCount));
            int frames = src.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += src[f * channels + c];
                dest[f] = sum / channels;
            }
            return frames;
        }

        if (IsPcmFormat(format))
        {
            switch (format.BitsPerSample)
            {
                case 16:
                {
                    var src = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, byteCount));
                    int frames = src.Length / channels;
                    for (int f = 0; f < frames; f++)
                    {
                        int sum = 0;
                        for (int c = 0; c < channels; c++)
                            sum += src[f * channels + c];
                        dest[f] = sum / (float)channels / 32768f;
                    }
                    return frames;
                }
                case 24:
                {
                    int bytesPerFrame = 3 * channels;
                    int frames = byteCount / bytesPerFrame;
                    for (int f = 0; f < frames; f++)
                    {
                        float sum = 0f;
                        int baseIdx = f * bytesPerFrame;
                        for (int c = 0; c < channels; c++)
                        {
                            int i = baseIdx + c * 3;
                            int v = buffer[i] | (buffer[i + 1] << 8) | ((sbyte)buffer[i + 2] << 16);
                            sum += v / 8388608f;
                        }
                        dest[f] = sum / channels;
                    }
                    return frames;
                }
                case 32:
                {
                    var src = MemoryMarshal.Cast<byte, int>(buffer.AsSpan(0, byteCount));
                    int frames = src.Length / channels;
                    for (int f = 0; f < frames; f++)
                    {
                        double sum = 0;
                        for (int c = 0; c < channels; c++)
                            sum += src[f * channels + c];
                        dest[f] = (float)(sum / channels / 2147483648.0);
                    }
                    return frames;
                }
            }
        }

        throw new NotSupportedException($"Unsupported capture format: {format}");
    }
}

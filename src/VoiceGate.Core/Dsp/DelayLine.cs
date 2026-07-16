namespace VoiceGate.Dsp;

/// <summary>
/// Fixed sample delay used for gate lookahead: the audio path is delayed while
/// gate decisions are made on the undelayed signal, so the gate opens slightly
/// "before" speech onset reaches the output.
/// </summary>
public sealed class DelayLine
{
    private float[] _buf = [];
    private int _pos;

    public int DelaySamples { get; private set; }

    public DelayLine(int delaySamples) => SetDelay(delaySamples);

    public void SetDelay(int samples)
    {
        DelaySamples = Math.Max(0, samples);
        _buf = new float[Math.Max(1, DelaySamples)];
        _pos = 0;
    }

    public void Process(Span<float> data)
    {
        if (DelaySamples == 0)
            return;
        for (int i = 0; i < data.Length; i++)
        {
            float delayed = _buf[_pos];
            _buf[_pos] = data[i];
            data[i] = delayed;
            _pos++;
            if (_pos == _buf.Length)
                _pos = 0;
        }
    }

}

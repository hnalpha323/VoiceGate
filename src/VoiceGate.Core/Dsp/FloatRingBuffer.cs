namespace VoiceGate.Dsp;

/// <summary>
/// Thread-safe FIFO of float samples. When full, the oldest samples are overwritten
/// so a stalled consumer cannot deadlock the producer (audio keeps flowing).
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private int _readPos, _writePos, _count;

    public FloatRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new float[capacity];
    }

    public int Available
    {
        get
        {
            lock (_lock)
                return _count;
        }
    }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_lock)
        {
            foreach (float s in data)
            {
                _buffer[_writePos] = s;
                _writePos = (_writePos + 1) % _buffer.Length;
                if (_count == _buffer.Length)
                    _readPos = (_readPos + 1) % _buffer.Length; // drop oldest
                else
                    _count++;
            }
        }
    }

    /// <summary>Reads up to dest.Length samples; returns the number actually read.</summary>
    public int Read(Span<float> dest)
    {
        lock (_lock)
        {
            int n = Math.Min(dest.Length, _count);
            for (int i = 0; i < n; i++)
            {
                dest[i] = _buffer[_readPos];
                _readPos = (_readPos + 1) % _buffer.Length;
            }
            _count -= n;
            return n;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
        }
    }
}

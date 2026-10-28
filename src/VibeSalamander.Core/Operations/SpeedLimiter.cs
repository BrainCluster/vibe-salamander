// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace VibeSalamander.Core.Operations;

/// <summary>
/// A speed limit for transfers (a token bucket): copies tell it the bytes they moved and it makes them wait when they
/// go faster than allowed. The limit can change at any time, also while a copy waits; one limiter can be shared by
/// several transfers (the limit is then for all of them together). Thread-safe.
/// </summary>
public sealed class SpeedLimiter
{
    private readonly Func<long> _now;
    private readonly Action<TimeSpan, CancellationToken> _sleep;
    private readonly object _lock = new();
    private long _limit;
    private double _tokens;
    private long _last;

    /// <summary>The longest single wait, so a raised limit or a cancel is noticed soon.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(200);

    public SpeedLimiter() : this(Stopwatch.GetTimestamp, (t, ct) => ct.WaitHandle.WaitOne(t))
    {
    }

    /// <param name="now">A clock in <see cref="Stopwatch.Frequency"/> ticks (tests give their own).</param>
    /// <param name="sleep">Waits (tests just move their clock).</param>
    public SpeedLimiter(Func<long> now, Action<TimeSpan, CancellationToken> sleep)
    {
        _now = now;
        _sleep = sleep;
        _last = now();
    }

    /// <summary>Bytes per second; 0 = no limit.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _limit);
        set
        {
            lock (_lock)
            {
                Refill();
                _limit = Math.Max(0, value);
                _tokens = Math.Min(_tokens, Burst);
            }
        }
    }

    public bool IsLimited => BytesPerSecond > 0;

    /// <summary>Half a second's worth (at least 16 KB): a short burst is allowed, a long one isn't.</summary>
    private double Burst => Math.Max(16 * 1024, _limit / 2.0);

    private void Refill()
    {
        long now = _now();
        double seconds = (now - _last) / (double)Stopwatch.Frequency;
        _last = now;
        if (_limit > 0 && seconds > 0)
            _tokens = Math.Min(Burst, _tokens + seconds * _limit);
    }

    /// <summary>Counts bytes that were moved, and waits as long as the limit asks. Returns at once without a limit.</summary>
    public void Take(long bytes, CancellationToken ct = default)
    {
        if (bytes <= 0)
            return;
        lock (_lock)
        {
            if (_limit <= 0)
                return;
            Refill();
            _tokens -= bytes;
        }
        // the debt is paid by waiting; the limit may change meanwhile
        while (!ct.IsCancellationRequested)
        {
            double wait;
            lock (_lock)
            {
                if (_limit <= 0)
                {
                    _tokens = 0;
                    return;
                }
                Refill();
                if (_tokens >= 0)
                    return;
                wait = -_tokens / _limit;
            }
            TimeSpan time = TimeSpan.FromSeconds(wait);
            _sleep(time < Step ? time : Step, ct);
        }
    }
}

/// <summary>The recent speeds of a transfer, for its graph: one sample per interval, the oldest dropped.</summary>
public sealed class SpeedHistory
{
    private readonly double[] _samples;
    private int _start, _count;

    public SpeedHistory(int capacity = 60) => _samples = new double[Math.Max(2, capacity)];

    public int Capacity => _samples.Length;
    public int Count => _count;

    public void Add(double bytesPerSecond)
    {
        if (_count < _samples.Length)
            _samples[(_start + _count++) % _samples.Length] = Math.Max(0, bytesPerSecond);
        else
        {
            _samples[_start] = Math.Max(0, bytesPerSecond);
            _start = (_start + 1) % _samples.Length;
        }
    }

    /// <summary>The samples, oldest first.</summary>
    public IReadOnlyList<double> Samples
    {
        get
        {
            var list = new double[_count];
            for (int i = 0; i < _count; i++)
                list[i] = _samples[(_start + i) % _samples.Length];
            return list;
        }
    }

    public double Max => _count == 0 ? 0 : Samples.Max();

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }
}

// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Localization;
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

/// <summary>The speed limits offered in the menus, and limits typed by hand ("5", "500 KB", "1.5 MB/s").</summary>
public static class SpeedLimits
{
    private const long KB = 1024, MB = 1024 * 1024;

    /// <summary>The choices of the limit menus (0 = no limit).</summary>
    public static IReadOnlyList<long> Presets { get; } = new[] { 0, 256 * KB, 512 * KB, 1 * MB, 2 * MB, 5 * MB, 10 * MB, 20 * MB, 50 * MB, 100 * MB, 200 * MB };

    /// <summary>"5 MB/s", or "No limit".</summary>
    public static string Describe(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? L.T("No limit") : Formatting.Formats.ShortSize(bytesPerSecond) + "/s";

    /// <summary>
    /// A typed limit in bytes per second: a number with KB, MB or GB (and an optional "/s"); a number alone is in MB/s.
    /// "0", "none", "" and "no limit" mean no limit. Null when it can't be read.
    /// </summary>
    public static long? Parse(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("no limit", StringComparison.OrdinalIgnoreCase) ||
            t == "-" || t == L.T("No limit"))
            return 0;
        return ParseBytes(t.Replace("/s", string.Empty, StringComparison.OrdinalIgnoreCase), MB);
    }

    /// <summary>
    /// A size typed by hand: a number with B, KB, MB, GB or TB ("1.5 GB", "500k"); a number alone is in
    /// <paramref name="defaultUnit"/>. Null when it can't be read.
    /// </summary>
    public static long? ParseBytes(string text, long defaultUnit)
    {
        string t = text.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        long unit = defaultUnit;
        foreach (var (suffix, value) in new[]
                 {
                     ("KIB", KB), ("MIB", MB), ("GIB", 1024 * MB), ("TIB", 1024L * 1024 * MB),
                     ("KB", KB), ("MB", MB), ("GB", 1024 * MB), ("TB", 1024L * 1024 * MB),
                     ("K", KB), ("M", MB), ("G", 1024 * MB), ("T", 1024L * 1024 * MB), ("B", 1L),
                 })
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                unit = value;
                t = t[..^suffix.Length];
                break;
            }
        }
        t = t.Replace(',', '.');
        if (t.Length == 0 || !double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) ||
            number < 0 || double.IsInfinity(number))
            return null;
        return (long)Math.Min(number * unit, long.MaxValue / 2);
    }
}

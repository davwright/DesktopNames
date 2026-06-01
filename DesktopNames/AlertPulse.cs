namespace DesktopNames;

/// <summary>
/// Drives the 2.5s "fast pulse" on state change and the continuous slow breathe
/// for desktops in <see cref="StateKind.Asking"/>. Owns a single 16ms timer that
/// only runs while at least one desktop needs animating, so it costs nothing at rest.
///
/// Painting consults <see cref="GetFastPulseIntensity"/> and <see cref="GetBreatheIntensity"/>
/// inside <c>OnPaint</c>; this class just decides when to call back to invalidate.
/// </summary>
internal sealed class AlertPulse : IDisposable
{
    private const int FastPulseDurationMs = 2500;
    private const int BreathePeriodMs = 1500;

    private readonly SessionState _state;
    private readonly Settings _settings;
    private readonly Action<Guid> _invalidate;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<Guid, DateTime> _fastPulseStart = new();

    public AlertPulse(SessionState state, Settings settings, Action<Guid> invalidate)
    {
        _state = state;
        _settings = settings;
        _invalidate = invalidate;
        _state.Changed += OnStateChanged;

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;
    }

    /// <summary>0..1 sin-shaped intensity if a fast pulse is active for this desktop; else 0.</summary>
    public double GetFastPulseIntensity(Guid desktopId)
    {
        if (!_settings.AlertPulseEnabled) return 0;
        if (!_fastPulseStart.TryGetValue(desktopId, out var start)) return 0;
        double elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
        if (elapsed >= FastPulseDurationMs) return 0;
        return Math.Sin(elapsed / FastPulseDurationMs * Math.PI);
    }

    /// <summary>Continuous -1..1 breathe value; only non-zero for asking desktops.</summary>
    public double GetBreatheIntensity(StateKind state)
    {
        if (!_settings.AlertPulseEnabled) return 0;
        if (state != StateKind.Asking) return 0;
        double phaseMs = Environment.TickCount % BreathePeriodMs;
        return Math.Sin(phaseMs / BreathePeriodMs * 2 * Math.PI);
    }

    private void OnStateChanged(Guid desktopId)
    {
        if (!_settings.AlertPulseEnabled)
        {
            // Pulse disabled — still need to invalidate once so the new color paints.
            _invalidate(desktopId);
            return;
        }
        _fastPulseStart[desktopId] = DateTime.UtcNow;
        if (!_timer.Enabled) _timer.Start();
        _invalidate(desktopId);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        // Drop expired fast pulses (single invalidate per expiry).
        var expired = new List<Guid>();
        foreach (var (g, start) in _fastPulseStart)
        {
            if ((now - start).TotalMilliseconds >= FastPulseDurationMs) expired.Add(g);
        }
        foreach (var g in expired)
        {
            _fastPulseStart.Remove(g);
            _invalidate(g);
        }

        // Invalidate any desktop that still needs animation. The fast set + every asking
        // desktop. We poll SessionState rather than tracking asking subscriptions because
        // the asking set changes rarely and the poll is O(active desktops).
        bool anyActive = false;
        foreach (var g in _fastPulseStart.Keys)
        {
            _invalidate(g);
            anyActive = true;
        }
        foreach (var (g, s) in _state.ActiveDesktops())
        {
            if (s != StateKind.Asking) continue;
            if (_fastPulseStart.ContainsKey(g)) continue; // already invalidated above
            _invalidate(g);
            anyActive = true;
        }

        if (!anyActive) _timer.Stop();
    }

    public void EnsureRunningIfAskingPresent()
    {
        // Called after Sweep / Consume in case the active set changed without a Changed event.
        if (_timer.Enabled) return;
        foreach (var (_, s) in _state.ActiveDesktops())
        {
            if (s == StateKind.Asking) { _timer.Start(); return; }
        }
    }

    public void Dispose()
    {
        _state.Changed -= OnStateChanged;
        _timer.Stop();
        _timer.Dispose();
    }
}

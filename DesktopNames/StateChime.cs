using System.Media;

namespace DesktopNames;

/// <summary>
/// Plays a short audible cue when a desktop changes state, encoding the desktop's number so the
/// sound alone tells you where to look. Edge-triggered off <see cref="SessionState.Changed"/>:
/// rings only on the *entry* into a chiming state, so a desktop that stays put is silent.
///   Asking (yellow, "needs you")  → an ascending  rise.
///   Ready  (green,  "turn done")  → a descending fall.
/// Busy and Error are silent — they're transient and not where the user is asked to act.
///
/// Two styles, chosen by <see cref="Settings.AlertAskingChimeStyle"/>:
///   "notes" — N pentatonic notes for desktop N (countable by ear); direction marks the state.
///   "dtmf"  — a single phone-keypad tone whose pitch encodes the desktop number.
/// </summary>
internal sealed class StateChime
{
    private const int SampleRate = 8000;

    // C major pentatonic over two octaves — only consonant intervals, so any prefix of it
    // sounds pleasant. Desktop N rings the first N notes.
    private static readonly double[] Pentatonic =
    {
        523.25, 587.33, 659.25, 783.99, 880.00,    // C5 D5 E5 G5 A5
        1046.50, 1174.66, 1318.51, 1567.98, 1760.00 // C6 D6 E6 G6 A6
    };

    // Standard DTMF row (low) / column (high) frequencies keyed by keypad symbol.
    private static readonly Dictionary<char, (int low, int high)> DtmfTones = new()
    {
        ['1'] = (697, 1209), ['2'] = (697, 1336), ['3'] = (697, 1477), ['A'] = (697, 1633),
        ['4'] = (770, 1209), ['5'] = (770, 1336), ['6'] = (770, 1477), ['B'] = (770, 1633),
        ['7'] = (852, 1209), ['8'] = (852, 1336), ['9'] = (852, 1477), ['C'] = (852, 1633),
        ['*'] = (941, 1209), ['0'] = (941, 1336), ['#'] = (941, 1477), ['D'] = (941, 1633),
    };

    // Desktop number → keypad symbol. 1..9 dial as themselves, 10 → 0 (matching the
    // Win+Ctrl+digit desktop hotkeys), 11..16 extend into A-D/*/# for distinct pitches.
    private const string DtmfSymbols = "1234567890ABCD*#";

    private readonly SessionState _state;
    private readonly DesktopService _desktop;
    private readonly Settings _settings;
    private readonly Dictionary<Guid, StateKind> _prev = new();

    // Chimes are serialised through a single worker so two never overlap. The worker plays
    // one cue, waits a gap, then takes the next — so a burst of state changes is heard as a
    // clean sequence rather than a chord. Identical pending cues are collapsed, and as the
    // backlog grows the per-cue length and the gap shrink so it drains promptly.
    private readonly record struct ChimeItem(Guid Desktop, int Number, bool Ascending);
    private readonly object _gate = new();
    private readonly Queue<ChimeItem> _queue = new();
    private bool _playing;

    public StateChime(SessionState state, DesktopService desktop, Settings settings)
    {
        _state = state;
        _desktop = desktop;
        _settings = settings;
        _state.Changed += OnStateChanged;
    }

    private void OnStateChanged(Guid desktopId)
    {
        var (now, _, _) = _state.GetAggregate(desktopId);
        StateKind was = _prev.TryGetValue(desktopId, out var p) ? p : StateKind.None;
        _prev[desktopId] = now;

        if (!_settings.AlertAskingChimeEnabled || now == was) return;
        // Ring only on entry into Asking or Ready; Busy/Error/None are silent.
        bool ascending;
        if (now == StateKind.Asking) ascending = true;
        else if (now == StateKind.Ready) ascending = false;
        else return;

        var info = _desktop.GetDesktops().FirstOrDefault(d => d.Id == desktopId);
        if (info == null) return; // unresolved bucket has no desktop number — stay silent

        Enqueue(new ChimeItem(desktopId, info.Index + 1, ascending));
    }

    private void Enqueue(ChimeItem item)
    {
        lock (_gate)
        {
            if (_queue.Contains(item)) return; // collapse: this exact cue is already pending
            _queue.Enqueue(item);
            if (_playing) return;              // a worker is already draining the queue
            _playing = true;
        }
        ThreadPool.QueueUserWorkItem(_ => Drain());
    }

    private void Drain()
    {
        while (true)
        {
            ChimeItem item;
            int backlog;
            lock (_gate)
            {
                if (_queue.Count == 0) { _playing = false; return; }
                item = _queue.Dequeue();
                backlog = _queue.Count; // cues still waiting behind this one
            }

            // Shrink length + gap as the backlog grows: relaxed when alone, snappy under a
            // burst. scale 1.0 (empty) → 0.35 (8+ waiting), linear between.
            double scale = backlog <= 0 ? 1.0 : Math.Max(0.35, 1.0 - backlog / 8.0 * 0.65);
            byte[] wav = string.Equals(_settings.AlertAskingChimeStyle, "dtmf", StringComparison.OrdinalIgnoreCase)
                ? BuildDtmfWav(item.Number, (int)(200 * scale))
                : BuildNotesWav(item.Number, item.Ascending, (int)(110 * scale));

            // PlaySync blocks for the cue's length, then we hold the (scaled) gap before the
            // next. A dropped chime (e.g. no audio device) must never disturb the overlay.
            try
            {
                using var ms = new MemoryStream(wav);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch { }
            Thread.Sleep((int)(1000 * scale));
        }
    }

    /// <summary>N pentatonic notes for desktop N, ascending (asking) or descending (ready).</summary>
    private static byte[] BuildNotesWav(int number, bool ascending, int noteMs)
    {
        noteMs = Math.Max(35, noteMs);
        int notes = Math.Max(1, number);
        int perNote = SampleRate * noteMs / 1000;
        var pcm = new short[notes * perNote];

        for (int n = 0; n < notes; n++)
        {
            int step = ascending ? n : notes - 1 - n; // reverse the climb for the "done" fall
            // Wrap past the table by stepping up an octave so high desktop numbers keep rising.
            double freq = Pentatonic[step % Pentatonic.Length] * (1 << (step / Pentatonic.Length));
            for (int i = 0; i < perNote; i++)
            {
                double t = (double)i / SampleRate;
                double s = Math.Sin(2 * Math.PI * freq * t) * Envelope(i, perNote);
                pcm[n * perNote + i] = (short)(s * 0.4 * short.MaxValue);
            }
        }
        return Encode(pcm);
    }

    /// <summary>A DTMF tone whose keypad symbol encodes the desktop number.</summary>
    private static byte[] BuildDtmfWav(int number, int durationMs)
    {
        char sym = DtmfSymbols[(number - 1) % DtmfSymbols.Length];
        var (low, high) = DtmfTones[sym];

        durationMs = Math.Max(70, durationMs);
        int samples = SampleRate * durationMs / 1000;
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SampleRate;
            double s = (Math.Sin(2 * Math.PI * low * t) + Math.Sin(2 * Math.PI * high * t)) / 2;
            pcm[i] = (short)(s * Envelope(i, samples) * 0.4 * short.MaxValue);
        }
        return Encode(pcm);
    }

    /// <summary>~20ms raised-cosine ramp on each end to kill the start/stop click.</summary>
    private static double Envelope(int i, int total)
    {
        const int fade = 160;
        if (i < fade) return 0.5 - 0.5 * Math.Cos(Math.PI * i / fade);
        if (i >= total - fade) return 0.5 - 0.5 * Math.Cos(Math.PI * (total - 1 - i) / fade);
        return 1.0;
    }

    /// <summary>Wrap a 16-bit mono PCM buffer in a RIFF/WAVE container.</summary>
    private static byte[] Encode(short[] pcm)
    {
        int dataBytes = pcm.Length * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                  // fmt chunk size
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);      // byte rate
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits per sample
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
        return ms.ToArray();
    }
}

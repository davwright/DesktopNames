namespace DesktopNames;

/// <summary>
/// Append-only debug log for the alert pipeline. Mirrors ClaudeHook's log format so
/// you can grep both files together. Writes to %APPDATA%\DesktopNames\desktopnames.log.
/// Rotates to .1 once the current file is 4h old, so 4-8h of history is always on disk. Gated by <see cref="Settings.AlertLogEnabled"/>.
///
/// Categories (loose convention so greps work):
///   <c>pipe</c>     — connection accepted / parsed / replied
///   <c>resolver</c> — which resolver path won (vscodePid / walk-up / learned / sticky / unresolved)
///   <c>state</c>    — SessionState mutations (apply / sweep reap / consume)
///   <c>tracker</c>  — VsCodeTracker layout restores (per-window outcome)
///   <c>startup</c>  — which build wrote the lines below it (see Program.GetBuildStamp)
/// </summary>
internal static class Log
{
    private static readonly object _lock = new();
    private static string? _path;
    private static bool _enabled;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(4);

    /// <summary>Configure once at startup. Subsequent calls update the enabled flag.</summary>
    public static void Configure(bool enabled)
    {
        _enabled = enabled;
        if (_path == null)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopNames");
            try { Directory.CreateDirectory(dir); } catch { }
            _path = Path.Combine(dir, "desktopnames.log");
        }
    }

    public static void Pipe(string msg)     => Write("pipe",     msg);
    public static void Tracker(string msg)  => Write("tracker",  msg);
    public static void Resolver(string msg) => Write("resolver", msg);
    public static void State(string msg)    => Write("state",    msg);
    public static void Screens(string msg)  => Write("screens",  msg);
    public static void Startup(string msg)  => Write("startup",  msg);
    public static void Projects(string msg) => Write("projects", msg);

    private static void Write(string category, string msg)
    {
        if (!_enabled || _path == null) return;
        try
        {
            lock (_lock)
            {
                bool rotated = MaybeRotate();
                var line = $"{DateTime.Now:HH:mm:ss.fff}  {category,-8}  {msg}{Environment.NewLine}";
                File.AppendAllText(_path, line);
                // Windows tunneling hands a re-created name the old file's creation time for ~15s.
                if (rotated) File.SetCreationTimeUtc(_path, DateTime.UtcNow);
            }
        }
        catch { /* logging must never throw */ }
    }

    private static bool MaybeRotate()
    {
        try
        {
            var fi = new FileInfo(_path!);
            if (!fi.Exists || DateTime.UtcNow - fi.CreationTimeUtc < MaxAge) return false;
            File.Move(_path!, _path + ".1", overwrite: true);
            return true;
        }
        catch { return false; }
    }
}

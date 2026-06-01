namespace DesktopNames;

/// <summary>
/// Append-only debug log for the alert pipeline. Mirrors ClaudeHook's log format so
/// you can grep both files together. Writes to %APPDATA%\DesktopNames\desktopnames.log.
/// Rotates at 512KB by renaming current to .1. Gated by <see cref="Settings.AlertLogEnabled"/>.
///
/// Categories (loose convention so greps work):
///   <c>pipe</c>     — connection accepted / parsed / replied
///   <c>resolver</c> — which resolver path won (vscodePid / walk-up / learned / sticky / unresolved)
///   <c>state</c>    — SessionState mutations (apply / sweep reap / consume)
/// </summary>
internal static class Log
{
    private static readonly object _lock = new();
    private static string? _path;
    private static bool _enabled;
    private const long MaxBytes = 512 * 1024;

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
    public static void Resolver(string msg) => Write("resolver", msg);
    public static void State(string msg)    => Write("state",    msg);

    private static void Write(string category, string msg)
    {
        if (!_enabled || _path == null) return;
        try
        {
            lock (_lock)
            {
                MaybeRotate();
                var line = $"{DateTime.Now:HH:mm:ss.fff}  {category,-8}  {msg}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    private static void MaybeRotate()
    {
        try
        {
            var fi = new FileInfo(_path!);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var old = _path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(_path!, old);
        }
        catch { }
    }
}

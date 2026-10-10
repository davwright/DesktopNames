using System.Text.Json;

namespace DesktopNames;

/// <summary>
/// On-demand index of currently-open VSCode workspaces and their folder paths, scraped
/// from <c>%APPDATA%\Code\User\workspaceStorage</c>. Each subdir there has a
/// <c>workspace.json</c> describing the workspace:
/// <list type="bullet">
///   <item>folder mode → <c>{ "folder": "file:///&lt;path&gt;" }</c></item>
///   <item>workspace mode → <c>{ "configuration": "file:///&lt;path&gt;.code-workspace" }</c></item>
///   <item>anon → no folder/configuration key</item>
/// </list>
/// We map every observed folder path to the rootName VSCode shows in its titles, so the
/// resolver can hand off to VsCodeTracker's existing rootName→desktop map. This closes
/// the .code-workspace gap (rootName ≠ folder name) without VsCodeTracker needing to
/// learn about workspace files.
///
/// Scans are demand-driven and rate-limited to once per 30s — no background polling.
/// </summary>
internal sealed class WorkspaceFolderIndex
{
    private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(30);

    // A single folder path can map to multiple rootNames — e.g. C:\git\tools\DesktopNames
    // might appear both as a standalone FOLDER (rootName "DesktopNames", from when the user
    // opened it directly) and as a folder member of a multi-folder .code-workspace called
    // "dav". The resolver will iterate candidates and pick the first one VsCodeTracker
    // has currently bound to a desktop.
    private readonly Dictionary<string, HashSet<string>> _pathToRootNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastScanUtc;

    /// <summary>
    /// Walk <paramref name="cwd"/> upward looking for a known open VSCode workspace whose
    /// folder is an ancestor. Returns the rootName VSCode uses in its window title for
    /// that workspace, or null. Triggers a rescan if the cache misses and the cooldown
    /// has elapsed.
    /// </summary>
    public IReadOnlyCollection<string> FindRootNameCandidates(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return Array.Empty<string>();
        string norm = NormalizePath(cwd);

        // 1. Walk up: cwd is at-or-under an open workspace folder. The common case.
        var hits = TryWalkUp(norm);
        if (hits.Count > 0) return hits;

        // 2. Rescan if the cache might be stale, then retry walk-up.
        if (DateTime.UtcNow - _lastScanUtc > ScanCooldown)
        {
            Rescan();
            _lastScanUtc = DateTime.UtcNow;
            hits = TryWalkUp(norm);
            if (hits.Count > 0) return hits;
        }

        // 3. Walk down: cwd is a parent of an open workspace folder (e.g. claude was
        // launched from C:\git\tools\EvolxCli but the open workspace is the subfolder
        // C:\git\tools\EvolxCli\src\Evolx.Cli).
        return TryWalkDown(norm);
    }

    /// <summary>Collect every rootName known for any workspace whose folder is under cwd.</summary>
    private IReadOnlyCollection<string> TryWalkDown(string cwd)
    {
        string prefix = cwd + "\\";
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (folderPath, rootNames) in _pathToRootNames)
        {
            if (!folderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var n in rootNames) set.Add(n);
        }
        return set;
    }

    private IReadOnlyCollection<string> TryWalkUp(string norm)
    {
        while (norm.Length > 0)
        {
            if (_pathToRootNames.TryGetValue(norm, out var hits)) return hits;
            int slash = norm.LastIndexOf('\\');
            if (slash <= 2) break;
            norm = norm[..slash];
        }
        return Array.Empty<string>();
    }

    private void AddPathRootName(string folderPath, string rootName)
    {
        string norm = NormalizePath(folderPath);
        if (!_pathToRootNames.TryGetValue(norm, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _pathToRootNames[norm] = set;
        }
        set.Add(rootName);
    }

    private void Rescan()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Code", "User", "workspaceStorage");
            if (!Directory.Exists(root)) return;

            // workspaceStorage accumulates every workspace ever opened (120+ on this user's
            // machine). Limit to dirs touched in the last day; that's plenty to capture
            // "currently open or recently used".
            var cutoff = DateTime.UtcNow.AddDays(-1);
            int folderHits = 0, workspaceHits = 0, skipped = 0, errors = 0;
            foreach (var dir in new DirectoryInfo(root).GetDirectories())
            {
                if (dir.LastWriteTimeUtc < cutoff) continue;
                var wf = Path.Combine(dir.FullName, "workspace.json");
                if (!File.Exists(wf)) { skipped++; continue; }
                try
                {
                    var (folder, ws) = ParseWorkspaceJsonDetailed(wf);
                    folderHits += folder;
                    workspaceHits += ws;
                }
                catch (Exception ex)
                {
                    errors++;
                    Log.Resolver($"workspace-index parse error on {dir.Name}: {ex.Message}");
                }
            }
            Log.Resolver($"workspace-index rescan: paths={_pathToRootNames.Count} folder+={folderHits} workspace+={workspaceHits} skipped={skipped} errors={errors}");
        }
        catch (Exception ex)
        {
            Log.Resolver($"workspace-index rescan failed: {ex.Message}");
        }
    }

    /// <summary>Parse a workspace.json. Returns (folder-mode-added, workspace-mode-added).</summary>
    private (int folder, int workspace) ParseWorkspaceJsonDetailed(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("folder", out var f))
        {
            string fsPath = UriToPath(f.GetString());
            if (fsPath.Length == 0) return (0, 0);
            string rootName = Path.GetFileName(fsPath.TrimEnd('\\'));
            if (rootName.Length == 0) return (0, 0);
            AddPathRootName(fsPath, rootName);
            return (1, 0);
        }
        // VSCode writes the indirection key as "workspace" (not "configuration").
        // The pointed file is either a real .code-workspace on disk or an untitled
        // workspace under %APPDATA%\Code\Workspaces\<id>\workspace.json.
        if (root.TryGetProperty("workspace", out var w))
        {
            string wsFile = UriToPath(w.GetString());
            if (wsFile.Length == 0)
            {
                Log.Resolver($"workspace-index: workspace key but path decode failed in {path}");
                return (0, 0);
            }
            if (!File.Exists(wsFile))
            {
                Log.Resolver($"workspace-index: workspace ref missing on disk: {wsFile}");
                return (0, 0);
            }
            return (0, ParseCodeWorkspaceFile(wsFile));
        }
        return (0, 0);
    }

    /// <summary>
    /// .code-workspace JSON has shape:
    /// <c>{ "name": "ev.exe", "folders": [ { "path": "c:/git/tools/EvolxCli" }, ... ] }</c>.
    /// Map every folder path to the name. If no name, default to the file's basename
    /// (matches VSCode's title fallback).
    /// </summary>
    private int ParseCodeWorkspaceFile(string wsFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(wsFile));
            var root = doc.RootElement;
            // VSCode's title rule for multi-folder workspaces:
            //   - explicit "name" → use as-is (e.g. ".code-workspace name=ev.exe" → "ev.exe")
            //   - no "name", real .code-workspace file → "<basename> (Workspace)"
            //   - no "name", untitled workspace (under %APPDATA%\Code\Workspaces) → "Untitled (Workspace)"
            // VsCodeTracker stores the rootName it sees in titles, so we mirror that here.
            string rootName = "";
            if (root.TryGetProperty("name", out var n)) rootName = n.GetString() ?? "";
            if (rootName.Length == 0)
            {
                if (wsFile.IndexOf(@"\Code\Workspaces\", StringComparison.OrdinalIgnoreCase) >= 0)
                    rootName = "Untitled (Workspace)";
                else
                    rootName = Path.GetFileNameWithoutExtension(wsFile) + " (Workspace)";
            }

            int added = 0;
            if (root.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
            {
                string baseDir = Path.GetDirectoryName(wsFile) ?? "";
                foreach (var folder in folders.EnumerateArray())
                {
                    if (!folder.TryGetProperty("path", out var p)) continue;
                    string folderPath = p.GetString() ?? "";
                    if (folderPath.Length == 0) continue;
                    if (!Path.IsPathRooted(folderPath))
                        folderPath = Path.GetFullPath(Path.Combine(baseDir, folderPath));
                    AddPathRootName(folderPath, rootName);
                    added++;
                }
            }
            return added;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Every folder VS Code has ever opened in folder mode that still exists on disk, with the
    /// last time its workspaceStorage entry was touched (≈ last opened). Backs the project
    /// switcher's list, so a project stays listed after its desktop is recycled.
    /// </summary>
    public static List<(string folder, DateTime lastUsedUtc)> AllOpenedFolders()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Code", "User", "workspaceStorage");
        var result = new List<(string, DateTime)>();
        foreach (var dir in new DirectoryInfo(root).GetDirectories())
        {
            var wf = Path.Combine(dir.FullName, "workspace.json");
            if (!File.Exists(wf)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(wf));
            if (!doc.RootElement.TryGetProperty("folder", out var f)) continue;
            string path = NormalizePath(UriToPath(f.GetString()));
            if (path.Length > 0 && Directory.Exists(path)) result.Add((path, dir.LastWriteTimeUtc));
        }
        return result;
    }

    /// <summary>
    /// Decode a VSCode file URI to a Windows path. <c>file:///c%3A/path</c> → <c>c:\path</c>.
    /// Using <c>new Uri().LocalPath</c> here is wrong: for percent-encoded drive letters it
    /// returns <c>/c:/path</c> which becomes <c>\c:\path</c> after slash replacement —
    /// a non-existent file. Manual decode avoids that.
    /// </summary>
    private static string UriToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        try
        {
            const string prefix = "file:///";
            string p = uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? uri[prefix.Length..] : uri;
            p = Uri.UnescapeDataString(p);
            return p.Replace('/', '\\');
        }
        catch { return ""; }
    }

    private static string NormalizePath(string p) =>
        p.Replace('/', '\\').TrimEnd('\\', ' ');
}

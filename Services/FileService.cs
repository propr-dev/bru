using System.IO;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Gives Clicky safe, scoped access to the filesystem for the list/find and copy tools.
/// Every path the model supplies is resolved and verified to stay within one of the
/// user's AllowedFolders — anything that escapes (system files, other drives, ".." traversal)
/// is rejected. Copies are STAGED and only run after explicit user confirmation.
/// There is intentionally no move and no delete.
/// </summary>
public class FileService
{
    private readonly List<string> _roots;
    private (string Source, string Destination)? _pendingCopy;

    public bool Enabled { get; }
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Fires with a human-readable description when a copy is staged and awaiting approval.</summary>
    public event Action<string>? CopyStaged;
    /// <summary>Fires with the result message once a staged copy is approved, denied, or fails.</summary>
    public event Action<string>? CopyResolved;
    public bool HasPendingCopy => _pendingCopy != null;

    public FileService(IEnumerable<string> allowedFolders, bool enabled)
    {
        Enabled = enabled;
        _roots = (allowedFolders ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();
    }

    /// <summary>
    /// Resolves a model-supplied path to an absolute path GUARANTEED to live inside an
    /// allowed root. GetFullPath canonicalizes "..", so traversal can't escape the check.
    /// </summary>
    private string ResolveInsideRoot(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Empty path.");

        string candidate = Path.IsPathRooted(input)
            ? input
            : Path.Combine(_roots.Count > 0 ? _roots[0] : "", input);
        string full = Path.GetFullPath(candidate);

        foreach (var root in _roots)
        {
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full;
        }
        throw new UnauthorizedAccessException(
            $"'{input}' is outside the folders Clicky is allowed to access.");
    }

    public string ListFiles(string folder, string? pattern)
    {
        var dir = ResolveInsideRoot(string.IsNullOrWhiteSpace(folder) ? "." : folder);
        if (!Directory.Exists(dir)) return $"Folder not found: {dir}";

        pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var entries = new List<string>();
        foreach (var d in Directory.GetDirectories(dir))
            entries.Add("[folder] " + Path.GetFileName(d));
        foreach (var f in Directory.GetFiles(dir, pattern))
            entries.Add($"         {Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024.0:F1} KB)");

        if (entries.Count == 0) return $"{dir} is empty (or nothing matched '{pattern}').";

        const int cap = 60;
        var listing = string.Join("\n", entries.Take(cap));
        if (entries.Count > cap) listing += $"\n…and {entries.Count - cap} more.";
        Logger.Log($"[Files] Listed {entries.Count} entries in {dir}");
        return $"Contents of {dir}:\n{listing}";
    }

    /// <summary>Validates a copy and stages it. Does NOT copy yet.</summary>
    public string StageCopy(string source, string destination)
    {
        var src = ResolveInsideRoot(source);
        if (!File.Exists(src)) return $"Cannot copy — file not found: {src}";
        var dst = ResolveInsideRoot(destination); // folder or full target path, must be inside a root

        _pendingCopy = (src, dst);
        Logger.Log($"[Files] Staged copy (awaiting confirmation): {src} -> {dst}");
        CopyStaged?.Invoke($"Copy “{Path.GetFileName(src)}”  →  {dst}");
        return $"Ready to copy '{Path.GetFileName(src)}' to '{dst}'. " +
               "This is NOT done yet — a permission card is now showing in the Bru window, and you should " +
               "also tell the user what will be copied and ask them to approve it (say 'yes' or click Allow).";
    }

    /// <summary>Executes or cancels the staged copy based on the user's confirmation.</summary>
    public string ConfirmPending(bool confirmed)
    {
        if (_pendingCopy == null) return "There is no file copy awaiting confirmation.";

        var (src, dst) = _pendingCopy.Value;
        _pendingCopy = null;

        string result;
        if (!confirmed)
        {
            Logger.Log($"[Files] Copy cancelled by user: {src} -> {dst}");
            result = "Copy cancelled. Nothing was changed.";
        }
        else
        {
            try
            {
                string target = Directory.Exists(dst)
                    ? Path.Combine(dst, Path.GetFileName(src))
                    : dst;

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (File.Exists(target))
                {
                    result = $"A file named '{Path.GetFileName(target)}' already exists there. " +
                             "Copy not performed, to avoid overwriting.";
                }
                else
                {
                    File.Copy(src, target);
                    Logger.Info($"Copied {Path.GetFileName(src)}");
                    Logger.Log($"[Files] Copied {src} -> {target}");
                    result = $"Done — copied '{Path.GetFileName(src)}' to '{target}'.";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Files] Copy failed: {ex.Message}");
                result = $"Copy failed: {ex.Message}";
            }
        }

        CopyResolved?.Invoke(result);
        return result;
    }
}

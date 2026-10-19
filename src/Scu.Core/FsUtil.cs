namespace Scu.Core;

/// <summary>File system helpers that survive what an earlier builder left behind (junctions into folders that no longer exist, read-only files).</summary>
public static class FsUtil
{
    static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Delete a folder tree. A junction or symbolic link is removed itself, never followed (Directory.Delete(path, true) can fail on a junction
    /// whose target is gone). Read-only files are made writable first.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        if (IsReparsePoint(path))
        {
            if (Directory.Exists(path)) Directory.Delete(path, false); else File.Delete(path);
            return;
        }
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) DeleteTree(entry);
        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, false);
    }

    /// <summary>DeleteTree with a few retries (virus scanners and indexers hold new files for a moment).</summary>
    public static void DeleteTreeWithRetry(string path, int attempts = 5)
    {
        for (int i = 1; ; i++)
        {
            try { DeleteTree(path); return; }
            catch (Exception e) when (i < attempts && (e is IOException || e is UnauthorizedAccessException)) { Thread.Sleep(500 * i); }
        }
    }
}

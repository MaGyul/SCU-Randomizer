using System.Collections.Concurrent;

namespace Scu.Core;

/// <summary>
/// The merge stages need a fresh, private copy of the same donor package over and over (each callback edits its copy).
/// Reading a package takes a second or two, so one copy per path is read ahead on a background thread while the caller
/// works on the previous one. <see cref="Clear"/> drops the spare copies at the end of a stage.
/// </summary>
public static class AssetCache
{
    static readonly ConcurrentDictionary<string, Task<Asset>> Spare = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A fresh copy of the package; the next copy starts loading immediately.</summary>
    public static Asset Take(string path)
    {
        var key = Path.GetFullPath(path);
        Asset result = Spare.TryRemove(key, out var ready) ? ready.GetAwaiter().GetResult() : Asset.Load(key);
        Spare[key] = Task.Run(() => Asset.Load(key));
        return result;
    }

    public static void Clear() => Spare.Clear();
}

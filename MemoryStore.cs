using System.IO;
using System.Text;
using StardewModdingAPI;

namespace WheatStook;

/// <summary>
/// Long-term memory channel. A plain text file in the mod folder that persists
/// facts the AI should remember between sessions. Loaded at launch and flushed on
/// change. This is the "remember" half of the channel split: the map-state channel
/// (world read) and the dialogue channel (live chat) are the other two.
///
/// The file lives next to the DLL so it's easy to find and edit by hand. The entry
/// list is guarded by a lock because the AI-forward path reads it on a background
/// thread while the console/main thread may add or remove simultaneously.
/// </summary>
public class MemoryStore
{
    private readonly string _path;
    private readonly IMonitor _monitor;
    private readonly object _lock = new();
    private readonly List<string> _entries = new();

    public MemoryStore(string path, IMonitor monitor)
    {
        _path = path;
        _monitor = monitor;
        Load();
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public void Load()
    {
        lock (_lock) _entries.Clear();
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadAllLines(_path))
        {
            var t = line.Trim();
            if (t.Length > 0)
                lock (_lock) _entries.Add(t);
        }
        _monitor.Log($"MemoryStore loaded: {Count} memories.", LogLevel.Info);
    }

    public void Add(string memory)
    {
        var t = memory.Trim();
        if (string.IsNullOrWhiteSpace(t)) return;
        lock (_lock)
        {
            if (_entries.Contains(t)) return;
            _entries.Add(t);
        }
        Save();
    }

    public void Remove(string memory)
    {
        bool removed;
        lock (_lock) removed = _entries.Remove(memory.Trim());
        if (removed) Save();
    }

    /// <summary>Append a timestamped journal line (shared-experience events).</summary>
    public void Journal(string text)
    {
        var t = text?.Trim();
        if (string.IsNullOrWhiteSpace(t)) return;
        Add($"[{DateTime.Now:yyyy-MM-dd HH:mm}] {t}");
    }

    /// <summary>Snapshot of the current entries (for the HTTP endpoint).</summary>
    public List<string> Snapshot()
    {
        lock (_lock) return new List<string>(_entries);
    }

    /// <summary>Drop every entry (POST /memory op=clear).</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Save();
    }

    /// <summary>Formatted memory context, one memory per line (snapshot).</summary>
    public string Context()
    {
        lock (_lock) return string.Join("\n", _entries);
    }

    private void Save()
    {
        List<string> snapshot;
        lock (_lock) snapshot = new List<string>(_entries);
        try
        {
            File.WriteAllLines(_path, snapshot, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _monitor.Log($"MemoryStore save failed: {ex.Message}", LogLevel.Error);
        }
    }
}

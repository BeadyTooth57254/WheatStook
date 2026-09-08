using System.IO;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;

namespace WheatStook;

/// <summary>One gift reaction: an item name -> emote id + a line to say.</summary>
public class ReactionRule
{
    public string item { get; set; } = "";
    public int emote { get; set; } = 4;
    public string text { get; set; } = "";
}

/// <summary>
/// Custom gift reactions (the "你送它东西它会真心反应" wish). Rules live in
/// wheatstook_reactions.json next to the DLL, so they can be hand-edited or written
/// over the /react endpoint. When the farmhand receives an item that matches a rule,
/// it performs the configured emote and says the configured line.
/// </summary>
public class ReactionStore
{
    private readonly string _path;
    private readonly IMonitor _monitor;
    private readonly object _lock = new();
    private readonly List<ReactionRule> _rules = new();

    public ReactionStore(string path, IMonitor monitor)
    {
        _path = path;
        _monitor = monitor;
        Load();
    }

    public int Count
    {
        get { lock (_lock) return _rules.Count; }
    }

    public void Load()
    {
        lock (_lock) _rules.Clear();
        if (!File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<ReactionRule>>(File.ReadAllText(_path)) ?? new();
            lock (_lock)
                foreach (var r in list)
                    if (!string.IsNullOrWhiteSpace(r.item)) _rules.Add(r);
            _monitor.Log($"ReactionStore loaded: {Count} rule(s).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"ReactionStore load failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public void Set(string item, int emote, string text)
    {
        var key = (item ?? "").Trim();
        if (key.Length == 0) return;
        lock (_lock)
        {
            var existing = _rules.FirstOrDefault(r => r.item.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.emote = emote; existing.text = text ?? ""; }
            else _rules.Add(new ReactionRule { item = key, emote = emote, text = text ?? "" });
        }
        Save();
    }

    public bool Remove(string item)
    {
        bool removed;
        lock (_lock) removed = _rules.RemoveAll(r => r.item.Equals((item ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    public List<ReactionRule> All()
    {
        lock (_lock) return _rules.Select(Clone).ToList();
    }

    /// <summary>First rule matching the item name either way (case-insensitive).</summary>
    public ReactionRule? Match(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        lock (_lock)
            return _rules
                .Where(r => itemName.Contains(r.item, StringComparison.OrdinalIgnoreCase)
                         || r.item.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
    }

    private static ReactionRule Clone(ReactionRule r) => new() { item = r.item, emote = r.emote, text = r.text };

    private void Save()
    {
        List<ReactionRule> snapshot;
        lock (_lock) snapshot = _rules.Select(Clone).ToList();
        try
        {
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _monitor.Log($"ReactionStore save failed: {ex.Message}", LogLevel.Error);
        }
    }
}

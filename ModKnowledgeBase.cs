using StardewModdingAPI;

namespace WheatStook;

/// <summary>
/// Indexes every installed mod (and content pack) at game launch so the AI can
/// look up what's in the farm's world ("what does this mod do / who made it / is
/// there a link"). Built once via SMAPI's ModRegistry in GameLaunched, so it
/// never has to scrape the console or guess.
///
/// v1: name/author/version/description/UpdateKeys + content-pack flag. Tiered
/// source/Nexus reading (modWhitelist/system, cache) are later refinements.
/// </summary>
public class ModKnowledgeBase
{
    private readonly IMonitor _monitor;
    private readonly List<ModInfo> _mods = new();

    public ModKnowledgeBase(IMonitor monitor) => _monitor = monitor;

    public int Count => _mods.Count;

    public IEnumerable<string> UniqueIds => _mods.Select(m => m.UniqueID);

    public void BuildFrom(IModRegistry registry)
    {
        _mods.Clear();
        foreach (var mod in registry.GetAll())
        {
            var m = mod.Manifest;
            if (m is null) continue;
            var (nexusId, nexusUrl, links) = DeriveLinks(m.UpdateKeys ?? Array.Empty<string>());
            _mods.Add(new ModInfo
            {
                Name = m.Name ?? string.Empty,
                UniqueID = m.UniqueID ?? string.Empty,
                Author = m.Author ?? string.Empty,
                Version = m.Version?.ToString() ?? string.Empty,
                Description = m.Description ?? string.Empty,
                UpdateKeys = m.UpdateKeys ?? Array.Empty<string>(),
                IsContentPack = m.ContentPackFor != null,
                NexusId = nexusId,
                NexusUrl = nexusUrl,
                Links = links,
            });
        }
        _monitor.Log($"Mod knowledge base built: {_mods.Count} mods indexed (enableModKnowledge={true}).", LogLevel.Info);
    }

    /// <summary>Return the mods whose name/unique id/author/description contain <paramref name="query"/>.</summary>
    public List<ModInfo> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _mods;
        var q = query.Trim();
        return _mods.Where(m =>
            m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.UniqueID.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public sealed class ModInfo
    {
        public string Name { get; set; } = string.Empty;
        public string UniqueID { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string[] UpdateKeys { get; set; } = Array.Empty<string>();
        public bool IsContentPack { get; set; }

        /// <summary>Nexus mod id from UpdateKeys (empty when the mod has none).</summary>
        public string NexusId { get; set; } = string.Empty;

        /// <summary>Ready-to-open Nexus page for this mod (empty when unknown).</summary>
        public string NexusUrl { get; set; } = string.Empty;

        /// <summary>All links derived from UpdateKeys (Nexus/GitHub/CurseForge/ModDrop).</summary>
        public List<string> Links { get; set; } = new();
    }

    /// <summary>
    /// Turn a manifest's UpdateKeys into real URLs. UpdateKeys looks like
    /// "Nexus:12345", "GitHub:owner/repo", "CurseForge:name", "ModDrop:name";
    /// this is what lets the AI hand back a precise link instead of guessing.
    /// </summary>
    private static (string nexusId, string nexusUrl, List<string> links) DeriveLinks(string[] updateKeys)
    {
        string nexusId = string.Empty, nexusUrl = string.Empty;
        var links = new List<string>();
        foreach (var raw in updateKeys)
        {
            var key = (raw ?? string.Empty).Trim();
            var sep = key.IndexOf(':');
            if (sep <= 0) continue;
            var kind = key.Substring(0, sep).Trim().ToLowerInvariant();
            var value = key.Substring(sep + 1).Trim();
            if (value.Length == 0) continue;
            switch (kind)
            {
                case "nexus":
                    nexusId = value;
                    nexusUrl = $"https://www.nexusmods.com/stardewvalley/mods/{value}";
                    links.Add(nexusUrl);
                    break;
                case "github":
                    links.Add($"https://github.com/{value}");
                    break;
                case "curseforge":
                    links.Add($"https://www.curseforge.com/stardewvalley/mods/{value}");
                    break;
                case "moddrop":
                    links.Add($"https://www.moddrop.com/stardew-valley/mods/{value}");
                    break;
            }
        }
        return (nexusId, nexusUrl, links);
    }
}

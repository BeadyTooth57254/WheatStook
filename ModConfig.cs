namespace WheatStook;

/// <summary>
/// All runtime settings. config.json is always the backing store; GMCM (when
/// installed) edits the same fields live. This class matches config.example.json,
/// and SMAPI reads/writes it via Helper.ReadConfig / Helper.WriteConfig.
/// </summary>
public class ModConfig
{
    // --- Chat backend ---
    public string Mode { get; set; } = "operit";

    // --- Co-op role ports ---
    public int HostPort { get; set; } = 58331;
    public int FarmhandPort { get; set; } = 58332;

    // --- MCP bridge (in-game chat panel <-> phone AI) ---
    // Port 14159, not 8000 or 18000: both are already IANA-assigned (irdmi / biimenu)
    // and 8000 is the default for uvicorn/FastAPI/Docker/MCP tooling too. 14159 is
    // unassigned. mcp_bridge/launcher.bat sets the same number.
    public string OperitBridgeUrl { get; set; } = "http://127.0.0.1:14159";
    public string OperitBridgeToken { get; set; } = "";

    // --- Operit native chat channel (default OFF, on demand) ---
    public bool forwardToOperitChat { get; set; } = false;
    public string operitWebUrl { get; set; } = "http://<Operit端点>:8094";
    public string operitWebChatId { get; set; } = "";
    public string operitWebToken { get; set; } = "";
    public bool forwardReadOperitReply { get; set; } = false;
    // How long to wait for Operit's reply stream before giving up. 0 = never cut it off
    // (default: an AI that is still thinking must not be disconnected mid-answer; only
    // the initial connect has a 15s cap).
    public int operitReplyTimeoutSeconds { get; set; } = 0;
    // When the SSE stream dies before assistant_done (Operit reports "Timed out while
    // waiting for response stream" for a cold agent, then answers anyway — verified
    // live), poll the chat history for the new reply instead of losing it.
    public bool operitHistoryFallback { get; set; } = true;
    public string operitForwardFormat { get; set; } = "【星露谷·{sender}】{message}";

    // --- Map / state reading ---
    public int chunkSize { get; set; } = 5;
    public string readWindow { get; set; } = "tool";
    public string stateOutput { get; set; } = "text";

    // --- Mod knowledge base ---
    public bool enableModKnowledge { get; set; } = true;
    public string sourceReadDepth { get; set; } = "intro";
    public List<string> modWhitelist { get; set; } = new();
    public List<string> modBlacklist { get; set; } = new();
    public bool cacheModUsage { get; set; } = true;

    // --- Hotkeys ---
    public string keybindChatPanel { get; set; } = "OemTilde";
    public string keybindBridgeToggle { get; set; } = "F8";
    public string keybindHelp { get; set; } = "F1";

    // --- Long-term memory channel ---
    // When true, the saved memories are prepended to each message forwarded to
    // Operit so the AI "remembers" past facts. Off by default to save tokens.
    public bool includeMemoryInForward { get; set; } = false;

    // --- AI reaction layer ---
    // When true, notable in-game events (e.g. a new day) are turned into a short
    // message the AI can react to. Off by default (on demand) to save tokens.
    public bool reactionEnabled { get; set; } = false;

    // --- Shared-experience journal ---
    // When true, notable events (items received, player emotes, new days) are
    // appended to the memory file with a timestamp, so the AI can remember what
    // happened together. Off by default (on demand) to keep the file short.
    public bool journalEnabled { get; set; } = false;

    // --- Gift reactions ---
    // When true, receiving an item looks up a rule in wheatstook_reactions.json
    // (set via the /react endpoint or wheatstook_react) and the farmhand emotes +
    // says the configured line. Off by default.
    public bool giftReactionsEnabled { get; set; } = false;

    // --- Day-end menu automation ---
    // When true the farmhand clicks through the day-end screens itself: the skill
    // level-up boxes, the level 5/10 profession choice, and the shipping/earnings
    // summary. In co-op those screens block the night and the host cannot click them
    // on a farmhand's behalf, so without this the night just hangs. Toggle live with
    // POST /autoconfirm (or the wheatstook_autoconfirm tool) without restarting.
    public bool autoConfirmDayEnd { get; set; } = true;

    // --- Profession choice at level 5/10 ---
    // "ai" (default) publishes the two options and waits for the AI to answer, falling
    // back to a random pick after professionTimeoutSeconds so the night never hangs.
    // Force a fixed answer with "left" / "right" / "random", or "off" to leave it to a
    // human. Answer live with POST /profession or the wheatstook_profession tool.
    public string professionMode { get; set; } = "ai";
    public int professionTimeoutSeconds { get; set; } = 45;

    // --- Dialogue options ---
    // When a dialogue offers choices (an NPC question, a shop prompt), the farmhand is
    // blocked until one is picked. "ai" (default) publishes the options and waits for
    // the AI to answer, falling back to the first option after professionTimeoutSeconds;
    // "first" / "random" force one, "off" leaves it to a human. Answer live with
    // POST /dialogue or the wheatstook_dialogue tool.
    public string dialogueChoiceMode { get; set; } = "ai";

    // --- Auto-compatibility layer ---
    // When enableAutoCompat is on, 麦垛 detects installed mods that change
    // world state (ring slots, backpack, custom crops/regions, professions, ...)
    // and activates the matching compat profile automatically. Off by default so
    // it only adapts when you opt in. compatOverrides forces a single profile
    // on/off by UniqueId and beats auto-detection.
    public bool enableAutoCompat { get; set; } = false;
    public Dictionary<string, bool>? compatOverrides { get; set; } = null;
}

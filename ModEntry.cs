using System.IO;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace WheatStook;

// Minimal GMCM API surface for the optional in-game config menu, matching the
// canonical GenericModConfigMenu 1.x shape (Register + Add*Option*, whose labels
// are Func<string> for tokenization). GetApi is wrapped in try/catch so an API-
// shape mismatch is silently skipped rather than logging an alarming stack trace.
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string>? tooltip = null, string[]? allowedValues = null, Func<string, string>? formatAllowedValue = null, string? fieldId = null);
}

/// <summary>
/// Clean-room rewrite of NagiBridge. This is a fresh, original implementation
/// (no upstream code) built up feature by feature.
///
/// Currently implemented:
///   - config layer (ModConfig <-> config.json)
///   - Operit native-chat forward + optional read-back (OperitChatClient)
///   - a console command 'wheatstook_operit' to exercise the channel
/// </summary>
public class ModEntry : Mod
{
    private ModConfig? _config;
    private OperitChatClient? _operitChat;
    private FarmhandServer? _server;
    private ModKnowledgeBase? _mods;
    private CompatLayer? _compat;
    private List<CompatRule> _compatRules = new();
    private ChatHud? _chatHud;
    private MemoryStore? _memory;
    private ReactionLayer? _reaction;
    private ReactionStore? _reactions;
    private SButton _chatKey = SButton.OemTilde;
    private SButton _bridgeKey = SButton.F8;
    private SButton _helpKey = SButton.F1;
    private bool _forwardEnabled = true;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        _operitChat = new OperitChatClient(_config!, Monitor);
        _chatHud = new ChatHud(Monitor);
        _chatHud.OnSubmit += OnChatSubmit;
        _chatKey = ParseKey(_config!.keybindChatPanel, SButton.OemTilde);
        _bridgeKey = ParseKey(_config!.keybindBridgeToggle, SButton.F8);
        _helpKey = ParseKey(_config!.keybindHelp, SButton.F1);
        var modDir = Path.GetDirectoryName(typeof(ModEntry).Assembly.Location) ?? ".";
        _memory = new MemoryStore(Path.Combine(modDir, "wheatstook_memory.txt"), Monitor);
        _reaction = new ReactionLayer(Monitor, SendAiMessage, s => _chatHud?.AddMessage(s), () => _config!.reactionEnabled);
        _reactions = new ReactionStore(Path.Combine(modDir, "wheatstook_reactions.json"), Monitor);

        // Emote watcher: SMAPI has no emote event, so a Harmony postfix on
        // Farmer.doEmote is the only way to see another player's emote. Wrapped so a
        // patch failure disables the feature instead of breaking the game.
        try
        {
            new Harmony(ModManifest.UniqueID).PatchAll(typeof(EmoteWatcher).Assembly);
            EmoteWatcher.OnEmote = OnOtherEmote;
            Monitor.Log("Emote watcher patched.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            EmoteWatcher.OnEmote = null;
            Monitor.Log($"Emote watcher patch failed (emote messages disabled): {ex.Message}", LogLevel.Warn);
        }

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Display.RenderedHud += OnRenderedHud;

        helper.ConsoleCommands.Add(
            "wheatstook_operit",
            "Forward an in-game message to Operit's native chat and read back its reply. Usage: wheatstook_operit <text>",
            OnOperitCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_mods",
            "Look up an installed mod from the knowledge base built at launch. Usage: wheatstook_mods <query> (empty lists all)",
            OnModsCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_help",
            "Show 麦垛 (WheatStook) commands and keybinds.",
            OnHelpCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_mem",
            "Manage long-term memory (the AI's persistent memories). Usage: wheatstook_mem <add <text> | list | del <text> | clear>",
            OnMemCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_selftest",
            "Run a quick self-check and print a diagnostic summary (config, channels, knowledge base, memory, server).",
            OnSelfTestCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_compat",
            "Explain the auto-compat framework: which things are auto-covered (runtime data) and which need a hand-written adapter, plus the active profiles.",
            OnCompatCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_react",
            "Manage gift reactions. Usage: wheatstook_react list | set <item> <emoteId> <text> | del <item> | match <item>",
            OnReactCommand);

        Monitor.Log($"WheatStook (clean-room) loaded. Mode={_config.Mode}, forwardToOperitChat={_config.forwardToOperitChat}", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        _mods = new ModKnowledgeBase(Monitor);
        _mods.BuildFrom(Helper.ModRegistry);
        _compat = new CompatLayer(_mods.UniqueIds, _config!, Monitor);
        _compat.Log();

        // Data-driven compat rules from CompatRules.json (comments allowed): only keep
        // rules whose mod profile is active, so they cost nothing when off.
        var rawRules = Helper.Data.ReadJsonFile<List<CompatRule>>("CompatRules.json") ?? new List<CompatRule>();
        _compatRules = rawRules.Where(r => !string.IsNullOrWhiteSpace(r.Mod) && _compat!.IsActive(r.Mod.Trim())).ToList();
        if (_compatRules.Count > 0)
            Monitor.Log($"CompatRules: {_compatRules.Count} 条识别规则生效.", LogLevel.Info);

        TryRegisterGmcm();
        Monitor.Log("WheatStook clean-room build is ready.", LogLevel.Info);
    }

    private void TryRegisterGmcm()
    {
        IGenericModConfigMenuApi? api = null;
        try
        {
            api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        }
        catch (Exception ex)
        {
            Monitor.Log($"GMCM API unavailable ({ex.Message}); config edited in config.json.", LogLevel.Info);
            return;
        }
        if (api is null)
        {
            Monitor.Log("GMCM not installed; config edited in config.json.", LogLevel.Info);
            return;
        }
        var modManifest = Helper.ModRegistry.Get("BeadyTooth57254.WheatStook")?.Manifest;
        if (modManifest is null)
        {
            Monitor.Log("GMCM: could not resolve this mod's manifest; skipping menu.", LogLevel.Warn);
            return;
        }
        try
        {
            api.Register(
                modManifest,
                () => { _config = new ModConfig(); Helper.WriteConfig(_config!); ApplyConfig(); },
                () => { Helper.WriteConfig(_config!); ApplyConfig(); });
            api.AddSectionTitle(modManifest, () => "麦垛 (WheatStook)", () => "AI 聊天 + farmhand 控制（干净封装）");
            api.AddTextOption(modManifest, () => _config!.Mode, v => _config!.Mode = v, () => "Mode", () => "聊天后端");
            api.AddNumberOption(modManifest, () => _config!.HostPort, v => _config!.HostPort = v, () => "HostPort", () => "host 实例端口");
            api.AddNumberOption(modManifest, () => _config!.FarmhandPort, v => _config!.FarmhandPort = v, () => "FarmhandPort", () => "farmhand 实例端口");
            api.AddBoolOption(modManifest, () => _config!.forwardToOperitChat, v => _config!.forwardToOperitChat = v, () => "forwardToOperitChat", () => "转发到 Operit 原生对话");
            api.AddBoolOption(modManifest, () => _config!.forwardReadOperitReply, v => _config!.forwardReadOperitReply = v, () => "forwardReadOperitReply", () => "读回 Operit 回复");
            api.AddBoolOption(modManifest, () => _config!.includeMemoryInForward, v => _config!.includeMemoryInForward = v, () => "includeMemoryInForward", () => "转发时带回忆");
            api.AddBoolOption(modManifest, () => _config!.reactionEnabled, v => _config!.reactionEnabled = v, () => "reactionEnabled", () => "AI 对事件有反应");
            api.AddNumberOption(modManifest, () => _config!.chunkSize, v => _config!.chunkSize = v, () => "chunkSize", () => "地图分块边长");
            api.AddTextOption(modManifest, () => _config!.readWindow, v => _config!.readWindow = v, () => "readWindow", () => "读取窗口 (tool/beehouse/navigate/explore)");
            api.AddTextOption(modManifest, () => _config!.stateOutput, v => _config!.stateOutput = v, () => "stateOutput", () => "状态输出 (text/image/auto)");
            api.AddTextOption(modManifest, () => _config!.keybindChatPanel, v => _config!.keybindChatPanel = v, () => "keybindChatPanel", () => "聊天面板按键");
            api.AddTextOption(modManifest, () => _config!.keybindBridgeToggle, v => _config!.keybindBridgeToggle = v, () => "keybindBridgeToggle", () => "转发开关按键");
            api.AddTextOption(modManifest, () => _config!.keybindHelp, v => _config!.keybindHelp = v, () => "keybindHelp", () => "帮助按键");
            Monitor.Log("GMCM config menu registered.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"GMCM registration failed (best-effort): {ex.Message}", LogLevel.Error);
        }
    }

    private void ApplyConfig()
    {
        _chatKey = ParseKey(_config!.keybindChatPanel, SButton.OemTilde);
        _bridgeKey = ParseKey(_config!.keybindBridgeToggle, SButton.F8);
        _helpKey = ParseKey(_config!.keybindHelp, SButton.F1);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // Start the farmhand control server once we're inside a session and this
        // instance is a co-op farmhand (not the main player). The host/chat side
        // is wired up separately (the in-game chat HUD brick).
        if (_server == null && Context.IsWorldReady && Game1.player != null && !Game1.player.IsMainPlayer)
        {
            _server = new FarmhandServer(_config!, Monitor, isHost: false, Helper);
            _server.CompatSummary = _compat?.Describe() ?? "";
            _server.CompatRules = _compatRules;
            _server.ChatDisplay = DisplayAiChat;
            _server.Memory = _memory;
            _server.Reactions = _reactions;
            _server.Mods = _mods;
            _server.Start();
        }
        _server?.Tick();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _server?.Stop();
        _server = null;
    }

    private void OnOperitCommand(string command, string[] args)
    {
        string text = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(text))
        {
            Monitor.Log("Usage: wheatstook_operit <text>", LogLevel.Info);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                string sender = string.IsNullOrWhiteSpace(Game1.player?.Name) ? "宿主" : Game1.player!.Name;
                string? reply = await _operitChat!.SendAndReadBackAsync(text, sender);
                if (reply is null)
                    Monitor.Log("Operit channel: no reply (disabled, not configured, or fire-and-forget).", LogLevel.Info);
                else
                    Monitor.Log($"Operit reply:\n{reply}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Operit chat error: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private void OnModsCommand(string command, string[] args)
    {
        string query = string.Join(' ', args);
        if (_mods is null)
        {
            Monitor.Log("Mod knowledge base not built yet (only after game launch).", LogLevel.Info);
            return;
        }
        var results = _mods.Search(query);
        if (results.Count == 0)
        {
            Monitor.Log($"No mods match '{query}'.", LogLevel.Info);
            return;
        }
        Monitor.Log($"{results.Count} mod(s) match '{query}':", LogLevel.Info);
        foreach (var m in results.Take(40))
            Monitor.Log($"  [{m.Name}] v{m.Version}{(m.IsContentPack ? " (content pack)" : "")} — {m.UniqueID} | {m.Author}{(m.NexusUrl.Length > 0 ? $" | {m.NexusUrl}" : "")}", LogLevel.Info);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == _chatKey)
        {
            _chatHud?.Toggle();
            return;
        }
        if (e.Button == _bridgeKey)
        {
            _forwardEnabled = !_forwardEnabled;
            Monitor.Log($"Operit forward {( _forwardEnabled ? "enabled" : "disabled")}.", LogLevel.Info);
            return;
        }
        if (e.Button == _helpKey)
        {
            ShowHelp();
            return;
        }
        // Keys are consumed by the panel while it is open (no key-suppression API here).
        _chatHud?.HandleKey(e.Button);
    }

    private static SButton ParseKey(string name, SButton fallback)
        => Enum.TryParse<SButton>(name, true, out var key) ? key : fallback;

    private void ShowHelp()
    {
        if (_chatHud is null) return;
        if (!_chatHud.IsOpen) _chatHud.Toggle();
        _chatHud.AddMessage("麦垛 帮助:");
        _chatHud.AddMessage($"  {_chatKey}: 聊天面板");
        _chatHud.AddMessage($"  {_bridgeKey}: 开关 Operit 转发 (当前 {( _forwardEnabled ? "开" : "关")})");
        _chatHud.AddMessage("  控制台: wheatstook_operit <话> / wheatstook_mods [关键词] / wheatstook_selftest / wheatstook_help");
    }

    private void OnHelpCommand(string command, string[] args)
    {
        Monitor.Log("麦垛 clean-room build — 命令:", LogLevel.Info);
        Monitor.Log($"  {_chatKey}: 游戏内聊天面板 (可与 Operit 对话)", LogLevel.Info);
        Monitor.Log($"  {_bridgeKey}: 开关 Operit 转发 (当前 {( _forwardEnabled ? "开" : "关")})", LogLevel.Info);
        Monitor.Log("  wheatstook_operit <话>: 直接把话发给 Operit 并读回", LogLevel.Info);
        Monitor.Log("  wheatstook_mods [关键词]: 查已装模组", LogLevel.Info);
        Monitor.Log("  wheatstook_selftest: 一键自检(配置/通道/知识库/记忆/服务器)", LogLevel.Info);
        Monitor.Log("  wheatstook_compat: 说明自动兼容(哪些自动覆盖/哪些需手写适配器)", LogLevel.Info);
        Monitor.Log("  wheatstook_help: 显示本帮助", LogLevel.Info);
        Monitor.Log("  wheatstook_mem add <话> | list | del <话> | clear: 长期记忆", LogLevel.Info);
    }

    private void OnMemCommand(string command, string[] args)
    {
        if (_memory is null) { Monitor.Log("Memory not ready yet.", LogLevel.Info); return; }
        string op = args.Length > 0 ? args[0].ToLower() : "list";
        switch (op)
        {
            case "add":
                string addText = string.Join(' ', args.Skip(1));
                _memory.Add(addText);
                Monitor.Log($"Added memory ({_memory.Count} total): {addText}", LogLevel.Info);
                break;
            case "del":
            case "delete":
                _memory.Remove(string.Join(' ', args.Skip(1)));
                Monitor.Log($"Removed memory ({_memory.Count} total).", LogLevel.Info);
                break;
            case "clear":
                foreach (var m in _memory.Context().Split('\n'))
                    if (!string.IsNullOrWhiteSpace(m)) _memory.Remove(m);
                Monitor.Log("Memory cleared.", LogLevel.Info);
                break;
            case "list":
            default:
                Monitor.Log($"Memory ({_memory.Count}):", LogLevel.Info);
                foreach (var m in _memory.Context().Split('\n'))
                    if (!string.IsNullOrWhiteSpace(m)) Monitor.Log("  · " + m, LogLevel.Info);
                break;
        }
    }

    private void OnSelfTestCommand(string command, string[] args)
    {
        Monitor.Log("=== 麦垛 self-test ===", LogLevel.Info);
        Monitor.Log($"[config] Mode={_config?.Mode}  forwardToOperitChat={_config?.forwardToOperitChat}", LogLevel.Info);
        Monitor.Log($"[operit] enabled={(_operitChat?.IsEnabled ?? false)}", LogLevel.Info);
        Monitor.Log($"[forward] toggle={_forwardEnabled}  (toggle with {_bridgeKey})", LogLevel.Info);
        Monitor.Log($"[knowledge] {_mods?.Count ?? 0} mods indexed", LogLevel.Info);
        Monitor.Log($"[compat] {_compat?.Describe() ?? "(未构建)"}", LogLevel.Info);
        Monitor.Log($"[memory] {_memory?.Count ?? 0} entries", LogLevel.Info);
        Monitor.Log($"[server] {(_server is { IsStarted: true } s ? $"{s.Role} bound on port {s.BoundPort}" : "not running (starts when a co-op farmhand session is ready)")}", LogLevel.Info);
        Monitor.Log("[hit] 敲 wheatstook_operit 测试 试试转发读回", LogLevel.Info);
        Monitor.Log("=== end self-test ===", LogLevel.Info);
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        _chatHud?.Draw();
    }

    private void OnCompatCommand(string command, string[] args)
    {
        Monitor.Log("=== 麦垛 自动兼容 (CompatLayer) ===", LogLevel.Info);
        Monitor.Log($"默认关 (enableAutoCompat=false), 按 UniqueId 检测; compatOverrides 可强开/强关. 已激活: {_compat?.Describe() ?? "(未构建)"}", LogLevel.Info);
        Monitor.Log("[自动覆盖 无需手写适配器] 用运行时真实数据读取: 作物/机器/宝石复制机/商店/附魔/温室/酒窖/建筑/农场; CP 内容包; 数据驱动 CompatRules.json。", LogLevel.Info);
        Monitor.Log("[需手写适配器] 数据表达不了的全新玩法: 新职业系统、行为异常的自定义机器、新小游戏、非数据驱动的全新机制。这类要在源码加 profile + 读取器。", LogLevel.Info);
        Monitor.Log("[自行添加, 不用写代码] 编辑模组目录 CompatRules.json (带注释 JSON), 给'原版没有的东西'贴标签即可。", LogLevel.Info);
        Monitor.Log($"[详情] 见仓库 COMPAT.md; 内置 profile 数: {_compat?.ProfileCount ?? 0}", LogLevel.Info);
    }

    private void OnReactCommand(string command, string[] args)
    {
        if (_reactions is null) return;
        if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var all = _reactions.All();
            Monitor.Log($"{all.Count} 条礼物反应规则 (giftReactionsEnabled={_config?.giftReactionsEnabled}):", LogLevel.Info);
            foreach (var r in all)
                Monitor.Log($"  [{r.item}] emote={r.emote} text={r.text}", LogLevel.Info);
            return;
        }
        switch (args[0].ToLowerInvariant())
        {
            case "set":
                if (args.Length < 2)
                {
                    Monitor.Log("用法: wheatstook_react set <item> [emoteId] [text...]", LogLevel.Info);
                    return;
                }
                int emote = args.Length >= 3 && int.TryParse(args[2], out var parsed) ? parsed : 4;
                string text = args.Length >= 4 ? string.Join(' ', args.Skip(3)) : "";
                _reactions.Set(args[1], emote, text);
                Monitor.Log($"已设置 [{args[1]}] emote={emote} text={text}", LogLevel.Info);
                break;
            case "del":
                if (args.Length < 2) { Monitor.Log("用法: wheatstook_react del <item>", LogLevel.Info); return; }
                Monitor.Log(_reactions.Remove(args[1]) ? "已删除" : "没找到该规则", LogLevel.Info);
                break;
            case "match":
                if (args.Length < 2) { Monitor.Log("用法: wheatstook_react match <item>", LogLevel.Info); return; }
                var hit = _reactions.Match(args[1]);
                Monitor.Log(hit is null ? "无匹配规则" : $"匹配: [{hit.item}] emote={hit.emote} text={hit.text}", LogLevel.Info);
                break;
            default:
                Monitor.Log("用法: wheatstook_react list | set <item> [emoteId] [text...] | del <item> | match <item>", LogLevel.Info);
                break;
        }
    }

    private void DisplayAiChat(string message)
    {
        // The AI said something in-game. The vanilla Game1.chatBox only renders on one
        // player's viewport, so it stayed invisible to the AI-farmhand view. Route it to
        // the mod's own chat panel (drawn on the active viewport via the global
        // spriteBatch) and pop it open so the player actually sees it.
        if (_chatHud is null) return;
        _chatHud.AddMessage("Operit: " + message);
        if (!_chatHud.IsOpen) _chatHud.Toggle();
        Monitor.Log($"AI chat (in-game): {message}", LogLevel.Info);
    }

    private void OnChatSubmit(string text)
    {
        if (!_forwardEnabled)
        {
            Monitor.Log($"(chat not forwarded; forward disabled by {_bridgeKey})", LogLevel.Info);
            return;
        }
        SendAiMessage(text);
    }

    private void SendAiMessage(string text)
    {
        Task.Run(async () =>
        {
            try
            {
                string sender = string.IsNullOrWhiteSpace(Game1.player?.Name) ? "宿主" : Game1.player!.Name;
                string forwardText = text;
                if (_config!.includeMemoryInForward && _memory is { Count: > 0 })
                    forwardText = $"[回忆]\n{_memory!.Context()}\n\n{text}";
                string? reply = await _operitChat!.SendAndReadBackAsync(forwardText, sender);
                if (reply != null)
                    _chatHud?.AddMessage("Operit: " + reply);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Operit chat error: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        _reaction?.OnDayStarted();
        if (_config?.journalEnabled == true)
            _memory?.Journal($"新的一天 · {Game1.Date?.Season} {Game1.Date?.DayOfMonth}日 · 金币 {Game1.player?.Money}");
    }

    /// <summary>
    /// Items the farmhand receives are packaged as a chat line, and a matching rule
    /// turns it into a custom gift reaction (emote + line). Gated by
    /// giftReactionsEnabled so a busy harvest day doesn't spam the panel unless the
    /// player asked for it.
    /// </summary>
    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        try
        {
            if (e?.Player is null) return;
            if (_config?.giftReactionsEnabled != true) return;
            foreach (var item in e.Added)
            {
                if (item is null) continue;
                string msg = $"【收到】{item.DisplayName} x{item.Stack}";
                _chatHud?.AddMessage(msg);
                if (_config.journalEnabled) _memory?.Journal(msg);

                var rule = _reactions?.Match(item.DisplayName);
                if (rule is null) continue;
                if (!string.IsNullOrWhiteSpace(rule.text))
                    _chatHud?.AddMessage($"{(string.IsNullOrWhiteSpace(Game1.player?.Name) ? "麦垛" : Game1.player!.Name)}: {rule.text}");
                Game1.player?.doEmote(rule.emote);
                _memory?.Journal($"对「{item.DisplayName}」的反应: {rule.text}");
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"OnInventoryChanged: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Another player emoted (see EmoteWatcher); package it as a line.</summary>
    private void OnOtherEmote(string farmerName, int emoteId)
    {
        try
        {
            if (Game1.player is not null && farmerName.Equals(Game1.player.Name, StringComparison.OrdinalIgnoreCase))
                return;
            if (_config?.journalEnabled != true && _config?.giftReactionsEnabled != true) return;
            string msg = $"【表情】{farmerName} 做了个表情 (id={emoteId})";
            _chatHud?.AddMessage(msg);
            if (_config.journalEnabled) _memory?.Journal(msg);
        }
        catch (Exception ex)
        {
            Monitor.Log($"OnOtherEmote: {ex.Message}", LogLevel.Warn);
        }
    }
}

/// <summary>
/// Harmony postfix on Farmer.doEmote. SMAPI exposes no emote event, so this is the
/// only way to notice that the human pressed an emote key. Everything is guarded:
/// if the patch never applies, OnEmote stays null and the feature is simply off.
/// </summary>
internal static class EmoteWatcher
{
    internal static Action<string, int>? OnEmote;

    [HarmonyPatch(typeof(Farmer), nameof(Farmer.doEmote))]
    internal static class Patch
    {
        private static void Postfix(Farmer __instance, int whichEmote)
        {
            try
            {
                if (__instance is null) return;
                OnEmote?.Invoke(__instance.Name ?? "?", whichEmote);
            }
            catch
            {
                // Never let a reaction break the game loop.
            }
        }
    }
}

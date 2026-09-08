using System.Net;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;
using Microsoft.Xna.Framework;

namespace WheatStook;

/// <summary>
/// Clean-room HTTP control server for the AI-controlled farmhand.
///
/// Binds the farmhand's role port (FarmhandPort) and serves the endpoints the
/// MCP bridge expects. Game mutations are marshalled onto the SMAPI update loop
/// via a main-thread queue, so the server never touches game state off-thread.
///
/// NOTE: v1 — this is the first working core. Some endpoints are intentionally
/// basic and will be refined (chunked state, better pathfinding, area ops, etc.).
/// </summary>
public class FarmhandServer
{
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;
    private readonly bool _isHost;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _started;
    private int _boundPort;

    // main-thread queue (marshalled onto the update loop)
    private readonly Queue<Action> _actions = new();
    private readonly object _actionsLock = new();

    // movement
    private Queue<Point>? _path;
    private int _moveCooldown;

    private readonly IModHelper _helper;
    private List<object>? _forgeEnchantments; // cached Pick Forge Enchantment tool->enchant map

    /// <summary>Called on the game thread when the AI sends an in-game chat message; the
    /// owner (ModEntry) routes it to the mod's own chat panel so the player can actually
    /// see it (the vanilla Game1.chatBox only renders on one player's viewport, so it was
    /// invisible to the AI-farmhand view).</summary>
    public Action<string>? ChatDisplay;

    /// <summary>Long-term memory store (set by ModEntry); backs /memory.</summary>
    public MemoryStore? Memory { get; set; }

    /// <summary>Custom gift-reaction rules (set by ModEntry); backs /react.</summary>
    public ReactionStore? Reactions { get; set; }

    /// <summary>Installed-mod knowledge base (set by ModEntry); backs /mods.</summary>
    public ModKnowledgeBase? Mods { get; set; }

    /// <summary>This mod's manifest version, set by ModEntry so /status never lies.</summary>
    public string ModVersion { get; set; } = "unknown";


    public FarmhandServer(ModConfig config, IMonitor monitor, bool isHost, IModHelper helper)
    {
        _config = config;
        _monitor = monitor;
        _isHost = isHost;
        _helper = helper;
    }

    public int Port => _isHost ? _config.HostPort : _config.FarmhandPort;

    /// <summary>Port the HTTP listener actually bound to (0 if not started).</summary>
    public int BoundPort => _boundPort;

    /// <summary>Whether the HTTP listener is currently running.</summary>
    public bool IsStarted => _started;

    /// <summary>Which side this server is (HOST or FARMHAND).</summary>
    public string Role => _isHost ? "HOST" : "FARMHAND";

    /// <summary>Human-readable summary of active auto-compat profiles, set by ModEntry.</summary>
    public string CompatSummary { get; set; } = "";

    /// <summary>Active data-driven compat rules (from CompatRules.json), set by ModEntry.</summary>
    public List<CompatRule> CompatRules { get; set; } = new();

    // ── lifecycle ──
    public void Start()
    {
        if (_started) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Bind the role port (then a few fallbacks). Prefer the wildcard prefix so the
        // server is reachable on every interface like the original wiring; if that needs
        // an HTTP URL ACL we don't have, fall back to loopback-only (no ACL required).
        int target = Port;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int tryPort = target + attempt;
            var listener = TryBindPort(tryPort);
            if (listener != null)
            {
                _listener = listener;
                _boundPort = tryPort;
                _monitor.Log($"WheatStook farmhand server listening on port {tryPort} (role={(_isHost ? "HOST" : "FARMHAND")})", LogLevel.Info);
                _started = true;
                break;
            }
            _monitor.Log($"Port {tryPort} unavailable, trying next...", LogLevel.Debug);
        }

        if (_listener == null)
        {
            _monitor.Log($"Failed to bind port {target}. Change HostPort/FarmhandPort in config.json.", LogLevel.Error);
            return;
        }

        Task.Run(() => AcceptLoopAsync(token), token);
    }

    /// <summary>
    /// Try to bind the HTTP listener on one port. First the wildcard prefix
    /// ("http://+:port/"), which matches the original wiring and is reachable on every
    /// interface; that needs an HTTP URL ACL for a non-admin process, so if it is denied
    /// we fall back to loopback-only ("http://localhost:port/"), which needs no ACL and
    /// is still reachable by the local MCP bridge.
    /// </summary>
    private HttpListener? TryBindPort(int port)
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://+:{port}/");
            l.Start();
            return l;
        }
        catch (Exception ex)
        {
            _monitor.Log($"wildcard :{port} unavailable ({ex.Message}); trying loopback", LogLevel.Debug);
        }

        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://localhost:{port}/");
            l.Start();
            return l;
        }
        catch (Exception ex)
        {
            _monitor.Log($"loopback :{port} rejected ({ex.Message})", LogLevel.Debug);
            return null;
        }
    }

    public void Stop()
    {
        _started = false;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequest(ctx), token);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                _monitor.Log($"Listener error: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    // ── main-thread marshalling / tick ──
    public void Enqueue(Action a)
    {
        lock (_actionsLock) _actions.Enqueue(a);
    }

    private int _tick;
    private sealed class DeferredAction { public int AtTick; public Action Run = null!; }
    private readonly List<DeferredAction> _deferred = new();

    /// <summary>
    /// Run an action on the game thread a few ticks from now. Needed for sequences
    /// where the game needs a tick to settle in between (e.g. warp to the bed, then
    /// use it) — Tick() drains the plain queue all at once, so that can't be split.
    /// </summary>
    public void EnqueueAfter(int ticks, Action a)
    {
        lock (_actionsLock)
            _deferred.Add(new DeferredAction { AtTick = _tick + Math.Max(1, ticks), Run = a });
    }

    /// <summary>Called from the mod's UpdateTicked. Drains the queue and steps movement.</summary>
    public void Tick()
    {
        _tick++;
        lock (_actionsLock)
        {
            while (_actions.Count > 0)
            {
                try { _actions.Dequeue().Invoke(); }
                catch (Exception ex) { _monitor.Log($"Queued server action error: {ex.Message}", LogLevel.Error); }
            }

            for (int i = _deferred.Count - 1; i >= 0; i--)
            {
                if (_deferred[i].AtTick > _tick) continue;
                var d = _deferred[i];
                _deferred.RemoveAt(i);
                try { d.Run(); }
                catch (Exception ex) { _monitor.Log($"Deferred server action error: {ex.Message}", LogLevel.Error); }
            }
        }

        StepMovement();
        AutoConfirmDayEnd();
        HandleDialogueChoice();
    }

    // ── day-end menu automation ──
    private int _dayEndTicks;
    private int _autoConfirmCooldown;
    private bool _autoConfirm = true;

    // pending level 5/10 profession choice: the AI decides, with a fallback so the night
    // never hangs waiting for an answer that may never come
    private LevelUpMenu? _pendingProfession;
    private int _pendingProfessionTicks;
    private int _pendingSkill = -1;
    private int _pendingLevel = -1;
    private int _pendingTimeoutTicks = 45 * 60;

    /// <summary>Auto-clear the day-end menus (skill-up, profession choice, shipping summary).</summary>
    public bool AutoConfirm { get => _autoConfirm; set => _autoConfirm = value; }

    /// <summary>How to answer a level 5/10 profession choice: "ai" (ask, then fall back),
    /// "random", "left", "right", or "off" (leave it for a human).</summary>
    public string ProfessionMode { get; set; } = "ai";

    /// <summary>Seconds to wait for an AI decision before falling back to random.</summary>
    public int ProfessionTimeoutSeconds
    {
        get => Math.Max(5, _pendingTimeoutTicks / 60);
        set => _pendingTimeoutTicks = Math.Max(5, value) * 60;
    }

    /// <summary>Optional push channel: called once when a decision is waiting for the AI.</summary>
    public Action<string>? AiNotify { get; set; }

    // pending dialogue choice (a question with options): the AI decides this one too
    private DialogueBox? _pendingDialogue;
    private int _pendingDialogueTicks;
    private string[] _pendingDialogueOptions = Array.Empty<string>();

    /// <summary>How to answer a dialogue that offers options: "ai" (ask, then fall back),
    /// "first", "random", or "off" (leave it to a human).</summary>
    public string DialogueChoiceMode { get; set; } = "ai";

    /// <summary>
    /// A farmhand that sleeps in co-op still gets the day-end screens — the skill
    /// level-up boxes, the level 5/10 profession choice, and the shipping/earnings
    /// summary — and they block the night until somebody clicks them. The host cannot
    /// click them for us, so clear them automatically while the day-end flow is active.
    /// </summary>
    private void AutoConfirmDayEnd()
    {
        if (!Context.IsWorldReady || Game1.player is null) return;

        var menu = Game1.activeClickableMenu;
        bool dayEndMenu = menu is LevelUpMenu or ShippingMenu or ConfirmationDialog;

        // In bed, just asked to sleep, or a day-end screen is already up → keep the
        // window open. These screens can appear after the clock has already rolled over
        // (isInBed back to false), so re-arm on the menus themselves as well.
        if (Game1.player.isInBed.Value || dayEndMenu) _dayEndTicks = Math.Max(_dayEndTicks, 120);
        if (_dayEndTicks > 0) _dayEndTicks--;
        if (!_autoConfirm || _dayEndTicks == 0) return;

        // A profession choice is a real decision, not a click-through: publish it, let
        // the AI answer while the night waits, and fall back so it never hangs.
        if (menu is LevelUpMenu prof && prof.isProfessionChooser)
        {
            HandleProfessionChoice(prof);
            return;
        }

        if (_autoConfirmCooldown > 0) { _autoConfirmCooldown--; return; }

        try
        {
            switch (menu)
            {
                case LevelUpMenu lum:
                    lum.okButtonClicked();
                    _monitor.Log("auto-confirm: 技能升级界面 确定", LogLevel.Info);
                    _autoConfirmCooldown = 20;
                    break;

                case ShippingMenu sm:
                    ClickComponent(sm, sm.okButton);
                    _monitor.Log("auto-confirm: 结算界面 确定", LogLevel.Info);
                    _autoConfirmCooldown = 20;
                    break;

                case ConfirmationDialog cd:
                    cd.confirm();
                    _monitor.Log("auto-confirm: 确认对话框 确定", LogLevel.Info);
                    _autoConfirmCooldown = 20;
                    break;
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"auto-confirm failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void ClickComponent(IClickableMenu menu, ClickableComponent? component)
    {
        if (component is null) return;
        menu.receiveLeftClick(component.bounds.Center.X, component.bounds.Center.Y, true);
    }

    /// <summary>
    /// Level 5/10 profession choice. This one is not a click-through: publish the two
    /// options so the AI can decide in the moment, and fall back after
    /// ProfessionTimeoutSeconds so a silent AI cannot hang the night forever.
    /// </summary>
    private void HandleProfessionChoice(LevelUpMenu lum)
    {
        if (!ReferenceEquals(_pendingProfession, lum))
        {
            _pendingProfession = lum;
            _pendingSkill = PrivateInt(lum, "currentSkill");
            _pendingLevel = PrivateInt(lum, "currentLevel");
            _pendingProfessionTicks = _pendingTimeoutTicks;

            string options = DescribeProfessions(lum);
            _monitor.Log($"职业二选一: {options} — 等 AI 决定 (最多 {_pendingProfessionTicks / 60}s)", LogLevel.Info);
            ChatDisplay?.Invoke($"【升级】{options} 我该选哪个？");
            AiNotify?.Invoke($"星露谷升级了，两个职业二选一：{options}。用 wheatstook_profession() 看选项，wheatstook_profession(choice=0 或 1) 回答。");
        }

        string mode = (ProfessionMode ?? "ai").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "off":
                return;
            case "left":
                ResolveProfession(0, "配置 left");
                return;
            case "right":
                ResolveProfession(1, "配置 right");
                return;
            case "random":
                ResolveProfession(Random.Shared.Next(2), "配置 random");
                return;
        }

        if (--_pendingProfessionTicks <= 0)
            ResolveProfession(Random.Shared.Next(2), "AI 没回话，随机决定");
    }

    /// <summary>Click the chosen profession. index 0 = left option, 1 = right option.</summary>
    private void ResolveProfession(int index, string reason)
    {
        var lum = _pendingProfession;
        _pendingProfession = null;
        _pendingProfessionTicks = 0;
        if (lum is null) return;
        try
        {
            ClickComponent(lum, index == 1 ? lum.rightProfession : lum.leftProfession);
            _monitor.Log($"auto-confirm: 职业选了 {ProfessionNameAt(lum, index)}（{reason}）", LogLevel.Info);
            ChatDisplay?.Invoke($"【升级】选了 {ProfessionNameAt(lum, index)}");
        }
        catch (Exception ex)
        {
            _monitor.Log($"profession click failed: {ex.Message}", LogLevel.Warn);
        }
        _autoConfirmCooldown = 20;
    }

    /// <summary>currentSkill / currentLevel are private too — read them by reflection.</summary>
    private static int PrivateInt(LevelUpMenu lum, string field)
    {
        try
        {
            object? v = lum.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(lum);
            return v is null ? -1 : Convert.ToInt32(v);
        }
        catch { return -1; }
    }

    /// <summary>LevelUpMenu keeps the offered profession ids private, so read them by reflection.</summary>
    private static List<int> ProfessionChoices(LevelUpMenu lum)
    {
        var result = new List<int>();
        try
        {
            var field = lum.GetType().GetField("professionsToChoose", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(lum) is System.Collections.IList list)
                foreach (var v in list) result.Add(Convert.ToInt32(v));
        }
        catch { }
        return result;
    }

    private static string ProfessionName(LevelUpMenu lum, int id)
    {
        if (id < 0) return "?";
        try
        {
            var method = lum.GetType().GetMethod("getProfessionName", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method?.Invoke(lum, new object[] { id }) is string name && name.Length > 0) return name;
        }
        catch { }
        return $"id {id}";
    }

    private static string ProfessionNameAt(LevelUpMenu lum, int index)
    {
        var ids = ProfessionChoices(lum);
        return index >= 0 && index < ids.Count ? ProfessionName(lum, ids[index]) : "?";
    }

    private static string DescribeProfessions(LevelUpMenu lum)
    {
        var ids = ProfessionChoices(lum);
        if (ids.Count == 0) return "两个职业";
        return string.Join("  /  ", ids.Select((id, i) => $"[{i}] {ProfessionName(lum, id)}"));
    }

    private object HandleAutoConfirm(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        if (p is not null && p.ContainsKey("enabled")) _autoConfirm = Get(p, "enabled", true);
        return new { ok = true, autoConfirm = _autoConfirm, dayEndActive = _dayEndTicks > 0, professionMode = ProfessionMode };
    }

    /// <summary>
    /// GET  /profession → what is being asked right now, if anything.
    /// POST /profession {"choice": 0|1|"left"|"right"|"random"} → answer it.
    /// </summary>
    private object HandleProfession(HttpListenerContext ctx)
    {
        var lum = _pendingProfession;

        if (ctx.Request.HttpMethod == "POST")
        {
            var p = ReadJson(ctx);
            if (p.ContainsKey("choice"))
            {
                if (lum is null)
                    return new { ok = false, error = "no profession choice is waiting right now" };

                string raw = Get(p, "choice", "").Trim().ToLowerInvariant();
                int index = raw switch
                {
                    "0" or "left" => 0,
                    "1" or "right" => 1,
                    "random" => Random.Shared.Next(2),
                    _ => -1,
                };
                if (index < 0)
                    throw new InvalidOperationException("choice must be 0/1, left/right or random");

                Enqueue(() => ResolveProfession(index, "AI 决定"));
                return new { ok = true, chose = index, name = ProfessionNameAt(lum, index) };
            }
        }

        if (lum is null) return new { ok = true, pending = false };
        var ids = ProfessionChoices(lum);
        return new
        {
            ok = true,
            pending = true,
            skill = _pendingSkill,
            level = _pendingLevel,
            secondsLeft = _pendingProfessionTicks / 60,
            options = ids.Select((id, i) => new { index = i, id, name = ProfessionName(lum, id) }).ToArray(),
        };
    }

    /// <summary>
    /// A dialogue that offers options (an NPC question, a shop prompt, ...) blocks the
    /// farmhand until one is picked. Publish the options so the AI can answer in
    /// character, and fall back after the timeout so it never wedges.
    /// </summary>
    private void HandleDialogueChoice()
    {
        if (!Context.IsWorldReady || Game1.player is null) return;

        string mode = (DialogueChoiceMode ?? "ai").Trim().ToLowerInvariant();
        if (mode == "off") return;

        if (Game1.activeClickableMenu is not DialogueBox box
            || box.responses is not { Length: > 0 }
            || box.responseCC is not { Count: > 0 })
        {
            _pendingDialogue = null;
            _pendingDialogueTicks = 0;
            return;
        }

        if (!ReferenceEquals(_pendingDialogue, box))
        {
            _pendingDialogue = box;
            _pendingDialogueOptions = box.responses.Select(r => r.responseText ?? "?").ToArray();
            _pendingDialogueTicks = _pendingTimeoutTicks;

            string options = DescribeResponses();
            _monitor.Log($"对话选项: {options} — 等 AI 决定 (最多 {_pendingDialogueTicks / 60}s)", LogLevel.Info);
            ChatDisplay?.Invoke($"【对话】{options}");
            AiNotify?.Invoke($"星露谷里有人问我：{options}。用 wheatstook_dialogue() 看选项，wheatstook_dialogue(choice=编号) 回答。");
        }

        switch (mode)
        {
            case "first":
                ResolveDialogue(0, "配置 first");
                return;
            case "random":
                ResolveDialogue(Random.Shared.Next(_pendingDialogueOptions.Length), "配置 random");
                return;
        }

        if (--_pendingDialogueTicks <= 0)
            ResolveDialogue(0, "AI 没回话，默认第一项");
    }

    /// <summary>Click the chosen dialogue response.</summary>
    private void ResolveDialogue(int index, string reason)
    {
        var box = _pendingDialogue;
        var options = _pendingDialogueOptions;
        _pendingDialogue = null;
        _pendingDialogueTicks = 0;
        if (box is null) return;
        try
        {
            var buttons = box.responseCC;
            if (buttons is not { Count: > 0 }) return;
            if (index < 0 || index >= buttons.Count) index = 0;
            ClickComponent(box, buttons[index]);
            string text = index < options.Length ? options[index] : "?";
            _monitor.Log($"对话选项: 选了「{text}」（{reason}）", LogLevel.Info);
            ChatDisplay?.Invoke($"【对话】选了「{text}」");
        }
        catch (Exception ex)
        {
            _monitor.Log($"dialogue click failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private string DescribeResponses()
    {
        if (_pendingDialogueOptions.Length == 0) return "几个选项";
        return string.Join("  /  ", _pendingDialogueOptions.Select((t, i) => $"[{i}] {t}"));
    }

    /// <summary>
    /// GET  /dialogue → what is being asked right now, if anything.
    /// POST /dialogue {"choice": 0|1|...|"first"|"random"} → answer it.
    /// </summary>
    private object HandleDialogue(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "POST")
        {
            var p = ReadJson(ctx);
            if (p.ContainsKey("choice"))
            {
                if (_pendingDialogue is null)
                    return new { ok = false, error = "no dialogue choice is waiting right now" };

                string raw = Get(p, "choice", "").Trim().ToLowerInvariant();
                int index = raw switch
                {
                    "first" or "0" => 0,
                    "random" => Random.Shared.Next(_pendingDialogueOptions.Length),
                    _ => int.TryParse(raw, out int n) ? n : -1,
                };
                if (index < 0 || index >= _pendingDialogueOptions.Length)
                    throw new InvalidOperationException($"choice must be 0..{_pendingDialogueOptions.Length - 1}, first or random");

                Enqueue(() => ResolveDialogue(index, "AI 决定"));
                return new { ok = true, chose = index, text = _pendingDialogueOptions[index] };
            }
        }

        if (_pendingDialogue is null) return new { ok = true, pending = false };
        return new
        {
            ok = true,
            pending = true,
            secondsLeft = _pendingDialogueTicks / 60,
            options = _pendingDialogueOptions.Select((t, i) => new { index = i, text = t }).ToArray(),
        };
    }

    private void StepMovement()
    {
        if (_path is not { Count: > 0 } || !Context.IsWorldReady || Game1.player is null) return;
        if (_moveCooldown > 0) { _moveCooldown--; return; }

        var farmer = Game1.player;
        var next = _path.Peek();
        var target = new Vector2(next.X * 64 + 32, next.Y * 64 + 32);
        var diff = target - farmer.Position;
        if (diff.Length() < 6f)
        {
            _path.Dequeue();
            _moveCooldown = 0;
            return;
        }

        // face the direction of travel
        if (Math.Abs(diff.X) > Math.Abs(diff.Y)) farmer.FacingDirection = diff.X > 0 ? 1 : 3;
        else farmer.FacingDirection = diff.Y > 0 ? 2 : 0;

        float speed = farmer.getMovementSpeed();
        if (diff.Length() < speed) farmer.Position = target;
        else { diff.Normalize(); farmer.Position += diff * speed; }
    }

    // ── HTTP plumbing ──
    private void HandleRequest(HttpListenerContext ctx)
    {
        var method = ctx.Request.HttpMethod;
        var path = ctx.Request.Url?.AbsolutePath ?? "/";

        try
        {
            object? result = (method, path) switch
            {
                ("GET", "/status") => HandleStatus(),
                ("GET", "/state") => HandleState(ctx),
                ("GET", "/surroundings") => HandleSurroundings(ctx, "surroundings"),
                ("GET", "/ctx") => HandleSurroundings(ctx, "ctx"),
                ("GET", "/machines") => HandleMachines(),
                ("GET", "/menu") => HandleMenu(),
                ("GET", "/inventory") => HandleInventory(ctx),
                ("GET", "/selftest") => HandleSelfTest(),
                ("GET", "/bed") => HandleBed(),
                ("GET", "/memory") => HandleMemoryRead(),
                ("GET", "/mods") => HandleMods(ctx),
                ("POST", "/move") => HandleMove(ctx),
                ("POST", "/stop") => HandleStop(),
                ("POST", "/face") => HandleFace(ctx),
                ("POST", "/interact") => HandleInteract(ctx),
                ("POST", "/tool") => HandleTool(ctx),
                ("POST", "/key") => HandleKey(ctx),
                ("POST", "/tractor") => HandleTractor(ctx),
                ("POST", "/drop") => HandleDrop(ctx),
                ("POST", "/follow") => HandleFollow(ctx),
                ("POST", "/area") => HandleArea(ctx),
                ("POST", "/sleep") => HandleSleep(ctx),
                ("POST", "/awake") => HandleAwake(ctx),
                ("POST", "/autoconfirm") => HandleAutoConfirm(ctx),
                ("GET", "/profession") => HandleProfession(ctx),
                ("POST", "/profession") => HandleProfession(ctx),
                ("GET", "/dialogue") => HandleDialogue(ctx),
                ("POST", "/dialogue") => HandleDialogue(ctx),
                ("POST", "/talk") => HandleTalk(ctx),
                ("POST", "/memory") => HandleMemoryWrite(ctx),
                ("POST", "/react") => HandleReact(ctx),
                ("POST", "/emote") => HandleEmote(ctx),
                ("POST", "/warp") => HandleWarp(ctx),
                ("POST", "/chat") => HandleChat(ctx),
                ("POST", "/select") => HandleSelect(ctx),
                ("POST", "/buy") => HandleBuy(ctx),
                _ => new { ok = false, error = $"Unknown endpoint: {path}" }
            };
            Respond(ctx, 200, result ?? new { ok = true });
        }
        catch (Exception ex)
        {
            Respond(ctx, 400, new { ok = false, error = ex.Message });
        }
    }

    private static Dictionary<string, object?> ReadJson(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body) ?? new();
    }

    private static T Get<T>(Dictionary<string, object?> d, string key, T def)
    {
        if (d.TryGetValue(key, out var v) && v != null)
        {
            if (v is JsonElement je)
                return je.ValueKind == JsonValueKind.Number ? (T)Convert.ChangeType(je.GetDouble(), typeof(T))
                                                            : (T)Convert.ChangeType(je.ToString(), typeof(T));
            return (T)Convert.ChangeType(v, typeof(T));
        }
        return def;
    }

    private T GetReq<T>(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null)
            throw new InvalidOperationException($"Missing parameter: {key}");
        return Get<T>(d, key, default!);
    }

    private static string JsonDict(object? o)
    {
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = false });
    }

    private static void Respond(HttpListenerContext ctx, int status, object body)
    {
        // Stamp every reply with the current in-game clock so the AI always knows the
        // time when any tool result comes back, not just on /state.
        System.Text.Json.Nodes.JsonNode? node = JsonSerializer.SerializeToNode(body);
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            obj["timeOfDay"] = Game1.timeOfDay;
            obj["gameTime"] = $"{Game1.currentSeason} {Game1.dayOfMonth}, Year {Game1.year}";
        }
        var json = node?.ToJsonString() ?? "{}";
        var buf = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    // ── handlers ──
    private object HandleStatus() => new
    {
        ok = true,
        server = "WheatStook",
        version = ModVersion,
        port = Port,
        role = _isHost ? "HOST" : "FARMHAND",
        isHost = _isHost,
        worldReady = Context.IsWorldReady,
        isMultiplayer = Context.IsMultiplayer
    };

    private object HandleState(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return new { ok = true, worldReady = false };

        // `full` gates the heavy readouts (inventory/buildings/chunk tiles/mods/teleports/forge)
        // behind an explicit request, so the default /state stays light and fast and the AI
        // calls a full read only when it genuinely needs everything.
        string fv = ctx.Request.QueryString["full"] ?? "";
        bool full = fv is "1" or "true";

        var farmer = Game1.player;
        var loc = farmer.currentLocation;

        var npcs = loc.characters.Select(n => new { name = n.Name, x = n.TilePoint.X, y = n.TilePoint.Y }).ToList();

        // Chunk-aware view: the current chunk's origin plus the notable tiles in it,
        // so a single /state call also tells the AI what's right around the player.
        int chunkSize = Math.Max(1, _config.chunkSize);
        string readWindow = _config.readWindow;
        int half = chunkSize / 2;
        int cx = farmer.TilePoint.X, cy = farmer.TilePoint.Y;
        int mapW = loc.Map.DisplayWidth / 64, mapH = loc.Map.DisplayHeight / 64;
        var chunkTiles = new List<object>();
        if (full)
        {
            foreach (var p in ComputeReadTiles(chunkSize, "tool"))
            {
                int tx = p.X, ty = p.Y;
                if (tx < 0 || ty < 0 || tx >= mapW || ty >= mapH) continue;
                var tv = new Vector2(tx, ty);
                bool passable = loc.isTilePassable(tv);
                string? obj = null;
                if (loc.objects.TryGetValue(tv, out var o)) obj = o.Name;
                string? terrain = null;
                if (loc.terrainFeatures.TryGetValue(tv, out var tf))
                {
                    if (tf is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.crop != null) terrain = $"HoeDirt:{ResolveItemName(dirt.crop.indexOfHarvest.Value)}";
                    else terrain = tf.GetType().Name;
                }
                if (obj != null || terrain != null || !passable)
                    chunkTiles.Add(new { x = tx, y = ty, passable, obj, terrain });
            }
        }
        object chunkInfo = new { size = chunkSize, window = readWindow, origin = new { x = cx - half, y = cy - half }, tiles = chunkTiles };

        object? menu = null;
        if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
        {
            string? t = null;
            try { t = db.getCurrentString(); } catch { }
            menu = new { type = db.GetType().Name, dialogue = string.IsNullOrEmpty(t) ? null : t };
        }
        else if (Game1.activeClickableMenu != null)
        {
            menu = new { type = Game1.activeClickableMenu.GetType().Name, dialogue = (string?)null };
        }

        return new
        {
            ok = true,
            worldReady = true,
            player = new
            {
                name = farmer.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y,
                tile = TilePos(farmer.TilePoint.X, farmer.TilePoint.Y, chunkSize),
                stamina = farmer.Stamina,
                maxStamina = farmer.MaxStamina,
                health = farmer.health,
                maxHealth = farmer.maxHealth,
                money = farmer.Money,
                facingDirection = farmer.FacingDirection,
                currentTool = farmer.CurrentTool?.Name,
                enchantments = CurrentToolEnchantments(),
                backpackCapacity = farmer.MaxItems,
                ridingTractor = RidingTractor(),
                mountName = farmer.mount?.Name,
                isMoving = _path is { Count: > 0 }
            },
            location = new { name = loc.Name, mapWidth = loc.Map.DisplayWidth / 64, mapHeight = loc.Map.DisplayHeight / 64, isGreenhouse = IsGreenhouseLocation(loc), isCellar = IsCellarLocation(loc) },
            time = new { timeOfDay = Game1.timeOfDay, dayOfMonth = Game1.dayOfMonth, season = Game1.currentSeason, year = Game1.year },
            stateOutput = _config.stateOutput,
            compat = CompatSummary.Length > 0 ? CompatSummary : "(未激活兼容profile)",
            forgeEnchantments = full ? ReadForgeEnchantments() : null,
            activeMenu = menu,
            npcs,
            inventory = full ? ReadInventory() : null,
            buildings = full ? CollectBuildings(loc) : null,
            chunk = chunkInfo,
            mods = full ? BuildModState(loc) : null,
            teleports = full ? CollectTeleports(loc) : null
        };
    }

    // ---- Mod behaviour layer (honest, data-driven: these only report what the mod
    //      actually does / what is present in-game, never guess). ----

    /// <summary>Report a tile as precise (x,y) + the chunk index it belongs to + its
    /// sub-position inside that chunk, so the AI gets an explicit coordinate hierarchy
    /// (e.g. world tile 72,38 → chunk 14,7 sub 2,3 for chunkSize 5).</summary>
    private static object TilePos(int x, int y, int size)
    {
        int cs = Math.Max(1, size);
        int chunkX = (int)Math.Floor((double)x / cs), chunkY = (int)Math.Floor((double)y / cs);
        return new { x, y, chunk = new { x = chunkX, y = chunkY, size = cs }, sub = new { x = x - chunkX * cs, y = y - chunkY * cs } };
    }

    /// <summary>Full inventory snapshot (name/stack/category) for /state?full=1.</summary>
    private object ReadInventory()
    {
        var farmer = Game1.player;
        if (farmer is null) return new List<object>();
        return farmer.Items
            .Where(i => i != null)
            .Select(i => new { name = i.Name, stack = i.Stack, category = i.getCategoryName() })
            .ToList();
    }

    /// <summary>Self-check for diagnosing why the AI loop isn't working: server binding,
    /// config snapshot, mod count, memory and live compat profiles. Mirrors the README's
    /// console selftest but exposed over the HTTP/MCP path so the AI can run it itself.</summary>
    private object HandleSelfTest()
    {
        int modCount = 0;
        List<string>? loaded = null;
        try
        {
            if (_helper?.ModRegistry != null)
            {
                var all = _helper.ModRegistry.GetAll().ToList();
                modCount = all.Count;
                loaded = all.Select(m => m.Manifest.UniqueID).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(30).ToList();
            }
        }
        catch { }
        int memMB = (int)(GC.GetTotalMemory(false) / 1048576);
        return new
        {
            ok = true,
            server = new
            {
                role = Role, port = Port, boundPort = _boundPort, started = _started,
                worldReady = Context.IsWorldReady,
                multiplayer = Context.IsMultiplayer
            },
            config = new
            {
                mode = _config.Mode,
                hostPort = _config.HostPort,
                farmhandPort = _config.FarmhandPort,
                operitBridgeUrl = _config.OperitBridgeUrl,
                forwardToOperitChat = _config.forwardToOperitChat,
                operitWebUrl = _config.operitWebUrl,
                enableAutoCompat = _config.enableAutoCompat,
                stateOutput = _config.stateOutput,
                chunkSize = _config.chunkSize,
                readWindow = _config.readWindow
            },
            compat = new { active = CompatSummary.Length > 0 ? CompatSummary : "(未激活或尚未构建)", rules = CompatRules.Count },
            mods = new { count = modCount, loaded },
            memory = new { mb = memMB }
        };
    }

    /// <summary>Read ONE row of the inventory grid, together with time + local map, so the
    /// AI can manage the bag row by row without a full dump. Labels the equip hotkey and
    /// how to move to another row (vanilla Stardew has no row-switch hotkey, so rows are
    /// picked by the `row` param).</summary>
    private object HandleInventory(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return new { ok = false, error = "World not ready" };
        var farmer = Game1.player;
        int row = int.TryParse(ctx.Request.QueryString["row"], out var r) && r >= 0 ? r : 0;
        const int perRow = 12;
        int totalSlots = farmer.MaxItems;
        int totalRows = Math.Max(1, (totalSlots + perRow - 1) / perRow);
        row = Math.Min(row, totalRows - 1);
        int start = row * perRow;
        var slots = new List<object>();
        for (int i = start; i < Math.Min(start + perRow, totalSlots); i++)
        {
            var it = i < farmer.Items.Count ? farmer.Items[i] : null;
            string? hotkey = i <= 8 ? (i + 1).ToString() : (i == 9 ? "0" : null);
            slots.Add(new { index = i, hotkey, name = it?.Name, stack = it?.Stack, category = it?.getCategoryName() });
        }
        int chunkSize = Math.Max(1, _config.chunkSize);
        return new
        {
            ok = true, row, totalRows, perRow, slots,
            time = new { timeOfDay = Game1.timeOfDay, dayOfMonth = Game1.dayOfMonth, season = Game1.currentSeason, year = Game1.year },
            chunk = new { size = chunkSize, origin = new { x = farmer.TilePoint.X, y = farmer.TilePoint.Y } },
            keys = new
            {
                equip = "slot hotkey 1-9,0 (toolbar row 0), or call select to bring a named item to hand",
                nextRow = "pass row=N to read another row (vanilla Stardew has no hotkey to switch inventory rows)"
            }
        };
    }


    /// <summary>Whether a given uniqueId is loaded in this session (real, not assumed).</summary>
    private bool IsModLoaded(string uniqueId)
    {
        try { return _helper?.ModRegistry?.IsLoaded(uniqueId) ?? false; } catch { return false; }
    }

    /// <summary>Physical teleport points in the current location (e.g. mini-obelisks).</summary>
    private static List<object> CollectTeleports(GameLocation loc)
    {
        var list = new List<object>();
        if (loc.objects is null) return list;
        foreach (var o in loc.objects.Values)
        {
            if (o is null) continue;
            string? nm = o.Name;
            if (string.IsNullOrEmpty(nm)) continue;
            if (nm.Contains("Obelisk", StringComparison.OrdinalIgnoreCase))
                list.Add(new { x = (int)o.TileLocation.X, y = (int)o.TileLocation.Y, name = nm });
        }
        return list;
    }

    /// <summary>FishingAssistant2 (自动钓鱼) behaviour: read its real keybinds + auto-stop rule so the
    /// AI can rely on it (auto-cast/hook/mini-game are on; it stops at midnight, full inventory, or
    /// out of stamina). Returns null when the mod isn't loaded.</summary>
    private object? ReadFishingAssistant()
    {
        if (!IsModLoaded("ChibiKyu.FishingAssistant2")) return null;
        string? toggle = null, chest = null, inventoryFull = null;
        try
        {
            var dir = FindModDir("ChibiKyu.FishingAssistant2");
            if (!string.IsNullOrEmpty(dir))
            {
                var cfg = Path.Combine(dir, "config.json");
                if (File.Exists(cfg))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                    var root = doc.RootElement;
                    toggle = root.TryGetProperty("EnableAutomationButton", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
                    chest = root.TryGetProperty("ToggleTreasureTargetingButton", out var c2) && c2.ValueKind == System.Text.Json.JsonValueKind.String ? c2.GetString() : null;
                    inventoryFull = root.TryGetProperty("ActionIfInventoryFull", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.String ? i.GetString() : null;
                }
            }
        }
        catch { }
        return new
        {
            active = true,
            toggleKey = toggle,
            chestKey = chest,
            autoStopWhenInventoryFull = string.Equals(inventoryFull, "Stop", StringComparison.OrdinalIgnoreCase),
            autoStopAtMidnight = true,
            autoStopsWhenOutOfStamina = true,
            runsAutomatically = true
        };
    }

    /// <summary>Skull Cavern Elevator (lestoph): every N floors there's an elevator you can
    /// use to descend quickly instead of stairs. Read its config so the AI can fast-travel down.
    /// Returns null when the mod isn't loaded.</summary>
    private object? ReadSkullCavernElevator()
    {
        if (!IsModLoaded("SkullCavernElevator")) return null;
        int? step = null, cost = null;
        try
        {
            var dir = FindModDir("SkullCavernElevator");
            if (!string.IsNullOrEmpty(dir))
            {
                var cfg = Path.Combine(dir, "config.json");
                if (File.Exists(cfg))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                    var root = doc.RootElement;
                    step = root.TryGetProperty("ElevatorStep", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetInt32() : (int?)null;
                    cost = root.TryGetProperty("ElevatorCostPerStep", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt32() : (int?)null;
                }
            }
        }
        catch { }
        return new
        {
            active = true,
            floorStep = step,
            costPerStep = cost
        };
    }

    /// <summary>Read any string/property value from a mod's config.json. Returns null if the
    /// mod isn't installed, has no config, or the property is absent. Used for data-driven compat
    /// readouts so the AI knows the real value, not a guessed default.</summary>
    private string? ReadConfigProp(string uniqueId, string prop)
    {
        try
        {
            var dir = FindModDir(uniqueId);
            if (string.IsNullOrEmpty(dir)) return null;
            var cfg = Path.Combine(dir, "config.json");
            if (!File.Exists(cfg)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.TryGetProperty(prop, out var v)) return v.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>Summarise the impactful gameplay mods and what they change, so the AI
    /// can adapt (don't manually operate automated machines; chests are reachable remotely;
    /// junimos handle chores). Every flag is real: read from the loaded mod registry.</summary>
    private object BuildModState(GameLocation loc)
    {
        bool automate   = IsModLoaded("Pathoschild.Automate");
        bool junimo     = IsModLoaded("hawkfalcon.BetterJunimos");
        bool chestsAny  = IsModLoaded("Pathoschild.ChestsAnywhere");
        bool fridge     = IsModLoaded("EternalSoap.RemoteFridgeStorage");
        bool resStorage = IsModLoaded("FlyingTNT.ResourceStorage");
        bool obelisk    = IsModLoaded("PeacefulEnd.MultipleMiniObelisks");
        bool bigPack    = IsModLoaded("spacechase0.BiggerBackpack");
        int junimoRange = junimo ? ReadJunimoRadius() : 0;

        return new
        {
            automationActive = automate,
            junimoHelpersActive = junimo,
            junimoRange = junimoRange > 0 ? junimoRange : (int?)null,
            remoteChestAccess = chestsAny || fridge || resStorage,
            chestsAnywhere = chestsAny,
            remoteFridge = fridge,
            resourceStorage = resStorage,
            miniObelisks = obelisk,
            bigBackpack = bigPack,
            fishingAssistant = ReadFishingAssistant(),
            skullCavernElevator = ReadSkullCavernElevator(),
            automaticGates = IsModLoaded("Rakiin.AutomaticGates") ? new
            {
                active = true,
                gateDelayMs = ReadConfigProp("Rakiin.AutomaticGates", "GateDelay")
            } : null,
            supplyCrates = IsModLoaded("otc.supplycratesonbeach") ? new
            {
                active = true,
                chancePct = ReadConfigProp("otc.supplycratesonbeach", "SpawnPercentageChance"),
                days = ReadConfigProp("otc.supplycratesonbeach", "NumberOfDays")
            } : null,
            seedDrop = IsModLoaded("recon88.HarvestSeedsContinued") ? new
            {
                active = true,
                seedChance = ReadConfigProp("recon88.HarvestSeedsContinued", "SeedChance"),
                guaranteedSeeds = ReadConfigProp("recon88.HarvestSeedsContinued", "GuaranteedSeeds")
            } : null,
            moreMonsters = IsModLoaded("Hong.MoreMonsters") ? new
            {
                active = true,
                spawnMultiplier = ReadConfigProp("Hong.MoreMonsters", "MonsterMulty")
            } : null,
            skillfulClothes = IsModLoaded("LunaticShade.SkillfulClothes") ? new
            {
                active = true,
                shirtEffects = ReadConfigProp("LunaticShade.SkillfulClothes", "EnableShirtEffects"),
                pantsEffects = ReadConfigProp("LunaticShade.SkillfulClothes", "EnablePantsEffects"),
                hatEffects = ReadConfigProp("LunaticShade.SkillfulClothes", "EnableHatEffects")
            } : null,
            letsMoveIt = IsModLoaded("Exblosis.LetsMoveIt") ? new
            {
                active = true,
                modKey = ReadConfigProp("Exblosis.LetsMoveIt", "ModKey"),
                moveKey = ReadConfigProp("Exblosis.LetsMoveIt", "MoveKey"),
                moveBuilding = ReadConfigProp("Exblosis.LetsMoveIt", "EnableMoveBuilding"),
                moveObject = ReadConfigProp("Exblosis.LetsMoveIt", "EnableMoveObject"),
                moveTree = ReadConfigProp("Exblosis.LetsMoveIt", "EnableMoveTree"),
                moveCrop = ReadConfigProp("Exblosis.LetsMoveIt", "EnableMoveCrop")
            } : null
        };
    }

    // ---- Automate / Better Junimos behaviour (honest: geometry + config based) ----

    /// <summary>A machine linked to automation and the chest(s) that feed it.</summary>
    private sealed class AutoLink
    {
        public List<string> Chests = new();
        public bool ViaConnector;
    }

    private static bool OrthoAdjacent(Vector2 a, Vector2 b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1;

    private string? _automateConnector;
    private bool _automateConnectorRead;
    /// <summary>The Automate connector floor name(s) from its config (`ConnectorNames` — the
    /// "specific floor" that bridges non-adjacent machines/chests), or null if none.</summary>
    private string? ReadAutomateConnector()
    {
        if (_automateConnectorRead) return _automateConnector;
        _automateConnectorRead = true;
        if (!IsModLoaded("Pathoschild.Automate")) return null;
        try
        {
            var dir = FindModDir("Pathoschild.Automate");
            if (string.IsNullOrEmpty(dir)) return null;
            var cfg = Path.Combine(dir, "config.json");
            if (File.Exists(cfg))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("ConnectorNames", out var c))
                    _automateConnector = c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() : null;
                else if (doc.RootElement.TryGetProperty("Connector", out var c2))
                    _automateConnector = c2.ValueKind == System.Text.Json.JsonValueKind.String ? c2.GetString() : null;
            }
        }
        catch { }
        return string.IsNullOrEmpty(_automateConnector) ? null : _automateConnector;
    }

    /// <summary>For each machine object, whether Automate would automate it and which chest feeds it.
    /// Automate connects a machine to an orthogonally-adjacent chest, and bridges non-adjacent ones
    /// through the connector floor. Only reported when Automate is loaded.</summary>
    private Dictionary<Vector2, AutoLink> ComputeAutomation(GameLocation loc)
    {
        var map = new Dictionary<Vector2, AutoLink>();
        if (!IsModLoaded("Pathoschild.Automate")) return map;

        var chestTiles = new HashSet<Vector2>();
        foreach (var o in loc.objects.Values)
            if (o is StardewValley.Objects.Chest c) chestTiles.Add(c.TileLocation);
        if (chestTiles.Count == 0) return map;

        string? connector = ReadAutomateConnector();
        var connTiles = new HashSet<Vector2>();
        if (!string.IsNullOrEmpty(connector))
            foreach (var o in loc.objects.Values)
                if (o is not null && !string.IsNullOrEmpty(o.Name) && o.Name.Equals(connector, StringComparison.OrdinalIgnoreCase))
                    connTiles.Add(o.TileLocation);

        foreach (var o in loc.objects.Values)
        {
            if (o is null || o is StardewValley.Objects.Chest) continue;
            var tile = o.TileLocation;
            var link = new AutoLink();
            foreach (var cpos in chestTiles)
                if (OrthoAdjacent(tile, cpos) && loc.objects.TryGetValue(cpos, out var ch))
                    link.Chests.Add(ch.Name);
            if (link.Chests.Count == 0 && connTiles.Count > 0)
            {
                foreach (var ct in connTiles)
                {
                    if (!OrthoAdjacent(tile, ct)) continue;
                    foreach (var cpos in chestTiles)
                        if (OrthoAdjacent(ct, cpos) && loc.objects.TryGetValue(cpos, out var ch))
                        { link.Chests.Add(ch.Name); link.ViaConnector = true; }
                }
            }
            if (link.Chests.Count > 0) map[tile] = link;
        }
        return map;
    }

    private int ReadJunimoRadius()
    {
        // Better Junimos adds a defined JunimoHut range. Read it, else default to vanilla-ish.
        try
        {
            if (!IsModLoaded("hawkfalcon.BetterJunimos")) return 0;
            var dir = FindModDir("hawkfalcon.BetterJunimos");
            if (string.IsNullOrEmpty(dir)) return 0;
            var cfg = Path.Combine(dir, "config.json");
            if (File.Exists(cfg))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("JunimoHut", out var jh)
                    && jh.TryGetProperty("JunimoRange", out var jr) && jr.ValueKind == System.Text.Json.JsonValueKind.Number)
                    return jr.GetInt32();
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Whether a location is a greenhouse (crops grow year-round), incl. CP-added ones.
    /// The vanilla greenhouse interior and CP greenhouse maps are named with "Greenhouse".</summary>
    private static bool IsGreenhouseLocation(GameLocation loc)
    {
        string? name = loc.NameOrUniqueName;
        return !string.IsNullOrEmpty(name)
            && name.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a location is a cellar (casks age wine/cheese), incl. CP-added ones.
    /// The vanilla cellar interior and build-more-cellars maps are named with "Cellar".</summary>
    private static bool IsCellarLocation(GameLocation loc)
        => !string.IsNullOrEmpty(loc.NameOrUniqueName)
            && loc.NameOrUniqueName.Contains("Cellar", StringComparison.OrdinalIgnoreCase);

    /// <summary>List buildings in the current location, flagging greenhouses (vanilla or CP-added).</summary>
    private static List<object> CollectBuildings(GameLocation loc)
    {
        var list = new List<object>();
        try
        {
            foreach (var b in loc.buildings)
            {
                string type = string.IsNullOrEmpty(b.buildingType.Value) ? b.GetType().Name : b.buildingType.Value;
                list.Add(new
                {
                    type,
                    x = b.tileX.Value,
                    y = b.tileY.Value,
                    isGreenhouse = type.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase),
                    isCellar = type.Contains("Cellar", StringComparison.OrdinalIgnoreCase),
                    isJunimoHut = type.Contains("Junimo Hut", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch { /* buildings may not be loaded in every location */ }
        return list;
    }

    private object HandleSurroundings(HttpListenerContext ctx, string kind)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return new { ok = false, error = "World not ready" };

        int chunkSize = Math.Max(1, _config.chunkSize);
        string window = _config.readWindow;
        // /ctx asks for a tighter window by default (the "immediate" one).
        if (kind == "ctx" && string.IsNullOrWhiteSpace(ctx.Request.QueryString["window"]))
            window = "tool";

        var farmer = Game1.player;
        var loc = farmer.currentLocation;
        int cx = farmer.TilePoint.X, cy = farmer.TilePoint.Y;
        int mapW = loc.Map.DisplayWidth / 64, mapH = loc.Map.DisplayHeight / 64;

        var tiles = new List<object>();
        foreach (var p in ComputeReadTiles(chunkSize, window))
        {
            int tx = p.X, ty = p.Y;
            if (tx < 0 || ty < 0 || tx >= mapW || ty >= mapH) continue;
            var tv = new Vector2(tx, ty);
            bool passable = loc.isTilePassable(tv);

            string? obj = null;
            if (loc.objects.TryGetValue(tv, out var o)) obj = o.Name;

            string? terrain = null;
            bool watered = false, harvestable = false;
            string? crop = null;
            if (loc.terrainFeatures.TryGetValue(tv, out var tf))
            {
                terrain = tf.GetType().Name;
                if (tf is StardewValley.TerrainFeatures.HoeDirt dirt)
                {
                    terrain = "HoeDirt";
                    watered = dirt.state.Value == 1;
                    if (dirt.crop != null) { crop = ResolveItemName(dirt.crop.indexOfHarvest.Value); harvestable = dirt.readyForHarvest(); }
                }
                else if (tf is StardewValley.TerrainFeatures.Tree tree) terrain = $"Tree:{tree.treeType.Value}";
            }

            StardewValley.NPC? n = loc.characters.FirstOrDefault(c => c.TilePoint.X == tx && c.TilePoint.Y == ty);
            string? npc = n?.Name;
            bool isTractor = IsTractorHorse(n);

            bool has = obj != null || terrain != null || npc != null || !passable || harvestable || watered;
            if (has)
            {
                var t = new Dictionary<string, object?> { ["x"] = tx, ["y"] = ty, ["passable"] = passable };
                if (obj != null)
                {
                    t["object"] = obj;
                    AppendRuleTags(t, o, ref harvestable);
                }
                if (terrain != null) t["terrain"] = terrain;
                if (watered) t["watered"] = true;
                if (crop != null) t["crop"] = crop;
                if (harvestable) t["harvestable"] = true;
                if (npc != null)
                {
                    t["npc"] = npc;
                    if (isTractor) t["tractor"] = true;
                }
                tiles.Add(t);
            }
        }

        string? map = null;
        if (kind == "ctx")
        {
            int radius = 8;
            if (int.TryParse(ctx.Request.QueryString["radius"], out var r) && r is >= 1 and <= 30) radius = r;
            map = _config.stateOutput == "image" ? RenderMapImage(radius) : BuildAsciiMap(radius);
        }
        // Ground items (dropped tools/objects). Skipped while riding a tractor with a tool
        // in hand — the tractor harvests a huge field of dropped items, so a ground read
        // would be absurd. Tool descriptions document this.
        bool onTractorWithTool = RidingTractor() && Game1.player.CurrentTool != null;
        List<object> groundItems = new List<object>();
        if (!onTractorWithTool)
        {
            foreach (var d in loc.debris)
            {
                if (d?.item is null) continue;
                var chunk = d.Chunks?.FirstOrDefault();
                if (chunk is null) continue;
                var gp = chunk.position.Value;
                groundItems.Add(new { x = (int)(gp.X / 64), y = (int)(gp.Y / 64), item = d.item.Name });
            }
        }
        return new
        {
            ok = true, kind,
            center = new { x = cx, y = cy },
            chunk = new { size = chunkSize, window },
            onTractorWithTool,
            groundItems = onTractorWithTool ? new List<object>() : groundItems,
            groundItemsNote = onTractorWithTool ? "ground items skipped (riding tractor + holding tool)" : (string?)null,
            map, tiles
        };
    }

    /// <summary>Whether an NPC is a Tractor Mod tractor (by its own modData marker).</summary>
    private static bool IsTractorHorse(StardewValley.NPC? npc)
    {
        return npc is StardewValley.Characters.Horse horse
            && horse.modData.TryGetValue("Pathoschild.TractorMod", out _);
    }

    /// <summary>Whether the current player is riding a Tractor Mod tractor.</summary>
    public static bool RidingTractor()
    {
        return Game1.player != null
            && Game1.player.isRidingHorse()
            && Game1.player.mount != null
            && IsTractorHorse(Game1.player.mount);
    }

    /// <summary>Enchantments on the currently held tool (works for vanilla, "Pick Forge
    /// Enchantment" (auto-selects one) and "Many Enchantments" (stacking)).</summary>
    private static List<string> CurrentToolEnchantments()
    {
        var list = new List<string>();
        try
        {
            if (Game1.player?.CurrentTool is StardewValley.Tool tool && tool.enchantments != null)
                foreach (var e in tool.enchantments)
                    if (e != null) list.Add(e.GetName());
        }
        catch { /* enchantments may not load on every frame */ }
        return list;
    }

    /// <summary>Read Pick Forge Enchantment's config.json to surface which enchantment the
    /// Forge will auto-apply to each tool type, so the AI can predict a forge result.
    /// Found by scanning the Mods folder (WheatStook's parent) for the mod's manifest.</summary>
    private List<object> ReadForgeEnchantments()
    {
        if (_forgeEnchantments != null) return _forgeEnchantments;
        var list = new List<object>();
        try
        {
            var dir = FindModDir("Dragoon23.ForgeEnchantment");
            string cfg = dir != null ? Path.Combine(dir, "config.json") : "";
            if (File.Exists(cfg))
            {
                foreach (var prop in JsonDocument.Parse(File.ReadAllText(cfg)).RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue; // skip cost/bonus numbers
                    var val = prop.Value.GetString();
                    if (string.IsNullOrEmpty(val) || val == "Default") continue;
                    int sep = prop.Name.IndexOf("__", StringComparison.Ordinal);
                    if (sep <= 0) continue;
                    list.Add(new { tool = prop.Name[..sep], enchant = val });
                }
            }
        }
        catch { /* config format may differ; ignore */ }
        _forgeEnchantments = list;
        return list;
    }

    /// <summary>Locate a mod's folder by scanning the Mods root for a matching manifest UniqueID.</summary>
    /// <summary>Return a loaded mod's install directory by scanning the Mods root
    /// (the parent of this mod's own directory) and matching its manifest's UniqueID.
    /// Each entry is handled in its own try/catch: one malformed or content-pack
    /// manifest elsewhere in the folder must not abort the whole scan (this is a real
    /// failure mode — a single bad JSON manifest used to make every config read null).</summary>
    private string? FindModDir(string uniqueId)
    {
        try
        {
            var root = Path.GetDirectoryName(_helper.DirectoryPath) ?? "";
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            foreach (var sub in Directory.GetDirectories(root))
            {
                try
                {
                    string m = Path.Combine(sub, "manifest.json");
                    if (!File.Exists(m)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(m));
                    if (doc.RootElement.TryGetProperty("UniqueID", out var id) && id.GetString() == uniqueId)
                        return sub;
                }
                catch { /* a bad manifest shouldn't break the whole scan */ }
            }
        }
        catch { /* Mods root unavailable; treat as not found */ }
        return null;
    }

    /// <summary>
    /// Resolve an item id to its display name, so modded crops and items (Cornucopia
    /// crops, custom machines, Content Patcher items) show a readable name instead of
    /// a bare id. The game data is loaded at runtime, so this resolves content-patched
    /// and other custom items too. Item ids are strings in Stardew 1.6.
    /// </summary>
    private static string ResolveItemName(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return "crop";
            var item = new StardewValley.Object(id, 1);
            return string.IsNullOrWhiteSpace(item.DisplayName) ? $"item#{id}" : item.DisplayName;
        }
        catch
        {
            return $"item#{id}";
        }
    }

    /// <summary>
    /// Enrich a tile entry with any matching data-driven compat rule. Only runs when
    /// rules are present; a rule matches by qualified item id and/or display name.
    /// </summary>
    private void AppendRuleTags(Dictionary<string, object?> t, StardewValley.Object o, ref bool harvestable)
    {
        if (CompatRules.Count == 0) return;
        string id = o.QualifiedItemId ?? "";
        string name = o.DisplayName ?? "";
        foreach (var rule in CompatRules)
        {
            if (!rule.Matches(id, name)) continue;
            if (!string.IsNullOrWhiteSpace(rule.Label) && !t.ContainsKey("compatLabel")) t["compatLabel"] = rule.Label;
            if (!string.IsNullOrWhiteSpace(rule.Category) && !t.ContainsKey("category")) t["category"] = rule.Category;
            if (rule.Harvestable) harvestable = true;
            if (rule.Processable) t["processable"] = true;
            if (rule.Collectible) t["collectible"] = true;
        }
    }

    /// <summary>
    /// Choose which tiles to read based on chunkSize and the read window. The current
    /// chunk is a chunkSize x chunkSize block centered on the player; the window decides
    /// how many neighbouring chunks are included. Only these tiles are read, and only
    /// notable ones are returned, so the AI isn't dragging the whole world along.
    /// </summary>
    private static List<Point> ComputeReadTiles(int chunkSize, string readWindow)
    {
        if (Game1.player is null) return new List<Point>();
        int cx = Game1.player.TilePoint.X, cy = Game1.player.TilePoint.Y;
        int half = chunkSize / 2;
        var tiles = new List<Point>();
        void AddChunk(int ox, int oy)
        {
            int baseX = cx - half + ox * chunkSize;
            int baseY = cy - half + oy * chunkSize;
            for (int dy = 0; dy < chunkSize; dy++)
                for (int dx = 0; dx < chunkSize; dx++)
                    tiles.Add(new Point(baseX + dx, baseY + dy));
        }

        switch ((readWindow ?? "").ToLower())
        {
            case "beehouse":
                // centre + the four cardinal chunks -> covers the bee-house flower zone.
                AddChunk(0, 0); AddChunk(1, 0); AddChunk(-1, 0); AddChunk(0, 1); AddChunk(0, -1);
                break;
            case "navigate":
                // centre in the direction of travel plus a leading chunk.
                AddChunk(0, 0); AddChunk(1, 0); AddChunk(2, 0); AddChunk(-1, 0); AddChunk(0, 1); AddChunk(0, -1);
                break;
            case "explore":
                // a 3x3 block of chunks.
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                        AddChunk(ox, oy);
                break;
            default: // "tool"
                AddChunk(0, 0);
                break;
        }
        return tiles;
    }

    /// <summary>Block-by-block ASCII map centred on the farmer (option 1, default).
    /// P player, C crop, M machine, T tree, N npc/tractor, o object, # blocked, . open.</summary>
    private static string BuildAsciiMap(int radius)
    {
        if (Game1.player is null) return "";
        var loc = Game1.player.currentLocation;
        int cx = Game1.player.TilePoint.X, cy = Game1.player.TilePoint.Y;
        int mapW = loc.Map.DisplayWidth / 64, mapH = loc.Map.DisplayHeight / 64;
        var sb = new StringBuilder();
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (tx < 0 || ty < 0 || tx >= mapW || ty >= mapH) { sb.Append(' '); continue; }
                sb.Append(TileSymbol(tx, ty, cx, cy, loc));
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static char TileSymbol(int tx, int ty, int cx, int cy, GameLocation loc)
    {
        if (tx == cx && ty == cy) return 'P';
        var tv = new Vector2(tx, ty);
        if (loc.objects.TryGetValue(tv, out var o))
            return (o.heldObject.Value != null || o.Name == "Crystalarium" || o.Name == "Cask") ? 'M' : 'o';
        if (loc.terrainFeatures.TryGetValue(tv, out var tf))
        {
            if (tf is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.crop != null) return 'C';
            if (tf is StardewValley.TerrainFeatures.Tree) return 'T';
        }
        if (loc.characters.Any(c => c.TilePoint.X == tx && c.TilePoint.Y == ty)) return 'N';
        return loc.isTilePassable(tv) ? '.' : '#';
    }

    /// <summary>Render the same grid to a BMP image (data-URI). Used when stateOutput=image
    /// (option 2, available but OFF by default). Each cell becomes a small coloured block.</summary>
    private static string RenderMapImage(int radius, int cell = 14)
    {
        string grid = BuildAsciiMap(radius);
        var lines = grid.Split('\n');
        int rows = lines.Length, cols = lines.Max(l => l.Length);
        int width = cols * cell, height = rows * cell;
        int rowSize = ((width * 3 + 3) / 4) * 4;
        int dataSize = rowSize * height;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(54 + dataSize); w.Write(0); w.Write(54);            // file header
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); // BITMAPINFOHEADER
        w.Write(0); w.Write(dataSize); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (int row = rows - 1; row >= 0; row--)
        {
            var line = row < lines.Length ? lines[row] : "";
            for (int x = 0; x < cols; x++)
            {
                var (r, g, b) = SymbolColor(x < line.Length ? line[x] : ' ');
                for (int i = 0; i < cell; i++) { w.Write(b); w.Write(g); w.Write(r); }
            }
            int pad = rowSize - cols * cell * 3;
            for (int i = 0; i < pad; i++) w.Write((byte)0);
        }
        return "data:image/bmp;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static (byte r, byte g, byte b) SymbolColor(char ch) => ch switch
    {
        'P' => (0x2e, 0x8b, 0x57),
        'C' => (0x7c, 0xfc, 0x00),
        'M' => (0xc0, 0x50, 0x4d),
        'T' => (0x22, 0x8b, 0x22),
        'N' => (0xdd, 0x8a, 0x89),
        'o' => (0x8a, 0x6a, 0x45),
        '#' => (0x42, 0x42, 0x42),
        _   => (0x9b, 0x9b, 0x9b),
    };

    private object HandleMachines()
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return new { ok = false, error = "World not ready" };
        try
        {
            var loc = Game1.player.currentLocation;
            var np = Game1.player.TilePoint;
            int mapW = loc.Map.DisplayWidth / 64, mapH = loc.Map.DisplayHeight / 64;
            var machines = new List<object>();
            var automation = ComputeAutomation(loc);
            for (int dy = -12; dy <= 12; dy++)
            {
                for (int dx = -12; dx <= 12; dx++)
                {
                    int tx = np.X + dx, ty = np.Y + dy;
                    if (tx < 0 || ty < 0 || tx >= mapW || ty >= mapH) continue;
                    var tv = new Vector2(tx, ty);
                    if (loc.objects.TryGetValue(tv, out var o))
                    {
                        var entry = new Dictionary<string, object?> { ["machine"] = o.Name, ["x"] = tx, ["y"] = ty };
                        if (o.Name == "Crystalarium") entry["isCrystalarium"] = true;
                        if (o is StardewValley.Objects.Chest) entry["isChest"] = true;
                        // Automate: a machine next to (or connector-linked to) a chest is automated.
                        if (automation.TryGetValue(tv, out var al))
                        {
                            entry["automated"] = true;
                            entry["connectedChests"] = al.Chests;
                            entry["viaConnector"] = al.ViaConnector;
                        }
                        // Enrich with what a machine is doing (esp. the Crystalarium), so the
                        // AI sees the actual gem being duplicated regardless of which gems a
                        // content-patched mod (e.g. Better Crystalarium) makes copyable.
                        try
                        {
                            string? heldId = o.heldObject.Value?.QualifiedItemId;
                            string? heldName = o.heldObject.Value?.Name;
                            if (heldId != null || heldName != null)
                            {
                                entry["heldObject"] = heldName;
                                entry["heldObjectId"] = heldId;
                            }
                            if (o.readyForHarvest.Value) entry["readyForHarvest"] = true;
                            if (o.MinutesUntilReady > 0) entry["minutesUntilReady"] = o.MinutesUntilReady;
                        }
                        catch { /* not every object is a machine; ignore */ }
                        machines.Add(entry);
                    }
                }
            }
            return new { ok = true, machines };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private object HandleMenu()
    {
        if (!Context.IsWorldReady) return new { ok = true, menu = (object?)null };
        var m = Game1.activeClickableMenu;
        string? text = null;
        object? shop = null;
        if (m is StardewValley.Menus.DialogueBox db) { try { text = db.getCurrentString(); } catch { } }
        else if (m is StardewValley.Menus.ShopMenu sm)
        {
            try { shop = ReadShop(sm); } catch { /* shop data may differ across versions */ }
        }
        return new { ok = true, menu = m == null ? null : new { type = m.GetType().Name, dialogue = text, shop } };
    }

    /// <summary>Read an open shop: what's for sale, prices, stock, and the shop's name.
    /// This is mod-agnostic, so CP shop mods (Marnie's Auto-Petters, Robin Sells Big Craftables)
    /// and UI mods like Shop Tabs are all covered automatically.</summary>
    private object ReadShop(StardewValley.Menus.ShopMenu shop)
    {
        var items = new List<object>();
        if (shop.forSale != null)
        {
            foreach (var item in shop.forSale)
            {
                int price = 0, count = 0;
                if (shop.itemPriceAndStock != null
                    && shop.itemPriceAndStock.TryGetValue(item, out var info))
                {
                    price = info.Price;
                    count = info.Stock;
                }
                items.Add(new { name = item.Name, price, stock = count });
            }
        }
        return new { items };
    }

    // ── actions ──
    private object HandleMove(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        int tx = GetReq<int>(p, "x"), ty = GetReq<int>(p, "y");
        // A farmer flagged in-bed must not be walked around: the old /sleep set the flag
        // without a real sleep, and move_to happily drove the "sleeping" farmhand across
        // the farm — which is how that bug was spotted. Refuse instead of pretending.
        if (Game1.player?.isInBed?.Value == true)
            return new { ok = false, error = "farmer is in bed — POST /awake first to stand up." };
        // FindPath is a bounded BFS on tile passability (no mutable state), so it's
        // safe to run synchronously to report the real step count. Only the field
        // writes stay on the game thread via Enqueue.
        var farmer = Game1.player;
        var path = farmer is null
            ? new List<Point>()
            : FindPath(farmer.currentLocation, farmer.TilePoint, new Point(tx, ty));
        int steps = path.Count;
        Enqueue(() =>
        {
            if (Game1.player is null) return;
            _path = steps > 0 ? new Queue<Point>(path) : new Queue<Point>(new[] { new Point(tx, ty) });
            _moveCooldown = 0;
        });
        return new { ok = true, x = tx, y = ty, steps };
    }

    private object HandleStop() { Enqueue(() => _path = null); return new { ok = true }; }

    private object HandleFace(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        int dir = GetReq<int>(ReadJson(ctx), "direction");
        Enqueue(() => { if (Game1.player != null) Game1.player.FacingDirection = dir; });
        return new { ok = true, direction = dir };
    }

    private object HandleInteract(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        bool? acted = null;
        Enqueue(() =>
        {
            var farmer = Game1.player;
            if (farmer is null) return;
            var facing = FacingTile(farmer);
            acted = farmer.currentLocation.checkAction(
                new xTile.Dimensions.Location((int)facing.X, (int)facing.Y), Game1.viewport, farmer);
        });
        return new { ok = true, actionTriggered = acted ?? false };
    }

    private object HandleTool(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string name = Get(p, "name", "current");
        // "current" uses whatever tool is equipped; any other name selects that tool.
        // Verify synchronously so the AI gets a real reason instead of a silent ok:false
        // when it asks for a tool it doesn't hold (this was the 'Hoe failed but why?' gap).
        var farmer = Game1.player;
        if (farmer is null) return new { ok = false, error = "No player" };
        if (name != "current")
        {
            var tool = farmer.Items.OfType<Tool>().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tool == null)
                return new { ok = false, error = $"未持有该工具 '{name}' (tool not in inventory). Current: {farmer.CurrentTool?.Name ?? "(empty)"}" };
        }
        // The actual tool use must run on the game thread (BeginUsingTool), so the reply
        // reports "accepted/queued" — it can't know the outcome yet.
        Enqueue(() =>
        {
            var f = Game1.player;
            if (f is null) return;
            if (name != "current")
            {
                var t = f.Items.OfType<Tool>().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (t != null) f.CurrentToolIndex = f.Items.IndexOf(t);
            }
            f.BeginUsingTool();
        });
        return new { ok = true, tool = name, queued = true };
    }

    private object HandleKey(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string key = Get(p, "key", "confirm").ToLower();
        int count = Get(p, "count", 1);
        Enqueue(() =>
        {
            for (int i = 0; i < Math.Max(1, count); i++)
            {
                switch (key)
                {
                    case "confirm": case "action":
                        Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState());
                        break;
                    case "escape": case "skip":
                        if (Game1.activeClickableMenu != null)
                            Game1.activeClickableMenu.exitThisMenu();
                        break;
                }
            }
        });
        return new { ok = true, key, count = Math.Max(1, count) };
    }

    private object HandleTractor(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) return new { ok = false, error = "World not ready" };
        var p = ReadJson(ctx);
        string op = Get(p, "op", "state").ToLower();
        bool riding = RidingTractor();

        if (op == "summon" && !riding)
        {
            // Tractor Mod's summon is host-coupled: on a farmhand it sends a request that
            // the host only accepts if the mod itself sent it (it checks FromModID). We
            // can't fake the sender, so be honest instead of pretending it worked.
            bool loaded = _helper.ModRegistry.IsLoaded("Pathoschild.TractorMod");
            return new { ok = false, op, ridingTractor = riding, error = loaded
                ? "summon is host-coupled in Tractor Mod; ask the host to summon, or use the mod's own summon key."
                : "Tractor Mod not installed." };
        }
        if (op == "dismiss" && riding)
        {
            try { Game1.player?.mount?.dismount(); }
            catch (Exception ex) { _monitor.Log($"Tractor dismount failed: {ex.Message}", LogLevel.Warn); }
        }
        return new { ok = true, op, ridingTractor = RidingTractor(), mountName = Game1.player?.mount?.Name };
    }

    private object HandleDrop(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        var farmer = Game1.player;
        string? item = farmer?.CurrentTool?.Name ?? farmer?.ActiveObject?.Name;
        bool isValuable = farmer?.CurrentTool != null ||
                          (farmer?.ActiveObject != null && farmer.ActiveObject.Category == StardewValley.Object.SeedsCategory);
        if (isValuable && !Get(p, "confirm", false))
            return new { ok = false, error = $"You're holding '{item}' (a tool/seed). Drop places it on the ground (recoverable) and removes it from your inventory — pass confirm:true to proceed." };
        Enqueue(() =>
        {
            var f = Game1.player;
            if (f is null) return;
            // Actually place a ground Debris at the farmer's feet (recoverable by walking
            // over it), then take one from the inventory — not just silently consume it.
            Item? held = f.CurrentTool != null ? f.CurrentTool : (Item?)f.ActiveObject;
            if (held is null) return;
            Game1.createItemDebris(held.getOne(), f.Position, 1);
            f.reduceActiveItemByOne();
        });
        return new { ok = true, item, warning = $"Dropped '{item}' on the ground at your feet — walk onto it to pick it back up." };
    }

    private object HandleFollow(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string target = Get(p, "target", "Follow");
        var loc = Game1.player?.currentLocation;
        // Validate the target actually exists before pathing, so the AI gets a real
        // "no such target" instead of an echo that silently goes nowhere.
        bool isNpc = loc != null && loc.characters.Any(n => n.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        bool isPlayer = Game1.otherFarmers.Values.Any(f => f.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (!isNpc && !isPlayer)
            return new { ok = false, error = $"找不到目标 '{target}' (no NPC or player with that name in the current location)." };
        Enqueue(() =>
        {
            var l = Game1.player?.currentLocation;
            var npc = l?.characters.FirstOrDefault(n => n.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (npc != null)
            {
                var path = FindPath(l!, Game1.player!.TilePoint, npc.TilePoint);
                _path = path.Count > 0 ? new Queue<Point>(path) : null;
            }
        });
        return new { ok = true, target, status = "tracking" };
    }

    private object HandleBed()
    {
        if (!Context.IsWorldReady) return new { ok = false, error = "World not ready" };
        var f = Game1.player;
        var loc = f?.currentLocation;
        var here = loc?.furniture?.OfType<StardewValley.Objects.BedFurniture>().FirstOrDefault();
        GameLocation? home = null;
        StardewValley.Objects.BedFurniture? bed = null;
        int bx = -1, by = -1;
        if (f is not null) (home, bed, bx, by) = FindHomeBed(f);
        return new
        {
            ok = true,
            inBed = f?.isInBed?.Value ?? false,
            homeLocation = f?.homeLocation?.Value,
            location = loc?.Name,
            bedInCurrentLocation = here != null,
            bedHere = here == null ? null : (object)new { x = (int)here.TileLocation.X, y = (int)here.TileLocation.Y },
            homeResolved = home?.Name,
            homeBed = bed == null ? null : (object)new { x = bx, y = by },
            note = "POST /sleep walks to the home bed and uses it like a player (that is what actually starts the co-op day handshake). POST /awake clears a stuck in-bed flag."
        };
    }

    private object HandleSleep(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        bool force = Get(p, "force", false);
        var f = Game1.player;
        if (f is null) return new { ok = false, error = "no player" };
        if (f.isInBed.Value && !force)
            return new { ok = false, error = "already flagged in-bed — POST /awake first (a half-sleep is what blocks the day)." };

        var (home, bed, bx, by) = FindHomeBed(f);
        if (home is null || bed is null)
            return new { ok = false, error = $"no bed found (homeLocation='{f.homeLocation?.Value}')" };
        var (sx, sy) = PickAdjacentStandingTile(home, bx, by);

        // Step 1 (this tick): get into the home location and stand next to the bed.
        // Game1.warpFarmer is a no-op here, so this uses the forced warp. Step 2 (a few
        // ticks later): use the bed — GameLocation.checkAction on the bed tile is exactly
        // what a player click does, and that is what starts the vanilla sleep + co-op
        // new-day handshake. Setting isInBed alone left the farmhand flagged in-bed while
        // standing in a field, and the night never passed — the bug this replaces.
        int face = by > sy ? 2 : by < sy ? 0 : bx > sx ? 1 : 3;
        _dayEndTicks = Math.Max(_dayEndTicks, 60 * 30);   // keep auto-confirm armed for the night
        Enqueue(() =>
        {
            var farmer = Game1.player;
            if (farmer is null) return;
            if (!ReferenceEquals(farmer.currentLocation, home))
                ForceWarp(farmer, home, sx, sy, face);
            else
            {
                farmer.Position = new Vector2(sx * 64 + 32, sy * 64 + 32);
                farmer.FacingDirection = face;
            }
        });
        EnqueueAfter(4, () =>
        {
            var farmer = Game1.player;
            if (farmer is null) return;
            try { home.checkAction(new xTile.Dimensions.Location(bx, by), Game1.viewport, farmer); }
            catch (Exception ex) { _monitor.Log($"bed checkAction failed: {ex.Message}", LogLevel.Warn); }
            if (force)
            {
                try
                {
                    farmer.isInBed.Value = true;
                    Game1.newDayAfterFade(() => { });
                }
                catch (Exception ex) { _monitor.Log($"forced new day failed: {ex.Message}", LogLevel.Warn); }
            }
        });
        return new
        {
            ok = true,
            home = home.Name,
            bed = new { x = bx, y = by },
            stand = new { x = sx, y = sy },
            forced = force,
            note = force
                ? "walking to the bed and using it, then forcing a new day (solo/host only — can desync co-op)."
                : "walking to the bed and using it; the host advances the day once every player is in bed."
        };
    }

    private object HandleAwake(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        Enqueue(() =>
        {
            var f = Game1.player;
            if (f is null) return;
            try
            {
                // Stepping off the bed is what actually clears the sleep. While the farmer
                // stands on a bed tile the game re-asserts isInBed, so clearing the flag
                // alone bounces straight back to true (verified live 3/3; warping off the
                // bed cleared it and restarted the clock instead).
                var loc = f.currentLocation;
                if (loc is not null)
                {
                    var (sx, sy) = PickAdjacentStandingTile(loc, f.TilePoint.X, f.TilePoint.Y);
                    f.Position = new Vector2(sx * 64 + 32, sy * 64 + 32);
                }
                f.isInBed.Value = false;
            }
            catch (Exception ex) { _monitor.Log($"awake failed: {ex.Message}", LogLevel.Warn); }
        });
        return new { ok = true, inBed = false, note = "stepped off the bed and cleared the in-bed flag." };
    }

    /// <summary>Home location + its bed tile. Falls back to the current location.</summary>
    private (GameLocation? home, StardewValley.Objects.BedFurniture? bed, int x, int y) FindHomeBed(Farmer f)
    {
        GameLocation? home = null;
        var name = f.homeLocation?.Value ?? "";
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { home = Game1.getLocationFromName(name); } catch { }
        }
        home ??= f.currentLocation;
        var bed = home?.furniture?.OfType<StardewValley.Objects.BedFurniture>().FirstOrDefault();
        if (bed is null) return (home, null, -1, -1);
        return (home, bed, (int)bed.TileLocation.X, (int)bed.TileLocation.Y);
    }

    /// <summary>First clear tile beside the bed (a player has to stand next to it).</summary>
    private static (int x, int y) PickAdjacentStandingTile(GameLocation loc, int bx, int by)
    {
        var candidates = new[] { (bx, by + 1), (bx, by - 1), (bx + 1, by), (bx - 1, by) };
        foreach (var (x, y) in candidates)
        {
            try { if (loc.isTilePassable(new Vector2(x, y))) return (x, y); }
            catch { }
        }
        return (bx, by + 1);
    }

    private object HandleTalk(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string target = Get(p, "target", "");
        var farmer = Game1.player;
        var loc = farmer?.currentLocation;
        if (farmer is null || loc is null) return new { ok = false, error = "No location" };
        StardewValley.NPC? npc;
        if (string.IsNullOrWhiteSpace(target))
            npc = loc.characters.OrderBy(n => Math.Abs(n.TilePoint.X - farmer.TilePoint.X) + Math.Abs(n.TilePoint.Y - farmer.TilePoint.Y)).FirstOrDefault();
        else
            npc = loc.characters.FirstOrDefault(n => n.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (npc is null)
            return new { ok = false, error = $"找不到 NPC '{target}' in {loc.Name}. 现有: {string.Join(", ", loc.characters.Select(n => n.Name))}" };
        string name = npc.Name;
        int nx = npc.TilePoint.X, ny = npc.TilePoint.Y;
        Enqueue(() =>
        {
            try { npc.checkAction(Game1.player, loc); }
            catch (Exception ex) { _monitor.Log($"talk checkAction failed: {ex.Message}", LogLevel.Warn); }
        });
        return new { ok = true, npc = name, x = nx, y = ny, queued = true, note = "NPC checkAction queued — they face you and say their line." };
    }

    private object HandleMemoryRead()
    {
        if (Memory is null) return new { ok = false, error = "memory not wired" };
        var entries = Memory.Snapshot();
        return new { ok = true, count = entries.Count, entries };
    }

    private object HandleMods(HttpListenerContext ctx)
    {
        if (Mods is null) return new { ok = false, error = "mod knowledge base not built yet (it is filled at game launch)" };
        string q = ctx.Request.QueryString["q"] ?? "";
        var results = Mods.Search(q);
        return new
        {
            ok = true,
            query = q,
            count = results.Count,
            mods = results.Take(60).Select(m => new
            {
                m.Name, m.UniqueID, m.Author, m.Version, m.IsContentPack,
                m.NexusId, m.NexusUrl, m.Links, m.UpdateKeys
            })
        };
    }

    private object HandleMemoryWrite(HttpListenerContext ctx)
    {
        if (Memory is null) return new { ok = false, error = "memory not wired" };
        var p = ReadJson(ctx);
        string op = Get(p, "op", "add").ToLower();
        string text = Get(p, "text", "");
        switch (op)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(text)) return new { ok = false, error = "text required" };
                Memory.Add(text);
                return new { ok = true, op, count = Memory.Count };
            case "journal":
                if (string.IsNullOrWhiteSpace(text)) return new { ok = false, error = "text required" };
                Memory.Journal(text);
                return new { ok = true, op, count = Memory.Count };
            case "del":
            case "remove":
                Memory.Remove(text);
                return new { ok = true, op, count = Memory.Count };
            case "clear":
                Memory.Clear();
                return new { ok = true, op, count = Memory.Count };
            case "reload":
                Memory.Load();
                return new { ok = true, op, count = Memory.Count };
            default:
                return new { ok = false, error = $"unsupported op '{op}' (add|journal|del|clear|reload)" };
        }
    }

    private object HandleReact(HttpListenerContext ctx)
    {
        if (Reactions is null) return new { ok = false, error = "reaction store not wired" };
        var p = ReadJson(ctx);
        string op = Get(p, "op", "list").ToLower();
        switch (op)
        {
            case "list":
                return new { ok = true, op, count = Reactions.Count, rules = Reactions.All() };
            case "set":
            {
                string item = Get(p, "item", "");
                if (string.IsNullOrWhiteSpace(item)) return new { ok = false, error = "item required" };
                Reactions.Set(item, Get(p, "emote", 4), Get(p, "text", ""));
                return new { ok = true, op, count = Reactions.Count };
            }
            case "del":
            case "remove":
            {
                bool removed = Reactions.Remove(Get(p, "item", ""));
                return new { ok = true, op, removed, count = Reactions.Count };
            }
            case "match":
            {
                string item = Get(p, "item", "");
                var rule = Reactions.Match(item);
                return new { ok = true, op, item, matched = rule != null, rule };
            }
            case "reload":
                Reactions.Load();
                return new { ok = true, op, count = Reactions.Count };
            default:
                return new { ok = false, error = $"unsupported op '{op}' (list|set|del|match|reload)" };
        }
    }

    private object HandleArea(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string op = Get(p, "op", "inspect").ToLower();
        int x1 = Get(p, "x1", 0), y1 = Get(p, "y1", 0), x2 = Get(p, "x2", 0), y2 = Get(p, "y2", 0);
        int ax = Math.Min(x1, x2), ay = Math.Min(y1, y2), bx = Math.Max(x1, x2), by = Math.Max(y1, y2);
        int w = bx - ax + 1, h = by - ay + 1;
        if (op == "inspect")
            return new { ok = true, op, area = new { x = ax, y = ay, w, h } };
        if (op != "water" && op != "harvest")
            return new { ok = false, error = $"unsupported op '{op}' (use inspect|water|harvest)" };
        // The mutation must run on the game thread; report "queued" rather than a count read
        // before the queued work ran (the old async-timing trap).
        Enqueue(() =>
        {
            var loc = Game1.player?.currentLocation;
            if (loc is null) return;
            for (int y = ay; y <= by; y++)
            for (int x = ax; x <= bx; x++)
            {
                if (!loc.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf)) continue;
                if (tf is not StardewValley.TerrainFeatures.HoeDirt dirt) continue;
                if (op == "water")
                {
                    if (dirt.state.Value != 1) dirt.state.Value = 1;
                }
                else if (dirt.crop != null)
                {
                    try { dirt.crop.harvest(x, y, dirt, null!, false); }
                    catch (Exception ex) { _monitor.Log($"harvest ({x},{y}) failed: {ex.Message}", LogLevel.Warn); }
                }
            }
        });
        return new { ok = true, op, area = new { x = ax, y = ay, w, h }, queued = true, note = $"{op} queued over {w}x{h} region (HoeDirt tiles only)." };
    }

    private object HandleEmote(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        int id = GetReq<int>(ReadJson(ctx), "id");
        Enqueue(() => { if (Game1.player != null) Game1.player.doEmote(id); });
        return new { ok = true, emoteId = id };
    }

    private object HandleWarp(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string loc = GetReq<string>(p, "location");
        int x = Get(p, "x", 10), y = Get(p, "y", 10);
        // "home" is a convenience alias: in co-op a cabin's real name is its UUID and
        // several cabins share the display name "Cabin", so the plain name is ambiguous.
        if (string.Equals(loc, "home", StringComparison.OrdinalIgnoreCase))
            loc = Game1.player?.homeLocation?.Value ?? loc;
        var target = Game1.getLocationFromName(loc);
        if (target is null) throw new InvalidOperationException($"unknown location '{loc}'");
        Enqueue(() => ForceWarp(Game1.player, target, x, y));
        return new { ok = true, location = target.Name, x, y, method = "forced" };
    }

    /// <summary>
    /// Move the farmhand between locations by hand.
    ///
    /// Game1.warpFarmer silently never completes for this farmhand: it logs
    /// "Warping to X" and then nothing — no fade, no position change, no error, with
    /// no path and no menu active (verified live with repeated sampling, and it fails
    /// for Town/Farm just as much as for the cabin). Direct Position writes DO work
    /// (the mod's own walking proves it), so set the location and position ourselves
    /// and drop any request that is stuck mid-fade.
    /// </summary>
    private void ForceWarp(Farmer? farmer, GameLocation? target, int tx, int ty, int facing = 2)
    {
        if (farmer is null || target is null) return;
        try
        {
            Game1.locationRequest = null;
            farmer.currentLocation = target;
            Game1.currentLocation = target;
            farmer.Position = new Vector2(tx * 64 + 32, ty * 64 + 32);
            farmer.FacingDirection = facing;
        }
        catch (Exception ex)
        {
            _monitor.Log($"forced warp failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private object HandleChat(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        string message = GetReq<string>(ReadJson(ctx), "message");
        Enqueue(() =>
        {
            // Broadcast through the vanilla player-chat path so the line lands in EVERY
            // player's chat box. ChatBox.addMessage only paints this farmhand's own chat
            // box, which is why the AI's messages were visible only in the SMAPI console.
            bool broadcast = false;
            try
            {
                if (Game1.chatBox is not null)
                {
                    Game1.chatBox.textBoxEnter(message);
                    broadcast = true;
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"chat broadcast failed ({ex.Message}); falling back to the local chat box.", LogLevel.Warn);
            }
            if (!broadcast)
            {
                try { Game1.chatBox?.addMessage(message, Color.White); } catch { }
            }
            ChatDisplay?.Invoke(message);   // this instance's own panel as well
        });
        return new { ok = true, message, note = "broadcast via the vanilla chat path (all players) + this instance's panel." };
    }

    private object HandleSelect(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady) throw new InvalidOperationException("World not ready");
        var p = ReadJson(ctx);
        string name = GetReq<string>(p, "name");
        // Look up the tool synchronously (a light read on the farmer's inventory) so we
        // can report a truthful ok/slot; the actual CurrentToolIndex write is deferred to
        // the game thread. (Previously `found` was read before the queued action ran, so
        // select always returned ok:false.)
        var farmer = Game1.player;
        int? slot = null;
        if (farmer != null)
        {
            for (int i = 0; i < farmer.Items.Count; i++)
            {
                if (farmer.Items[i] != null && farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                { slot = i; break; }
            }
        }
        bool ok = slot.HasValue;
        Enqueue(() =>
        {
            if (Game1.player is null || !slot.HasValue) return;
            Game1.player.CurrentToolIndex = slot.Value;
        });
        return new { ok, name, slot };
    }

    /// <summary>Buy an item from the currently open shop. Validates read-only, then queues the
    /// actual purchase (deduct money + add to inventory) on the game thread to stay safe.
    /// Works for any shop, so CP shop mods (Marnie's Auto-Petters, Robin Sells Big Craftables)
    /// and Shop Tabs are all covered.</summary>
    private object HandleBuy(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return new { ok = false, error = "World not ready" };
        if (Game1.activeClickableMenu is not StardewValley.Menus.ShopMenu shop)
            return new { ok = false, error = "No shop open yet; open a shop first." };

        var p = ReadJson(ctx);
        int index = GetReq<int>(p, "index");
        int count = Get<int>(p, "count", 1);
        if (count < 1) count = 1;

        if (shop.forSale == null || index < 0 || index >= shop.forSale.Count)
            return new { ok = false, error = $"Bad index {index} (shop has {shop.forSale?.Count ?? 0} items)" };
        var sale = shop.forSale[index];
        if (sale is not Item item)
            return new { ok = false, error = $"'{sale.Name}' isn't a standard item (maybe a building); buy it via the shop UI." };

        int price = 0, stock = 0;
        if (shop.itemPriceAndStock != null && shop.itemPriceAndStock.TryGetValue(sale, out var info))
        {
            price = info.Price;
            stock = info.Stock;
        }
        int total = price * count;
        if (stock >= 0 && count > stock)
            return new { ok = false, error = $"Not enough stock ({stock}) for {count}" };
        if (Game1.player.Money < total)
            return new { ok = false, error = $"Not enough money: need {total}g, have {Game1.player.Money}g" };

        string itemName = item.Name;
        Enqueue(() =>
        {
            try
            {
                int bought = 0;
                for (int i = 0; i < count; i++)
                {
                    if (Game1.player.Money < price) break;
                    if (Game1.player.addItemToInventory(item.getOne()) != null) break; // inventory full; stop
                    Game1.player.Money -= price;
                    bought++;
                }
                _monitor.Log($"Bought {bought}x {itemName} for {price * bought}g", LogLevel.Info);
            }
            catch (Exception ex) { _monitor.Log($"Buy failed: {ex.Message}", LogLevel.Error); }
        });

        return new { ok = true, queued = true, item = itemName, price, count, total, stock = stock >= 0 ? stock : (int?)null, money = Game1.player.Money };
    }

    // ── helpers ──
    private static Point FacingTile(Farmer farmer)
    {
        var offset = farmer.FacingDirection switch { 0 => new Vector2(0, -1), 1 => new Vector2(1, 0), 2 => new Vector2(0, 1), _ => new Vector2(-1, 0) };
        var t = farmer.TilePoint;
        return new Point(t.X + (int)offset.X, t.Y + (int)offset.Y);
    }

    private static List<Point> FindPath(GameLocation loc, Point start, Point goal)
    {
        // Simple BFS on passable tiles (bounded).
        var seen = new HashSet<Point> { start };
        var from = new Dictionary<Point, Point>();
        var q = new Queue<Point>();
        q.Enqueue(start);
        var dirs = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };
        int mapW = loc.Map.DisplayWidth / 64, mapH = loc.Map.DisplayHeight / 64;

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur == goal) break;
            foreach (var d in dirs)
            {
                var nxt = new Point(cur.X + d.X, cur.Y + d.Y);
                if (nxt.X < 0 || nxt.Y < 0 || nxt.X >= mapW || nxt.Y >= mapH) continue;
                if (seen.Contains(nxt)) continue;
                bool passable = loc.isTilePassable(new Vector2(nxt.X, nxt.Y));
                if (!passable) continue;
                seen.Add(nxt);
                from[nxt] = cur;
                q.Enqueue(nxt);
            }
        }

        if (!from.ContainsKey(goal)) return new List<Point>();
        var path = new List<Point>();
        var c = goal;
        while (c != start) { path.Add(c); c = from[c]; }
        path.Reverse();
        return path;
    }
}

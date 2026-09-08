using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace WheatStook;

/// <summary>
/// In-game chat panel: toggled by the chat hotkey, shows recent messages, and
/// captures a text line. On submit, it raises <see cref="OnSubmit"/> so the mod
/// can forward the message to the Operit / MCP channels.
///
/// Input goes through a real <see cref="TextBox"/> registered as the game's keyboard
/// subscriber, so the OS IME composes Chinese/Japanese/Korean straight into it — the
/// earlier SButton capture could only ever see ASCII letters.
/// </summary>
public class ChatHud
{
    private readonly IMonitor _monitor;
    private readonly object _lock = new();
    private readonly List<string> _messages = new();
    private TextBox? _input;
    private IKeyboardSubscriber? _previousSubscriber;
    private bool _open;

    private const int BoxW = 560;
    private const int BoxH = 240;

    public ChatHud(IMonitor monitor) => _monitor = monitor;

    public bool IsOpen => _open;
    public event Action<string>? OnSubmit;

    public void Toggle()
    {
        if (_open) Close();
        else Open();
    }

    public void Open()
    {
        if (_open) return;
        _open = true;
        try
        {
            EnsureInput();
            var dispatcher = Game1.keyboardDispatcher;
            if (dispatcher is not null && _input is not null)
            {
                _previousSubscriber = dispatcher.Subscriber;
                dispatcher.Subscriber = _input;
                _input.SelectMe();
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"chat input focus failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        try
        {
            var dispatcher = Game1.keyboardDispatcher;
            if (dispatcher is not null && _input is not null && ReferenceEquals(dispatcher.Subscriber, _input))
                dispatcher.Subscriber = _previousSubscriber;
        }
        catch (Exception ex)
        {
            _monitor.Log($"chat input release failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Lazily build the IME-capable text box (needs the game content ready).</summary>
    private void EnsureInput()
    {
        if (_input is not null) return;
        var boxTexture = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
        _input = new TextBox(boxTexture, Game1.mouseCursors, Game1.smallFont, Color.Black)
        {
            Width = BoxW - 16,
            Height = 44,
            textLimit = 300,
            Selected = true,
        };
        _input.OnEnterPressed += sender =>
        {
            var text = (sender.Text ?? string.Empty).Trim();
            sender.Text = string.Empty;
            if (text.Length > 0) Submit(text);
        };
    }

    /// <summary>Per-tick upkeep: keep the input box glued to the panel and blink its caret.</summary>
    public void Update()
    {
        if (_input is null) return;
        try
        {
            var (x, y) = PanelOrigin();
            _input.X = x + 8;
            _input.Y = y + BoxH - 66;
            _input.Width = BoxW - 16;
            _input.Update();
        }
        catch (Exception ex)
        {
            _monitor.Log($"chat input update failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public void AddMessage(string text)
    {
        lock (_lock) _messages.Add(text);
    }

    private void Submit(string text)
    {
        AddMessage("我: " + text);
        OnSubmit?.Invoke(text);
    }

    private static (int x, int y) PanelOrigin() => (20, Game1.viewport.Height - BoxH - 20);

    public void Draw()
    {
        if (!_open || Game1.spriteBatch is null) return;
        var (x, y) = PanelOrigin();

        Game1.drawDialogueBox(x - 16, y - 16, BoxW + 32, BoxH + 32, false, true);

        lock (_lock)
        {
            int line = 0;
            foreach (var m in _messages.TakeLast(5))
            {
                // dark ink on the tan dialogue box — the old Color.White was invisible
                Game1.spriteBatch.DrawString(Game1.dialogueFont, m, new Vector2(x + 12, y + 8 + line * 30), new Color(70, 40, 20));
                line++;
            }
        }

        try { _input?.Draw(Game1.spriteBatch, false); }
        catch (Exception ex)
        {
            _monitor.Log($"chat input draw failed: {ex.Message}", LogLevel.Warn);
        }
    }
}

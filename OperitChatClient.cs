using System.Text;
using System.Text.Json;
using StardewModdingAPI;

namespace WheatStook;

/// <summary>
/// Talks to the Operit web backend so an in-game message can land in Operit's
/// native chat, and optionally read back the AI's full reply.
///
/// Endpoint (verified live):  POST {operitWebUrl}/api/web/chats/{chatId}/messages/stream
/// body: {"message":"...","attachment_ids":[],"return_tool_status":true}
/// SSE:  event:start -> event:user_message -> event:assistant_delta... -> event:assistant_done
/// </summary>
public class OperitChatClient
{
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;
    private readonly HttpClient _http;

    public OperitChatClient(ModConfig config, IMonitor monitor)
    {
        _config = config;
        _monitor = monitor;

        // Never cut a live reply stream. HttpClient.Timeout covers reading the SSE body
        // too, so the old 60s cap killed the read-back while the AI was still writing
        // ("Timed out while waiting for response stream" came from our own disconnect).
        // Fail fast on connect instead, and only cap the reply if the user asks for it.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        _http = new HttpClient(handler);
        int seconds = config.operitReplyTimeoutSeconds;
        _http.Timeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
    }

    /// <summary>Whether the direct-to-Operit chat channel is turned on and configured.</summary>
    public bool IsEnabled =>
        _config.forwardToOperitChat &&
        !string.IsNullOrWhiteSpace(_config.operitWebUrl) &&
        !string.IsNullOrWhiteSpace(_config.operitWebChatId) &&
        !string.IsNullOrWhiteSpace(_config.operitWebToken);

    /// <summary>
    /// Forward an in-game <paramref name="message"/> to Operit's native chat.
    /// If <c>forwardReadOperitReply</c> is on, returns the assistant's full reply text;
    /// otherwise returns null (fire-and-forget delivery).
    /// </summary>
    public async Task<string?> SendAndReadBackAsync(string message, string sender = "宿主")
    {
        if (!IsEnabled)
        {
            _monitor.Log("Operit native chat disabled or not configured; skipping forward.", LogLevel.Debug);
            return null;
        }

        // Annotate so operit knows it's from inside Stardew, not ordinary input.
        var annotated = (_config.operitForwardFormat ?? string.Empty)
            .Replace("{sender}", sender)
            .Replace("{message}", message);

        var body = JsonSerializer.Serialize(new
        {
            message = annotated,
            attachment_ids = Array.Empty<string>(),
            return_tool_status = true,
        });

        var url = $"{_config.operitWebUrl.TrimEnd('/')}/api/web/chats/{Uri.EscapeDataString(_config.operitWebChatId)}/messages/stream";

        // Baseline: newest assistant message before we send, so the history fallback can
        // tell the new reply apart from an old one.
        string? baselineId = null;
        if (_config.forwardReadOperitReply && _config.operitHistoryFallback)
            baselineId = await LastAssistantAsync().ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_config.operitWebToken}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Operit forward request failed: {ex.Message}", LogLevel.Warn);
            return null;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _monitor.Log($"Operit forward failed: HTTP {(int)resp.StatusCode}", LogLevel.Warn);
                return null;
            }

            // Fire-and-forget mode: message is delivered, stop here (saves tokens).
            if (!_config.forwardReadOperitReply)
                return null;

            var reply = await ReadReplyAsync(resp).ConfigureAwait(false);
            if (reply is null && _config.operitHistoryFallback)
            {
                _monitor.Log("Operit stream ended without a reply; polling the chat history instead.", LogLevel.Info);
                reply = await PollHistoryForReplyAsync(baselineId).ConfigureAwait(false);
            }
            return reply;
        }
    }

    /// <summary>Id of the newest assistant message, used as a baseline before sending.</summary>
    private async Task<string?> LastAssistantAsync()
    {
        var (id, _) = await FetchLastAssistantAsync().ConfigureAwait(false);
        return id;
    }

    /// <summary>GET the chat history and return the newest assistant message (id, text).</summary>
    private async Task<(string? Id, string? Text)> FetchLastAssistantAsync()
    {
        var url = $"{_config.operitWebUrl.TrimEnd('/')}/api/web/chats/{Uri.EscapeDataString(_config.operitWebChatId)}/messages";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_config.operitWebToken}");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return (null, null);

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? id = null, text = null;
        foreach (var m in messages.EnumerateArray())
        {
            if (!m.TryGetProperty("sender", out var sender) || sender.GetString() != "assistant")
                continue;
            if (m.TryGetProperty("id", out var i)) id = i.GetString();
            if (m.TryGetProperty("content_raw", out var cr)) text = cr.GetString();
        }
        return (id, text);
    }

    /// <summary>
    /// Wait for a new assistant message to appear in the chat history. This is the safety
    /// net for a stream that died early: the message was delivered and Operit did answer,
    /// the stream just closed before assistant_done (seen live with a cold agent).
    /// </summary>
    private async Task<string?> PollHistoryForReplyAsync(string? baselineId)
    {
        int limit = _config.operitReplyTimeoutSeconds > 0 ? _config.operitReplyTimeoutSeconds : 120;
        var deadline = DateTime.UtcNow.AddSeconds(limit);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000).ConfigureAwait(false);
            try
            {
                var (id, text) = await FetchLastAssistantAsync().ConfigureAwait(false);
                if (id is not null && id != baselineId && !string.IsNullOrWhiteSpace(text))
                {
                    _monitor.Log("Recovered the Operit reply from the chat history.", LogLevel.Info);
                    return StripThink(text);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Operit history poll failed: {ex.Message}", LogLevel.Debug);
            }
        }
        return null;
    }

    /// <summary>Drop the model's &lt;think&gt; block and any tool markup, keeping the spoken text.</summary>
    private static string StripThink(string raw)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(raw, "<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<tool_[^>]*>.*?</tool_[^>]*>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<tool_result[^>]*>.*?</tool_result[^>]*>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return text.Trim();
    }

    /// <summary>Each the SSE stream and pull the assistant's reply on the terminal event.</summary>
    private async Task<string?> ReadReplyAsync(HttpResponseMessage resp)
    {
        using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventType = null;

        while (!reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
                break;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Substring(6).Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            string data = line.Substring(5).Trim();
            if (eventType == "error")
            {
                // Operit reports its own failures on the stream (e.g. a cold agent:
                // "Timed out while waiting for response stream"). Ignoring it made the
                // forward look silent; say so instead.
                _monitor.Log($"Operit reply stream error: {data}", LogLevel.Warn);
                return null;
            }
            if (eventType == "assistant_done")
            {
                try
                {
                    return ExtractReplyText(JsonDocument.Parse(data));
                }
                catch
                {
                    _monitor.Log("Operit reply event was not valid JSON; returning null.", LogLevel.Debug);
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>From the assistant_done payload, return just the plain text (not the HTML status card).</summary>
    private static string? ExtractReplyText(JsonDocument doc)
    {
        try
        {
            if (!doc.RootElement.TryGetProperty("message", out var msg))
                return null;
            if (!msg.TryGetProperty("content_blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                return null;

            var sb = new StringBuilder();
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.TryGetProperty("kind", out var kind) && kind.GetString() == "text"
                    && block.TryGetProperty("content", out var content))
                {
                    sb.Append(content.GetString());
                }
            }
            var text = sb.ToString().Trim();
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null; // ignore; logging handled by caller if needed
        }
    }
}

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
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
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

            return await ReadReplyAsync(resp).ConfigureAwait(false);
        }
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

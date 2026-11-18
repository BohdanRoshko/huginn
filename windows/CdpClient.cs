using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Munin;

/// <summary>
/// A minimal Chrome DevTools Protocol client: one page target, one call at a time, Runtime.evaluate only.
/// Domains are never enabled, so the socket carries no events — each reply is the answer to the last call.
/// </summary>
internal sealed class CdpClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _id;

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<CdpClient> ConnectAsync(string webSocketUrl, CancellationToken ct)
    {
        var c = new CdpClient();
        c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await c._ws.ConnectAsync(new Uri(webSocketUrl), ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Evaluates <paramref name="expression"/> in the page and returns its value (returnByValue), or throws.</summary>
    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        int id = ++_id;
        var msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true, awaitPromise = false },
        });
        await _ws.SendAsync(msg, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        while (true)
        {
            using var doc = JsonDocument.Parse(await ReceiveAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id) continue; // a stray event
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"CDP error: {err}");
            var result = root.GetProperty("result");
            if (result.TryGetProperty("exceptionDetails", out var ex))
                throw new InvalidOperationException($"page exception: {Trunc(ex.ToString())}");
            return result.GetProperty("result").TryGetProperty("value", out var v) ? v.Clone() : default;
        }
    }

    private async Task<string> ReceiveAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("DevTools socket closed");
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose()
    {
        try { _ws.Abort(); } catch { /* already gone */ }
        _ws.Dispose();
    }
}

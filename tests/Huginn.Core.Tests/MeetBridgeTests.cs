using System.Text;
using Huginn;
using Xunit;

namespace Huginn.Core.Tests;

public class MeetBridgeTests
{
    private static MeetBridge.Request Req(string body, string? huginn = "1", string? origin = "chrome-extension://abc", string path = "/meet/events")
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (huginn != null) h["x-huginn"] = huginn;
        if (origin != null) h["origin"] = origin;
        return new MeetBridge.Request("POST", path, h, body);
    }

    private const string Body = """{"meeting":"abc-defg-hij","events":[{"t":1000,"kind":"react","who":"Ana","text":"👍"},{"t":2000,"kind":"chat","who":"Ana","text":"hi"},{"t":3000,"kind":"nonsense","who":"x","text":""}]}""";

    [Fact]
    public void A_batch_from_the_extension_is_taken()
    {
        var bridge = new MeetBridge(0);
        List<KumoEvent>? got = null;
        bridge.Events += (_, e) => got = e.ToList();
        Assert.Equal(204, bridge.Handle(Req(Body)));
        Assert.NotNull(got);
        Assert.Equal(new[] { "react", "chat" }, got!.Select(e => e.Kind));
        Assert.Equal("👍", got[0].Text);
    }

    [Fact]
    public void A_web_page_cannot_write_events()
    {
        var bridge = new MeetBridge(0);
        int calls = 0;
        bridge.Events += (_, _) => calls++;
        Assert.Equal(403, bridge.Handle(Req(Body, huginn: null)));
        Assert.Equal(403, bridge.Handle(Req(Body, origin: "https://evil.example")));
        Assert.Equal(404, bridge.Handle(Req(Body, path: "/other")));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_request_is_read_off_the_socket()
    {
        var body = Encoding.UTF8.GetBytes(Body);
        var raw = Encoding.ASCII.GetBytes($"POST /meet/events HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Huginn: 1\r\nContent-Length: {body.Length}\r\n\r\n")
            .Concat(body).ToArray();
        var req = await MeetBridge.ReadRequestAsync(new MemoryStream(raw), CancellationToken.None);
        Assert.NotNull(req);
        Assert.Equal("/meet/events", req!.Value.Path);
        Assert.Equal("1", req.Value.Headers["x-huginn"]);
        Assert.Equal(Body, req.Value.Body);
    }
}

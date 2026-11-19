using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Huginn;

/// <summary>What Odin made of a posted meeting transcript.</summary>
internal sealed record OdinMeetingResult(string Classification, string Title, string Slug, int TaskCount, string? SecondaryType);

/// <summary>
/// Posts a finished meeting transcript to Odin's meeting agent (<c>POST /api/meetings/process</c>), which
/// classifies it and files a meeting note + tasks (+ optional client-log/worklog). Best-effort and fully
/// decoupled: if Odin is down this throws and Huginn keeps the transcript file as the fallback.
/// </summary>
internal static class OdinClient
{
    // Analysis + ingest can take a while on a long transcript (local Claude pass); give it room.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <param name="notes">The lines typed in the panel while it recorded (<see cref="MeetingNotes"/>), or null.
    /// Odin treats every line as a must-include and expands it from the transcript around its stamp.</param>
    public static async Task<OdinMeetingResult> ProcessMeetingAsync(string transcript, string? date, string? notes, CancellationToken ct = default)
    {
        using var resp = await Http.PostAsJsonAsync(
            $"{Config.OdinBaseUrl}/api/meetings/process", new { transcript, date, notes }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Odin {(int)resp.StatusCode}: {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new OdinMeetingResult(
            Str(r, "classification"),
            Str(r, "title"),
            Str(r, "slug"),
            r.TryGetProperty("taskCount", out var tc) && tc.TryGetInt32(out var n) ? n : 0,
            r.TryGetProperty("secondaryType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null);
    }

    /// <summary>A filed note's whole markdown file (frontmatter and body), for the meeting window.</summary>
    public static async Task<string> NoteContentAsync(string slug, CancellationToken ct = default)
    {
        var body = await Http.GetStringAsync($"{Config.OdinBaseUrl}/api/notes/{Uri.EscapeDataString(slug)}/content", ct);
        using var doc = JsonDocument.Parse(body);
        return Str(doc.RootElement, "content");
    }

    /// <summary>Stores one screen snapshot next to a filed meeting's transcript (<c>PUT /api/notes/{slug}/shots/{name}</c>).
    /// One request per image, so a long meeting never makes one huge body.</summary>
    public static async Task UploadShotAsync(string slug, string name, byte[] jpeg, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        using var resp = await Http.PutAsync($"{Config.OdinBaseUrl}/api/notes/{Uri.EscapeDataString(slug)}/shots/{Uri.EscapeDataString(name)}", content, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Odin {(int)resp.StatusCode}: {Trunc(await resp.Content.ReadAsStringAsync(ct))}");
    }

    /// <summary>The slug of the meeting note Odin filed under this title, for meetings filed before Huginn kept the
    /// slug: Odin's note search, newest first, and the row whose title matches exactly. Null when none does.</summary>
    public static async Task<string?> FindMeetingSlugAsync(string title, CancellationToken ct = default)
    {
        var url = $"{Config.OdinBaseUrl}/api/notes?type=meeting&limit=20&q={Uri.EscapeDataString(title)}";
        var body = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        foreach (var row in doc.RootElement.EnumerateArray())
            if (string.Equals(Str(row, "title").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)) return Str(row, "slug");
        return null;
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Trunc(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

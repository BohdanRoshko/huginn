namespace Munin;

internal static class ModelDownloader
{
    /// <summary>Downloads a model from <paramref name="url"/> on first run; no-op once it exists.</summary>
    public static async Task EnsureAsync(string destPath, string url, Action<double>? progress)
    {
        if (File.Exists(destPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        string tmp = destPath + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 20];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total is > 0) progress?.Invoke((double)read / total.Value);
            }
        }
        File.Move(tmp, destPath, overwrite: true);
    }
}

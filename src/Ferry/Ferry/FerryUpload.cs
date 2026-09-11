using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Ferry;

// HttpContent that streams a file while reporting upload progress.
// Server side is the raw-body POST /api/upload handler in server.js.
public sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly IProgress<double>? _progress;

    public ProgressStreamContent(Stream source, IProgress<double>? progress)
    {
        _source = source;
        _progress = progress;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = _source.CanSeek ? _source.Length : -1;
        long sent = 0;
        int read;
        while ((read = await _source.ReadAsync(buffer, ct)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            sent += read;
            if (total > 0) _progress?.Report((double)sent / total * 100);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_source.CanSeek)
        {
            length = _source.Length;
            return true;
        }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}

public sealed partial class FerryClient
{
    public async Task UploadFileAsync(
        string path, string senderId, string senderName,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        var url = "/api/upload?name=" + Uri.EscapeDataString(name)
            + "&senderId=" + Uri.EscapeDataString(senderId)
            + "&senderName=" + Uri.EscapeDataString(senderName);
        using var fs = File.OpenRead(path);
        using var content = new ProgressStreamContent(fs, progress);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await _http.PostAsync(url, content, ct);
        res.EnsureSuccessStatusCode();
    }
}

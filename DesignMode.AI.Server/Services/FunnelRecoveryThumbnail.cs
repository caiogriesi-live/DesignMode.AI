using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public static class FunnelRecoveryThumbnail
{
    public static async Task<string?> PrepareAsync(string? selectedImageUrl, ILogger logger,
        Func<string, Task<string>>? compose = null)
    {
        var source = FunnelRecoverySelectedImage.PublicUrl(selectedImageUrl);
        if (source is null) return null;
        try
        {
            var thumbnail = FunnelRecoverySelectedImage.PublicUrl(await (compose ?? ComposeAsync)(source));
            if (thumbnail is not null)
            {
                logger.LogInformation("[RECOVERY EMAIL] play thumbnail resolved=true");
                return thumbnail;
            }
        }
        catch { /* Optional artwork must not affect delivery or expose URLs in logs. */ }
        logger.LogInformation("[RECOVERY EMAIL] play thumbnail unavailable; selected image retained");
        return source;
    }

    private static async Task<string> ComposeAsync(string source)
    {
        var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrWhiteSpace(storage)) throw new InvalidOperationException();
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG_PATH")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException();
        // Reuse the video pipeline's storage and read-only SAS convention. No new container or permissions.
        var container = new BlobServiceClient(storage).GetBlobContainerClient(
            Environment.GetEnvironmentVariable("FINAL_VIDEO_CONTAINER") ?? "final-videos");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("recovery-play-v1:" + source))).ToLowerInvariant();
        var blob = container.GetBlobClient($"recovery-thumbnails/{key}.png");
        if (FunnelRecoverySelectedImage.PublicUrl(blob.Uri.AbsoluteUri) is null) throw new InvalidOperationException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (!blob.CanGenerateSasUri && (await container.GetAccessPolicyAsync(cancellationToken: timeout.Token)).Value.BlobPublicAccess
            == PublicAccessType.None) throw new InvalidOperationException();
        if (!(await blob.ExistsAsync(timeout.Token)).Value)
        {
            var directory = Path.Combine(Path.GetTempPath(), "recovery-thumbnail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await DownloadAsync(source, Path.Combine(directory, "source"), timeout.Token);
                await RenderAsync(ffmpeg, directory, timeout.Token);
                await using var output = File.OpenRead(Path.Combine(directory, "thumbnail.png"));
                await blob.UploadAsync(output, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "image/png", CacheControl = "public,max-age=2592000" }
                }, timeout.Token);
            }
            finally { try { Directory.Delete(directory, recursive: true); } catch { } }
        }
        return blob.CanGenerateSasUri
            ? blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddDays(30)).AbsoluteUri
            : blob.Uri.AbsoluteUri;
    }

    private static async Task DownloadAsync(string source, string destination, CancellationToken token)
    {
        // Pin the socket to a public DNS result and disallow redirects/proxies; never hand remote URLs to FFmpeg.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ConnectCallback = async (context, cancellation) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
            var address = addresses.FirstOrDefault(IsPublicAddress) ?? throw new HttpRequestException();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        const int maximumBytes = 12 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var file = File.Create(destination);
        var buffer = new byte[81920];
        var total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException();
            await file.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false; // Public IPv4 is sufficient for the image providers; fail closed otherwise.
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || b[1] == 0)) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
            !(b[0] == 198 && b[1] is 18 or 19);
    }

    public static byte[] PlayBadge()
    {
        const int size = 128;
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes($"P7\nWIDTH {size}\nHEIGHT {size}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n"));
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var radius = Math.Sqrt(Math.Pow(x - 63.5, 2) + Math.Pow(y - 63.5, 2));
            var white = radius >= 59 && radius <= 62 || x >= 49 && x <= 86 && Math.Abs(y - 64) <= (86 - x) * .65;
            byte color = white ? (byte)255 : (byte)17;
            output.Write([color, color, color, radius <= 62 ? white ? (byte)255 : (byte)145 : (byte)0]);
        }
        return output.ToArray();
    }

    public static async Task RenderAsync(string ffmpeg, string directory, CancellationToken token)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, "play.pam"), PlayBadge(), token);
        var info = new ProcessStartInfo(ffmpeg) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        // Raster formats only; no playlists, SVG external resources, or network protocols.
        string[] args = ["-y", "-protocol_whitelist", "file,pipe", "-format_whitelist", "image2,jpeg_pipe,png_pipe,webp_pipe",
            "-i", "source", "-i", "play.pam", "-filter_complex",
            "[0:v]scale=w='min(1168,iw)':h=-1[image];[image][1:v]overlay=(W-w)/2:(H-h)/2:format=auto",
            "-frames:v", "1", "-threads", "1", "thumbnail.png"];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException();
        var error = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(error, output); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Recovery thumbnail rendering failed.");
    }
}

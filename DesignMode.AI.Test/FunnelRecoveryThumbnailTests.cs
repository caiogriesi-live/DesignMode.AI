using System.Net;
using System.Text.RegularExpressions;
using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryThumbnailTests
{
    private const string Source = "https://cdn.designmode.ai/selected.png";

    [TestMethod]
    public async Task ThumbnailAndPreviewButton_UseExactlyTheSameDestination()
    {
        var thumbnail = await FunnelRecoveryThumbnail.PrepareAsync(Source, NullLogger.Instance,
            source => Task.FromResult(source.Replace("selected", "play")));
        var email = FunnelRecoveryEmail.Create("https://designmode.ai", "/init/mainconcept", selectedImageUrl: thumbnail);
        var imageAnchor = Regex.Match(email.Html, "<a href=\"([^\"]+)\"[^>]*><img src=\"([^\"]+)\"");
        Assert.IsTrue(imageAnchor.Success);
        Assert.AreEqual(email.ResumeUrl, WebUtility.HtmlDecode(imageAnchor.Groups[1].Value));
        Assert.AreEqual("https://cdn.designmode.ai/play.png", imageAnchor.Groups[2].Value);
        Assert.IsTrue(Regex.Matches(email.Html, Regex.Escape("href=\"" + email.ResumeUrl + "\"")).Count >= 2);
    }

    [TestMethod]
    public async Task CompositionFailure_KeepsClickableSelectedImage()
    {
        var image = await FunnelRecoveryThumbnail.PrepareAsync(Source, NullLogger.Instance, _ => throw new IOException());
        Assert.AreEqual(Source, image);
        Assert.AreEqual(Source, await FunnelRecoveryThumbnail.PrepareAsync(Source, NullLogger.Instance,
            _ => Task.FromResult("http://localhost/thumbnail.png")));
        Assert.IsNull(await FunnelRecoveryThumbnail.PrepareAsync(null, NullLogger.Instance,
            _ => throw new AssertFailedException("Missing image must not invoke the compositor.")));
    }

    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.0.0.1")]
    [DataRow("172.16.0.1")]
    [DataRow("192.168.0.1")]
    [DataRow("169.254.169.254")]
    [DataRow("100.64.0.1")]
    [DataRow("::1")]
    [DataRow("::ffff:127.0.0.1")]
    public void Download_RejectsPrivateAddresses(string address) =>
        Assert.IsFalse(FunnelRecoveryThumbnail.IsPublicAddress(IPAddress.Parse(address)));

    [DataTestMethod]
    [DataRow(584, 328)]
    [DataRow(328, 584)]
    public async Task Ffmpeg_BakesWhitePlayTriangleAndRingIntoSelectedImage(int width, int height)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DesignMode.AI.sln"))) root = root.Parent;
        var ffmpeg = Path.Combine(root!.FullName, "DesignMode.AI.Server", "tools", "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) Assert.Inconclusive("Bundled FFmpeg is required for raster integration coverage.");
        var directory = Path.Combine(Path.GetTempPath(), "recovery-thumbnail-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await CommercialOverlayCompositor.RunAsync(ffmpeg,
                ["-y", "-f", "lavfi", "-i", $"color=c=blue:s={width}x{height}", "-frames:v", "1", "fixture.png"], directory);
            File.Move(Path.Combine(directory, "fixture.png"), Path.Combine(directory, "source"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await FunnelRecoveryThumbnail.RenderAsync(ffmpeg, directory, timeout.Token);
            await CommercialOverlayCompositor.RunAsync(ffmpeg,
                ["-y", "-i", "thumbnail.png", "-f", "rawvideo", "-pix_fmt", "rgb24", "pixels.rgb"], directory);
            var pixels = await File.ReadAllBytesAsync(Path.Combine(directory, "pixels.rgb"));
            Assert.AreEqual(width * height * 3, pixels.Length, "Aspect ratio and dimensions must be preserved.");
            var center = ((height / 2) * width + width / 2) * 3;
            Assert.IsTrue(pixels[center] > 235 && pixels[center + 1] > 235 && pixels[center + 2] > 235, "Center contains the white play triangle.");
            var ring = ((height / 2) * width + width / 2 + 60) * 3;
            Assert.IsTrue(pixels[ring] > 235 && pixels[ring + 1] > 235, "Play button has a white ring.");
            Assert.IsTrue(pixels[0] < 10 && pixels[1] < 10 && pixels[2] > 240, "Selected artwork remains outside the badge.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

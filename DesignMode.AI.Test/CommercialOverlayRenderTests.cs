using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

namespace DesignMode.AI.Test;

[TestClass]
public class CommercialOverlayRenderTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [DataTestMethod]
    [DataRow(864, 480)] [DataRow(480, 864)] [DataRow(720, 720)]
    public async Task Render_PreservesScenesAudioDurationAndAddsAnimatedText(int width, int height)
    {
        var ffmpeg = Path.Combine(Root(), "DesignMode.AI.Server", "tools", "ffmpeg", "ffmpeg.exe");
        Assert.IsTrue(File.Exists(ffmpeg), "Bundled FFmpeg required for integration test.");
        var directory = Path.Combine(Path.GetTempPath(), "commercial-overlay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "base.mp4");
            await CommercialOverlayCompositor.RunAsync(ffmpeg,
                ["-y", "-f", "lavfi", "-i", $"color=red:s={width}x{height}:d=3:r=25", "-f", "lavfi", "-i", $"color=blue:s={width}x{height}:d=3:r=25",
                    "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
                    "-map", "[v]", "-map", "2:a", "-t", "6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source], directory);
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var plan = new CommercialOverlayPlan
            {
                Overlays = [new() { SceneIndex = 0, Text = "Orange Blast", Role = "brand", StartSeconds = .3, DurationSeconds = 2,
                    Position = "lower-left", AnimationIn = "fade-rise", Emphasis = "strong" },
                    new() { SceneIndex = 1, Text = "Orange Blast", Role = "end-card", StartSeconds = 3.3, DurationSeconds = 2,
                    Position = "top-right", AnimationIn = "masked-reveal", Emphasis = "strong" }],
                MotionAccents = [new() { SceneIndex = 0, Type = "line-sweep", StartSeconds = .3, DurationSeconds = .7 }]
            };
            var enhanced = await CommercialOverlayCompositor.RenderAsync(ffmpeg, source, directory, plan, width, height);
            CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(source)), "Source footage must never be overwritten.");
            var before = await CommercialOverlayCompositor.ProbeAsync(ffmpeg, source, directory);
            var after = await CommercialOverlayCompositor.ProbeAsync(ffmpeg, enhanced, directory);
            Assert.AreEqual(before.Duration, after.Duration, .05); Assert.AreEqual(width, after.Width); Assert.AreEqual(height, after.Height);
            var probe = await CommercialOverlayCompositor.RunAsync(ffmpeg, ["-i", enhanced], directory, true);
            StringAssert.Contains(probe, "Audio: aac");

            for (var scene = 0; scene < 2; scene++)
            {
                var frame = Path.Combine(directory, $"frame-{scene}.rgb");
                await CommercialOverlayCompositor.RunAsync(ffmpeg, ["-y", "-ss", scene == 0 ? "1" : "4", "-i", enhanced,
                    "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", frame], directory);
                var pixels = await File.ReadAllBytesAsync(frame);
                var center = ((height / 2) * width + width / 2) * 3;
                Assert.IsTrue(pixels[center + (scene == 0 ? 0 : 2)] > 200, "Each original scene remains visible at the center.");
                Assert.IsTrue(pixels[center + 1] < 30, "Central subject region stays unobstructed.");
                var whitePixels = 0;
                for (var i = 0; i < pixels.Length; i += 3)
                    if (pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180) whitePixels++;
                Assert.IsTrue(whitePixels > 20, "Text must actually appear over each test scene.");
                Assert.IsTrue(whitePixels < width * height * .06, "Text must remain a small part of the frame.");
            }

            var retained = await CommercialOverlayEnhancement.BestEffortAsync(source,
                () => CommercialOverlayCompositor.RenderAsync("missing-renderer", source, directory, plan, width, height), NullLogger.Instance);
            Assert.AreEqual(source, retained);
            CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(retained)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

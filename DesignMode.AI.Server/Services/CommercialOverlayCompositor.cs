using DesignMode.AI.Shared.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DesignMode.AI.Server.Services;

public static class CommercialOverlayCompositor
{
    private static string N(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Floor(seconds * 100) / 100)
        .ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    public static string AnimationTags(string animation, int x, int y, int width, int height) =>
        CommercialOverlayPolicy.Animation(animation) switch
        {
            "fade-rise" => $@"\move({x},{y + 12},{x},{y},0,350)\fad(250,250)",
            "fade-slide" => $@"\move({x - 12},{y},{x},{y},0,350)\fad(250,250)",
            "masked-reveal" => $@"\pos({x},{y})\clip(0,{y - height},0,{y + height})\t(0,450,\clip(0,{y - height},{width},{y + height}))\fad(100,250)",
            "tracking-expand" => $@"\pos({x},{y})\fsp-1\t(0,450,\fsp1)\fad(250,250)",
            "scale-settle" => $@"\pos({x},{y})\fscx96\fscy96\t(0,350,\fscx100\fscy100)\fad(250,250)",
            _ => $@"\pos({x},{y})\fad(250,250)"
        };

    public static string BuildAss(CommercialOverlayPlan plan, int width, int height)
    {
        if (width < 64 || height < 64 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        var script = new StringBuilder($"[Script Info]\nScriptType: v4.00+\nPlayResX: {width}\nPlayResY: {height}\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n" +
            "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Brand,Arial,28,&H00FFFFFF,&H00FFFFFF,&H80000000,&HA0000000,-1,0,0,0,100,100,0,0,1,1,1,1,0,0,0,1\n\n" +
            "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var overlay in plan.Overlays)
        {
            if (!CommercialOverlayPolicy.SafeText(overlay.Text, [])) continue;
            var right = overlay.Position.EndsWith("right", StringComparison.Ordinal);
            var top = overlay.Position.StartsWith("top", StringComparison.Ordinal);
            var vertical = height > width;
            var x = (int)(width * (right ? .82 : .1));
            var y = (int)(height * (top ? .17 : vertical ? .74 : .83));
            var alignment = top ? (right ? 9 : 7) : (right ? 3 : 1);
            // Conservative maximum glyph width (one em) keeps even wide glyphs inside
            // the title-safe area. Long canonical names shrink, never truncate or wrap.
            var fontSize = Math.Min(Math.Min(width, height) * (overlay.Emphasis == "strong" ? .055 : .045),
                width * .68 / Math.Max(1, overlay.Text.Length));
            if (fontSize < Math.Min(width, height) * .025) continue;
            var tags = $@"\an{alignment}\fs{N(fontSize)}\b{(overlay.Emphasis == "strong" ? 1 : 0)}" +
                AnimationTags(overlay.AnimationIn, x, y, width, (int)Math.Ceiling(fontSize * 2));
            script.AppendLine($"Dialogue: 1,{Time(overlay.StartSeconds)},{Time(overlay.StartSeconds + overlay.DurationSeconds)},Brand,,0,0,0,,{{{tags}}}{overlay.Text}");
            foreach (var accent in plan.MotionAccents.Where(a => a.SceneIndex == overlay.SceneIndex && CommercialOverlayPolicy.Accents.Contains(a.Type)))
            {
                var length = (int)(width * .09);
                var ax = right ? x - length : x;
                var ay = top ? y - 10 : y + 10;
                var drawing = accent.Type == "frame-edge-accent" ? "m 0 0 l 2 0 2 18 0 18" : $"m 0 0 l {length} 0 {length} 2 0 2";
                var motion = accent.Type == "line-sweep" ? $@"\move({ax - 8},{ay},{ax},{ay},0,350)" : $@"\pos({ax},{ay})\fscx10\t(0,350,\fscx100)";
                script.AppendLine($"Dialogue: 0,{Time(accent.StartSeconds)},{Time(accent.StartSeconds + accent.DurationSeconds)},Brand,,0,0,0,,{{\\an7\\p1\\bord0\\shad0\\alpha&H65&\\fad(150,200){motion}}}{drawing}");
            }
        }
        return script.ToString();
    }

    public static async Task<(double Duration, int Width, int Height)> ProbeAsync(string ffmpeg, string file, string directory)
    {
        var output = await RunAsync(ffmpeg, ["-hide_banner", "-i", file], directory, allowNonzero: true);
        var duration = Regex.Match(output, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        var dimensions = Regex.Match(output, @"Video: [^\r\n]*?\b(\d{2,5})x(\d{2,5})\b");
        if (!duration.Success || !dimensions.Success) throw new InvalidDataException("Commercial media timing unavailable.");
        var seconds = double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
            double.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
            double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
        if (seconds <= 0 || !double.IsFinite(seconds)) throw new InvalidDataException("Invalid commercial duration.");
        return (seconds, int.Parse(dimensions.Groups[1].Value), int.Parse(dimensions.Groups[2].Value));
    }

    public static async Task<string> RenderAsync(string ffmpeg, string baseFile, string directory, CommercialOverlayPlan plan, int width, int height)
    {
        if (plan.Overlays.Count == 0) return baseFile;
        // Fixed local filenames and ArgumentList keep both shell and filter syntax out of AI control.
        await File.WriteAllTextAsync(Path.Combine(directory, "commercial-overlays.ass"), BuildAss(plan, width, height), new UTF8Encoding(false));
        var enhanced = Path.Combine(directory, "commercial-enhanced.mp4");
        string[] arguments = ["-y", "-i", baseFile, "-vf", "ass=commercial-overlays.ass", "-map", "0:v:0", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p", "-c:a", "copy", enhanced];
        try { await RunAsync(ffmpeg, arguments, directory); }
        catch (InvalidOperationException)
        {
            // Retry renderer errors once with plain fades and no vector accents.
            // Do not retry timeouts; the caller will retain the base commercial.
            var simple = new CommercialOverlayPlan { Overlays = plan.Overlays.Select(o => new CommercialTextOverlay
            {
                SceneIndex = o.SceneIndex, Text = o.Text, Role = o.Role, StartSeconds = o.StartSeconds,
                DurationSeconds = o.DurationSeconds, Position = o.Position, Emphasis = o.Emphasis, AnimationIn = "fade"
            }).ToList() };
            await File.WriteAllTextAsync(Path.Combine(directory, "commercial-overlays.ass"), BuildAss(simple, width, height), new UTF8Encoding(false));
            await RunAsync(ffmpeg, arguments, directory);
        }
        if (!File.Exists(enhanced) || new FileInfo(enhanced).Length == 0) throw new InvalidDataException("Empty overlay render.");
        return enhanced;
    }

    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string directory, bool allowNonzero = false)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start commercial renderer.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var result = await stderr + await stdout;
        if (!allowNonzero && process.ExitCode != 0) throw new InvalidOperationException("Commercial overlay renderer failed.");
        return result;
    }
}

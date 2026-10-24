using DesignMode.AI.Shared.Models;
using System.Text.RegularExpressions;

namespace DesignMode.AI.Server.Services;

public static class CommercialOverlayPolicy
{
    public static readonly string[] Animations = ["fade", "fade-rise", "fade-slide", "masked-reveal", "tracking-expand", "scale-settle"];
    public static readonly string[] Accents = ["line-sweep", "underline-reveal", "frame-edge-accent"];
    // Without visual safe-region metadata, keep the central subject area clear.
    public static readonly string[] Positions = ["top-left", "top-right", "lower-left", "lower-right"];

    public static string Animation(string? name) => Animations.Contains(name) ? name! : "fade";

    public static bool SafeText(string? text, IEnumerable<string> internalIds)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100 ||
            text.Any(c => char.IsControl(c) || "{}\\<>".Contains(c))) return false;
        if (internalIds.Any(id => !string.IsNullOrWhiteSpace(id) && text.Contains(id, StringComparison.OrdinalIgnoreCase))) return false;
        return !Regex.IsMatch(text, @"(?i)projectid|companyideaid|userid|https?[:/]|www\.|@|\b[0-9a-f]{8}-[0-9a-f-]{27,}\b");
    }

    public static CommercialOverlayPlan Validate(CommercialOverlayPlan? plan, string companyName,
        IReadOnlyList<CommercialSceneTiming> scenes, params string[] internalIds)
    {
        var result = new CommercialOverlayPlan();
        if (plan == null || scenes.Count == 0 || !SafeText(companyName, internalIds)) return result;
        var total = scenes.Max(s => s.StartSeconds + s.DurationSeconds);
        var budget = Math.Max(1, (int)Math.Ceiling(total / 8));
        // Preserve a planned brand/end-card moment before spending the density budget on copy.
        foreach (var item in (plan.Overlays ?? []).Where(x => x != null)
            .OrderBy(x => x.Role is "brand" or "end-card" ? 0 : 1).ThenBy(x => x.StartSeconds))
        {
            var scene = scenes.FirstOrDefault(s => s.SceneIndex == item.SceneIndex);
            if (scene == null || !TryTiming(item.StartSeconds, item.DurationSeconds, scene, out var start, out var duration)) continue;
            if (item.Role is not ("brand" or "end-card" or "headline" or "supporting" or "accent")) continue;
            var brand = item.Role is "brand" or "end-card";
            var text = brand ? companyName : item.Text?.Trim();
            if (!SafeText(text, internalIds)) continue;
            // No approved claims are supplied to this planner. Fail closed on declared claims,
            // numbers, contact information, superlatives and common regulated/product claims.
            if (!brand && (item.ContainsFactualClaim ||
                Regex.IsMatch(text!, @"(?i)[\d%$£€#]|\b(best|first|leading|guarantee\w*|proven|organic|zero|sugar|free|certified|award\w*|cure\w*|clinically|results|healthiest|safest|fastest)\b"))) continue;
            var words = text!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if ((!brand && words > 6) || duration < Math.Max(1.2, words * .3 + .4)) continue;
            if (result.Overlays.Count >= budget || result.Overlays.Any(o => o.SceneIndex == item.SceneIndex ||
                (start < o.StartSeconds + o.DurationSeconds + .5 && start + duration + .5 > o.StartSeconds))) continue;
            result.Overlays.Add(new CommercialTextOverlay
            {
                SceneIndex = item.SceneIndex, Text = text, Role = item.Role,
                StartSeconds = start, DurationSeconds = duration,
                Position = Positions.Contains(item.Position) ? item.Position : "lower-left",
                AnimationIn = Animation(item.AnimationIn), AnimationOut = "fade",
                Emphasis = item.Emphasis == "strong" ? "strong" : "normal", ContainsFactualClaim = false
            });
        }
        foreach (var item in (plan.MotionAccents ?? []).Where(x => x != null))
        {
            var overlay = result.Overlays.FirstOrDefault(o => o.SceneIndex == item.SceneIndex);
            var scene = scenes.FirstOrDefault(s => s.SceneIndex == item.SceneIndex);
            if (overlay == null || scene == null || !Accents.Contains(item.Type) ||
                result.MotionAccents.Count >= Math.Max(1, scenes.Count / 3) ||
                result.MotionAccents.Any(a => a.SceneIndex == item.SceneIndex) ||
                !TryTiming(item.StartSeconds, item.DurationSeconds, scene, out var start, out var duration)) continue;
            start = Math.Max(start, overlay.StartSeconds);
            duration = Math.Min(duration, overlay.StartSeconds + overlay.DurationSeconds - start);
            if (duration < .2) continue;
            result.MotionAccents.Add(new CommercialMotionAccent { SceneIndex = item.SceneIndex, Type = item.Type,
                StartSeconds = start, DurationSeconds = duration, Intensity = "subtle" });
        }
        result.Overlays = result.Overlays.OrderBy(o => o.StartSeconds).ToList();
        return result;
    }

    public static bool TryTiming(double start, double duration, CommercialSceneTiming scene, out double safeStart, out double safeDuration)
    {
        safeStart = safeDuration = 0;
        if (!double.IsFinite(start) || !double.IsFinite(duration) || duration <= 0 ||
            !double.IsFinite(scene.StartSeconds) || !double.IsFinite(scene.DurationSeconds) || scene.StartSeconds < 0 || scene.DurationSeconds <= 0) return false;
        safeStart = Math.Max(scene.StartSeconds, start);
        safeDuration = Math.Min(duration, scene.StartSeconds + scene.DurationSeconds - safeStart);
        return safeDuration > 0;
    }
}

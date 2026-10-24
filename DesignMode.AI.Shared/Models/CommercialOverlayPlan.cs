namespace DesignMode.AI.Shared.Models;

public sealed record CommercialSceneTiming(int SceneIndex, double StartSeconds, double DurationSeconds, string Description);

public sealed class CommercialOverlayPlan
{
    public string CreativeDirection { get; set; } = string.Empty;
    public string TypographyStyle { get; set; } = "clean";
    public List<CommercialTextOverlay> Overlays { get; set; } = [];
    public List<CommercialMotionAccent> MotionAccents { get; set; } = [];
}

public sealed class CommercialTextOverlay
{
    public int SceneIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Role { get; set; } = "headline";
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public string Position { get; set; } = "lower-left";
    public string AnimationIn { get; set; } = "fade";
    public string AnimationOut { get; set; } = "fade";
    public string Emphasis { get; set; } = "normal";
    public bool ContainsFactualClaim { get; set; } = true;
}

public sealed class CommercialMotionAccent
{
    public int SceneIndex { get; set; }
    public string Type { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public string Intensity { get; set; } = "subtle";
}

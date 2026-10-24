using DesignMode.AI.Client.Services;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class DesignModeAnalyticsServiceTests
{
    [TestMethod]
    public async Task ImageGenerationEvents_UseOnlyAllowlistedNonSensitiveParameters()
    {
        var js = new RecordingJavaScript();
        var analytics = new DesignModeAnalyticsService(js);
        var id = Guid.NewGuid().ToString("D");

        await analytics.TrackImageGenerationStartedAsync(id);
        await analytics.TrackImageGenerationSucceededAsync(id, 4_000_000);
        await analytics.TrackImageGenerationFailedAsync(id, "raw prompt must not be accepted", 15);

        Assert.AreEqual("designModeAnalytics.track", js.Calls[0].Identifier);
        Assert.AreEqual("image_generation_started", js.Calls[0].Arguments![0]);
        var started = (Dictionary<string, object>)js.Calls[0].Arguments![1]!;
        Assert.AreEqual(id, started["generation_id"]);
        Assert.AreEqual("runware", started["provider"]);
        Assert.AreEqual("runware:101@1", started["model"]);
        Assert.IsFalse(started.ContainsKey("prompt"));
        Assert.IsFalse(started.ContainsKey("duration_ms"));

        var succeeded = (Dictionary<string, object>)js.Calls[1].Arguments![1]!;
        Assert.AreEqual(3_600_000, succeeded["duration_ms"]);
        Assert.AreEqual("image_generation_success", js.Calls[1].Arguments![0]);

        var failed = (Dictionary<string, object>)js.Calls[2].Arguments![1]!;
        Assert.IsFalse(failed.ContainsKey("error_type"));
        Assert.AreEqual("image_generation_failed", js.Calls[2].Arguments![0]);
    }

    [TestMethod]
    public async Task AnalyticsInteropFailure_DoesNotEscapeIntoGeneration()
    {
        var analytics = new DesignModeAnalyticsService(new RecordingJavaScript { Throw = true });

        await analytics.TrackImageGenerationStartedAsync(Guid.NewGuid().ToString("D"));
        Assert.IsFalse(await analytics.ImageLoadedAsync("https://images.example/image.jpg"));
    }

    [TestMethod]
    public async Task ImageLoadedAsync_UsesBrowserImageLoadResult()
    {
        var js = new RecordingJavaScript { ImageLoaded = true };
        var analytics = new DesignModeAnalyticsService(js);

        Assert.IsTrue(await analytics.ImageLoadedAsync("https://images.example/image.jpg"));
        Assert.AreEqual("designModeAnalytics.imageLoaded", js.Calls[0].Identifier);
    }

    private sealed class RecordingJavaScript : IJSRuntime
    {
        public List<(string Identifier, object?[]? Arguments)> Calls { get; } = new();
        public bool Throw { get; init; }
        public bool ImageLoaded { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (Throw) throw new JSException("Unavailable");
            Calls.Add((identifier, args));
            if (typeof(TValue) == typeof(bool)) return ValueTask.FromResult((TValue)(object)ImageLoaded);
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

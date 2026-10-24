using DesignMode.AI.Client.Components;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AICreditsCheckoutInteractionTests
{
    [DataTestMethod]
    [DataRow(20, false)]
    [DataRow(40, false)]
    [DataRow(60, false)]
    [DataRow(129, true)]
    public async Task PackClick_StartsCheckoutAndNavigatesToServerUrl(int credits, bool upgrade)
    {
        var usage = new AIUsageRefreshService();
        var summary = AICreditsExhaustionTests.Summary(5, "starter");
        summary.RecoveryOptions = new()
        {
            CanPurchaseCredits = true,
            CreditPacks = upgrade ? [] : [new("pack-" + credits, "Credits", credits, credits, true)],
            Upgrades = upgrade ? [new("extreme", "Extreme", 129, 130, true)] : []
        };
        usage.SetSummary(summary);
        usage.Open();
        var checkout = new Checkout();
        var browser = new Browser();
        using var services = new ServiceCollection().AddLogging().AddSingleton(usage)
            .AddSingleton<IAICreditPurchaseClient>(checkout).AddSingleton<IJSRuntime>(browser)
            .AddSingleton<DesignModeAnalyticsService>().BuildServiceProvider();
        await using var renderer = new ClickRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            await renderer.Render(id);
            await renderer.ClickPurchase(id);
        });
        Assert.AreEqual(upgrade ? "extreme" : "pack-" + credits, checkout.Package);
        Assert.AreEqual(Checkout.Url, browser.Destination);
    }

    private sealed class Checkout : IAICreditPurchaseClient
    {
        public const string Url = "https://buy.stripe.com/test_fixture?client_reference_id=credit_fixture";
        public string? Package { get; private set; }
        public Task<AICreditCheckoutResponse> StartAsync(string package)
        {
            Package = package;
            return Task.FromResult(new AICreditCheckoutResponse("credit_fixture", Url));
        }
        public Task<string?> PendingAsync() => Task.FromResult<string?>(null);
        public Task<AICreditCheckoutResponse> UpgradeAsync(string plan) => StartAsync(plan);
        public Task<AICreditPurchaseStatus?> StatusAsync(string id, CancellationToken cancellationToken) => throw new AssertFailedException("No pending payment expected.");
        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class Browser : IJSRuntime, IJSObjectReference
    {
        public string? Destination { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "location.assign") Destination = (string)args![0]!;
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ClickRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach() => AssignRootComponentId(InstantiateComponent(typeof(AICreditsExhaustedModal)));
        public Task Render(int id) => RenderRootComponentAsync(id, ParameterView.Empty);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        public Task ClickPurchase(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; i++)
            {
                var element = frames.Array[i];
                if (element.FrameType != RenderTreeFrameType.Element || element.ElementName != "button") continue;
                var button = frames.Array.Skip(i + 1).Take(element.ElementSubtreeLength - 1).ToArray();
                var label = string.Concat(button.Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent
                    : f.FrameType == RenderTreeFrameType.Markup ? f.MarkupContent : ""));
                if (!(label.StartsWith("Add ") && label.Contains("Credits")) && !label.StartsWith("Upgrade to ")) continue;
                Assert.IsFalse(button.Any(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "disabled" && Equals(f.AttributeValue, true)));
                var handler = button.Single(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
                return DispatchEventAsync(handler.AttributeEventHandlerId, null, new MouseEventArgs());
            }
            throw new AssertFailedException("Credit purchase button missing.");
        }
    }
}

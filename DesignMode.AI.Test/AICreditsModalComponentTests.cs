using DesignMode.AI.Client.Components;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AICreditsModalComponentTests
{
    [DataTestMethod]
    [DataRow("starter", true, true)]
    [DataRow("plus", false, true)]
    [DataRow("pro", false, true)]
    [DataRow("extreme", false, false)]
    public async Task Modal_RendersServerOptionsAndDisabledBilling(string plan, bool plus, bool extreme)
    {
        var html = await Render(plan);
        StringAssert.Contains(html, "20 / 20 used");
        StringAssert.Contains(html, "0 remaining");
        Assert.AreEqual(plus, html.Contains("Upgrade to Plus"));
        Assert.AreEqual(extreme, html.Contains("Upgrade to Extreme"));
        if (plus) StringAssert.Contains(html, "$79");
        if (extreme) StringAssert.Contains(html, "$129");
        Assert.IsFalse(html.Contains("credits-pack"));
        StringAssert.Contains(html, "aria-labelledby=\"credits-title\"");
        StringAssert.Contains(html, "Maybe later");
        Assert.IsFalse(html.Contains("href="));
        if (plus || extreme) StringAssert.Contains(html, "disabled");
    }

    [TestMethod]
    public async Task ConfiguredPack_ShowsDisabledActionWhileEntitlementFlowIsMissing()
    {
        var html = await Render("pro", true);
        StringAssert.Contains(html, "credits-pack");
        StringAssert.Contains(html, "10 AI Credits");
        StringAssert.Contains(html, "disabled");
        StringAssert.Contains(html, "unavailable");
        Assert.IsFalse(html.Contains("price_test"));
    }

    [TestMethod]
    public void ResponsiveStyles_StackAtExistingBreakpointAndKeepCloseAccessible()
    {
        var css = Read("DesignMode.AI.Client/Components/AICreditsExhaustedModal.razor.css");
        StringAssert.Contains(css, "repeat(2, minmax(0, 1fr))");
        StringAssert.Contains(css, "@media (max-width: 767px)");
        StringAssert.Contains(css, "grid-template-columns: minmax(0, 1fr)");
        StringAssert.Contains(css, "width: calc(100% - 2rem)");
        StringAssert.Contains(css, "overflow-y: auto");
        StringAssert.Contains(css, "position: sticky");
        Assert.IsFalse(css.Contains("min-width: 42rem"));
    }

    [TestMethod]
    public void PaidGenerationSurfaces_UseSharedQuotaHandler()
    {
        foreach (var file in new[] { "Gen.razor.cs", "WebsiteModal.razor", "EnhancementRecommendationModal.razor",
            "SocialPostModal.razor", "PrintMaterialModal.razor", "EventModal.razor", "MakeVideoSelectImages.razor",
            "PackageLabelModal.razor", "BrandLogoModal.razor", "DigitalMarketingModal.razor" })
            StringAssert.Contains(Read($"DesignMode.AI.Client/Pages/Init/{file}"), "UsageRefresh.HandleResponseAsync(response)", file);
    }

    [TestMethod]
    public void SummaryEndpoint_ResolvesAuthenticatedOwnerBeforeOptions()
    {
        var source = Read("DesignMode.AI.Server/AIUsageSummaryFunction.cs");
        StringAssert.Contains(source, "jwt.ResolveUserId(request)");
        StringAssert.Contains(source, "attribution.ResolveClaimedUserIdAsync(claimed)");
        Assert.IsTrue(source.IndexOf("summaries.GetAsync(userId)") < source.IndexOf("ForSummary(summary"));
        Assert.IsFalse(source.Contains("request.Query"));
    }

    [TestMethod]
    public async Task AvailableCredits_ShowsManagementCopyAndUpgradeOptions()
    {
        var html = await Render("starter", remaining: 10);
        StringAssert.Contains(html, "Add AI Credits or upgrade your plan.");
        StringAssert.Contains(html, "10 remaining");
        StringAssert.Contains(html, "Upgrade to Plus");
        StringAssert.Contains(html, "Upgrade to Extreme");
        Assert.IsFalse(html.Contains("used all your AI Credits"));
    }

    [TestMethod]
    public void WorkspaceHeader_UsesSingleCreditAction()
    {
        StringAssert.Contains(Read("DesignMode.AI.Client/Pages/Dashboard.razor"), "WorkspaceBrandHeader");
        var header = Read("DesignMode.AI.Client/Components/WorkspaceBrandHeader.razor");
        StringAssert.Contains(Read("DesignMode.AI.Client/Pages/Init/Gen.razor"), "WorkspaceBrandHeader");
        StringAssert.Contains(header, "<AIUsageMeter />");
        Assert.IsFalse(header.Contains("Add Credits / Upgrade Plan"));
        var meter = Read("DesignMode.AI.Client/Components/AIUsageMeter.razor");
        StringAssert.Contains(meter, "@onclick=\"UsageRefresh.Open\">Add Credits</button>");
    }

    [TestMethod]
    public async Task SandboxCatalog_RendersThreePacksAndNewMonthlyAllowancesWithoutCheckoutLinks()
    {
        var html = await Render("starter", sandbox: true);
        StringAssert.Contains(html, "Sandbox");
        StringAssert.Contains(html, "Add 20 Credits");
        StringAssert.Contains(html, "Add 40 Credits");
        StringAssert.Contains(html, "Add 60 Credits");
        StringAssert.Contains(html, "$20");
        StringAssert.Contains(html, "$40");
        StringAssert.Contains(html, "$60");
        StringAssert.Contains(html, "80 included AI Credits");
        StringAssert.Contains(html, "130 included AI Credits");
        Assert.IsFalse(html.Contains("buy.stripe.com"));
        Assert.IsFalse(html.Contains("href="));
    }

    [TestMethod]
    public void Modal_IsViewportCenteredAndRespectsReducedMotion()
    {
        var css = Read("DesignMode.AI.Client/Components/AICreditsExhaustedModal.razor.css");
        StringAssert.Contains(css, "position: fixed");
        StringAssert.Contains(css, "inset: 0");
        StringAssert.Contains(css, "margin: auto");
        StringAssert.Contains(css, "height: fit-content");
        StringAssert.Contains(css, "credits-modal-open 240ms ease-in-out");
        StringAssert.Contains(css, "prefers-reduced-motion: reduce");
        StringAssert.Contains(css, "animation: none");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(25)]
    public async Task ConfiguredPurchases_AvailableBeforeAndAfterExhaustion(int remaining)
    {
        var html = await Render("plus", remaining: remaining, purchases: true);
        StringAssert.Contains(html, "Add 20 Credits");
        StringAssert.Contains(html, "Add 40 Credits");
        StringAssert.Contains(html, "Add 60 Credits");
        Assert.IsFalse(html.Contains("Additional credit purchases are currently unavailable"));
        var packButtons = System.Text.RegularExpressions.Regex.Matches(html, "<button[^>]*>Add (20|40|60) Credits[^<]*</button>");
        Assert.AreEqual(3, packButtons.Count);
        foreach (System.Text.RegularExpressions.Match button in packButtons)
            Assert.IsFalse(button.Value.Contains("disabled"), "Configured checkout actions must be enabled.");
        Assert.IsFalse(html.Contains("buy.stripe.com"));
        var output = Environment.GetEnvironmentVariable("AI_CREDITS_VISUAL_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output) && remaining == 25)
        {
            var css = Read("DesignMode.AI.Client/Components/AICreditsExhaustedModal.razor.css");
            await File.WriteAllTextAsync(output, "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>body{font-family:Arial;background:#f2f3f8}.btn{padding:12px;border-radius:8px;border:1px solid #596cff;min-height:44px}.bg-gradient-primary{background:#596cff;color:white}" + css + "</style></head><body>" + html + "<script>document.querySelector('dialog').showModal()</script></body></html>");
        }
    }

    private static async Task<string> Render(string plan, bool pack = false, decimal remaining = 0, bool sandbox = false, bool purchases = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_CREDIT_PACK_ENABLED"] = pack.ToString(), ["AI_CREDIT_PACK_UNITS"] = "10",
            ["AI_CREDIT_PACK_STRIPE_PRICE_ID"] = "price_test", ["AI_CREDIT_PACK_DISPLAY_PRICE"] = "$10",
            ["AI_CREDITS_SANDBOX_CATALOG_ENABLED"] = sandbox.ToString(), ["StripeSecretKey"] = "sk_test_fixture",
            ["AI_CREDITS_LEDGER_ENABLED"] = purchases.ToString(), ["AI_CREDITS_PURCHASES_ENABLED"] = purchases.ToString(),
            ["AI_CREDITS_MODE"] = "Test", ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
            ["AI_CREDITS_TEST_20_PAYMENT_LINK_ID"] = "plink_20", ["AI_CREDITS_TEST_20_PRICE_ID"] = "price_20",
            ["AI_CREDITS_TEST_40_PAYMENT_LINK_ID"] = "plink_40", ["AI_CREDITS_TEST_40_PRICE_ID"] = "price_40",
            ["AI_CREDITS_TEST_60_PAYMENT_LINK_ID"] = "plink_60", ["AI_CREDITS_TEST_60_PRICE_ID"] = "price_60"
        }).Build();
        var usage = new AIUsageRefreshService();
        var summary = AICreditsExhaustionTests.Summary(remaining, plan);
        if (remaining == 25)
        {
            summary.GenerationLimit = 35;
            summary.GenerationsUsed = 30;
            summary.BaseRemaining = 5;
            summary.PurchasedCreditsRemaining = 20;
        }
        summary.RecoveryOptions = AICreditsRecoveryConfiguration.ForSummary(summary, config, AIUsageQuotaOptions.FromConfiguration(config));
        usage.SetSummary(summary);
        var services = new ServiceCollection().AddLogging().AddSingleton(usage)
            .AddSingleton<IAICreditPurchaseClient, NoPurchases>()
            .AddSingleton<IJSRuntime, NoJavaScript>().AddSingleton<DesignModeAnalyticsService>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AICreditsExhaustedModal>(ParameterView.Empty)).ToHtmlString());
    }

    private sealed class NoPurchases : IAICreditPurchaseClient
    {
        public Task<DesignMode.AI.Shared.Models.AICreditCheckoutResponse> StartAsync(string id) => throw new InvalidOperationException();
        public Task<string?> PendingAsync() => Task.FromResult<string?>(null);
        public Task<DesignMode.AI.Shared.Models.AICreditPurchaseStatus?> StatusAsync(string id, CancellationToken token) => throw new InvalidOperationException();
        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("No browser allowed in component tests.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, path));
    }
}

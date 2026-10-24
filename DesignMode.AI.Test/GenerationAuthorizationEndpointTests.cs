using System.Net;
using System.Text;
using System.Text.Json;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using DesignMode.AI.Shared.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Context.Features;
using static DesignMode.AI.Test.GenerationProjectAuthorizationTests;

namespace DesignMode.AI.Test;

[TestClass, DoNotParallelize]
public sealed class GenerationAuthorizationEndpointTests
{
    private readonly Dictionary<string, string?> _old = new();
    [TestInitialize]
    public void Setup()
    {
        foreach (var (key, value) in new[] { ("HostUrl", "http://localhost"),
            ("JwtSecretKey", "generation-authorization-unit-test-secret-123456789"), ("OpenAISettings:ApiKey", "test-only"),
            ("RUNWARE_API_KEY", "test-only"), ("RUNWARE_API_URL", "https://example.invalid") })
        {
            _old[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }
    [TestCleanup]
    public void Cleanup() { foreach (var pair in _old) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingFunnel_OwnerGeneratesSocialAndWebsite_ProviderAndUsageExactlyOnce(bool website)
    {
        using var db = new Database();
        var store = Store(db);
        var bind = await store.BindWithResultAsync(new BindIdentityRequest { FunnelId = "missing", ProjectId = Cid }, User);
        Assert.AreEqual("funnel_not_found", bind.ErrorCode);
        var authorization = new GenerationProjectFunction(store, new JwtUserResolver(), NullLogger<GenerationProjectFunction>.Instance);
        Assert.IsInstanceOfType(await authorization.Run(Request(new { LegacyCompanyIdeaId = Cid, UserId = "forged" })), typeof(OkObjectResult));
        var provider = new Provider(website);
        Assert.IsInstanceOfType(await Generate(db, provider, website, true), typeof(OkObjectResult));
        Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual(1, db.UsageRows.Count);
        Assert.AreEqual(1, db.UsageCompletions);
        var usage = db.UsageRows.Single();
        Assert.AreEqual(User, usage["UserId"].S);
        Assert.AreEqual(Cid, usage["ProjectId"].S);
        Assert.AreEqual("Paid", usage["UsageScope"].S);
        Assert.AreEqual("one-job", usage["GenerationJobId"].S);
        Assert.AreEqual(website ? "5" : "3", usage["GenerationUnits"].N);
        Assert.AreEqual(website ? 0.00001m : 0.000024m, decimal.Parse(usage["EstimatedCost"].N, System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("1", usage["InputTokens"].N);
        Assert.AreEqual("1", usage["OutputTokens"].N);
        Assert.IsFalse(usage.ContainsKey("FunnelId"));
    }

    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task UnauthenticatedForeignProjectOrQuotaExhausted_NeverCallsProviderOrRecordsUsage(bool website, bool authenticated, bool exhausted)
    {
        using var db = new Database { PromptOwner = exhausted ? User : "other", Exhausted = exhausted };
        var provider = new Provider(website);
        var result = await Generate(db, provider, website, authenticated);
        if (exhausted) Assert.AreEqual(429, ((ObjectResult)result).StatusCode);
        else Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual(0, db.UsageRows.Count);
    }

    [TestMethod]
    public async Task Preflight_RejectsClientUserIdWithoutJwt()
    {
        using var db = new Database();
        var function = new GenerationProjectFunction(Store(db), new JwtUserResolver(), NullLogger<GenerationProjectFunction>.Instance);
        Assert.IsInstanceOfType(await function.Run(Request(new { LegacyCompanyIdeaId = Cid, UserId = User }, false)), typeof(UnauthorizedResult));
        Assert.AreEqual(0, db.PromptReads);
    }

    [TestMethod]
    public async Task Binding_ExistingOtherOwnerCannotBeTransferred()
    {
        using var db = new Database
        {
            Funnel = Row(("ProjectId", "project"), ("VisitorId", "visitor")),
            Project = Row(("UserId", "other"), ("OriginatingFunnelId", "funnel"), ("AcquisitionId", "acquisition"))
        };
        var result = await Store(db).BindWithResultAsync(new BindIdentityRequest { ProjectId = "project", FunnelId = "funnel", VisitorId = "visitor" }, User);
        Assert.AreEqual("project_ownership_failure", result.ErrorCode);
        Assert.AreEqual(0, db.Writes);
    }

    [TestMethod]
    public async Task Binding_UnownedAcquisitionRepairsRotatedBrowserIdentityAndBindsWithConditionalOwnershipWrites()
    {
        using var db = new Database
        {
            Funnel = Row(("ProjectId", "project"), ("VisitorId", "visitor")),
            Project = Row(("OriginatingFunnelId", "funnel"), ("AcquisitionId", "acquisition"))
        };
        var result = await Store(db).BindWithResultAsync(new BindIdentityRequest { ProjectId = "project", FunnelId = "funnel", VisitorId = "rotated-visitor", SessionId = "rotated-session" }, User);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, db.Writes);
    }

    private static AttributionStore Store(Database db) => new(NullLogger<AttributionStore>.Instance, clients: () => db);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImagesAcrossThreeProjects_ConsumeOneAccountAllowanceAndKeepAttribution(bool ledgerEnabled)
    {
        using var db = new Database();
        var provider = new ImageProvider();
        var store = Store(db);
        var creditDb = new AICreditPurchaseTests.CreditDatabase();
        var ledger = ledgerEnabled ? new AICreditLedger(() => creditDb) : null;
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance, ledger);
        Assert.AreEqual(20m, (await summary.GetAsync(User))!.GenerationsRemaining);
        decimal used = 0;
        foreach (var (project, feature, units) in new[] { (Cid, "gen_main_image", 0.1m), ("project-b", "ai_recommendations", 1m), ("project-c", "gen_secondary_image", 0.1m) })
        {
            db.OwnedCompanyIdeas.Add(project);
            Assert.AreEqual(20m - used, (await summary.GetAsync(User))!.GenerationsRemaining, "Creating a project cannot grant credits.");
            Assert.IsInstanceOfType(await GenerateImage(db, provider, project, feature, ledger: ledger), typeof(OkObjectResult));
            used += units;
            Assert.AreEqual(20m - used, (await summary.GetAsync(User))!.GenerationsRemaining);
            var row = db.UsageRows.Last();
            Assert.AreEqual(project, row["ProjectId"].S);
            Assert.AreEqual(User, row["UserId"].S);
            Assert.AreEqual(units.ToString(System.Globalization.CultureInfo.InvariantCulture), row["GenerationUnits"].N);
            Assert.AreEqual("Succeeded", row["Status"].S);
            Assert.AreEqual("0.01", row["ActualCost"].N);
            Assert.IsFalse(row.ContainsKey("FunnelId"), "Client-supplied acquisition context must not become attribution.");
        }
        foreach (var project in db.OwnedCompanyIdeas)
        {
            Assert.IsNotNull(await store.GetOwnedGenerationProjectAsync(User, project));
            Assert.AreEqual(18.8m, (await summary.GetAsync(User))!.GenerationsRemaining);
        }
        Assert.AreEqual(3, provider.Calls);
        Assert.AreEqual(3, db.UsageRows.Count);
    }

    [DataTestMethod]
    [DataRow("gen_main_image")]
    [DataRow("gen_secondary_image")]
    [DataRow("social_image")]
    [DataRow("ai_recommendations")]
    [DataRow("package_label_image")]
    [DataRow("unknown-feature")]
    public async Task ExhaustedAccount_NewProjectCannotBypassImageQuota(string feature)
    {
        using var db = new Database();
        var provider = new ImageProvider();
        for (var i = 0; i < 20; i++)
            Assert.IsInstanceOfType(await GenerateImage(db, provider, Cid, "ai_recommendations"), typeof(OkObjectResult));
        db.OwnedCompanyIdeas.Add("new-project");
        var result = (ObjectResult)await GenerateImage(db, provider, "new-project", feature);
        Assert.AreEqual(429, result.StatusCode);
        StringAssert.Contains(JsonSerializer.Serialize(result.Value), "ai_generation_limit_exhausted");
        Assert.AreEqual(20, provider.Calls);
        Assert.AreEqual(20, db.UsageRows.Count);
    }

    [DataTestMethod]
    [DataRow(false, Cid)]
    [DataRow(true, "")]
    [DataRow(true, "foreign-project")]
    public async Task ImageMissingAuthenticationOrOwnedProject_NeverCallsProvider(bool authenticated, string project)
    {
        using var db = new Database();
        var provider = new ImageProvider();
        Assert.IsInstanceOfType(await GenerateImage(db, provider, project, "gen_main_image", authenticated), typeof(UnauthorizedResult));
        Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual(0, db.UsageRows.Count);
    }

    [TestMethod]
    public async Task ImageProviderFailure_DoesNotConsumeLegacyAllowance()
    {
        using var db = new Database();
        var provider = new ImageProvider { Status = HttpStatusCode.BadGateway };
        Assert.AreEqual(502, ((StatusCodeResult)await GenerateImage(db, provider, Cid, "social_image")).StatusCode);
        Assert.AreEqual("Failed", db.UsageRows.Single()["Status"].S);
        var summary = new AIUsageSummaryService(Store(db), AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance);
        Assert.AreEqual(20m, (await summary.GetAsync(User))!.GenerationsRemaining);
    }

    [TestMethod]
    public async Task LedgerDuplicateAndReservationFailure_DoNotCallProviderAgain()
    {
        using var db = new Database();
        var creditDb = new AICreditPurchaseTests.CreditDatabase();
        var ledger = new AICreditLedger(() => creditDb);
        var provider = new ImageProvider();
        Assert.IsInstanceOfType(await GenerateImage(db, provider, Cid, "social_image", ledger: ledger, jobId: "same-job"), typeof(OkObjectResult));
        db.OwnedCompanyIdeas.Add("project-b");
        Assert.IsInstanceOfType(await GenerateImage(db, provider, "project-b", "social_image", ledger: ledger, jobId: "same-job"), typeof(ConflictObjectResult));
        creditDb.FailNextTransaction = true;
        Assert.AreEqual(503, ((StatusCodeResult)await GenerateImage(db, provider, "project-b", "social_image", ledger: ledger)).StatusCode);
        Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual(1, db.UsageRows.Count);
        Assert.AreEqual(0.1m, (await ledger.GetWalletAsync(User)).BaseUsed);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UsageWriteFailure_WithoutLedger_NeverStartsImageProvider(bool reference)
    {
        using var db = new Database { FailUsageWrites = true };
        var provider = new ImageProvider();
        var result = await GenerateImage(db, provider, Cid, "social_image", reference: reference);
        Assert.AreEqual(503, ((StatusCodeResult)result).StatusCode);
        Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual(0, db.UsageRows.Count);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReferenceImage_RecordsProviderUsageWithoutAddingCreditCharge(bool workspace)
    {
        using var db = new Database();
        var provider = new ImageProvider();
        Assert.IsInstanceOfType(await GenerateImage(db, provider, workspace ? Cid : "", "reference_image",
            authenticated: workspace, reference: true), typeof(OkObjectResult));
        var usage = db.UsageRows.Single();
        Assert.AreEqual("Succeeded", usage["Status"].S);
        Assert.AreEqual("0", usage["GenerationUnits"].N);
        Assert.AreEqual("1", usage["ImageCount"].N);
        Assert.AreEqual("reference_image", usage["Feature"].S);
        Assert.AreEqual("0.01", usage["ActualCost"].N);
        Assert.AreEqual(usage["GenerationJobId"].S, usage["ProviderTaskId"].S);
        Assert.AreEqual(workspace ? User : null, usage.GetValueOrDefault("UserId")?.S);
        Assert.AreEqual(workspace ? Cid : null, usage.GetValueOrDefault("ProjectId")?.S);
        Assert.AreEqual(1, provider.Calls);
    }

    [TestMethod]
    public async Task ReferenceImage_ForeignProjectRejected_ProviderFailureRecorded()
    {
        using var db = new Database();
        var provider = new ImageProvider();
        Assert.IsInstanceOfType(await GenerateImage(db, provider, "foreign-project", "reference_image", reference: true), typeof(UnauthorizedResult));
        Assert.AreEqual(0, provider.Calls);
        provider.Status = HttpStatusCode.BadGateway;
        Assert.IsInstanceOfType(await GenerateImage(db, provider, Cid, "reference_image", reference: true), typeof(BadRequestObjectResult));
        Assert.AreEqual("Failed", db.UsageRows.Single()["Status"].S);
        Assert.AreEqual(1, provider.Calls);
    }

    private static Task<IActionResult> GenerateImage(Database db, ImageProvider provider, string project, string feature, bool authenticated = true,
        AICreditLedger? ledger = null, string? jobId = null, bool reference = false)
    {
        var store = Store(db);
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance, ledger);
        var usage = new AIGenerationUsageStore(store, NullLogger<AIGenerationUsageStore>.Instance, ledger, summary);
        var pricing = new AIProviderPricingService();
        var recorder = new AIUsageRecorder(usage, pricing, NullLogger<AIUsageRecorder>.Instance);
        var function = new DesignModeFunctions(NullLogger<DesignModeFunctions>.Instance, null!, store, null!, new JwtUserResolver(),
            usage, pricing, null!, summary, recorder, runwareImageClient: () => new HttpClient(provider));
        var request = Request(new
        {
            positivePrompt = "display|idea|name|industry|detail|Social|description|", projectId = project,
            referenceImage = "https://example.invalid/reference.jpg",
            funnelId = "stale-acquisition-funnel", userId = "forged-user", feature, generationJobId = jobId ?? Guid.NewGuid().ToString(),
            width = 1344, height = 768, numberResults = 1
        }, authenticated);
        return reference ? function.CallRunwareAPIWithReferenceImage(request, NullLogger<DesignModeFunctions>.Instance) : function.CallRunwareAPI(request);
    }

    private sealed class ImageProvider : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("""{"data":[{"imageURL":"https://example.invalid/image.jpg","cost":0.01}]}""") });
        }
        protected override void Dispose(bool disposing) { }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SubscriptionLinkedProject_UsageKeepsOriginalProjectAndFunnel(bool website)
    {
        using var db = SubscriptionLinkedProject();
        var provider = new Provider(website);
        Assert.IsInstanceOfType(await Generate(db, provider, website, true), typeof(OkObjectResult));
        Assert.AreEqual(1, provider.Calls);
        var usage = db.UsageRows.Single();
        Assert.AreEqual("original-project", usage["ProjectId"].S);
        Assert.AreEqual("original-funnel", usage["FunnelId"].S);
        Assert.AreEqual(User, usage["UserId"].S);
        Assert.AreEqual(1, db.UsageCompletions);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CommercialVideo_EnforcesCanonicalOwnershipAndQuotaBeforeRunware(bool owner)
    {
        using var db = new Database { PromptOwner = owner ? User : "other", Exhausted = true };
        var store = Store(db);
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance);
        var function = new RunwareVideoFunctions(store, new JwtUserResolver(), summary, null!);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new TestFunctionContext { InstanceServices = services };
        var result = await function.CallRunwareFinalizeSceneVideo(Request(new
        {
            positivePrompt = "Test", projectId = Cid, generationJobId = "job", providerOperationId = "operation",
            sceneNumber = 1, frameImages = new[] { "https://example.invalid/image.png" }
        }), context);
        Assert.AreEqual(owner ? 429 : 403, ((ObjectResult)result).StatusCode);
        Assert.AreEqual(0, db.UsageRows.Count);
    }

    private sealed class TestFunctionContext : FunctionContext
    {
        public override string InvocationId => "test";
        public override string FunctionId => "test";
        public override TraceContext TraceContext => null!;
        public override BindingContext BindingContext => null!;
        public override RetryContext RetryContext => null!;
        public override IServiceProvider InstanceServices { get; set; } = null!;
        public override FunctionDefinition FunctionDefinition => null!;
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
        public override IInvocationFeatures Features => null!;
    }

    [DataTestMethod]
    [DataRow("print", false)]
    [DataRow("print", true)]
    [DataRow("events", false)]
    [DataRow("events", true)]
    [DataRow("brand", false)]
    [DataRow("brand", true)]
    [DataRow("digital", false)]
    [DataRow("digital", true)]
    [DataRow("package", false)]
    [DataRow("package", true)]
    public async Task OtherRecommendationEndpoints_EnforceOwnershipThenQuota(string feature, bool owner)
    {
        using var db = new Database { PromptOwner = owner ? User : "other", Exhausted = true };
        var store = Store(db);
        var jwt = new JwtUserResolver();
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance);
        var request = Request(new { ImageUrl = "https://example.invalid/image.png", ProjectId = Cid, AssetTypes = new[] { "poster" } });
        // A null recorder makes any accidental progress past the gates fail the test.
        var result = feature switch
        {
            "print" => await new PrintMaterialsFunctions(NullLogger<PrintMaterialsFunctions>.Instance, null!, store, jwt, summary).GeneratePrintMaterialPdf(request),
            "events" => await new GenerateEventActivationFunction(NullLogger<GenerateEventActivationFunction>.Instance, null!, store, jwt, summary).GenerateEventActivation(request),
            "brand" => await new GenerateBrandingLogoFunction(NullLogger<GenerateBrandingLogoFunction>.Instance, null!, store, jwt, summary).GenerateBrandingLogo(request),
            "digital" => await new GenerateDigitalMarketingFunction(NullLogger<GenerateDigitalMarketingFunction>.Instance, null!, store, jwt, summary).GenerateDigitalMarketing(request),
            _ => await new GeneratePackageLabelFunction(NullLogger<GeneratePackageLabelFunction>.Instance, store, jwt, summary, null!).GeneratePackageLabel(request)
        };
        if (owner) Assert.AreEqual(429, ((ObjectResult)result).StatusCode);
        else Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.AreEqual(0, db.UsageRows.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackageLabelImage_EnforcesOwnershipAndQuotaBeforeProvider(bool owner)
    {
        using var db = new Database { PromptOwner = owner ? User : "other", Exhausted = true };
        var store = Store(db);
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance);
        var function = new DesignModeFunctions(NullLogger<DesignModeFunctions>.Instance, null!, store, null!, new JwtUserResolver(), null!, null!, null!, summary);
        var request = Request(new
        {
            positivePrompt = "display|idea|name|industry|detail|Package Label|description|",
            projectId = Cid, funnelId = "forged", feature = "package_label_image", generationJobId = "one-job",
            width = 1344, height = 768, numberResults = 1
        });

        var result = await function.CallRunwareAPI(request);

        if (owner) Assert.AreEqual(429, ((ObjectResult)result).StatusCode);
        else Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.AreEqual(0, db.UsageRows.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WebsiteSupportingCalls_DoNotTrustBodyUserId(bool description)
    {
        using var db = new Database();
        var function = new DesignModeFunctions(NullLogger<DesignModeFunctions>.Instance, null!, Store(db), null!, new JwtUserResolver(), null!, null!, null!);
        var request = Request(new { ImageUrl = "https://example.invalid/image.png", ProjectId = Cid, UserId = User }, false);
        var result = description ? await function.GetWorkspaceAIImageDescription(request) : await function.ExtractWebsiteBackground(request);
        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
    }
    private static async Task<IActionResult> Generate(Database db, Provider provider, bool website, bool authenticated)
    {
        var store = Store(db);
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance);
        var usageStore = new AIGenerationUsageStore(store, NullLogger<AIGenerationUsageStore>.Instance);
        var pricing = new AIProviderPricingService();
        var recorder = new AIUsageRecorder(usageStore, pricing, NullLogger<AIUsageRecorder>.Instance);
        var request = Request(new { ImageUrl = "https://example.invalid/image.png", Name = "Test", Industry = "Test", Prompt = "Test",
            ProjectId = Cid, FunnelId = "client-forged-funnel", UserId = "client-forged-user", GenerationJobId = "one-job" }, authenticated);
        if (website)
        {
            var function = new DesignModeFunctions(NullLogger<DesignModeFunctions>.Instance, null!, store, null!, new JwtUserResolver(),
                usageStore, pricing, null!, summary, recorder, websiteGenerationClient: () => new HttpClient(provider));
            return await function.GenerateWebsiteFromImage(request);
        }
        return await new GenerateSocialMediaPostFunction(NullLogger<GenerateSocialMediaPostFunction>.Instance, store,
            new JwtUserResolver(), summary, recorder, () => new HttpClient(provider)).GenerateSocialMediaPost(request);
    }
    private static HttpRequest Request(object value, bool authenticated = true)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Origin = "http://localhost";
        request.ContentType = "application/json";
        if (authenticated) request.Headers.Authorization = "Bearer " + Utilities.GenerateJwt("stored-username", User);
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
        return request;
    }
    private sealed class Provider(bool website) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(website
                ? """{"output":[{"content":[{"text":"<html>Test</html>"}]}],"usage":{"input_tokens":1,"output_tokens":1}}"""
                : """{"output":[{"type":"image_generation_call","result":"dGVzdA=="}],"usage":{"input_tokens":1,"output_tokens":1}}""") });
        }
    }
}

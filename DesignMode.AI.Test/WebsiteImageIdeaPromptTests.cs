using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Image = DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel;

namespace DesignMode.AI.Test;

[TestClass, DoNotParallelize]
public sealed class WebsiteImageIdeaPromptTests
{
    private const string Prompt = "browser|High-energy sports drink landing page with orange product photography|Project B|Drinks|Canonical website detail|Web & UX/UI Website|Company context|";
    private readonly Dictionary<string, string?> _environment = new();

    [TestInitialize]
    public void Setup()
    {
        foreach (var (key, value) in new[] { ("HostUrl", "http://localhost"),
            ("JwtSecretKey", "website-image-prompt-unit-test-secret-123456789"),
            ("RUNWARE_API_KEY", "test-only"), ("RUNWARE_API_URL", "https://example.invalid") })
        {
            _environment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }

    [DataTestMethod]
    [DataRow("Branding")]
    [DataRow("Social")]
    [DataRow("Package Label")]
    [DataRow("Print Materials")]
    [DataRow("Events & Activations")]
    public void Selection_UsesWebsiteMetadataAndStoredPrompt(string otherFeature)
    {
        var other = Idea("other", otherFeature, "0");
        var website = Idea("website", "Web & UX/UI Website", "5");
        Assert.AreSame(website, WebsiteImageState.ResolveImageIdea([other, website]));
        Assert.AreEqual(Prompt, WebsiteImageState.ResolveImageIdea([website]).ImagePrompt);
    }

    [TestMethod]
    public void MultipleIdeas_UsesCanonicalNumericCountOrder()
    {
        var first = Idea("first", "Website", "4");
        Assert.AreSame(first, WebsiteImageState.ResolveImageIdea([
            Idea("invalid-count", "Website", ""), Idea("later", "Website", "10"), first]));
    }

    [TestMethod]
    public void MissingOrMalformedWebsiteIdea_NeverSubstitutesOtherFeature()
    {
        Assert.ThrowsException<InvalidOperationException>(() => WebsiteImageState.ResolveImageIdea([]));
        Assert.ThrowsException<InvalidOperationException>(() => WebsiteImageState.ResolveImageIdea([Idea("brand", "Branding", "1")]));
        var malformed = Idea("website", "Website", "1");
        malformed.ImagePrompt = "Build the Website HTML page";
        Assert.ThrowsException<InvalidOperationException>(() => WebsiteImageState.ResolveImageIdea([malformed]));
        malformed.ImagePrompt = Prompt.Replace("Web & UX/UI Website", "Package Label");
        Assert.ThrowsException<InvalidOperationException>(() => WebsiteImageState.ResolveImageIdea([malformed]));
    }

    [TestMethod]
    public async Task CanonicalSelection_PreservesPromptAndProjectThroughPersistenceAndPublication()
    {
        var state = new WebsiteImageState();
        var website = Idea("source", "Website", "5");
        website.PromptId = "project-B-prompt";
        var rows = new List<Image> { Idea("brand", "Branding", "0"), website };
        Image? selected = null;
        List<Image>? published = null;
        var calls = 0;
        var image = await state.EnsureAsync("B", () => Task.FromResult(rows.ToList()),
            images => { selected = WebsiteImageState.ResolveImageIdea(images); return Task.FromResult(selected.PromptId); },
            () => { calls++; Assert.AreEqual(Prompt, selected!.ImagePrompt); return Task.FromResult("https://example.test/generated.png"); },
            (promptId, url) =>
            {
                var saved = Idea("saved", selected!.SubCategory, "6");
                saved.PromptId = promptId;
                saved.ImagePrompt = selected.ImagePrompt;
                saved.ImageUrl = url;
                rows.Add(saved);
                return Task.FromResult(saved.Id);
            },
            images => { published = images; return Task.CompletedTask; });
        Assert.AreEqual(1, calls);
        Assert.AreEqual("project-B-prompt", image.PromptId);
        Assert.AreEqual(Prompt, image.ImagePrompt);
        Assert.AreSame(image, published!.Single(row => row.Id == "saved"));
    }

    [DataTestMethod]
    [DataRow("Generic company text")]
    [DataRow("Branding prompt")]
    [DataRow("Package Label prompt")]
    [DataRow("Build Website HTML/page prompt")]
    public async Task Provider_UsesServerCanonicalPromptOnce_IgnoresClientPrompt(string untrustedPrompt)
    {
        using var db = new Database();
        var provider = new Provider();
        var result = await Generate(db, provider, untrustedPrompt);
        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreEqual(1, provider.Calls);
        var task = provider.Payload!.RootElement[1];
        var actual = task.GetProperty("positivePrompt").GetString()!;
        StringAssert.Contains(actual, Prompt.Split('|')[1]);
        StringAssert.Contains(actual, "Canonical website detail");
        StringAssert.Contains(actual, "device screens");
        Assert.IsFalse(actual.Contains(untrustedPrompt));
        Assert.AreEqual("runware:101@1", task.GetProperty("model").GetString());
        Assert.AreEqual(1344, task.GetProperty("width").GetInt32());
        Assert.AreEqual(768, task.GetProperty("height").GetInt32());
        Assert.AreEqual(1, task.GetProperty("numberResults").GetInt32());
        Assert.AreEqual(1, db.Accounting.UsageRows.Count);
        Assert.AreEqual(1, db.Accounting.UsageCompletions);
        var usage = db.Accounting.UsageRows.Single();
        Assert.AreEqual("B", usage["ProjectId"].S);
        Assert.AreEqual("gen_secondary_image", usage["Feature"].S);
        Assert.AreEqual("0.1", usage["GenerationUnits"].N);
        Assert.AreEqual("one-job", usage["GenerationJobId"].S);
        Assert.IsFalse(usage.ContainsKey("Prompt"));
    }

    [DataTestMethod]
    [DataRow("Brand Logo", "")]
    [DataRow("Social", "device screens")]
    [DataRow("Packaging", "existing packaging instruction")]
    [DataRow("Print Materials", "print surfaces")]
    [DataRow("Events & Activations", "event or activation setup")]
    public async Task OtherImageRequests_KeepTheirExistingPromptFormatting(string category, string expectedInstruction)
    {
        using var db = new Database { Missing = true };
        var provider = new Provider();
        var raw = $"display|Original {category} intent|Name|Industry|Existing detail|{category}|Company context|existing packaging instruction";
        var result = await Generate(db, provider, raw, websiteRequest: false);
        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        var task = provider.Payload!.RootElement[1];
        var actual = task.GetProperty("positivePrompt").GetString()!;
        StringAssert.Contains(actual, $"Original {category} intent");
        StringAssert.Contains(actual, expectedInstruction);
        Assert.IsFalse(actual.Contains("High-energy sports drink"));
        Assert.AreEqual(4, task.GetProperty("numberResults").GetInt32());
        Assert.AreEqual(1, provider.Calls);
    }

    [DataTestMethod]
    [DataRow("foreign-user")]
    [DataRow("foreign-project")]
    [DataRow("foreign-image")]
    [DataRow("foreign-context")]
    [DataRow("branding")]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("unauthenticated")]
    [DataRow("quota")]
    public async Task InvalidContextOrIdeaOrQuota_DoesNotCallProviderOrRecordUsage(string scenario)
    {
        using var db = new Database();
        if (scenario == "foreign-user") db.Owner = "other";
        if (scenario == "foreign-project") db.Cid = "A";
        if (scenario == "foreign-image") db.ImagePromptId = "project-A-prompt";
        if (scenario == "branding") db.Category = "Branding";
        if (scenario == "missing") db.Missing = true;
        if (scenario == "malformed") db.StoredPrompt = "Website HTML instead of an image prompt";
        if (scenario == "quota") db.Accounting.Exhausted = true;
        var provider = new Provider();
        var result = await Generate(db, provider, "untrusted", scenario != "unauthenticated", scenario == "foreign-context" ? "A" : "B");
        if (scenario == "quota") Assert.AreEqual(429, ((ObjectResult)result).StatusCode);
        else Assert.IsFalse(result is OkObjectResult);
        Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual(0, db.Accounting.UsageRows.Count);
    }

    [TestMethod]
    public async Task IsolatedInvocation_UsesInjectedLoggerAndContinuesToProvider()
    {
        using var db = new Database();
        var provider = new Provider();
        var logger = new CaptureLogger();

        var result = await Generate(db, provider, "untrusted", logger: logger);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreEqual(1, provider.Calls);
        CollectionAssert.Contains(logger.Messages, "[WEBSITE IMAGE] prompt-source=canonical-image-idea");
        Assert.IsFalse(logger.Messages.Any(message => message.Contains(Prompt)));
    }

    private sealed class CaptureLogger : ILogger<DesignModeFunctions>
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static Image Idea(string id, string category, string count) => new()
    {
        Id = id, PromptId = "prompt", DeliverableType = "Secondary", SubCategory = category,
        ImagePrompt = Prompt, ImageUrl = "", CountId = count
    };

    private static async Task<IActionResult> Generate(Database db, Provider provider, string prompt,
        bool authenticated = true, string projectId = "B", bool websiteRequest = true, ILogger<DesignModeFunctions>? logger = null)
    {
        var store = new AttributionStore(NullLogger<AttributionStore>.Instance, clients: () => db);
        var summary = new AIUsageSummaryService(store, AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build()), NullLogger<AIUsageSummaryService>.Instance);
        var usage = new AIGenerationUsageStore(store, NullLogger<AIGenerationUsageStore>.Instance);
        var pricing = new AIProviderPricingService();
        var recorder = new AIUsageRecorder(usage, pricing, NullLogger<AIUsageRecorder>.Instance);
        var function = new DesignModeFunctions(logger ?? NullLogger<DesignModeFunctions>.Instance, null!, store, null!, new JwtUserResolver(), usage, pricing, null!,
            summary, recorder, runwareImageClient: () => new HttpClient(provider));
        var request = new DefaultHttpContext().Request;
        request.Headers.Origin = "http://localhost";
        if (authenticated) request.Headers.Authorization = "Bearer " + Utilities.GenerateJwt("stored-username", GenerationProjectAuthorizationTests.User);
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            positivePrompt = prompt, websiteImageIdeaId = websiteRequest ? "website" : null, companyIdeaId = "B", projectId,
            funnelId = (string?)null, feature = "gen_secondary_image", generationJobId = "one-job",
            width = 1344, height = 768, numberResults = 4
        })));
        return await function.CallRunwareAPI(request);
    }

    private sealed class Provider : HttpMessageHandler
    {
        public int Calls;
        public JsonDocument? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"taskUUID":"one-job","imageUUID":"image","imageURL":"https://example.test/generated.png","cost":0.01}]}""") };
        }
    }

    private sealed class Database() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public readonly GenerationProjectAuthorizationTests.Database Accounting = new();
        public string Owner = GenerationProjectAuthorizationTests.User, Cid = "B", ImagePromptId = "prompt", Category = "Website", StoredPrompt = Prompt;
        public bool Missing;
        protected override void Dispose(bool disposing) { /* Shared test connection; endpoint scopes may dispose it. */ }
        public override Task<GetItemResponse> GetItemAsync(string table, Dictionary<string, AttributeValue> key, CancellationToken token = default) =>
            Accounting.GetItemAsync(table, key, token);
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken token = default) => Accounting.GetItemAsync(request, token);
        public override Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken token = default) => Accounting.ScanAsync(request, token);
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken token = default) => Accounting.PutItemAsync(request, token);
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken token = default) => Accounting.UpdateItemAsync(request, token);
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken token = default)
        {
            Dictionary<string, AttributeValue> Row(params (string Key, string Value)[] fields) => fields.ToDictionary(x => x.Key, x => new AttributeValue { S = x.Value });
            if (request.Statement.Contains("FROM Prompt"))
                return Task.FromResult(new ExecuteStatementResponse { Items = [Row(("Id", "prompt"), ("UserId", Owner), ("CompanyIdeaId", Cid))] });
            if (request.Statement.Contains("FROM Projects")) return Task.FromResult(new ExecuteStatementResponse { Items = [] });
            Assert.AreEqual("SELECT * FROM ImageIdeas WHERE PromptId = ?", request.Statement);
            Assert.AreEqual("prompt", request.Parameters.Single().S);
            return Task.FromResult(new ExecuteStatementResponse { Items = Missing ? [] :
                [Row(("Id", "website"), ("PromptId", ImagePromptId), ("DeliverableType", "Secondary"),
                    ("SubCategory", Category), ("ImagePrompt", StoredPrompt), ("CountId", "5"))] });
        }
    }
}

using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DesignMode.AI.Test;

[TestClass]
public class CommercialOverlayTests
{
    private static readonly CommercialSceneTiming[] Scenes = [new(0, 0, 5, "Product at center"), new(1, 5, 5, "Wide environment"), new(2, 10, 5, "Closing product")];
    private static CommercialTextOverlay Overlay() => new() { Text = "Bright. Bold. Charged.", SceneIndex = 0,
        StartSeconds = .4, DurationSeconds = 2, ContainsFactualClaim = false, AnimationIn = "fade-rise" };
    private static CommercialOverlayPlan Validate(CommercialTextOverlay overlay) =>
        CommercialOverlayPolicy.Validate(new() { Overlays = [overlay] }, "Orange Blast", Scenes, "secret-project", "secret-user");

    [TestMethod]
    public void Request_SuppliesBrandIndustryAndActualTimingWithoutIds()
    {
        var request = JObject.FromObject(CommercialOverlayPlanner.BuildRequest("Orange Blast", "Energy Drinks", Scenes, 480, 864));
        var context = JObject.Parse(request.Value<string>("input")!);
        Assert.AreEqual("Orange Blast", context.Value<string>("companyName"));
        Assert.AreEqual("Energy Drinks", context.Value<string>("industry"));
        Assert.AreEqual(15d, context.Value<double>("durationSeconds"));
        Assert.AreEqual(5d, context["scenes"]![1]!.Value<double>("StartSeconds"));
        Assert.AreEqual(864, context.Value<int>("height"));
        Assert.IsFalse(context.ToString().Contains("ProjectId"));
        Assert.AreEqual("json_schema", request.SelectToken("text.format.type")!.Value<string>());
        Assert.IsTrue(request.SelectToken("text.format.strict")!.Value<bool>());
    }

    [TestMethod]
    public void Response_ParsesStructuredOutput()
    {
        var json = JsonConvert.SerializeObject(new { status = "completed", output = new[] { new { type = "message",
            content = new[] { new { type = "output_text", text = JsonConvert.SerializeObject(new CommercialOverlayPlan { Overlays = [Overlay()] }) } } } } });
        Assert.AreEqual("Bright. Bold. Charged.", CommercialOverlayPlanner.ParseResponse(json)!.Overlays.Single().Text);
        Assert.IsNull(CommercialOverlayPlanner.ParseResponse("{\"status\":\"incomplete\",\"output\":[]}"));
    }

    [DataTestMethod]
    [DataRow(-1d, 2d, 0d, 2d)]
    [DataRow(4d, 4d, 4d, 1d)]
    [DataRow(.4d, 2d, .4d, 2d)]
    public void Timing_ClampsToScene(double start, double duration, double expectedStart, double expectedDuration)
    {
        Assert.IsTrue(CommercialOverlayPolicy.TryTiming(start, duration, Scenes[0], out var actualStart, out var actualDuration));
        Assert.AreEqual(expectedStart, actualStart); Assert.AreEqual(expectedDuration, actualDuration);
    }

    [DataTestMethod]
    [DataRow(0d)] [DataRow(-1d)] [DataRow(double.NaN)] [DataRow(double.PositiveInfinity)]
    public void Timing_InvalidDurationRejected(double duration)
    {
        var overlay = Overlay(); overlay.DurationSeconds = duration;
        Assert.AreEqual(0, Validate(overlay).Overlays.Count);
    }

    [TestMethod]
    public void Timing_OutsideSceneAndNonfiniteStartRejected()
    {
        var overlay = Overlay(); overlay.StartSeconds = 7;
        Assert.AreEqual(0, Validate(overlay).Overlays.Count);
        overlay.StartSeconds = double.NaN;
        Assert.AreEqual(0, Validate(overlay).Overlays.Count);
    }

    [DataTestMethod]
    [DataRow("brand")] [DataRow("end-card")]
    public void BrandRoles_AlwaysUseCanonicalName(string role)
    {
        var overlay = Overlay(); overlay.Role = role; overlay.Text = "Acme Coffee";
        Assert.AreEqual("Orange Blast", Validate(overlay).Overlays.Single().Text);
    }

    [DataTestMethod]
    [DataRow("secret-project")] [DataRow("secret-user")] [DataRow("ProjectId")]
    [DataRow("CompanyIdeaId")] [DataRow("UserId")] [DataRow("{\\p1}injection")]
    [DataRow("www.fake.com")] [DataRow("https://fake.com")]
    [DataRow("#1 ENERGY DRINK")] [DataRow("ZERO SUGAR")]
    [DataRow("100% ORGANIC")] [DataRow("GUARANTEED RESULTS")] [DataRow("BEST IN AMERICA")]
    public void Copy_UnsafeTextRejected(string text)
    {
        var overlay = Overlay(); overlay.Text = text;
        Assert.AreEqual(0, Validate(overlay).Overlays.Count);
    }

    [TestMethod]
    public void Copy_DeclaredOrMissingClaimReviewRejected()
    {
        var overlay = Overlay(); overlay.ContainsFactualClaim = true;
        Assert.AreEqual(0, Validate(overlay).Overlays.Count);
        Assert.IsTrue(new CommercialTextOverlay().ContainsFactualClaim);
    }

    [TestMethod]
    public void Density_LeavesBreathingRoomAndRejectsUnreadableCopy()
    {
        var plan = new CommercialOverlayPlan { Overlays = Enumerable.Range(0, 3).Select(i => new CommercialTextOverlay
            { SceneIndex = i, StartSeconds = i * 5 + .4, DurationSeconds = 2, Text = "A bright beginning", ContainsFactualClaim = false }).ToList() };
        Assert.AreEqual(2, CommercialOverlayPolicy.Validate(plan, "Orange Blast", Scenes).Overlays.Count);
        var shortOverlay = Overlay(); shortOverlay.DurationSeconds = .4;
        Assert.AreEqual(0, Validate(shortOverlay).Overlays.Count);
    }

    [TestMethod]
    public void Density_ReservesCanonicalClosingBrand()
    {
        var plan = new CommercialOverlayPlan { Overlays = Enumerable.Range(0, 3).Select(i => new CommercialTextOverlay
            { SceneIndex = i, StartSeconds = i * 5 + .4, DurationSeconds = 2, Text = "Bright beginnings", ContainsFactualClaim = false,
                Role = i == 2 ? "end-card" : "headline" }).ToList() };
        var result = CommercialOverlayPolicy.Validate(plan, "Orange Blast", Scenes);
        Assert.AreEqual(2, result.Overlays.Count);
        Assert.AreEqual("Orange Blast", result.Overlays.Last().Text);
        Assert.AreEqual(2, result.Overlays.Last().SceneIndex);
    }

    [DataTestMethod]
    [DataRow("fade-rise", "\\move(")] [DataRow("masked-reveal", "\\clip(")]
    [DataRow("tracking-expand", "\\fsp-1")] [DataRow("scale-settle", "\\fscx96")]
    [DataRow("fade-slide", "\\move(")] [DataRow("arbitrary-code", "\\fad(250,250)")]
    public void Animation_MapsOnlyTrustedTags(string animation, string expected)
    {
        StringAssert.Contains(CommercialOverlayCompositor.AnimationTags(animation, 80, 400, 864, 50), expected);
    }

    [TestMethod]
    public void UnknownAnimationAndCenterPosition_FallBack()
    {
        var overlay = Overlay(); overlay.AnimationIn = "alert(1)"; overlay.Position = "center";
        var valid = Validate(overlay).Overlays.Single();
        Assert.AreEqual("fade", valid.AnimationIn); Assert.AreEqual("lower-left", valid.Position);
    }

    [DataTestMethod]
    [DataRow("line-sweep", 1)] [DataRow("underline-reveal", 1)] [DataRow("frame-edge-accent", 1)] [DataRow("script", 0)]
    public void Accents_AllowlistAndRender(string type, int count)
    {
        var plan = CommercialOverlayPolicy.Validate(new() { Overlays = [Overlay()], MotionAccents =
            [new() { SceneIndex = 0, Type = type, StartSeconds = .4, DurationSeconds = .7 }] }, "Orange Blast", Scenes);
        Assert.AreEqual(count, plan.MotionAccents.Count);
        Assert.AreEqual(count == 1, CommercialOverlayCompositor.BuildAss(plan, 864, 480).Contains("\\p1"));
    }

    [TestMethod]
    public void Project_OwnershipAndCacheAreIsolated()
    {
        var a = new Dictionary<string, AttributeValue> { ["UserId"] = new() { S = "user-a" }, ["Name"] = new() { S = "Acme Coffee" }, ["Industry"] = new() { S = "Coffee" } };
        var b = new Dictionary<string, AttributeValue> { ["UserId"] = new() { S = "user-b" }, ["Name"] = new() { S = "Orange Blast" }, ["Industry"] = new() { S = "Energy Drinks" } };
        Assert.IsNull(CommercialOverlayEnhancement.OwnedBrand(a, "user-b"));
        Assert.AreEqual("Orange Blast", CommercialOverlayEnhancement.OwnedBrand(b, "user-b")!.Value.Name);
        Assert.AreNotEqual(CommercialOverlayEnhancement.CacheKey("u", "a", "job"), CommercialOverlayEnhancement.CacheKey("u", "b", "job"));
        Assert.AreEqual(CommercialOverlayEnhancement.CacheKey("u", "b", "job"), CommercialOverlayEnhancement.CacheKey("u", "b", "job"));
    }

    [TestMethod]
    public async Task Brand_LegacyProjectResolvesItsLinkedCompany()
    {
        var project = new Dictionary<string, AttributeValue> { ["UserId"] = new() { S = "user" },
            ["LegacyCompanyIndustryId"] = new() { S = "company-b" } };
        var company = new Dictionary<string, AttributeValue> { ["Id"] = new() { S = "company-b" },
            ["UserId"] = new() { S = "user" }, ["Name"] = new() { S = "Orange Blast" }, ["Industry"] = new() { S = "Energy Drinks" } };
        var brand = await CommercialOverlayEnhancement.ResolveBrandAsync(project, "user", id =>
        {
            Assert.AreEqual("company-b", id);
            return Task.FromResult(new List<Dictionary<string, AttributeValue>> { company });
        });
        Assert.AreEqual("Orange Blast", brand!.Value.Name);
        Assert.AreEqual("Energy Drinks", brand.Value.Industry);
    }

    [DataTestMethod]
    [DataRow("other-user", "company-b", 1)]
    [DataRow("user", "company-a", 1)]
    [DataRow("user", "company-b", 2)]
    public async Task Brand_RejectsWrongOwnerWrongCompanyOrAmbiguity(string owner, string companyId, int count)
    {
        var project = new Dictionary<string, AttributeValue> { ["UserId"] = new() { S = "user" },
            ["LegacyCompanyIndustryId"] = new() { S = "company-b" } };
        var company = new Dictionary<string, AttributeValue> { ["Id"] = new() { S = companyId },
            ["UserId"] = new() { S = owner }, ["Name"] = new() { S = "Acme Coffee" }, ["Industry"] = new() { S = "Coffee" } };
        Assert.IsNull(await CommercialOverlayEnhancement.ResolveBrandAsync(project, "user",
            _ => Task.FromResult(Enumerable.Repeat(company, count).ToList())));
    }

    [TestMethod]
    public async Task Brand_UnownedProjectNeverQueriesCompany()
    {
        var project = new Dictionary<string, AttributeValue> { ["UserId"] = new() { S = "other-user" },
            ["LegacyCompanyIndustryId"] = new() { S = "company-b" } };
        Assert.IsNull(await CommercialOverlayEnhancement.ResolveBrandAsync(project, "user",
            _ => throw new AssertFailedException("Unowned project must not trigger company lookup.")));
    }

    [TestMethod]
    public void Usage_IdentifiesProviderOperationWithZeroCustomerCredits()
    {
        var usage = CommercialOverlayPlanner.CreateUsage("user", "project", "job", "operation");
        Assert.AreEqual("commercial-video-overlay-plan", usage.Feature); Assert.AreEqual("OpenAI", usage.Provider);
        Assert.AreEqual("project", usage.ProjectId); Assert.AreEqual("job", usage.GenerationJobId);
        Assert.AreEqual(0m, usage.GenerationUnits); Assert.AreEqual("operation", usage.ProviderTaskId);
    }

    [TestMethod]
    public async Task Failure_ReturnsUnmodifiedBaseFile()
    {
        Assert.AreEqual("base.mp4", await CommercialOverlayEnhancement.BestEffortAsync("base.mp4",
            () => throw new InvalidDataException("Malformed plan"), NullLogger.Instance));
        Assert.AreEqual("base.mp4", await CommercialOverlayCompositor.RenderAsync("missing", "base.mp4", ".", new(), 864, 480));
    }

    [DataTestMethod]
    [DataRow(864, 480)] [DataRow(480, 864)] [DataRow(720, 720)]
    public void Composition_UsesAspectAwareTitleSafeLayout(int width, int height)
    {
        var script = CommercialOverlayCompositor.BuildAss(Validate(Overlay()), width, height);
        StringAssert.Contains(script, $"PlayResX: {width}"); StringAssert.Contains(script, $"PlayResY: {height}");
        StringAssert.Contains(script, "Arial"); StringAssert.Contains(script, "Bright. Bold. Charged.");
        Assert.IsFalse(script.Contains("\\p1"));
    }
}

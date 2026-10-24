using System.Reflection;
using DesignMode.AI.Client.Pages;
using DesignMode.AI.Client.Pages.Init;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Navigation;
using DesignMode.AI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Stripe.Checkout;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class WebsiteFunnelTests
{
    [TestMethod]
    public async Task SecondaryOutputs_PreserveOpenAIIdeasThroughGenerationAndPersistence()
    {
        string[] openAiIdeas = ["Campaign: Launch Acme's new collection", "Print: Acme event invitation",
            "Packaging: Acme product sleeve", "Branding: Acme stationery"];
        var ideas = InitialGenerationOrchestrator.SelectSecondaryCandidates(
            InitialGenerationOrchestrator.SelectIdeaCandidates(["Old fixed website prompt"], ["Stale session idea"], openAiIdeas));
        var saved = new List<(int Index, string Image)>();
        var calls = ideas.Select(idea => (Func<Task<string>>)(() =>
        {
            StringAssert.Contains(idea, "Acme");
            return Task.FromResult("image:" + idea);
        })).ToList();
        var count = await InitialGenerationOrchestrator.GenerateSecondaryAsync(calls,
            (index, image) => { saved.Add((index, image)); return Task.CompletedTask; });
        Assert.AreEqual(4, count);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, saved.Select(x => x.Index).ToArray());
        CollectionAssert.AreEqual(openAiIdeas.Select(x => "image:" + x).ToArray(), saved.Select(x => x.Image).ToArray());
    }

    [DataTestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)]
    public void Concepts_HaveWebsiteHierarchyAndDistinctDirection(int index)
    {
        var prompt = WebsiteFunnelPromptResolver.Concept("Studio & Sons", "Architecture", index);
        foreach (var required in new[] { "Studio & Sons", "Architecture", "homepage", "responsive", "hero section", "section hierarchy", "typography", "conversion", WebsiteFunnelPromptResolver.Styles[index] })
            StringAssert.Contains(prompt, required);
        StringAssert.Contains(prompt, "not an Instagram post");
        Assert.IsFalse(prompt.Contains("Quantity: 10 posts"));
    }

    [TestMethod]
    public void Concepts_ExactlyThreeUniqueDirections() => Assert.AreEqual(3,
        Enumerable.Range(0, WebsiteFunnelPromptResolver.ConceptCount)
            .Select(i => WebsiteFunnelPromptResolver.Concept("Acme", "Technology", i)).Distinct().Count());

    [DataTestMethod] [DataRow(-1)] [DataRow(3)]
    public void Concepts_InvalidIndexIsRejected(int index) =>
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => WebsiteFunnelPromptResolver.Concept("A", "B", index));

    [TestMethod]
    public void Video_AnimatesTheSourceWebsiteWithoutSocialCampaignDirections()
    {
        var prompt = WebsiteFunnelPromptResolver.Video("Acme", "Design", "purple navigation and green hero");
        foreach (var value in new[] { "purple navigation and green hero", "homepage hero", "scrolling", "responsive mobile", "UI", "CUT TO", "500 characters", "No social feed" })
            StringAssert.Contains(prompt, value);
        Assert.IsFalse(prompt.Contains("vertical social media preview video"));
    }

    [DataTestMethod] [DataRow(null)] [DataRow("social")] [DataRow("social-default")] [DataRow("website")]
    public void Identity_UnrecognizedVariantsRetainExistingBehavior(string? variant) =>
        Assert.IsFalse(WebsiteFunnelPromptResolver.IsWebsite(variant));

    [TestMethod]
    public void Identity_ExplicitWebsiteVariantSelectsIntroUsage()
    {
        Assert.IsTrue(WebsiteFunnelPromptResolver.IsWebsite("WEBSITE-FUNNEL"));
        Assert.AreEqual("Intro", WebsiteFunnelPromptResolver.UsageScope);
        Assert.AreEqual("website_intro", WebsiteFunnelPromptResolver.Feature);
        Assert.AreNotEqual("Paid", WebsiteFunnelPromptResolver.UsageScope);
    }

    [TestMethod]
    public void Usage_SuccessfulWebsiteIntroDoesNotConsumePaidAllowance()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        Assert.IsFalse(AIUsageSummaryService.CountsTowardPaidAllowance(
            WebsiteFunnelPromptResolver.UsageScope, "Succeeded", start.AddDays(1), start, start.AddMonths(1)));
        Assert.IsTrue(AIUsageSummaryService.CountsTowardPaidAllowance(
            "Paid", "Succeeded", start.AddDays(1), start, start.AddMonths(1)));
    }

    [TestMethod]
    public void Queue_RoundTripRetainsWebsiteIdentityAndCanonicalContext()
    {
        var input = new DesignModeFunctions.OpenAIIdeaRequestQueue
        {
            LandingVariant = WebsiteFunnelPromptResolver.LandingVariant, JobId = "job", GenerationJobId = "job",
            UserId = "user", ProjectId = "project", FunnelId = "funnel", ImageId = "selected",
            ImageUrl = "https://example.test/website.png", ConceptNumber = 3
        };
        var output = VideoQueuePipeline.Deserialize<DesignModeFunctions.OpenAIIdeaRequestQueue>(VideoQueuePipeline.Serialize(input))!;
        Assert.AreEqual(input.LandingVariant, output.LandingVariant);
        Assert.AreEqual(input.GenerationJobId, output.GenerationJobId);
        Assert.AreEqual(input.UserId, output.UserId);
        Assert.AreEqual(input.ProjectId, output.ProjectId);
        Assert.AreEqual(input.FunnelId, output.FunnelId);
        Assert.AreEqual(input.ImageId, output.ImageId);
        Assert.AreEqual(3, output.ConceptNumber);
        Assert.AreEqual("api-requests", VideoQueuePipeline.RequestQueueName);
    }

    [TestMethod]
    public void Queue_SocialRequestRemainsUnmarked()
    {
        var output = VideoQueuePipeline.Deserialize<DesignModeFunctions.OpenAIIdeaRequestQueue>("{\"ConceptNumber\":1}")!;
        Assert.IsNull(output.LandingVariant);
    }

    [TestMethod]
    public void Workspace_CheckoutPassesSavedBriefAndSelectionToSocialGenerationPath()
    {
        const string cid = "company+encoded|~~|@@|^^|industry&encoded|~~|@@|^^|unique";
        const string brief = "Website: Café & Sons\nKeep the blue hero + typography, with a clear CTA?";
        const string seed = "https://example.test/concept.png?version=2&token=REDACTED_FOR_GROOMING";
        var url = WebsiteFunnelContext.WorkspaceUrl(cid, "project", "image+1",
            "Café & Sons", "Art / Design", brief, "invite+1", seed);
        var query = QueryHelpers.ParseQuery(new Uri("https://example.test" + url).Query);
        Assert.AreEqual(cid, query["cid"].ToString());
        Assert.AreEqual("image+1", query["websiteImageId"].ToString());
        Assert.IsFalse(query.ContainsKey("s"), "Checkout must enter fresh generation, not the saved-preview shortcut.");
        Assert.AreEqual(":" + brief, query["prompt"].ToString());
        Assert.AreEqual("Café & Sons", query["name"].ToString());
        Assert.AreEqual("Art / Design", query["industry"].ToString());
        Assert.AreEqual("invite+1", query["inviteId"].ToString());
        Assert.AreEqual(seed, query["seed"].ToString());
        Assert.AreEqual("1", query["website"].ToString());
        StringAssert.StartsWith(url, "/init/gen/project?");
    }

    [DataTestMethod] [DataRow("", "p", "i")] [DataRow("cid", "", "i")] [DataRow("cid", "p", "")]
    public void Workspace_MissingCanonicalContextIsRejected(string cid, string project, string image) =>
        Assert.ThrowsException<ArgumentException>(() => WebsiteFunnelContext.WorkspaceUrl(cid, project, image,
            "Company", "Industry", "Website brief", "invite", "https://example.test/concept.png"));

    [DataTestMethod]
    [DataRow("", "Industry", "Brief", "invite", "image")]
    [DataRow("Company", "", "Brief", "invite", "image")]
    [DataRow("Company", "Industry", "", "invite", "image")]
    [DataRow("Company", "Industry", "Brief", "", "image")]
    [DataRow("Company", "Industry", "Brief", "invite", "")]
    public void Workspace_IncompleteGenerationContextCannotFallBackToDemoPrompt(
        string name, string industry, string prompt, string inviteId, string imageUrl) =>
        Assert.ThrowsException<ArgumentException>(() => WebsiteFunnelContext.WorkspaceUrl("cid", "project", "image",
            name, industry, prompt, inviteId, imageUrl));

    [DataTestMethod]
    [DataRow("/designerintrowebsite?name=Acme&industry=Design&funnelId=f")]
    [DataRow("/init/website-success?session_id=cs_test_123")]
    public async Task Auth_ExistingResolverPreservesWebsiteResumeContext(string destination) =>
        Assert.AreEqual(destination, await PostAuthDestinationResolver.ResolveAsync("name", null, new PostAuthDestinationResolverTests.RoutingStore { User = new("id", destination) }));

    [TestMethod]
    public void Checkout_OnlyPaidSessionOwnedByTheAuthenticatedUserCanReturn() =>
        Assert.IsTrue(WebsiteFunnelCheckout.IsPaidOwner(PaidSession(), "owner"));

    [DataTestMethod] [DataRow("open", "paid")] [DataRow("complete", "unpaid")]
    [DataRow("expired", "paid")] [DataRow("complete", "no_payment_required")]
    public void Checkout_UnconfirmedPaymentCannotUnlock(string status, string payment)
    {
        var session = PaidSession(); session.Status = status; session.PaymentStatus = payment;
        Assert.IsFalse(WebsiteFunnelCheckout.IsPaidOwner(session, "owner"));
    }

    [TestMethod]
    public void Checkout_OtherAccountCannotOpenTheProject() => Assert.IsFalse(WebsiteFunnelCheckout.IsPaidOwner(PaidSession(), "other"));

    [TestMethod]
    public void Checkout_MissingProjectContextCannotUnlock()
    {
        var session = PaidSession(); session.Metadata.Remove("projectId");
        Assert.IsFalse(WebsiteFunnelCheckout.IsPaidOwner(session, "owner"));
    }

    [TestMethod]
    public void Components_WebsiteIsOptInAndHasDedicatedRoutes()
    {
        Assert.IsFalse(new DesignerIntro().WebsiteFunnel);
        Assert.IsFalse(new MainConcept().WebsiteFunnel);
        Assert.IsFalse(new FunnelEnd().WebsiteFunnel);
        Assert.AreEqual("/designerintrowebsite", typeof(DesignerIntroWebsite).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.AreEqual("/init/mainconceptwebsite", typeof(MainConceptWebsite).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.AreEqual("/init/funnelendwebsite", typeof(FunnelEndWebsite).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    private static Session PaidSession() => new()
    {
        Status = "complete", PaymentStatus = "paid", ClientReferenceId = "owner",
        Metadata = new() { ["userId"] = "owner", ["projectId"] = "project", ["funnelId"] = "funnel" }
    };
}

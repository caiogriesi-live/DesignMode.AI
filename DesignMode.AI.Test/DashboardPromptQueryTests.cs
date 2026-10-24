using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Amazon.Runtime;
using System.Text;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class DashboardPromptQueryTests
{
    private const string Cid = "12345678-1234-1234-1234-123456789abc";

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not-a-project")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    [DataRow("%%%|~~|@@|^^|%%%|~~|@@|^^|20260909T070000Z")]
    [DataRow("QQ==|~~|@@|^^|Qg==|~~|@@|^^|invalid-date")]
    public void InvalidCid_IsExcluded(string? value)
        => Assert.IsNull(DashboardProject.FromCompanyIdeaId(value));

    [TestMethod]
    public void LegacyCid_DecodesDisplayAndEncodesExactLink()
    {
        var cid = Convert.ToBase64String(Encoding.UTF8.GetBytes("Orange Blast")) + "|~~|@@|^^|" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("Energy Drinks")) + "|~~|@@|^^|20260909T070000Z";
        var project = DashboardProject.FromCompanyIdeaId(cid)!;
        Assert.AreEqual("Orange Blast", project.Name);
        Assert.AreEqual("Energy Drinks", project.Industry);
        Assert.AreEqual("/init/gen?s=1&cid=" + Uri.EscapeDataString(cid), project.Link);
    }

    [DataTestMethod]
    [DataRow("19fd6a4ba30449af8c5f80ee4c846661")]
    [DataRow("19fd6a4b-a304-49af-8c5f-80ee4c846661")]
    public void GuidSuffixCid_DecodesDisplayAndPreservesExactSavedKey(string suffix)
    {
        var cid = "b3JhbmdlIGJsYXN0|~~|@@|^^|ZW5lcmd5IGRyaW5rcw==|~~|@@|^^|" + suffix;
        var project = DashboardProject.FromCompanyIdeaId(cid);
        Assert.IsNotNull(project);
        Assert.AreEqual("orange blast", project.Name);
        Assert.AreEqual("energy drinks", project.Industry);
        Assert.AreEqual(cid, project.CompanyIdeaId);
        Assert.AreEqual("/init/gen?s=1&cid=" + Uri.EscapeDataString(cid), project.Link);
    }

    [TestMethod]
    public async Task UsernameOwnedPrompts_WithGuidSuffix_AppearAsOneSavedProject()
    {
        const string cid = "b3JhbmdlIGJsYXN0|~~|@@|^^|ZW5lcmd5IGRyaW5rcw==|~~|@@|^^|19fd6a4ba30449af8c5f80ee4c846661";
        using var client = new DynamoStub { Username = "owner@example.test" };
        client.Pages.Enqueue(new() { Items = [Row("owner@example.test", cid)], NextToken = "page2" });
        client.Pages.Enqueue(new() { Items = [Row("owner@example.test", cid)] });
        var projects = await DashboardPromptQuery.ListAsync(client, "A");
        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual(cid, projects[0].CompanyIdeaId);
        Assert.AreEqual("orange blast", projects[0].Name);
    }

    [TestMethod]
    public void GuidCid_UsesCompanyIdeaIdAndSafeResponseOnly()
    {
        var project = DashboardProject.FromCompanyIdeaId(Cid)!;
        Assert.AreEqual("/init/gen?s=1&cid=" + Cid, project.Link);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(project));
        CollectionAssert.AreEquivalent(new[] { "CompanyIdeaId", "Name", "Industry", "ThumbnailUrl" },
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task Lookup_ConsumesEmptyAndPopulatedPages_FiltersUser_DeduplicatesAllProjects()
    {
        using var client = new DynamoStub();
        var proxy = client;
        proxy.Pages.Enqueue(new() { Items = [Row("A", Cid), Row("B", Guid.NewGuid().ToString()), Row("A", null)], NextToken = "page2" });
        proxy.Pages.Enqueue(new() { Items = [], NextToken = "page3" });
        var second = Guid.NewGuid().ToString();
        var third = Guid.NewGuid().ToString();
        proxy.Pages.Enqueue(new() { Items = [Row("A", Cid), Row("A", second), Row("A", third), Row("A", " "), Row("A", "bad")] });
        var projects = await DashboardPromptQuery.ListAsync(client, "A");
        CollectionAssert.AreEquivalent(new[] { Cid, second, third }, projects.Select(p => p.CompanyIdeaId).ToArray());
        Assert.AreEqual(3, projects.Select(p => p.Link).Distinct().Count());
        CollectionAssert.AreEqual(new string?[] { null, "page2", "page3" }, proxy.Tokens.ToArray());
        Assert.IsTrue(proxy.Users.All(u => u == "A"));
    }

    [TestMethod]
    public async Task NoProjects_ReturnsEmptyList()
    {
        using var client = new DynamoStub();
        client.Pages.Enqueue(new() { Items = [] });
        Assert.AreEqual(0, (await DashboardPromptQuery.ListAsync(client, "A")).Count);
    }

    [TestMethod]
    public async Task Thumbnail_UsesOwnedPromptId_SkipsBlankImagesAndStopsAtFirstUrl()
    {
        using var client = new DynamoStub();
        var owned = Row("A", Cid);
        owned["Id"] = new() { S = "owned-prompt" };
        var foreign = Row("B", Guid.NewGuid().ToString());
        foreign["Id"] = new() { S = "foreign-prompt" };
        client.Pages.Enqueue(new() { Items = [foreign, owned, owned] });
        client.ImagePages.Enqueue(new() { Items = [new() { ["ImageUrl"] = new() { S = "" } }], NextToken = "images2" });
        client.ImagePages.Enqueue(new() { Items = [new() { ["ImageUrl"] = new() { S = "https://example.test/first.png" } }, new() { ["ImageUrl"] = new() { S = "https://example.test/second.png" } }] });
        var projects = await DashboardPromptQuery.ListAsync(client, "A");
        Assert.AreEqual("https://example.test/first.png", projects.Single().ThumbnailUrl);
        CollectionAssert.AreEqual(new[] { "owned-prompt", "owned-prompt" }, client.ImagePrompts.ToArray());
        CollectionAssert.AreEqual(new string?[] { null, "images2" }, client.ImageTokens.ToArray());
    }

    [TestMethod]
    public async Task Thumbnail_NoValidImage_KeepsProjectWithoutThumbnail()
    {
        using var client = new DynamoStub();
        var owned = Row("A", Cid);
        owned["Id"] = new() { S = "owned-prompt" };
        client.Pages.Enqueue(new() { Items = [owned] });
        client.ImagePages.Enqueue(new() { Items = [new() { ["ImageUrl"] = new() { S = "javascript:alert(1)" } }] });
        Assert.IsNull((await DashboardPromptQuery.ListAsync(client, "A")).Single().ThumbnailUrl);
    }

    [TestMethod]
    public async Task LegacyUsernameProjects_AreIncludedAlongsideCanonicalProjects()
    {
        const string legacyCid = "b3JhbmdlIGJsYXN0|~~|@@|^^|ZW5lcmd5IGRyaW5rcw==|~~|@@|^^|20260907T170206Z";
        using var client = new DynamoStub { Username = "owner@example.test" };
        client.Pages.Enqueue(new() { Items = [Row("owner@example.test", legacyCid)], NextToken = "next" });
        client.Pages.Enqueue(new() { Items = [Row("A", legacyCid), Row("A", Cid), Row("other@example.test", Guid.NewGuid().ToString())] });
        var projects = await DashboardPromptQuery.ListAsync(client, "A");
        Assert.AreEqual(2, projects.Count);
        var legacy = projects.Single(p => p.CompanyIdeaId == legacyCid);
        Assert.AreEqual("orange blast", legacy.Name);
        Assert.AreEqual("energy drinks", legacy.Industry);
        Assert.AreEqual("/init/gen?s=1&cid=" + Uri.EscapeDataString(legacyCid), legacy.Link);
    }

    [TestMethod]
    public async Task SavedProjectQuery_UsesOnlyCanonicalUsersAliasAndRequestedCid()
    {
        using var client = new DynamoStub { Username = "owner@example.test" };
        var (request, owners) = await DashboardPromptQuery.CreateOwnedRequestAsync(client, "A", Cid);
        StringAssert.Contains(request.Statement, "(UserId = ? OR UserId = ?) AND CompanyIdeaId = ?");
        CollectionAssert.AreEqual(new[] { "A", "owner@example.test", Cid }, request.Parameters.Select(p => p.S).ToArray());
        Assert.IsFalse(owners.Contains("other@example.test"));
    }

    [TestMethod]
    public async Task MismatchedUsersRecord_CannotAddAnotherUsersAlias()
    {
        using var client = new DynamoStub { Username = "other@example.test", ReturnedId = "B" };
        var (_, owners) = await DashboardPromptQuery.CreateOwnedRequestAsync(client, "A", Cid);
        CollectionAssert.AreEqual(new[] { "A" }, owners.ToArray());
    }

    [TestMethod]
    public async Task Lookup_MissingIdentity_RejectsBeforeDatabaseAccess()
    {
        using var client = new DynamoStub();
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => DashboardPromptQuery.ListAsync(client, ""));
    }

    [TestMethod]
    public async Task Endpoint_QueryAndBodyUserIdCannotAuthenticate()
    {
        var function = new DashboardProjectsFunction(new JwtUserResolver(),
            new AttributionStore(NullLogger<AttributionStore>.Instance), NullLogger<DashboardProjectsFunction>.Instance);
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString("?UserId=other-user");
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"UserId\":\"other-user\"}"));
        Assert.IsInstanceOfType(await function.Run(request), typeof(UnauthorizedResult));
        request.Headers.Authorization = "Bearer invalid";
        Assert.IsInstanceOfType(await function.Run(request), typeof(UnauthorizedResult));
    }

    private static Dictionary<string, AttributeValue> Row(string user, string? cid) => new()
    {
        ["UserId"] = new() { S = user }, ["CompanyIdeaId"] = cid is null ? new() { NULL = true } : new() { S = cid }
    };

    public class DynamoStub : AmazonDynamoDBClient
    {
        public DynamoStub() : base(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1) { }
        public string? Username { get; init; }
        public string? ReturnedId { get; init; }
        public Queue<ExecuteStatementResponse> Pages { get; } = new();
        public Queue<ExecuteStatementResponse> ImagePages { get; } = new();
        public List<string> ImagePrompts { get; } = [];
        public List<string?> ImageTokens { get; } = [];
        public List<string?> Tokens { get; } = [];
        public List<string> Users { get; } = [];
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Statement.StartsWith("SELECT ImageUrl FROM ImageIdeas"))
            {
                Assert.AreEqual("SELECT ImageUrl FROM ImageIdeas.\"PromptId-index\" WHERE PromptId = ?", request.Statement);
                ImagePrompts.Add(request.Parameters.Single().S);
                ImageTokens.Add(request.NextToken);
                return Task.FromResult(ImagePages.Dequeue());
            }
            Assert.AreEqual(Username is null
                ? "SELECT Id, UserId, CompanyIdeaId FROM Prompt WHERE (UserId = ?)"
                : "SELECT Id, UserId, CompanyIdeaId FROM Prompt WHERE (UserId = ? OR UserId = ?)", request.Statement);
            Tokens.Add(request.NextToken);
            Users.Add(request.Parameters.First().S);
            return Task.FromResult(Pages.Dequeue());
        }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("Users", request.TableName);
            return Task.FromResult(new GetItemResponse { Item = new()
            {
                ["Id"] = new() { S = ReturnedId ?? request.Key["Id"].S },
                ["Username"] = Username is null ? new() { NULL = true } : new() { S = Username }
            } });
        }
    }
}

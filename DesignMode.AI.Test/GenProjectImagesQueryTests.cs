using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenProjectImagesQueryTests
{
    [TestMethod]
    public async Task WebsiteFallback_SavedPromptAssociation_IsReturnedOnlyForCurrentProject()
    {
        using var db = new Stub();
        db.Prompts.Enqueue(new() { Items = [Prompt("project-B-prompt", "user"), Prompt("project-A-prompt", "user", "A")] });
        var saved = Image("website-fallback", "project-B-prompt");
        saved["SubCategory"] = new() { S = "Web & UX/UI Website" };
        saved["DeliverableType"] = new() { S = "Secondary" };
        db.Images["project-B-prompt"] = new([new() { Items = [saved] }]);

        var result = await GenProjectImagesQuery.LoadAsync(db, "user", "cid");

        Assert.AreEqual(1, result.Images.Count);
        Assert.AreEqual("website-fallback", result.Images[0].Id);
        Assert.AreEqual("project-B-prompt", result.Images[0].PromptId);
        Assert.AreEqual("Web & UX/UI Website", result.Images[0].SubCategory);
        Assert.AreEqual(1, db.ImageRequests);
    }

    [TestMethod]
    public async Task CheckoutImages_LoadEveryOwnedPromptAndImagePage()
    {
        using var db = new Stub();
        db.Prompts.Enqueue(new() { Items = [Prompt("intro", "user"), Prompt("foreign", "other")], NextToken = "p2" });
        db.Prompts.Enqueue(new() { Items = [], NextToken = "p3" });
        db.Prompts.Enqueue(new() { Items = [Prompt("paid", "owner@example.test"), Prompt("intro", "user"), Prompt("other-project", "user", "elsewhere")] });
        db.Images["intro"] = new([new() { Items = [Image("concept", "intro")] }]);
        db.Images["paid"] = new([
            new() { Items = [Image("main", "paid")], NextToken = "i2" },
            new() { Items = [], NextToken = "i3" },
            new() { Items = [Image("secondary", "paid"), Image("main", "paid"), Image("unowned", "foreign"), Image("pending", "paid", "")] }
        ]);

        var result = await GenProjectImagesQuery.LoadAsync(db, "user", "cid");

        Assert.IsTrue(result.HasOwnedPrompts);
        CollectionAssert.AreEquivalent(new[] { "concept", "main", "secondary", "pending" }, result.Images.Select(i => i.Id).ToArray());
        Assert.AreEqual("https://example.test/secondary.png", result.Images.Single(i => i.Id == "secondary").ImageUrl);
        CollectionAssert.AreEqual(new string?[] { null, "p2", "p3" }, db.PromptTokens);
        Assert.AreEqual(4, db.ImageRequests);
    }

    [TestMethod]
    public async Task NoOwnedPrompt_DoesNotReadAnyImages()
    {
        using var db = new Stub();
        db.Prompts.Enqueue(new() { Items = [Prompt("foreign", "other")] });
        var result = await GenProjectImagesQuery.LoadAsync(db, "user", "cid");
        Assert.IsFalse(result.HasOwnedPrompts);
        Assert.AreEqual(0, result.Images.Count);
        Assert.AreEqual(0, db.ImageRequests);
    }

    private static Dictionary<string, AttributeValue> Prompt(string id, string user, string cid = "cid") => new()
    {
        ["Id"] = new() { S = id }, ["UserId"] = new() { S = user }, ["CompanyIdeaId"] = new() { S = cid }
    };
    private static Dictionary<string, AttributeValue> Image(string id, string prompt, string? url = null) => new()
    {
        ["Id"] = new() { S = id }, ["PromptId"] = new() { S = prompt },
        ["ImageUrl"] = new() { S = url ?? $"https://example.test/{id}.png" }
    };

    private sealed class Stub() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public Queue<ExecuteStatementResponse> Prompts { get; } = new();
        public Dictionary<string, Queue<ExecuteStatementResponse>> Images { get; } = new();
        public List<string?> PromptTokens { get; } = [];
        public int ImageRequests { get; private set; }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("user", request.Key["Id"].S);
            return Task.FromResult(new GetItemResponse { Item = new()
            {
                ["Id"] = new() { S = "user" }, ["Username"] = new() { S = "owner@example.test" }
            } });
        }
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            Assert.IsTrue(request.ConsistentRead, "A saved fallback must be immediately visible on canonical reload.");
            if (request.Statement.Contains("FROM Prompt"))
            {
                StringAssert.Contains(request.Statement, "(UserId = ? OR UserId = ?) AND CompanyIdeaId = ?");
                CollectionAssert.AreEqual(new[] { "user", "owner@example.test", "cid" }, request.Parameters.Select(p => p.S).ToArray());
                PromptTokens.Add(request.NextToken);
                return Task.FromResult(Prompts.Dequeue());
            }
            Assert.AreEqual("SELECT * FROM ImageIdeas WHERE PromptId = ?", request.Statement);
            ImageRequests++;
            return Task.FromResult(Images[request.Parameters.Single().S].Dequeue());
        }
    }
}

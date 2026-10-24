using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelCompanyLookupTests
{
    [TestMethod]
    public async Task ExplicitProjectId_TakesPriorityOverLegacyDetails()
    {
        using var client = new LookupClient(new ExecuteStatementResponse() { Items = [Company("browser")] });
        var matches = await FunnelCompanyLookup.FindAsync(client, "browser", "project-id", "old name", "old industry");
        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual("SELECT * FROM \"CompanyIndustry\" WHERE \"Id\" = ?", client.Statement);
        CollectionAssert.AreEqual(new[] { "project-id" }, client.Parameters);
    }

    [TestMethod]
    public async Task LegacyUrl_MatchesNameAndIndustryUsingParameters()
    {
        using var client = new LookupClient(new ExecuteStatementResponse() { Items = [Company("browser")] });
        await FunnelCompanyLookup.FindAsync(client, "browser", null, "A's Company", "Design");
        Assert.AreEqual("SELECT * FROM \"CompanyIndustry\" WHERE \"Local\" = ? AND \"Name\" = ? AND \"Industry\" = ?", client.Statement);
        CollectionAssert.AreEqual(new[] { "browser", "A's Company", "Design" }, client.Parameters);
    }

    [TestMethod]
    public async Task DuplicateOnLaterPage_RemainsAmbiguous()
    {
        using var client = new LookupClient(
            new() { Items = [Company("browser")], NextToken = "next" },
            new() { Items = [Company("browser")] });
        var matches = await FunnelCompanyLookup.FindAsync(client, "browser", null, "Name", "Industry");
        Assert.AreEqual(2, matches.Count);
        CollectionAssert.AreEqual(new string?[] { null, "next" }, client.Tokens.ToArray());
    }

    [DataTestMethod]
    [DataRow("browser", "browser", true)]
    [DataRow("canonical-id", "browser", true)]
    [DataRow("OWNER@EXAMPLE.COM", "browser", true)]
    [DataRow("another-user", "browser", false)]
    [DataRow("browser", "another-browser", false)]
    [DataRow("", "browser", false)]
    [DataRow("canonical-id", "", true)]
    [DataRow("browser", "", false)]
    [DataRow("another-user", "", false)]
    [DataRow("canonical-id", "changed-browser", true)]
    [DataRow("OWNER@EXAMPLE.COM", "changed-browser", true)]
    [DataRow("another-user", "changed-browser", false)]
    public void Claim_RequiresMatchingBrowserAndCompatibleOwner(string owner, string local, bool allowed)
    {
        Assert.AreEqual(allowed, FunnelCompanyLookup.CanClaim(Company(owner), local, "canonical-id", "owner@example.com"));
    }

    [TestMethod]
    public async Task MissingBrowserIdentity_RequiresProjectIdAndStillChecksOwnership()
    {
        using var client = new LookupClient(new ExecuteStatementResponse { Items = [Company("canonical-id")] });
        var result = await FunnelCompanyLookup.FindAsync(client, "", "project-id", null, null);
        Assert.AreEqual("SELECT * FROM \"CompanyIndustry\" WHERE \"Id\" = ?", client.Statement);
        CollectionAssert.AreEqual(new[] { "project-id" }, client.Parameters);
        Assert.IsTrue(FunnelCompanyLookup.CanClaim(result[0], "", "canonical-id", "owner@example.com"));
        Assert.AreEqual(0, (await FunnelCompanyLookup.FindAsync(client, "", null, null, null)).Count);
    }
    private static Dictionary<string, AttributeValue> Company(string owner) => new()
    {
        ["Local"] = new() { S = "browser" },
        ["UserId"] = new() { S = owner }
    };

    private sealed class LookupClient(params ExecuteStatementResponse[] responses) : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        private readonly Queue<ExecuteStatementResponse> _responses = new(responses);
        public string? Statement;
        public string[] Parameters = [];
        public List<string?> Tokens = [];

        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            Assert.IsTrue(request.ConsistentRead);
            Statement = request.Statement;
            Parameters = request.Parameters.Select(value => value.S).ToArray();
            Tokens.Add(request.NextToken);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

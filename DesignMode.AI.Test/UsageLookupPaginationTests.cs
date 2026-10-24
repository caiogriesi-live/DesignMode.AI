using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class UsageLookupPaginationTests
{
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Lookup_SearchesBeyondEmptyFilteredPage(bool found)
    {
        using var client = new ScanClient(found);
        Assert.AreEqual(found ? "usage-id" : null, await AIGenerationUsageStore.FindUsageIdAsync(client, "job", "VideoGeneration"));
        Assert.AreEqual(2, client.Calls);
    }

    private sealed class ScanClient(bool found) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public int Calls;
        public override Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
        {
            Assert.IsTrue(request.ConsistentRead);
            Assert.AreEqual("job", request.ExpressionAttributeValues[":job"].S);
            Assert.AreEqual("VideoGeneration", request.ExpressionAttributeValues[":operation"].S);
            if (++Calls == 1) return Task.FromResult(new ScanResponse { Items = [], LastEvaluatedKey = new() { ["Id"] = new() { S = "next" } } });
            Assert.AreEqual("next", request.ExclusiveStartKey["Id"].S);
            return Task.FromResult(new ScanResponse { Items = found ? [new() { ["Id"] = new() { S = "usage-id" } }] : [] });
        }
    }
}

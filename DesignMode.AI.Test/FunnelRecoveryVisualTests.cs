using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryVisualTests
{
    private const string ImageUrl = "https://cdn.designmode.ai/selected.png?a=1&b=2";

    [DataTestMethod]
    [DataRow("/init/mainconcept")]
    [DataRow("/init/funnelend")]
    public void PreviewLinks_PreserveEntireFunnelEndQuery(string page)
    {
        const string query = "i=1&id=owned-invite&e=YWJjJTNE%3D&eid=eHl6%2B%2F%3D";
        var destination = "/init/funnelend?" + query;
        var expected = "https://designmode.ai/login?returnUrl=" + Uri.EscapeDataString(destination);
        var messages = new[] { FunnelRecoveryEmail.Create("https://designmode.ai", page + "?" + query, selectedImageUrl: ImageUrl) }
            .Concat(Enum.GetValues<FunnelRecoveryStep>().Select(step => FunnelRecoveryEmail.CreateReminder(
                step, "https://designmode.ai", page + "?" + query, selectedImageUrl: ImageUrl)));
        foreach (var message in messages)
        {
            Assert.AreEqual(expected, message.ResumeUrl);
            var loginQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(message.ResumeUrl).Query);
            Assert.AreEqual(destination, loginQuery["returnUrl"].ToString());
            StringAssert.Contains(message.Text, expected);
            var links = System.Text.RegularExpressions.Regex.Matches(message.Html, "href=\"([^\"]+)\"")
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
            Assert.AreEqual(3, links.Count(link => link == expected), "Hero, Outlook fallback, and button must use the identical login URL with FunnelEnd returnUrl.");
        }
    }

    [TestMethod]
    public async Task LocalRecovery_ReadOnlyActualSelection_RendersHero()
    {
        if (Environment.GetEnvironmentVariable("RUN_RECOVERY_READONLY_DIAGNOSTIC") != "true")
            Assert.Inconclusive("Opt-in read-only check of the latest existing local recovery.");
        using var client = new AmazonDynamoDBClient(new BasicAWSCredentials("fakeMyKeyId", "fakeSecretAccessKey"),
            new AmazonDynamoDBConfig { ServiceURL = "http://localhost:8000", AuthenticationRegion = "us-east-1" });
        var recoveries = new List<Dictionary<string, AttributeValue>>();
        var scan = new ScanRequest { TableName = "FunnelRecovery", ConsistentRead = true };
        do
        {
            var page = await client.ScanAsync(scan);
            recoveries.AddRange(page.Items);
            scan.ExclusiveStartKey = page.LastEvaluatedKey;
        } while (scan.ExclusiveStartKey is { Count: > 0 });
        var latest = recoveries.OrderByDescending(r => DateTimeOffset.Parse(r["VideoReadyAt"].S)).First();
        var project = (await client.GetItemAsync(new GetItemRequest { TableName = "Projects",
            Key = new() { ["Id"] = latest["ProjectId"] }, ConsistentRead = true })).Item;
        Assert.IsTrue(project["UserId"].S == latest["UserId"].S);
        Assert.IsTrue(project["OriginatingFunnelId"].S == latest["FunnelId"].S);
        var image = await FunnelRecoverySelectedImage.ResolveAsync(client, latest["UserId"].S, project);
        Assert.IsNotNull(image, "Latest local recovery must resolve its actual selected concept.");
        var email = FunnelRecoveryEmail.Create("https://designmode.ai", "/init/funnelend", selectedImageUrl: image);
        StringAssert.Contains(email.Html, "<img src=");
        Assert.IsTrue(email.Html.Contains(System.Net.WebUtility.HtmlEncode(image)));
    }

    [DataTestMethod]
    [DataRow("valid", true)]
    [DataRow("other-project", false)]
    [DataRow("other-user", false)]
    [DataRow("other-funnel", false)]
    [DataRow("ambiguous-invite", false)]
    [DataRow("unowned-selection", false)]
    public async Task MissingInviteMapping_UsesOnlyCanonicalJobImages(string scenario, bool expected)
    {
        using var client = new ImageStore { ResolveFromJobs = true, JobScenario = scenario };
        var project = Row(("Id", "project"), ("UserId", "user"), ("OriginatingFunnelId", "funnel"));
        var image = await FunnelRecoverySelectedImage.ResolveAsync(client, "user", project);
        Assert.AreEqual(expected, image is not null);
        if (expected) Assert.AreEqual(ImageUrl, image);
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Sequence_UsesResponsiveSharedLayoutAndSafeResume(int step)
    {
        var message = step < 0 ? FunnelRecoveryEmail.Create("https://designmode.ai", "/init/mainconcept", selectedImageUrl: ImageUrl) :
            FunnelRecoveryEmail.CreateReminder((FunnelRecoveryStep)step, "https://designmode.ai", "/init/mainconcept", selectedImageUrl: ImageUrl);
        StringAssert.Contains(message.Html, "Design<span");
        StringAssert.Contains(message.Html, "max-width:664px");
        StringAssert.Contains(message.Html, "width:100%;max-width:584px;height:auto");
        StringAssert.Contains(message.Html, "max-width:400px");
        StringAssert.Contains(message.Html, "a=1&amp;b=2");
        StringAssert.Contains(message.Html, "<h1");
        StringAssert.Contains(message.Html, "email-play-overlay");
        StringAssert.Contains(message.Html, "align=\"center\" valign=\"middle\" style=\"background:");
        Assert.IsFalse(message.Html.Contains(" background="), "Legacy HTML backgrounds can tile when repeat styles are stripped.");
        StringAssert.Contains(message.Html, "no-repeat center center");
        StringAssert.Contains(message.Html, "background-repeat:no-repeat!important");
        StringAssert.Contains(message.Html, "background-position:center center");
        StringAssert.Contains(message.Html, "background-size:contain");
        StringAssert.Contains(message.Html, "padding:128px 0;text-align:center");
        Assert.IsFalse(message.Html.Contains("position:absolute"));
        StringAssert.Contains(message.Html, "border:2px solid #ffffff");
        StringAssert.Contains(message.Html, "background-color:rgba(17,19,33,0.25)");
        StringAssert.Contains(message.Html, "email-play-triangle");
        StringAssert.Contains(message.Html, "border-top:17px solid transparent;border-bottom:17px solid transparent;border-left:28px solid #ffffff");
        Assert.IsFalse(message.Html.Contains("&#9654;"));
        Assert.IsFalse(message.Html.Contains("\u25b6"));
        StringAssert.Contains(message.Html, message.ResumeUrl);
        Assert.AreEqual("https://designmode.ai/login?returnUrl=%2Finit%2Fmainconcept", message.ResumeUrl);
        StringAssert.Contains(message.Text, message.ResumeUrl);
        Assert.IsFalse(message.Html.Contains("<script"));
        Assert.IsFalse(message.Html.Contains("stylesheet"));
        Assert.IsFalse(message.Html.Contains("UserId"));
        Assert.IsFalse(message.Html.Contains("ProjectId"));
        Assert.IsFalse(message.Html.Contains("CompanyIdeaId"));
        var withoutImage = step < 0 ? FunnelRecoveryEmail.Create("https://designmode.ai", null) :
            FunnelRecoveryEmail.CreateReminder((FunnelRecoveryStep)step, "https://designmode.ai", null);
        Assert.IsFalse(withoutImage.Html.Contains("<img"));
        Assert.IsFalse(withoutImage.Html.Contains("email-play-overlay"));
        StringAssert.Contains(withoutImage.Html, withoutImage.ResumeUrl);
    }

    [TestMethod]
    public void Sequence_CopyRemainsDistinct()
    {
        var messages = Enum.GetValues<FunnelRecoveryStep>().Select(step =>
            FunnelRecoveryEmail.CreateReminder(step, "https://designmode.ai", null)).ToList();
        Assert.AreEqual(3, messages.Select(m => m.Subject).Distinct().Count());
        Assert.AreEqual(3, messages.Select(m => m.Text).Distinct().Count());
        StringAssert.Contains(messages[2].Text, "final reminder");
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("http://cdn.designmode.ai/a.png")]
    [DataRow("https://localhost/a.png")]
    [DataRow("https://127.0.0.1/a.png")]
    [DataRow("https://10.0.0.1/a.png")]
    [DataRow("https://storage.internal/a.png")]
    [DataRow("https://azurite.example.com/a.png")]
    [DataRow("https://functions.azurewebsites.net/api/image")]
    [DataRow("https://user:secret@example.com/a.png")]
    [DataRow("C:/images/a.png")]
    public void UnsafeOrMissingImage_IsOmitted(string? url)
    {
        Assert.IsNull(FunnelRecoverySelectedImage.PublicUrl(url));
        Assert.IsFalse(FunnelRecoveryEmail.Create("https://designmode.ai", null, selectedImageUrl: url).Html.Contains("<img"));
    }

    [TestMethod]
    public async Task SelectedImage_UsesExactPersistedSelectionAndOwnedProject()
    {
        using var client = new ImageStore();
        var image = await FunnelRecoverySelectedImage.ResolveAsync(client, "user", Project());
        Assert.AreEqual(ImageUrl, image);
        Assert.AreEqual("selected", client.ImageRead);
        Assert.IsTrue(client.Reads.All(r => r.ConsistentRead == true));
        // The backing store also has earlier and newer unselected images.
        Assert.AreNotEqual(client.Images["first"]["ImageUrl"].S, image);
        Assert.AreNotEqual(client.Images["newest"]["ImageUrl"].S, image);
    }

    [DataTestMethod]
    [DataRow("other-project")]
    [DataRow("other-owner")]
    [DataRow("other-invite")]
    [DataRow("missing-image")]
    [DataRow("missing-selection")]
    [DataRow("missing-link")]
    [DataRow("failure")]
    [DataRow("ambiguous")]
    public async Task InvalidOrUnavailableSelection_FallsBack(string scenario)
    {
        using var client = new ImageStore();
        var project = Project();
        switch (scenario)
        {
            case "other-project": client.Images["selected"]["InviteId"].S = "project-b-invite"; break;
            case "other-owner": project["UserId"].S = "other-user"; break;
            case "other-invite": project["LegacyInviteId"].S = "other-invite"; break;
            case "missing-image": client.Images.Remove("selected"); break;
            case "missing-selection": client.Invite.Remove("ImageId"); break;
            case "missing-link": project.Remove("LegacyInviteId"); break;
            case "failure": client.Fail = true; break;
            case "ambiguous": client.Ambiguous = true; break;
        }
        Assert.IsNull(await FunnelRecoverySelectedImage.ResolveAsync(client, "user", project));
    }

    private static Dictionary<string, AttributeValue> Project() => Row(("UserId", "user"),
        ("LegacyInviteId", "invite"));
    private static Dictionary<string, AttributeValue> Row(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => new AttributeValue { S = f.Value });

    private sealed class ImageStore() : AmazonDynamoDBClient(new AnonymousAWSCredentials(),
        new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:8000" })
    {
        public Dictionary<string, AttributeValue> Invite = Row(("Id", "invite"), ("ImageId", "selected"));
        public Dictionary<string, Dictionary<string, AttributeValue>> Images = new()
        {
            ["first"] = Row(("ImageId", "first"), ("InviteId", "invite"), ("ImageUrl", "https://cdn.designmode.ai/first.png")),
            ["selected"] = Row(("ImageId", "selected"), ("InviteId", "invite"), ("ImageUrl", ImageUrl)),
            ["newest"] = Row(("ImageId", "newest"), ("InviteId", "invite"), ("ImageUrl", "https://cdn.designmode.ai/newest.png"))
        };
        public List<ExecuteStatementRequest> Reads = [];
        public string? ImageRead;
        public bool Fail, Ambiguous;
        public bool ResolveFromJobs;
        public string JobScenario = "valid";
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetItemResponse { Item = Row(("Id", "user"), ("Username", "legacy@example.com")) });
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Optional image storage unavailable");
            Reads.Add(request);
            List<Dictionary<string, AttributeValue>> rows;
            if (request.Statement.Contains("FROM \"DesignModeJobs\""))
            {
                Assert.IsTrue(ResolveFromJobs);
                rows = [Row(("UserId", JobScenario == "other-user" ? "foreign" : "user"),
                    ("ProjectId", JobScenario == "other-project" ? "foreign" : "project"),
                    ("FunnelId", JobScenario == "other-funnel" ? "foreign" : "funnel"),
                    ("ImageId", JobScenario == "unowned-selection" ? "first" : "selected"), ("PreviewKind", "Social"))];
            }
            else if (request.Statement.Contains("FROM InviteClientImages WHERE ImageId"))
            {
                rows = [Row(("ImageId", request.Parameters[0].S), ("InviteId", "invite"))];
                if (JobScenario == "ambiguous-invite") rows.Add(Row(("ImageId", request.Parameters[0].S), ("InviteId", "foreign")));
            }
            else if (request.Statement.Contains("FROM InviteClientImages"))
            {
                Assert.AreEqual("invite", request.Parameters[0].S);
                ImageRead = request.Parameters[1].S;
                rows = Images.TryGetValue(ImageRead, out var image) ? [image] : [];
            }
            else if (request.Statement.Contains("FROM InviteClient WHERE")) rows = request.Parameters[0].S == "invite" ? [Invite] : [];
            else throw new AssertFailedException("Recovery must use FunnelEnd's InviteClientImages source, not ImageIdeas or Prompt.");
            // Empty first page proves pagination does not imply an absent selection.
            if (request.NextToken is null) return Task.FromResult(new ExecuteStatementResponse { Items = [], NextToken = "next" });
            if (Ambiguous && rows.Count > 0) rows.Add(rows[0]);
            return Task.FromResult(new ExecuteStatementResponse { Items = rows });
        }
    }
}

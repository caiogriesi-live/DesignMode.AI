using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DesignMode.AI.Server.Services;

public sealed class AttributionStore
{
    private readonly ILogger<AttributionStore> _logger;
    private readonly IAdvertisingConsentReader _consent;
    private readonly Func<AmazonDynamoDBClient> _clients;

    public AttributionStore(ILogger<AttributionStore> logger, IAdvertisingConsentReader? consent = null,
        Func<AmazonDynamoDBClient>? clients = null)
    { _logger = logger; _consent = consent ?? new AdvertisingConsentStore(); _clients = clients ?? DynamoDbClientFactory.CreateClient; }

    public sealed record CheckoutCreated(string CheckoutAttemptId, string AttributionSnapshotId);
    public sealed record BindResult(bool Success, string? ErrorCode = null);
    public AmazonDynamoDBClient CreateClient()
    {
        return _clients();
    }

    public async Task<AcquisitionCaptureResponse> CaptureAsync(AcquisitionCaptureRequest value)
    {
        var total = Stopwatch.StartNew();
        using var client = CreateClient();
        var funnelId = IsUuid(value.FunnelId) ? value.FunnelId! : Guid.NewGuid().ToString();
        var acquisitionId = Guid.NewGuid().ToString();
        var touchId = acquisitionId;
        var now = DateTime.UtcNow.ToString("o");

        _logger.LogInformation("[ATTRIBUTION SERVER] GetItem Funnels START");
        var operation = Stopwatch.StartNew();
        var funnel = await client.GetItemAsync("Funnels", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = funnelId } });
        _logger.LogInformation("[ATTRIBUTION SERVER] GetItem Funnels COMPLETE {ElapsedMs} ms", operation.ElapsedMilliseconds);
        var funnelItem = funnel?.Item;
        if (funnelItem is { Count: > 0 } &&
            (funnelItem.ContainsKey("UserId") || funnelItem.ContainsKey("ProjectId")))
        {
            // A public landing page cannot prove ownership of a previous account
            // project, even in the same browser. Start a fresh anonymous funnel
            // instead of exposing or rewriting the account's attribution record.
            _logger.LogInformation("[ATTRIBUTION SERVER] Existing funnel is already bound; starting a fresh anonymous funnel");
            funnelId = Guid.NewGuid().ToString();
            funnelItem = null;
        }
        if (funnelItem is null || funnelItem.Count == 0)
        {
            _logger.LogInformation("[ATTRIBUTION SERVER] PutItem Funnels START");
            operation.Restart();
            await client.PutItemAsync("Funnels", new Dictionary<string, AttributeValue>
            {
                ["Id"] = new() { S = funnelId }, ["VisitorId"] = new() { S = value.VisitorId },
                ["SessionId"] = new() { S = value.SessionId }, ["AcquisitionId"] = new() { S = acquisitionId },
                ["Status"] = new() { S = "Started" }, ["CreatedAt"] = new() { S = now }, ["UpdatedAt"] = new() { S = now }
            });
            _logger.LogInformation("[ATTRIBUTION SERVER] PutItem Funnels COMPLETE {ElapsedMs} ms", operation.ElapsedMilliseconds);
        }
        else
        {
            acquisitionId = funnelItem.TryGetValue("AcquisitionId", out var saved) && !string.IsNullOrWhiteSpace(saved?.S) ? saved.S : acquisitionId;
            touchId = Guid.NewGuid().ToString();

            // An anonymous browser may resume an unclaimed funnel with rotated
            // visitor/session IDs. Refresh those identifiers only while the funnel
            // is still unowned and has no project; never rewrite an account binding.
            if (!funnelItem.ContainsKey("UserId") && !funnelItem.ContainsKey("ProjectId"))
            {
                try
                {
                    await client.UpdateItemAsync(new UpdateItemRequest
                    {
                        TableName = "Funnels",
                        Key = new() { ["Id"] = new() { S = funnelId } },
                        ConditionExpression = "attribute_not_exists(UserId) AND attribute_not_exists(ProjectId)",
                        UpdateExpression = "SET VisitorId = :visitorId, SessionId = :sessionId, AcquisitionId = if_not_exists(AcquisitionId, :acquisition), UpdatedAt = :updated",
                        ExpressionAttributeValues = new()
                        {
                            [":visitorId"] = new() { S = value.VisitorId },
                            [":sessionId"] = new() { S = value.SessionId },
                            [":acquisition"] = new() { S = acquisitionId },
                            [":updated"] = new() { S = now }
                        }
                    });
                }
                catch (ConditionalCheckFailedException)
                {
                    _logger.LogInformation("[ATTRIBUTION SERVER] Anonymous funnel identity refresh skipped after ownership or project assignment");
                }

                var refreshed = await client.GetItemAsync("Funnels", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = funnelId } });
                if (!string.IsNullOrWhiteSpace(refreshed?.Item?.GetValueOrDefault("AcquisitionId")?.S))
                    acquisitionId = refreshed.Item["AcquisitionId"].S;
            }
        }

        var item = new Dictionary<string, AttributeValue>
        {
            ["Id"] = new() { S = touchId }, ["OriginalAcquisitionId"] = new() { S = acquisitionId }, ["VisitorId"] = new() { S = value.VisitorId },
            ["SessionId"] = new() { S = value.SessionId }, ["FunnelId"] = new() { S = funnelId },
            ["TouchType"] = new() { S = IsPaid(value) ? "Paid" : "DirectOrReferral" }, ["CreatedAt"] = new() { S = now }
        };
        // New touches only. Acquisition remains independent of Google consent; no old rows are rewritten.
        item["AdvertisingMeasurementEnabled"] = new() { BOOL = AdvertisingMeasurementPolicy.AdvertisingMeasurementEnabled };
        item["AdvertisingMeasurementPolicyVersion"] = new() { S = AdvertisingMeasurementPolicy.Version };
        item["GoogleAdvertisingConsent"] = new() { BOOL = true };
        item["GoogleAdvertisingPolicyVersion"] = new() { S = AdvertisingMeasurementPolicy.GooglePolicyVersion };
        Add(item, "Gclid", value.Gclid); Add(item, "Gbraid", value.Gbraid); Add(item, "Wbraid", value.Wbraid);
        Add(item, "UtmSource", value.UtmSource); Add(item, "UtmMedium", value.UtmMedium); Add(item, "UtmCampaign", value.UtmCampaign);
        Add(item, "UtmTerm", value.UtmTerm); Add(item, "UtmContent", value.UtmContent); Add(item, "CampaignId", value.CampaignId);
        Add(item, "AdGroupId", value.AdGroupId); Add(item, "AdId", value.AdId); Add(item, "Keyword", value.Keyword);
        Add(item, "MatchType", value.MatchType); Add(item, "Device", value.Device); Add(item, "Network", value.Network);
        Add(item, "LandingPage", value.LandingPage); Add(item, "LandingVariant", value.LandingVariant); Add(item, "Referrer", value.Referrer);
        _logger.LogInformation("[ATTRIBUTION SERVER] PutItem AttributionTouches START");
        operation.Restart();
        await client.PutItemAsync("AttributionTouches", item);
        _logger.LogInformation("[ATTRIBUTION SERVER] PutItem AttributionTouches COMPLETE {ElapsedMs} ms", operation.ElapsedMilliseconds);
        _logger.LogInformation("[ATTRIBUTION SERVER] CaptureSocialAcquisition COMPLETE {ElapsedMs} ms", total.ElapsedMilliseconds);

        return new() { VisitorId = value.VisitorId, SessionId = value.SessionId, FunnelId = funnelId, AcquisitionId = acquisitionId };
    }

    public async Task<ProjectContextResponse?> CreateProjectAsync(CreateSocialProjectRequest value)
    {
        var total = Stopwatch.StartNew();
        using var client = CreateClient();
        var funnel = await client.GetItemAsync("Funnels", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = value.FunnelId } });
        var funnelItem = funnel?.Item;
        if (funnelItem is null || funnelItem.Count == 0 || !funnelItem.TryGetValue("VisitorId", out var visitor) || visitor?.S != value.VisitorId) return null;
        var funnelAcquisitionId = funnelItem.TryGetValue("AcquisitionId", out var acquisition) ? acquisition?.S : null;
        if (string.IsNullOrWhiteSpace(funnelAcquisitionId)) return null;
        if (funnelItem.TryGetValue("ProjectId", out var existing) && !string.IsNullOrWhiteSpace(existing?.S))
            return new() { ProjectId = existing.S, FunnelId = value.FunnelId, AcquisitionId = funnelAcquisitionId };

        var projectId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");
        var project = new Dictionary<string, AttributeValue>
        {
            ["Id"] = new() { S = projectId }, ["OriginatingFunnelId"] = new() { S = value.FunnelId },
            ["AcquisitionId"] = new() { S = funnelAcquisitionId }, ["IsOriginalAcquisitionProject"] = new() { BOOL = true },
            ["CreatedAt"] = new() { S = now }, ["UpdatedAt"] = new() { S = now }
        };
        Add(project, "LegacyCompanyIndustryId", value.LegacyCompanyIndustryId); Add(project, "LegacyCompanyIdeaId", value.LegacyCompanyIdeaId); Add(project, "LegacyInviteId", value.LegacyInviteId);
        _logger.LogInformation("[ATTRIBUTION SERVER] PutItem Projects START");
        var operation = Stopwatch.StartNew();
        await client.PutItemAsync("Projects", project);
        _logger.LogInformation("[ATTRIBUTION SERVER] PutItem Projects COMPLETE {ElapsedMs} ms", operation.ElapsedMilliseconds);
        await client.UpdateItemAsync(new UpdateItemRequest { TableName = "Funnels", Key = new() { ["Id"] = new() { S = value.FunnelId } },
            UpdateExpression = "SET ProjectId = :projectId, UpdatedAt = :updated", ExpressionAttributeValues = new() { [":projectId"] = new() { S = projectId }, [":updated"] = new() { S = now } } });
        _logger.LogInformation("[ATTRIBUTION SERVER] CreateSocialProject COMPLETE {ElapsedMs} ms", total.ElapsedMilliseconds);
        return new() { ProjectId = projectId, FunnelId = value.FunnelId, AcquisitionId = funnelAcquisitionId };
    }

    public async Task<string?> ResolveUserIdAsync(string username)
    {
        using var client = CreateClient();
        var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT Id FROM Users WHERE Username = ?", Parameters = [new AttributeValue { S = username }]
        });
        return result?.Items?.FirstOrDefault()?.GetValueOrDefault("Id")?.S;
    }

    public async Task<string?> ResolveClaimedUserIdAsync(string userId)
    {
        if (!Guid.TryParse(userId, out _)) return null;
        using var client = CreateClient();
        var result = await client.GetItemAsync("Users", new() { ["Id"] = new() { S = userId } });
        return result.Item is { Count: > 0 } ? userId : null;
    }

    public async Task<bool> ValidateGenerationContextAsync(string userId, string projectId, string funnelId)
    {
        using var client = CreateClient();
        var result = await client.GetItemAsync("Projects", new() { ["Id"] = new() { S = projectId } });
        var item = result?.Item;
        return item is { Count: > 0 } && item.GetValueOrDefault("UserId")?.S == userId && item.GetValueOrDefault("OriginatingFunnelId")?.S == funnelId;
    }

    public async Task<bool> UserOwnsProjectAsync(string userId, string projectId)
    {
        if (!Guid.TryParse(projectId, out _)) return false;
        using var client = CreateClient();
        var result = await client.GetItemAsync("Projects", new() { ["Id"] = new() { S = projectId } });
        return result.Item is { Count: > 0 } && result.Item.GetValueOrDefault("UserId")?.S == userId;
    }

    public async Task<ProjectContextResponse?> GetOwnedGenerationProjectAsync(string userId, string projectId)
    {
        using var client = CreateClient();
        return await GenerationProjectAuthorization.ResolveProjectAsync(client, userId, projectId);
    }

    public async Task<ProjectContextResponse?> GetOwnedProjectContextAsync(string userId, string projectId)
    {
        if (!Guid.TryParse(projectId, out _)) return null;
        using var client = CreateClient();
        var result = await client.GetItemAsync("Projects", new() { ["Id"] = new() { S = projectId } });
        var item = result.Item;
        if (item is not { Count: > 0 } || item.GetValueOrDefault("UserId")?.S != userId) return null;
        return new ProjectContextResponse { ProjectId = projectId, FunnelId = item.GetValueOrDefault("OriginatingFunnelId")?.S ?? string.Empty, AcquisitionId = item.GetValueOrDefault("AcquisitionId")?.S, CompanyIndustryId = item.GetValueOrDefault("LegacyCompanyIndustryId")?.S };
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(string userId)
    {
        using var client = CreateClient();
        var projects = new List<ProjectSummary>();
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var page = await client.ScanAsync(new ScanRequest
            {
                TableName = "Projects",
                ExclusiveStartKey = cursor,
                FilterExpression = "UserId = :userId",
                ExpressionAttributeValues = new() { [":userId"] = new() { S = userId } },
                ProjectionExpression = "Id, #name, Industry, ThumbnailUrl, UpdatedAt, CreatedAt, OriginatingFunnelId, LegacyCompanyIndustryId, #status",
                ExpressionAttributeNames = new() { ["#name"] = "Name", ["#status"] = "Status" }
            });
            projects.AddRange((page.Items ?? []).Select(ToProjectSummary));
            cursor = page.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });

        return projects.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    private static ProjectSummary ToProjectSummary(Dictionary<string, AttributeValue> item)
    {
        var updated = item.GetValueOrDefault("UpdatedAt")?.S ?? item.GetValueOrDefault("CreatedAt")?.S;
        return new ProjectSummary
        {
            ProjectId = item.GetValueOrDefault("Id")?.S ?? string.Empty,
            Name = item.GetValueOrDefault("Name")?.S ?? "Untitled project",
            Industry = item.GetValueOrDefault("Industry")?.S,
            ThumbnailUrl = item.GetValueOrDefault("ThumbnailUrl")?.S,
            UpdatedAt = DateTime.TryParse(updated, out var timestamp) ? timestamp : DateTime.UnixEpoch,
            Status = item.GetValueOrDefault("Status")?.S ?? "In progress",
            FunnelId = item.GetValueOrDefault("OriginatingFunnelId")?.S ?? string.Empty,
            CompanyIndustryId = item.GetValueOrDefault("LegacyCompanyIndustryId")?.S
        };
    }

    public async Task<bool> BindAsync(BindIdentityRequest value, string userId) =>
        (await BindWithResultAsync(value, userId)).Success;

    public async Task<BindResult> BindWithResultAsync(BindIdentityRequest value, string userId)
    {
        using var client = CreateClient();
        _logger.LogInformation("[ATTRIBUTION SERVER] Funnel lookup started");
        var funnel = await client.GetItemAsync("Funnels", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = value.FunnelId } });
        var funnelItem = funnel?.Item;
        _logger.LogInformation("[ATTRIBUTION SERVER] Funnel lookup complete");
        if (funnelItem is null || funnelItem.Count == 0) return new(false, "funnel_not_found");
        if (funnelItem.GetValueOrDefault("ProjectId")?.S != value.ProjectId) return new(false, "funnel_project_mismatch");
        var sessionResumed = funnelItem.GetValueOrDefault("SessionId")?.S != value.SessionId;
        var visitorResumed = funnelItem.GetValueOrDefault("VisitorId")?.S != value.VisitorId;
        if (sessionResumed || visitorResumed)
            _logger.LogInformation("[ATTRIBUTION SERVER] Browser attribution identifiers rotated; canonical funnel/project relationship remains valid");

        _logger.LogInformation("[ATTRIBUTION SERVER] Project lookup started");
        var project = await client.GetItemAsync("Projects", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = value.ProjectId } });
        var projectItem = project?.Item;
        _logger.LogInformation("[ATTRIBUTION SERVER] Project lookup complete");
        if (projectItem is null || projectItem.Count == 0) return new(false, "project_not_found");
        // Anonymous-to-account binding may claim unowned records, never transfer an
        // existing account's project through stale or copied browser attribution.
        if (new[] { funnelItem, projectItem }.Any(item =>
            !string.IsNullOrWhiteSpace(item.GetValueOrDefault("UserId")?.S) &&
            item.GetValueOrDefault("UserId")?.S != userId)) return new(false, "project_ownership_failure");
        if (projectItem.GetValueOrDefault("OriginatingFunnelId")?.S != value.FunnelId) return new(false, "project_funnel_mismatch");
        var acquisitionId = projectItem.GetValueOrDefault("AcquisitionId")?.S;
        if (string.IsNullOrWhiteSpace(acquisitionId)) return new(false, "project_acquisition_missing");
        var now = DateTime.UtcNow.ToString("o");
        _logger.LogInformation("[ATTRIBUTION SERVER] Funnel UserId update started");
        await client.UpdateItemAsync(new UpdateItemRequest { TableName = "Funnels", Key = new() { ["Id"] = new() { S = value.FunnelId } },
            ConditionExpression = "attribute_not_exists(UserId) OR UserId = :userId",
            UpdateExpression = "SET UserId = :userId, ProjectId = :projectId, VisitorId = :visitorId, SessionId = :sessionId, #status = :status, UpdatedAt = :updated",
            ExpressionAttributeNames = new() { ["#status"] = "Status" }, ExpressionAttributeValues = new() { [":userId"] = new() { S = userId }, [":projectId"] = new() { S = value.ProjectId }, [":visitorId"] = new() { S = value.VisitorId }, [":sessionId"] = new() { S = value.SessionId }, [":status"] = new() { S = "Authenticated" }, [":updated"] = new() { S = now } } });
        _logger.LogInformation("[ATTRIBUTION SERVER] Funnel UserId update complete");
        _logger.LogInformation("[ATTRIBUTION SERVER] Project UserId update started");
        await client.UpdateItemAsync(new UpdateItemRequest { TableName = "Projects", Key = new() { ["Id"] = new() { S = value.ProjectId } },
            ConditionExpression = "attribute_not_exists(UserId) OR UserId = :userId",
            UpdateExpression = "SET UserId = :userId, UpdatedAt = :updated", ExpressionAttributeValues = new() { [":userId"] = new() { S = userId }, [":updated"] = new() { S = now } } });
        _logger.LogInformation("[ATTRIBUTION SERVER] Project UserId update complete");
        await SaveLegacyMappingsAsync(client, value.ProjectId, value.LegacyCompanyIndustryId, value.LegacyCompanyIdeaId, value.LegacyInviteId);
        _logger.LogInformation("[ATTRIBUTION SERVER] original acquisition assignment started");
        try
        {
            await client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = "Users", Key = new() { ["Id"] = new() { S = userId } },
                UpdateExpression = "SET OriginalAcquisitionId = :acquisition, OriginalAcquisitionProjectId = :project",
                ConditionExpression = "attribute_not_exists(OriginalAcquisitionId) AND attribute_not_exists(OriginalAcquisitionProjectId)",
                ExpressionAttributeValues = new() { [":acquisition"] = new() { S = acquisitionId }, [":project"] = new() { S = value.ProjectId } }
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // A returning user's original acquisition is intentionally immutable.
        }
        _logger.LogInformation("[ATTRIBUTION SERVER] original acquisition assignment complete");
        return new(true);
    }

    public async Task<ProjectContextResponse> CreateAdditionalProjectAsync(AdditionalProjectRequest value, string userId)
    {
        using var client = CreateClient();
        var projectId = Guid.NewGuid().ToString();
        var funnelId = Guid.TryParse(value.FunnelId, out _) ? value.FunnelId! : Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");
        var item = new Dictionary<string, AttributeValue> { ["Id"] = new() { S = projectId }, ["UserId"] = new() { S = userId }, ["OriginatingFunnelId"] = new() { S = funnelId }, ["IsOriginalAcquisitionProject"] = new() { BOOL = false }, ["CreatedAt"] = new() { S = now }, ["UpdatedAt"] = new() { S = now }, ["Status"] = new() { S = "In progress" } };
        Add(item, "Name", value.Name); Add(item, "Industry", value.Industry);
        Add(item, "LegacyCompanyIndustryId", value.LegacyCompanyIndustryId); Add(item, "LegacyCompanyIdeaId", value.LegacyCompanyIdeaId); Add(item, "LegacyInviteId", value.LegacyInviteId);
        await client.PutItemAsync("Projects", item);
        await client.PutItemAsync("Funnels", new Dictionary<string, AttributeValue>
        {
            ["Id"] = new() { S = funnelId }, ["ProjectId"] = new() { S = projectId }, ["UserId"] = new() { S = userId },
            ["Status"] = new() { S = "Authenticated" }, ["CreatedAt"] = new() { S = now }, ["UpdatedAt"] = new() { S = now }
        });
        return new() { ProjectId = projectId, FunnelId = funnelId };
    }

    public async Task<ProjectContextResponse> ResolveLegacyProjectAsync(LegacyProjectResolutionRequest value, string userId)
    {
        using var client = CreateClient();
        var candidates = new[] { ("LegacyCompanyIndustryId", value.LegacyCompanyIndustryId), ("LegacyCompanyIdeaId", value.LegacyCompanyIdeaId), ("LegacyInviteId", value.LegacyInviteId) };
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x.Item2)))
        {
            var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT Id, OriginatingFunnelId FROM Projects WHERE UserId = ? AND {candidate.Item1} = ?", Parameters = [new() { S = userId }, new() { S = candidate.Item2 }] });
            var found = result?.Items?.FirstOrDefault();
            var foundProjectId = found?.GetValueOrDefault("Id")?.S;
            if (!string.IsNullOrWhiteSpace(foundProjectId)) return new() { ProjectId = foundProjectId, FunnelId = found?.GetValueOrDefault("OriginatingFunnelId")?.S ?? string.Empty };
        }
        return await CreateAdditionalProjectAsync(new AdditionalProjectRequest { LegacyCompanyIndustryId = value.LegacyCompanyIndustryId, LegacyCompanyIdeaId = value.LegacyCompanyIdeaId, LegacyInviteId = value.LegacyInviteId }, userId);
    }

    private static async Task SaveLegacyMappingsAsync(AmazonDynamoDBClient client, string projectId, string? companyIndustryId, string? companyIdeaId, string? inviteId)
    {
        var values = new Dictionary<string, AttributeValue>(); var sets = new List<string>();
        void AddSet(string name, string token, string? value) { if (!string.IsNullOrWhiteSpace(value)) { sets.Add($"{name} = {token}"); values[token] = new() { S = value }; } }
        AddSet("LegacyCompanyIndustryId", ":company", companyIndustryId); AddSet("LegacyCompanyIdeaId", ":idea", companyIdeaId); AddSet("LegacyInviteId", ":invite", inviteId);
        if (sets.Count == 0) return;
        await client.UpdateItemAsync(new UpdateItemRequest { TableName = "Projects", Key = new() { ["Id"] = new() { S = projectId } }, UpdateExpression = "SET " + string.Join(", ", sets), ExpressionAttributeValues = values });
    }

    public async Task<CheckoutCreated?> CreateCheckoutAttemptAsync(string userId, string projectId, string funnelId, string plan, decimal amount, string currency, MeasurementContext? measurement = null, string billingInterval = "")
    {
        using var client = CreateClient();
        var project = await client.GetItemAsync("Projects", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = projectId } });
        var projectItem = project?.Item;
        if (projectItem is null || projectItem.Count == 0 || projectItem.GetValueOrDefault("UserId")?.S != userId || projectItem.GetValueOrDefault("OriginatingFunnelId")?.S != funnelId) return null;
        var attemptId = Guid.NewGuid().ToString();
        var snapshotId = Guid.NewGuid().ToString();
        // Freeze last paid touch in this funnel (otherwise original acquisition). Never read browser click IDs at payment time.
        var acquisitionId = projectItem.GetValueOrDefault("AcquisitionId")?.S ?? string.Empty;
        var touches = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var page = await client.ScanAsync(new ScanRequest { TableName = "AttributionTouches", ExclusiveStartKey = cursor,
                FilterExpression = "FunnelId = :funnel", ExpressionAttributeValues = new() { [":funnel"] = new() { S = funnelId } } });
            touches.AddRange(page.Items ?? []); cursor = page.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });
        var touch = touches.Where(x => x.ContainsKey("Gclid") || x.ContainsKey("Gbraid") || x.ContainsKey("Wbraid"))
            .OrderByDescending(x => x.GetValueOrDefault("CreatedAt")?.S, StringComparer.Ordinal).FirstOrDefault()
            ?? touches.FirstOrDefault(x => x.GetValueOrDefault("Id")?.S == acquisitionId);
        // Prospective product policy is server-authoritative; browser false/missing cannot disable either measurement field.
        var safeMeasurement = CreateMeasurementContext(measurement);
        var snapshot = new AttributionSnapshot
        {
            Id = snapshotId, AcquisitionId = acquisitionId, TouchId = touch?.GetValueOrDefault("Id")?.S,
            Gclid = touch?.GetValueOrDefault("Gclid")?.S, Gbraid = touch?.GetValueOrDefault("Gbraid")?.S, Wbraid = touch?.GetValueOrDefault("Wbraid")?.S,
            Measurement = safeMeasurement,
            AdvertisingConsentEvidence = null,
            Acquisition = new[] { "UtmSource", "UtmMedium", "UtmCampaign", "UtmTerm", "UtmContent", "CampaignId", "AdGroupId", "AdId", "Keyword", "LandingPage", "LandingVariant", "Referrer" }
                .Where(k => !string.IsNullOrWhiteSpace(touch?.GetValueOrDefault(k)?.S)).ToDictionary(k => k, k => touch![k].S)
        };
        await client.PutItemAsync(new PutItemRequest { TableName = "AttributionSnapshots", ConditionExpression = "attribute_not_exists(Id)",
            Item = new() { ["Id"] = new() { S = snapshotId }, ["Data"] = new() { S = System.Text.Json.JsonSerializer.Serialize(snapshot) } } });
        await client.PutItemAsync("CheckoutAttempts", new Dictionary<string, AttributeValue>
        {
            ["Id"] = new() { S = attemptId }, ["CheckoutAttemptId"] = new() { S = attemptId }, ["UserId"] = new() { S = userId },
            ["ProjectId"] = new() { S = projectId }, ["FunnelId"] = new() { S = funnelId }, ["AttributionSnapshotId"] = new() { S = snapshotId },
            ["AcquisitionId"] = new() { S = projectItem.GetValueOrDefault("AcquisitionId")?.S ?? string.Empty }, ["Plan"] = new() { S = plan },
            ["BillingInterval"] = new() { S = billingInterval },
            ["Amount"] = new() { N = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ["Currency"] = new() { S = currency },
            ["StripeCheckoutSessionId"] = new() { S = string.Empty }, ["Status"] = new() { S = "Pending" }, ["CreatedAt"] = new() { S = DateTime.UtcNow.ToString("o") }
        });
        return new(attemptId, snapshotId);
    }

    // Only called when creating a NEW checkout; browser flags cannot disable internal measurement.
    public static MeasurementContext CreateMeasurementContext(MeasurementContext? measurement) => new()
        {
            AnalyticsConsent = measurement?.AnalyticsConsent == true,
            AdvertisingConsent = AdvertisingMeasurementPolicy.AdvertisingMeasurementEnabled,
            AdvertisingMeasurementPolicyVersion = AdvertisingMeasurementPolicy.Version,
            GoogleAdvertisingConsent = true,
            GaClientId = measurement?.AnalyticsConsent == true && System.Text.RegularExpressions.Regex.IsMatch(measurement.GaClientId ?? "", @"\A[0-9]{1,20}\.[0-9]{1,20}\z") ? measurement.GaClientId : null,
            GaSessionId = measurement?.AnalyticsConsent == true && System.Text.RegularExpressions.Regex.IsMatch(measurement.GaSessionId ?? "", @"\A[0-9]{1,20}\z") ? measurement.GaSessionId : null
        };

    public async Task SetCheckoutSessionAsync(string attemptId, string sessionId)
    {
        using var client = CreateClient();
        await client.UpdateItemAsync(new UpdateItemRequest { TableName = "CheckoutAttempts", Key = new() { ["Id"] = new() { S = attemptId } }, UpdateExpression = "SET StripeCheckoutSessionId = :sessionId", ExpressionAttributeValues = new() { [":sessionId"] = new() { S = sessionId } } });
    }

    public async Task CompleteCheckoutAsync(string attemptId, string status, string? subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(attemptId)) return;
        using var client = CreateClient();
        await client.UpdateItemAsync(new UpdateItemRequest { TableName = "CheckoutAttempts", Key = new() { ["Id"] = new() { S = attemptId } },
            UpdateExpression = "SET #status = :status, StripeSubscriptionId = :subscription, UpdatedAt = :updated",
            ExpressionAttributeNames = new() { ["#status"] = "Status" }, ExpressionAttributeValues = new() { [":status"] = new() { S = status }, [":subscription"] = new() { S = subscriptionId ?? string.Empty }, [":updated"] = new() { S = DateTime.UtcNow.ToString("o") } } });
    }

    private static bool IsUuid(string? value) => Guid.TryParse(value, out _);
    private static bool IsPaid(AcquisitionCaptureRequest v) => !string.IsNullOrWhiteSpace(v.Gclid) || !string.IsNullOrWhiteSpace(v.Gbraid) || !string.IsNullOrWhiteSpace(v.Wbraid);
    private static void Add(Dictionary<string, AttributeValue> item, string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) item[key] = new() { S = value.Trim() }; }
}

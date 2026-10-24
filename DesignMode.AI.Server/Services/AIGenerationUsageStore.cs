using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DesignMode.AI.Server.Services;

public sealed class AIGenerationUsageStore
{
    private const string Table = "AIGenerationUsage";
    private readonly AttributionStore _clients;
    private readonly ILogger<AIGenerationUsageStore> _logger;
    private readonly AICreditLedger? _credits;
    private readonly AIUsageSummaryService? _summaries;
    public bool CreditEnforcementEnabled => _credits?.Enabled == true;
    public AIGenerationUsageStore(AttributionStore clients, ILogger<AIGenerationUsageStore> logger, AICreditLedger? credits = null, AIUsageSummaryService? summaries = null)
    { _clients = clients; _logger = logger; _credits = credits; _summaries = summaries; }

    public async Task<string> CreateStartedAsync(AIGenerationUsage usage)
    {
        usage.Id = string.IsNullOrWhiteSpace(usage.Id) ? OpaqueId(usage.Provider, usage.Operation,
            _credits?.Enabled == true && usage.UsageScope == "Paid" ? $"{usage.UserId}:{usage.ProviderTaskId ?? Guid.NewGuid().ToString()}" : usage.ProviderTaskId) : usage.Id;
        usage.UsageId = string.IsNullOrWhiteSpace(usage.UsageId) ? usage.Id : usage.UsageId;
        var now = DateTimeOffset.UtcNow;
        usage.StartedAt = usage.StartedAt == default ? now : usage.StartedAt;
        usage.CreatedAt = usage.CreatedAt == default ? now : usage.CreatedAt;
        usage.UpdatedAt = now; usage.Status = "Started";
        var reserved = false;
        if (_credits?.Enabled == true && usage.UsageScope == "Paid" && usage.Feature == "commercial_video" &&
            usage.Operation == "VideoGeneration" && usage.GenerationUnits == 0)
            await _credits.RequireFundedJobAsync(usage.UserId!, usage.GenerationJobId, usage.Feature);
        if (_credits?.Enabled == true && usage.UsageScope == "Paid" && usage.GenerationUnits > 0)
        {
            if (string.IsNullOrWhiteSpace(usage.UserId) || string.IsNullOrWhiteSpace(usage.GenerationJobId))
                throw new InvalidOperationException("Paid credit usage requires canonical user and job.");
            var summary = await _summaries!.GetAsync(usage.UserId) ?? throw new AICreditQuotaException();
            await _credits.ReserveAsync(usage, summary);
            reserved = true;
        }
        using var client = _clients.CreateClient();
        try { await client.PutItemAsync(new PutItemRequest { TableName = Table, Item = ToItem(usage), ConditionExpression = "attribute_not_exists(Id)" }); }
        catch (ConditionalCheckFailedException)
        {
            if (reserved)
            {
                await _credits!.CompleteAsync(usage.Id, false);
                throw new AICreditDuplicateGenerationException();
            }
            _logger.LogInformation("[AI USAGE] Existing idempotent usage record reused");
        }
        catch
        {
            // No provider call has started. A confirmed reservation can be returned;
            // if returning it fails, retain the durable hold for reconciliation.
            if (reserved) await _credits!.CompleteAsync(usage.Id, false);
            throw;
        }
        return usage.Id;
    }

    public Task CompleteSuccessAsync(string id, decimal? actualCost = null, decimal? estimatedCost = null, long? inputTokens = null, long? cachedInputTokens = null, long? outputTokens = null, string? providerRequestId = null, string? rawUsageReference = null) =>
        CompleteAsync(id, "Succeeded", null, actualCost, estimatedCost, inputTokens, cachedInputTokens, outputTokens, providerRequestId, rawUsageReference);
    public Task CompleteFailureAsync(string id, string failureCategory, decimal? actualCost = null) => CompleteAsync(id, "Failed", failureCategory, actualCost, null, null, null, null, null, null);

    public Task CompleteOpenAiSuccessAsync(string id, OpenAIUsage usage, AIProviderPricingService pricing, string operation, string? requestId)
    {
        var recognized = pricing.HasOpenAiPricing(usage.Model);
        var estimated = pricing.EstimateOpenAiText(usage.Model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens);
        _logger.LogInformation("[AI COST] OpenAI usage received; operation: {Operation}; model recognized: {Recognized}; input tokens present: {InputPresent}; cached input tokens present: {CachedPresent}; output tokens present: {OutputPresent}; pricing resolved: {PricingResolved}; estimated cost calculated: {Calculated}; input tokens: {Input}; cached input tokens: {Cached}; output tokens: {Output}; estimated cost: {Cost}",
            operation, recognized, usage.InputTokens.HasValue, usage.CachedInputTokens.HasValue, usage.OutputTokens.HasValue,
            recognized, estimated.HasValue, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, estimated);
        if (!recognized)
        {
            var model = AIProviderPricingService.NormalizeModel(usage.Model);
            // Never allow arbitrary provider strings or control characters into diagnostics.
            var safeModel = model.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(model, @"\A[a-z0-9._-]+\z") ? model : "unrecognized-model";
            _logger.LogWarning("[AI COST] pricing unavailable for model {Model}", safeModel);
        }
        if (!estimated.HasValue && recognized)
            _logger.LogWarning("[AI COST] estimate unavailable: missing or inconsistent token usage");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var sets = new List<string> { "#status = :status", "#model = :model", "PricingVersion = :version", "CompletedAt = :now", "UpdatedAt = :now" };
        var removes = new List<string>();
        var values = new Dictionary<string, AttributeValue>
        {
            [":status"] = S("Succeeded"), [":model"] = S(usage.Model),
            [":version"] = S(AIProviderPricingService.CurrentVersion), [":now"] = S(now)
        };
        void SetNumber(string field, string token, decimal? value)
        {
            if (value.HasValue) { sets.Add($"{field} = {token}"); values[token] = N(value.Value); }
            else removes.Add(field);
        }
        SetNumber("InputTokens", ":input", usage.InputTokens);
        SetNumber("CachedInputTokens", ":cached", usage.CachedInputTokens);
        SetNumber("OutputTokens", ":output", usage.OutputTokens);
        SetNumber("EstimatedCost", ":estimated", estimated);
        Add(sets, values, "ProviderRequestId", ":request", requestId);
        return UpdateAsync(id, "SET " + string.Join(", ", sets) + (removes.Count > 0 ? " REMOVE " + string.Join(", ", removes) : ""),
            values, new() { ["#status"] = "Status", ["#model"] = "Model" });
    }
    public Task UpdateActualCostAsync(string id, decimal actualCost) => UpdateAsync(id, "SET ActualCost = :actual, UpdatedAt = :updated", new() { [":actual"] = N(actualCost), [":updated"] = S(DateTimeOffset.UtcNow.ToString("O")) });
    public Task UpdateProviderTaskAsync(string id, string providerTaskId) => UpdateAsync(id, "SET ProviderTaskId = :task, UpdatedAt = :updated", new() { [":task"] = S(providerTaskId), [":updated"] = S(DateTimeOffset.UtcNow.ToString("O")) });
    public async Task CompleteJobOperationSuccessAsync(string generationJobId, string operation, decimal? actualCost)
    { var id = await FindUsageIdAsync(generationJobId, operation); if (id is null) throw new InvalidOperationException("Usage record was not found."); await CompleteSuccessAsync(id, actualCost: actualCost); }
    public async Task CompleteJobOperationFailureAsync(string generationJobId, string operation, string category)
    { var id = await FindUsageIdAsync(generationJobId, operation); if (id is null) throw new InvalidOperationException("Usage record was not found."); await CompleteFailureAsync(id, category); }
    private async Task<string?> FindUsageIdAsync(string job, string operation)
    {
        using var client = _clients.CreateClient();
        return await FindUsageIdAsync(client, job, operation);
    }

    public static async Task<string?> FindUsageIdAsync(Amazon.DynamoDBv2.IAmazonDynamoDB client, string job, string operation)
    {
        var request = new ScanRequest
        {
            TableName = Table,
            ConsistentRead = true,
            FilterExpression = "GenerationJobId = :job AND Operation = :operation",
            ExpressionAttributeValues = new() { [":job"] = S(job), [":operation"] = S(operation) },
            ProjectionExpression = "Id"
        };
        do
        {
            var page = await client.ScanAsync(request);
            var id = page.Items.FirstOrDefault()?.GetValueOrDefault("Id")?.S;
            if (!string.IsNullOrWhiteSpace(id)) return id;
            request.ExclusiveStartKey = page.LastEvaluatedKey;
        } while (request.ExclusiveStartKey is { Count: > 0 });
        return null;
    }
    private Task CompleteAsync(string id, string status, string? failure, decimal? actual, decimal? estimated, long? input, long? cached, long? output, string? requestId, string? rawRef)
    {
        var sets = new List<string> { "#status = :status", "CompletedAt = :completed", "UpdatedAt = :updated" };
        var names = new Dictionary<string, string> { ["#status"] = "Status" };
        var values = new Dictionary<string, AttributeValue> { [":status"] = S(status), [":completed"] = S(DateTimeOffset.UtcNow.ToString("O")), [":updated"] = S(DateTimeOffset.UtcNow.ToString("O")) };
        Add(sets, values, "FailureCategory", ":failure", failure); Add(sets, values, "ActualCost", ":actual", actual); Add(sets, values, "EstimatedCost", ":estimated", estimated);
        Add(sets, values, "InputTokens", ":input", input); Add(sets, values, "CachedInputTokens", ":cached", cached); Add(sets, values, "OutputTokens", ":output", output);
        Add(sets, values, "ProviderRequestId", ":request", requestId); Add(sets, values, "RawUsageReference", ":raw", rawRef);
        return UpdateAsync(id, "SET " + string.Join(", ", sets), values, names);
    }

    private async Task UpdateAsync(string id, string expression, Dictionary<string, AttributeValue> values, Dictionary<string, string>? names = null)
    {
        using var client = _clients.CreateClient();
        var condition = "attribute_exists(Id)";
        if (_credits?.Enabled == true && values.ContainsKey(":status"))
        {
            condition += " AND (#status = :started OR #status = :status)";
            values[":started"] = S("Started");
        }
        var request = new UpdateItemRequest { TableName = Table, Key = new() { ["Id"] = S(id) }, UpdateExpression = expression, ExpressionAttributeValues = values, ExpressionAttributeNames = names, ConditionExpression = condition };
        for (var attempt = 0; ; attempt++)
        {
            try { await client.UpdateItemAsync(request); break; }
            catch (Exception ex) when (attempt < 2 && ex is ProvisionedThroughputExceededException or InternalServerErrorException)
            {
                // Retry the same idempotent metadata update, never the provider call.
                await Task.Delay(100 * (attempt + 1));
            }
        }
        if (_credits?.Enabled == true && values.TryGetValue(":status", out var status))
            await _credits.CompleteAsync(id, status.S == "Succeeded");
    }

    public Task<AIGenerationCostAggregate> AggregateByProjectAsync(string id) => AggregateAsync("ProjectId", id);
    public Task<AIGenerationCostAggregate> AggregateByFunnelAsync(string id) => AggregateAsync("FunnelId", id);
    public Task<AIGenerationCostAggregate> AggregateByUserAsync(string id) => AggregateAsync("UserId", id);
    public Task<AIGenerationCostAggregate> AggregateByGenerationJobAsync(string id) => AggregateAsync("GenerationJobId", id);
    private async Task<AIGenerationCostAggregate> AggregateAsync(string field, string id)
    {
        using var client = _clients.CreateClient(); decimal actual = 0, estimated = 0; int succeeded = 0, failed = 0; Dictionary<string, AttributeValue>? key = null;
        do { var page = await client.ScanAsync(new ScanRequest { TableName = Table, ExclusiveStartKey = key, FilterExpression = "#field = :id", ExpressionAttributeNames = new() { ["#field"] = field }, ExpressionAttributeValues = new() { [":id"] = S(id) } }); key = page.LastEvaluatedKey;
            foreach (var item in page.Items) { actual += Decimal(item, "ActualCost"); estimated += Decimal(item, "EstimatedCost"); if (item.GetValueOrDefault("Status")?.S == "Succeeded") succeeded++; if (item.GetValueOrDefault("Status")?.S == "Failed") failed++; }
        } while (key is { Count: > 0 });
        return new(actual, estimated, "USD", succeeded, failed);
    }

    private static Dictionary<string, AttributeValue> ToItem(AIGenerationUsage u) { var x = new Dictionary<string, AttributeValue> { ["Id"] = S(u.Id), ["UsageId"] = S(u.UsageId), ["GenerationJobId"] = S(u.GenerationJobId), ["Operation"] = S(u.Operation), ["Provider"] = S(u.Provider), ["Model"] = S(u.Model), ["StartedAt"] = S(u.StartedAt.ToString("O")), ["Status"] = S(u.Status), ["Currency"] = S(u.Currency), ["CreatedAt"] = S(u.CreatedAt.ToString("O")), ["UpdatedAt"] = S(u.UpdatedAt.ToString("O")) }; Add(x,"UserId",u.UserId); Add(x,"ProjectId",u.ProjectId); Add(x,"FunnelId",u.FunnelId); Add(x,"ParentUsageId",u.ParentUsageId); Add(x,"GenerationCategory",u.GenerationCategory); Add(x,"DeliverableType",u.DeliverableType); Add(x,"Feature",u.Feature); Add(x,"UsageScope",u.UsageScope); Add(x,"GenerationUnits",u.GenerationUnits); Add(x,"ProviderRequestId",u.ProviderRequestId); Add(x,"ProviderTaskId",u.ProviderTaskId); Add(x,"InputTokens",u.InputTokens); Add(x,"CachedInputTokens",u.CachedInputTokens); Add(x,"OutputTokens",u.OutputTokens); Add(x,"ImageCount",u.ImageCount); Add(x,"ImageWidth",u.ImageWidth); Add(x,"ImageHeight",u.ImageHeight); Add(x,"VideoSeconds",u.VideoSeconds); Add(x,"AudioSeconds",u.AudioSeconds); Add(x,"CharacterCount",u.CharacterCount); Add(x,"OtherBillableUnits",u.OtherBillableUnits); if(u.CompletedAt.HasValue)x["CompletedAt"]=S(u.CompletedAt.Value.ToString("O")); Add(x,"FailureCategory",u.FailureCategory); Add(x,"EstimatedCost",u.EstimatedCost); Add(x,"ActualCost",u.ActualCost); Add(x,"PricingVersion",u.PricingVersion); Add(x,"RawUsageReference",u.RawUsageReference); return x; }
    private static string OpaqueId(string provider, string operation, string? task) { if (string.IsNullOrWhiteSpace(task)) return Guid.NewGuid().ToString(); var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"usage:v1:{provider}:{operation}:{task}"))[..16]; return new Guid(bytes).ToString(); }
    private static AttributeValue S(string value) => new() { S = value }; private static AttributeValue N(decimal value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static decimal Decimal(Dictionary<string, AttributeValue> i,string k) => decimal.TryParse(i.GetValueOrDefault(k)?.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static void Add(Dictionary<string,AttributeValue> x,string k,string? v){if(!string.IsNullOrWhiteSpace(v))x[k]=S(v);} private static void Add(Dictionary<string,AttributeValue> x,string k,int? v){if(v.HasValue)x[k]=new(){N=v.Value.ToString()};} private static void Add(Dictionary<string,AttributeValue> x,string k,decimal? v){if(v.HasValue)x[k]=N(v.Value);}
    private static void Add(Dictionary<string,AttributeValue> x,string k,long? v){if(v.HasValue)x[k]=new(){N=v.Value.ToString(CultureInfo.InvariantCulture)};}
    private static void Add(List<string>s,Dictionary<string,AttributeValue>v,string f,string t,string? x){if(!string.IsNullOrWhiteSpace(x)){s.Add($"{f} = {t}");v[t]=S(x);}} private static void Add(List<string>s,Dictionary<string,AttributeValue>v,string f,string t,decimal? x){if(x.HasValue){s.Add($"{f} = {t}");v[t]=N(x.Value);}} private static void Add(List<string>s,Dictionary<string,AttributeValue>v,string f,string t,long? x){if(x.HasValue){s.Add($"{f} = {t}");v[t]=new(){N=x.Value.ToString()};}}
}

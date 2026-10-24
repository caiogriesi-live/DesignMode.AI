using Amazon.DynamoDBv2.Model;
using Azure;
using Azure.Storage.Blobs;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DesignMode.AI.Server.Services;

public sealed class CommercialOverlayEnhancement(AttributionStore projects, AIUsageRecorder usage, ILogger logger)
{
    public static string CacheKey(string userId, string projectId, string jobId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new[] { "commercial-overlay-v1", userId, projectId, jobId })))).ToLowerInvariant();

    public static (string Name, string Industry)? OwnedBrand(Dictionary<string, AttributeValue> project, string userId)
    {
        if (project.GetValueOrDefault("UserId")?.S != userId) return null;
        var name = project.GetValueOrDefault("Name")?.S;
        var industry = project.GetValueOrDefault("Industry")?.S;
        return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(industry) ? null : (name.Trim(), industry.Trim());
    }

    public static async Task<(string Name, string Industry)?> ResolveBrandAsync(
        Dictionary<string, AttributeValue> project, string userId,
        Func<string, Task<List<Dictionary<string, AttributeValue>>>> findCompany)
    {
        if (string.IsNullOrWhiteSpace(userId) || project.GetValueOrDefault("UserId")?.S != userId) return null;
        var direct = OwnedBrand(project, userId);
        if (direct != null) return direct;
        var companyId = project.GetValueOrDefault("LegacyCompanyIndustryId")?.S;
        if (string.IsNullOrWhiteSpace(companyId)) return null;
        // Follow only the owned project's persisted mapping; never search by user,
        // latest company, browser identity, or client-supplied company text.
        var matches = await findCompany(companyId);
        if (matches.Count != 1 || matches[0].GetValueOrDefault("Id")?.S != companyId) return null;
        return OwnedBrand(matches[0], userId);
    }

    public async Task<string> EnhanceAsync(string baseFile, IReadOnlyList<string> segments, IReadOnlyList<string> descriptions,
        string ffmpeg, string directory, string storage, string containerName, string userId, string projectId, string jobId)
    {
        return await BestEffortAsync(baseFile, async () =>
        {
            if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(projectId, out _) || !Guid.TryParse(jobId, out _))
            {
                logger.LogWarning("Commercial overlays skipped: missing authenticated generation context.");
                return baseFile;
            }
            using var db = projects.CreateClient();
            var project = await db.GetItemAsync("Projects", new() { ["Id"] = new() { S = projectId } });
            var brand = await ResolveBrandAsync(project.Item, userId,
                companyId => FunnelCompanyLookup.FindAsync(db, string.Empty, companyId, null, null));
            if (brand == null)
            {
                logger.LogWarning("Commercial overlays skipped: owned project branding unavailable, including linked company lookup.");
                return baseFile;
            }
            logger.LogInformation("Commercial overlays: project branding resolved; measuring {SceneCount} scenes.", segments.Count);
            var internalIds = new[] { userId, projectId, jobId, project.Item.GetValueOrDefault("LegacyCompanyIdeaId")?.S ?? "" };
            var scenes = new List<CommercialSceneTiming>();
            var offset = 0d;
            var width = 0;
            var height = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                var media = await CommercialOverlayCompositor.ProbeAsync(ffmpeg, segments[i], directory);
                width = media.Width; height = media.Height;
                var description = i < descriptions.Count ? descriptions[i] ?? "" : "";
                foreach (var id in internalIds.Where(x => !string.IsNullOrWhiteSpace(x)))
                    description = description.Replace(id, "", StringComparison.OrdinalIgnoreCase);
                description = Regex.Replace(description, @"https?://\S+|\b[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}\b", "");
                scenes.Add(new(i, offset, media.Duration, description[..Math.Min(800, description.Length)]));
                offset += media.Duration;
            }
            var finalMedia = await CommercialOverlayCompositor.ProbeAsync(ffmpeg, baseFile, directory);
            // Container/audio rounding must never let an overlay outlive the final video.
            scenes = scenes.Where(s => s.StartSeconds < finalMedia.Duration)
                .Select(s => s with { DurationSeconds = Math.Min(s.DurationSeconds, finalMedia.Duration - s.StartSeconds) }).ToList();
            width = finalMedia.Width; height = finalMedia.Height;
            var container = new BlobServiceClient(storage).GetBlobContainerClient(containerName);
            await container.CreateIfNotExistsAsync();
            var key = CacheKey(userId, projectId, jobId);
            var cached = container.GetBlobClient($"overlay-plans/{key}.json");
            var plan = await GetPlanOnceAsync(async () => await cached.ExistsAsync()
                ? JsonConvert.DeserializeObject<CommercialOverlayPlan>((await cached.DownloadContentAsync()).Value.Content.ToString()) : null,
                async () =>
                {
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenAISettings:ApiKey")))
                    {
                        logger.LogWarning("Commercial overlays skipped: OpenAI API key is not configured.");
                        return false;
                    }
                    // A durable create-only claim gives at-most-one provider attempt per job,
                    // including concurrent requests and crashes after a billable response.
                    // A claimed job without a result deliberately falls back, never re-bills.
                    var claim = container.GetBlobClient($"overlay-plans/{key}.started");
                    try { await claim.UploadAsync(BinaryData.FromString("started"), overwrite: false); return true; }
                    catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412) { return false; }
                }, async () =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
                    var generated = await new CommercialOverlayPlanner(client, usage).PlanAsync(userId, projectId, jobId, key,
                        brand.Value.Name, brand.Value.Industry, scenes, width, height);
                    return CommercialOverlayPolicy.Validate(generated, brand.Value.Name, scenes, internalIds);
                }, async generated => { await cached.UploadAsync(BinaryData.FromString(JsonConvert.SerializeObject(generated)), overwrite: false); });
            plan = CommercialOverlayPolicy.Validate(plan, brand.Value.Name, scenes, internalIds);
            logger.LogInformation("Commercial overlays: {TextCount} validated text moments and {AccentCount} accents.",
                plan.Overlays.Count, plan.MotionAccents.Count);
            return await CommercialOverlayCompositor.RenderAsync(ffmpeg, baseFile, directory, plan, width, height);
        }, logger);
    }

    public static async Task<CommercialOverlayPlan?> GetPlanOnceAsync(Func<Task<CommercialOverlayPlan?>> load,
        Func<Task<bool>> claim, Func<Task<CommercialOverlayPlan>> generate, Func<CommercialOverlayPlan, Task> save)
    {
        var existing = await load();
        if (existing != null) return existing;
        if (!await claim()) return null;
        var plan = await generate();
        await save(plan);
        return plan;
    }

    public static async Task<string> BestEffortAsync(string baseFile, Func<Task<string>> enhancement, ILogger logger)
    {
        try { return await enhancement(); }
        catch (Exception ex)
        {
            logger.LogWarning("Commercial overlay enhancement skipped; base footage retained. Category: {Category}; HTTP status: {HttpStatus}",
                ex.GetType().Name, ex is HttpRequestException http ? http.StatusCode : null);
            return baseFile;
        }
    }
}

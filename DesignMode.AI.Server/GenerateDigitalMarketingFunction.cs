using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Server.Security;

namespace DesignMode.AI.Server
{
    public class GenerateDigitalMarketingFunction
    {
        private readonly ILogger<GenerateDigitalMarketingFunction> _logger;
        private readonly AIUsageRecorder _usage;
        private readonly AttributionStore _attribution;
        private readonly JwtUserResolver _jwt;
        private readonly AIUsageSummaryService? _summary;

        public GenerateDigitalMarketingFunction(ILogger<GenerateDigitalMarketingFunction> logger, AIUsageRecorder usage, AttributionStore attribution, JwtUserResolver jwt, AIUsageSummaryService? summary = null)
        {
            _logger = logger;
            _usage = usage;
            _attribution = attribution;
            _jwt = jwt;
            _summary = summary;
        }

        [Function("GenerateDigitalMarketing")]
        public async Task<IActionResult> GenerateDigitalMarketing(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                // Get Origin header
                string referer = req.Headers["Origin"].FirstOrDefault();

                if (referer != null)
                {
                    if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                    {
                        return new UnauthorizedResult();
                    }
                }
                else
                {
                    return new UnauthorizedResult();
                }

                var body = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) return new BadRequestObjectResult("Request body is required.");

                GenerateDigitalMarketingRequest? data;
                try { data = JsonConvert.DeserializeObject<GenerateDigitalMarketingRequest>(body); }
                catch (Exception ex) { return new BadRequestObjectResult($"Unable to parse request: {ex.Message}"); }

                if (data == null) return new BadRequestObjectResult("Digital marketing request is required.");
                if (string.IsNullOrWhiteSpace(data.ImageUrl)) return new BadRequestObjectResult("ImageUrl is required.");

                var claimedUserId = _jwt.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? await ResolveLegacyJwtUserIdAsync(req)
                    : await _attribution.ResolveClaimedUserIdAsync(claimedUserId);
                var generationProject = string.IsNullOrWhiteSpace(userId) ? null :
                    await _attribution.GetOwnedGenerationProjectAsync(userId, data.ProjectId ?? string.Empty);
                if (generationProject is null) return new UnauthorizedResult();
                data.ProjectId = generationProject.ProjectId;
                data.FunnelId = generationProject.FunnelId;
                var quota = await _summary!.EvaluateAsync(userId!, "digital_marketing");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };

                var generationJobId = string.IsNullOrWhiteSpace(data.GenerationJobId)
                    ? Guid.NewGuid().ToString()
                    : data.GenerationJobId;

                var apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
                if (string.IsNullOrWhiteSpace(apiKey)) return new BadRequestObjectResult("OpenAI API key is not configured.");

                var companyName = string.IsNullOrWhiteSpace(data.Name) ? "Brand" : data.Name.Trim();
                var assetSpecs = new[]
                {
                    new AssetSpec("instagram-feed", "Instagram Feed Post", "1080 × 1080", "1:1 square social feed post"),
                    new AssetSpec("instagram-story-reel", "Instagram Story / Reel", "1080 × 1920", "9:16 vertical story/reel creative"),
                    new AssetSpec("facebook-linkedin-ad", "Facebook / LinkedIn Ad", "1200 × 628", "1.91:1 landscape paid social advertisement"),
                    new AssetSpec("x-twitter-post", "X (Twitter) Post", "1600 × 900", "16:9 landscape social post")
                };

                var generatedAssets = new List<GeneratedMarketingAsset>();
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var creditUsageId = await _usage.StartCreditJobAsync(userId!, data.ProjectId, data.FunnelId,
                    generationJobId!, "digital_marketing", _summary!.UnitsFor("digital_marketing"));
                foreach (var spec in assetSpecs)
                {
                    var operationId = $"{generationJobId}-{spec.AssetType}";
                    var usageId = await _usage.StartAsync(new AIGenerationUsage
                    {
                        UserId = userId, ProjectId = data.ProjectId, FunnelId = data.FunnelId,
                        GenerationJobId = generationJobId, ProviderTaskId = operationId,
                        GenerationCategory = "PaidWorkspace", DeliverableType = "DigitalMarketing",
                        Feature = "digital_marketing", UsageScope = "Paid", Operation = "DigitalMarketingGeneration",
                        Provider = "OpenAI", Model = "gpt-5.6", ImageCount = 1,
                        PricingVersion = AIProviderPricingService.CurrentVersion
                    });
                    var prompt = BuildAssetPrompt(data, companyName, spec);
                    var requestPayload = new
                    {
                        model = "gpt-5.6",
                        input = new object[]
                        {
                            new
                            {
                                role = "user",
                                content = new object[]
                                {
                                    new { type = "input_text", text = prompt },
                                    new { type = "input_image", image_url = data.ImageUrl, detail = "high" }
                                }
                            }
                        },
                        tools = new object[] { new { type = "image_generation", action = "generate" } }
                    };

                    var json = JsonConvert.SerializeObject(requestPayload);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync("https://api.openai.com/v1/responses", content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        await _usage.CompleteFailureAsync(usageId, "ProviderError");
                        _logger.LogError("OpenAI generation failed for {AssetType}: {Body}", spec.AssetType, responseText);
                        return new BadRequestObjectResult(new
                        {
                            Success = false,
                            Message = $"OpenAI generation failed for {spec.DisplayName}.",
                            Error = responseText
                        });
                    }

                    var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
                    await _usage.CompleteOpenAiAsync(usageId, responseText, "gpt-5.6", "DigitalMarketingGeneration", requestId);

                    var base64 = ExtractGeneratedImageBase64(responseText);
                    if (string.IsNullOrWhiteSpace(base64))
                    {
                        return new BadRequestObjectResult(new
                        {
                            Success = false,
                            Message = $"OpenAI completed {spec.DisplayName} but did not return an image."
                        });
                    }

                    generatedAssets.Add(new GeneratedMarketingAsset
                    {
                        AssetType = spec.AssetType,
                        DisplayName = spec.DisplayName,
                        Dimensions = spec.Dimensions,
                        ImageUrl = $"data:image/png;base64,{base64}",
                        DownloadUrl = $"data:image/png;base64,{base64}",
                        MimeType = "image/png",
                        FileName = GetAssetFileName(spec.AssetType)
                    });
                }

                await _usage.CompleteSuccessAsync(creditUsageId);
                return new OkObjectResult(new
                {
                    Success = true,
                    Message = "Digital marketing assets generated successfully.",
                    DigitalMarketingId = Guid.NewGuid().ToString(),
                    CompanyName = companyName,
                    SourceImageUrl = data.ImageUrl,
                    Assets = generatedAssets,
                    GeneratedAtUtc = DateTime.UtcNow
                });
            }
            catch (AICreditQuotaException)
            {
                return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = 429 };
            }
            catch (AICreditDuplicateGenerationException)
            {
                return new ConflictObjectResult(new { code = "generation_already_started" });
            }
            catch (TaskCanceledException ex)
            {
                return new BadRequestObjectResult(new { Success = false, Message = "Digital marketing generation timed out. Please try again.", Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateDigitalMarketing failed.");
                return new ObjectResult(new { Success = false, Message = "An exception occurred while generating digital marketing assets.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        [Function("SaveDigitalMarketing")]
        public async Task<IActionResult> SaveDigitalMarketing(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<DigitalMarketingRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null || string.IsNullOrWhiteSpace(data.DigitalMarketingId) ||
                string.IsNullOrWhiteSpace(data.CompanyName) || string.IsNullOrWhiteSpace(data.Version) ||
                string.IsNullOrWhiteSpace(data.TimeStamp))
            {
                return new BadRequestObjectResult("DigitalMarketingId, CompanyName, Version, and TimeStamp are required.");
            }

            try
            {
                using var client = CreateDynamoClient();
                string id;
                while (true)
                {
                    id = Guid.NewGuid().ToString();
                    var check = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM DigitalMarketing WHERE Id = ?",
                        Parameters = new List<AttributeValue> { new() { S = id } }
                    });
                    if (check.Items.Count == 0) break;
                }

                var put = new PutItemRequest
                {
                    TableName = "DigitalMarketing",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["Id"] = new() { S = id },
                        ["DigitalMarketingId"] = new() { S = data.DigitalMarketingId },
                        ["CompanyName"] = new() { S = data.CompanyName },
                        ["Version"] = new() { S = data.Version },
                        ["TimeStamp"] = new() { S = data.TimeStamp }
                    }
                };

                await client.PutItemAsync(put);
                return new OkObjectResult(new { Message = "Digital marketing version saved successfully.", Id = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving DigitalMarketing row failed.");
                return new ObjectResult(new { Message = "Failed saving DigitalMarketing row.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        [Function("GetDigitalMarketingByCompany")]
        public async Task<IActionResult> GetDigitalMarketingByCompany(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<GetDigitalMarketingByCompanyRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(data?.CompanyName)) return new BadRequestObjectResult("CompanyName is required.");

            try
            {
                using var client = CreateDynamoClient();
                var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM DigitalMarketing WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue> { new() { S = data.CompanyName } }
                });

                var rows = result.Items.Select(item => new
                {
                    Id = item.TryGetValue("Id", out var id) ? id.S : null,
                    DigitalMarketingId = item.TryGetValue("DigitalMarketingId", out var dmId) ? dmId.S : null,
                    CompanyName = item.TryGetValue("CompanyName", out var cn) ? cn.S : null,
                    Version = item.TryGetValue("Version", out var v) ? v.S : null,
                    TimeStamp = item.TryGetValue("TimeStamp", out var ts) ? ts.S : null
                })
                .OrderByDescending(x => DateTime.TryParse(x.TimeStamp, out var dt) ? dt : DateTime.MinValue)
                .ToList();

                return new OkObjectResult(new { Message = "Digital marketing versions retrieved successfully.", Count = rows.Count, Data = rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching DigitalMarketing rows failed.");
                return new ObjectResult(new { Message = "Failed fetching DigitalMarketing rows.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        [Function("SaveDigitalMarketingImage")]
        public async Task<IActionResult> SaveDigitalMarketingImage(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveDigitalMarketingImageRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null || string.IsNullOrWhiteSpace(data.ImageUrl) || string.IsNullOrWhiteSpace(data.CompanyName) ||
                string.IsNullOrWhiteSpace(data.Version) || string.IsNullOrWhiteSpace(data.AssetType))
            {
                return new BadRequestObjectResult("ImageUrl, CompanyName, Version, and AssetType are required.");
            }

            try
            {
                var useLocal = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
                    || req.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase);

                var safeCompany = SanitizeCompanyName(data.CompanyName);
                var safeVersion = data.Version.Replace("v", "", StringComparison.OrdinalIgnoreCase).Trim();
                var folderName = $"{safeCompany}-digital-marketing-v{safeVersion}";
                var fileName = GetAssetFileName(data.AssetType);

                var (imageBytes, contentType) = await ReadImageBytesAsync(data.ImageUrl);
                string filePath;
                string webUrl;

                if (useLocal)
                {
                    // REQUIRED LOCAL STRUCTURE:
                    // <function-app>/wwwroot/SavedImages/<company>-digital-marketing-vN/<asset>.png
                    var savedImagesPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "SavedImages");
                    var folderPath = Path.Combine(savedImagesPath, folderName);
                    Directory.CreateDirectory(folderPath);
                    filePath = Path.Combine(folderPath, fileName);
                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    var rootUrl = $"{req.Scheme}://{req.Host.Host}";
                    webUrl = $"{rootUrl}/{folderName}/{fileName}";
                }
                else
                {
                    var bucket = RequireEnv("S3_BUCKET_NAME");
                    var region = RequireEnv("AWS_REGION");
                    var access = RequireEnv("AWS_ACCESS_KEY_ID");
                    var secret = RequireEnv("AWS_SECRET_ACCESS_KEY");

                    using var s3 = new AmazonS3Client(access, secret, new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });
                    var keyName = $"{folderName}/{fileName}";
                    using var stream = new MemoryStream(imageBytes);
                    var put = new PutObjectRequest { BucketName = bucket, Key = keyName, InputStream = stream, ContentType = contentType };
                    put.Headers["Content-Disposition"] = "inline";
                    await s3.PutObjectAsync(put);
                    filePath = keyName;
                    webUrl = $"https://{bucket}.s3.{region}.amazonaws.com/{keyName}";
                }

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = useLocal ? "Digital marketing image saved locally." : "Digital marketing image saved to S3.",
                    FilePath = filePath,
                    FileName = fileName,
                    FolderName = folderName,
                    WebUrl = webUrl,
                    ImageUrl = webUrl,
                    SavedUrl = webUrl,
                    ContentType = contentType,
                    Size = imageBytes.Length,
                    Mode = useLocal ? "LOCAL" : "S3"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving digital marketing image failed.");
                return new ObjectResult(new { Success = false, Message = "Error saving digital marketing image.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        private bool IsAllowedOrigin(HttpRequest req)
        {
            var origin = req.Headers["Origin"].FirstOrDefault();
            var allowed = Environment.GetEnvironmentVariable("HostUrl");
            return !string.IsNullOrWhiteSpace(origin) && origin == allowed;
        }

        private AmazonDynamoDBClient CreateDynamoClient()
        {
            return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
        }

        private static string BuildAssetPrompt(GenerateDigitalMarketingRequest data, string companyName, AssetSpec spec)
        {
            return $@"
Create ONE finished digital marketing image for: {spec.DisplayName}.

REFERENCE IMAGE RULES
- The supplied image is the visual reference.
- Preserve its brand personality, color relationships, graphic language, typography personality, photography/illustration style, shapes, patterns, spacing, and art direction.
- Create a NEW platform-specific composition. Do not simply return or frame the reference image.
- Do not show a phone, laptop, browser, social-media UI, editor, mockup device, hands, or presentation board.

COMPANY
Company: {companyName}
Company description: {data.CompanyDescription}
Brand / creative direction: {data.Prompt}

CAMPAIGN
Primary goal: {data.PrimaryGoal}
Target audience: {data.TargetAudience}
Tone: {data.CampaignTone}
Campaign message / offer: {data.CampaignMessage}
Call to action: {data.CallToAction}
Include logo / brand-mark treatment: {BoolText(data.IncludeBrandLogo)}
Include CTA text: {BoolText(data.IncludeCTA)}
Include hashtags only if they visually belong in this asset: {BoolText(data.IncludeHashtags)}

OUTPUT FORMAT
Asset: {spec.DisplayName}
Target dimensions: {spec.Dimensions}
Composition: {spec.Composition}

DESIGN REQUIREMENTS
- Premium commercial digital-marketing quality.
- Strong focal point and clear visual hierarchy.
- Platform-appropriate text density.
- Legible, correctly spelled headline/CTA text.
- Do not invent fake claims, prices, dates, certifications, testimonials, legal language, or contact details.
- Use supplied campaign message when present; otherwise keep copy concise and brand appropriate.
- Keep important content away from outer edges.
- Generate the final artwork itself, not a screenshot of the platform.

Generate ONE completed {spec.DisplayName} image now.";
        }

        private static string? ExtractGeneratedImageBase64(string jsonResponse)
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            if (!doc.RootElement.TryGetProperty("output", out var output)) return null;
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "image_generation_call", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.TryGetProperty("result", out var result) && !string.IsNullOrWhiteSpace(result.GetString())) return result.GetString();
            }
            return null;
        }

        private static async Task<(byte[] Bytes, string ContentType)> ReadImageBytesAsync(string imageUrl)
        {
            if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = imageUrl.IndexOf(',');
                if (comma < 0) throw new InvalidOperationException("Invalid base64 image data URI.");
                var meta = imageUrl[..comma];
                var contentType = "image/png";
                var semi = meta.IndexOf(';');
                if (semi > 5) contentType = meta.Substring(5, semi - 5);
                var b64 = imageUrl[(comma + 1)..].Replace("\r", "").Replace("\n", "").Trim();
                return (Convert.FromBase64String(b64), contentType);
            }

            if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient();
                using var response = await http.GetAsync(imageUrl);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType ?? "image/png");
            }

            throw new InvalidOperationException("ImageUrl must be a data URI or HTTP/HTTPS URL.");
        }

        private static string GetAssetFileName(string assetType) => assetType switch
        {
            "instagram-feed" => "instagram-feed.png",
            "instagram-story-reel" => "instagram-story-reel.png",
            "facebook-linkedin-ad" => "facebook-linkedin-ad.png",
            "x-twitter-post" => "x-twitter-post.png",
            _ => $"{SanitizeAssetType(assetType)}.png"
        };

        private static string SanitizeCompanyName(string value)
        {
            var chars = value.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray();
            var safe = new string(chars).Replace(" ", "-").Replace("&", "and");
            return string.IsNullOrWhiteSpace(safe) ? "Brand" : safe;
        }

        private static string SanitizeAssetType(string value)
        {
            var safe = new string((value ?? "asset").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "asset" : safe;
        }

        private static string BoolText(bool value) => value ? "YES" : "NO";
        private static string RequireEnv(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"{key} is not configured.");

        private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
        {
            var username = _jwt.ResolveUsername(request);
            return string.IsNullOrWhiteSpace(username)
                ? null
                : await _attribution.ResolveUserIdAsync(username);
        }

        private record AssetSpec(string AssetType, string DisplayName, string Dimensions, string Composition);
    }

    public class GenerateDigitalMarketingRequest
    {
        public string? GenerationJobId { get; set; }
        public string? ProjectId { get; set; }
        public string? FunnelId { get; set; }
        public string ImageUrl { get; set; } = "";
        public string SelectedImageId { get; set; } = "";
        public string Name { get; set; } = "";
        public string CompanyDescription { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string PrimaryGoal { get; set; } = "";
        public string TargetAudience { get; set; } = "";
        public string CampaignTone { get; set; } = "";
        public string CampaignMessage { get; set; } = "";
        public string CallToAction { get; set; } = "";
        public bool IncludeBrandLogo { get; set; }
        public bool IncludeCTA { get; set; }
        public bool IncludeHashtags { get; set; }
        public string UserId { get; set; } = "";
        public string CompanyIdeaId { get; set; } = "";
        public string PromptId { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    public class GeneratedMarketingAsset
    {
        public string AssetType { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Dimensions { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string FileName { get; set; } = "";
    }

    public class DigitalMarketingRequest
    {
        public string DigitalMarketingId { get; set; } = "";
        public string CompanyName { get; set; } = "";
        public string Version { get; set; } = "";
        public string TimeStamp { get; set; } = "";
    }

    public class GetDigitalMarketingByCompanyRequest
    {
        public string CompanyName { get; set; } = "";
    }

    public class SaveDigitalMarketingImageRequest
    {
        public string ImageUrl { get; set; } = "";
        public string CompanyName { get; set; } = "";
        public string Version { get; set; } = "";
        public string AssetType { get; set; } = "";
    }
}

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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Server.Security;

namespace DesignMode.AI.Server
{
    public class GenerateBrandingLogoFunction
    {
        private readonly ILogger<GenerateBrandingLogoFunction> _logger;
        private readonly AIUsageRecorder _usage;
        private readonly AttributionStore _attribution;
        private readonly JwtUserResolver _jwt;
        private readonly AIUsageSummaryService? _summary;

        public GenerateBrandingLogoFunction(ILogger<GenerateBrandingLogoFunction> logger, AIUsageRecorder usage, AttributionStore attribution, JwtUserResolver jwt, AIUsageSummaryService? summary = null)
        {
            _logger = logger;
            _usage = usage;
            _attribution = attribution;
            _jwt = jwt;
            _summary = summary;
        }

        [Function("GenerateBrandingLogo")]
        public async Task<IActionResult> GenerateBrandingLogo(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(requestBody))
                    return new BadRequestObjectResult("Request body is required.");

                GenerateBrandingLogoRequest? data;
                try
                {
                    data = JsonConvert.DeserializeObject<GenerateBrandingLogoRequest>(requestBody);
                }
                catch (Exception ex)
                {
                    return new BadRequestObjectResult($"Unable to parse request: {ex.Message}");
                }

                if (data == null) return new BadRequestObjectResult("Branding request is required.");
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
                var quota = await _summary!.EvaluateAsync(userId!, "branding_logo");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };

                var generationJobId = string.IsNullOrWhiteSpace(data.GenerationJobId)
                    ? Guid.NewGuid().ToString()
                    : data.GenerationJobId;

                var companyName = string.IsNullOrWhiteSpace(data.Name) ? "Brand" : data.Name.Trim();
                var industry = string.IsNullOrWhiteSpace(data.Industry) ? "General" : data.Industry.Trim();
                var logoStyle = string.IsNullOrWhiteSpace(data.LogoStyle) ? "Combination" : data.LogoStyle.Trim();
                var colorDirection = string.IsNullOrWhiteSpace(data.ColorPalette) ? "Standard" : data.ColorPalette.Trim();
                var description = data.CompanyDescription?.Trim() ?? string.Empty;
                var creativeDirection = data.Prompt?.Trim() ?? string.Empty;

                var apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
                if (string.IsNullOrWhiteSpace(apiKey))
                    return new BadRequestObjectResult("OpenAI API key is not configured.");

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var creditUsageId = await _usage.StartCreditJobAsync(userId!, data.ProjectId, data.FunnelId,
                    generationJobId!, "branding_logo", _summary!.UnitsFor("branding_logo"));
                // Generate the five deterministic brand-kit assets concurrently.
                // Running these sequentially can easily exceed the Blazor WASM default
                // 100-second HttpClient timeout. The client still allows up to 10 minutes
                // for each individual OpenAI request.
                var logoVariationsTask = GenerateBrandAssetAsync(
                    client,
                    data.ImageUrl,
                    BuildLogoVariationsPrompt(companyName, industry, logoStyle, colorDirection, description, creativeDirection),
                    userId, data.ProjectId, data.FunnelId, generationJobId, "logo_variations");

                var colorPaletteTask = GenerateBrandAssetAsync(
                    client,
                    data.ImageUrl,
                    BuildColorPalettePrompt(companyName, industry, colorDirection, description, creativeDirection),
                    userId, data.ProjectId, data.FunnelId, generationJobId, "color_palette");

                var typographyTask = GenerateBrandAssetAsync(
                    client,
                    data.ImageUrl,
                    BuildTypographyPrompt(companyName, industry, logoStyle, description, creativeDirection),
                    userId, data.ProjectId, data.FunnelId, generationJobId, "typography");

                var brandGuidelinesTask = GenerateBrandAssetAsync(
                    client,
                    data.ImageUrl,
                    BuildBrandGuidelinesPrompt(companyName, industry, logoStyle, colorDirection, description, creativeDirection),
                    userId, data.ProjectId, data.FunnelId, generationJobId, "brand_guidelines");

                var socialMediaKitTask = GenerateBrandAssetAsync(
                    client,
                    data.ImageUrl,
                    BuildSocialMediaKitPrompt(companyName, industry, logoStyle, colorDirection, description, creativeDirection),
                    userId, data.ProjectId, data.FunnelId, generationJobId, "social_media_kit");

                await Task.WhenAll(
                    logoVariationsTask,
                    colorPaletteTask,
                    typographyTask,
                    brandGuidelinesTask,
                    socialMediaKitTask);

                var logoVariationsUrl = await logoVariationsTask;
                var colorPaletteUrl = await colorPaletteTask;
                var typographyUrl = await typographyTask;
                var brandGuidelinesUrl = await brandGuidelinesTask;
                var socialMediaKitUrl = await socialMediaKitTask;

                await _usage.CompleteSuccessAsync(creditUsageId);

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = "Branding and logo kit generated successfully.",
                    BrandingLogoId = Guid.NewGuid().ToString(),
                    LogoVariationsUrl = logoVariationsUrl,
                    ColorPaletteUrl = colorPaletteUrl,
                    TypographyUrl = typographyUrl,
                    BrandGuidelinesUrl = brandGuidelinesUrl,
                    SocialMediaKitUrl = socialMediaKitUrl,
                    CompanyName = companyName,
                    Industry = industry,
                    LogoStyle = logoStyle,
                    ColorPalette = colorDirection,
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
                _logger.LogError(ex, "GenerateBrandingLogo timed out.");
                return new ObjectResult(new { Success = false, Message = "Branding generation timed out.", Error = ex.Message })
                { StatusCode = StatusCodes.Status504GatewayTimeout };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateBrandingLogo failed.");
                return new ObjectResult(new { Success = false, Message = "Branding generation failed.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        [Function("SaveBrandingLogo")]
        public async Task<IActionResult> SaveBrandingLogo(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<BrandingLogoRequest>(requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null ||
                string.IsNullOrWhiteSpace(data.BrandingLogoId) ||
                string.IsNullOrWhiteSpace(data.CompanyName) ||
                string.IsNullOrWhiteSpace(data.Version) ||
                string.IsNullOrWhiteSpace(data.TimeStamp))
            {
                return new BadRequestObjectResult("BrandingLogoId, CompanyName, Version, and TimeStamp are required.");
            }

            try
            {
                using var client = CreateDynamoClient();
                string rowId;

                while (true)
                {
                    rowId = Guid.NewGuid().ToString();
                    var check = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM BrandingLogo WHERE Id = ?",
                        Parameters = new List<AttributeValue> { new AttributeValue { S = rowId } }
                    });
                    if (!check.Items.Any()) break;
                }

                await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "INSERT INTO BrandingLogo VALUE {'Id': ?, 'BrandingLogoId': ?, 'CompanyName': ?, 'Version': ?, 'TimeStamp': ?}",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = rowId },
                        new AttributeValue { S = data.BrandingLogoId },
                        new AttributeValue { S = data.CompanyName },
                        new AttributeValue { S = data.Version },
                        new AttributeValue { S = data.TimeStamp }
                    }
                });

                return new OkObjectResult(new
                {
                    Message = "BrandingLogo saved successfully.",
                    Id = rowId,
                    data.BrandingLogoId,
                    data.CompanyName,
                    data.Version,
                    data.TimeStamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving BrandingLogo failed.");
                return new ObjectResult(new { Message = "Saving BrandingLogo failed.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        [Function("GetBrandingLogosByCompany")]
        public async Task<IActionResult> GetBrandingLogosByCompany(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<GetBrandingLogosByCompanyRequest>(requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (string.IsNullOrWhiteSpace(data?.CompanyName))
                return new BadRequestObjectResult("CompanyName is required.");

            try
            {
                using var client = CreateDynamoClient();
                var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM BrandingLogo WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue> { new AttributeValue { S = data.CompanyName } }
                });

                var records = result.Items.Select(item => new
                {
                    Id = GetString(item, "Id"),
                    BrandingLogoId = GetString(item, "BrandingLogoId"),
                    CompanyName = GetString(item, "CompanyName"),
                    Version = GetString(item, "Version"),
                    TimeStamp = GetString(item, "TimeStamp")
                })
                .OrderByDescending(x => DateTime.TryParse(x.TimeStamp, out var dt) ? dt : DateTime.MinValue)
                .ToList();

                return new OkObjectResult(new
                {
                    Message = "BrandingLogo records retrieved successfully.",
                    Count = records.Count,
                    Data = records
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching BrandingLogo records failed.");
                return new ObjectResult(new { Message = "Fetching BrandingLogo records failed.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        [Function("SaveBrandingLogoAssets")]
        public async Task<IActionResult> SaveBrandingLogoAssets(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req)) return new UnauthorizedResult();

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveBrandingLogoAssetsRequest>(requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null || string.IsNullOrWhiteSpace(data.CompanyName) || string.IsNullOrWhiteSpace(data.Version))
                return new BadRequestObjectResult("CompanyName and Version are required.");

            try
            {
                var safeCompanyName = SanitizeName(data.CompanyName);
                var safeVersion = SanitizeVersion(data.Version);
                var folderName = $"{safeCompanyName}-branding-logo-v{safeVersion}";

                var inputs = new Dictionary<string, string?>
                {
                    ["logo-variations.png"] = data.LogoVariationsImageUrl,
                    ["color-palette.png"] = data.ColorPaletteImageUrl,
                    ["typography.png"] = data.TypographyImageUrl,
                    ["brand-guidelines.png"] = data.BrandGuidelinesImageUrl,
                    ["social-media-kit.png"] = data.SocialMediaKitImageUrl
                };

                var useLocal = IsLocal(req);
                var savedAssets = new Dictionary<string, string>();

                foreach (var item in inputs.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
                {
                    var (bytes, contentType) = await ReadImageBytesAsync(item.Value!);
                    if (bytes.Length == 0) throw new InvalidOperationException($"{item.Key} was empty.");

                    string url;
                    if (useLocal)
                    {
                        // Required local layout:
                        // wwwroot/SavedImages/{Company}-branding-logo-v{Version}/{asset}.png
                        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "SavedImages", folderName);
                        Directory.CreateDirectory(folderPath);
                        var filePath = Path.Combine(folderPath, item.Key);
                        await System.IO.File.WriteAllBytesAsync(filePath, bytes);

                        var rootUrl = $"{req.Scheme}://{req.Host.Host}";
                        url = $"{rootUrl}/{folderName}/{item.Key}";
                        _logger.LogInformation("Branding asset saved locally. File={FilePath}, Url={Url}", filePath, url);
                    }
                    else
                    {
                        var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                        var region = Environment.GetEnvironmentVariable("AWS_REGION");
                        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(region) ||
                            string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
                            throw new InvalidOperationException("S3 configuration is incomplete.");

                        using var s3 = new AmazonS3Client(accessKey, secretKey,
                            new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });

                        var key = $"{folderName}/{item.Key}";
                        using var stream = new MemoryStream(bytes);
                        var put = new PutObjectRequest
                        {
                            BucketName = bucketName,
                            Key = key,
                            InputStream = stream,
                            ContentType = contentType
                        };
                        put.Headers["Content-Disposition"] = "inline";
                        await s3.PutObjectAsync(put);
                        url = $"https://{bucketName}.s3.{region}.amazonaws.com/{key}";
                    }

                    savedAssets[item.Key] = url;
                }

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = useLocal ? "Branding assets saved locally." : "Branding assets saved to S3.",
                    FolderName = folderName,
                    Assets = savedAssets,
                    Mode = useLocal ? "LOCAL" : "S3"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving branding assets failed.");
                return new ObjectResult(new { Success = false, Message = "Saving branding assets failed.", Error = ex.Message })
                { StatusCode = StatusCodes.Status500InternalServerError };
            }
        }

        private async Task<string> GenerateBrandAssetAsync(HttpClient client, string imageUrl, string prompt,
            string userId, string projectId, string? funnelId, string generationJobId, string assetType)
        {
            var operationId = $"{generationJobId}-{assetType}";
            var usageId = await _usage.StartAsync(new AIGenerationUsage
            {
                UserId = userId, ProjectId = projectId, FunnelId = funnelId,
                GenerationJobId = generationJobId, ProviderTaskId = operationId,
                GenerationCategory = "PaidWorkspace", DeliverableType = "BrandingLogo",
                Feature = "branding_logo", UsageScope = "Paid", Operation = "BrandingLogoGeneration",
                Provider = "OpenAI", Model = "gpt-5.6", ImageCount = 1,
                PricingVersion = AIProviderPricingService.CurrentVersion
            });
            var payload = new
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
                            new { type = "input_image", image_url = imageUrl, detail = "high" }
                        }
                    }
                },
                tools = new object[] { new { type = "image_generation", action = "generate" } }
            };

            var json = JsonConvert.SerializeObject(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("https://api.openai.com/v1/responses", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                await _usage.CompleteFailureAsync(usageId, "ProviderError");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI image generation failed: {responseJson}");

            var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            await _usage.CompleteOpenAiAsync(usageId, responseJson, "gpt-5.6", "BrandingLogoGeneration", requestId);

            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("output", out var output))
                throw new InvalidOperationException("OpenAI response did not contain output.");

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) ||
                    !string.Equals(type.GetString(), "image_generation_call", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.TryGetProperty("result", out var result))
                {
                    var base64 = result.GetString();
                    if (!string.IsNullOrWhiteSpace(base64))
                        return $"data:image/png;base64,{base64}";
                }
            }

            throw new InvalidOperationException("OpenAI completed the request without returning an image_generation_call result.");
        }

        private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
        {
            var username = _jwt.ResolveUsername(request);
            return string.IsNullOrWhiteSpace(username)
                ? null
                : await _attribution.ResolveUserIdAsync(username);
        }

        private static string SharedContext(string companyName, string industry, string description, string creativeDirection)
            => $@"
COMPANY: {companyName}
INDUSTRY: {industry}
COMPANY DESCRIPTION: {description}
CREATIVE DIRECTION: {creativeDirection}

Use the supplied image only as a visual reference. Study its colors, forms, composition, graphic language, typography personality, imagery, and brand tone. Create NEW brand assets that feel intentionally related to the reference without simply returning or framing the original image. Do not create a browser, laptop, phone mockup, design-editor screenshot, or presentation UI. Create the actual requested branding artwork.";

        private static string BuildLogoVariationsPrompt(string companyName, string industry, string logoStyle, string colorDirection, string description, string creativeDirection)
            => $@"Create a professional LOGO VARIATIONS sheet for the brand. {SharedContext(companyName, industry, description, creativeDirection)}
Requested logo style: {logoStyle}. Color direction: {colorDirection}.
Show a cohesive logo system with: primary logo, horizontal lockup, stacked lockup, icon/mark, monochrome dark, monochrome light/reversed, and simplified small-size mark. Make the company name '{companyName}' legible. Keep the page clean, premium, and presentation-ready, with neutral spacing and no unnecessary mockup devices.";

        private static string BuildColorPalettePrompt(string companyName, string industry, string colorDirection, string description, string creativeDirection)
            => $@"Create a professional COLOR PALETTE brand sheet. {SharedContext(companyName, industry, description, creativeDirection)}
Color direction: {colorDirection}. Build a cohesive primary and secondary palette inspired by the reference image. Show large clean color swatches with readable HEX values and useful roles such as Primary, Secondary, Accent, Background, Surface, and Text. Keep it visually polished and specific to '{companyName}'.";

        private static string BuildTypographyPrompt(string companyName, string industry, string logoStyle, string description, string creativeDirection)
            => $@"Create a professional TYPOGRAPHY brand sheet. {SharedContext(companyName, industry, description, creativeDirection)}
Logo style: {logoStyle}. Propose a clear headline/display direction and a complementary body/UI direction. Show the company name '{companyName}', alphabet samples, headline hierarchy, subheading, body text sample, weights, spacing personality, and usage hierarchy. Prioritize readable, correctly rendered text and a refined brand-guideline layout.";

        private static string BuildBrandGuidelinesPrompt(string companyName, string industry, string logoStyle, string colorDirection, string description, string creativeDirection)
            => $@"Create a concise one-page BRAND GUIDELINES sheet. {SharedContext(companyName, industry, description, creativeDirection)}
Logo style: {logoStyle}. Color direction: {colorDirection}. Include practical visual guidance for logo clear space, minimum size, approved backgrounds, basic do/don't examples, color usage, typography hierarchy, image/graphic style, and brand personality. Keep copy short and readable. Make this look like a real professional mini brand standards page for '{companyName}'.";

        private static string BuildSocialMediaKitPrompt(string companyName, string industry, string logoStyle, string colorDirection, string description, string creativeDirection)
            => $@"Create a LOGO SOCIAL MEDIA KIT sheet for '{companyName}'. {SharedContext(companyName, industry, description, creativeDirection)}
Logo style: {logoStyle}. Color direction: {colorDirection}. Show coordinated branded assets for profile/avatar logo, square social post, story/reel cover, channel/banner header, announcement tile, and simple logo watermark treatment. Use the brand identity consistently. The output should be the actual asset sheet, not phone screens or social-app UI mockups.";

        private static bool IsAllowedOrigin(HttpRequest req)
        {
            var origin = req.Headers["Origin"].FirstOrDefault();
            var allowedHost = Environment.GetEnvironmentVariable("HostUrl");
            return !string.IsNullOrWhiteSpace(origin) && origin == allowedHost;
        }

        private static bool IsLocal(HttpRequest req)
            => string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
               || req.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase);

        private static AmazonDynamoDBClient CreateDynamoClient()
        {
            return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
        }

        private static string GetString(Dictionary<string, AttributeValue> item, string key)
            => item.ContainsKey(key) ? item[key].S ?? string.Empty : string.Empty;

        private static string SanitizeName(string value)
        {
            var cleaned = string.Concat(value.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                .Replace(" ", "-")
                .Replace("&", "");
            while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
            return string.IsNullOrWhiteSpace(cleaned) ? "Brand" : cleaned;
        }

        private static string SanitizeVersion(string value)
        {
            var cleaned = string.Concat(value.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                .Replace(" ", "")
                .Replace("v", "", StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrWhiteSpace(cleaned) ? "1" : cleaned;
        }

        private static async Task<(byte[] Bytes, string ContentType)> ReadImageBytesAsync(string imageUrl)
        {
            if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = imageUrl.IndexOf(',');
                if (comma < 0) throw new InvalidOperationException("Invalid data URI.");
                var metadata = imageUrl.Substring(0, comma);
                var base64 = imageUrl.Substring(comma + 1).Replace("\r", "").Replace("\n", "").Trim();
                var contentType = "image/png";
                var semicolon = metadata.IndexOf(';');
                if (metadata.StartsWith("data:") && semicolon > 5)
                    contentType = metadata.Substring(5, semicolon - 5);
                return (Convert.FromBase64String(base64), contentType);
            }

            if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(imageUrl);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType ?? "image/png");
            }

            throw new InvalidOperationException("Image URL must be HTTP/HTTPS or a data:image URI.");
        }
    }

    public class GenerateBrandingLogoRequest
    {
        public string? GenerationJobId { get; set; }
        public string? ProjectId { get; set; }
        public string? FunnelId { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public string SelectedImageId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Industry { get; set; } = string.Empty;
        public string CompanyDescription { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string LogoStyle { get; set; } = string.Empty;
        public string ColorPalette { get; set; } = string.Empty;
        public bool IncludeLogoVariations { get; set; } = true;
        public bool IncludeColorPalette { get; set; } = true;
        public bool IncludeTypography { get; set; } = true;
        public bool GenerateBrandGuidelines { get; set; } = true;
        public bool IncludeSocialMediaKit { get; set; } = true;
        public string UserId { get; set; } = string.Empty;
        public string CompanyIdeaId { get; set; } = string.Empty;
        public string PromptId { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }

    public class BrandingLogoRequest
    {
        public string BrandingLogoId { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string TimeStamp { get; set; } = string.Empty;
    }

    public class GetBrandingLogosByCompanyRequest
    {
        public string CompanyName { get; set; } = string.Empty;
    }

    public class SaveBrandingLogoAssetsRequest
    {
        public string CompanyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string LogoVariationsImageUrl { get; set; } = string.Empty;
        public string ColorPaletteImageUrl { get; set; } = string.Empty;
        public string TypographyImageUrl { get; set; } = string.Empty;
        public string BrandGuidelinesImageUrl { get; set; } = string.Empty;
        public string SocialMediaKitImageUrl { get; set; } = string.Empty;
    }
}

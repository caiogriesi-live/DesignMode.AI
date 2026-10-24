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
using DesignMode.AI.Server.Security;
using DesignMode.AI.Shared.Models;

namespace DesignMode.AI.Server
{
    public class GenerateSocialMediaPostFunction
    {
        private readonly ILogger<GenerateSocialMediaPostFunction> _logger;
        private readonly AttributionStore _attribution;
        private readonly JwtUserResolver _jwt;
        private readonly AIUsageSummaryService _summary;
        private readonly AIUsageRecorder _usage;
        private readonly Func<HttpClient> _generationClient;

        public GenerateSocialMediaPostFunction(ILogger<GenerateSocialMediaPostFunction> logger, AttributionStore attribution,
            JwtUserResolver jwt, AIUsageSummaryService summary, AIUsageRecorder usage, Func<HttpClient>? generationClient = null)
        {
            _logger = logger;
            _attribution = attribution;
            _jwt = jwt;
            _summary = summary;
            _usage = usage;
            _generationClient = generationClient ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        }

        [Function("GenerateSocialMediaPost")]
        public async Task<IActionResult> GenerateSocialMediaPost(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                var unauthorized = VerifyOrigin(req);
                if (unauthorized != null) return unauthorized;

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return new BadRequestObjectResult("Request body is required.");

                GenerateSocialMediaPostRequest data;
                try
                {
                    data = JsonConvert.DeserializeObject<GenerateSocialMediaPostRequest>(body);
                }
                catch (Exception ex)
                {
                    return new BadRequestObjectResult($"Unable to parse request: {ex.Message}");
                }

                if (data == null)
                    return new BadRequestObjectResult("Social media post request is required.");

                if (string.IsNullOrWhiteSpace(data.ImageUrl))
                    return new BadRequestObjectResult("ImageUrl is required.");

                var claimedUserId = _jwt.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? null
                    : await _attribution.ResolveClaimedUserIdAsync(claimedUserId);
                if (string.IsNullOrWhiteSpace(userId))
                    userId = await ResolveLegacyJwtUserIdAsync(req);
                var generationProject = string.IsNullOrWhiteSpace(userId) ? null :
                    await _attribution.GetOwnedGenerationProjectAsync(userId, data.ProjectId ?? string.Empty);
                if (generationProject is null) return new UnauthorizedResult();
                data.ProjectId = generationProject.ProjectId;
                data.FunnelId = generationProject.FunnelId;
                var quota = await _summary.EvaluateAsync(userId!, "social_post");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };
                var jobId = string.IsNullOrWhiteSpace(data.GenerationJobId) ? Guid.NewGuid().ToString() : data.GenerationJobId;
                var usageId = await _usage.StartAsync(new AIGenerationUsage
                {
                    UserId = userId, ProjectId = data.ProjectId, FunnelId = data.FunnelId, GenerationJobId = jobId!,
                    ProviderTaskId = jobId, GenerationCategory = "PaidWorkspace", DeliverableType = "SocialAsset",
                    Feature = "social_post", UsageScope = "Paid", GenerationUnits = _summary.UnitsFor("social_post"),
                    Operation = "SocialAssetGeneration", Provider = "OpenAI", Model = "gpt-5.6",
                    PricingVersion = AIProviderPricingService.CurrentVersion, ImageCount = 1
                });

                string companyName = CleanDefault(data.CompanyName, "Brand");
                string industry = CleanDefault(data.Industry, "General");
                string companyDescription = data.CompanyDescription?.Trim() ?? "";
                string mainPrompt = data.Prompt?.Trim() ?? "";
                string platform = CleanDefault(data.Platform, "Instagram");
                string postType = CleanDefault(data.PostType, "SingleImage");
                string aspectRatio = CleanDefault(data.AspectRatio, "1:1");
                string visualStyle = CleanDefault(data.VisualStyle, "BrandMatched");
                string suppliedCaption = data.Caption?.Trim() ?? "";
                string callToAction = data.CallToAction?.Trim() ?? "";
                int hashtagCount = Math.Clamp(data.HashtagCount, 0, 30);

                string apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    await _usage.CompleteFailureAsync(usageId, "ProviderNotConfigured");
                    return new BadRequestObjectResult("OpenAI API key is not configured.");
                }

                string prompt = $@"
Analyze the supplied reference image and create ONE completely new, polished social media post graphic inspired by its branding, colors, typography personality, imagery, composition, visual hierarchy, and overall art direction.

IMPORTANT
The supplied image is a VISUAL REFERENCE for the new social post.
Do not simply return the original image.
Do not place the original image inside a frame.
Do not make a screenshot of a website, phone, social network UI, browser, design editor, laptop, or presentation board.
Generate the finished social media artwork itself.

BRAND INFORMATION
Company: {companyName}
Industry: {industry}
Company description: {companyDescription}
Brand / creative direction: {mainPrompt}

SOCIAL POST SPECIFICATIONS
Platform: {platform}
Post type: {ReadablePostType(postType)}
Aspect ratio: {aspectRatio}
Visual style: {ReadableVisualStyle(visualStyle)}

CONTENT DIRECTION
User caption / campaign direction: {suppliedCaption}
Call to action: {callToAction}
Include CTA: {BoolText(data.IncludeCTA)}
Include brand identity / logo treatment: {BoolText(data.IncludeBrandLogo)}
Allow text on the graphic: {BoolText(data.IncludeTextOnImage)}
Generate hashtags: {BoolText(data.IncludeHashtags)}
Requested hashtag count: {hashtagCount}

DESIGN INSTRUCTIONS
Study the supplied reference image carefully and use it as inspiration for brand colors, color relationships, typography personality, illustration or photography style, shapes, patterns, spacing, composition, visual hierarchy, and brand personality.

Create a NEW social media graphic that feels like it belongs to the same brand, but do not merely copy the source image.

The final graphic should be intentionally composed for {platform} and the requested {aspectRatio} aspect ratio.
The design must look professionally art-directed, commercially usable, balanced, readable, and visually engaging.

If text is allowed on the graphic:
- Keep copy concise.
- Use only meaningful text connected to the supplied campaign direction, company, or CTA.
- Avoid random placeholder words and fake lorem ipsum.
- Prioritize correct spelling and legibility.

If text is not allowed on the graphic:
- Do not add headlines, captions, CTA text, or decorative fake text.

If brand identity is requested:
- Use the visual identity of the supplied reference image to create an appropriate brand-mark treatment for {companyName}.
- If an obvious logo exists in the source image, preserve its visual character without copying the entire source image.

If CTA is requested and a CTA value is provided, use it naturally where appropriate:
{callToAction}

DO NOT INVENT unsupported legal claims, prices, discounts, certifications, awards, statistics, guarantees, contact information, or product facts.

OUTPUT IMAGE
Generate ONE completed social media post image only.
The artwork should fill the image canvas cleanly.
Do not include browser chrome, app UI, mockup devices, hands holding a phone, design software, watermarks, or presentation framing.
";

                using var client = _generationClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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
                    tools = new object[]
                    {
                        new { type = "image_generation", action = "generate" }
                    }
                };

                string requestJson = JsonConvert.SerializeObject(requestPayload);
                using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await client.PostAsync("https://api.openai.com/v1/responses", content);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    await _usage.CompleteFailureAsync(usageId, response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "InvalidRequest" : "ProviderError");
                    _logger.LogError("OpenAI social media generation failed: {Response}", responseJson);
                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI social media post generation failed.",
                        Error = responseJson
                    });
                }

                var providerRequestId = response.Headers.TryGetValues("x-request-id", out var requestIds) ? requestIds.FirstOrDefault() : null;
                await _usage.CompleteOpenAiAsync(usageId, responseJson, "gpt-5.6", "SocialAssetGeneration", providerRequestId);

                string generatedImageBase64 = null;
                string outputText = "";

                using (JsonDocument document = JsonDocument.Parse(responseJson))
                {
                    if (!document.RootElement.TryGetProperty("output", out JsonElement output))
                        return new BadRequestObjectResult("OpenAI response did not contain an output array.");

                    foreach (JsonElement item in output.EnumerateArray())
                    {
                        if (!item.TryGetProperty("type", out JsonElement typeElement))
                            continue;

                        string type = typeElement.GetString() ?? "";

                        if (type.Equals("image_generation_call", StringComparison.OrdinalIgnoreCase) &&
                            item.TryGetProperty("result", out JsonElement resultElement))
                        {
                            generatedImageBase64 = resultElement.GetString();
                        }

                        if (type.Equals("message", StringComparison.OrdinalIgnoreCase) &&
                            item.TryGetProperty("content", out JsonElement messageContent) &&
                            messageContent.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement contentItem in messageContent.EnumerateArray())
                            {
                                if (contentItem.TryGetProperty("type", out JsonElement contentType) &&
                                    contentType.GetString() == "output_text" &&
                                    contentItem.TryGetProperty("text", out JsonElement textElement))
                                {
                                    outputText += textElement.GetString();
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedImageBase64))
                {
                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI completed the request but did not return a generated social media image."
                    });
                }

                string imageDataUrl = $"data:image/png;base64,{generatedImageBase64}";

                // The image-generation call may or may not also return text.
                // Keep user-supplied caption as the reliable fallback.
                string caption = !string.IsNullOrWhiteSpace(suppliedCaption)
                    ? suppliedCaption
                    : ExtractCaption(outputText);

                string hashtags = data.IncludeHashtags
                    ? ExtractHashtags(outputText, hashtagCount)
                    : "";

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = "Social media post generated successfully.",
                    PreviewUrl = imageDataUrl,
                    DownloadUrl = imageDataUrl,
                    ImageBase64 = generatedImageBase64,
                    MimeType = "image/png",
                    FileName = BuildFileName(companyName, platform),
                    SocialPostId = Guid.NewGuid().ToString(),
                    SourceImageUrl = data.ImageUrl,
                    CompanyName = companyName,
                    Industry = industry,
                    Platform = platform,
                    PostType = postType,
                    AspectRatio = aspectRatio,
                    Caption = caption,
                    Hashtags = hashtags,
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
                _logger.LogError(ex, "GenerateSocialMediaPost timed out.");
                return new BadRequestObjectResult(new
                {
                    Success = false,
                    Message = "Social media post generation timed out. Please try again.",
                    Error = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateSocialMediaPost failed.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "An exception occurred while generating the social media post.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        [Function("SaveSocialMediaPostImage")]
        public async Task<IActionResult> SaveSocialMediaPostImage(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                var unauthorized = VerifyOrigin(req);
                if (unauthorized != null) return unauthorized;

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<SaveSocialMediaPostImageRequest>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null)
                    return new BadRequestObjectResult("Invalid social media image data.");

                if (string.IsNullOrWhiteSpace(data.ImageUrl) ||
                    string.IsNullOrWhiteSpace(data.CompanyName) ||
                    string.IsNullOrWhiteSpace(data.Platform) ||
                    string.IsNullOrWhiteSpace(data.Version))
                {
                    return new BadRequestObjectResult("ImageUrl, CompanyName, Platform, and Version are required.");
                }

                string safeCompanyName = SanitizePathPart(data.CompanyName);
                string safePlatform = SanitizePathPart(data.Platform).ToLowerInvariant();
                string safeVersion = SanitizeVersion(data.Version);

                if (string.IsNullOrWhiteSpace(safeCompanyName) ||
                    string.IsNullOrWhiteSpace(safePlatform) ||
                    string.IsNullOrWhiteSpace(safeVersion))
                {
                    return new BadRequestObjectResult("CompanyName, Platform, or Version is invalid.");
                }

                string folderName = $"{safeCompanyName}-social-{safePlatform}-v{safeVersion}";
                string fileName = "social-post.png";

                (byte[] imageBytes, string contentType) = await ReadImageBytesAsync(data.ImageUrl);
                if (imageBytes == null || imageBytes.Length == 0)
                    throw new InvalidOperationException("Generated social media image was empty.");

                var useLocal =
                    string.Equals(
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                        "Development",
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    req.Host.Host.Contains(
                        "localhost",
                        StringComparison.OrdinalIgnoreCase);

                string filePath;
                string webUrl;

                if (useLocal)
                {
                    string wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    string savedImagesPath = Path.Combine(wwwrootPath, "SavedImages");
                    string folderPath = Path.Combine(savedImagesPath, folderName);
                    Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, fileName);
                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    // IMPORTANT: SavedImages is part of the public URL too.
                    string rootUrl = $"{req.Scheme}://{req.Host.Host}";
                    webUrl = $"{rootUrl}/{folderName}/{fileName}";

                    _logger.LogInformation("Social post saved locally. File={FilePath}, Url={WebUrl}", filePath, webUrl);
                }
                else
                {
                    string bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    string region = Environment.GetEnvironmentVariable("AWS_REGION");
                    string accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    string secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    if (string.IsNullOrWhiteSpace(bucketName)) throw new InvalidOperationException("S3_BUCKET_NAME is not configured.");
                    if (string.IsNullOrWhiteSpace(region)) throw new InvalidOperationException("AWS_REGION is not configured.");
                    if (string.IsNullOrWhiteSpace(accessKey)) throw new InvalidOperationException("AWS_ACCESS_KEY_ID is not configured.");
                    if (string.IsNullOrWhiteSpace(secretKey)) throw new InvalidOperationException("AWS_SECRET_ACCESS_KEY is not configured.");

                    var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
                    using var s3Client = new AmazonS3Client(accessKey, secretKey, config);

                    // REQUIRED: put every version folder under SavedImages/.
                    string keyName = $"{folderName}/{fileName}";

                    using var stream = new MemoryStream(imageBytes);
                    var putRequest = new PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = contentType
                    };
                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";

                    _logger.LogInformation("Social post saved to S3. Key={Key}, Url={WebUrl}", keyName, webUrl);
                }

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = useLocal ? "Social media post image saved locally." : "Social media post image saved to S3.",
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
                _logger.LogError(ex, "Failed saving social media post image.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error saving social media post image.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        [Function("SaveSocialMediaPost")]
        public async Task<IActionResult> SaveSocialMediaPost(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            var unauthorized = VerifyOrigin(req);
            if (unauthorized != null) return unauthorized;

            string body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveSocialMediaPostRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null)
                return new BadRequestObjectResult("Invalid social media post data.");

            if (string.IsNullOrWhiteSpace(data.SocialId) ||
                string.IsNullOrWhiteSpace(data.CompanyName) ||
                string.IsNullOrWhiteSpace(data.Platform) ||
                string.IsNullOrWhiteSpace(data.Version) ||
                string.IsNullOrWhiteSpace(data.TimeStamp) ||
                string.IsNullOrWhiteSpace(data.ImageUrl))
            {
                return new BadRequestObjectResult("SocialId, CompanyName, Platform, Version, TimeStamp, and ImageUrl are required.");
            }

            try
            {
                using var client = CreateDynamoDbClient();
                string id = Guid.NewGuid().ToString();

                var request = new ExecuteStatementRequest
                {
                    Statement =
                        "INSERT INTO Social VALUE {" +
                        "'Id': ?, " +
                        "'SocialId': ?, " +
                        "'WebsiteId': ?, " +
                        "'CompanyName': ?, " +
                        "'Platform': ?, " +
                        "'Version': ?, " +
                        "'TimeStamp': ?, " +
                        "'ImageUrl': ?, " +
                        "'Caption': ?, " +
                        "'Hashtags': ?, " +
                        "'PostType': ?, " +
                        "'AspectRatio': ?, " +
                        "'SourceImageUrl': ?" +
                        "}",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = id },
                        new AttributeValue { S = data.SocialId },
                        new AttributeValue { S = "Website" },
                        new AttributeValue { S = data.CompanyName },
                        new AttributeValue { S = data.Platform },
                        new AttributeValue { S = data.Version },
                        new AttributeValue { S = data.TimeStamp },
                        new AttributeValue { S = data.ImageUrl },
                        new AttributeValue { S = data.Caption ?? "" },
                        new AttributeValue { S = data.Hashtags ?? "" },
                        new AttributeValue { S = data.PostType ?? "" },
                        new AttributeValue { S = data.AspectRatio ?? "" },
                        new AttributeValue { S = data.SourceImageUrl ?? "" }
                    }
                };

                await client.ExecuteStatementAsync(request);

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = "Social media post version saved.",
                    Id = id,
                    data.SocialId,
                    data.Version,
                    data.ImageUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving Social record.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Unable to save social media post record.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        [Function("GetSocialMediaPostsByCompany")]
        public async Task<IActionResult> GetSocialMediaPostsByCompany(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            var unauthorized = VerifyOrigin(req);
            if (unauthorized != null) return unauthorized;

            string body = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<GetSocialMediaPostsByCompanyRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null || string.IsNullOrWhiteSpace(data.CompanyName))
                return new BadRequestObjectResult("CompanyName is required.");

            try
            {
                using var client = CreateDynamoDbClient();

                ExecuteStatementRequest query;
                if (string.IsNullOrWhiteSpace(data.Platform))
                {
                    query = new ExecuteStatementRequest
                    {
                        Statement = "SELECT * FROM Social WHERE CompanyName = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = data.CompanyName }
                        }
                    };
                }
                else
                {
                    query = new ExecuteStatementRequest
                    {
                        Statement = "SELECT * FROM Social WHERE CompanyName = ? AND Platform = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = data.CompanyName },
                            new AttributeValue { S = data.Platform }
                        }
                    };
                }

                var response = await client.ExecuteStatementAsync(query);
                var records = response.Items.Select(ToSocialRecord)
                    .OrderByDescending(x => DateTime.TryParse(x.TimeStamp, out var dt) ? dt : DateTime.MinValue)
                    .ToList();

                return new OkObjectResult(new
                {
                    Message = "Social media posts retrieved.",
                    Count = records.Count,
                    Data = records
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed retrieving Social records.");
                return new ObjectResult(new
                {
                    Message = "Unable to retrieve social media posts.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        private IActionResult VerifyOrigin(HttpRequest req)
        {
            string origin = req.Headers["Origin"].FirstOrDefault();
            string allowedHost = Environment.GetEnvironmentVariable("HostUrl");

            if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(allowedHost) || origin != allowedHost)
                return new UnauthorizedResult();

            return null;
        }

        private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
        {
            var username = _jwt.ResolveUsername(request);
            return string.IsNullOrWhiteSpace(username)
                ? null
                : await _attribution.ResolveUserIdAsync(username);
        }

        private AmazonDynamoDBClient CreateDynamoDbClient()
        {
            return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
        }

        private static SocialRecord ToSocialRecord(Dictionary<string, AttributeValue> item)
        {
            string Get(string key) => item.TryGetValue(key, out var value) ? value.S ?? "" : "";

            return new SocialRecord
            {
                Id = Get("Id"),
                SocialId = Get("SocialId"),
                CompanyName = Get("CompanyName"),
                Platform = Get("Platform"),
                Version = Get("Version"),
                TimeStamp = Get("TimeStamp"),
                ImageUrl = Get("ImageUrl"),
                Caption = Get("Caption"),
                Hashtags = Get("Hashtags"),
                PostType = Get("PostType"),
                AspectRatio = Get("AspectRatio"),
                SourceImageUrl = Get("SourceImageUrl")
            };
        }

        private static async Task<(byte[] Bytes, string ContentType)> ReadImageBytesAsync(string imageUrl)
        {
            if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int commaIndex = imageUrl.IndexOf(',');
                if (commaIndex < 0) throw new InvalidOperationException("Invalid base64 image data URL.");

                string header = imageUrl.Substring(0, commaIndex);
                string base64 = imageUrl.Substring(commaIndex + 1).Replace("\r", "").Replace("\n", "").Trim();
                string contentType = header.Split(';')[0].Replace("data:", "");
                return (Convert.FromBase64String(base64), string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType);
            }

            if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var httpClient = new HttpClient();
                using var response = await httpClient.GetAsync(imageUrl);
                response.EnsureSuccessStatusCode();
                return (
                    await response.Content.ReadAsByteArrayAsync(),
                    response.Content.Headers.ContentType?.MediaType ?? "image/png");
            }

            throw new InvalidOperationException("ImageUrl must be an HTTP/HTTPS URL or a base64 data:image URI.");
        }

        private static string SanitizePathPart(string value)
        {
            var chars = value.Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ' ')
                .ToArray();
            return new string(chars).Replace(" ", "-").Trim('-');
        }

        private static string SanitizeVersion(string value)
        {
            string cleaned = value.Replace("v", "", StringComparison.OrdinalIgnoreCase);
            return new string(cleaned.Where(char.IsLetterOrDigit).ToArray());
        }

        private static string CleanDefault(string value, string defaultValue) =>
            string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

        private static string BoolText(bool value) => value ? "YES" : "NO";

        private static string ReadablePostType(string value) => value switch
        {
            "SingleImage" => "Single image feed post",
            "Story" => "Story or reel cover",
            "Ad" => "Sponsored advertisement",
            "Announcement" => "Brand announcement",
            "Promotion" => "Promotional post",
            _ => value
        };

        private static string ReadableVisualStyle(string value) => value switch
        {
            "BrandMatched" => "Closely match the selected image's brand aesthetic",
            "CleanModern" => "Clean and modern",
            "BoldPromotional" => "Bold and promotional",
            "PremiumEditorial" => "Premium editorial",
            "Minimal" => "Minimal",
            _ => value
        };

        private static string BuildFileName(string companyName, string platform) =>
            $"{SanitizePathPart(companyName)}-{SanitizePathPart(platform).ToLowerInvariant()}-social-post.png";

        private static string ExtractCaption(string outputText)
        {
            if (string.IsNullOrWhiteSpace(outputText)) return "";

            var lines = outputText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !x.StartsWith("#"))
                .ToList();

            return string.Join(" ", lines).Trim();
        }

        private static string ExtractHashtags(string outputText, int maxCount)
        {
            if (string.IsNullOrWhiteSpace(outputText) || maxCount <= 0) return "";

            var tags = outputText
                .Split(new[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.StartsWith("#") && x.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxCount)
                .ToList();

            return string.Join(" ", tags);
        }

        public class GenerateSocialMediaPostRequest
        {
            public string ImageUrl { get; set; } = "";
            public string SelectedImageId { get; set; } = "";
            public string CompanyName { get; set; } = "";
            public string Industry { get; set; } = "";
            public string CompanyDescription { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string Platform { get; set; } = "Instagram";
            public string PostType { get; set; } = "SingleImage";
            public string AspectRatio { get; set; } = "1:1";
            public string VisualStyle { get; set; } = "BrandMatched";
            public string Caption { get; set; } = "";
            public string CallToAction { get; set; } = "";
            public bool IncludeHashtags { get; set; } = true;
            public int HashtagCount { get; set; } = 10;
            public bool IncludeCTA { get; set; } = true;
            public bool IncludeBrandLogo { get; set; } = true;
            public bool IncludeTextOnImage { get; set; } = true;
            public string? GenerationJobId { get; set; }
            public string? ProjectId { get; set; }
            public string? FunnelId { get; set; }
        }

        public class SaveSocialMediaPostImageRequest
        {
            public string ImageUrl { get; set; } = "";
            public string CompanyName { get; set; } = "";
            public string Platform { get; set; } = "";
            public string Version { get; set; } = "";
            public string FileName { get; set; } = "";
        }

        public class SaveSocialMediaPostRequest
        {
            public string SocialId { get; set; } = "";
            public string CompanyName { get; set; } = "";
            public string Platform { get; set; } = "";
            public string Version { get; set; } = "";
            public string TimeStamp { get; set; } = "";
            public string ImageUrl { get; set; } = "";
            public string Caption { get; set; } = "";
            public string Hashtags { get; set; } = "";
            public string PostType { get; set; } = "";
            public string AspectRatio { get; set; } = "";
            public string SourceImageUrl { get; set; } = "";
        }

        public class GetSocialMediaPostsByCompanyRequest
        {
            public string CompanyName { get; set; } = "";
            public string Platform { get; set; } = "";
        }

        public class SocialRecord
        {
            public string Id { get; set; } = "";
            public string SocialId { get; set; } = "";
            public string CompanyName { get; set; } = "";
            public string Platform { get; set; } = "";
            public string Version { get; set; } = "";
            public string TimeStamp { get; set; } = "";
            public string ImageUrl { get; set; } = "";
            public string Caption { get; set; } = "";
            public string Hashtags { get; set; } = "";
            public string PostType { get; set; } = "";
            public string AspectRatio { get; set; } = "";
            public string SourceImageUrl { get; set; } = "";
        }
    }
}

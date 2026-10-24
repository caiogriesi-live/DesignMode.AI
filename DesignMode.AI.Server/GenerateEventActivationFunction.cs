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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Server.Security;

namespace DesignMode.AI.Server
{
    public class GenerateEventActivationFunction
    {
        private readonly ILogger<GenerateEventActivationFunction> _logger;
        private readonly AIUsageRecorder _usage;
        private readonly AttributionStore _attribution;
        private readonly JwtUserResolver _jwt;
        private readonly AIUsageSummaryService? _summary;

        private static readonly Dictionary<string, string> AssetDisplayNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["poster"] = "event poster",
                ["flyer"] = "event flyer",
                ["social-media"] = "social media event graphic",
                ["event-badge"] = "event badge / credential",
                ["merchandise"] = "branded event merchandise graphic",
                ["wayfinding"] = "event wayfinding signage",
                ["booth-table"] = "booth and table graphics",
                ["digital-display"] = "digital event display graphic"
            };

        public GenerateEventActivationFunction(ILogger<GenerateEventActivationFunction> logger, AIUsageRecorder usage, AttributionStore attribution, JwtUserResolver jwt, AIUsageSummaryService? summary = null)
        {
            _logger = logger;
            _usage = usage;
            _attribution = attribution;
            _jwt = jwt;
            _summary = summary;
        }

        // ================================================================
        // GENERATE THE EVENT / ACTIVATION KIT
        // ================================================================
        [Function("GenerateEventActivation")]
        public async Task<IActionResult> GenerateEventActivation(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(requestBody))
                    return new BadRequestObjectResult("Request body is required.");

                GenerateEventActivationRequest? data;
                try
                {
                    data = JsonConvert.DeserializeObject<GenerateEventActivationRequest>(requestBody);
                }
                catch (Exception ex)
                {
                    return new BadRequestObjectResult($"Unable to parse request: {ex.Message}");
                }

                if (data == null)
                    return new BadRequestObjectResult("Event / activation request is required.");

                if (string.IsNullOrWhiteSpace(data.ImageUrl))
                    return new BadRequestObjectResult("ImageUrl is required.");

                if (data.AssetTypes == null || data.AssetTypes.Count == 0)
                    return new BadRequestObjectResult("At least one AssetType is required.");

                var claimedUserId = _jwt.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? await ResolveLegacyJwtUserIdAsync(req)
                    : await _attribution.ResolveClaimedUserIdAsync(claimedUserId);
                var generationProject = string.IsNullOrWhiteSpace(userId) ? null :
                    await _attribution.GetOwnedGenerationProjectAsync(userId, data.ProjectId ?? string.Empty);
                if (generationProject is null) return new UnauthorizedResult();
                data.ProjectId = generationProject.ProjectId;
                data.FunnelId = generationProject.FunnelId;
                var quota = await _summary!.EvaluateAsync(userId!, "event_activation");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };

                var generationJobId = string.IsNullOrWhiteSpace(data.GenerationJobId)
                    ? Guid.NewGuid().ToString()
                    : data.GenerationJobId;

                string apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(apiKey))
                    return new BadRequestObjectResult("OpenAI API key is not configured.");

                string companyName = string.IsNullOrWhiteSpace(data.Name) ? "Brand" : data.Name.Trim();
                string industry = string.IsNullOrWhiteSpace(data.Industry) ? "General" : data.Industry.Trim();
                string companyDescription = data.CompanyDescription?.Trim() ?? string.Empty;
                string mainPrompt = data.Prompt?.Trim() ?? string.Empty;
                string eventType = string.IsNullOrWhiteSpace(data.EventType) ? "Event" : data.EventType.Trim();
                string materialType = string.IsNullOrWhiteSpace(data.MaterialType) ? "FullKit" : data.MaterialType.Trim();
                string eventDate = data.EventDate?.Trim() ?? string.Empty;
                string eventLocation = data.EventLocation?.Trim() ?? string.Empty;
                string eventDescription = data.EventDescription?.Trim() ?? string.Empty;

                var normalizedAssetTypes = data.AssetTypes
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(NormalizeAssetType)
                    .Where(x => AssetDisplayNames.ContainsKey(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (normalizedAssetTypes.Count == 0)
                    return new BadRequestObjectResult("No supported AssetTypes were supplied.");

                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(5)
                };
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);

                var creditUsageId = await _usage.StartCreditJobAsync(userId!, data.ProjectId, data.FunnelId,
                    generationJobId!, "event_activation", _summary!.UnitsFor("event_activation"));
                // Generate each deliverable independently so every saved file is
                // an actual production asset, not a single presentation board.
                var generationTasks = normalizedAssetTypes.Select(assetType =>
                    GenerateAssetAsync(
                        httpClient,
                        data.ImageUrl,
                        companyName,
                        industry,
                        companyDescription,
                        mainPrompt,
                        eventType,
                        materialType,
                        eventDate,
                        eventLocation,
                        eventDescription,
                        assetType,
                        userId,
                        data.ProjectId,
                        data.FunnelId,
                        generationJobId));

                var generated = await Task.WhenAll(generationTasks);
                var assets = generated.Where(x => x != null).Cast<GeneratedEventAssetResponse>().ToList();

                if (assets.Count == 0)
                {
                    await _usage.CompleteFailureAsync(creditUsageId, "NoAssetsReturned");
                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI completed the request but no event / activation images were returned."
                    });
                }

                await _usage.CompleteSuccessAsync(creditUsageId);
                return new OkObjectResult(new
                {
                    Success = true,
                    Message = $"Generated {assets.Count} event / activation assets successfully.",
                    EventActivationId = Guid.NewGuid().ToString(),
                    CompanyName = companyName,
                    EventType = eventType,
                    Assets = assets,
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
                _logger.LogError(ex, "GenerateEventActivation timed out.");
                return new BadRequestObjectResult(new
                {
                    Success = false,
                    Message = "Event / activation generation timed out. Try fewer assets or try again.",
                    Error = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateEventActivation failed.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "An exception occurred while generating the event / activation kit.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        private async Task<GeneratedEventAssetResponse?> GenerateAssetAsync(
            HttpClient client,
            string imageUrl,
            string companyName,
            string industry,
            string companyDescription,
            string mainPrompt,
            string eventType,
            string materialType,
            string eventDate,
            string eventLocation,
            string eventDescription,
            string assetType,
            string userId,
            string projectId,
            string? funnelId,
            string generationJobId)
        {
            var operationId = $"{generationJobId}-{assetType}";
            var usageId = await _usage.StartAsync(new AIGenerationUsage
            {
                UserId = userId, ProjectId = projectId, FunnelId = funnelId,
                GenerationJobId = generationJobId, ProviderTaskId = operationId,
                GenerationCategory = "PaidWorkspace", DeliverableType = "EventActivation",
                Feature = "event_activation", UsageScope = "Paid", Operation = "EventActivationGeneration",
                Provider = "OpenAI", Model = "gpt-5.6", ImageCount = 1,
                PricingVersion = AIProviderPricingService.CurrentVersion
            });
            string displayName = AssetDisplayNames[assetType];
            string prompt = BuildAssetPrompt(
                companyName,
                industry,
                companyDescription,
                mainPrompt,
                eventType,
                materialType,
                eventDate,
                eventLocation,
                eventDescription,
                assetType,
                displayName);

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
                            new { type = "input_image", image_url = imageUrl, detail = "high" }
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
            using var response = await client.PostAsync("https://api.openai.com/v1/responses", content);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                await _usage.CompleteFailureAsync(usageId, "ProviderError");
                _logger.LogError(
                    "OpenAI event asset generation failed. AssetType={AssetType}, Status={Status}, Body={Body}",
                    assetType,
                    response.StatusCode,
                    responseJson);
                return null;
            }

            var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            await _usage.CompleteOpenAiAsync(usageId, responseJson, "gpt-5.6", "EventActivationGeneration", requestId);

            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("output", out JsonElement outputElement))
                return null;

            string? generatedImageBase64 = null;
            foreach (var outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("type", out JsonElement typeElement))
                    continue;

                if (!string.Equals(typeElement.GetString(), "image_generation_call", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!outputItem.TryGetProperty("result", out JsonElement resultElement))
                    continue;

                generatedImageBase64 = resultElement.GetString();
                if (!string.IsNullOrWhiteSpace(generatedImageBase64))
                    break;
            }

            if (string.IsNullOrWhiteSpace(generatedImageBase64))
                return null;

            string imageDataUrl = $"data:image/png;base64,{generatedImageBase64}";
            return new GeneratedEventAssetResponse
            {
                AssetType = assetType,
                DisplayName = displayName,
                PreviewUrl = imageDataUrl,
                DownloadUrl = imageDataUrl,
                ImageBase64 = generatedImageBase64,
                MimeType = "image/png",
                FileName = GetAssetFileName(assetType)
            };
        }

        private static string BuildAssetPrompt(
            string companyName,
            string industry,
            string companyDescription,
            string mainPrompt,
            string eventType,
            string materialType,
            string eventDate,
            string eventLocation,
            string eventDescription,
            string assetType,
            string displayName)
        {
            string formatInstructions = assetType switch
            {
                "poster" => "Create a finished event poster with a strong headline hierarchy, date/location area, brand presence, and compelling campaign artwork. Portrait-oriented unless the reference clearly demands another proportion.",
                "flyer" => "Create a polished event flyer suitable for print and digital sharing. Use clear hierarchy for event name, date, location, short description, and call to action.",
                "social-media" => "Create a premium social media event graphic optimized for feed promotion. Use bold, legible event messaging and a composition that crops safely for common social placements.",
                "event-badge" => "Create a professional event badge / credential design with brand identity, attendee-name placeholder area, role area, event name, and clean access-badge hierarchy. Show the flat badge artwork itself, not a lanyard mockup.",
                "merchandise" => "Create a standalone branded event merchandise graphic suitable for apparel, tote bags, or promotional items. Produce the artwork itself without placing it on a person or lifestyle mockup.",
                "wayfinding" => "Create a professional event wayfinding sign with a clear destination hierarchy, directional arrow, event branding, and high-distance legibility. Show the sign artwork itself, not a photographed venue mockup.",
                "booth-table" => "Create coordinated booth and table graphics as one flat production-ready environmental graphic composition, emphasizing branded backdrop/panel and table-front visual treatment. Avoid showing people or a photorealistic trade-show scene.",
                "digital-display" => "Create a professional event digital display graphic for lobby screens, stage screens, or venue monitors. Use a widescreen composition, strong hierarchy, and high-distance legibility.",
                _ => "Create a polished, production-ready event / activation graphic."
            };

            return $@"
Analyze the supplied reference image and create a NEW professional {displayName} that belongs to the same brand and event visual system.

IMPORTANT
The supplied image is a VISUAL REFERENCE only.
Do not simply return the original image.
Do not place the original image inside a frame or mockup.
Do not create a screenshot, browser, design editor, laptop, phone, presentation board, or UI.
Generate the actual final event asset artwork.

COMPANY
Company name: {companyName}
Industry: {industry}
Company description: {companyDescription}
Brand / creative direction: {mainPrompt}

EVENT
Event type: {eventType}
Primary format: {materialType}
Event date: {eventDate}
Event location: {eventLocation}
Event description / tagline: {eventDescription}

REQUESTED ASSET
Asset type: {displayName}
{formatInstructions}

VISUAL SYSTEM
Study the reference image carefully and use it as inspiration for:
- brand colors and color relationships
- typography personality
- graphic and illustration style
- photography treatment
- shapes, patterns, borders, and decorative elements
- visual hierarchy
- spacing and composition
- brand personality
- overall art direction

Create a completely new design that feels unmistakably related to the reference image while being purpose-built for this event deliverable.

TEXT ACCURACY
Prioritize readable, correct text for the company name and supplied event details.
Do not invent addresses, sponsors, legal statements, dates, venues, pricing, claims, certifications, URLs, QR destinations, or event facts that were not supplied.
Avoid lorem ipsum, meaningless filler, and random words.
If a field is blank, omit it rather than fabricate it.

QUALITY
The final asset should look like work from an experienced event branding / experiential design team:
- premium
- cohesive
- commercially usable
- balanced
- highly legible
- visually striking
- brand appropriate
- production-ready in appearance

OUTPUT
Generate ONE completed {displayName} image.
The image should primarily contain the flat artwork itself.
Do not show hands, people wearing merchandise, printing equipment, computers, phones, browser chrome, design software, or a presentation mockup.
";
        }

        // ================================================================
        // SAVE VERSION ROW TO EventActivations
        // Fields: Id, EventActivationsId, CompanyName, Version, TimeStamp
        // ================================================================
        [Function("SaveEventActivation")]
        public async Task<IActionResult> SaveEventActivation(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<EventActivationRecordRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null ||
                string.IsNullOrWhiteSpace(data.EventActivationsId) ||
                string.IsNullOrWhiteSpace(data.CompanyName) ||
                string.IsNullOrWhiteSpace(data.Version) ||
                string.IsNullOrWhiteSpace(data.TimeStamp))
            {
                return new BadRequestObjectResult(new
                {
                    Message = "EventActivationsId, CompanyName, Version, and TimeStamp are required."
                });
            }

            try
            {
                using var client = CreateDynamoDbClient();
                string recordId;

                while (true)
                {
                    recordId = Guid.NewGuid().ToString();
                    var check = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM EventActivations WHERE Id = ?",
                        Parameters = new List<AttributeValue> { new() { S = recordId } }
                    });
                    if (check.Items.Count == 0) break;
                }

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement =
                        "INSERT INTO EventActivations VALUE {" +
                        "'Id': ?, " +
                        "'EventActivationsId': ?, " +
                        "'CompanyName': ?, " +
                        "'Version': ?, " +
                        "'TimeStamp': ?}",
                    Parameters = new List<AttributeValue>
                    {
                        new() { S = recordId },
                        new() { S = data.EventActivationsId },
                        new() { S = data.CompanyName },
                        new() { S = data.Version },
                        new() { S = data.TimeStamp }
                    }
                };

                await client.ExecuteStatementAsync(insertRequest);
                return new OkObjectResult(new
                {
                    Message = "Event activation version saved successfully.",
                    Id = recordId,
                    data.EventActivationsId,
                    data.CompanyName,
                    data.Version,
                    data.TimeStamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveEventActivation failed.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Unable to save event activation version.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // ================================================================
        // GET ALL VERSION ROWS FOR A COMPANY
        // ================================================================
        [Function("GetEventActivationsByCompany")]
        public async Task<IActionResult> GetEventActivationsByCompany(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<GetEventActivationsByCompanyRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (string.IsNullOrWhiteSpace(data?.CompanyName))
                return new BadRequestObjectResult("CompanyName is required.");

            try
            {
                using var client = CreateDynamoDbClient();
                var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM EventActivations WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue> { new() { S = data.CompanyName } }
                });

                var rows = result.Items.Select(item => new
                {
                    Id = GetString(item, "Id"),
                    EventActivationsId = GetString(item, "EventActivationsId"),
                    CompanyName = GetString(item, "CompanyName"),
                    Version = GetString(item, "Version"),
                    TimeStamp = GetString(item, "TimeStamp")
                })
                .OrderByDescending(x => DateTime.TryParse(x.TimeStamp, out var dt) ? dt : DateTime.MinValue)
                .ToList();

                return new OkObjectResult(new
                {
                    Message = "Event activation versions retrieved successfully.",
                    Count = rows.Count,
                    Data = rows
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEventActivationsByCompany failed.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Unable to retrieve event activation versions.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // ================================================================
        // SAVE EACH GENERATED PNG
        // LOCAL:
        // wwwroot/SavedImages/{Company}-event-activation-v{Version}/{asset}.png
        // URL:
        // http(s)://host/SavedImages/{Company}-event-activation-v{Version}/{asset}.png
        // ================================================================
        [Function("SaveEventActivationImage")]
        public async Task<IActionResult> SaveEventActivationImage(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveEventActivationImageRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null ||
                string.IsNullOrWhiteSpace(data.ImageUrl) ||
                string.IsNullOrWhiteSpace(data.CompanyName) ||
                string.IsNullOrWhiteSpace(data.Version) ||
                string.IsNullOrWhiteSpace(data.AssetType))
            {
                return new BadRequestObjectResult("ImageUrl, CompanyName, Version, and AssetType are required.");
            }

            try
            {
                bool useLocal =
                    string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase) ||
                    req.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase);

                string safeCompany = SanitizeCompanyName(data.CompanyName);
                string safeVersion = SanitizeVersion(data.Version);
                string safeAssetType = NormalizeAssetType(data.AssetType);

                if (!AssetDisplayNames.ContainsKey(safeAssetType))
                    return new BadRequestObjectResult($"Unsupported AssetType: {data.AssetType}");

                string folderName = $"{safeCompany}-event-activation-v{safeVersion}";
                string fileName = GetAssetFileName(safeAssetType);
                (byte[] imageBytes, string contentType) = await ReadImageBytesAsync(data.ImageUrl);

                string filePath;
                string webUrl;

                if (useLocal)
                {
                    // IMPORTANT: the version folder is physically inside SavedImages.
                    string wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    string savedImagesPath = Path.Combine(wwwrootPath, "SavedImages");
                    string folderPath = Path.Combine(savedImagesPath, folderName);
                    Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, fileName);
                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    string rootUrl = $"{req.Scheme}://{req.Host.Host}";
                    webUrl = $"{rootUrl}/{folderName}/{fileName}";

                    _logger.LogInformation(
                        "Event activation asset saved locally. File={FilePath}, Url={WebUrl}",
                        filePath,
                        webUrl);
                }
                else
                {
                    string bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? string.Empty;
                    string region = Environment.GetEnvironmentVariable("AWS_REGION") ?? string.Empty;
                    string accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? string.Empty;
                    string secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(region) ||
                        string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
                    {
                        throw new InvalidOperationException("S3 configuration is incomplete.");
                    }

                    using var s3Client = new AmazonS3Client(
                        accessKey,
                        secretKey,
                        new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });

                    // Keep SavedImages in the S3 key too so local and production paths match.
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
                }

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = useLocal ? "Event activation asset saved locally." : "Event activation asset saved to S3.",
                    FilePath = filePath,
                    FileName = fileName,
                    FolderName = folderName,
                    AssetType = safeAssetType,
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
                _logger.LogError(ex, "SaveEventActivationImage failed.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error saving event activation image.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // ================================================================
        // HELPERS
        // ================================================================
        private bool IsAllowedOrigin(HttpRequest req)
        {
            string? origin = req.Headers["Origin"].FirstOrDefault();
            string? allowedHost = Environment.GetEnvironmentVariable("HostUrl");
            return !string.IsNullOrWhiteSpace(origin) && origin == allowedHost;
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

        private static string? GetString(Dictionary<string, AttributeValue> item, string key)
        {
            return item.ContainsKey(key) ? item[key].S : null;
        }

        private static string SanitizeCompanyName(string companyName)
        {
            string safe = string.Concat(companyName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                .Replace(" ", "-")
                .Replace("&", "");
            return string.IsNullOrWhiteSpace(safe) ? "Brand" : safe;
        }

        private static string SanitizeVersion(string version)
        {
            string safe = string.Concat(version.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                .Replace(" ", "")
                .Replace("v", "", StringComparison.OrdinalIgnoreCase)
                .ToLowerInvariant();
            return string.IsNullOrWhiteSpace(safe) ? "1" : safe;
        }

        private static string NormalizeAssetType(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        }

        private static string GetAssetFileName(string assetType)
        {
            return $"{NormalizeAssetType(assetType)}.png";
        }

        private static async Task<(byte[] bytes, string contentType)> ReadImageBytesAsync(string imageUrl)
        {
            if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int commaIndex = imageUrl.IndexOf(',');
                if (commaIndex < 0)
                    throw new InvalidOperationException("Invalid base64 image data URI.");

                string metadata = imageUrl.Substring(0, commaIndex);
                string base64Data = imageUrl.Substring(commaIndex + 1).Replace("\r", "").Replace("\n", "");
                string contentType = "image/png";
                if (metadata.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int semicolonIndex = metadata.IndexOf(';');
                    if (semicolonIndex > 5)
                        contentType = metadata.Substring(5, semicolonIndex - 5);
                }
                return (Convert.FromBase64String(base64Data), contentType);
            }

            if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(imageUrl);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
                return (bytes, contentType);
            }

            throw new InvalidOperationException("ImageUrl must be an HTTP/HTTPS URL or a base64 data:image URI.");
        }

        // ================================================================
        // REQUEST / RESPONSE MODELS
        // ================================================================
        public class GenerateEventActivationRequest
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
            public string EventType { get; set; } = string.Empty;
            public string MaterialType { get; set; } = string.Empty;
            public string EventDate { get; set; } = string.Empty;
            public string EventLocation { get; set; } = string.Empty;
            public string EventDescription { get; set; } = string.Empty;
            public List<string> AssetTypes { get; set; } = new();
            public string UserId { get; set; } = string.Empty;
            public string Timestamp { get; set; } = string.Empty;
        }

        public class GeneratedEventAssetResponse
        {
            public string AssetType { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string PreviewUrl { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public string ImageBase64 { get; set; } = string.Empty;
            public string MimeType { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
        }

        public class EventActivationRecordRequest
        {
            public string EventActivationsId { get; set; } = string.Empty;
            public string CompanyName { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string TimeStamp { get; set; } = string.Empty;
        }

        public class GetEventActivationsByCompanyRequest
        {
            public string CompanyName { get; set; } = string.Empty;
        }

        public class SaveEventActivationImageRequest
        {
            public string ImageUrl { get; set; } = string.Empty;
            public string CompanyName { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string AssetType { get; set; } = string.Empty;
        }
    }
}

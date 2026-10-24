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
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
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
    public class PrintMaterialsFunctions
    {
        private readonly ILogger<PrintMaterialsFunctions> _logger;
        private readonly AIUsageRecorder _usage;
        private readonly AttributionStore _attribution;
        private readonly JwtUserResolver _jwt;
        private readonly AIUsageSummaryService? _summary;

        public PrintMaterialsFunctions(ILogger<PrintMaterialsFunctions> logger, AIUsageRecorder usage, AttributionStore attribution, JwtUserResolver jwt, AIUsageSummaryService? summary = null)
        {
            _logger = logger;
            _usage = usage;
            _attribution = attribution;
            _jwt = jwt;
            _summary = summary;
        }

        // ================================================================
        // 1) OPENAI: SELECTED IMAGE -> NEW PRINT ARTWORK -> PDF
        // ================================================================
        [Function("GeneratePrintMaterialPdf")]
        public async Task<IActionResult> GeneratePrintMaterialPdf(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBody))
                    return new BadRequestObjectResult("Request body is required.");

                GeneratePrintMaterialRequest? getData;

                try
                {
                    getData = JsonConvert.DeserializeObject<GeneratePrintMaterialRequest>(requestBody);
                }
                catch (Exception ex)
                {
                    return new BadRequestObjectResult($"Unable to parse request: {ex.Message}");
                }

                if (getData == null)
                    return new BadRequestObjectResult("Print-material request is required.");

                if (string.IsNullOrWhiteSpace(getData.ImageUrl))
                    return new BadRequestObjectResult("ImageUrl is required.");

                var claimedUserId = _jwt.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? await ResolveLegacyJwtUserIdAsync(req)
                    : await _attribution.ResolveClaimedUserIdAsync(claimedUserId);
                var generationProject = string.IsNullOrWhiteSpace(userId) ? null :
                    await _attribution.GetOwnedGenerationProjectAsync(userId, getData.ProjectId ?? string.Empty);
                if (generationProject is null) return new UnauthorizedResult();
                getData.ProjectId = generationProject.ProjectId;
                getData.FunnelId = generationProject.FunnelId;
                var quota = await _summary!.EvaluateAsync(userId!, "print_material");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };

                string companyName = string.IsNullOrWhiteSpace(getData.Name)
                    ? "Brand"
                    : getData.Name.Trim();

                string industry = string.IsNullOrWhiteSpace(getData.Industry)
                    ? "General"
                    : getData.Industry.Trim();

                string companyDescription = string.IsNullOrWhiteSpace(getData.CompanyDescription)
                    ? ""
                    : getData.CompanyDescription.Trim();

                string mainPrompt = string.IsNullOrWhiteSpace(getData.Prompt)
                    ? ""
                    : getData.Prompt.Trim();

                string printType = string.IsNullOrWhiteSpace(getData.PrintType)
                    ? "Brochure"
                    : getData.PrintType.Trim();

                string paperSize = string.IsNullOrWhiteSpace(getData.PaperSize)
                    ? "A4"
                    : getData.PaperSize.Trim();

                string orientation = string.IsNullOrWhiteSpace(getData.Orientation)
                    ? "Portrait"
                    : getData.Orientation.Trim();

                string colorMode = string.IsNullOrWhiteSpace(getData.ColorMode)
                    ? "CMYK"
                    : getData.ColorMode.Trim();

                string printContent = string.IsNullOrWhiteSpace(getData.Content)
                    ? ""
                    : getData.Content.Trim();

                string apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey") ?? "";
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI API key is not configured."
                    });
                }

                string prompt = BuildPrintMaterialPrompt(
                    getData,
                    companyName,
                    industry,
                    companyDescription,
                    mainPrompt,
                    printType,
                    paperSize,
                    orientation,
                    colorMode,
                    printContent);

                // Convert the selected source image to a data URL before sending it
                // to OpenAI. This also works when the image is hosted somewhere that
                // OpenAI cannot fetch directly (for example, a development URL).
                string openAiImageInput = await PrepareImageInputForOpenAiAsync(getData.ImageUrl);

                using var openAiClient = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(5)
                };

                openAiClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);

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
                                new
                                {
                                    type = "input_text",
                                    text = prompt
                                },
                                new
                                {
                                    type = "input_image",
                                    image_url = openAiImageInput,
                                    detail = "high"
                                }
                            }
                        }
                    },
                    tools = new object[]
                    {
                        new
                        {
                            type = "image_generation",
                            action = "generate",
                            quality = getData.HighResolutionOutput ? "high" : "medium"
                        }
                    },
                    tool_choice = new
                    {
                        type = "image_generation"
                    }
                };

                string requestJson = JsonConvert.SerializeObject(requestPayload);
                var operationId = string.IsNullOrWhiteSpace(getData.GenerationJobId) ? Guid.NewGuid().ToString() : getData.GenerationJobId;
                var usageId = await _usage.StartAsync(new AIGenerationUsage
                {
                    UserId = userId, ProjectId = getData.ProjectId, FunnelId = getData.FunnelId,
                    GenerationJobId = operationId, ProviderTaskId = operationId,
                    GenerationCategory = "PaidWorkspace", DeliverableType = "PrintMaterial",
                    Feature = "print_material", UsageScope = "Paid", Operation = "PrintMaterialGeneration",
                    GenerationUnits = _summary!.UnitsFor("print_material"),
                    Provider = "OpenAI", Model = "gpt-5.6", ImageCount = 1,
                    PricingVersion = AIProviderPricingService.CurrentVersion
                });
                using var jsonContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage openAiResponse = await openAiClient.PostAsync(
                    "https://api.openai.com/v1/responses",
                    jsonContent);

                string jsonResponse = await openAiResponse.Content.ReadAsStringAsync();

                if (!openAiResponse.IsSuccessStatusCode)
                {
                    await _usage.CompleteFailureAsync(usageId, "ProviderError");
                    _logger.LogError(
                        "OpenAI print-material generation failed. Status={Status}; Response={Response}",
                        openAiResponse.StatusCode,
                        jsonResponse);

                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI print-material generation failed.",
                        Error = jsonResponse
                    });
                }

                var providerRequestId = openAiResponse.Headers.TryGetValues("x-request-id", out var requestIds) ? requestIds.FirstOrDefault() : null;
                await _usage.CompleteOpenAiAsync(usageId, jsonResponse, "gpt-5.6", "PrintMaterialGeneration", providerRequestId);

                string? generatedImageBase64 = ExtractGeneratedImageBase64(jsonResponse);

                if (string.IsNullOrWhiteSpace(generatedImageBase64))
                {
                    _logger.LogError(
                        "OpenAI completed without an image_generation_call result. Response={Response}",
                        jsonResponse);

                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI completed the request but did not return a generated print-material image."
                    });
                }

                byte[] generatedImageBytes;
                try
                {
                    generatedImageBytes = Convert.FromBase64String(generatedImageBase64);
                }
                catch (FormatException ex)
                {
                    return new BadRequestObjectResult(new
                    {
                        Success = false,
                        Message = "OpenAI returned invalid print-material image data.",
                        Error = ex.Message
                    });
                }

                byte[] pdfBytes = CreatePrintMaterialPdf(
                    generatedImageBytes,
                    paperSize,
                    orientation,
                    getData.IncludeBleedArea,
                    getData.BleedAreaSize,
                    getData.IncludeCropMarks);

                string imageDataUrl = $"data:image/png;base64,{generatedImageBase64}";
                string pdfBase64 = Convert.ToBase64String(pdfBytes);
                string pdfDataUrl = $"data:application/pdf;base64,{pdfBase64}";

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = "Print material generated successfully.",
                    PreviewUrl = imageDataUrl,
                    ImageDataUrl = imageDataUrl,
                    DownloadUrl = pdfDataUrl,
                    PrintReadyPdfUrl = pdfDataUrl,
                    PdfDataUrl = pdfDataUrl,
                    ImageBase64 = generatedImageBase64,
                    PdfBase64 = pdfBase64,
                    ImageMimeType = "image/png",
                    PdfMimeType = "application/pdf",
                    FileName = BuildPrintMaterialFileName(companyName, printType),
                    SourceImageUrl = getData.ImageUrl,
                    CompanyName = companyName,
                    Industry = industry,
                    PrintType = printType,
                    PaperSize = paperSize,
                    Orientation = orientation,
                    ColorMode = colorMode,
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
                _logger.LogError(ex, "GeneratePrintMaterialPdf timed out.");
                return new BadRequestObjectResult(new
                {
                    Success = false,
                    Message = "Print-material generation timed out. Please try again.",
                    Error = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GeneratePrintMaterialPdf failed.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error generating print material.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
        {
            var username = _jwt.ResolveUsername(request);
            return string.IsNullOrWhiteSpace(username)
                ? null
                : await _attribution.ResolveUserIdAsync(username);
        }

        // ================================================================
        // 2) SAVE GENERATED PNG + PDF
        // LOCAL:
        // wwwroot/SavedImages/{Company}-print-material-vN/
        //     print-material.png
        //     print-material.pdf
        // ================================================================
        [Function("SavePrintMaterialFiles")]
        public async Task<IActionResult> SavePrintMaterialFiles(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            try
            {
                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<SavePrintMaterialFilesRequest>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.ImageDataUrl) ||
                    string.IsNullOrWhiteSpace(data.PdfDataUrl) ||
                    string.IsNullOrWhiteSpace(data.CompanyName) ||
                    string.IsNullOrWhiteSpace(data.Version))
                {
                    return new BadRequestObjectResult(
                        "ImageDataUrl, PdfDataUrl, CompanyName, and Version are required.");
                }

                byte[] imageBytes = DecodeDataUrl(data.ImageDataUrl, "image/");
                byte[] pdfBytes = DecodePdfDataUrl(data.PdfDataUrl);

                string safeCompany = SanitizeCompanyName(data.CompanyName);
                string safeVersion = NormalizeVersion(data.Version);
                string folderName = $"{safeCompany}-print-material-v{safeVersion}";
                const string imageFileName = "print-material.png";
                const string pdfFileName = "print-material.pdf";

                bool useLocal = Environment.GetEnvironmentVariable("USE_LOCAL_DYNAMODB") == "true";

                string imagePath;
                string pdfPath;
                string imageUrl;
                string pdfUrl;

                if (useLocal)
                {
                    string wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    string savedImagesPath = Path.Combine(wwwrootPath, "SavedImages");
                    string folderPath = Path.Combine(savedImagesPath, folderName);
                    Directory.CreateDirectory(folderPath);

                    imagePath = Path.Combine(folderPath, imageFileName);
                    pdfPath = Path.Combine(folderPath, pdfFileName);

                    await System.IO.File.WriteAllBytesAsync(imagePath, imageBytes);
                    await System.IO.File.WriteAllBytesAsync(pdfPath, pdfBytes);

                    string rootUrl = $"{req.Scheme}://{req.Host.Host}";
                    imageUrl = $"{rootUrl}/{folderName}/{imageFileName}";
                    pdfUrl = $"{rootUrl}/{folderName}/{pdfFileName}";
                }
                else
                {
                    string bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? "";
                    string regionName = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";

                    if (string.IsNullOrWhiteSpace(bucketName))
                        return new BadRequestObjectResult("S3_BUCKET_NAME is not configured.");

                    var region = RegionEndpoint.GetBySystemName(regionName);
                    using var s3Client = new AmazonS3Client(
                        Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
                        Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
                        region);

                    string imageKey = $"{folderName}/{imageFileName}";
                    string pdfKey = $"{folderName}/{pdfFileName}";

                    using (var imageStream = new MemoryStream(imageBytes))
                    {
                        await s3Client.PutObjectAsync(new PutObjectRequest
                        {
                            BucketName = bucketName,
                            Key = imageKey,
                            InputStream = imageStream,
                            ContentType = "image/png"
                        });
                    }

                    using (var pdfStream = new MemoryStream(pdfBytes))
                    {
                        await s3Client.PutObjectAsync(new PutObjectRequest
                        {
                            BucketName = bucketName,
                            Key = pdfKey,
                            InputStream = pdfStream,
                            ContentType = "application/pdf"
                        });
                    }

                    imagePath = imageKey;
                    pdfPath = pdfKey;
                    imageUrl = $"https://{bucketName}.s3.{regionName}.amazonaws.com/{imageKey}";
                    pdfUrl = $"https://{bucketName}.s3.{regionName}.amazonaws.com/{pdfKey}";
                }

                return new OkObjectResult(new
                {
                    Success = true,
                    Message = useLocal
                        ? "Print material image and PDF saved locally."
                        : "Print material image and PDF saved to S3.",
                    FolderName = folderName,
                    ImageFileName = imageFileName,
                    PdfFileName = pdfFileName,
                    ImagePath = imagePath,
                    PdfPath = pdfPath,
                    ImageUrl = imageUrl,
                    PreviewUrl = imageUrl,
                    PdfUrl = pdfUrl,
                    DownloadUrl = pdfUrl,
                    Mode = useLocal ? "LOCAL" : "S3"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving print-material files.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error saving print-material files.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // Backwards-compatible PDF-only endpoint if anything still calls it.
        [Function("SavePrintMaterialPdf")]
        public async Task<IActionResult> SavePrintMaterialPdf(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            try
            {
                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<SavePrintMaterialPdfRequest>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.PdfDataUrl) ||
                    string.IsNullOrWhiteSpace(data.CompanyName) ||
                    string.IsNullOrWhiteSpace(data.Version))
                {
                    return new BadRequestObjectResult("PdfDataUrl, CompanyName, and Version are required.");
                }

                byte[] pdfBytes = DecodePdfDataUrl(data.PdfDataUrl);
                string safeCompany = SanitizeCompanyName(data.CompanyName);
                string safeVersion = NormalizeVersion(data.Version);
                string folderName = $"{safeCompany}-print-material-v{safeVersion}";
                const string fileName = "print-material.pdf";
                bool useLocal = Environment.GetEnvironmentVariable("USE_LOCAL_DYNAMODB") == "true";

                string filePath;
                string webUrl;

                if (useLocal)
                {
                    string wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    string savedImagesPath = Path.Combine(wwwrootPath, "SavedImages");
                    string folderPath = Path.Combine(savedImagesPath, folderName);
                    Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, fileName);
                    await System.IO.File.WriteAllBytesAsync(filePath, pdfBytes);

                    string rootUrl = $"{req.Scheme}://{req.Host.Host}";
                    webUrl = $"{rootUrl}/{folderName}/{fileName}";
                }
                else
                {
                    string bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? "";
                    string regionName = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";

                    if (string.IsNullOrWhiteSpace(bucketName))
                        return new BadRequestObjectResult("S3_BUCKET_NAME is not configured.");

                    var region = RegionEndpoint.GetBySystemName(regionName);
                    using var s3Client = new AmazonS3Client(
                        Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
                        Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
                        region);

                    string keyName = $"{folderName}/{fileName}";
                    using var stream = new MemoryStream(pdfBytes);
                    await s3Client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = "application/pdf"
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{regionName}.amazonaws.com/{keyName}";
                }

                return new OkObjectResult(new
                {
                    Success = true,
                    PdfUrl = webUrl,
                    SavedUrl = webUrl,
                    FilePath = filePath,
                    FolderName = folderName,
                    FileName = fileName
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving print-material PDF.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error saving print-material PDF.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // ================================================================
        // 3) SAVE VERSION ROW TO PrintMaterials
        // Id, PrintMaterialsId, CompanyName, Version, TimeStamp
        // ================================================================
        [Function("SavePrintMaterial")]
        public async Task<IActionResult> SavePrintMaterial(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            try
            {
                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<PrintMaterialRequest>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.PrintMaterialsId) ||
                    string.IsNullOrWhiteSpace(data.CompanyName) ||
                    string.IsNullOrWhiteSpace(data.Version) ||
                    string.IsNullOrWhiteSpace(data.TimeStamp))
                {
                    return new BadRequestObjectResult(
                        "PrintMaterialsId, CompanyName, Version, and TimeStamp are required.");
                }

                using var client = CreateDynamoClient();
                string recordId = await CreateUniquePrintMaterialRowId(client);

                var request = new ExecuteStatementRequest
                {
                    Statement = "INSERT INTO PrintMaterials VALUE {'Id': ?, 'PrintMaterialsId': ?, 'CompanyName': ?, 'Version': ?, 'TimeStamp': ?}",
                    Parameters = new List<AttributeValue>
                    {
                        new() { S = recordId },
                        new() { S = data.PrintMaterialsId },
                        new() { S = data.CompanyName },
                        new() { S = data.Version },
                        new() { S = data.TimeStamp }
                    }
                };

                await client.ExecuteStatementAsync(request);

                return new OkObjectResult(new
                {
                    Success = true,
                    Id = recordId,
                    data.PrintMaterialsId,
                    data.CompanyName,
                    data.Version,
                    data.TimeStamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving PrintMaterials row.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error saving PrintMaterials row.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // ================================================================
        // 4) LOAD ALL VERSIONS FOR COMPANY
        // ================================================================
        [Function("GetPrintMaterialsByCompany")]
        public async Task<IActionResult> GetPrintMaterialsByCompany(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            if (!IsAllowedOrigin(req))
                return new UnauthorizedResult();

            try
            {
                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<GetPrintMaterialsByCompanyRequest>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null || string.IsNullOrWhiteSpace(data.CompanyName))
                    return new BadRequestObjectResult("CompanyName is required.");

                using var client = CreateDynamoClient();

                var response = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM PrintMaterials WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new() { S = data.CompanyName }
                    }
                });

                var rows = response.Items
                    .Select(item => new PrintMaterialRequest
                    {
                        Id = GetS(item, "Id"),
                        PrintMaterialsId = GetS(item, "PrintMaterialsId"),
                        CompanyName = GetS(item, "CompanyName"),
                        Version = GetS(item, "Version"),
                        TimeStamp = GetS(item, "TimeStamp")
                    })
                    .OrderByDescending(x => DateTime.TryParse(x.TimeStamp, out var dt)
                        ? dt
                        : DateTime.MinValue)
                    .ToList();

                return new OkObjectResult(new
                {
                    Success = true,
                    Count = rows.Count,
                    Data = rows
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed loading PrintMaterials rows.");
                return new ObjectResult(new
                {
                    Success = false,
                    Message = "Error loading PrintMaterials rows.",
                    Error = ex.Message
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        // ================================================================
        // PROMPT
        // ================================================================
        private static string BuildPrintMaterialPrompt(
            GeneratePrintMaterialRequest getData,
            string companyName,
            string industry,
            string companyDescription,
            string mainPrompt,
            string printType,
            string paperSize,
            string orientation,
            string colorMode,
            string printContent)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine($@"
Analyze the supplied reference image and create a completely new, professional print-material design inspired by the visual design, branding, colors, typography, imagery, and overall aesthetic of that reference image.

IMPORTANT:
The supplied image is a VISUAL REFERENCE for the new print material.

Study the supplied reference image carefully and infer the brand system from what is visibly present.

Extract and understand:
- Brand colors
- Secondary and accent colors
- Color combinations
- Typography personality
- Logo / wordmark treatment
- Graphic style
- Illustration style
- Photography style
- Shapes
- Patterns
- Borders
- Decorative elements
- Visual hierarchy
- Brand personality
- Composition
- Spacing
- Overall art direction

Use those visual characteristics to design a COMPLETELY NEW print material.

Do not simply return the original image.
Do not place the original image inside a frame.
Do not stretch the original image to fill the page.
Do not create a screenshot.
Do not create a website.
Do not create a computer screen, laptop, phone, browser, application window, or UI mockup.
Do not create a photograph of someone holding the print material.
Do not create a print-shop scene.

Create the ACTUAL FLAT PRINT DESIGN itself.

COMPANY INFORMATION

Company name:
{companyName}

Industry:
{industry}

Company description:
{companyDescription}

Brand / creative direction:
{mainPrompt}

PRINT MATERIAL SPECIFICATIONS

Print material type:
{GetReadablePrintType(printType)}

Paper size:
{paperSize}

Orientation:
{orientation}

Color mode:
{colorMode}

Include bleed area:
{BoolText(getData.IncludeBleedArea)}

Bleed size:
{getData.BleedAreaSize} mm

Include crop marks:
{BoolText(getData.IncludeCropMarks)}

Add page numbers:
{BoolText(getData.AddPageNumbers)}

Include company logo:
{BoolText(getData.IncludeCompanyLogo)}

Include contact information:
{BoolText(getData.IncludeContactInfo)}

High-resolution output:
{BoolText(getData.HighResolutionOutput)}

PRINT CONTENT

{printContent}

DESIGN OBJECTIVE

Create a polished commercial-quality {GetReadablePrintType(printType)} for {companyName}.

The finished artwork must immediately feel like it belongs to the same brand represented by the supplied reference image, while using a new composition appropriate for the requested print-material type.

REFERENCE IMAGE ANALYSIS

Before designing, determine:
1. Dominant brand colors.
2. Supporting and accent colors.
3. Typography characteristics.
4. Brand personality and tone.
5. Photography or illustration style.
6. Geometric or organic shape language.
7. Spacing and visual density.
8. Logo / wordmark treatment.
9. Contrast level.
10. Overall visual hierarchy.

Carry these characteristics into the NEW print design.

BRAND CONSISTENCY

Preserve the visual CHARACTER of:
- Colors
- Typography
- Graphic language
- Logo styling
- Photography or illustration
- Shapes
- Patterns
- Decorative elements
- Layout rhythm
- Brand personality

Do not merely copy the exact original composition.
");

            switch (printType.Trim())
            {
                case "Brochure":
                    prompt.AppendLine(@"
BROCHURE REQUIREMENTS
Create professional flat brochure artwork with strong cover/header treatment, clear company branding, organized information sections, professional visual hierarchy, marketing typography, appropriate imagery, clear section separation, a call-to-action area where appropriate, and balanced use of text and graphics.
Do not create a photograph or mockup of a folded brochure. Generate the FLAT PRINT ARTWORK itself.");
                    break;

                case "Flyer":
                    prompt.AppendLine(@"
FLYER REQUIREMENTS
Create a professional promotional flyer with a strong headline, clear company branding, primary marketing message, supporting visual imagery, clear supporting information, strong hierarchy, appropriate call to action, excellent readability, and balanced whitespace.
Do not create a flyer mockup. Generate the FLAT FLYER ARTWORK itself.");
                    break;

                case "Poster":
                    prompt.AppendLine(@"
POSTER REQUIREMENTS
Create a professional poster with a strong focal point, large readable headline, clear company branding, high-impact imagery, excellent distance readability, limited but effective text, and commercial advertising quality.
Do not show the poster hanging on a wall or inside a frame. Generate the FLAT POSTER ARTWORK itself.");
                    break;

                case "BusinessCard":
                    prompt.AppendLine(@"
BUSINESS CARD REQUIREMENTS
Create professional flat business-card artwork with company identity, appropriate logo/brand mark, clean typography, premium spacing, contact-information area if requested, and strong brand consistency.
Do not show hands, stacks of cards, or a business-card mockup. Generate the FLAT BUSINESS-CARD ARTWORK itself.");
                    break;

                case "Letterhead":
                    prompt.AppendLine(@"
LETTERHEAD REQUIREMENTS
Create professional company letterhead with clear brand identity, appropriate logo/brand mark, professional header/footer treatment, optional contact area, a large clean writing area, and restrained brand graphics.
Generate the FLAT LETTERHEAD ARTWORK itself.");
                    break;

                case "Catalog":
                    prompt.AppendLine(@"
CATALOG REQUIREMENTS
Create one professional catalog-style page with strong branding, organized product/content areas, clear hierarchy, professional image placement, balanced grids, and commercial catalog design quality.
Generate ONE FLAT CATALOG PAGE representing the visual system.");
                    break;

                case "Presentation":
                    prompt.AppendLine(@"
PRESENTATION REQUIREMENTS
Create one professionally designed presentation-style print page with strong title treatment, company branding, clear information hierarchy, professional graphics, and balanced text/image composition.
Generate ONE COMPLETED FLAT PRESENTATION PAGE.");
                    break;
            }

            prompt.AppendLine($@"
PAPER SIZE
The intended paper size is {paperSize}. Design proportions, typography scale, spacing, and content density appropriately.

ORIENTATION
The required orientation is {orientation}. The artwork MUST respect that orientation.

COLOR MODE
Requested color mode: {colorMode}.
For CMYK, favor colors that reproduce well in professional print. For RGB, use clean digital-preview-friendly colors. For Grayscale, create an effective monochrome hierarchy.

BLEED AREA
Include bleed area: {BoolText(getData.IncludeBleedArea)}.
Requested bleed: {getData.BleedAreaSize} mm.
If bleed is enabled, extend background artwork/colors/patterns/photography to the edge. Do not draw or label a fake bleed border. The PDF stage will handle the physical bleed area.

CROP MARKS
Include crop marks: {BoolText(getData.IncludeCropMarks)}.
Do not draw crop marks into the generated artwork itself. The PDF generator adds them separately.

SAFE AREA
Keep important text, logos, headlines, contact information, and calls to action safely away from trim edges.

COMPANY LOGO
Include company logo: {BoolText(getData.IncludeCompanyLogo)}.
If YES, study the reference image for an obvious logo/wordmark/brand-mark treatment and create an appropriate treatment consistent with that visible brand character for {companyName}. Do not invent a different company name.
If NO, do not create a dedicated logo treatment.

CONTACT INFORMATION
Include contact information: {BoolText(getData.IncludeContactInfo)}.
If contact details were not supplied, do NOT invent phone numbers, email addresses, street addresses, websites, or social handles.

TEXT CONTENT
Supplied content:
{printContent}

If content was supplied, use it where appropriate and preserve important wording. If no content was supplied, keep invented copy minimal and generic. Avoid fake factual claims.

IMPORTANT TEXT ACCURACY
Prioritize correctly rendering:
Company: {companyName}
Print material type: {GetReadablePrintType(printType)}
Provided content: {printContent}

Avoid random placeholder words, gibberish, fake lorem ipsum, invented addresses, phone numbers, websites, certifications, legal claims, prices, statistics, or awards.

TYPOGRAPHY
Typography should be professional, readable, brand appropriate, aligned, spaced consistently, and suitable for print. Use a clear hierarchy between headlines, subheads, body content, supporting information, and calls to action.

LAYOUT REQUIREMENTS
Create a complete professional print composition with:
1. Strong visual hierarchy
2. Clear company branding
3. Professional typography
4. Excellent spacing
5. Excellent alignment
6. Appropriate margins
7. Balanced imagery and graphics
8. Appropriate whitespace
9. Clear information grouping
10. Print-ready appearance
11. Premium commercial quality
12. Strong brand consistency

HIGH RESOLUTION
High-resolution output requested: {BoolText(getData.HighResolutionOutput)}.
Create sharp graphics, clean typography, clear edges, detailed visual elements, and professional print-design quality.

VISUAL QUALITY
Create a premium, polished, professionally designed {GetReadablePrintType(printType)} that looks like work from an experienced graphic designer or advertising agency.
The result should be clean, modern, brand appropriate, commercial quality, well balanced, visually striking, easy to read, professionally composed, and ready to be placed into a print PDF.

OUTPUT REQUIREMENTS
Generate ONE completed print-material artwork image.
Generate ONLY the flat design artwork.
The image itself should primarily contain the finished print design.

Do not show:
- Photoshop
- Illustrator
- Canva
- A design editor
- Browser chrome
- Laptop
- Desktop computer
- Phone
- Tablet
- Hands
- People holding the design
- Printing equipment
- Printer
- Desk scene
- Design presentation board
- Picture frame
- Wall mockup
- Physical mockup
- Folded brochure mockup
- Business-card stack
- Poster hanging on a wall

Do not surround the artwork with unnecessary environmental elements.
Do not create a screenshot of the artwork.
Do not place the artwork inside another scene.
Generate the FINAL FLAT PRINT DESIGN itself.

This generated image will be placed directly into the final print PDF, so it must fill the composition appropriately.
");

            return prompt.ToString();
        }

        // ================================================================
        // PDF CREATION
        // ================================================================
        private static byte[] CreatePrintMaterialPdf(
            byte[] generatedImageBytes,
            string paperSize,
            string orientation,
            bool includeBleedArea,
            int bleedAreaSize,
            bool includeCropMarks)
        {
            var (trimWidthPoints, trimHeightPoints) = GetPageSizeInPoints(paperSize);

            if (string.Equals(orientation, "Landscape", StringComparison.OrdinalIgnoreCase))
                (trimWidthPoints, trimHeightPoints) = (trimHeightPoints, trimWidthPoints);

            double bleedPoints = includeBleedArea
                ? MillimetersToPoints(Math.Clamp(bleedAreaSize, 0, 25))
                : 0;

            double pageWidth = trimWidthPoints + bleedPoints * 2;
            double pageHeight = trimHeightPoints + bleedPoints * 2;

            using var output = new MemoryStream();
            using var document = new PdfDocument();

            document.Info.Title = "Generated Print Material";
            document.Info.Subject = "OpenAI-generated print material";
            document.Info.Creator = "DesignMode.AI";

            var page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);

            using var graphics = XGraphics.FromPdfPage(page);
            using var image = XImage.FromStream(() => new MemoryStream(generatedImageBytes));

            DrawImageCover(graphics, image, 0, 0, pageWidth, pageHeight);

            if (includeCropMarks)
                DrawCropMarks(graphics, bleedPoints, trimWidthPoints, trimHeightPoints);

            document.Save(output, false);
            return output.ToArray();
        }

        private static void DrawImageCover(
            XGraphics graphics,
            XImage image,
            double x,
            double y,
            double width,
            double height)
        {
            double imageRatio = image.PixelWidth / (double)image.PixelHeight;
            double targetRatio = width / height;

            double drawWidth;
            double drawHeight;
            double drawX;
            double drawY;

            if (imageRatio > targetRatio)
            {
                drawHeight = height;
                drawWidth = height * imageRatio;
                drawX = x - (drawWidth - width) / 2.0;
                drawY = y;
            }
            else
            {
                drawWidth = width;
                drawHeight = width / imageRatio;
                drawX = x;
                drawY = y - (drawHeight - height) / 2.0;
            }

            graphics.DrawImage(image, drawX, drawY, drawWidth, drawHeight);
        }

        private static void DrawCropMarks(
            XGraphics graphics,
            double bleedPoints,
            double trimWidthPoints,
            double trimHeightPoints)
        {
            if (bleedPoints <= 0)
                return;

            double left = bleedPoints;
            double top = bleedPoints;
            double right = left + trimWidthPoints;
            double bottom = top + trimHeightPoints;
            double mark = Math.Min(MillimetersToPoints(4), bleedPoints * 0.85);
            double gap = Math.Min(MillimetersToPoints(1), bleedPoints * 0.25);

            var pen = new XPen(XColors.Black, 0.5);

            graphics.DrawLine(pen, left - gap - mark, top, left - gap, top);
            graphics.DrawLine(pen, left, top - gap - mark, left, top - gap);

            graphics.DrawLine(pen, right + gap, top, right + gap + mark, top);
            graphics.DrawLine(pen, right, top - gap - mark, right, top - gap);

            graphics.DrawLine(pen, left - gap - mark, bottom, left - gap, bottom);
            graphics.DrawLine(pen, left, bottom + gap, left, bottom + gap + mark);

            graphics.DrawLine(pen, right + gap, bottom, right + gap + mark, bottom);
            graphics.DrawLine(pen, right, bottom + gap, right, bottom + gap + mark);
        }

        // ================================================================
        // GENERAL HELPERS
        // ================================================================
        private bool IsAllowedOrigin(HttpRequest req)
        {
            string? origin = req.Headers["Origin"].FirstOrDefault();
            string? allowedHost = Environment.GetEnvironmentVariable("HostUrl");

            return !string.IsNullOrWhiteSpace(origin) &&
                   !string.IsNullOrWhiteSpace(allowedHost) &&
                   string.Equals(origin, allowedHost, StringComparison.OrdinalIgnoreCase);
        }

        private static AmazonDynamoDBClient CreateDynamoClient()
        {
            return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
        }

        private static async Task<string> CreateUniquePrintMaterialRowId(
            AmazonDynamoDBClient client)
        {
            while (true)
            {
                string id = Guid.NewGuid().ToString();

                var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM PrintMaterials WHERE Id = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new() { S = id }
                    }
                });

                if (result.Items == null || result.Items.Count == 0)
                    return id;
            }
        }

        private static string? ExtractGeneratedImageBase64(string jsonResponse)
        {
            using JsonDocument document = JsonDocument.Parse(jsonResponse);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("output", out JsonElement outputElement) ||
                outputElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("type", out JsonElement typeElement))
                    continue;

                if (!string.Equals(
                        typeElement.GetString(),
                        "image_generation_call",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!outputItem.TryGetProperty("result", out JsonElement resultElement))
                    continue;

                string? result = resultElement.GetString();
                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }

            return null;
        }

        private static async Task<string> PrepareImageInputForOpenAiAsync(string imageUrl)
        {
            if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return imageUrl;

            if (!imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ImageUrl must be an HTTP/HTTPS URL or a data:image URI.");
            }

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            using var response = await http.GetAsync(imageUrl);
            response.EnsureSuccessStatusCode();

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
                throw new InvalidOperationException("Selected reference image was empty.");

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = "image/png";

            return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }

        private static byte[] DecodeDataUrl(string dataUrl, string requiredMimePrefix)
        {
            if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected a base64 data URI.");

            int comma = dataUrl.IndexOf(',');
            if (comma < 0)
                throw new InvalidOperationException("Invalid base64 data URI.");

            string metadata = dataUrl.Substring(5, comma - 5);
            if (!metadata.StartsWith(requiredMimePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Expected content type beginning with {requiredMimePrefix}.");

            string base64 = dataUrl[(comma + 1)..]
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();

            return Convert.FromBase64String(base64);
        }

        private static byte[] DecodePdfDataUrl(string pdfDataUrl)
        {
            if (!pdfDataUrl.StartsWith(
                    "data:application/pdf;base64,",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PdfDataUrl must be a base64 PDF data URI.");
            }

            int comma = pdfDataUrl.IndexOf(',');
            return Convert.FromBase64String(
                pdfDataUrl[(comma + 1)..]
                    .Replace("\r", "")
                    .Replace("\n", "")
                    .Trim());
        }

        private static (double widthPoints, double heightPoints) GetPageSizeInPoints(
            string? paperSize)
        {
            return (paperSize ?? "A4").ToUpperInvariant() switch
            {
                "LETTER" => (InchesToPoints(8.5), InchesToPoints(11)),
                "A3" => (MillimetersToPoints(297), MillimetersToPoints(420)),
                "LEGAL" => (InchesToPoints(8.5), InchesToPoints(14)),
                "SQUARE" => (InchesToPoints(8), InchesToPoints(8)),
                "TABLOID" => (InchesToPoints(11), InchesToPoints(17)),
                _ => (MillimetersToPoints(210), MillimetersToPoints(297))
            };
        }

        private static string GetReadablePrintType(string printType)
        {
            if (string.IsNullOrWhiteSpace(printType))
                return "Professional Brochure";

            return printType switch
            {
                "Brochure" => "Professional Brochure",
                "Flyer" => "Promotional Flyer",
                "Poster" => "Promotional Poster",
                "BusinessCard" => "Business Card",
                "Letterhead" => "Company Letterhead",
                "Presentation" => "Presentation Page",
                "Catalog" => "Product Catalog",
                _ => printType
            };
        }

        private static string BuildPrintMaterialFileName(
            string companyName,
            string printType)
        {
            string rawName = $"{companyName}-{printType}-print-material";

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                rawName = rawName.Replace(invalidChar.ToString(), "");

            rawName = rawName
                .Replace(" ", "-")
                .Replace("&", "and")
                .ToLowerInvariant();

            rawName = Regex.Replace(rawName, @"[^a-z0-9._-]", "-");
            rawName = Regex.Replace(rawName, @"-+", "-").Trim('-');

            return $"{rawName}.pdf";
        }

        private static string SanitizeCompanyName(string companyName)
        {
            if (string.IsNullOrWhiteSpace(companyName))
                return "Company";

            string safe = string.Concat(
                    companyName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                .Replace(" ", "-")
                .Replace("&", "and");

            safe = Regex.Replace(safe, @"[^A-Za-z0-9._-]", "-");
            safe = Regex.Replace(safe, @"-+", "-").Trim('-');

            return string.IsNullOrWhiteSpace(safe) ? "Company" : safe;
        }

        private static string NormalizeVersion(string version)
        {
            string value = (version ?? "1").Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value[1..];

            return string.IsNullOrWhiteSpace(value) ? "1" : value;
        }

        private static string GetS(
            Dictionary<string, AttributeValue> item,
            string key)
        {
            return item.TryGetValue(key, out var value)
                ? value.S ?? ""
                : "";
        }

        private static string BoolText(bool value) => value ? "YES" : "NO";
        private static double InchesToPoints(double inches) => inches * 72.0;
        private static double MillimetersToPoints(double mm) => mm / 25.4 * 72.0;
    }

    // ====================================================================
    // REQUEST / RESPONSE MODELS
    // ====================================================================
    public class GeneratePrintMaterialRequest
    {
        public string? UserId { get; set; }
        public string? GenerationJobId { get; set; }
        public string? ProjectId { get; set; }
        public string? FunnelId { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Industry { get; set; } = string.Empty;
        public string CompanyDescription { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string PrintType { get; set; } = "Brochure";
        public string PaperSize { get; set; } = "A4";
        public string Orientation { get; set; } = "Portrait";
        public string ColorMode { get; set; } = "CMYK";
        public bool IncludeBleedArea { get; set; } = true;
        public int BleedAreaSize { get; set; } = 3;
        public bool IncludeCropMarks { get; set; } = true;
        public bool AddPageNumbers { get; set; }
        public bool IncludeCompanyLogo { get; set; } = true;
        public bool IncludeContactInfo { get; set; } = true;
        public bool HighResolutionOutput { get; set; } = true;
        public string Content { get; set; } = string.Empty;
    }

    public class SavePrintMaterialFilesRequest
    {
        public string ImageDataUrl { get; set; } = string.Empty;
        public string PdfDataUrl { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    public class SavePrintMaterialPdfRequest
    {
        public string PdfDataUrl { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string FileName { get; set; } = "print-material.pdf";
    }

    public class PrintMaterialRequest
    {
        public string Id { get; set; } = string.Empty;
        public string PrintMaterialsId { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string TimeStamp { get; set; } = string.Empty;
    }

    public class GetPrintMaterialsByCompanyRequest
    {
        public string CompanyName { get; set; } = string.Empty;
    }
}

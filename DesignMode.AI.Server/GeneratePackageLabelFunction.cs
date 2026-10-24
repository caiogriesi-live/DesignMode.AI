using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Amazon.S3.Model;
using Amazon.S3;
using System.Net;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;

namespace DesignMode.AI.Server
{
    public class GeneratePackageLabelFunction
    {
        private readonly ILogger<GeneratePackageLabelFunction> _logger;
        private readonly AttributionStore _attribution;
        private readonly JwtUserResolver _jwt;
        private readonly AIUsageSummaryService _summary;
        private readonly AIUsageRecorder _usage;

        public GeneratePackageLabelFunction(
            ILogger<GeneratePackageLabelFunction> logger,
            AttributionStore attribution,
            JwtUserResolver jwt,
            AIUsageSummaryService summary,
            AIUsageRecorder usage)
        {
            _logger = logger;
            _attribution = attribution;
            _jwt = jwt;
            _summary = summary;
            _usage = usage;
        }

        [Function("GeneratePackageLabel")]
        public async Task<IActionResult> GeneratePackageLabel(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string? usageId = null;
            try
            {
                // ------------------------------------------------------------
                // 1. SECURITY - VERIFY REQUEST ORIGIN
                // ------------------------------------------------------------

                string referer = req.Headers["Origin"].FirstOrDefault();

                string allowedHost =
                    Environment.GetEnvironmentVariable("HostUrl");

                if (referer == null || referer != allowedHost)
                {
                    return new UnauthorizedResult();
                }


                // ------------------------------------------------------------
                // 2. READ REQUEST BODY
                // ------------------------------------------------------------

                string requestBodyData =
                    await new StreamReader(req.Body).ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBodyData))
                {
                    return new BadRequestObjectResult(
                        "Request body is required.");
                }


                GeneratePackageLabelRequest getData;

                try
                {
                    getData =
                        JsonConvert.DeserializeObject<GeneratePackageLabelRequest>(
                            requestBodyData);
                }
                catch (Exception ex)
                {
                    return new BadRequestObjectResult(
                        $"Unable to parse request: {ex.Message}");
                }


                // ------------------------------------------------------------
                // 3. VALIDATE REQUIRED DATA
                // ------------------------------------------------------------

                if (getData == null)
                {
                    return new BadRequestObjectResult(
                        "Package label request is required.");
                }

                if (string.IsNullOrWhiteSpace(getData.ImageUrl))
                {
                    return new BadRequestObjectResult(
                        "ImageUrl is required.");
                }

                var claimedUserId = _jwt.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? null
                    : await _attribution.ResolveClaimedUserIdAsync(claimedUserId);
                if (string.IsNullOrWhiteSpace(userId))
                    userId = await ResolveLegacyJwtUserIdAsync(req);
                var generationProject = string.IsNullOrWhiteSpace(userId) ? null :
                    await _attribution.GetOwnedGenerationProjectAsync(userId, getData.ProjectId ?? string.Empty);
                if (generationProject is null) return new UnauthorizedResult();
                getData.ProjectId = generationProject.ProjectId;
                getData.FunnelId = generationProject.FunnelId;

                var quota = await _summary.EvaluateAsync(userId, "package_label");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" })
                    { StatusCode = StatusCodes.Status429TooManyRequests };

                const string packageLabelModel = "gpt-5.6";
                var generationJobId = string.IsNullOrWhiteSpace(getData.GenerationJobId)
                    ? Guid.NewGuid().ToString()
                    : getData.GenerationJobId;
                usageId = await _usage.StartAsync(new AIGenerationUsage
                {
                    UserId = userId, ProjectId = getData.ProjectId, FunnelId = getData.FunnelId,
                    GenerationJobId = generationJobId, ProviderTaskId = generationJobId,
                    GenerationCategory = "PaidWorkspace", DeliverableType = "PackageLabel",
                    Feature = "package_label", UsageScope = "Paid",
                    GenerationUnits = _summary.UnitsFor("package_label"), Operation = "PackageLabelGeneration",
                    Provider = "OpenAI", Model = packageLabelModel,
                    PricingVersion = AIProviderPricingService.CurrentVersion, ImageCount = 1
                });


                // ------------------------------------------------------------
                // 4. DEFAULT VALUES
                // ------------------------------------------------------------

                string companyName =
                    string.IsNullOrWhiteSpace(getData.Name)
                        ? "Brand"
                        : getData.Name.Trim();

                string industry =
                    string.IsNullOrWhiteSpace(getData.Industry)
                        ? "General"
                        : getData.Industry.Trim();

                string companyDescription =
                    string.IsNullOrWhiteSpace(getData.CompanyDescription)
                        ? ""
                        : getData.CompanyDescription.Trim();

                string mainPrompt =
                    string.IsNullOrWhiteSpace(getData.Prompt)
                        ? ""
                        : getData.Prompt.Trim();

                string labelType =
                    string.IsNullOrWhiteSpace(getData.LabelType)
                        ? "ProductLabel"
                        : getData.LabelType.Trim();

                string labelSize =
                    string.IsNullOrWhiteSpace(getData.LabelSize)
                        ? "4x6"
                        : getData.LabelSize.Trim();

                string material =
                    string.IsNullOrWhiteSpace(getData.Material)
                        ? "Paper"
                        : getData.Material.Trim();

                string orientation =
                    string.IsNullOrWhiteSpace(getData.Orientation)
                        ? "Portrait"
                        : getData.Orientation.Trim();

                string productName =
                    string.IsNullOrWhiteSpace(getData.ProductName)
                        ? companyName
                        : getData.ProductName.Trim();

                string skuBarcode =
                    string.IsNullOrWhiteSpace(getData.SkuBarcode)
                        ? ""
                        : getData.SkuBarcode.Trim();

                string additionalInfo =
                    string.IsNullOrWhiteSpace(getData.AdditionalInfo)
                        ? ""
                        : getData.AdditionalInfo.Trim();


                // ------------------------------------------------------------
                // 5. GET OPENAI API KEY
                // ------------------------------------------------------------

                string apiKey =
                    Environment.GetEnvironmentVariable(
                        "OpenAISettings:ApiKey");

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new BadRequestObjectResult(
                        "OpenAI API key is not configured.");
                }


                // ------------------------------------------------------------
                // 6. BUILD PACKAGE LABEL PROMPT
                // ------------------------------------------------------------

                string prompt = $@"
Analyze the supplied reference image and create a professional package label inspired by the visual design, branding, colors, typography, imagery, and overall aesthetic of that image.

IMPORTANT:
The supplied image is a VISUAL REFERENCE for the new package label.

Do not simply return the original image.
Do not place the original image inside a frame.
Do not create a screenshot of a package label.
Do not create a website.
Do not create a computer screen, laptop, phone, browser, or UI mockup.

Create an actual polished package label design.

COMPANY INFORMATION

Company name:
{companyName}

Industry:
{industry}

Company description:
{companyDescription}

Brand / creative direction:
{mainPrompt}


PACKAGE LABEL SPECIFICATIONS

Product name:
{productName}

Label type:
{GetReadableLabelType(labelType)}

Label size:
{labelSize}

Material:
{material}

Orientation:
{orientation}


LABEL CONTENT

SKU / Barcode value:
{skuBarcode}

Additional information:
{additionalInfo}

Include barcode:
{BoolText(getData.IncludeBarcode)}

Include QR code:
{BoolText(getData.IncludeQrCode)}

Include brand logo:
{BoolText(getData.IncludeBrandLogo)}


DESIGN INSTRUCTIONS

Study the supplied image carefully.

Use the image as inspiration for:

- Brand colors
- Color combinations
- Typography personality
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

Create a completely new package label that feels like it belongs to the same brand.

The final design should look intentionally designed for:
{productName}

The company is:
{companyName}

The industry is:
{industry}


LAYOUT REQUIREMENTS

Create a complete professional label composition.

The label should have:

1. Strong visual hierarchy
2. Clear product name
3. Clear company / brand identity
4. Professional typography
5. Excellent spacing and alignment
6. Legible text
7. Balanced imagery and graphics
8. Appropriate whitespace
9. Print-ready appearance
10. Premium commercial design quality


PRODUCT NAME

The product name must be clearly visible:

""{productName}""


BRAND NAME

The company / brand name should be visible:

""{companyName}""


LABEL TYPE

The requested label type is:

{GetReadableLabelType(labelType)}

Design the layout appropriately for this kind of label.


LABEL SIZE

The intended label size is:

{labelSize}

Design the proportions and content density appropriately.


ORIENTATION

The desired orientation is:

{orientation}

Respect this orientation when composing the image.


MATERIAL

The intended material is:

{material}

Visually account for the material where appropriate.

For example:

- Kraft should feel organic and natural.
- Clear material can use transparent-looking areas.
- Glossy material can have a premium polished appearance.
- Vinyl can feel durable and professionally printed.
- Paper should look like a high-quality printed label.


BARCODE

If Include Barcode is YES:

Create a visually appropriate barcode area.

If the following SKU/barcode value is provided, display the human-readable value underneath or near the barcode:

{skuBarcode}

The barcode does not need to be machine-scannable unless the exact barcode can be rendered accurately, but it must visually resemble a professional commercial barcode.

If Include Barcode is NO:

Do not include a barcode.


QR CODE

If Include QR Code is YES:

Include a clean QR-code-style graphic positioned appropriately in the label.

The QR code should not dominate the design.

If Include QR Code is NO:

Do not include a QR code.


BRAND LOGO

If Include Brand Logo is YES:

Use the visual identity of the supplied image to create an appropriate logo or brand-mark treatment for:

{companyName}

If there is an obvious logo visible in the source image, preserve its overall visual character without merely copying the entire source image.

If Include Brand Logo is NO:

Do not create a dedicated logo area.


ADDITIONAL INFORMATION

Use this information where appropriate:

{additionalInfo}

Organize long information into clean readable sections.

Do not overload the front of the label.

Use proper visual hierarchy.


IMPORTANT TEXT ACCURACY

Text is important in this design.

Prioritize correct, readable text for:

Company:
{companyName}

Product:
{productName}

SKU:
{skuBarcode}

Additional information:
{additionalInfo}

Avoid random placeholder words.

Avoid meaningless text.

Avoid fake lorem ipsum.

Do not invent unnecessary claims, certifications, warnings, ingredients, addresses, nutritional facts, legal claims, or regulatory information that was not supplied.


VISUAL QUALITY

Create a premium, polished, professional package-label design.

It should look like something produced by an experienced branding and packaging designer.

The result should be:

- Clean
- Modern
- Brand appropriate
- Commercial quality
- Print-design quality
- Well balanced
- Visually striking
- Easy to read
- Professionally composed


OUTPUT

Generate ONE completed package label image.

The image itself should primarily contain the package label artwork.

Do not show:

- A design editor
- Photoshop
- Illustrator
- Browser chrome
- Laptop
- Desktop computer
- Phone
- Hands holding the label
- Printing equipment
- A design presentation board

Do not surround the label with unnecessary environmental elements.

Generate the final package label design itself.
";


                // ------------------------------------------------------------
                // 7. CREATE OPENAI HTTP CLIENT
                // ------------------------------------------------------------

                using var client = new HttpClient();

                client.Timeout = TimeSpan.FromMinutes(5);

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        apiKey);


                // ------------------------------------------------------------
                // 8. BUILD RESPONSES API REQUEST
                // ------------------------------------------------------------
                //
                // The image_generation tool produces the final image.
                //
                // The selected ImageUrl is supplied as an input_image so the
                // model can use it as the design reference.
                // ------------------------------------------------------------

                var requestPayload = new
                {
                    model = packageLabelModel,

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
                                    image_url = getData.ImageUrl,
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
                            action = "generate"
                        }
                    }
                };


                // ------------------------------------------------------------
                // 9. SERIALIZE REQUEST
                // ------------------------------------------------------------

                string requestJson =
                    JsonConvert.SerializeObject(requestPayload);

                var jsonContent =
                    new StringContent(
                        requestJson,
                        Encoding.UTF8,
                        "application/json");


                // ------------------------------------------------------------
                // 10. CALL OPENAI RESPONSES API
                // ------------------------------------------------------------

                HttpResponseMessage response =
                    await client.PostAsync(
                        "https://api.openai.com/v1/responses",
                        jsonContent);


                string jsonResponse =
                    await response.Content.ReadAsStringAsync();


                if (!response.IsSuccessStatusCode)
                {
                    await _usage.CompleteFailureAsync(
                        usageId,
                        response.StatusCode == HttpStatusCode.BadRequest ? "InvalidRequest" : "ProviderError");
                    Console.WriteLine(
                        $"OpenAI error: {jsonResponse}");

                    return new BadRequestObjectResult(
                        new
                        {
                            Success = false,
                            Message = "OpenAI package label generation failed.",
                            Error = jsonResponse
                        });
                }

                var requestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
                    ? requestIds.FirstOrDefault()
                    : null;
                await _usage.CompleteOpenAiAsync(
                    usageId, jsonResponse, packageLabelModel, "PackageLabelGeneration", requestId);


                // ------------------------------------------------------------
                // 11. PARSE OPENAI RESPONSE
                // ------------------------------------------------------------

                JsonDocument responseDocument;

                try
                {
                    responseDocument =
                        JsonDocument.Parse(jsonResponse);
                }
                catch (Exception ex)
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            Success = false,
                            Message = "Unable to parse OpenAI response.",
                            Error = ex.Message
                        });
                }


                using (responseDocument)
                {
                    JsonElement root =
                        responseDocument.RootElement;

                    if (!root.TryGetProperty(
                            "output",
                            out JsonElement outputElement))
                    {
                        return new BadRequestObjectResult(
                            new
                            {
                                Success = false,
                                Message =
                                    "OpenAI response did not contain an output array."
                            });
                    }


                    // --------------------------------------------------------
                    // 12. FIND IMAGE GENERATION RESULT
                    // --------------------------------------------------------

                    string generatedImageBase64 = null;

                    foreach (JsonElement outputItem
                             in outputElement.EnumerateArray())
                    {
                        if (!outputItem.TryGetProperty(
                                "type",
                                out JsonElement typeElement))
                        {
                            continue;
                        }

                        string outputType =
                            typeElement.GetString();


                        if (!string.Equals(
                                outputType,
                                "image_generation_call",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }


                        if (!outputItem.TryGetProperty(
                                "result",
                                out JsonElement resultElement))
                        {
                            continue;
                        }


                        generatedImageBase64 =
                            resultElement.GetString();


                        if (!string.IsNullOrWhiteSpace(
                                generatedImageBase64))
                        {
                            break;
                        }
                    }


                    // --------------------------------------------------------
                    // 13. VERIFY IMAGE WAS GENERATED
                    // --------------------------------------------------------

                    if (string.IsNullOrWhiteSpace(
                            generatedImageBase64))
                    {
                        Console.WriteLine(
                            "No image_generation_call result found.");

                        Console.WriteLine(jsonResponse);

                        return new BadRequestObjectResult(
                            new
                            {
                                Success = false,
                                Message =
                                    "OpenAI completed the request but did not return a generated package label image."
                            });
                    }


                    // --------------------------------------------------------
                    // 14. CREATE DATA URL
                    // --------------------------------------------------------
                    //
                    // This lets the Blazor app immediately display the
                    // generated image without requiring Azure Blob Storage.
                    //
                    // Later, if desired, this Base64 image can be uploaded
                    // to Blob Storage and PreviewUrl can be replaced by the
                    // permanent blob URL.
                    // --------------------------------------------------------

                    string imageDataUrl =
                        $"data:image/png;base64,{generatedImageBase64}";


                    // --------------------------------------------------------
                    // 15. RETURN RESPONSE EXPECTED BY BLAZOR MODAL
                    // --------------------------------------------------------

                    return new OkObjectResult(
                        new
                        {
                            Success = true,

                            Message =
                                "Package label generated successfully.",

                            PreviewUrl =
                                imageDataUrl,

                            DownloadUrl =
                                imageDataUrl,

                            ImageBase64 =
                                generatedImageBase64,

                            MimeType =
                                "image/png",

                            FileName =
                                BuildFileName(
                                    companyName,
                                    productName),

                            SourceImageUrl =
                                getData.ImageUrl,

                            CompanyName =
                                companyName,

                            Industry =
                                industry,

                            ProductName =
                                productName,

                            LabelType =
                                labelType,

                            LabelSize =
                                labelSize,

                            Material =
                                material,

                            Orientation =
                                orientation,

                            GeneratedAtUtc =
                                DateTime.UtcNow
                        });
                }
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
                await _usage.CompleteFailureAsync(usageId, "Timeout");
                Console.WriteLine(
                    $"GeneratePackageLabel timeout: {ex}");

                return new BadRequestObjectResult(
                    new
                    {
                        Success = false,
                        Message =
                            "Package label generation timed out. Please try again.",
                        Error = ex.Message
                    });
            }
            catch (Exception ex)
            {
                await _usage.CompleteFailureAsync(usageId, "ProviderError");
                Console.WriteLine(
                    $"GeneratePackageLabel exception: {ex}");

                return new BadRequestObjectResult(
                    new
                    {
                        Success = false,
                        Message =
                            "An exception occurred while generating the package label.",
                        Error = ex.Message
                    });
            }
        }

        private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
        {
            var username = _jwt.ResolveUsername(request);
            return string.IsNullOrWhiteSpace(username)
                ? null
                : await _attribution.ResolveUserIdAsync(username);
        }

        [Function("SavePackageLabel")]
        public async Task<IActionResult> SavePackageLabel(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer != null)
            {
                if (referer != Environment.GetEnvironmentVariable("HostUrl"))
                {
                    return new UnauthorizedResult();
                }
            }
            else
            {
                return new UnauthorizedResult();
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var packageLabelData =
                System.Text.Json.JsonSerializer.Deserialize<PackageLabelRequest>(
                    requestBody,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (packageLabelData == null)
            {
                return new BadRequestObjectResult(new
                {
                    Message = "Invalid package label data."
                });
            }

            if (string.IsNullOrWhiteSpace(packageLabelData.PackageLabelId) ||
                string.IsNullOrWhiteSpace(packageLabelData.CompanyName) ||
                string.IsNullOrWhiteSpace(packageLabelData.Version) ||
                string.IsNullOrWhiteSpace(packageLabelData.TimeStamp))
            {
                return new BadRequestObjectResult(new
                {
                    Message = "PackageLabelId, CompanyName, Version, and TimeStamp are required."
                });
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate a unique row Id
                string packageLabelRecordId;

                while (true)
                {
                    packageLabelRecordId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement =
                            "SELECT Id FROM PackageLabel WHERE Id = ?",

                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue
                    {
                        S = packageLabelRecordId
                    }
                }
                    };

                    var existingIdResult =
                        await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                    {
                        break;
                    }
                }

                // Insert the generated package label record
                var insertStatement =
                    "INSERT INTO PackageLabel VALUE {" +
                    "'Id': ?, " +
                    "'PackageLabelId': ?, " +
                    "'CompanyName': ?, " +
                    "'Version': ?, " +
                    "'TimeStamp': ?" +
                    "}";

                var parameters = new List<AttributeValue>
        {
            new AttributeValue
            {
                S = packageLabelRecordId
            },
            new AttributeValue
            {
                S = packageLabelData.PackageLabelId
            },
            new AttributeValue
            {
                S = packageLabelData.CompanyName
            },
            new AttributeValue
            {
                S = packageLabelData.Version
            },
            new AttributeValue
            {
                S = packageLabelData.TimeStamp
            }
        };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = insertStatement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new
                {
                    Message = "Package label saved successfully",
                    PackageLabelRecordId = packageLabelRecordId,
                    PackageLabelId = packageLabelData.PackageLabelId,
                    CompanyName = packageLabelData.CompanyName,
                    Version = packageLabelData.Version,
                    TimeStamp = packageLabelData.TimeStamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Saving package label failed.");

                return new StatusCodeResult(500);
            }
        }

        [Function("GetPackageLabelsByCompany")]
        public async Task<IActionResult> GetPackageLabelsByCompany(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer != null)
            {
                if (referer != Environment.GetEnvironmentVariable("HostUrl"))
                {
                    return new UnauthorizedResult();
                }
            }
            else
            {
                return new UnauthorizedResult();
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var data = System.Text.Json.JsonSerializer.Deserialize<GetPackageLabelsByCompanyRequest>(
                requestBody,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (string.IsNullOrWhiteSpace(data?.CompanyName))
            {
                return new BadRequestObjectResult("CompanyName is required");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query PackageLabel table by CompanyName
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement =
                        "SELECT * FROM PackageLabel WHERE CompanyName = ?",

                    Parameters = new List<AttributeValue>
            {
                new AttributeValue
                {
                    S = data.CompanyName
                }
            }
                };

                var result =
                    await client.ExecuteStatementAsync(queryRequest);

                var packageLabels = result.Items
                    .Select(item => new
                    {
                        Id = item.ContainsKey("Id")
                            ? item["Id"].S
                            : null,

                        PackageLabelId = item.ContainsKey("PackageLabelId")
                            ? item["PackageLabelId"].S
                            : null,

                        CompanyName = item.ContainsKey("CompanyName")
                            ? item["CompanyName"].S
                            : null,

                        Version = item.ContainsKey("Version")
                            ? item["Version"].S
                            : null,

                        TimeStamp = item.ContainsKey("TimeStamp")
                            ? item["TimeStamp"].S
                            : null
                    })
                    .ToList();

                // Sort newest versions first
                packageLabels = packageLabels
                    .OrderByDescending(x =>
                    {
                        if (DateTime.TryParse(
                            x.TimeStamp,
                            out var timestamp))
                        {
                            return timestamp;
                        }

                        return DateTime.MinValue;
                    })
                    .ToList();

                return new OkObjectResult(new
                {
                    Message = "Package labels retrieved successfully",
                    Count = packageLabels.Count,
                    Data = packageLabels
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Fetching package labels by company failed.");

                return new StatusCodeResult(500);
            }
        }

        [Function("SavePackageLabelImage")]
        public async Task<IActionResult> SavePackageLabelImage(
                    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // ============================================================
            // ORIGIN VALIDATION
            // ============================================================
            string? referer = req.Headers["Origin"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(referer) ||
                referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            var requestBody =
                await new StreamReader(req.Body).ReadToEndAsync();

            var data =
                System.Text.Json.JsonSerializer.Deserialize<SavePackageLabelImageRequest>(
                    requestBody,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            if (data == null ||
                string.IsNullOrWhiteSpace(data.ImageUrl) ||
                string.IsNullOrWhiteSpace(data.CompanyName) ||
                string.IsNullOrWhiteSpace(data.Version))
            {
                return new BadRequestObjectResult(
                    "ImageUrl, CompanyName, and Version are required.");
            }

            try
            {
                var useLocal =
                    string.Equals(
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                        "Development",
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    req.Host.Host.Contains(
                        "localhost",
                        StringComparison.OrdinalIgnoreCase);

                // ========================================================
                // SANITIZE COMPANY NAME
                // ========================================================
                var safeCompanyName =
                    string.Concat(
                        data.CompanyName.Where(
                            c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                // Accept "v4" or "4". Both become "4".
                var safeVersion =
                    string.Concat(
                        data.Version.Where(
                            c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .Replace(
                        "v",
                        "",
                        StringComparison.OrdinalIgnoreCase)
                    .ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(safeCompanyName) ||
                    string.IsNullOrWhiteSpace(safeVersion))
                {
                    return new BadRequestObjectResult(
                        "CompanyName or Version is invalid.");
                }

                // This must match PackageLabelModal.GetPackageLabelFolderName().
                var folderName =
                    $"{safeCompanyName}-package-label-v{safeVersion}";

                const string fileName = "package-label.png";

                byte[] imageBytes;
                string contentType = "image/png";

                // ========================================================
                // SOURCE OPTION 1: BASE64 DATA URI
                // ========================================================
                if (data.ImageUrl.StartsWith(
                    "data:image/",
                    StringComparison.OrdinalIgnoreCase))
                {
                    var commaIndex = data.ImageUrl.IndexOf(',');

                    if (commaIndex < 0)
                    {
                        return new BadRequestObjectResult(
                            "Invalid base64 image data URI.");
                    }

                    var metadata =
                        data.ImageUrl.Substring(0, commaIndex);

                    var base64Data =
                        data.ImageUrl.Substring(commaIndex + 1);

                    if (metadata.StartsWith(
                        "data:",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var semicolonIndex = metadata.IndexOf(';');

                        if (semicolonIndex > 5)
                        {
                            contentType =
                                metadata.Substring(
                                    5,
                                    semicolonIndex - 5);
                        }
                    }

                    base64Data =
                        base64Data
                            .Replace("\r", "")
                            .Replace("\n", "")
                            .Trim();

                    try
                    {
                        imageBytes =
                            Convert.FromBase64String(base64Data);
                    }
                    catch (FormatException)
                    {
                        return new BadRequestObjectResult(
                            "The package label contains invalid base64 image data.");
                    }
                }

                // ========================================================
                // SOURCE OPTION 2: HTTP / HTTPS
                // ========================================================
                else if (
                    data.ImageUrl.StartsWith(
                        "http://",
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    data.ImageUrl.StartsWith(
                        "https://",
                        StringComparison.OrdinalIgnoreCase))
                {
                    using var httpClient = new HttpClient();

                    using var httpResponse =
                        await httpClient.GetAsync(data.ImageUrl);

                    httpResponse.EnsureSuccessStatusCode();

                    imageBytes =
                        await httpResponse.Content.ReadAsByteArrayAsync();

                    var remoteContentType =
                        httpResponse.Content.Headers.ContentType?.MediaType;

                    if (!string.IsNullOrWhiteSpace(remoteContentType))
                    {
                        contentType = remoteContentType;
                    }
                }
                else
                {
                    return new BadRequestObjectResult(
                        "ImageUrl must be either an HTTP/HTTPS URL or a base64 data:image URI.");
                }

                if (imageBytes == null || imageBytes.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Generated package label image was empty.");
                }

                string filePath;
                string webUrl;

                // ========================================================
                // LOCAL STORAGE
                //
                // wwwroot/
                //   SavedImages/
                //     Company-package-label-v1/
                //       package-label.png
                // ========================================================
                if (useLocal)
                {
                    var wwwrootPath =
                        Path.Combine(
                            Directory.GetCurrentDirectory(),
                            "wwwroot");

                    var savedImagesPath =
                        Path.Combine(
                            wwwrootPath,
                            "SavedImages");

                    var folderPath =
                        Path.Combine(
                            savedImagesPath,
                            folderName);

                    Directory.CreateDirectory(folderPath);

                    filePath =
                        Path.Combine(
                            folderPath,
                            fileName);

                    await System.IO.File.WriteAllBytesAsync(
                        filePath,
                        imageBytes);

                    // Intentionally omit the Azure Functions development port.
                    // Example:
                    // http://localhost/SavedImages/Company-package-label-v1/package-label.png
                    var rootUrl =
                        $"{req.Scheme}://{req.Host.Host}";

                    webUrl =
                        $"{rootUrl}/{folderName}/{fileName}";

                    _logger.LogInformation(
                        "Package label saved locally. File={FilePath}, Url={WebUrl}",
                        filePath,
                        webUrl);
                }

                // ========================================================
                // AWS S3 STORAGE
                //
                // SavedImages/
                //   Company-package-label-v1/
                //     package-label.png
                //
                // IMPORTANT:
                // Do not set an object ACL here. Production buckets using
                // Bucket owner enforced have ACLs disabled.
                // ========================================================
                else
                {
                    var bucketName =
                        Environment.GetEnvironmentVariable("S3_BUCKET_NAME");

                    var region =
                        Environment.GetEnvironmentVariable("AWS_REGION");

                    var accessKey =
                        Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");

                    var secretKey =
                        Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    if (string.IsNullOrWhiteSpace(bucketName))
                    {
                        throw new InvalidOperationException(
                            "S3_BUCKET_NAME is not configured.");
                    }

                    if (string.IsNullOrWhiteSpace(region))
                    {
                        throw new InvalidOperationException(
                            "AWS_REGION is not configured.");
                    }

                    if (string.IsNullOrWhiteSpace(accessKey))
                    {
                        throw new InvalidOperationException(
                            "AWS_ACCESS_KEY_ID is not configured.");
                    }

                    if (string.IsNullOrWhiteSpace(secretKey))
                    {
                        throw new InvalidOperationException(
                            "AWS_SECRET_ACCESS_KEY is not configured.");
                    }

                    var config =
                        new AmazonS3Config
                        {
                            RegionEndpoint =
                                RegionEndpoint.GetBySystemName(region)
                        };

                    using var s3Client =
                        new AmazonS3Client(
                            accessKey,
                            secretKey,
                            config);

                    var keyName =
                        $"{folderName}/{fileName}";

                    using var stream =
                        new MemoryStream(imageBytes);

                    var putRequest =
                        new PutObjectRequest
                        {
                            BucketName = bucketName,
                            Key = keyName,
                            InputStream = stream,
                            ContentType = contentType
                        };

                    putRequest.Headers["Content-Disposition"] =
                        "inline";

                    // Upload only. No CannedACL and no PutACLAsync.
                    await s3Client.PutObjectAsync(putRequest);

                    filePath = keyName;

                    webUrl =
                        $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";

                    _logger.LogInformation(
                        "Package label saved to S3. Key={Key}, Url={WebUrl}",
                        keyName,
                        webUrl);
                }

                return new OkObjectResult(
                    new
                    {
                        Success = true,

                        Message =
                            useLocal
                                ? "Package label image saved locally."
                                : "Package label image saved to S3.",

                        FilePath = filePath,
                        FileName = fileName,
                        FolderName = folderName,
                        WebUrl = webUrl,
                        ImageUrl = webUrl,
                        SavedUrl = webUrl,
                        ContentType = contentType,
                        Size = imageBytes.Length,

                        Mode =
                            useLocal
                                ? "LOCAL"
                                : "S3"
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed saving package label image.");

                return new ObjectResult(
                    new
                    {
                        Success = false,
                        Message = "Error saving package label image.",
                        Error = ex.Message
                    })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        public class SavePackageLabelImageRequest
        {
            public string ImageUrl { get; set; } = string.Empty;

            public string CompanyName { get; set; } = string.Empty;

            public string Version { get; set; } = string.Empty;

            // Accepted so the current Blazor request can keep sending it.
            // The function intentionally uses the canonical
            // "package-label.png" filename for deterministic loading.
            public string FileName { get; set; } = string.Empty;
        }

        public class GetPackageLabelsByCompanyRequest
        {
            public string CompanyName { get; set; } = string.Empty;
        }

        public class PackageLabelRequest
        {
            public string PackageLabelId { get; set; } = string.Empty;

            public string CompanyName { get; set; } = string.Empty;

            public string Version { get; set; } = string.Empty;

            public string TimeStamp { get; set; } = string.Empty;
        }

        // ================================================================
        // HELPERS
        // ================================================================

        private static string BoolText(bool value)
        {
            return value ? "YES" : "NO";
        }


        private static string GetReadableLabelType(
            string labelType)
        {
            if (string.IsNullOrWhiteSpace(labelType))
            {
                return "Product Label";
            }

            return labelType switch
            {
                "ProductLabel" =>
                    "Product Label",

                "ShippingLabel" =>
                    "Shipping Label",

                "WarningLabel" =>
                    "Warning / Caution Label",

                "InformationalLabel" =>
                    "Informational Label",

                "BrandedLabel" =>
                    "Branded Packaging Label",

                "PromotionalLabel" =>
                    "Promotional Sticker / Label",

                _ =>
                    labelType
            };
        }


        private static string BuildFileName(
            string companyName,
            string productName)
        {
            string rawName =
                $"{companyName}-{productName}-package-label";

            foreach (char invalidChar
                     in Path.GetInvalidFileNameChars())
            {
                rawName =
                    rawName.Replace(
                        invalidChar.ToString(),
                        "");
            }

            rawName =
                rawName
                    .Replace(" ", "-")
                    .Replace("&", "and")
                    .ToLowerInvariant();


            while (rawName.Contains("--"))
            {
                rawName =
                    rawName.Replace("--", "-");
            }


            rawName =
                rawName.Trim('-');


            if (string.IsNullOrWhiteSpace(rawName))
            {
                rawName =
                    "package-label";
            }


            return
                $"{rawName}-{DateTime.UtcNow:yyyyMMddHHmmss}.png";
        }
    }


    // ====================================================================
    // REQUEST MODEL
    // ====================================================================
    //
    // This matches the request currently being sent from the Blazor
    // PackageLabelModal.
    // ====================================================================

    public class GeneratePackageLabelRequest
    {
        // ------------------------------------------------------------
        // Selected source image
        // ------------------------------------------------------------

        public string ImageUrl { get; set; }

        public string SelectedImageId { get; set; }


        // ------------------------------------------------------------
        // Company / branding
        // ------------------------------------------------------------

        public string Name { get; set; }

        public string Industry { get; set; }

        public string Prompt { get; set; }

        public string CompanyDescription { get; set; }


        // ------------------------------------------------------------
        // Label specifications
        // ------------------------------------------------------------

        public string LabelType { get; set; }

        public string LabelSize { get; set; }

        public string Material { get; set; }

        public string Orientation { get; set; }


        // ------------------------------------------------------------
        // Label content
        // ------------------------------------------------------------

        public string ProductName { get; set; }

        public string SkuBarcode { get; set; }

        public string AdditionalInfo { get; set; }


        // ------------------------------------------------------------
        // Options
        // ------------------------------------------------------------

        public bool IncludeBarcode { get; set; }

        public bool IncludeQrCode { get; set; }

        public bool IncludeBrandLogo { get; set; }


        // ------------------------------------------------------------
        // Application metadata
        // ------------------------------------------------------------

        public string UserId { get; set; }

        public string CompanyIdeaId { get; set; }

        public string PromptId { get; set; }

        public string Timestamp { get; set; }
        public string GenerationJobId { get; set; }
        public string ProjectId { get; set; }
        public string FunnelId { get; set; }
    }

    public class LabelGenerationResult
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        public string PreviewUrl { get; set; }

        public string DownloadUrl { get; set; }

        public string ImageBase64 { get; set; }

        public string MimeType { get; set; }

        public string FileName { get; set; }

        public string SourceImageUrl { get; set; }

        public string CompanyName { get; set; }

        public string Industry { get; set; }

        public string ProductName { get; set; }

        public string LabelType { get; set; }

        public string LabelSize { get; set; }

        public string Material { get; set; }

        public string Orientation { get; set; }
    }
}

using DesignMode.AI.Shared.Interfaces.Business;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using DesignMode.AI.Common;
using DesignMode.AI.Shared;
using DesignMode.AI.Shared.Interfaces.Data;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Models.Attribution;
using DesignMode.AI.Shared.Requests;
using Newtonsoft.Json;
using DesignMode.AI.Services.OpenAI;
using DesignMode.AI.Services.OpenAI.Models;
using DesignMode.AI.Services.OpenAI.Models.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest.Azure.Authentication;
using Microsoft.Azure.Management.Sql;
using Microsoft.Azure.Management.Sql.Models;
using System.Net.Http;
using DesignMode.AI.Shared.Util;
using System.Data.SqlClient;
using System.Text.Json;
using System.Reflection;
using System.Net.Http.Json;
using Telerik.Blazor.Components.Gantt.Models;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;
using MySql.Data;
using MySql.Data.MySqlClient;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Azure.Functions.Worker.Http;
using Google.Apis.Auth;
using static DesignMode.AI.Server.DesignModeFunctions;
using static Grpc.Core.Metadata;
using Telerik.SvgIcons;
using System.Net;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.IO.Compression;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs.Models;
using Microsoft.Playwright;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.AspNetCore.WebUtilities;
using Grpc.Core;
using Microsoft.AspNetCore.Identity.Data;
using SendGrid.Helpers.Mail;
using Telerik.Windows.Documents.Fixed.Model.Navigation;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Stripe;
using Stripe.Checkout;
using DesignMode.AI.Shared.Services;
using Newtonsoft.Json.Linq;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using DesignMode.AI.Common.Extensions;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Server.Security;


namespace DesignMode.AI.Server
{
    public partial class DesignModeFunctions
    {
        private readonly ILogger<DesignModeFunctions> _logger;
        private readonly IConfiguration? _configuration;
        private readonly ITestString testStringBac;
        private readonly AttributionStore attributionStore;
        private readonly CanonicalUserStore canonicalUserStore;
        private readonly JwtUserResolver jwtUserResolver;
        private readonly AIGenerationUsageStore aiUsage;
        private readonly AIProviderPricingService aiPricing;
        private readonly AIUsageSummaryService? aiUsageSummary;
        private readonly AIUsageRecorder? aiUsageRecorder;
        private readonly Func<HttpClient> websiteGenerationClient;
        private readonly Func<HttpClient> runwareImageClient;
        private readonly StripeVerifiedPurchaseHandler purchaseMeasurement;
        private readonly StripeCreditPurchaseHandler? creditPurchases;
        private readonly FunnelRecoveryPublisher? funnelRecovery;
        private readonly FunnelRecoverySequence? funnelRecoverySequence;
        private const string VideoJobsQueueName = "video-jobs";
        private static readonly HttpClient Http = new();

        /// <summary>
        /// Initializes a new instance of Function1 class.
        /// </summary>
        /// <param name="logger">The pack list bac.</param>
        /// <param name="teststring">The quote bac.</param>
        public DesignModeFunctions(ILogger<DesignModeFunctions> logger, ITestString testStringBac, AttributionStore attributionStore, CanonicalUserStore canonicalUserStore, JwtUserResolver jwtUserResolver, AIGenerationUsageStore aiUsage, AIProviderPricingService aiPricing, StripeVerifiedPurchaseHandler purchaseMeasurement, AIUsageSummaryService? aiUsageSummary = null, AIUsageRecorder? aiUsageRecorder = null, FunnelRecoveryPublisher? funnelRecovery = null, FunnelRecoverySequence? funnelRecoverySequence = null, Func<HttpClient>? websiteGenerationClient = null, Func<HttpClient>? runwareImageClient = null, StripeCreditPurchaseHandler? creditPurchases = null, IConfiguration? configuration = null)
        {
            _logger = logger;
            _configuration = configuration;
            this.runwareImageClient = runwareImageClient ?? (() => new HttpClient());
            this.testStringBac = testStringBac;
            this.attributionStore = attributionStore;
            this.canonicalUserStore = canonicalUserStore;
            this.jwtUserResolver = jwtUserResolver;
            this.aiUsage = aiUsage;
            this.aiPricing = aiPricing;
            this.aiUsageSummary = aiUsageSummary;
            this.aiUsageRecorder = aiUsageRecorder;
            this.websiteGenerationClient = websiteGenerationClient ?? (() => new HttpClient());
            this.purchaseMeasurement = purchaseMeasurement;
            this.creditPurchases = creditPurchases;
            this.funnelRecovery = funnelRecovery;
            this.funnelRecoverySequence = funnelRecoverySequence;
        }

        [Function("QueueApiCall")]
        public async Task<IActionResult> QueueApiCall(
            [HttpTrigger(
        AuthorizationLevel.Function,
        "post")]
    HttpRequest req,
            CancellationToken cancellationToken)
        {
            try
            {
                string? origin =
                    req.Headers["Origin"].FirstOrDefault();

                string? hostUrl =
                    Environment.GetEnvironmentVariable("HostUrl");

                if (string.IsNullOrWhiteSpace(origin) ||
                    string.IsNullOrWhiteSpace(hostUrl) ||
                    !string.Equals(
                        origin,
                        hostUrl,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new UnauthorizedResult();
                }

                OpenAIIdeaRequestQueue? requestData =
                    await System.Text.Json.JsonSerializer
                        .DeserializeAsync<OpenAIIdeaRequestQueue>(
                            req.Body,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            },
                            cancellationToken);

                if (requestData is null)
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "The request body could not be parsed."
                        });
                }

                if (!string.IsNullOrWhiteSpace(requestData.ProjectId) || !string.IsNullOrWhiteSpace(requestData.FunnelId))
                {
                    var generationUsername = jwtUserResolver.ResolveUsername(req);
                    var generationUserId = string.IsNullOrWhiteSpace(generationUsername) ? null : await attributionStore.ResolveUserIdAsync(generationUsername);
                    if (string.IsNullOrWhiteSpace(generationUserId) || string.IsNullOrWhiteSpace(requestData.ProjectId) || string.IsNullOrWhiteSpace(requestData.FunnelId) ||
                        !await attributionStore.ValidateGenerationContextAsync(generationUserId, requestData.ProjectId, requestData.FunnelId))
                        return new UnauthorizedResult();
                    requestData.UserId = generationUserId;
                }

                /*
                 * ==========================================================
                 * VALIDATE IMAGE ID
                 * ==========================================================
                 */

                if (string.IsNullOrWhiteSpace(requestData.ImageId))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "Missing 'imageId' in the request body."
                        });
                }

                /*
                 * ==========================================================
                 * VALIDATE IMAGE URL
                 * ==========================================================
                 */

                if (string.IsNullOrWhiteSpace(requestData.ImageUrl))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "Missing 'imageUrl' in the request body."
                        });
                }

                if (!Uri.TryCreate(
                        requestData.ImageUrl,
                        UriKind.Absolute,
                        out Uri? imageUri) ||
                    (imageUri.Scheme != Uri.UriSchemeHttp &&
                     imageUri.Scheme != Uri.UriSchemeHttps))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error =
                                "'imageUrl' must be a valid HTTP or HTTPS URL."
                        });
                }

                /*
                 * ==========================================================
                 * VALIDATE CONCEPT NUMBER
                 * ==========================================================
                 */

                if (requestData.ConceptNumber <= 0)
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error =
                                "'conceptNumber' must be greater than zero."
                        });
                }

                /*
                 * Generate the job ID here.
                 *
                 * The ImageId is supplied by the caller and is preserved on
                 * requestData so it is stored in DynamoDB and serialized into
                 * the Azure Storage Queue message.
                 */
                requestData.JobId = Guid.TryParse(requestData.GenerationJobId, out _)
                    ? requestData.GenerationJobId!
                    : Guid.NewGuid().ToString();
                requestData.GenerationJobId = requestData.JobId;

                string now =
                    DateTimeOffset.UtcNow.ToString("O");

                string tableName =
                    Environment.GetEnvironmentVariable(
                        "DynamoDbTableName")
                    ?? "DesignModeJobs";

                var dynamoDb = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                /*
                 * ==========================================================
                 * CREATE DYNAMODB JOB
                 * ==========================================================
                 */

                using (dynamoDb)
                {
                    await dynamoDb.PutItemAsync(
                        new PutItemRequest
                        {
                            TableName = tableName,

                            Item =
                                new Dictionary<string, AttributeValue>
                                {
                                    ["JobId"] =
                                        new AttributeValue
                                        {
                                            S = requestData.JobId
                                        },

                                    /*
                                     * Store the incoming ImageId immediately
                                     * when the job is created.
                                     */
                                    ["ImageId"] =
                                        new AttributeValue
                                        {
                                            S = requestData.ImageId
                                        },

                                    ["GenerationJobId"] = new AttributeValue { S = requestData.GenerationJobId },
                                    ["UserId"] = new AttributeValue { S = requestData.UserId ?? string.Empty },
                                    ["ProjectId"] = new AttributeValue { S = requestData.ProjectId ?? string.Empty },
                                    ["FunnelId"] = new AttributeValue { S = requestData.FunnelId ?? string.Empty },
                                    ["PreviewKind"] = new AttributeValue { S = WebsiteFunnelPromptResolver.IsWebsite(requestData.LandingVariant) ? "Website" : "Social" },

                                    ["Status"] =
                                        new AttributeValue
                                        {
                                            S = "Queued"
                                        },

                                    ["CurrentStep"] =
                                        new AttributeValue
                                        {
                                            S = "Queued"
                                        },

                                    ["Name"] =
                                        new AttributeValue
                                        {
                                            S = requestData.Name
                                                ?? string.Empty
                                        },

                                    ["Industry"] =
                                        new AttributeValue
                                        {
                                            S = requestData.Industry
                                                ?? string.Empty
                                        },

                                    ["ConceptNumber"] =
                                        new AttributeValue
                                        {
                                            N = requestData.ConceptNumber
                                                .ToString()
                                        },

                                    ["ImageUrl"] =
                                        new AttributeValue
                                        {
                                            S = requestData.ImageUrl
                                        },

                                    ["ImageDescription"] =
                                        new AttributeValue
                                        {
                                            S = string.Empty
                                        },

                                    ["Script"] =
                                        new AttributeValue
                                        {
                                            S = string.Empty
                                        },

                                    ["VideoUrl"] =
                                        new AttributeValue
                                        {
                                            S = string.Empty
                                        },

                                    ["Error"] =
                                        new AttributeValue
                                        {
                                            S = string.Empty
                                        },

                                    ["CreatedAtUtc"] =
                                        new AttributeValue
                                        {
                                            S = now
                                        },

                                    ["UpdatedAtUtc"] =
                                        new AttributeValue
                                        {
                                            S = now
                                        }
                                },

                            ConditionExpression =
                                "attribute_not_exists(JobId)"
                        },
                        cancellationToken);
                }

                /*
                 * ==========================================================
                 * ADD REQUEST TO AZURE STORAGE QUEUE
                 * ==========================================================
                 */

                string storageConnectionString =
                    Environment.GetEnvironmentVariable(
                        "AzureWebJobsStorage")
                    ?? throw new InvalidOperationException(
                        "AzureWebJobsStorage is missing.");

                var queue =
                    new QueueClient(
                        storageConnectionString,
                        VideoQueuePipeline.RequestQueueName,
                        VideoQueuePipeline.CreateClientOptions());

                _logger.LogInformation(
                    "[VIDEO QUEUE] producer storage={Storage} queue={Queue}",
                    VideoQueuePipeline.DescribeStorage(storageConnectionString),
                    VideoQueuePipeline.RequestQueueName);

                await queue.CreateIfNotExistsAsync(
                    cancellationToken: cancellationToken);

                /*
                 * ImageId is automatically included here because it is a
                 * property on OpenAIIdeaRequestQueue.
                 */
                string queueMessage = VideoQueuePipeline.Serialize(requestData);

                await queue.SendMessageAsync(
                    queueMessage,
                    cancellationToken);

                _logger.LogInformation(
                    "[VIDEO QUEUE] message enqueued queue={Queue} jobId={JobId}",
                    VideoQueuePipeline.RequestQueueName,
                    requestData.JobId);

                /*
                 * ==========================================================
                 * RETURN ACCEPTED RESPONSE
                 * ==========================================================
                 */

                return new ObjectResult(
                    new
                    {
                        jobId = requestData.JobId,
                        imageId = requestData.ImageId,
                        status = "Queued",
                        currentStep = "Queued"
                    })
                {
                    StatusCode =
                        StatusCodes.Status202Accepted
                };
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "QueueApiCall received invalid JSON.");

                return new BadRequestObjectResult(
                    new
                    {
                        error =
                            "The request body contains invalid JSON."
                    });
            }
            catch (ConditionalCheckFailedException ex)
            {
                _logger.LogError(
                    ex,
                    "The generated JobId already exists.");

                return new ObjectResult(
                    new
                    {
                        error =
                            "Unable to create a unique job."
                    })
                {
                    StatusCode =
                        StatusCodes.Status409Conflict
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to queue the generation request.");

                return new ObjectResult(
                    new
                    {
                        error =
                            "Unable to queue the generation request."
                    })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }

        public string ImageId { get; set; } = string.Empty;
        [Function("ProcessApiCall")]
        public async Task ProcessApiCall(
            [QueueTrigger(
        VideoQueuePipeline.RequestQueueName,
        Connection = "AzureWebJobsStorage")]
    string body,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "[VIDEO QUEUE] processor started queue={Queue}",
                VideoQueuePipeline.RequestQueueName);

            OpenAIIdeaRequestQueue? queueRequest = null;
            AmazonDynamoDBClient? dynamoDb = null;

            string tableName =
                Environment.GetEnvironmentVariable("DynamoDbTableName")
                ?? "DesignModeJobs";

            try
            {
                /*
                 * ==========================================================
                 * CREATE DYNAMODB CLIENT
                 * ==========================================================
                 */

                dynamoDb = DynamoDbClientFactory.CreateClient();

                /*
                 * ==========================================================
                 * READ AND VALIDATE QUEUE MESSAGE
                 * ==========================================================
                 */

                queueRequest = VideoQueuePipeline.Deserialize<OpenAIIdeaRequestQueue>(body);

                if (queueRequest is null)
                {
                    throw new InvalidOperationException(
                        "The queued request could not be deserialized.");
                }

                if (string.IsNullOrWhiteSpace(queueRequest.JobId))
                {
                    throw new InvalidOperationException(
                        "The queued request is missing jobId.");
                }

                if (string.IsNullOrWhiteSpace(queueRequest.ImageId))
                {
                    throw new InvalidOperationException(
                        "The queued request is missing imageId.");
                }

                _logger.LogInformation(
                    "[VIDEO QUEUE] message parsed jobId={JobId}",
                    queueRequest.JobId);

                if (string.IsNullOrWhiteSpace(queueRequest.Name))
                {
                    throw new InvalidOperationException(
                        "The queued request is missing name.");
                }

                if (string.IsNullOrWhiteSpace(queueRequest.Industry))
                {
                    throw new InvalidOperationException(
                        "The queued request is missing industry.");
                }

                if (string.IsNullOrWhiteSpace(queueRequest.ImageUrl))
                {
                    throw new InvalidOperationException(
                        "The queued request is missing imageUrl.");
                }

                if (!Uri.TryCreate(
                        queueRequest.ImageUrl,
                        UriKind.Absolute,
                        out Uri? imageUri) ||
                    (imageUri.Scheme != Uri.UriSchemeHttp &&
                     imageUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException(
                        "The queued imageUrl must be a valid HTTP or HTTPS URL.");
                }

                if (queueRequest.ConceptNumber <= 0)
                {
                    throw new InvalidOperationException(
                        "The queued request has an invalid concept number.");
                }

                /*
                 * ==========================================================
                 * READ SETTINGS
                 * ==========================================================
                 */

                string imageDescriptionApiUrl =
                    Environment.GetEnvironmentVariable(
                        "BackgroundImageDescriptionApiUrl")
                    ?? throw new InvalidOperationException(
                        "BackgroundImageDescriptionApiUrl is missing.");

                string generateScriptApiUrl =
                    Environment.GetEnvironmentVariable(
                        "BackgroundGenerateScriptApiUrl")
                    ?? throw new InvalidOperationException(
                        "BackgroundGenerateScriptApiUrl is missing.");

                string generateVideoApiUrl =
                    Environment.GetEnvironmentVariable(
                        "BackgroundGenerateVideoApiUrl")
                    ?? throw new InvalidOperationException(
                        "BackgroundGenerateVideoApiUrl is missing.");

                string hostUrl =
                    Environment.GetEnvironmentVariable(
                        "HostUrl")
                    ?? throw new InvalidOperationException(
                        "HostUrl is missing.");

                /*
                 * ==========================================================
                 * STEP 1: GENERATE IMAGE DESCRIPTION
                 * ==========================================================
                 */

                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key =
                            new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.JobId
                                    }
                            },

                        UpdateExpression =
                            "SET #status = :status, " +
                            "CurrentStep = :currentStep, " +
                            "#error = :error, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeNames =
                            new Dictionary<string, string>
                            {
                                ["#status"] = "Status",
                                ["#error"] = "Error"
                            },

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":status"] =
                                    new AttributeValue
                                    {
                                        S = "Processing"
                                    },

                                [":currentStep"] =
                                    new AttributeValue
                                    {
                                        S = "GeneratingImageDescription"
                                    },

                                [":error"] =
                                    new AttributeValue
                                    {
                                        S = string.Empty
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow
                                            .ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                var imageDescriptionRequestBody =
                    new
                    {
                        imageUrl = queueRequest.ImageUrl,
                        generationJobId = queueRequest.GenerationJobId ?? queueRequest.JobId,
                        userId = queueRequest.UserId,
                        projectId = queueRequest.ProjectId,
                        funnelId = queueRequest.FunnelId,
                        landingVariant = queueRequest.LandingVariant
                    };

                string imageDescriptionRequestJson =
                    System.Text.Json.JsonSerializer.Serialize(
                        imageDescriptionRequestBody);

                using var imageDescriptionRequest =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        imageDescriptionApiUrl);

                imageDescriptionRequest.Content =
                    new StringContent(
                        imageDescriptionRequestJson,
                        Encoding.UTF8,
                        "application/json");

                imageDescriptionRequest.Headers
                    .TryAddWithoutValidation(
                        "Origin",
                        hostUrl);

                using HttpResponseMessage imageDescriptionResponse =
                    await Http.SendAsync(
                        imageDescriptionRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                string imageDescriptionResponseBody =
                    await imageDescriptionResponse.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                _logger.LogInformation(
                    "Image description API returned {StatusCode}. " +
                    "Body: {ResponseBody}",
                    (int)imageDescriptionResponse.StatusCode,
                    imageDescriptionResponseBody);

                if (!imageDescriptionResponse.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Image description API returned " +
                        $"{(int)imageDescriptionResponse.StatusCode} " +
                        $"{imageDescriptionResponse.ReasonPhrase}. " +
                        $"Body: {imageDescriptionResponseBody}");
                }

                string imageDescription =
                    ReadStringResponse(
                        imageDescriptionResponseBody,
                        "The image description API returned an empty result.");

                /*
                 * Save both the generated image description and the ImageId
                 * from the queue message.
                 */
                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key =
                            new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.JobId
                                    }
                            },

                        UpdateExpression =
                            "SET ImageDescription = :imageDescription, " +
                            "ImageId = :imageId, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":imageDescription"] =
                                    new AttributeValue
                                    {
                                        S = imageDescription
                                    },

                                [":imageId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.ImageId
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow
                                            .ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                /*
                 * ==========================================================
                 * STEP 2: GENERATE SCENE SCRIPT / VIDEO PROMPT
                 * ==========================================================
                 */

                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key =
                            new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.JobId
                                    }
                            },

                        UpdateExpression =
                            "SET CurrentStep = :currentStep, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":currentStep"] =
                                    new AttributeValue
                                    {
                                        S = "GeneratingScript"
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow
                                            .ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                string fullVideoPrompt =
                    $"The prompt is for a company named " +
                    $"{queueRequest.Name} in the " +
                    $"{queueRequest.Industry} industry.";

                /*
                 * These three property names match
                 * AIVideoSceneFromImageRequest:
                 *
                 * FullVideoPrompt
                 * ImageBase64
                 * SceneNumber
                 *
                 * ImageBase64 currently contains the image description
                 * because RegenerateSceneFromImageInit treats that field as
                 * the source image description.
                 */
                var generateScriptRequestBody =
                    new
                    {
                        FullVideoPrompt = fullVideoPrompt,
                        ImageBase64 = imageDescription,
                        SceneNumber = queueRequest.ConceptNumber,
                        GenerationJobId = queueRequest.GenerationJobId ?? queueRequest.JobId,
                        UserId = queueRequest.UserId,
                        ProjectId = queueRequest.ProjectId,
                        FunnelId = queueRequest.FunnelId,
                        LandingVariant = queueRequest.LandingVariant
                    };

                string generateScriptRequestJson =
                    System.Text.Json.JsonSerializer.Serialize(
                        generateScriptRequestBody);

                _logger.LogInformation(
                    "Sending script-generation request. " +
                    "JobId: {JobId}. SceneNumber: {SceneNumber}.",
                    queueRequest.JobId,
                    queueRequest.ConceptNumber);

                using var generateScriptRequest =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        generateScriptApiUrl);

                generateScriptRequest.Content =
                    new StringContent(
                        generateScriptRequestJson,
                        Encoding.UTF8,
                        "application/json");

                generateScriptRequest.Headers
                    .TryAddWithoutValidation(
                        "Origin",
                        hostUrl);

                using HttpResponseMessage generateScriptResponse =
                    await Http.SendAsync(
                        generateScriptRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                string generateScriptResponseBody =
                    await generateScriptResponse.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                _logger.LogInformation(
                    "Script-generation API returned {StatusCode}. " +
                    "Body: {ResponseBody}",
                    (int)generateScriptResponse.StatusCode,
                    generateScriptResponseBody);

                if (!generateScriptResponse.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Script-generation API returned " +
                        $"{(int)generateScriptResponse.StatusCode} " +
                        $"{generateScriptResponse.ReasonPhrase}. " +
                        $"Body: {generateScriptResponseBody}");
                }

                string generatedScript =
                    ReadStringResponse(
                        generateScriptResponseBody,
                        "The script-generation API returned an empty result.");

                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key =
                            new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.JobId
                                    }
                            },

                        UpdateExpression =
                            "SET Script = :script, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":script"] =
                                    new AttributeValue
                                    {
                                        S = generatedScript
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow
                                            .ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                /*
                 * ==========================================================
                 * STEP 3: SUBMIT RUNWARE VIDEO GENERATION
                 * ==========================================================
                 */

                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key =
                            new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.JobId
                                    }
                            },

                        UpdateExpression =
                            "SET CurrentStep = :currentStep, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":currentStep"] =
                                    new AttributeValue
                                    {
                                        S = "SubmittingVideo"
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow
                                            .ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                /*
                 * These property names match RunwareVideoRequest.
                 *
                 * Required:
                 *   frameImage
                 *   positivePrompt
                 *
                 * The remaining values are optional and can be omitted
                 * if you want GenerateRunwareVideo to use its defaults.
                 */
                var generateVideoRequestBody =
                    new
                    {
                        model =
                            string.IsNullOrWhiteSpace(queueRequest.Model)
                                ? "bytedance:2@2"
                                : queueRequest.Model,

                        inputs = new
                        {
                            frameImages = new[]
                            {
                queueRequest.ImageUrl
                            }
                        },

                        positivePrompt =
                            generatedScript,

                        width =
                            queueRequest.Width ?? 1248,

                        height =
                            queueRequest.Height ?? 704,

                        duration =
                            queueRequest.Duration ?? 5,

                        numberResults =
                            queueRequest.NumberResults ?? 1,

                        outputQuality =
                            queueRequest.OutputQuality ?? 95,

                        outputType =
                            queueRequest.OutputType ?? "URL",

                        outputFormat =
                            queueRequest.OutputFormat ?? "MP4",

                        deliveryMethod =
                            "async",

                        includeCost =
                            queueRequest.IncludeCost ?? true
                    };

                string generateVideoRequestJson =
                    System.Text.Json.JsonSerializer.Serialize(
                        generateVideoRequestBody);

                var videoUsageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage
                {
                    UserId = queueRequest.UserId, ProjectId = queueRequest.ProjectId, FunnelId = queueRequest.FunnelId,
                    GenerationJobId = queueRequest.GenerationJobId ?? queueRequest.JobId,
                    GenerationCategory = WebsiteFunnelPromptResolver.IsWebsite(queueRequest.LandingVariant) ? "WebsiteFunnel" : "SocialFunnel",
                    DeliverableType = WebsiteFunnelPromptResolver.IsWebsite(queueRequest.LandingVariant) ? "WebsitePreview" : "SocialVideo",
                    Feature = WebsiteFunnelPromptResolver.IsWebsite(queueRequest.LandingVariant) ? WebsiteFunnelPromptResolver.Feature : null,
                    UsageScope = WebsiteFunnelPromptResolver.IsWebsite(queueRequest.LandingVariant) ? WebsiteFunnelPromptResolver.UsageScope : null, Operation = "VideoGeneration",
                    Provider = "Runware", Model = generateVideoRequestBody.model, ProviderTaskId = queueRequest.JobId,
                    VideoSeconds = generateVideoRequestBody.duration
                });

                _logger.LogInformation(
                    "Sending GenerateRunwareVideo request. " +
                    "JobId: {JobId}. SceneNumber: {SceneNumber}.",
                    queueRequest.JobId,
                    queueRequest.ConceptNumber);

                using var generateVideoRequest =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        generateVideoApiUrl);

                generateVideoRequest.Content =
                    new StringContent(
                        generateVideoRequestJson,
                        Encoding.UTF8,
                        "application/json");

                generateVideoRequest.Headers
                    .TryAddWithoutValidation(
                        "Origin",
                        hostUrl);

                using HttpResponseMessage generateVideoResponse =
                    await Http.SendAsync(
                        generateVideoRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                string generateVideoResponseBody =
                    await generateVideoResponse.Content
                        .ReadAsStringAsync(
                            cancellationToken);

                _logger.LogInformation(
                    "GenerateRunwareVideo returned {StatusCode}. " +
                    "Body: {ResponseBody}",
                    (int)generateVideoResponse.StatusCode,
                    generateVideoResponseBody);

                if (!generateVideoResponse.IsSuccessStatusCode)
                {
                    await aiUsage.CompleteJobOperationFailureAsync(queueRequest.GenerationJobId ?? queueRequest.JobId, "VideoGeneration", "ProviderError");
                    throw new HttpRequestException(
                        $"GenerateRunwareVideo returned " +
                        $"{(int)generateVideoResponse.StatusCode} " +
                        $"{generateVideoResponse.ReasonPhrase}. " +
                        $"Body: {generateVideoResponseBody}");
                }

                /*
                 * Expected response:
                 *
                 * {
                 *   "taskUUID": "...",
                 *   "status": "submitted",
                 *   "deliveryMethod": "async",
                 *   "runware": ...
                 * }
                 */

                using JsonDocument videoResponseDocument =
    JsonDocument.Parse(generateVideoResponseBody);

                JsonElement videoResponseRoot =
                    videoResponseDocument.RootElement;

                string? runwareTaskUuid = null;
                string? deliveryMethod = null;
                bool submissionSucceeded = false;

                if (videoResponseRoot.TryGetProperty(
                        "success",
                        out JsonElement successElement) &&
                    successElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    submissionSucceeded = successElement.GetBoolean();
                }

                if (videoResponseRoot.TryGetProperty(
                        "taskUUID",
                        out JsonElement taskUuidElement))
                {
                    runwareTaskUuid = taskUuidElement.GetString();
                }

                if (videoResponseRoot.TryGetProperty(
                        "deliveryMethod",
                        out JsonElement deliveryMethodElement))
                {
                    deliveryMethod = deliveryMethodElement.GetString();
                }

                if (!submissionSucceeded)
                {
                    throw new InvalidOperationException(
                        "CallRunwareVideoAPI did not report a successful submission. " +
                        $"Response: {generateVideoResponseBody}");
                }

                if (string.IsNullOrWhiteSpace(runwareTaskUuid))
                {
                    throw new InvalidOperationException(
                        "CallRunwareVideoAPI did not return a taskUUID. " +
                        $"Response: {generateVideoResponseBody}");
                }

                await aiUsage.UpdateProviderTaskAsync(videoUsageId, runwareTaskUuid);

                string videoSubmissionStatus = "submitted";

                /*
                 * Runware accepted the task, but the video is not complete.
                 * Save the task UUID for polling or webhook processing.
                 */
                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key =
                            new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] =
                                    new AttributeValue
                                    {
                                        S = queueRequest.JobId
                                    }
                            },

                        UpdateExpression =
                            "SET #status = :status, " +
                            "CurrentStep = :currentStep, " +
                            "RunwareTaskUuid = :runwareTaskUuid, " +
                            "VideoSubmissionStatus = :videoSubmissionStatus, " +
                            "VideoDeliveryMethod = :videoDeliveryMethod, " +
                            "#error = :error, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeNames =
                            new Dictionary<string, string>
                            {
                                ["#status"] = "Status",
                                ["#error"] = "Error"
                            },

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":status"] =
                                    new AttributeValue
                                    {
                                        S = "Processing"
                                    },

                                [":currentStep"] =
                                    new AttributeValue
                                    {
                                        S = "VideoSubmitted"
                                    },

                                [":runwareTaskUuid"] =
                                    new AttributeValue
                                    {
                                        S = runwareTaskUuid
                                    },

                                [":videoSubmissionStatus"] =
                                    new AttributeValue
                                    {
                                        S = videoSubmissionStatus
                                            ?? "submitted"
                                    },

                                [":videoDeliveryMethod"] =
                                    new AttributeValue
                                    {
                                        S = deliveryMethod
                                            ?? "async"
                                    },

                                [":error"] =
                                    new AttributeValue
                                    {
                                        S = string.Empty
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow
                                            .ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                string storageConnectionString =
    Environment.GetEnvironmentVariable("AzureWebJobsStorage")
    ?? throw new InvalidOperationException(
        "AzureWebJobsStorage is missing.");

                var videoPollQueue = new QueueClient(
                    storageConnectionString,
                    VideoQueuePipeline.PollQueueName,
                    VideoQueuePipeline.CreateClientOptions());

                await videoPollQueue.CreateIfNotExistsAsync(
                    cancellationToken: cancellationToken);

                var pollMessage = new RunwareVideoPollMessage
                {
                    JobId = queueRequest.JobId,
                    TaskUUID = runwareTaskUuid,
                    Attempt = 0
                };

                string pollMessageJson = VideoQueuePipeline.Serialize(pollMessage);

                await videoPollQueue.SendMessageAsync(
                    messageText: pollMessageJson,
                    visibilityTimeout: TimeSpan.FromSeconds(20),
                    timeToLive: TimeSpan.FromHours(6),
                    cancellationToken: cancellationToken);

                _logger.LogInformation(
                    "[VIDEO QUEUE] poll scheduled jobId={JobId} attempt={Attempt}",
                    queueRequest.JobId,
                    0);

                _logger.LogInformation(
                    "Runware video task submitted successfully. " +
                    "JobId: {JobId}. TaskUUID: {TaskUUID}.",
                    queueRequest.JobId,
                    runwareTaskUuid);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "ProcessApiCall was cancelled.");

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ProcessApiCall failed. Body: {Body}",
                    body);

                /*
                 * Mark the DynamoDB job as failed when possible.
                 */
                if (dynamoDb is not null &&
                    !string.IsNullOrWhiteSpace(queueRequest?.JobId))
                {
                    try
                    {
                        string errorMessage =
                            ex.Message.Length > 1500
                                ? ex.Message[..1500]
                                : ex.Message;

                        await dynamoDb.UpdateItemAsync(
                            new UpdateItemRequest
                            {
                                TableName = tableName,

                                Key =
                                    new Dictionary<string, AttributeValue>
                                    {
                                        ["JobId"] =
                                            new AttributeValue
                                            {
                                                S = queueRequest.JobId
                                            }
                                    },

                                UpdateExpression =
                                    "SET #status = :status, " +
                                    "CurrentStep = :currentStep, " +
                                    "#error = :error, " +
                                    "UpdatedAtUtc = :updatedAtUtc",

                                ExpressionAttributeNames =
                                    new Dictionary<string, string>
                                    {
                                        ["#status"] = "Status",
                                        ["#error"] = "Error"
                                    },

                                ExpressionAttributeValues =
                                    new Dictionary<string, AttributeValue>
                                    {
                                        [":status"] =
                                            new AttributeValue
                                            {
                                                S = "Failed"
                                            },

                                        [":currentStep"] =
                                            new AttributeValue
                                            {
                                                S = "Failed"
                                            },

                                        [":error"] =
                                            new AttributeValue
                                            {
                                                S = errorMessage
                                            },

                                        [":updatedAtUtc"] =
                                            new AttributeValue
                                            {
                                                S = DateTimeOffset.UtcNow
                                                    .ToString("O")
                                            }
                                    },

                                ConditionExpression =
                                    "attribute_exists(JobId)"
                            },
                            CancellationToken.None);
                    }
                    catch (Exception updateException)
                    {
                        _logger.LogError(
                            updateException,
                            "Unable to mark job {JobId} as failed.",
                            queueRequest.JobId);
                    }
                }

                /*
                 * Re-throw so Azure Storage Queue retries the message.
                 */
                throw;
            }
        }
        public sealed class RunwareVideoPollMessage
        {
            public string JobId { get; set; } = string.Empty;
            public string TaskUUID { get; set; } = string.Empty;
            public int Attempt { get; set; }
        }

        [Function("PollRunwareVideo")]
        public async Task PollRunwareVideo(
    [QueueTrigger(
        VideoQueuePipeline.PollQueueName,
        Connection = "AzureWebJobsStorage")]
    string body,
    CancellationToken cancellationToken)
        {
            RunwareVideoPollMessage? pollMessage = null;
            AmazonDynamoDBClient? dynamoDb = null;

            string tableName =
                Environment.GetEnvironmentVariable("DynamoDbTableName")
                ?? "DesignModeJobs";

            try
            {
                pollMessage = VideoQueuePipeline.Deserialize<RunwareVideoPollMessage>(body);

                if (pollMessage is null ||
                    string.IsNullOrWhiteSpace(pollMessage.JobId) ||
                    string.IsNullOrWhiteSpace(pollMessage.TaskUUID))
                {
                    throw new InvalidOperationException(
                        "The Runware polling message is invalid.");
                }

                string apiUrl =
                    Environment.GetEnvironmentVariable("RUNWARE_API_URL")
                    ?? throw new InvalidOperationException(
                        "RUNWARE_API_URL is missing.");

                string apiKey =
                    Environment.GetEnvironmentVariable("RUNWARE_API_KEY")
                    ?? throw new InvalidOperationException(
                        "RUNWARE_API_KEY is missing.");

                dynamoDb = CreateDynamoDbClient();

                /*
                 * Mark the current polling state.
                 */
                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["JobId"] = new AttributeValue
                            {
                                S = pollMessage.JobId
                            }
                        },

                        UpdateExpression =
                            "SET CurrentStep = :currentStep, " +
                            "VideoPollAttempt = :attempt, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":currentStep"] =
                                    new AttributeValue
                                    {
                                        S = "CheckingVideoStatus"
                                    },

                                [":attempt"] =
                                    new AttributeValue
                                    {
                                        N = pollMessage.Attempt.ToString()
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow.ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                /*
                 * Runware recommends getResponse for polling async inference.
                 */
                var runwareRequest = new object[]
                {
            new
            {
                taskType = "getResponse",
                taskUUID = pollMessage.TaskUUID
            }
                };

                string requestJson =
                    System.Text.Json.JsonSerializer.Serialize(runwareRequest);

                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Post,
                        apiUrl);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        apiKey);

                request.Content =
                    new StringContent(
                        requestJson,
                        Encoding.UTF8,
                        "application/json");

                using HttpResponseMessage response =
                    await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                string responseBody =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                _logger.LogInformation(
                    "Runware polling returned {StatusCode}. " +
                    "JobId: {JobId}. TaskUUID: {TaskUUID}. Body: {Body}",
                    (int)response.StatusCode,
                    pollMessage.JobId,
                    pollMessage.TaskUUID,
                    responseBody);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Runware polling returned " +
                        $"{(int)response.StatusCode} " +
                        $"{response.ReasonPhrase}. " +
                        $"Body: {responseBody}");
                }

                using JsonDocument document =
                    JsonDocument.Parse(responseBody);

                JsonElement root = document.RootElement;

                string? videoUrl = FindStringProperty(
                    root,
                    "videoURL",
                    "videoUrl");

                string? status = FindStringProperty(
                    root,
                    "status",
                    "taskStatus");

                string? errorMessage = FindStringProperty(
                    root,
                    "error",
                    "errorMessage",
                    "message");

                /*
                 * ==========================================================
                 * VIDEO COMPLETE
                 * ==========================================================
                 */

                if (!string.IsNullOrWhiteSpace(videoUrl))
                {
                    decimal? videoCost = FindDecimalProperty(root, "cost");
                    await aiUsage.CompleteJobOperationSuccessAsync(pollMessage.JobId, "VideoGeneration", videoCost);
                    await dynamoDb.UpdateItemAsync(
                        new UpdateItemRequest
                        {
                            TableName = tableName,

                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["JobId"] = new AttributeValue
                                {
                                    S = pollMessage.JobId
                                }
                            },

                            UpdateExpression =
                                "SET #status = :status, " +
                                "CurrentStep = :currentStep, " +
                                "VideoUrl = :videoUrl, " +
                                "VideoSubmissionStatus = :videoStatus, " +
                                "#error = :error, " +
                                "CompletedAtUtc = :completedAtUtc, " +
                                "UpdatedAtUtc = :updatedAtUtc",

                            ExpressionAttributeNames =
                                new Dictionary<string, string>
                                {
                                    ["#status"] = "Status",
                                    ["#error"] = "Error"
                                },

                            ExpressionAttributeValues =
                                new Dictionary<string, AttributeValue>
                                {
                                    [":status"] =
                                        new AttributeValue
                                        {
                                            S = "Completed"
                                        },

                                    [":currentStep"] =
                                        new AttributeValue
                                        {
                                            S = "Completed"
                                        },

                                    [":videoUrl"] =
                                        new AttributeValue
                                        {
                                            S = videoUrl
                                        },

                                    [":videoStatus"] =
                                        new AttributeValue
                                        {
                                            S = status ?? "completed"
                                        },

                                    [":error"] =
                                        new AttributeValue
                                        {
                                            S = string.Empty
                                        },

                                    [":completedAtUtc"] =
                                        new AttributeValue
                                        {
                                            S = DateTimeOffset.UtcNow.ToString("O")
                                        },

                                    [":updatedAtUtc"] =
                                        new AttributeValue
                                        {
                                            S = DateTimeOffset.UtcNow.ToString("O")
                                        }
                                },

                            ConditionExpression =
                                "attribute_exists(JobId)"
                        },
                        cancellationToken);

                    if (funnelRecovery is not null)
                        await funnelRecovery.PublishSafelyAsync(pollMessage.JobId);

                    _logger.LogInformation(
                        "[VIDEO QUEUE] video completed; final result persisted. " +
                        "JobId: {JobId}. TaskUUID: {TaskUUID}. VideoUrl: {VideoUrl}",
                        pollMessage.JobId,
                        pollMessage.TaskUUID,
                        videoUrl);

                    return;
                }

                /*
                 * ==========================================================
                 * TERMINAL FAILURE
                 * ==========================================================
                 */

                if (IsTerminalFailure(status) ||
                    !string.IsNullOrWhiteSpace(errorMessage))
                {
                    await aiUsage.CompleteJobOperationFailureAsync(pollMessage.JobId, "VideoGeneration", "ProviderError");
                    await MarkVideoJobFailedAsync(
                        dynamoDb,
                        tableName,
                        pollMessage.JobId,
                        errorMessage ??
                        $"Runware task ended with status '{status}'.",
                        cancellationToken);

                    return;
                }

                /*
                 * ==========================================================
                 * STILL PROCESSING — SCHEDULE ANOTHER CHECK
                 * ==========================================================
                 */

                const int maximumAttempts = 90;

                if (pollMessage.Attempt >= maximumAttempts)
                {
                    await MarkVideoJobFailedAsync(
                        dynamoDb,
                        tableName,
                        pollMessage.JobId,
                        "Video generation did not complete before the polling limit.",
                        cancellationToken);

                    return;
                }

                await dynamoDb.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = tableName,

                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["JobId"] = new AttributeValue
                            {
                                S = pollMessage.JobId
                            }
                        },

                        UpdateExpression =
                            "SET #status = :status, " +
                            "CurrentStep = :currentStep, " +
                            "VideoSubmissionStatus = :videoStatus, " +
                            "UpdatedAtUtc = :updatedAtUtc",

                        ExpressionAttributeNames =
                            new Dictionary<string, string>
                            {
                                ["#status"] = "Status"
                            },

                        ExpressionAttributeValues =
                            new Dictionary<string, AttributeValue>
                            {
                                [":status"] =
                                    new AttributeValue
                                    {
                                        S = "Processing"
                                    },

                                [":currentStep"] =
                                    new AttributeValue
                                    {
                                        S = "WaitingForVideo"
                                    },

                                [":videoStatus"] =
                                    new AttributeValue
                                    {
                                        S = status ?? "processing"
                                    },

                                [":updatedAtUtc"] =
                                    new AttributeValue
                                    {
                                        S = DateTimeOffset.UtcNow.ToString("O")
                                    }
                            },

                        ConditionExpression =
                            "attribute_exists(JobId)"
                    },
                    cancellationToken);

                await EnqueueNextVideoPollAsync(
                    pollMessage.JobId,
                    pollMessage.TaskUUID,
                    pollMessage.Attempt + 1,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "PollRunwareVideo failed. Body: {Body}",
                    body);

                /*
                 * Throwing causes the Azure Queue trigger's normal retry mechanism.
                 * Terminal task errors above are handled without throwing.
                 */
                throw;
            }
            finally
            {
                dynamoDb?.Dispose();
            }
        }
        private static async Task EnqueueNextVideoPollAsync(
    string jobId,
    string taskUuid,
    int attempt,
    CancellationToken cancellationToken)
        {
            string storageConnectionString =
                Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                ?? throw new InvalidOperationException(
                    "AzureWebJobsStorage is missing.");

            var queue = new QueueClient(
                storageConnectionString,
                VideoQueuePipeline.PollQueueName,
                VideoQueuePipeline.CreateClientOptions());

            await queue.CreateIfNotExistsAsync(
                cancellationToken: cancellationToken);

            var nextMessage = new RunwareVideoPollMessage
            {
                JobId = jobId,
                TaskUUID = taskUuid,
                Attempt = attempt
            };

            string json = VideoQueuePipeline.Serialize(nextMessage);

            /*
             * Moderate backoff:
             * attempts 1–10: 15 seconds
             * attempts 11–30: 30 seconds
             * attempts 31+: 60 seconds
             */
            TimeSpan delay =
                attempt <= 10
                    ? TimeSpan.FromSeconds(15)
                    : attempt <= 30
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromMinutes(1);

            await queue.SendMessageAsync(
                messageText: json,
                visibilityTimeout: delay,
                timeToLive: TimeSpan.FromHours(6),
                cancellationToken: cancellationToken);
        }
        private static string? FindStringProperty(
    JsonElement element,
    params string[] propertyNames)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (propertyNames.Any(
                            name => string.Equals(
                                property.Name,
                                name,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            return property.Value.GetString();
                        }

                        if (property.Value.ValueKind is
                            JsonValueKind.Number or
                            JsonValueKind.True or
                            JsonValueKind.False)
                        {
                            return property.Value.ToString();
                        }
                    }

                    string? nestedResult =
                        FindStringProperty(
                            property.Value,
                            propertyNames);

                    if (!string.IsNullOrWhiteSpace(nestedResult))
                    {
                        return nestedResult;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nestedResult =
                        FindStringProperty(
                            item,
                            propertyNames);

                    if (!string.IsNullOrWhiteSpace(nestedResult))
                    {
                        return nestedResult;
                    }
                }
            }

            return null;
        }
        private static decimal? FindDecimalProperty(JsonElement element, params string[] propertyNames)
        {
            var value = FindStringProperty(element, propertyNames);
            return decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        private static bool IsTerminalFailure(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            return status.Equals(
                       "failed",
                       StringComparison.OrdinalIgnoreCase) ||
                   status.Equals(
                       "error",
                       StringComparison.OrdinalIgnoreCase) ||
                   status.Equals(
                       "cancelled",
                       StringComparison.OrdinalIgnoreCase) ||
                   status.Equals(
                       "canceled",
                       StringComparison.OrdinalIgnoreCase);
        }
        private static async Task MarkVideoJobFailedAsync(
    AmazonDynamoDBClient dynamoDb,
    string tableName,
    string jobId,
    string error,
    CancellationToken cancellationToken)
        {
            string safeError =
                error.Length > 1500
                    ? error[..1500]
                    : error;

            await dynamoDb.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = tableName,

                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["JobId"] = new AttributeValue
                        {
                            S = jobId
                        }
                    },

                    UpdateExpression =
                        "SET #status = :status, " +
                        "CurrentStep = :currentStep, " +
                        "VideoSubmissionStatus = :videoStatus, " +
                        "#error = :error, " +
                        "UpdatedAtUtc = :updatedAtUtc",

                    ExpressionAttributeNames =
                        new Dictionary<string, string>
                        {
                            ["#status"] = "Status",
                            ["#error"] = "Error"
                        },

                    ExpressionAttributeValues =
                        new Dictionary<string, AttributeValue>
                        {
                            [":status"] =
                                new AttributeValue
                                {
                                    S = "Failed"
                                },

                            [":currentStep"] =
                                new AttributeValue
                                {
                                    S = "VideoFailed"
                                },

                            [":videoStatus"] =
                                new AttributeValue
                                {
                                    S = "failed"
                                },

                            [":error"] =
                                new AttributeValue
                                {
                                    S = safeError
                                },

                            [":updatedAtUtc"] =
                                new AttributeValue
                                {
                                    S = DateTimeOffset.UtcNow.ToString("O")
                                }
                        },

                    ConditionExpression =
                        "attribute_exists(JobId)"
                },
                cancellationToken);
        }
        private static AmazonDynamoDBClient CreateDynamoDbClient()
        {
            return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
        }
        private static string ReadStringResponse(
    string responseBody,
    string emptyResultError)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                throw new InvalidOperationException(
                    emptyResultError);
            }

            string? result;

            try
            {
                result =
                    System.Text.Json.JsonSerializer.Deserialize<string>(
                        responseBody);
            }
            catch (System.Text.Json.JsonException)
            {
                result =
                    responseBody
                        .Trim()
                        .Trim('"');
            }

            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException(
                    emptyResultError);
            }

            return result;
        }


        [Function("GetJobStatus")]
        public async Task<IActionResult> GetJobStatus(
            [HttpTrigger(
        AuthorizationLevel.Function,
        "get",
        Route = "GetJobStatus/{jobId}")]
    HttpRequest req,
            string jobId,
            CancellationToken cancellationToken)
        {
            try
            {
                string? origin =
                    req.Headers["Origin"].FirstOrDefault();

                string? hostUrl =
                    Environment.GetEnvironmentVariable(
                        "HostUrl");

                if (string.IsNullOrWhiteSpace(origin) ||
                    string.IsNullOrWhiteSpace(hostUrl) ||
                    !string.Equals(
                        origin,
                        hostUrl,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new UnauthorizedResult();
                }

                if (string.IsNullOrWhiteSpace(jobId))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "JobId is required."
                        });
                }

                string tableName =
                    Environment.GetEnvironmentVariable(
                        "DynamoDbTableName")
                    ?? "DesignModeJobs";

                var dynamoDb = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                using (dynamoDb)
                {
                    GetItemResponse result =
                        await dynamoDb.GetItemAsync(
                            new GetItemRequest
                            {
                                TableName = tableName,

                                Key =
                                    new Dictionary<string, AttributeValue>
                                    {
                                        ["JobId"] =
                                            new AttributeValue
                                            {
                                                S = jobId
                                            }
                                    },

                                ConsistentRead = true
                            },
                            cancellationToken);

                    if (result.Item is null ||
                        result.Item.Count == 0)
                    {
                        return new NotFoundObjectResult(
                            new
                            {
                                error = "Job not found."
                            });
                    }

                    Dictionary<string, AttributeValue> item =
                        result.Item;

                    return new OkObjectResult(
                        new
                        {
                            jobId =
                                item.TryGetValue(
                                    "JobId",
                                    out AttributeValue? jobIdValue)
                                        ? jobIdValue.S
                                        : null,

                            status =
                                item.TryGetValue(
                                    "Status",
                                    out AttributeValue? statusValue)
                                        ? statusValue.S
                                        : null,

                            currentStep =
                                item.TryGetValue(
                                    "CurrentStep",
                                    out AttributeValue? currentStepValue)
                                        ? currentStepValue.S
                                        : null,

                            name =
                                item.TryGetValue(
                                    "Name",
                                    out AttributeValue? nameValue)
                                        ? nameValue.S
                                        : null,

                            industry =
                                item.TryGetValue(
                                    "Industry",
                                    out AttributeValue? industryValue)
                                        ? industryValue.S
                                        : null,

                            conceptNumber =
                                item.TryGetValue(
                                    "ConceptNumber",
                                    out AttributeValue? conceptNumberValue) &&
                                int.TryParse(
                                    conceptNumberValue.N,
                                    out int parsedConceptNumber)
                                        ? parsedConceptNumber
                                        : 0,

                            imageUrl =
                                item.TryGetValue(
                                    "ImageUrl",
                                    out AttributeValue? imageUrlValue)
                                        ? imageUrlValue.S
                                        : null,

                            imageDescription =
                                item.TryGetValue(
                                    "ImageDescription",
                                    out AttributeValue? descriptionValue)
                                        ? descriptionValue.S
                                        : null,

                            script =
                                item.TryGetValue(
                                    "Script",
                                    out AttributeValue? scriptValue)
                                        ? scriptValue.S
                                        : null,

                            videoUrl =
                                item.TryGetValue(
                                    "VideoUrl",
                                    out AttributeValue? videoUrlValue)
                                        ? videoUrlValue.S
                                        : null,

                            error =
                                item.TryGetValue(
                                    "Error",
                                    out AttributeValue? errorValue)
                                        ? errorValue.S
                                        : null,

                            createdAtUtc =
                                item.TryGetValue(
                                    "CreatedAtUtc",
                                    out AttributeValue? createdAtValue)
                                        ? createdAtValue.S
                                        : null,

                            updatedAtUtc =
                                item.TryGetValue(
                                    "UpdatedAtUtc",
                                    out AttributeValue? updatedAtValue)
                                        ? updatedAtValue.S
                                        : null
                        });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unable to retrieve job {JobId}.",
                    jobId);

                return new ObjectResult(
                    new
                    {
                        error = "Unable to retrieve the job."
                    })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }

        [Function("Batch")]
        public async Task<IActionResult> Batch([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                // Get the Origin header
                string referer = req.Headers["Origin"].FirstOrDefault();

                // Check if the Origin header exists and if it contains the allowed host URL
                if (referer != null)
                {
                    if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                    {
                        return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                    }
                }
                else
                {
                    return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
                }

                // Read the request body
                //string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                //// Deserialize the JSON string into a RequestBody object
                //var data = JsonConvert.DeserializeObject<RequestBody>(requestBody);
                ////var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(requestBody);

                //// Check if the ID is missing or invalid in the request body
                //if (data == null || (data.FunctionGUID == ""))
                //{
                //    return new BadRequestObjectResult("Missing or invalid ID in request body.");
                //}

                //var id = Utilities.DecryptXorString(data.FunctionGUID);
                //var connectionString = Environment.GetEnvironmentVariable("DesignModeAIConnectionString");

                //using var connection = new SqlConnection(connectionString);
                //await connection.OpenAsync();

                //var cmd = new SqlCommand("SELECT FunctionName FROM FunctionLookup WHERE FunctionGUID = @Id", connection);
                //cmd.Parameters.AddWithValue("@Id", id);

                //var functionName = (string)await cmd.ExecuteScalarAsync();

                //if (string.IsNullOrEmpty(functionName))
                //{
                //    return new NotFoundObjectResult($"No function found for ID: {id}");
                //}

                //HttpClient client = new HttpClient();
                //string functionUrl = $"{Environment.GetEnvironmentVariable("FunctionUrl")}api/{functionName}?code=REDACTED_FOR_GROOMING";

                //var response = await client.GetFromJsonAsync<Response<TestModel>>(functionUrl);
                var response = new Response<TestModel>();

                // Use reflection to find the method in the current class
                //var methodInfo = typeof(DesignModeFunctions)
                //    .GetMethod(functionName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

                //if (methodInfo == null)
                //{
                //    return new NotFoundObjectResult($"Function '{functionName}' does not exist.");
                //}

                //// Check if method matches the expected signature
                //var parameters = methodInfo.GetParameters();
                //if (parameters.Length != 1 || parameters[0].ParameterType != typeof(HttpRequest))
                //{
                //    return new BadRequestObjectResult($"Function '{functionName}' has an unsupported signature.");
                //}

                //// Call the method dynamically
                //var resultTask = (Task<IActionResult>)methodInfo.Invoke(this, new object[] { req });
                //return await resultTask;
                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in Batch function: {ex.Message}");
                return new ObjectResult($"Internal Server Error: {ex.Message}") { StatusCode = 500 };
            }
        }

        [Function("Function1")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            var response = new Response<TestModel>();

            try
            {
                // Determine if local DynamoDB should be used
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // PartiQL query
                var request = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM testtable WHERE id = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { N = "10" }
                    }
                };

                var result = await client.ExecuteStatementAsync(request);

                if (result.Items.Count > 0)
                {
                    var item = result.Items.First();
                    var testModel = new TestModel
                    {
                        TestText = item.ContainsKey("testcolumn") ? item["testcolumn"].S : null
                    };

                    response.Item = testModel;
                    return new OkObjectResult(response);
                }
                else
                {
                    return new NotFoundObjectResult("No item found with id = 10.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DynamoDB query failed.");
                return new StatusCodeResult(500);
            }

            //var response = new Response<TestModel>();
            //var test = await this.testStringBac.TestStringResponse();
            //response = test;
            //_logger.LogInformation("C# HTTP trigger function processed a request.");
            //return new OkObjectResult(test);
        }

        [Function("Function2")]
        public async Task<IActionResult> Function2([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            var response = new Response<TestModel>();

            try
            {
                // Determine if local DynamoDB should be used
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // PartiQL query
                var request = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM testtable WHERE id = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { N = "10" }
                    }
                };

                var result = await client.ExecuteStatementAsync(request);

                if (result.Items.Count > 0)
                {
                    var item = result.Items.First();
                    var testModel = new TestModel
                    {
                        TestText = item.ContainsKey("testcolumn") ? item["testcolumn"].S : null
                    };

                    response.Item = testModel;
                    return new OkObjectResult(response);
                }
                else
                {
                    return new NotFoundObjectResult("No item found with id = 10.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DynamoDB query failed.");
                return new StatusCodeResult(500);
            }

            //var response = new Response<TestModel>();
            //var test = await this.testStringBac.TestStringResponse();
            //response = test;
            //_logger.LogInformation("C# HTTP trigger function processed a request.");
            //return new OkObjectResult(test);
        }


        [Function("Login")]
        public async Task<IActionResult> Login([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            var response = new Response<TokenResponse>();
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var loginData = System.Text.Json.JsonSerializer.Deserialize<DesignMode.AI.Shared.Models.LoginRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var user = await canonicalUserStore.FindByEmailAsync(loginData.Username, includePassword: true);
                if (user is null)
                {
                    return new UnauthorizedResult(); // Username not found
                }

                // 2. Retrieve the stored password hash and salt
                var storedHash = user.PasswordHash;
                var storedSalt = user.Salt;

                if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
                {
                    return new UnauthorizedResult(); // Invalid stored data
                }

                // 3. Hash the input password with the stored salt
                var hashedInputPassword = Utilities.HashPassword(loginData.Password, storedSalt);

                // 4. Compare the hashed passwords
                if (hashedInputPassword != storedHash)
                {
                    return new UnauthorizedResult(); // Invalid password
                }

                // 5. Generate JWT token if authentication is successful
                var jwtToken = Utilities.GenerateJwt(user.Username, user.Id);
                var tokenResponse = new TokenResponse { Token = jwtToken };
                response.Item = tokenResponse;

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("ForgotPassword")]
        public async Task<IActionResult> ForgotPassword(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
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

            var response = new Response<ForgotPasswordResponse>();
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var forgotRequest = System.Text.Json.JsonSerializer.Deserialize<ForgotPasswordRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                // Initialize DynamoDB client
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // 1. Check if user exists
                var checkUserRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, Username FROM Users WHERE Username = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = forgotRequest.Username }
            }
                };

                var userResult = await client.ExecuteStatementAsync(checkUserRequest);

                // IMPORTANT: Do not reveal if user exists (security best practice)
                if (userResult.Items.Any())
                {
                    var user = userResult.Items.First();
                    var userId = user["Id"].S;

                    // 2. Generate reset token
                    var resetToken = Guid.NewGuid().ToString();
                    var expiration = DateTime.UtcNow.AddHours(1);

                    // 3. Store reset token in DynamoDB (you need a table or fields for this)
                    var updateRequest = new ExecuteStatementRequest
                    {
                        Statement = @"
                UPDATE Users 
                SET ResetToken = ?, ResetTokenExpiry = ?
                WHERE Id = ?",
                        Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = resetToken },
                new AttributeValue { S = expiration.ToString("o") },
                new AttributeValue { S = userId }
            }
                    };

                    await client.ExecuteStatementAsync(updateRequest);

                    // 4. TODO: send email (SendGrid, SES, etc.)
                    await SendResetEmailAsync(forgotRequest.Username, resetToken);
                    return new OkObjectResult(new Response<ForgotPasswordResponse>
                    {
                        Item = new ForgotPasswordResponse
                        {
                            Success = true,
                            Message = "If the account exists, a reset link has been sent."
                        }
                    });
                }
                else
                {
                    return new NotFoundObjectResult(new Response<ForgotPasswordResponse>
                    {
                        Item = new ForgotPasswordResponse
                        {
                            Success = false,
                            Message = "No account found with this email."
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Forgot password failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetUserIdByUsername")]
        public async Task<IActionResult> GetUserIdByUsername(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = "GetUserIdByUsername")]
        HttpRequest req)
        {
            // ------------------------------------------------------------
            // 1. Validate Origin
            // ------------------------------------------------------------
            var origin =
                req.Headers["Origin"].FirstOrDefault();

            var hostUrl =
                Environment.GetEnvironmentVariable("HostUrl");

            _logger.LogInformation(
                "GetUserIdByUsername called. Origin: {Origin}",
                origin);

            if (string.IsNullOrWhiteSpace(origin) ||
                string.IsNullOrWhiteSpace(hostUrl) ||
                !string.Equals(
                    origin.TrimEnd('/'),
                    hostUrl.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Unauthorized origin. Origin: {Origin}, Expected: {HostUrl}",
                    origin,
                    hostUrl);

                return new UnauthorizedObjectResult(new
                {
                    success = false,
                    message = "Unauthorized origin."
                });
            }

            // ------------------------------------------------------------
            // 2. Read request body
            // ------------------------------------------------------------
            GetUserIdRequest? request;

            try
            {
                var requestBody =
                    await new StreamReader(req.Body)
                        .ReadToEndAsync();

                request =
                    System.Text.Json.JsonSerializer.Deserialize<GetUserIdRequest>(
                        requestBody,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid JSON supplied to GetUserIdByUsername.");

                return new BadRequestObjectResult(new
                {
                    success = false,
                    message = "Invalid request body."
                });
            }

            if (request == null ||
                string.IsNullOrWhiteSpace(request.Username))
            {
                return new BadRequestObjectResult(new
                {
                    success = false,
                    message = "Username is required."
                });
            }

            // Do not change case here because DynamoDB string
            // comparisons are case-sensitive.
            var username = request.Username.Trim();

            _logger.LogInformation(
                "Looking up user with Username: '{Username}'",
                username);

            try
            {
                // --------------------------------------------------------
                // 3. Create DynamoDB client
                // --------------------------------------------------------
                using var client = CreateDynamoDbClient();

                // --------------------------------------------------------
                // 4. Query Users table
                // --------------------------------------------------------
                var queryRequest =
                    new ExecuteStatementRequest
                    {
                        Statement =
                            "SELECT \"Id\", \"Username\" " +
                            "FROM \"Users\" " +
                            "WHERE \"Username\" = ?",

                        Parameters =
                            new List<AttributeValue>
                            {
                            new AttributeValue
                            {
                                S = username
                            }
                            }
                    };

                _logger.LogInformation(
                    "Executing DynamoDB lookup for '{Username}'",
                    username);

                var result =
                    await client.ExecuteStatementAsync(
                        queryRequest);

                var itemCount =
                    result.Items?.Count ?? 0;

                _logger.LogInformation(
                    "DynamoDB returned {ItemCount} item(s) " +
                    "for username '{Username}'.",
                    itemCount,
                    username);

                // --------------------------------------------------------
                // 5. User not found
                // --------------------------------------------------------
                if (result.Items == null ||
                    result.Items.Count == 0)
                {
                    _logger.LogWarning(
                        "No user found in Users table " +
                        "with Username '{Username}'.",
                        username);

                    return new NotFoundObjectResult(new
                    {
                        success = false,
                        message = "User not found.",
                        username
                    });
                }

                // --------------------------------------------------------
                // 6. Extract first matching user
                // --------------------------------------------------------
                var user = result.Items.First();

                if (!user.TryGetValue(
                        "Id",
                        out var idAttribute) ||
                    string.IsNullOrWhiteSpace(idAttribute?.S))
                {
                    _logger.LogError(
                        "User '{Username}' was found, " +
                        "but the Id attribute was missing.",
                        username);

                    return new ObjectResult(new
                    {
                        success = false,
                        message =
                            "User record does not contain an Id."
                    })
                    {
                        StatusCode =
                            StatusCodes.Status500InternalServerError
                    };
                }

                var userId = idAttribute.S;

                _logger.LogInformation(
                    "Successfully resolved username " +
                    "'{Username}' to UserId '{UserId}'.",
                    username,
                    userId);

                // --------------------------------------------------------
                // 7. Return UserId
                // --------------------------------------------------------
                return new OkObjectResult(
                    new GetUserIdResponse
                    {
                        Success = true,
                        UserId = userId,
                        Username = username
                    });
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(
                    ex,
                    "DynamoDB error while looking up " +
                    "username '{Username}'.",
                    username);

                return new ObjectResult(new
                {
                    success = false,
                    message = "Database lookup failed."
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "GetUserIdByUsername failed for " +
                    "username '{Username}'.",
                    username);

                return new ObjectResult(new
                {
                    success = false,
                    message = "An unexpected error occurred."
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }

        public class GetUserIdRequest
        {
            public string Username { get; set; } = string.Empty;
        }

        public class GetUserIdResponse
        {
            public bool Success { get; set; }

            public string? UserId { get; set; }

            public string? Message { get; set; }

            public string? Username { get; set; }
        }

        private async Task SendResetEmailAsync(string toEmail, string resetToken)
        {
            try
            {
                var region = RegionEndpoint.GetBySystemName(
                    Environment.GetEnvironmentVariable("AWS_REGION"));

                var sesClient = new AmazonSimpleEmailServiceClient(
                    Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
                    Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
                    region
                );

                var frontendUrl = Environment.GetEnvironmentVariable("HostUrl");
                var resetLink = $"{frontendUrl}/resetpassword?token=REDACTED_FOR_GROOMING";

                var subject = "Password Reset Request";

                var bodyHtml = $@"
        <html>
        <body>
            <h2>Password Reset</h2>
            <p>You requested a password reset.</p>
            <p>Click the link below to reset your password:</p>
            <a href='{resetLink}'>Reset Password</a>
            <p>This link expires in 1 hour.</p>
        </body>
        </html>";
                var copyEmail = Environment.GetEnvironmentVariable("COPY_EMAIL");
                var request = new SendEmailRequest
                {
                    Source = Environment.GetEnvironmentVariable("SES_FROM_EMAIL"),
                    Destination = new Amazon.SimpleEmail.Model.Destination
                    {
                        ToAddresses = new List<string> { toEmail },
                        BccAddresses = new List<string> { copyEmail }
                    },
                    Message = new Amazon.SimpleEmail.Model.Message
                    {
                        Subject = new Amazon.SimpleEmail.Model.Content(subject),
                        Body = new Body
                        {
                            Html = new Amazon.SimpleEmail.Model.Content(bodyHtml)
                        }
                    }
                };

                var result = await sesClient.SendEmailAsync(request);

                _logger.LogInformation("Password reset email sent to {Email}. MessageId: {MessageId}",
                    toEmail, result.MessageId);
            }
            catch (AmazonSimpleEmailServiceException sesEx)
            {
                _logger.LogError(sesEx,
                    "SES failed sending reset email to {Email}. ErrorCode: {Code}",
                    toEmail, sesEx.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error sending reset email to {Email}",
                    toEmail);
            }
        }

        [Function("ValidateResetToken")]
        public async Task<IActionResult> ValidateResetToken(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var validateRequest = System.Text.Json.JsonSerializer.Deserialize<ValidateResetTokenRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                // Initialize DynamoDB client
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // 1. Find user by reset token
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, ResetToken, ResetTokenExpiry FROM Users WHERE ResetToken = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = validateRequest.Token }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                if (!result.Items.Any())
                {
                    return new OkObjectResult(new Response<ValidateResetTokenResponse>
                    {
                        Item = new ValidateResetTokenResponse
                        {
                            Success = false,
                            Message = "Invalid or expired token."
                        }
                    });
                }

                var user = result.Items.First();

                // 2. Validate expiry
                var expiryString = user.ContainsKey("ResetTokenExpiry")
                    ? user["ResetTokenExpiry"].S
                    : null;

                if (expiryString == null || !DateTime.TryParse(expiryString, out var expiry))
                {
                    return new OkObjectResult(new Response<ValidateResetTokenResponse>
                    {
                        Item = new ValidateResetTokenResponse
                        {
                            Success = false,
                            Message = "Invalid or expired token."
                        }
                    });
                }

                if (expiry < DateTime.UtcNow)
                {
                    return new OkObjectResult(new Response<ValidateResetTokenResponse>
                    {
                        Item = new ValidateResetTokenResponse
                        {
                            Success = false,
                            Message = "Token has expired."
                        }
                    });
                }

                // 3. Valid token
                return new OkObjectResult(new Response<ValidateResetTokenResponse>
                {
                    Item = new ValidateResetTokenResponse
                    {
                        Success = true,
                        Message = "Token is valid."
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validate reset token failed.");
                return new StatusCodeResult(500);
            }
        }

        public class ValidateResetTokenRequest
        {
            public string Token { get; set; }
        }

        public class ValidateResetTokenResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; }
        }

        public class ForgotPasswordRequest
        {
            public string Username { get; set; }
        }

        public class ForgotPasswordResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; }
        }

        [Function("UpdateOnboarded")]
        public async Task<IActionResult> UpdateOnboarded(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var data = System.Text.Json.JsonSerializer.Deserialize<UpdateOnboardedRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // Basic validation
            if (data == null || string.IsNullOrWhiteSpace(data.UserId))
            {
                return new BadRequestObjectResult(new { Message = "UserId is required." });
            }

            try
            {
                // Initialize DynamoDB client
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Update Onboarded field
                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                UPDATE Users 
                SET Onboarded = ?
                WHERE Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = data.Onboarded },
                new AttributeValue { S = data.UserId }
            }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    Message = "User onboarding status updated successfully"
                });
            }
            catch (AmazonServiceException ex)
            {
                _logger.LogError(ex,
                    "AWS Error. ErrorCode={ErrorCode}, StatusCode={StatusCode}, Message={Message}",
                    ex.ErrorCode,
                    ex.StatusCode,
                    ex.Message);

                return new ObjectResult(new
                {
                    ErrorCode = ex.ErrorCode,
                    Message = ex.Message,
                    StatusCode = ex.StatusCode
                })
                {
                    StatusCode = 500
                };
            }
        }

        public class UpdateOnboardedRequest
        {
            public string UserId { get; set; }
            public string Onboarded { get; set; }
        }

        [Function("ResetPassword")]
        public async Task<IActionResult> ResetPassword(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var resetData = System.Text.Json.JsonSerializer.Deserialize<ResetPasswordRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // Basic validation
            if (resetData == null ||
                string.IsNullOrWhiteSpace(resetData.Token) ||
                string.IsNullOrWhiteSpace(resetData.NewPassword))
            {
                return new BadRequestObjectResult(new { Message = "Token and password are required." });
            }

            try
            {
                // Initialize DynamoDB client
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // 1. Find user by reset token
                var getUserRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, ResetTokenExpiry FROM Users WHERE ResetToken = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = resetData.Token }
            }
                };

                var userResult = await client.ExecuteStatementAsync(getUserRequest);

                if (!userResult.Items.Any())
                {
                    return new BadRequestObjectResult(new { Message = "Invalid or expired token." });
                }

                var user = userResult.Items.First();
                var userId = user["Id"].S;

                // 2. Validate expiry
                if (!user.ContainsKey("ResetTokenExpiry") ||
                    !DateTime.TryParse(
                        user["ResetTokenExpiry"].S,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var expiry) ||
                    expiry.ToUniversalTime() < DateTime.UtcNow)
                {
                    return new BadRequestObjectResult(new { Message = "Invalid or expired token." });
                }

                // 3. Hash new password (same style as Signup)
                var salt = Guid.NewGuid().ToString();
                var hashedPassword = Utilities.HashPassword(resetData.NewPassword, salt);

                // 4. Update user password + clear reset token
                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                UPDATE Users 
                SET PasswordHash = ?, Salt = ?, ResetToken = ?, ResetTokenExpiry = ?
                WHERE Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = hashedPassword },
                new AttributeValue { S = salt },
                new AttributeValue { NULL = true },
                new AttributeValue { NULL = true },
                new AttributeValue { S = userId }
            }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    Message = "Password reset successful"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResetPassword failed.");
                return new StatusCodeResult(500);
            }
        }

        public class ResetPasswordRequest
        {
            public string Token { get; set; }
            public string NewPassword { get; set; }
        }

        [Function("GoogleLogin")]
        public async Task<IActionResult> GoogleLogin([HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
        {
            _logger.LogInformation("[AUTH] GoogleLogin started");
            DynamoDbClientFactory.LogSelection(_logger, DynamoDbClientFactory.Resolve());

            // Validate Origin header
            string referer = req.Headers.TryGetValues("Origin", out var values) ? values.FirstOrDefault() : null;

            // Get the Origin header
            //string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var loginData = System.Text.Json.JsonSerializer.Deserialize<GoogleLoginRequest>(
                requestBody,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                // Verify Google ID token
                GoogleJsonWebSignature.Payload payload;
                try
                {
                    payload = await GoogleJsonWebSignature.ValidateAsync(
                        loginData.IdToken,
                        new GoogleJsonWebSignature.ValidationSettings
                        {
                            Audience = new[] { Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") }
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Invalid Google token.");
                    return new UnauthorizedResult();
                }

                _logger.LogInformation("[AUTH] Google identity verified");
                if (string.IsNullOrWhiteSpace(payload.Email) || payload.EmailVerified != true)
                    return new UnauthorizedResult();

                var user = await canonicalUserStore.FindOrCreateGoogleUserAsync(payload.Email);
                var jwtToken = Utilities.GenerateJwt(user.Username, user.Id);
                _logger.LogInformation("[AUTH] JWT created for canonical user");

                var response = new TokenResponse
                {
                    Token = jwtToken,
                    Email = user.Username,
                    Name = payload.Name
                };

                _logger.LogInformation("[AUTH] GoogleLogin completed");
                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Google login failed.");
                return new StatusCodeResult(500);
            }
        }

        public class GoogleLoginRequest
        {
            public string IdToken { get; set; }
        }

        public class TokenResponse
        {
            public string Token { get; set; }
            public string Email { get; set; }
            public string Name { get; set; }
        }

        [Function("PostTestText")]
        public async Task<IActionResult> PostTestText([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            //string setTest = req.SetTestText;
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<TestTextRequest>(requestBody);
            var response = new Response<TestModel>();

            var testModel = new TestModel()
            {
                TestText = data.SetTestText
            };
            response.Item = testModel;
            //if (data == null || string.IsNullOrEmpty(data.SetTestText))
            //{
            //    return new BadRequestObjectResult("Invalid request data.");
            //}

            //_logger.LogInformation($"Received test text: {data.SetTestText}");
            return new OkObjectResult(response);// new { message = "Text received successfully", text = data.SetTestText });
        }

        [Function("GetAIResponse")]
        public async Task<IActionResult> GetAIResponse([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<OpenAIRequest>(requestBody);

            if (data == null || string.IsNullOrEmpty(data.Prompt))
            {
                return new BadRequestObjectResult("Prompt cannot be empty.");
            }

            try
            {
                var response = await testStringBac.GetAIResponse(); // Call business layer
                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in GetAIResponse: {ex.Message}");
                return new ObjectResult($"Internal Server Error: {ex.Message}") { StatusCode = 500 };
            }
        }

        public class CheckoutRequest
        {
            public string? LandingVariant { get; set; }
            public string plan { get; set; }
            public string billingInterval { get; set; }
            public bool isOneTime { get; set; }
            public string returnUrl { get; set; }
            public string userId { get; set; }
            public string id { get; set; }
            public string? projectId { get; set; }
            public string? funnelId { get; set; }
            public DesignMode.AI.Shared.Models.Attribution.MeasurementContext? measurement { get; set; }
        }

        [Function("CreateCheckoutSession")]
        public async Task<IActionResult> CreateCheckoutSession(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                var origin = req.Headers["Origin"].FirstOrDefault();
                var allowedHost = Environment.GetEnvironmentVariable("HostUrl");

                if (!string.IsNullOrEmpty(origin) && !string.IsNullOrEmpty(allowedHost))
                {
                    if (!origin.StartsWith(allowedHost, StringComparison.OrdinalIgnoreCase))
                        return new UnauthorizedResult();
                }

                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var data = JsonConvert.DeserializeObject<CheckoutRequest>(body);

                if (data == null)
                    return new BadRequestObjectResult("Invalid request body.");

                string plan = data.plan;
                string interval = data.billingInterval;
                bool isOneTime = data.isOneTime;
                string userId = data.userId;
                string inviteId = data.id;
                string returnUrl = data.returnUrl;

                var authenticatedUsername = jwtUserResolver.ResolveUsername(req);
                var authenticatedUserId = string.IsNullOrWhiteSpace(authenticatedUsername) ? null : await attributionStore.ResolveUserIdAsync(authenticatedUsername);
                if (!string.IsNullOrWhiteSpace(authenticatedUserId)) userId = authenticatedUserId;

                if (string.IsNullOrWhiteSpace(userId))
                    return new BadRequestObjectResult("Missing userId.");

                if (string.IsNullOrWhiteSpace(inviteId))
                    return new BadRequestObjectResult("Missing id.");

                string baseUrl = allowedHost?.TrimEnd('/');

                if (string.IsNullOrEmpty(baseUrl))
                {
                    _logger.LogError("HostUrl is missing in configuration.");
                    return new StatusCodeResult(500);
                }

                var stripeClient = new StripeClient(
                    Environment.GetEnvironmentVariable("StripeSecretKey"));

                var sessionService = new SessionService(stripeClient);
                string priceId = GetPriceId(plan, interval, isOneTime);
                var stripePrice = await new PriceService(stripeClient).GetAsync(priceId);
                var checkoutAttempt = !string.IsNullOrWhiteSpace(data.projectId) && !string.IsNullOrWhiteSpace(data.funnelId)
                    ? await attributionStore.CreateCheckoutAttemptAsync(userId, data.projectId, data.funnelId, plan, (stripePrice.UnitAmountDecimal ?? 0m) / 100m, stripePrice.Currency ?? "usd", data.measurement, isOneTime ? "one_time" : interval)
                    : null;

                if (WebsiteFunnelPromptResolver.IsWebsite(data.LandingVariant) &&
                    (string.IsNullOrWhiteSpace(authenticatedUserId) || checkoutAttempt is null ||
                     !await ValidateWebsiteInviteAsync(userId, data.projectId!, inviteId)))
                    return new UnauthorizedResult();

                var options = new SessionCreateOptions
                {
                    Mode = isOneTime ? "payment" : "subscription",

                    SuccessUrl = WebsiteFunnelPromptResolver.IsWebsite(data.LandingVariant)
                        ? $"{baseUrl}/init/website-success?session_id={{CHECKOUT_SESSION_ID}}"
                        : $"{baseUrl}/init/Success?session_id={{CHECKOUT_SESSION_ID}}&inviteId={inviteId}",
                    CancelUrl = $"{returnUrl}",

                    LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Price = priceId,
                    Quantity = 1
                }
            },

                    ClientReferenceId = userId,

                    Metadata = new Dictionary<string, string>
            {
                { "userId", userId },
                { "plan", plan },
                { "interval", interval },
                { "id", inviteId }
            },

                    SubscriptionData = isOneTime ? null : new SessionSubscriptionDataOptions
                    {
                        Metadata = new Dictionary<string, string>
                {
                    { "userId", userId },
                    { "plan", plan },
                    { "interval", interval },
                    { "id", inviteId }
                }
                    }
                };

                if (checkoutAttempt is not null)
                {
                    options.Metadata["checkoutAttemptId"] = checkoutAttempt.CheckoutAttemptId;
                    options.Metadata["projectId"] = data.projectId!;
                    options.Metadata["funnelId"] = data.funnelId!;
                    options.Metadata["attributionSnapshotId"] = checkoutAttempt.AttributionSnapshotId;
                    if (options.SubscriptionData is not null)
                    {
                        options.SubscriptionData.Metadata["checkoutAttemptId"] = checkoutAttempt.CheckoutAttemptId;
                        options.SubscriptionData.Metadata["projectId"] = data.projectId!;
                        options.SubscriptionData.Metadata["funnelId"] = data.funnelId!;
                        options.SubscriptionData.Metadata["attributionSnapshotId"] = checkoutAttempt.AttributionSnapshotId;
                    }
                }

                var session = await sessionService.CreateAsync(options);
                if (checkoutAttempt is not null) await attributionStore.SetCheckoutSessionAsync(checkoutAttempt.CheckoutAttemptId, session.Id);

                return new OkObjectResult(new
                {
                    url = session.Url,
                    sessionId = session.Id,
                    value = MeasurementMoney.FromMinor(stripePrice.UnitAmount ?? 0, stripePrice.Currency ?? "usd"),
                    currency = (stripePrice.Currency ?? "usd").ToUpperInvariant()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Stripe checkout session.");
                return new StatusCodeResult(500);
            }
        }

        private string GetPriceId(string plan, string interval, bool isOneTime)
        {
            plan = plan?.Trim().ToLower();
            interval = interval?.Trim().ToLower();

            return (plan, interval, isOneTime) switch
            {
                ("starter", _, true) =>
                    Environment.GetEnvironmentVariable("StripePrices:StarterOneTime"),

                ("plus", "monthly", false) =>
                    Environment.GetEnvironmentVariable("StripePrices:PlusMonthly"),

                ("plus", "yearly", false) =>
                    Environment.GetEnvironmentVariable("StripePrices:PlusYearly"),

                ("pro", "monthly", false) =>
                    Environment.GetEnvironmentVariable("StripePrices:ProMonthly"),

                ("pro", "yearly", false) =>
                    Environment.GetEnvironmentVariable("StripePrices:ProYearly"),

                _ => throw new Exception(
                    $"Invalid plan/interval combination: plan={plan}, interval={interval}, isOneTime={isOneTime}"
                )
            };
        }

        [Function("StripeWebhook")]
        public async Task<IActionResult> StripeWebhook(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            _logger.LogInformation("[STRIPE PURCHASE] webhook request received");
            try
            {
                // Read Stripe signature
                string stripeSignature = req.Headers["Stripe-Signature"].FirstOrDefault();
                if (string.IsNullOrEmpty(stripeSignature))
                {
                    _logger.LogWarning("[STRIPE PURCHASE] failed: signature verification (missing header)");
                    return new BadRequestObjectResult("Missing Stripe-Signature header.");
                }

                string webhookSecret = Environment.GetEnvironmentVariable("StripeWebhookSecret");
                string stripeSecretKey = Environment.GetEnvironmentVariable("StripeSecretKey");
                if (string.IsNullOrWhiteSpace(webhookSecret))
                {
                    _logger.LogError("[STRIPE PURCHASE] failed: StripeWebhookSecret not configured");
                    return new StatusCodeResult(500);
                }

                var stripeClient = new StripeClient(stripeSecretKey);

                string json = await new StreamReader(req.Body).ReadToEndAsync();

                Stripe.Event stripeEvent;
                try { stripeEvent = StripeWebhookVerification.Verify(json, stripeSignature, webhookSecret); }
                catch (StripeException)
                {
                    _logger.LogWarning("[STRIPE PURCHASE] failed: signature verification");
                    return new BadRequestObjectResult("Invalid Stripe signature.");
                }
                _logger.LogInformation("[STRIPE PURCHASE] signature verified");
                if (stripeEvent.Type == "invoice.paid")
                    _logger.LogInformation("[STRIPE WEBHOOK] invoice.paid received");

                if (creditPurchases is not null && await creditPurchases.HandleVerifiedAsync(stripeEvent))
                    return new OkResult();
                await purchaseMeasurement.HandleVerifiedAsync(stripeEvent, json);

                switch (stripeEvent.Type)
                {
                    // =========================================================
                    // Checkout completed (BEST place to persist mapping)
                    // =========================================================
                    case "checkout.session.completed":
                        {
                            var session = stripeEvent.Data.Object as Stripe.Checkout.Session;

                            if (session == null)
                                return new BadRequestObjectResult("Invalid session payload.");

                            var userId = session.ClientReferenceId;
                            var customerId = session.CustomerId;
                            var subscriptionId = session.SubscriptionId;

                            string id = null;
                            string? completedAttemptId = null;

                            session?.Metadata?.TryGetValue("id", out id);
                            session?.Metadata?.TryGetValue("checkoutAttemptId", out completedAttemptId);
                            await attributionStore.CompleteCheckoutAsync(completedAttemptId ?? string.Empty, "CheckoutCompleted", subscriptionId);

                            _logger.LogInformation("Checkout Session Completed");

                            break;
                        }

                    // =========================================================
                    // Subscription events (USE METADATA HERE)
                    // =========================================================
                    case "customer.subscription.created":
                    case "customer.subscription.updated":
                        {
                            var subscription = stripeEvent.Data.Object as Stripe.Subscription;

                            if (subscription == null)
                                return new BadRequestObjectResult("Invalid subscription payload.");

                            string id = null;
                            string? checkoutAttemptId = null;
                            string? subscriptionProjectId = null;
                            string? subscriptionFunnelId = null;
                            string? attributionSnapshotId = null;

                            subscription?.Metadata?.TryGetValue("id", out id);
                            subscription?.Metadata?.TryGetValue("checkoutAttemptId", out checkoutAttemptId);
                            subscription?.Metadata?.TryGetValue("projectId", out subscriptionProjectId);
                            subscription?.Metadata?.TryGetValue("funnelId", out subscriptionFunnelId);
                            subscription?.Metadata?.TryGetValue("attributionSnapshotId", out attributionSnapshotId);
                            await attributionStore.CompleteCheckoutAsync(checkoutAttemptId ?? string.Empty, subscription.Status ?? "Active", subscription.Id);

                            var customerService = new CustomerService(stripeClient);
                            var customer = await customerService.GetAsync(subscription.CustomerId);

                            var item = subscription.Items.Data.FirstOrDefault();

                            var priceId = item?.Price?.Id;
                            var productId = item?.Price?.ProductId;

                            // Pull metadata
                            var userId = subscription.Metadata != null &&
                                         subscription.Metadata.ContainsKey("userId")
                                ? subscription.Metadata["userId"]
                                : null;

                            // Subscription details
                            var subscriptionStatus = subscription.Status;
                            var name = item?.Price?.Nickname ?? item?.Price?.Id;

                            // Stripe stores amount in cents
                            decimal price = item?.Price?.UnitAmount != null
                                ? (decimal)item.Price.UnitAmount.Value / 100m
                                : 0;

                            string recurring = "";

                            _logger.LogInformation("Subscription Event");

                            // =====================================================
                            // SAVE SUBSCRIPTION TO DYNAMODB
                            // =====================================================

                            var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                            // Generate unique GUID
                            string subscriptionGuid;

                            while (true)
                            {
                                subscriptionGuid = Guid.NewGuid().ToString();

                                var checkIdRequest = new ExecuteStatementRequest
                                {
                                    Statement = "SELECT Id FROM Subscriptions WHERE Id = ?",
                                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = subscriptionGuid }
            }
                                };

                                var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                                if (!existingIdResult.Items.Any())
                                    break;
                            }

                            var timestamp = DateTime.UtcNow.ToString("o");

                            var starterOneTime = Environment.GetEnvironmentVariable("StripePrices:StarterOneTime");
                            var plusMonthly = Environment.GetEnvironmentVariable("StripePrices:PlusMonthly");
                            var plusYearly = Environment.GetEnvironmentVariable("StripePrices:PlusYearly");
                            var proMonthly = Environment.GetEnvironmentVariable("StripePrices:ProMonthly");
                            var proYearly = Environment.GetEnvironmentVariable("StripePrices:ProYearly");

                            if (priceId == starterOneTime)
                            {
                                name = "Starter";
                                price = 99;
                                recurring = "OneTime";
                            }
                            else if (priceId == plusMonthly)
                            {
                                name = "Plus Monthly";
                                price = 49m;
                                recurring = "Monthly";
                            }
                            else if (priceId == plusYearly)
                            {
                                name = "Plus Yearly";
                                price = 468m;
                                recurring = "Yearly";
                            }
                            else if (priceId == proMonthly)
                            {
                                name = "Pro Monthly";
                                price = 59m;
                                recurring = "Monthly";
                            }
                            else if (priceId == proYearly)
                            {
                                name = "Pro Yearly";
                                price = 1236m;
                                recurring = "Yearly";
                            }

                            if (global::DesignMode.AI.Server.Services.StripeVerifiedPaymentResolver.ConfiguredPricePlan(priceId, subscription.Livemode) is { } upgradedPlan)
                            {
                                name = upgradedPlan == "extreme" ? "Extreme Monthly" : "Plus Monthly";
                                price = (item?.Price?.UnitAmount ?? 0) / 100m;
                                recurring = "Monthly";
                            }

                            var insertRequest = new ExecuteStatementRequest
                            {
                                Statement = @"
INSERT INTO Subscriptions VALUE
{
    'Id': ?,
    'UserId': ?,
    'SubscriptionId': ?,
    'SubscriptionStatus': ?,
    'PriceId': ?,
    'ProductId': ?,
    'Name': ?,
    'Price': ?,
    'Recurring': ?,
    'TimeStamp': ?,
    'InviteId': ?,
    'CheckoutAttemptId': ?,
    'ProjectId': ?,
    'FunnelId': ?,
    'AttributionSnapshotId': ?
}",
                                Parameters = new List<AttributeValue>
        {
            new AttributeValue { S = subscriptionGuid },
            new AttributeValue { S = userId ?? string.Empty },
            new AttributeValue { S = subscription.Id },
            new AttributeValue { S = subscriptionStatus ?? string.Empty },
            new AttributeValue { S = priceId ?? string.Empty },
            new AttributeValue { S = productId ?? string.Empty },
            new AttributeValue { S = name ?? string.Empty },
            new AttributeValue { N = price.ToString() },
            new AttributeValue { S = recurring ?? string.Empty },
            new AttributeValue { S = timestamp },
                // ✅ NEW FIELD
    new AttributeValue { S = id ?? string.Empty },
    new AttributeValue { S = checkoutAttemptId ?? string.Empty },
    new AttributeValue { S = subscriptionProjectId ?? string.Empty },
    new AttributeValue { S = subscriptionFunnelId ?? string.Empty },
    new AttributeValue { S = attributionSnapshotId ?? string.Empty }
        }
                            };

                            await client.ExecuteStatementAsync(insertRequest);

                            if (funnelRecoverySequence is not null)
                                await funnelRecoverySequence.SuppressSubscribedSafelyAsync(userId, subscriptionGuid);

                            _logger.LogInformation("Subscription saved to DynamoDB.");
                            _logger.LogInformation("[STRIPE WEBHOOK] legacy subscription update completed");

                            break;
                        }
                    // =========================================================
                    // One-time payment succeeded
                    // =========================================================
                    case "payment_intent.succeeded":
                        {
                            var paymentIntent = stripeEvent.Data.Object as Stripe.PaymentIntent;

                            if (paymentIntent == null)
                                return new BadRequestObjectResult("Invalid payment intent payload.");

                            _logger.LogInformation("Payment Intent Succeeded");

                            // =====================================================
                            // GET SESSION (for userId + metadata)
                            // =====================================================
                            var sessionService = new Stripe.Checkout.SessionService(stripeClient);

                            var sessions = await sessionService.ListAsync(new Stripe.Checkout.SessionListOptions
                            {
                                PaymentIntent = paymentIntent.Id,
                                Limit = 1
                            });

                            var session = sessions.FirstOrDefault();

                            string id = null;
                            string? paymentCheckoutAttemptId = null;
                            string? paymentProjectId = null;
                            string? paymentFunnelId = null;
                            string? paymentSnapshotId = null;

                            session?.Metadata?.TryGetValue("id", out id);
                            session?.Metadata?.TryGetValue("checkoutAttemptId", out paymentCheckoutAttemptId);
                            session?.Metadata?.TryGetValue("projectId", out paymentProjectId);
                            session?.Metadata?.TryGetValue("funnelId", out paymentFunnelId);
                            session?.Metadata?.TryGetValue("attributionSnapshotId", out paymentSnapshotId);

                            // 🚨 KEY FIX: SKIP SUBSCRIPTIONS (prevents duplicate inserts)
                            if (session == null || session.SubscriptionId != null)
                            {
                                _logger.LogInformation("Skipping payment_intent.succeeded (not a one-time payment).");
                                break;
                            }

                            var userId = session?.ClientReferenceId;

                            var priceId = session?.Metadata != null &&
                                          session.Metadata.ContainsKey("plan")
                                ? session.Metadata["plan"]
                                : null;

                            if (priceId == "started")
                            {
                                priceId = Environment.GetEnvironmentVariable("StripePrices:StarterOneTime");
                            }


                            // =====================================================
                            // BUILD "SUBSCRIPTION-LIKE" RECORD (ONE TIME)
                            // =====================================================

                            var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                            // Generate unique GUID
                            string subscriptionGuid;

                            while (true)
                            {
                                subscriptionGuid = Guid.NewGuid().ToString();

                                var checkIdRequest = new ExecuteStatementRequest
                                {
                                    Statement = "SELECT Id FROM Subscriptions WHERE Id = ?",
                                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = subscriptionGuid }
            }
                                };

                                var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                                if (!existingIdResult.Items.Any())
                                    break;
                            }

                            var timestamp = DateTime.UtcNow.ToString("o");

                            string name = "One Time Purchase";
                            decimal price = (decimal)paymentIntent.Amount / 100m;
                            string recurring = "OneTime";

                            // OPTIONAL: map priceId if you still want your pricing logic
                            var starterOneTime = Environment.GetEnvironmentVariable("StripePrices:StarterOneTime");

                            if (priceId == starterOneTime)
                            {
                                name = "Starter";
                            }

                            var lineItemService = new Stripe.Checkout.SessionLineItemService(stripeClient);

                            var lineItems = await lineItemService.ListAsync(session.Id);

                            var firstItem = lineItems.FirstOrDefault();

                            var productId = firstItem?.Price?.ProductId;

                            // =====================================================
                            // INSERT INTO DYNAMODB (same structure as subscriptions)
                            // =====================================================

                            var insertRequest = new ExecuteStatementRequest
                            {
                                Statement = @"
INSERT INTO Subscriptions VALUE
{
    'Id': ?,
    'UserId': ?,
    'SubscriptionId': ?,
    'SubscriptionStatus': ?,
    'PriceId': ?,
    'ProductId': ?,
    'Name': ?,
    'Price': ?,
    'Recurring': ?,
    'TimeStamp': ?,
    'InviteId': ?,
    'CheckoutAttemptId': ?,
    'ProjectId': ?,
    'FunnelId': ?,
    'AttributionSnapshotId': ?
}",
                                Parameters = new List<AttributeValue>
        {
            new AttributeValue { S = subscriptionGuid },
            new AttributeValue { S = userId ?? string.Empty },
            new AttributeValue { S = paymentIntent.Id }, // using PaymentIntent as ID
            new AttributeValue { S = "Paid" },
            new AttributeValue { S = starterOneTime },
            new AttributeValue { S = productId },
            new AttributeValue { S = name },
            new AttributeValue { N = price.ToString() },
            new AttributeValue { S = recurring },
            new AttributeValue { S = timestamp },

    // ✅ NEW FIELD
    new AttributeValue { S = id ?? string.Empty },
    new AttributeValue { S = paymentCheckoutAttemptId ?? string.Empty },
    new AttributeValue { S = paymentProjectId ?? string.Empty },
    new AttributeValue { S = paymentFunnelId ?? string.Empty },
    new AttributeValue { S = paymentSnapshotId ?? string.Empty }
        }
                            };

                            await client.ExecuteStatementAsync(insertRequest);

                            if (funnelRecoverySequence is not null)
                                await funnelRecoverySequence.SuppressSubscribedSafelyAsync(userId, subscriptionGuid);

                            _logger.LogInformation("One-time payment saved to Subscriptions table.");
                            _logger.LogInformation("[STRIPE WEBHOOK] legacy subscription update completed");

                            break;
                        }

                    // =========================================================
                    // Invoice paid
                    // =========================================================
                    case "invoice.paid":
                        {
                            var invoice = stripeEvent.Data.Object as Stripe.Invoice;

                            if (invoice == null)
                                return new BadRequestObjectResult("Invalid invoice payload.");

                            _logger.LogInformation("Invoice Paid");

                            break;
                        }

                    default:
                        break;
                }

                return new OkObjectResult("Webhook processed successfully.");
            }
            catch (StripeException ex)
            {
                _logger.LogError("[STRIPE PURCHASE] failed: Stripe API resolution error");
                return new StatusCodeResult(500);
            }
            catch (Exception ex)
            {
                _logger.LogError("[STRIPE PURCHASE] failed: webhook processing or persistence error ({ErrorType})", ex.GetType().Name);
                return new StatusCodeResult(500);
            }
        }

        [Function("CheckUserOnboarded")]
        public async Task<IActionResult> CheckUserOnboarded(
    [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            try
            {
                // =====================================================
                // GET USERNAME PARAM
                // Example:
                // /api/user/onboarded?username=test@test.com
                // =====================================================

                var username = req.Query["username"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(username))
                {
                    return new BadRequestObjectResult("Missing username parameter.");
                }

                // =====================================================
                // DYNAMODB CLIENT
                // =====================================================

                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // =====================================================
                // FIND USER BY USERNAME
                // =====================================================

                var userRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM Users WHERE Username = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = username }
            }
                };

                var userResult = await client.ExecuteStatementAsync(userRequest);

                if (!userResult.Items.Any())
                {
                    return new NotFoundObjectResult(new
                    {
                        Success = false,
                        Username = username,
                        UserId = (string)null,
                        Onboarded = false
                    });
                }

                var user = userResult.Items.First();

                var userId = user.ContainsKey("Id") ? user["Id"].S : null;

                string onboarded = user.ContainsKey("Onboarded")
    ? user["Onboarded"].S
    : "0";

                // =====================================================
                // RESPONSE
                // =====================================================

                return new OkObjectResult(new
                {
                    Success = true,
                    Username = username,
                    UserId = userId,
                    Onboarded = onboarded
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking onboarding status.");

                return new ObjectResult(new
                {
                    Success = false,
                    Username = (string)null,
                    UserId = (string)null,
                    Onboarded = false
                })
                {
                    StatusCode = 500
                };
            }
        }

        [Function("CheckUserSubscription")]
        public async Task<IActionResult> CheckUserSubscription(
        [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
        {
            try
            {
                // =====================================================
                // GET EMAIL PARAM
                // Example:
                // /api/subscription/check?email=test@test.com
                // =====================================================

                var email = req.Query["email"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(email))
                {
                    return new BadRequestObjectResult("Missing email parameter.");
                }

                // =====================================================
                // DYNAMODB CLIENT
                // =====================================================

                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // =====================================================
                // FIND USER BY EMAIL
                // =====================================================

                var userRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM Users WHERE Username = ?",
                    Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = email }
                }
                };

                var userResult = await client.ExecuteStatementAsync(userRequest);

                if (!userResult.Items.Any())
                {
                    return new NotFoundObjectResult(new SubscriptionResponse
                    {
                        Success = false,
                        Email = email,
                        UserId = null,
                        HasSubscription = false,
                        SubscriptionCount = 0,
                        Subscriptions = new List<SubscriptionDto>()
                    });
                }

                var userId = userResult.Items.First()["Id"].S;

                // =====================================================
                // CHECK SUBSCRIPTIONS TABLE
                // =====================================================

                var subscriptionRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM Subscriptions WHERE UserId = ?",
                    Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = userId }
                }
                };

                var subscriptionResult =
                    await client.ExecuteStatementAsync(subscriptionRequest);

                bool hasSubscription = subscriptionResult.Items.Any();

                // =====================================================
                // RESPONSE OBJECT
                // =====================================================

                var subscriptions = subscriptionResult.Items.Select(x => new SubscriptionDto
                {
                    Id = x.TryGetValue("Id", out var id) ? id.S : null,
                    UserId = x.TryGetValue("UserId", out var userIdVal) ? userIdVal.S : null,
                    SubscriptionId = x.TryGetValue("SubscriptionId", out var subId) ? subId.S : null,
                    Status = x.TryGetValue("Status", out var status) ? status.S : null,
                    PriceId = x.TryGetValue("PriceId", out var priceId) ? priceId.S : null,
                    Name = x.TryGetValue("Name", out var name) ? name.S : null
                }).ToList();

                var response = new SubscriptionResponse
                {
                    Success = true,
                    Email = email,
                    UserId = userId,
                    HasSubscription = hasSubscription,
                    SubscriptionCount = subscriptions.Count,
                    Subscriptions = subscriptions
                };

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking subscription.");

                return new ObjectResult(new SubscriptionResponse
                {
                    Success = false,
                    Email = null,
                    UserId = null,
                    HasSubscription = false,
                    SubscriptionCount = 0,
                    Subscriptions = new List<SubscriptionDto>()
                })
                {
                    StatusCode = 500
                };
            }
        }

        public class SubscriptionResponse
        {
            public bool Success { get; set; }
            public string Email { get; set; }
            public string UserId { get; set; }
            public bool HasSubscription { get; set; }
            public int SubscriptionCount { get; set; }
            public List<SubscriptionDto> Subscriptions { get; set; }
        }

        public class SubscriptionDto
        {
            public string Id { get; set; }
            public string UserId { get; set; }
            public string SubscriptionId { get; set; }
            public string Status { get; set; }
            public string PriceId { get; set; }
            public string Name { get; set; }
        }

        [Function("GetIndustrySuggestions")]
        public async Task<IActionResult> GetIndustrySuggestions(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (string.IsNullOrEmpty(referer) ||
                referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var request =
                JsonConvert.DeserializeObject<IndustrySearchRequest>(requestBody);

            if (string.IsNullOrWhiteSpace(request?.Query))
            {
                return new BadRequestObjectResult("Query is required.");
            }

            string prompt = $@"
Return up to 3 company industry suggestions that start with or closely match:
'{request.Query}'

Rules:
- Return ONLY a JSON array of strings.
- No explanations.
- No markdown.
- No numbering.
- Industries only.

Example:
[
  ""Healthcare"",
  ""Health Insurance"",
  ""Health Technology""
]
";

            using var client = new HttpClient();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var openAiRequest = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
                {
            new
            {
                role = "system",
                content = "You return valid JSON only."
            },
            new
            {
                role = "user",
                content = prompt
            }
        },
                temperature = 0.2,
                max_tokens = 150
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(openAiRequest),
                    Encoding.UTF8,
                    "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();

            var openAiResponse =
                System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var content = openAiResponse
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            try
            {
                var industries =
                    System.Text.Json.JsonSerializer.Deserialize<List<string>>(content);

                return new OkObjectResult(industries ?? new List<string>());
            }
            catch
            {
                return new OkObjectResult(new List<string>());
            }
        }

        public class IndustrySearchRequest
        {
            public string Query { get; set; }
        }
        public sealed class OpenAIIdeaRequestQueue
        {
            public string? LandingVariant { get; set; }
            public string JobId { get; set; } = string.Empty;
            public string ImageId { get; set; } = string.Empty;
            public string ImageUrl { get; set; } = string.Empty;

            public string? Name { get; set; }
            public string? Industry { get; set; }

            public int ConceptNumber { get; set; }

            public int? Width { get; set; }
            public int? Height { get; set; }
            public int? Duration { get; set; }
            public int? NumberResults { get; set; }
            public int? OutputQuality { get; set; }

            public string? OutputType { get; set; }
            public string? OutputFormat { get; set; }
            public string? Model { get; set; }

            public bool? IncludeCost { get; set; }
            public string? GenerationJobId { get; set; }
            public string? UserId { get; set; }
            public string? ProjectId { get; set; }
            public string? FunnelId { get; set; }
        }
        public class DesignModeJobResponse
        {
            public string? JobId { get; set; }

            public string? Status { get; set; }

            public string? Name { get; set; }

            public string? Industry { get; set; }

            public string? ImageUrl { get; set; }

            public string? ImageDescription { get; set; }

            public string? Error { get; set; }

            public string? CreatedAtUtc { get; set; }

            public string? UpdatedAtUtc { get; set; }
        }
        [Function("GetAIIdeaResponse")]
        public async Task<IActionResult> GetAIIdeaResponse([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);
            if (getData is null || string.IsNullOrWhiteSpace(getData.Name) || string.IsNullOrWhiteSpace(getData.Industry))
                return new BadRequestObjectResult("Name and industry are required.");

            var claimedIdeaUserId = jwtUserResolver.ResolveUserId(req);
            var ideaUserId = string.IsNullOrWhiteSpace(claimedIdeaUserId) ? null : await attributionStore.ResolveClaimedUserIdAsync(claimedIdeaUserId);
            var hasGenerationContext = !string.IsNullOrWhiteSpace(ideaUserId) &&
                !string.IsNullOrWhiteSpace(getData.ProjectId) &&
                !string.IsNullOrWhiteSpace(getData.FunnelId);
            if (hasGenerationContext && !await attributionStore.ValidateGenerationContextAsync(ideaUserId!, getData.ProjectId!, getData.FunnelId!))
                return new UnauthorizedResult();

            var industry = getData.Industry; var name = getData.Name;
            var ideaJobId = Guid.TryParse(getData.GenerationJobId, out _) ? getData.GenerationJobId! : Guid.NewGuid().ToString();
            var ideaModel = Environment.GetEnvironmentVariable("OpenAISettings:Model") ?? string.Empty;
            string? ideaUsageId = null;
            if (hasGenerationContext)
            {
                try
                {
                    ideaUsageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage { UserId = ideaUserId, ProjectId = getData.ProjectId, FunnelId = getData.FunnelId, GenerationJobId = ideaJobId, GenerationCategory = "SocialFunnel", DeliverableType = "CreativeDirections", Operation = "TextGeneration", Provider = "OpenAI", Model = ideaModel, PricingVersion = AIProviderPricingService.CurrentVersion });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AI USAGE] idea generation tracking unavailable; generation will continue");
                }
            }
            else
            {
                _logger.LogWarning("[AI USAGE] idea generation context unavailable; generation will continue");
            }

            var q1 = $"Generate a list of 9 prompts in random order for creative deliverables like Branding Identity and Logo, Print Materials, Social Media Content, Digital Marketing, Web & UX/UI Website, Video & Photography, Events & Activations, Packaging Label to get work done for a company in the {industry} industry named {name}. " +
                $"For each idea, provide a short, one or two-word creative design idea followed by a more detailed sentence that elaborates on the idea with 15 words or less. " +
                $"The sentence should describe the creative direction, or any relevant marketing aspects for each concept. only have 1 colon in the response. Add company name the sentence. " +
                $"Add the name of the company if the name was provided. " +
                $"Don't add influencer ideas to the list. Don't have quotes.";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
                    new { role = "user", content = $"{q1}" },
                },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            HttpResponseMessage response;
            try { response = await client.PostAsync(Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"), new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")); }
            catch (TaskCanceledException) { if (ideaUsageId is not null) await aiUsage.CompleteFailureAsync(ideaUsageId, "Timeout"); throw; }
            catch { if (ideaUsageId is not null) await aiUsage.CompleteFailureAsync(ideaUsageId, "ProviderError"); throw; }

            if (!response.IsSuccessStatusCode)
            {
                if (ideaUsageId is not null) await aiUsage.CompleteFailureAsync(ideaUsageId, response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "InvalidRequest" : "ProviderError");
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);
            var parsedIdeaUsage = OpenAIUsageParser.ParseChatCompletions(data, ideaModel);
            var openAiRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (ideaUsageId is not null)
            {
                try { await aiUsage.CompleteOpenAiSuccessAsync(ideaUsageId, parsedIdeaUsage, aiPricing, "TextGeneration", openAiRequestId); }
                catch (Exception ex) { _logger.LogWarning(ex, "[AI USAGE] idea generation completion tracking unavailable"); }
            }

            // Assuming the response content is a list of ideas
            var ideasText = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            // Split the response by lines, removing the "1.", "2.", etc., and storing them in a list
            List<string> items = new List<string>();
            var lines = ideasText.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length > 6)
            {
                StringBuilder currentItem = new StringBuilder();
                foreach (var line in lines)
                {
                    int dotIndex = line.IndexOf(".");
                    if (char.IsDigit(line[0]) && line.Contains("."))
                    {
                        if (currentItem.Length > 0)
                        {
                            items.Add(currentItem.ToString().Trim());
                        }

                        // Start a new item
                        currentItem.Clear();
                        currentItem.Append(line.Substring(dotIndex + 1).Trim() + ":");
                    }
                    else
                    {
                        // Append to the current item to maintain single-line format
                        currentItem.Append(" " + line.Trim());
                    }
                }

                // Add the last item to the list
                if (currentItem.Length > 0)
                {
                    items.Add(currentItem.ToString().Trim());
                }
            }
            else
            {
                foreach (var line in lines)
                {
                    if (line.Contains("."))
                    {
                        // Remove the numeric prefix and add it to the list
                        string item = line.Substring(line.IndexOf('.') + 2).Trim(); // Trims the number and the dot
                        items.Add(item);
                    }
                }
            }

            return new OkObjectResult(items);
        }

        public class OpenAIDeliverableIdeaRequest
        {
            public string Industry { get; set; }
            public string Name { get; set; }
            public string Deliverable { get; set; }
        }

        [Function("GetAIDeliverableIdeaResponse")]
        public async Task<IActionResult> GetAIDeliverableIdeaResponse(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIDeliverableIdeaRequest>(requestBodyData);

            var industry = getData.Industry;
            var name = getData.Name;
            var deliverable = getData.Deliverable;

            var q1 =
                $"Generate exactly 1 creative prompt idea for the deliverable type '{deliverable}' " +
                $"for a company in the {industry} industry named {name}. " +
                $"Provide a short, one or two-word creative design idea followed by one colon, " +
                $"then a detailed sentence of 15 words or less. " +
                $"The sentence should describe the creative direction or marketing aspect. " +
                $"Include the company name if provided. " +
                $"Do not include influencer ideas. Do not use quotes. " +
                $"Return only the single item, with no numbering.";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
                {
            new { role = "user", content = q1 }
        },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var ideaText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            List<string> items = new List<string>
    {
        ideaText
    };

            return new OkObjectResult(items);
        }

        [Function("GetAICommercialVoiceOverScript")]
        public async Task<IActionResult> GetAICommercialVoiceOverScript(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AICommercialRequest>(requestBodyData);

            var companyName = getData.CompanyName;
            var industry = getData.Industry;
            var prompt = getData.Prompt;
            var script = getData.Script;

            if (string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(industry) || string.IsNullOrWhiteSpace(prompt))
            {
                return new BadRequestObjectResult("Missing companyName, industry, or prompt.");
            }

            // Build AI prompt for a full commercial voice-over script
            var aiPrompt = $@"
Generate a voice-over script for a commercial for a company named '{companyName}' in the '{industry}' industry. Incorporate this prompt: '{prompt}'.

Constraints:
- Maximum 40 words total
- Exactly 6 scenes
- Each scene: 1–2 very short sentences
- No lists, complex punctuation, or filler words
- Do not write ""Welcome,"" ""Introducing,"" or other greetings
- Only provide the narrator's lines
- Include a clear intro, value proposition, and call-to-action
- Verify the total word count before returning; if over 40, shorten immediately

Format exactly like this:
Scene 1: [narration]
Scene 2: [narration]
…
Scene 6: [narration]
            ";
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

                var requestBody = new
                {
                    model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                    messages = new[]
                    {
                new { role = "user", content = aiPrompt }
            },
                    max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
                };

                var response = await client.PostAsync(
                    Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                    new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(requestBody),
                        Encoding.UTF8,
                        "application/json"
                    )
                );

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Failed to fetch from AI API: {errorMsg}");
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

                var resultText = data
    .GetProperty("choices")[0]
    .GetProperty("message")
    .GetProperty("content")
    .GetString();

                if (string.IsNullOrWhiteSpace(resultText))
                {
                    resultText = string.Empty; // or handle error
                }
                else
                {
                    resultText = resultText.Trim();
                }


                return new OkObjectResult(new { voiceOverScript = resultText });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate commercial voice-over script.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetAICommercialCTA")]
        public async Task<IActionResult> GetAICommercialCTA(
[HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AICommercialCTARequest>(requestBodyData);

            var companyName = getData.CompanyName;
            var industry = getData.Industry;
            var prompt = getData.Prompt;
            var sceneVoiceOverScript = getData.SceneVoiceOverScript;

            if (string.IsNullOrWhiteSpace(companyName) ||
                string.IsNullOrWhiteSpace(industry) ||
                string.IsNullOrWhiteSpace(sceneVoiceOverScript))
            {
                return new BadRequestObjectResult("Missing companyName, industry, or sceneVoiceOverScript.");
            }

            // Build AI prompt for CTA
            var aiPrompt = $@"
You are writing the final call-to-action text for the last frames of a commercial video.

Company: '{companyName}'
Industry: '{industry}'

Voice-over script:
{sceneVoiceOverScript}

Additional direction: '{prompt}'

Requirements:
- Maximum 14 words
- Must clearly tell viewers where to take action
- Include ONE destination such as:
  - a website
  - booking page
  - contact page
  - quote page
  - physical location
- If referencing a website, format it like:
  {companyName}.com
- Start with an action verb
- No greetings
- No quotes
- No hashtags
- No emojis

Return ONLY the CTA sentence text.
Do NOT include labels, explanations, JSON, or word counts.
";

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

                var requestBody = new
                {
                    model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                    messages = new[]
                    {
                new { role = "user", content = aiPrompt }
            },
                    max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
                };

                var response = await client.PostAsync(
                    Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                    new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(requestBody),
                        Encoding.UTF8,
                        "application/json"
                    )
                );

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Failed to fetch from AI API: {errorMsg}");
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

                var resultText = data
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrWhiteSpace(resultText))
                {
                    resultText = string.Empty;
                }
                else
                {
                    resultText = resultText.Trim();
                }

                return new OkObjectResult(resultText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate CTA.");
                return new StatusCodeResult(500);
            }
        }

        public class AICommercialCTARequest
        {
            public string CompanyName { get; set; }
            public string Industry { get; set; }
            public string Prompt { get; set; }
            public string SceneVoiceOverScript { get; set; }
        }

        [Function("GetAICommercialResponse")]
        public async Task<IActionResult> GetAICommercialResponse(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AICommercialRequest>(requestBodyData);

            var companyName = getData.CompanyName;
            var industry = getData.Industry;
            var prompt = getData.Prompt;

            // Build AI prompt
            var aiPrompt = $@"with a company named {companyName}, in the {industry} industry and a 
prompt of ""{prompt}"" create a one sentence 
that describe it for a commercial.  use the word commercial in it.  follow this as an example:  
""almond milk commercial, this company is proud of their great taste and low calorie.""";

            // Call AI API
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
                {
            new { role = "user", content = aiPrompt }
        },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from AI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var resultText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            return new OkObjectResult(resultText);
        }

        public class AICommercialRequest
        {
            public string CompanyName { get; set; }
            public string Industry { get; set; }
            public string Prompt { get; set; }
            public string Script { get; set; }
        }
        [Function("GetAIVideoPromptSequence")]
        public async Task<IActionResult> GetAIVideoPromptSequence(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AIVideoPromptRequest>(requestBodyData);

            // This should be the result from the first Azure Function
            var basePrompt = getData.BaseCommercialPrompt;

            // Build the AI prompt
            var aiPrompt = $@"with this ai video prompt as an example, generate similar prompts with 500 or less 
characters for a {basePrompt}.  this is 5 seconds long and 2 different camera shots. so generate the same for the 
commercial with camera, lighting, color contrast and color grade  in the prompts. make it feel cinematic
and professional.  make multiple 5 second prompts to amount to 30 seconds total.  Be creative on what 
happens with the shots, not necessarily like the same camera angles, lighting, color contrast, color 
grade and what happens in the shot.  make sure to have a CUT TO in each segment.  make it a stunning looking 
commercial that highlights the product.  make every person in it white, add it to the prompt if needed. 
nothing inappropriate.  add no text into each segment text.  order each section with numbers 1-6.  don't show any Chinese or any other text.  
example prompt:  [Camera slow zoom in shallow depth of field] A pink lip stick with water splash behind it 
and  with strawberries floating in space around it, CUT TO close up side angle slow motion shallow depth of 
field a female hand slowly picking up a strawberry as the water splashes behind it.";

            // Call AI API
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
                {
            new { role = "user", content = aiPrompt }
        },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from AI API");
            }

            // Parse response
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var resultText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            return new OkObjectResult(resultText);
        }
        public class AIVideoPromptRequest
        {
            public string BaseCommercialPrompt { get; set; }
        }
        [Function("GetAISceneCameraDescription")]
        public async Task<IActionResult> GetAISceneCameraDescription(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AIScenePromptRequest>(requestBodyData);

            if (string.IsNullOrWhiteSpace(getData?.SceneScript))
            {
                return new BadRequestObjectResult("Missing scene script.");
            }

            var sceneScript = getData.SceneScript;

            // Build AI prompt
            var aiPrompt = $@"
with this prompt, generate a plain description of what's happening in the scene without the camera movement, just describe what's happening. don't say the ethnicity of the person. make it 500 characters or less. 
prompt: {sceneScript}.";

            // Call AI API
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer",
                    Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
                {
            new { role = "user", content = aiPrompt }
        },
                max_tokens = 100
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from AI API");
            }

            // Parse response
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var resultText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            return new OkObjectResult(resultText);
        }
        [Function("GetAISceneDialogue")]
        public async Task<IActionResult> GetAISceneDialogue(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AIScenePromptRequest>(requestBodyData);

            if (string.IsNullOrWhiteSpace(getData?.SceneScript))
            {
                return new BadRequestObjectResult("Missing scene script.");
            }

            var sceneScript = getData.SceneScript;

            // Build AI prompt
            var aiPrompt = $@"
Using this scene script, generate a character voice script.  Scene script: {sceneScript}";

            // Call AI API
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer",
                    Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
                {
            new { role = "user", content = aiPrompt }
        },
                max_tokens = 150
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from AI API");
            }

            // Parse response
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var resultText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            return new OkObjectResult(resultText);
        }

        public class AIScenePromptRequest
        {
            public string SceneScript { get; set; }
        }
        [Function("GetAIImageDescription")]
        public async Task<IActionResult> GetAIImageDescription(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
            => await GetAIImageDescriptionCore(req, false);

        [Function("GetWorkspaceAIImageDescription")]
        public async Task<IActionResult> GetWorkspaceAIImageDescription(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
            => await GetAIImageDescriptionCore(req, true);

        private async Task<IActionResult> GetAIImageDescriptionCore(HttpRequest req, bool workspace)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AIImageDescriptionRequest>(requestBodyData);

            if (string.IsNullOrWhiteSpace(getData?.ImageUrl))
            {
                return new BadRequestObjectResult("Missing image URL.");
            }

            string? imageUserId = null;
            ProjectContextResponse? imageProject = null;
            var hasWebsiteGenerationContext = workspace || !string.IsNullOrWhiteSpace(getData.ProjectId) ||
                !string.IsNullOrWhiteSpace(getData.GenerationJobId);
            if (hasWebsiteGenerationContext)
            {
                var imageClaimedUserId = jwtUserResolver.ResolveUserId(req);
                imageUserId = string.IsNullOrWhiteSpace(imageClaimedUserId)
                    ? null
                    : await attributionStore.ResolveClaimedUserIdAsync(imageClaimedUserId);
                if (string.IsNullOrWhiteSpace(imageUserId))
                    imageUserId = await ResolveLegacyJwtUserIdAsync(req);
                // Background queue work has no browser JWT. QueueApiCall already
                // resolved this UserId from the authenticated request before it
                // persisted and enqueued the job; still verify project ownership
                // below before accepting the worker context.
                if (!workspace && string.IsNullOrWhiteSpace(imageUserId))
                    imageUserId = getData.UserId;
                if (string.IsNullOrWhiteSpace(imageUserId) || string.IsNullOrWhiteSpace(getData.ProjectId))
                    return new UnauthorizedResult();
                imageProject = workspace
                    ? await attributionStore.GetOwnedGenerationProjectAsync(imageUserId, getData.ProjectId)
                    : await attributionStore.GetOwnedProjectContextAsync(imageUserId, getData.ProjectId);
                if (imageProject is null) return new UnauthorizedResult();
                if (workspace)
                {
                    var quota = await aiUsageSummary!.EvaluateAsync(imageUserId, "website_generation");
                    if (!quota.Allowed)
                        return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };
                }

                _logger.LogInformation(
                    "[VIDEO QUEUE] image-description context validated jobId={JobId}",
                    getData.GenerationJobId);
            }

            const string imageDescriptionModel = "gpt-4.1-mini";
            var imageDescriptionJobId = string.IsNullOrWhiteSpace(getData.GenerationJobId)
                ? Guid.NewGuid().ToString()
                : getData.GenerationJobId;
            var imageDescriptionUsageId = await aiUsageRecorder!.StartAsync(new AIGenerationUsage
            {
                UserId = imageUserId ?? getData.UserId,
                ProjectId = imageProject?.ProjectId ?? getData.ProjectId,
                FunnelId = imageProject?.FunnelId ?? getData.FunnelId,
                GenerationJobId = imageDescriptionJobId,
                GenerationCategory = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? "WebsiteFunnel" : hasWebsiteGenerationContext ? "PaidWorkspace" : "SocialFunnel",
                DeliverableType = hasWebsiteGenerationContext ? "Website" : "SocialVideo",
                Feature = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? WebsiteFunnelPromptResolver.Feature : hasWebsiteGenerationContext ? "website_generation" : null,
                UsageScope = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? WebsiteFunnelPromptResolver.UsageScope : hasWebsiteGenerationContext ? "Paid" : null,
                GenerationUnits = 0,
                Operation = hasWebsiteGenerationContext ? "WebsiteImageDescription" : "ImageDescription",
                Provider = "OpenAI",
                Model = imageDescriptionModel, ProviderTaskId = $"{imageDescriptionJobId}:image-description",
                PricingVersion = AIProviderPricingService.CurrentVersion
            });

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = imageDescriptionModel,
                input = new[]
                {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = "Generate a short, descriptive caption of the scene in this image, 1–2 sentences." },
                    //new { type = "input_text", text = "Generate a short, descriptive caption of the scene in this image, 1–2 sentences. Also, generate a color scheme based on the image, output 3 hex codes for it. Also, determine the best place to place overlay text on the image without disrupting the video made from the image." },
                    new { type = "input_image", image_url = getData.ImageUrl }
                }
            }
        }
            };

            var response = await client.PostAsync(
                "https://api.openai.com/v1/responses",
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                await aiUsageRecorder.CompleteFailureAsync(imageDescriptionUsageId, response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "InvalidRequest" : "ProviderError");
                return new BadRequestObjectResult("The image description provider rejected the request.");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);
            var parsedimageUsage = OpenAIUsageParser.ParseResponses(data, imageDescriptionModel);
            var imageProviderRequestId = response.Headers.TryGetValues("x-request-id", out var imageRequestIds) ? imageRequestIds.FirstOrDefault() : null;

            string? resultText = null;

            if (data.TryGetProperty("output", out var outputArray))
            {
                foreach (var outputItem in outputArray.EnumerateArray())
                {
                    if (outputItem.TryGetProperty("content", out var contentArray))
                    {
                        foreach (var contentItem in contentArray.EnumerateArray())
                        {
                            if (contentItem.TryGetProperty("type", out var typeProp) &&
                                typeProp.GetString() == "output_text" &&
                                contentItem.TryGetProperty("text", out var textProp))
                            {
                                resultText = textProp.GetString();
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(resultText))
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(resultText))
            {
                await aiUsageRecorder.CompleteFailureAsync(imageDescriptionUsageId, "ProviderError");
                return new BadRequestObjectResult("No image description returned by AI.");
            }

            await aiUsageRecorder.CompleteOpenAiAsync(imageDescriptionUsageId, jsonResponse, imageDescriptionModel, "WebsiteImageDescription", imageProviderRequestId);

            return new OkObjectResult(resultText.Trim());
        }


        [Function("ExtractWebsiteBackground")]
        public async Task<IActionResult> ExtractWebsiteBackground(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                string referer = req.Headers["Origin"].FirstOrDefault();
                if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
                {
                    return new UnauthorizedResult();
                }

                string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
                var getData = JsonConvert.DeserializeObject<AIImageDescriptionRequest>(requestBodyData);

                if (string.IsNullOrWhiteSpace(getData?.ImageUrl))
                {
                    return new BadRequestObjectResult("Missing image URL.");
                }

                var claimedId = jwtUserResolver.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedId) ? await ResolveLegacyJwtUserIdAsync(req) :
                    await attributionStore.ResolveClaimedUserIdAsync(claimedId);
                var project = string.IsNullOrWhiteSpace(userId) ? null :
                    await attributionStore.GetOwnedGenerationProjectAsync(userId, getData.ProjectId ?? string.Empty);
                if (project is null) return new UnauthorizedResult();
                var quota = await aiUsageSummary!.EvaluateAsync(userId!, "website_generation");
                if (!quota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };

                using var client = new HttpClient();

                var apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);

                // Download the image
                var imageBytes = await client.GetByteArrayAsync(getData.ImageUrl);

                var prompt = @"
Extract the background image that appears inside the computer screen.

Instructions:
- Remove the computer frame, keyboard, and surrounding devices.
- Remove all UI including text, menus, buttons, and icons.
- Reconstruct only the clean background image.
- Extend the scene naturally so it becomes a full standalone background.

Result must be:
clean background only, seamless, high resolution, modern website hero background.
";

                using var form = new MultipartFormDataContent();

                form.Add(new StringContent("gpt-image-1"), "model");
                form.Add(new StringContent(prompt), "prompt");
                form.Add(new StringContent("auto"), "size");

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");

                form.Add(imageContent, "image", "input.png");

                var response = await client.PostAsync(
                    "https://api.openai.com/v1/images/edits",
                    form
                );

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return new BadRequestObjectResult(error);
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

                string imageBase64 = data
                    .GetProperty("data")[0]
                    .GetProperty("b64_json")
                    .GetString();

                return new OkObjectResult(new
                {
                    Image = $"data:image/png;base64,{imageBase64}"
                });
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Exception: {ex.Message}");
            }
        }

        [Function("GenerateWebsiteFromImage")]
        public async Task<IActionResult> GenerateWebsiteFromImage(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                string referer = req.Headers["Origin"].FirstOrDefault();
                if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
                {
                    return new UnauthorizedResult();
                }

                string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
                var getData = JsonConvert.DeserializeObject<AIWebsiteRequest>(requestBodyData);

                if (string.IsNullOrWhiteSpace(getData?.ImageUrl) ||
                    string.IsNullOrWhiteSpace(getData?.Name) ||
                    string.IsNullOrWhiteSpace(getData?.Industry) ||
                    string.IsNullOrWhiteSpace(getData?.Prompt))
                {
                    return new BadRequestObjectResult("Missing required fields.");
                }

                var claimedUserId = jwtUserResolver.ResolveUserId(req);
                var websiteUserId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? null
                    : await attributionStore.ResolveClaimedUserIdAsync(claimedUserId);
                if (string.IsNullOrWhiteSpace(websiteUserId))
                    websiteUserId = await ResolveLegacyJwtUserIdAsync(req);
                if (string.IsNullOrWhiteSpace(websiteUserId) || string.IsNullOrWhiteSpace(getData.ProjectId))
                    return new UnauthorizedResult();
                var websiteProject = await attributionStore.GetOwnedGenerationProjectAsync(websiteUserId, getData.ProjectId);
                if (websiteProject is null) return new UnauthorizedResult();
                var websiteQuota = await aiUsageSummary!.EvaluateAsync(websiteUserId!, "website_generation");
                if (!websiteQuota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };

                var websiteJobId = string.IsNullOrWhiteSpace(getData.GenerationJobId) ? Guid.NewGuid().ToString() : getData.GenerationJobId;
                var websiteUsageId = await aiUsageRecorder!.StartAsync(new AIGenerationUsage
                {
                    UserId = websiteUserId, ProjectId = websiteProject.ProjectId, FunnelId = websiteProject.FunnelId,
                    GenerationJobId = websiteJobId!, ProviderTaskId = websiteJobId, GenerationCategory = "PaidWorkspace",
                    DeliverableType = "Website", Feature = "website_generation", UsageScope = "Paid",
                    GenerationUnits = aiUsageSummary.UnitsFor("website_generation"), Operation = "WebsiteGeneration",
                    Provider = "OpenAI", Model = "gpt-4.1", PricingVersion = AIProviderPricingService.CurrentVersion
                });

                using var client = websiteGenerationClient();

                var apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);

                var prompt = $@"
Analyze the website design visible inside the computer screen in the provided image.

Make the hero image an image called hero-background.png.
Also, use 3 other images in the website called image-1.jpg, image-2.jpg and image-3.jpg.

Generate text for the website for a company name {getData.Name} and industry {getData.Industry} and prompt {getData.Prompt}. Make the hero text based on the company name, industry and prompt.
Make the hero text legible.

Ignore the laptop frame, devices, and surrounding environment.
Focus only on the website layout displayed on the screen.

Recreate the full website layout based on the design, including:

• Page structure (header, navigation, hero section, content sections, footer)
• Typography and color scheme based on the design
• Spacing, alignment, and layout proportions
• UI components such as buttons, cards, images, and sections

Generate clean, modern production-ready code using HTML, CSS, and JavaScript (or React if appropriate).

Requirements:

* The website must be fully responsive for desktop, tablet, and mobile.
* Use modern responsive techniques (Flexbox/Grid, media queries).
* Add smooth animations and transitions that match the style of the design (hover effects, scroll animations, button interactions).
* Implement basic interactive behaviors if visible (menus, sliders, etc.).
* Maintain visual fidelity to the design shown in the image.
* Use semantic HTML and well-structured, readable code.

Output the complete code structure including:

* HTML structure
* CSS styling
* JavaScript for animations and interactions
* Responsive breakpoints 

Only return the code, no extra text.
Return the whole HTML with embedded styles and scripts.
";

                var requestPayload = new
                {
                    model = "gpt-4.1",
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
                                    image_url = getData.ImageUrl
                                }
                            }
                        }
                    }
                };

                var jsonContent = new StringContent(
                    JsonConvert.SerializeObject(requestPayload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await client.PostAsync(
                    "https://api.openai.com/v1/responses",
                    jsonContent
                );

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    await aiUsageRecorder.CompleteFailureAsync(websiteUsageId, response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "InvalidRequest" : "ProviderError");
                    return new BadRequestObjectResult(error);
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);
                var websiteRequestId = response.Headers.TryGetValues("x-request-id", out var websiteRequestIds) ? websiteRequestIds.FirstOrDefault() : null;
                await aiUsageRecorder.CompleteOpenAiAsync(websiteUsageId, jsonResponse, "gpt-4.1", "WebsiteGeneration", websiteRequestId);

                string html = data
                    .GetProperty("output")[0]
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString();

                return new OkObjectResult(new
                {
                    Html = html
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
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Exception: {ex.Message}");
            }
        }

        public class AIWebsiteRequest
        {
            public string ImageUrl { get; set; }
            public string Name { get; set; }
            public string Industry { get; set; }
            public string Prompt { get; set; }
            public string? GenerationJobId { get; set; }
            public string? ProjectId { get; set; }
            public string? FunnelId { get; set; }
        }

        public class AIImageDescriptionRequest
        {
            public string? LandingVariant { get; set; }
            public string ImageUrl { get; set; }
            public string? GenerationJobId { get; set; }
            public string? UserId { get; set; }
            public string? ProjectId { get; set; }
            public string? FunnelId { get; set; }
        }

        public class SocialPostRequest
        {
            public string ImageUrl { get; set; }
            public string BrandName { get; set; }
            public string Platform { get; set; } // e.g., Instagram, Twitter, LinkedIn
            public string Prompt { get; set; } // Optional context or campaign
        }

        public class AIImageRequest
        {
            public string ImageUrl { get; set; }
        }

        [Function("ExtractSocialMediaImage")]
        public async Task<IActionResult> ExtractSocialMediaImage(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                string referer = req.Headers["Origin"].FirstOrDefault();
                if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
                    return new UnauthorizedResult();

                string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
                var getData = JsonConvert.DeserializeObject<AIImageRequest>(requestBodyData);

                if (string.IsNullOrWhiteSpace(getData?.ImageUrl))
                    return new BadRequestObjectResult("Missing image URL.");

                using var client = new HttpClient();
                var apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                // Download the original image
                var imageBytes = await client.GetByteArrayAsync(getData.ImageUrl);

                var prompt = @"
Extract and clean the main subject of this image for a social media post.

Instructions:
- Remove the computer frame, keyboard, and surrounding devices.
- Remove all UI including text, menus, buttons, and icons.
- Reconstruct only the clean background image.
- Extend the scene naturally so it becomes a full standalone background.
- Remove any background clutter or surrounding environment.
- Keep the main subject fully visible.
- Make the background clean, modern, and suitable for Instagram/Twitter/LinkedIn.
- Ensure the image is high-resolution and visually striking.
- Optionally add subtle padding or gradient background for post aesthetic.

Result must be:
clean background only, seamless, high resolution, modern social hero background.
";

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent("gpt-image-1"), "model");
                form.Add(new StringContent(prompt), "prompt");
                form.Add(new StringContent("1024x1024"), "size"); // social media friendly

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");

                form.Add(imageContent, "image", "input.png");

                var response = await client.PostAsync(
                    "https://api.openai.com/v1/images/edits",
                    form
                );

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return new BadRequestObjectResult(error);
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonResponse);

                string imageBase64 = data
                    .GetProperty("data")[0]
                    .GetProperty("b64_json")
                    .GetString();

                return new OkObjectResult(new
                {
                    Image = $"data:image/png;base64,{imageBase64}"
                });
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Exception: {ex.Message}");
            }
        }

        [Function("GenerateSocialPostFromImage")]
        public async Task<IActionResult> GenerateSocialPostFromImage(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                string referer = req.Headers["Origin"].FirstOrDefault();
                if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
                    return new UnauthorizedResult();

                string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
                var getData = JsonConvert.DeserializeObject<SocialPostRequest>(requestBodyData);

                if (string.IsNullOrWhiteSpace(getData?.ImageUrl) ||
                    string.IsNullOrWhiteSpace(getData?.BrandName) ||
                    string.IsNullOrWhiteSpace(getData?.Platform))
                {
                    return new BadRequestObjectResult("Missing required fields.");
                }

                using var client = new HttpClient();
                var apiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                // Prompt tailored for social media post generation
                var prompt = $@"
Analyze the image provided at {getData.ImageUrl}.
Create a high-engagement social media post for {getData.BrandName} to post on {getData.Platform}.
Use the context or theme: {getData.Prompt}.
Include:
- Catchy headline or caption
- Short, compelling text suitable for the platform
- Suggested hashtags (relevant and trending)
- Optional emoji suggestions
Focus on creativity, clarity, and engagement.
Output in JSON format with keys: 'Caption', 'Hashtags', 'Emojis'.
Do not include any extra text outside the JSON.
";

                var requestPayload = new
                {
                    model = "gpt-4.1",
                    input = new object[]
                    {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_text", text = prompt },
                            new { type = "input_image", image_url = getData.ImageUrl }
                        }
                    }
                    }
                };

                var jsonContent = new StringContent(
                    JsonConvert.SerializeObject(requestPayload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await client.PostAsync(
                    "https://api.openai.com/v1/responses",
                    jsonContent
                );

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return new BadRequestObjectResult(error);
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

                string outputText = data
                    .GetProperty("output")[0]
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString();

                return new OkObjectResult(new
                {
                    Content = outputText
                });
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Exception: {ex.Message}");
            }
        }
        private static async Task QueueMessageAsync(
    string queueName,
    string body,
    CancellationToken cancellationToken)
        {
            string connectionString =
                Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                ?? throw new InvalidOperationException(
                    "AzureWebJobsStorage is missing.");

            var queueClient = new QueueClient(
                connectionString,
                queueName);

            await queueClient.CreateIfNotExistsAsync(
                cancellationToken: cancellationToken);

            await queueClient.SendMessageAsync(
                body,
                cancellationToken);
        }
        [Function("RegenerateSceneFromImage")]
        public async Task<IActionResult> RegenerateSceneFromImage(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AIVideoSceneFromImageRequest>(requestBodyData);

            if (string.IsNullOrWhiteSpace(getData?.FullVideoPrompt) ||
                string.IsNullOrWhiteSpace(getData?.ImageBase64) ||
                getData.SceneNumber <= 0)
            {
                return new BadRequestObjectResult("Missing full video prompt, image, or valid scene number.");
            }

            var scenePromptModel = Environment.GetEnvironmentVariable("OpenAISettings:Model") ?? string.Empty;
            var scenePromptUsageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage
            {
                UserId = getData.UserId, ProjectId = getData.ProjectId, FunnelId = getData.FunnelId,
                GenerationJobId = getData.GenerationJobId ?? Guid.NewGuid().ToString(), GenerationCategory = "SocialFunnel",
                DeliverableType = "SocialVideo", Operation = "VideoPromptGeneration", Provider = "OpenAI",
                Model = scenePromptModel, ProviderTaskId = string.IsNullOrWhiteSpace(getData.GenerationJobId) ? null : $"{getData.GenerationJobId}:video-prompt",
                PricingVersion = AIProviderPricingService.CurrentVersion
            });

            // Dynamic AI prompt
            var aiPrompt =
                //$"with this full prompt: {getData.FullVideoPrompt}. " +
                $"Generate a prompt with this image, 500 characters, nothing inappropriate. {getData.FullVideoPrompt} " +
                $"Image description: {getData.ImageBase64}. " +
                $"Make sure there a 2 camera views, an initial comera description then a CUT TO. Make sure it's 500 characters or less. " +
                $"Make like this example prompt: " +
                $"[Camera slow zoom in shallow depth of field] A pink lip stick with water splash behind it and with strawberries floating in space around it, CUT TO close up side angle slow motion shallow depth of field a female hand slowly picking up a strawberry as the water splashes behind it.";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = scenePromptModel,
                messages = new[]
    {
        new { role = "user", content = aiPrompt }
    },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };


            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                await aiUsage.CompleteFailureAsync(scenePromptUsageId, response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "InvalidRequest" : "ProviderError");
                throw new HttpRequestException("Failed to fetch from AI API");
            }

            // Parse response
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);
            var parsedsceneUsage = OpenAIUsageParser.ParseChatCompletions(data, scenePromptModel);
            var sceneProviderRequestId = response.Headers.TryGetValues("x-request-id", out var sceneRequestIds) ? sceneRequestIds.FirstOrDefault() : null;

            var resultText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            await aiUsage.CompleteOpenAiSuccessAsync(scenePromptUsageId, parsedsceneUsage, aiPricing, "VideoPromptGeneration", sceneProviderRequestId);

            return new OkObjectResult(resultText.Replace("\n", " "));
        }

        [Function("RegenerateSceneFromImageInit")]
        public async Task<IActionResult> RegenerateSceneFromImageInit(
[HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<AIVideoSceneFromImageRequest>(requestBodyData);

            if (string.IsNullOrWhiteSpace(getData?.FullVideoPrompt) ||
                string.IsNullOrWhiteSpace(getData?.ImageBase64) ||
                getData.SceneNumber <= 0)
            {
                return new BadRequestObjectResult("Missing full video prompt, image, or valid scene number.");
            }

            // Dynamic AI prompt
            var aiPrompt =
                $"Generate a cinematic prompt for a 5-second vertical social media preview video, 500 characters or less, nothing inappropriate. " +
                $"Campaign concept: {getData.FullVideoPrompt}. " +
                $"Source image description: {getData.ImageBase64}. " +

                $"Create exactly 2 camera shots. " +
                $"Separate the shots using the exact words CUT TO and use CUT TO exactly 1 time. " +

                $"The first shot should be an attention-grabbing hook and subject or product reveal. " +
                $"If mobile phones, tablets, monitors, televisions, or digital screens are present in the source image, show realistic video or animation playing inside their screens during the first shot. " +
                $"The screen content may include social media video, motion graphics, scrolling visuals, app animation, cinematic footage, or other campaign-relevant movement. " +
                $"Keep all animated screen content fully contained inside the display boundaries. " +
                $"Do not allow the video or animation to spill outside, detach from, or distort the screens. " +
                $"Keep the physical devices unchanged in shape, design, proportions, and position. " +

                $"The second shot should transition to a different but related premium scene and should not repeat the same phone composition. " +
                $"It may show a campaign-related subject, product detail, lifestyle moment, abstract digital environment, or premium visual ending. " +

                $"Randomly choose appropriate camera angles, shot types, camera movements, lens styles, framing, lighting, and depth of field for every generated prompt. " +
                $"Do not copy or repeatedly default to the camera setup used in the example. " +
                $"Avoid always using slow push-ins, shallow depth of field, wide hero shots, or slow pullbacks. " +
                $"Choose varied combinations such as macro close-ups, extreme close-ups, top-down views, overhead shots, low angles, high angles, side profiles, three-quarter views, tracking shots, dolly shots, crane shots, orbit shots, handheld shots, static compositions, rack focus, or subtle pans when appropriate. " +
                $"The 2 shots should use visually distinct camera setups while remaining coherent as one short campaign video. " +

                $"Maintain consistent subjects, faces, products, clothing, colors, proportions, environments, lighting continuity, and object details unless the second shot intentionally introduces a different campaign-related scene. " +
                $"Keep motion natural and visually coherent throughout the video. " +

                $"Use realistic physics, believable gravity, and natural object interactions. " +
                $"Avoid morphing, melting, duplicated objects, extra limbs, distorted anatomy, disappearing objects, sudden size changes, flickering, clipping, unrealistic movement, impossible interactions, or screen content escaping its boundaries. " +

                $"Keep each shot simple with one primary visual focus, one clear action, and no more than one subtle environmental effect. " +
                $"Avoid chaotic movement, rapid spinning cameras, extreme rotations, multiple competing actions, or overly complex scenes. " +

                $"Optimize for premium commercial advertising and vertical 9:16 social media video with realistic lighting, natural shadows, clean composition, and clear visual hierarchy. " +

                $"Do not add logos, brand names, labels, watermarks, captions, text overlays, typography, UI elements, or readable text unless explicitly mentioned in the campaign concept or already visible in the source image. " +
                $"Do not invent new branding or unrelated objects not described by the source image or campaign concept. " +

                $"Return only the finished video prompt with no explanation, title, numbering, quotation marks, markdown, or extra text. " +

                $"Example format only. Do not copy its camera angles, movements, lighting, lens style, wording, or scene composition: " +
                $"[Camera slow push-in, shallow depth of field] Mobile phones display vibrant campaign videos playing naturally inside their screens while soft reflections move across the glass, CUT TO " +
                $"[Wide hero shot, cinematic lighting, slow pullback] Campaign-related motion graphics and digital elements move through a premium abstract environment.";

            if (WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant))
                aiPrompt = WebsiteFunnelPromptResolver.Video(getData.FullVideoPrompt, "", getData.ImageBase64);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[]
    {
        new { role = "user", content = aiPrompt }
    },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };


            var promptUsageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage
            {
                UserId = getData.UserId, ProjectId = getData.ProjectId, FunnelId = getData.FunnelId,
                GenerationJobId = getData.GenerationJobId ?? Guid.NewGuid().ToString(),
                GenerationCategory = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? "WebsiteFunnel" : "SocialFunnel",
                DeliverableType = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? "WebsitePreview" : "SocialVideo",
                Feature = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? WebsiteFunnelPromptResolver.Feature : null,
                UsageScope = WebsiteFunnelPromptResolver.IsWebsite(getData.LandingVariant) ? WebsiteFunnelPromptResolver.UsageScope : null,
                Operation = "VideoPromptGeneration", Provider = "OpenAI", Model = requestBody.model ?? string.Empty,
                PricingVersion = AIProviderPricingService.CurrentVersion
            });

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                )
            );

            if (!response.IsSuccessStatusCode)
            {
                await aiUsage.CompleteFailureAsync(promptUsageId, "ProviderError");
                throw new HttpRequestException("Failed to fetch from AI API");
            }

            // Parse response
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);
            var usage = OpenAIUsageParser.ParseChatCompletions(data, requestBody.model ?? string.Empty);
            var providerRequestId = response.Headers.TryGetValues("x-request-id", out var requestIds) ? requestIds.FirstOrDefault() : null;
            await aiUsage.CompleteOpenAiSuccessAsync(promptUsageId, usage, aiPricing, "VideoPromptGeneration", providerRequestId);

            var resultText = data
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                .Trim();

            return new OkObjectResult(resultText.Replace("\n", " "));
        }


        public class AIVideoSceneFromImageRequest
        {
            public string? LandingVariant { get; set; }
            public string FullVideoPrompt { get; set; }
            public int SceneNumber { get; set; }
            public string ImageBase64 { get; set; } // data:image/png;base64,...
            public string? GenerationJobId { get; set; }
            public string? UserId { get; set; }
            public string? ProjectId { get; set; }
            public string? FunnelId { get; set; }
        }

        [Function("GetMainDeliverableCategory")]
        public async Task<IActionResult> GetMainDeliverableCategory([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            // Read and deserialize request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);
            var name = getData.Name;
            var userPrompt = getData.Prompt;

            var q1 = $"What is the primary deliverable category for this prompt? \"{userPrompt}\" " +
                     $"then the other 4 most important deliverable categories " +
                     $"Respond with just the deliverable categories from this list: " +
                     $"Branding Identity & Logo, Print Materials, Social Media Content, Digital Marketing, Web & UX/UI Website, " +
                     $"Video & Photography, Events & Activations, Packaging Label.";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
                    new { role = "user", content = $"{q1}" },
                },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            // Extract response string
            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new OkObjectResult(result);
        }

        [Function("GetCommercialDescription")]
        public async Task<IActionResult> GetCommercialDescription([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read and deserialize request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);
            var name = getData.Name;        // e.g. "Hoverloft"
            var industry = getData.Industry; // e.g. "Drone Photography Marketplace"
            var userPrompt = getData.Prompt; // e.g. "Craft a brand video script that sells the thrill of seeing the world from impossible angles."

            // Build the OpenAI prompt
            //var q1 = $"Create one sentence that describes a commercial for a company named {name} in the {industry} industry. " +
            // $"The sentence should include the word 'commercial' and be based on this prompt: \"{userPrompt}\". " +
            // $"Follow this example format: \"almond milk commercial, this company is proud of their great taste and low calorie.\"";

            var q1 = $"with a company named {name}, in the {industry} industry and a prompt of \"{userPrompt}\" " +
                     $"create a one sentence that describe it for a commercial.  " +
                     $"use the word commercial in it.  follow this as an example:  " +
                     $"\"almond milk commercial, this company is proud of their great taste and low calorie.\"";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
                    new { role = "user", content = q1 },
                },
                max_tokens = 100
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            // Extract response string
            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new OkObjectResult(result);
        }

        [Function("GenerateVideoPrompts")]
        public async Task<IActionResult> GenerateVideoPrompts([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read and deserialize request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var requestData = JsonConvert.DeserializeObject<VideoPromptRequest>(requestBodyData);
            var commercial = requestData.Commercial; // e.g. "luxury perfume"

            // Build the OpenAI prompt based on your requirement
            var q1 = $"With this AI video prompt as an example, generate similar prompts with 500 or less characters for a {commercial} commercial. " +
                $"This is 5 seconds long and has 2 different camera shots. Generate the same for the commercial with camera, lighting, color contrast, and color grade in the prompts. " +
                $"Make it feel cinematic and professional. Make multiple 5-second prompts to amount to 30 seconds total. " +
                $"So 6 prompts. " +
                $"Be creative with what happens in the shots, not necessarily like the example. " +
                $"Include 'CUT TO' in each segment. Make it stunning and highlight the product. " +
                $"Make every person in it white (add to prompt if needed). " +
                $"No text in any segment, no Chinese or other text. " +
                $"Example prompt: " +
                $"[Camera slow zoom in shallow depth of field] A pink lipstick with water splash behind it and " +
                $"with strawberries floating in space around it, CUT TO close up side angle slow motion shallow depth of field " +
                $"a female hand slowly picking up a strawberry as the water splashes behind it.";

            // Setup HttpClient for OpenAI API
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"), // e.g. "gpt-4"
                messages = new[]
                {
                    new { role = "user", content = q1 }
                },
                max_tokens = 1000, // enough for multiple 5-sec prompts
                temperature = 0.9
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            // Extract response string
            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new OkObjectResult(result);
        }

        // Model for request body
        public class VideoPromptRequest
        {
            public string Commercial { get; set; }
        }


        [Function("GetDeliverableDetail")]
        public async Task<IActionResult> GetDeliverableDetail([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            // Read deliverable name from request body
            string deliverable = await new StreamReader(req.Body).ReadToEndAsync();
            deliverable = deliverable.Trim();

            if (string.IsNullOrWhiteSpace(deliverable))
            {
                return new BadRequestObjectResult("Deliverable name is required.");
            }

            var prompt = $"Generate a deliverable for a cretive brief. The deliverable is \"{deliverable}\" and following this template:\n\n" +
                         $"Specify the format, dimensions, quantity and technical requirements for each deliverable." +
                         $"Follow this template:" +
                         $"[Deliverable Name] " +
                         $"Description: [Provide a clear description of the deliverable.] " +
                         $"Example: \"Low-fidelity wireframes outlining the structure and layout of the app interface.\" " +
                         $"Display: [Where will this design be used? e.g., Website, Facebook, Intagrams, social media, packaging, Print magazines etc.] " +
                         $"Format: [Specify the format, e.g., PDF, Figma, Sketch, etc.] " +
                         $"Dimensions/Size: [Specify dimensions, resolution, or file size requirements.] " +
                         $"Example: \"375x812 pixels for iPhone 13.\" " +
                         $"Quantity: [Specify the number of items, always use numbers] " +
                         $"Example: \"5 key screens (e.g., home screen, meal plan dashboard, recipe details, grocery list, and user profile).\" " +
                         $"Technical Requirements: [Include any technical specifications.] " +
                         $"Example: \"Include annotations for functionality and user flow.\" " +
                         $"Make the response one line.";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
            new { role = "user", content = prompt }
        },
                max_tokens = int.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                return new StatusCodeResult((int)response.StatusCode);
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new ContentResult
            {
                Content = result,
                ContentType = "text/plain",
                StatusCode = 200
            };
        }

        [Function("GetCompanyDescription")]
        public async Task<IActionResult> GetCompanyDescription([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate the Origin header
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

            // Parse the request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);
            var companyName = getData.Name;
            var industry = getData.Industry;
            var userPrompt = getData.Prompt;

            // Formulate the question for the AI
            var q1 = $"With a company name \"{companyName}\", industry \"{industry}\", " +
                     $"and the following prompt: \"{userPrompt}\", " +
                     $"what would you say this company does, in 10 to 15 words?";


            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
            new { role = "user", content = $"{q1}" },
        },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            // Extract the answer
            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new OkObjectResult(result);
        }

        [Function("CheckPackageLabelRelevance")]
        public async Task<IActionResult> CheckPackageLabelRelevance([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate the Origin header
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

            // Parse the request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);
            var companyName = getData.Name;
            var industry = getData.Industry;
            var userPrompt = getData.Prompt;
            var companyDescription = getData.CompanyDescription;

            if (!string.IsNullOrEmpty(companyDescription))
            {
                companyDescription = $" and the company description is \"{companyDescription}\", ";
            }

            // Build a question for the AI
            var relevanceCheckPrompt =
                $"With a company name \"{companyName}\", industry \"{industry}\", and prompt \"{userPrompt}\", " +
                companyDescription +
                $"does this company sell physical products? respond with physical:yes/no " +
                $"if yes, do the products need a packages?  respond with package:yes/no, " +
                $"if yes, can the products fit in a small package?  respond with small:yes/no, " +
                $"so the response should be physical:yes/no, package:yes/no and small:yes/no no spaces";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
                    new { role = "user", content = relevanceCheckPrompt },
                },
                max_tokens = 100
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            // Extract the answer
            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new OkObjectResult(result);
        }

        [Function("GetPackageModularFormula")]
        public async Task<IActionResult> GetPackageModularFormula([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Parse the request body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);

            if (getData == null || string.IsNullOrWhiteSpace(getData.Name) ||
                string.IsNullOrWhiteSpace(getData.Industry) || string.IsNullOrWhiteSpace(getData.Prompt))
            {
                return new BadRequestObjectResult("Missing required fields: Name, Industry, Prompt.");
            }

            var companyName = getData.Name;
            var industry = getData.Industry;
            var userPrompt = getData.Prompt;
            var companyDescription = getData.CompanyDescription;

            if (!string.IsNullOrEmpty(companyDescription))
            {
                companyDescription = $", and the company description is: \"{companyDescription}\"";
            }

            // Modular formula prompt
            var modularPrompt =
                $"create a modular formula similar to " +
                $"\"[Render the label design clearly on the appropriate packaging surface(e.g., bottle, box, pouch, jar) " +
                $"in both flat and realistic 3D perspectives.Surround the packaging with relevant props, ingredients, or " +
                $"lifestyle elements that reflect the product's use and market appeal. Ensure material textures, lighting, " +
                $"and branding are consistent and visually polished.]\" but that applies to packaging for a company " +
                $"named {companyName}, in the industry {industry}, " +
                $"and prompt \"{userPrompt}\"" +
                companyDescription;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
            new { role = "user", content = modularPrompt }
        },
                max_tokens = 300
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Failed to fetch from OpenAI API");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var result = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Trim();

            return new OkObjectResult(result);
        }

        [Function("GetAICommercialSceneOrder")]
        public async Task<IActionResult> GetAICommercialSceneOrder(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin
            string referer = req.Headers["Origin"].FirstOrDefault();
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
                return new UnauthorizedResult();

            // Read body
            string requestBodyData = await new StreamReader(req.Body).ReadToEndAsync();
            var getData = JsonConvert.DeserializeObject<OpenAIIdeaRequest>(requestBodyData);

            var company = getData.Name;
            var industry = getData.Industry;
            var items = getData.Items; // List<ImageIdeaItem>

            // Prepare list for LLM
            var deliverableListJson = JsonConvert.SerializeObject(
                items.Select(x => new
                {
                    id = x.Id,
                    countid = x.CountId,
                    subcategory = x.SubCategory,
                    deliverable = x.DeliverableType
                })
            );

            // Prompt
            var q1 =
                $"You are organizing the 6-scene order for a creative commercial video for the company '{company}' in the {industry} industry. " +
                $"Use ONLY the provided list of deliverables below. Each deliverable contains id, countid, deliverable type, and subcategory. " +
                $"RETURN EXACTLY 6 ITEMS.\n\n" +
                $"Rules:\n" +
                $"- ALWAYS start with deliverables related to 'Video & Photography'.\n" +
                $"- ALWAYS end with a deliverable corresponding to a 'Logo' or 'Branding Identity'.\n" +
                $"- Choose the most relevant 6 in proper commercial story order.\n" +
                $"- The output must be a JSON array of objects containing: subcategory, countid, id.\n" +
                $"- If a subcategory is unknown, return 'Generic'.\n" +
                $"- Do NOT describe scenes. Do NOT add text outside the JSON.\n\n" +
                $"Deliverables:\n{deliverableListJson}";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("OpenAISettings:ApiKey"));

            var requestBody = new
            {
                model = Environment.GetEnvironmentVariable("OpenAISettings:Model"),
                messages = new[] {
            new { role = "user", content = q1 }
        },
                max_tokens = Int32.Parse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokensIdea"))
            };

            var response = await client.PostAsync(
                Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl"),
                new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json")
            );

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Failed to fetch from OpenAI API");

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            var content = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();

            // Parse JSON returned by the AI
            List<Dictionary<string, string>> output;
            try
            {
                output = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(content);
            }
            catch
            {
                return new BadRequestObjectResult("AI did not return valid JSON.");
            }

            // 🔥 FIX: Replace "Generic" with DeliverableType (lookup from input list)
            foreach (var entry in output)
            {
                if (entry.TryGetValue("subcategory", out var sc))
                {
                    if (sc != null && sc.Trim().ToLower() == "generic")
                    {
                        if (entry.TryGetValue("id", out var id))
                        {
                            var match = items.FirstOrDefault(x => x.Id == id);
                            if (match != null && !string.IsNullOrWhiteSpace(match.DeliverableType))
                            {
                                entry["subcategory"] = match.DeliverableType;
                            }
                        }
                    }
                }
            }

            return new OkObjectResult(output);
        }

        [Function("CallRunwareAPI")]
        public async Task<IActionResult> CallRunwareAPI([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req)
        {
            // Isolated workers provide the logger through constructor injection, not an invocation parameter.
            var log = _logger;
            // Get the Origin header
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Check if the Origin header exists and if it contains the allowed host URL
            if (referer != null)
            {
                if (!(referer == Environment.GetEnvironmentVariable("HostUrl")))
                {
                    return new UnauthorizedResult(); // Return Unauthorized if the origin is invalid
                }
            }
            else
            {
                return new UnauthorizedResult(); // Return Unauthorized if no Origin header is present
            }

            string apiUrl = Environment.GetEnvironmentVariable("RUNWARE_API_URL");
            string apiKey = Environment.GetEnvironmentVariable("RUNWARE_API_KEY");

            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                log.LogError("Missing environment variables.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string positivePrompt = data?.positivePrompt;
            string? websiteImageIdeaId = data?.websiteImageIdeaId;
            WebsiteImageIdeaResolver.Resolution? websiteImage = null;
            if (websiteImageIdeaId is not null)
            {
                var websiteClaim = jwtUserResolver.ResolveUserId(req);
                var websiteUser = string.IsNullOrWhiteSpace(websiteClaim) ? null
                    : await attributionStore.ResolveClaimedUserIdAsync(websiteClaim);
                if (string.IsNullOrWhiteSpace(websiteUser)) return new UnauthorizedResult();
                string? websiteCid = data?.companyIdeaId;
                string? websiteProjectId = data?.projectId;
                using var imageClient = attributionStore.CreateClient();
                websiteImage = await WebsiteImageIdeaResolver.ResolveAsync(imageClient, websiteUser,
                    websiteCid ?? string.Empty, websiteProjectId ?? string.Empty, websiteImageIdeaId);
                if (websiteImage is null) return new NotFoundObjectResult("The current project's Website Image Idea is unavailable.");
                // The browser's prompt is never authoritative for this path.
                positivePrompt = websiteImage.Idea.ImagePrompt;
                log.LogInformation("[WEBSITE IMAGE] prompt-source=canonical-image-idea");
            }
            string[] finalPromptArr = positivePrompt.Split('|');

            // Remove colon at the start of finalPromptArr[1] if present
            if (finalPromptArr[1].StartsWith(":"))
            {
                finalPromptArr[1] = finalPromptArr[1].Substring(1).TrimStart();
            }

            var random = new Random();
            bool includeLastLine = true; // random.Next(4) == 0; // 25% chance

            var sb = new StringBuilder();

            //IMPORTANT (GET WHAT THE COMPANY DOES with PROMPT):  with a company name Hoverglow Mobility,
            //industry Urban Tech / E-Mobility and
            //prompt “Launch a futuristic campaign that makes clean commuting look rebellious.”
            //what would you say this company does in 10 to 15 words?

            sb.Append($"[make a product page for {finalPromptArr[0]}] ");
            //sb.Append($"[Create social content for a café brand that caters to digital nomads in every timezone.] ");
            sb.Append($"[{finalPromptArr[1]}] | [Main subject fully centered in a Square format (1:1)] ");
            sb.Append($"[company name: {finalPromptArr[2]}] ");
            sb.Append($"[Industry: {finalPromptArr[3]}] ");
            if (finalPromptArr[6] != null && finalPromptArr[6] != "")
            {
                sb.Append($"[{finalPromptArr[6]}]");
            }
            //sb.Append($"[Marrow & Myth crafts myth-inspired jewelry and fashion blending ancient symbolism with avant-garde design.] ");
            sb.Append($"[{finalPromptArr[4]}] ");

            var MainDeliverable = finalPromptArr[5];

            if (includeLastLine)
            {
                if (MainDeliverable.IndexOf("website", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("[Display the creative content clearly within device screens (phones, tablets, browsers, laptops, computers), " +
                              "and surround the devices with relevant contextual items and props that enhance the product narrative. " +
                              "Ensure consistency in lighting, color palette, and thematic cohesion.] ");
                }
                else if (MainDeliverable.IndexOf("social", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("[Display the creative content clearly within device screens (phones, tablets), " +
                              "and surround the devices with relevant contextual items and props that enhance the product narrative. " +
                              "Ensure consistency in lighting, color palette, and thematic cohesion.] ");
                }
                else if (MainDeliverable.IndexOf("digital", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("[Display the creative content within high-performing digital ad formats (e.g., banners, email headers, carousel ads, or sponsored post layouts). Emphasize the core message, product visuals, and brand assets in a clear hierarchy optimized for quick scanning and engagement. Surround the content with device-appropriate UI elements or contextual props relevant to the platform or audience. Include strong call-to-action buttons and ensure readability across sizes. Align visual tone, layout, and styling with campaign goals and the target audience’s mindset.]");
                }
                else if (MainDeliverable.IndexOf("packaging", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (finalPromptArr[7] != null && finalPromptArr[7] != "")
                    {
                        sb.Append(finalPromptArr[7]);
                    }
                    //IMPORTANT: Check if this product would have a package and get the package modular formula from
                    //GPT passing existing data.
                    //sb.Append("[Render the label design clearly on the appropriate packaging surface (e.g., bottle, box, pouch, jar) in both flat and realistic 3D perspectives. Surround the packaging with relevant props, ingredients, or lifestyle elements that reflect the product's use and market appeal. Ensure material textures, lighting, and branding are consistent and visually polished.] ");
                    //sb.Append("[Show the packaging design applied to elegant product containers (e.g., rigid boxes, embossed sleeves, velvet pouches, or branded tags) in both flat and realistic 3D perspectives. Style the scene with symbolic props, textured fabrics, stone surfaces, or lighting that reflects ancient or mythical themes. Include the product subtly—such as jewelry resting inside, on a display tray, or worn by a model—to reinforce the connection between packaging and brand essence. Use refined lighting, elevated materials (e.g., matte black, gold foil, linen texture), and composition that aligns with the brand’s mystical, avant-garde tone.] ");
                }
                else if (MainDeliverable.IndexOf("print", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("[Showcase the creative design clearly on print surfaces (e.g., flyers, posters, brochures, packaging), and place relevant contextual items or props around the print materials to convey purpose and enhance visual storytelling. Use cohesive lighting, texture realism, and a thematically aligned setting.] ");
                }
                else if (MainDeliverable.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("[Showcase the event or activation setup in a realistic, immersive environment, highlighting branded elements such as signage, booths, stages, or interactive installations. Include people engaging with the space naturally, with visible faces and body language that conveys interest, excitement, or interaction. Surround the scene with relevant props, lighting, and environmental details (e.g., décor, venue features, crowd energy) to reflect the brand experience and atmosphere. Ensure the composition captures scale, energy, and purpose clearly and cohesively.] ");
                }
                if ((MainDeliverable.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    sb.Append("");

                }
            }
            else
            {
                if (!(MainDeliverable.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    sb.Append("Present the creative content clearly within its intended medium—whether digital, physical, interactive, or experiential—using realistic context, appropriate formatting, and thoughtful composition. Integrate supporting elements such as props, people, environments, or branding assets that enhance the message, reflect the brand’s tone, and align with the deliverable’s purpose. Maintain visual cohesion through consistent lighting, materials, scale, and aesthetic.");
                }
            }

            var finalPrompt = sb.ToString(); //positivePrompt;



            int width = data?.width ?? 1344;
            int height = data?.height ?? 768;
            int numberResults = data?.numberResults ?? 4;
            if (websiteImage is not null) numberResults = 1;

            if (string.IsNullOrWhiteSpace(positivePrompt))
            {
                return new BadRequestObjectResult("Missing 'positivePrompt' in the request body.");
            }

            string generationJobId = data?.generationJobId ?? Guid.NewGuid().ToString();
            string? projectId = data?.projectId;
            string? funnelId = data?.funnelId;
            string requestedFeature = data?.feature ?? "social_image";
            if (websiteImage is not null) requestedFeature = "gen_secondary_image";
            var feature = requestedFeature is "gen_main_image" or "gen_secondary_image" or "social_image" or "ai_recommendations" or "package_label_image"
                ? requestedFeature
                : "social_image";
            var claimedUserId = jwtUserResolver.ResolveUserId(req);
            var generationUserId = string.IsNullOrWhiteSpace(claimedUserId) ? null : await attributionStore.ResolveClaimedUserIdAsync(claimedUserId);
            var recommendationProject = websiteImage?.Project ?? (!string.IsNullOrWhiteSpace(generationUserId)
                ? await attributionStore.GetOwnedGenerationProjectAsync(generationUserId, projectId ?? string.Empty) : null);
            // Every image on this paid-workspace endpoint uses owned project context.
            // Dashboard projects need not have an acquisition funnel; missing context
            // must reject the request, never turn a paid operation into unmetered work.
            var validContext = recommendationProject is not null;
            if (recommendationProject is not null)
            {
                projectId = recommendationProject.ProjectId;
                funnelId = recommendationProject.FunnelId;
            }
            if (!validContext)
                return new UnauthorizedResult();
            if (validContext)
            {
                var imageQuota = await aiUsageSummary!.EvaluateAsync(generationUserId!, feature);
                if (!imageQuota.Allowed)
                    return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = StatusCodes.Status429TooManyRequests };
            }

            string? usageId = null;
            if (validContext)
            {
                try
                {
                    usageId = await aiUsageRecorder!.StartAsync(new AIGenerationUsage
                    {
                        UserId = generationUserId, ProjectId = projectId, FunnelId = funnelId, GenerationJobId = generationJobId,
                        GenerationCategory = "PaidWorkspace", DeliverableType = "Image", Feature = feature, UsageScope = "Paid",
                        GenerationUnits = aiUsageSummary!.UnitsFor(feature), Operation = "ImageGeneration", Provider = "Runware",
                        Model = "runware:101@1", ProviderTaskId = generationJobId, ImageCount = numberResults, ImageWidth = width, ImageHeight = height
                    });
                }
                catch (AICreditQuotaException) { return new ObjectResult(new { code = "ai_generation_limit_exhausted" }) { StatusCode = 429 }; }
                catch (AICreditDuplicateGenerationException) { return new ConflictObjectResult(new { code = "generation_already_started" }); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "[AI USAGE] Paid generation accounting unavailable");
                    return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
                }
            }
            var payload = new object[]
            {
                new {
                    taskType = "authentication",
                    apiKey = apiKey
                },
                new {
                    taskType = "imageInference",
                    taskUUID = generationJobId,
                    positivePrompt = finalPrompt,
                    width = width,
                    height = height,
                    model = "runware:101@1",
                    numberResults = numberResults,
                    outputType = new[] { "dataURI", "URL" },
                    outputFormat = "JPEG",
                    CFGScale = 3.5,
                    steps = 28,
                    seed = GenerateRandomSeed(),
                    scheduler = "FlowMatchEulerDiscreteScheduler",
                    includeCost = true,
                    outputQuality = 90
                }
            };

            var jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                using var httpClient = runwareImageClient();
                var response = await httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                var result = JsonConvert.DeserializeObject<RunwareResponse>(responseContent);
                if (!response.IsSuccessStatusCode)
                {
                    await aiUsageRecorder!.CompleteFailureAsync(usageId, "ProviderError");
                    return new StatusCodeResult((int)response.StatusCode);
                }
                if (usageId is not null)
                {
                    try
                    {
                        var actualCost = RunwareUsageParser.ImageCost(responseContent);
                        await aiUsageRecorder!.CompleteSuccessAsync(usageId, actualCost);
                    }
                    catch (Exception ex) { log.LogWarning(ex, "[AI USAGE] Runware tracking completion failed"); }
                }
                return new OkObjectResult(result);
            }
            catch (HttpRequestException ex)
            {
                await aiUsageRecorder!.CompleteFailureAsync(usageId, "ProviderError");
                log.LogError($"Error calling Runware API: {ex.Message}");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [Function("CallRunwareVideoAPI")]
        public async Task<IActionResult> CallRunwareVideoAPI(
             [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = null)]
        HttpRequest req,
             FunctionContext context)
        {
            var log = context.GetLogger("CallRunwareVideoAPI");

            // Keep the same origin validation used by the finalize endpoint.
            string origin = req.Headers["Origin"].FirstOrDefault();
            string allowedOrigin = Environment.GetEnvironmentVariable("HostUrl");

            if (string.IsNullOrWhiteSpace(origin) ||
                string.IsNullOrWhiteSpace(allowedOrigin) ||
                !string.Equals(
                    origin.TrimEnd('/'),
                    allowedOrigin.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UnauthorizedResult();
            }

            string apiUrl =
                Environment.GetEnvironmentVariable("RUNWARE_API_URL")
                ?? "https://api.runware.ai/v1";

            string apiKey =
                Environment.GetEnvironmentVariable("RUNWARE_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                log.LogError("RUNWARE_API_KEY is not configured.");

                return new ObjectResult(new
                {
                    success = false,
                    error = "Runware API configuration is missing."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            JObject data;

            try
            {
                using var reader = new StreamReader(req.Body);
                string requestBody = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    return new BadRequestObjectResult(
                        "The request body cannot be empty.");
                }

                data = JObject.Parse(requestBody);
            }
            catch (JsonReaderException ex)
            {
                log.LogWarning(ex, "Invalid JSON request body.");

                return new BadRequestObjectResult(
                    "The request body must contain valid JSON.");
            }

            string positivePrompt =
                data.Value<string>("positivePrompt");

            string model =
                data.Value<string>("model")
                ?? "bytedance:2@2";

            // Match the lower-cost/faster finalize-scene defaults.
            int width =
                data.Value<int?>("width")
                ?? 864;

            int height =
                data.Value<int?>("height")
                ?? 480;

            double duration =
                data.Value<double?>("duration")
                ?? 5.0;

            int numberResults =
                data.Value<int?>("numberResults")
                ?? 1;

            int outputQuality =
                data.Value<int?>("outputQuality")
                ?? 95;

            string outputType =
                data.Value<string>("outputType")
                ?? "URL";

            string outputFormat =
                data.Value<string>("outputFormat")
                ?? "MP4";

            // Preserve this request field for compatibility with existing callers,
            // but Runware submission stays async so we can poll for videoUrl.
            string requestedDeliveryMethod =
                data.Value<string>("deliveryMethod")
                ?? "async";

            bool includeCost =
                data.Value<bool?>("includeCost")
                ?? true;

            if (string.IsNullOrWhiteSpace(positivePrompt))
            {
                return new BadRequestObjectResult(
                    "Missing 'positivePrompt' in the request body.");
            }

            if (width <= 0 || height <= 0)
            {
                return new BadRequestObjectResult(
                    "'width' and 'height' must be greater than zero.");
            }

            if (numberResults <= 0)
            {
                return new BadRequestObjectResult(
                    "'numberResults' must be greater than zero.");
            }

            // Same duration behavior as CallRunwareFinalizeSceneVideo.
            duration = Math.Clamp(duration, 1.2, 12.0);

            // Accept either:
            //
            // "inputs": {
            //   "frameImages": ["https://..."]
            // }
            //
            // or:
            //
            // "frameImages": ["https://..."]
            JToken frameImagesToken =
                data.SelectToken("inputs.frameImages")
                ?? data["frameImages"];

            var frameImages =
                frameImagesToken?.ToObject<List<string>>()
                ?? new List<string>();

            frameImages = frameImages
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .ToList();

            if (frameImages.Count == 0)
            {
                return new BadRequestObjectResult(
                    "At least one frame image URL is required in " +
                    "'inputs.frameImages' or 'frameImages'.");
            }

            string taskUuid =
                Guid.NewGuid().ToString();

            // Keep this payload aligned with CallRunwareFinalizeSceneVideo.
            var videoTask = new
            {
                taskType = "videoInference",
                taskUUID = taskUuid,
                model,
                inputs = new
                {
                    frameImages
                },
                positivePrompt,
                width,
                height,
                duration,
                numberResults,
                outputType,
                outputFormat,
                outputQuality,
                deliveryMethod = "async",
                includeCost
            };

            try
            {
                using var httpClient =
                    new HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(90)
                    };

                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        apiKey);

                string submitJson =
                    JsonConvert.SerializeObject(
                        new object[]
                        {
                        videoTask
                        });

                using var submitContent =
                    new StringContent(
                        submitJson,
                        Encoding.UTF8,
                        "application/json");

                using var submitResponse =
                    await httpClient.PostAsync(
                        apiUrl,
                        submitContent);

                string submitBody =
                    await submitResponse.Content.ReadAsStringAsync();

                JToken submitRunwareResponse =
                    SafeParseJson(submitBody);

                if (!submitResponse.IsSuccessStatusCode)
                {
                    log.LogError(
                        "Runware video request failed. HTTP {Status}. Response: {Response}",
                        (int)submitResponse.StatusCode,
                        submitBody);

                    return new ObjectResult(new
                    {
                        success = false,
                        taskUUID = taskUuid,
                        deliveryMethod = "async",
                        requestedDeliveryMethod,
                        statusCode = (int)submitResponse.StatusCode,
                        runwareResponse = submitRunwareResponse
                    })
                    {
                        StatusCode =
                            (int)submitResponse.StatusCode
                    };
                }

                // If Runware happened to return a completed video immediately,
                // return it without waiting for the first poll.
                var immediateVideoResult =
                    FindVideoResult(submitRunwareResponse);

                if (immediateVideoResult != null)
                {
                    string immediateVideoUrl =
                        immediateVideoResult.Value<string>("videoURL")
                        ?? immediateVideoResult.Value<string>("videoUrl");

                    if (!string.IsNullOrWhiteSpace(immediateVideoUrl))
                    {
                        return new OkObjectResult(new
                        {
                            success = true,
                            taskUUID = taskUuid,

                            // Old response contract fields:
                            deliveryMethod = "async",
                            requestedDeliveryMethod,
                            runwareResponse = submitRunwareResponse,

                            // New/finalized response fields:
                            videoUrl = immediateVideoUrl,
                            videoUUID =
                                immediateVideoResult.Value<string>("videoUUID")
                                ?? string.Empty,
                            cost =
                                immediateVideoResult.Value<double?>("cost")
                        });
                    }
                }

                // Same polling approach as CallRunwareFinalizeSceneVideo.
                var pollDeadline =
                    DateTime.UtcNow.AddMinutes(6);

                JToken latestRunwareResponse =
                    submitRunwareResponse;

                while (DateTime.UtcNow < pollDeadline)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(3));

                    var pollTask =
                        new object[]
                        {
                        new
                        {
                            taskType = "getResponse",
                            taskUUID = taskUuid
                        }
                        };

                    using var pollContent =
                        new StringContent(
                            JsonConvert.SerializeObject(pollTask),
                            Encoding.UTF8,
                            "application/json");

                    using var pollResponse =
                        await httpClient.PostAsync(
                            apiUrl,
                            pollContent);

                    string pollBody =
                        await pollResponse.Content.ReadAsStringAsync();

                    if (!pollResponse.IsSuccessStatusCode)
                    {
                        log.LogWarning(
                            "Runware poll for {TaskUUID} returned HTTP {Status}: {Response}",
                            taskUuid,
                            (int)pollResponse.StatusCode,
                            pollBody);

                        continue;
                    }

                    JToken parsed =
                        SafeParseJson(pollBody);

                    latestRunwareResponse =
                        parsed;

                    var errors =
                        parsed["errors"] as JArray;

                    if (errors != null &&
                        errors.Count > 0)
                    {
                        log.LogError(
                            "Runware task {TaskUUID} failed: {Errors}",
                            taskUuid,
                            errors.ToString(Formatting.None));

                        return new ObjectResult(new
                        {
                            success = false,
                            taskUUID = taskUuid,
                            deliveryMethod = "async",
                            requestedDeliveryMethod,
                            errors,
                            runwareResponse = parsed
                        })
                        {
                            StatusCode =
                                StatusCodes.Status502BadGateway
                        };
                    }

                    var videoResult =
                        FindVideoResult(parsed);

                    if (videoResult != null)
                    {
                        string videoUrl =
                            videoResult.Value<string>("videoURL")
                            ?? videoResult.Value<string>("videoUrl");

                        if (!string.IsNullOrWhiteSpace(videoUrl))
                        {
                            return new OkObjectResult(new
                            {
                                success = true,
                                taskUUID = taskUuid,

                                // Preserve the response fields older callers already
                                // receive from CallRunwareVideoAPI.
                                deliveryMethod = "async",
                                requestedDeliveryMethod,
                                runwareResponse = parsed,

                                // Also expose the completed video just like the
                                // finalize-scene endpoint.
                                videoUrl,
                                videoUUID =
                                    videoResult.Value<string>("videoUUID")
                                    ?? string.Empty,
                                cost =
                                    videoResult.Value<double?>("cost")
                            });
                        }
                    }

                    string status =
                        parsed
                            .SelectToken("data[0].status")
                            ?.Value<string>()
                        ?? string.Empty;

                    log.LogInformation(
                        "Runware video task {TaskUUID} status: {Status}",
                        taskUuid,
                        string.IsNullOrWhiteSpace(status)
                            ? "processing"
                            : status);
                }

                return new ObjectResult(new
                {
                    success = false,
                    taskUUID = taskUuid,
                    deliveryMethod = "async",
                    requestedDeliveryMethod,
                    runwareResponse = latestRunwareResponse,
                    error =
                        "Runware video generation did not finish before the polling timeout."
                })
                {
                    StatusCode =
                        StatusCodes.Status504GatewayTimeout
                };
            }
            catch (TaskCanceledException ex)
            {
                log.LogError(
                    ex,
                    "Runware video request timed out for task {TaskUUID}.",
                    taskUuid);

                return new ObjectResult(new
                {
                    success = false,
                    taskUUID = taskUuid,
                    deliveryMethod = "async",
                    requestedDeliveryMethod,
                    error = "The Runware API request timed out."
                })
                {
                    StatusCode =
                        StatusCodes.Status504GatewayTimeout
                };
            }
            catch (HttpRequestException ex)
            {
                log.LogError(
                    ex,
                    "HTTP error while calling Runware video task {TaskUUID}.",
                    taskUuid);

                return new ObjectResult(new
                {
                    success = false,
                    taskUUID = taskUuid,
                    deliveryMethod = "async",
                    requestedDeliveryMethod,
                    error = "Unable to connect to the Runware API."
                })
                {
                    StatusCode =
                        StatusCodes.Status502BadGateway
                };
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "Unexpected error in Runware video task {TaskUUID}.",
                    taskUuid);

                return new ObjectResult(new
                {
                    success = false,
                    taskUUID = taskUuid,
                    deliveryMethod = "async",
                    requestedDeliveryMethod,
                    error = ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }
        private static JObject FindVideoResult(JToken token)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;

                if (obj["videoURL"] != null ||
                    obj["videoUrl"] != null)
                {
                    return obj;
                }

                foreach (var property in obj.Properties())
                {
                    var found = FindVideoResult(property.Value);

                    if (found != null)
                        return found;
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var child in token.Children())
                {
                    var found = FindVideoResult(child);

                    if (found != null)
                        return found;
                }
            }

            return null;
        }
        private static JToken SafeParseJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return JValue.CreateNull();

            try
            {
                return JToken.Parse(value);
            }
            catch
            {
                return new JObject
                {
                    ["rawResponse"] = value
                };
            }
        }

        [Function("GenerateRunwareVideo")]
        public async Task<IActionResult> GenerateRunwareVideo(
            [HttpTrigger(
        AuthorizationLevel.Function,
        "post",
        Route = null)]
    HttpRequest req)
        {
            try
            {
                string requestBody;

                using (var reader = new StreamReader(req.Body))
                {
                    requestBody =
                        await reader.ReadToEndAsync();
                }

                _logger.LogInformation(
                    "GenerateRunwareVideo received request: {RequestBody}",
                    requestBody);

                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "Request body is empty."
                        });
                }

                RunwareVideoRequest? request;

                try
                {
                    request =
                        System.Text.Json.JsonSerializer
                            .Deserialize<RunwareVideoRequest>(
                                requestBody,
                                new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogError(
                        ex,
                        "Unable to deserialize RunwareVideoRequest.");

                    return new BadRequestObjectResult(
                        new
                        {
                            error = "Invalid request JSON.",
                            details = ex.Message
                        });
                }

                if (request == null)
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "Unable to read the video request."
                        });
                }

                if (string.IsNullOrWhiteSpace(request.FrameImage))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "FrameImage is required."
                        });
                }

                if (string.IsNullOrWhiteSpace(request.PositivePrompt))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "PositivePrompt is required."
                        });
                }

                request.Width ??= 1248;
                request.Height ??= 704;
                request.Duration ??= 5;
                request.NumberResults ??= 1;
                request.OutputQuality ??= 95;
                request.OutputType ??= "URL";
                request.OutputFormat ??= "MP4";
                request.IncludeCost ??= true;
                request.Model ??= "bytedance:2@2";

                _logger.LogInformation(
                    "Submitting Runware video. " +
                    "FrameImage: {FrameImage}, " +
                    "Width: {Width}, " +
                    "Height: {Height}, " +
                    "Duration: {Duration}, " +
                    "Model: {Model}",
                    request.FrameImage,
                    request.Width,
                    request.Height,
                    request.Duration,
                    request.Model);

                /*
                 * Keep your existing Runware submission code here.
                 *
                 * For example, if your existing code produces these values:
                 *
                 * string taskUuid = ...;
                 * object runwareResponse = ...;
                 *
                 * leave that code unchanged and return the result below.
                 */

                string taskUuid =
                    Guid.NewGuid().ToString();

                /*
                 * Replace this temporary object with the response returned by
                 * your existing Runware call.
                 */
                var runwareResponse =
                    new
                    {
                        taskUUID = taskUuid,
                        status = "submitted"
                    };

                return new OkObjectResult(
                    new
                    {
                        taskUUID = taskUuid,
                        status = "submitted",
                        deliveryMethod = "async",
                        runware = runwareResponse
                    });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Runware HTTP request failed.");

                return new ObjectResult(
                    new
                    {
                        error = "Runware HTTP request failed.",
                        details = ex.Message,
                        innerError = ex.InnerException?.Message
                    })
                {
                    StatusCode =
                        StatusCodes.Status502BadGateway
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "GenerateRunwareVideo failed.");

                return new ObjectResult(
                    new
                    {
                        error = "GenerateRunwareVideo failed.",
                        details = ex.Message,
                        innerError = ex.InnerException?.Message,
                        stackTrace = ex.StackTrace
                    })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }

        public class RunwareVideoRequest
        {
            public string? FrameImage { get; set; }

            public string? PositivePrompt { get; set; }

            public int? Width { get; set; }

            public int? Height { get; set; }

            public int? Duration { get; set; }

            public int? NumberResults { get; set; }

            public int? OutputQuality { get; set; }

            public string? OutputType { get; set; }

            public string? OutputFormat { get; set; }

            public bool? IncludeCost { get; set; }

            public string? Model { get; set; }
        }

        [Function("GenerateAIVideo")]
        public async Task<IActionResult> GenerateAIVideo(
                [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = "runware/video")]
        HttpRequest req,
                CancellationToken cancellationToken)
        {
            /*
             * 1. Validate Origin
             */
            string? origin = req.Headers["Origin"].FirstOrDefault();
            string? allowedHostUrl =
                Environment.GetEnvironmentVariable("HostUrl");

            if (string.IsNullOrWhiteSpace(origin) ||
                string.IsNullOrWhiteSpace(allowedHostUrl) ||
                !string.Equals(
                    origin,
                    allowedHostUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UnauthorizedResult();
            }

            /*
             * 2. Get Azure Storage connection
             */
            string? storageConnectionString =
                Environment.GetEnvironmentVariable("AzureWebJobsStorage");

            if (string.IsNullOrWhiteSpace(storageConnectionString))
            {
                _logger.LogError(
                    "AzureWebJobsStorage is not configured.");

                return new ObjectResult(new
                {
                    error = "Azure Storage is not configured."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            /*
             * 3. Read and deserialize request
             */
            string requestBody;

            using (var reader = new StreamReader(req.Body))
            {
                requestBody = await reader.ReadToEndAsync(
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new BadRequestObjectResult(new
                {
                    error = "The request body is required."
                });
            }

            GenerateVideoRequest? request;

            try
            {
                request =
                    JsonConvert.DeserializeObject<GenerateVideoRequest>(
                        requestBody);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid AI video request JSON.");

                return new BadRequestObjectResult(new
                {
                    error = "The request body contains invalid JSON."
                });
            }

            if (request == null)
            {
                return new BadRequestObjectResult(new
                {
                    error = "The request body could not be parsed."
                });
            }

            /*
             * 4. Validate and normalize request
             */
            IActionResult? validationResult =
                ValidateVideoRequest(request);

            if (validationResult != null)
            {
                return validationResult;
            }

            string taskUuid = string.IsNullOrWhiteSpace(request.TaskUUID)
                ? Guid.NewGuid().ToString()
                : request.TaskUUID.Trim();

            request.TaskUUID = taskUuid;
            request.Model = string.IsNullOrWhiteSpace(request.Model)
                ? "bytedance:2@2"
                : request.Model.Trim();

            request.Width ??= 1248;
            request.Height ??= 704;
            request.Duration ??= 2;
            request.NumberResults ??= 1;
            request.OutputType ??= "URL";
            request.OutputFormat ??= "MP4";
            request.OutputQuality ??= 95;
            request.DeliveryMethod ??= "async";
            request.IncludeCost ??= true;

            var queuedJob = new VideoQueueJob
            {
                JobId = Guid.NewGuid().ToString(),
                TaskUUID = taskUuid,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Status = VideoJobStatuses.Queued,
                Request = request
            };

            /*
             * 5. Put the job on Azure Queue Storage
             */
            try
            {
                var queueClient = new QueueClient(
                    storageConnectionString,
                    VideoJobsQueueName);

                await queueClient.CreateIfNotExistsAsync(
                    cancellationToken: cancellationToken);

                string queueMessage =
                    JsonConvert.SerializeObject(queuedJob);

                await queueClient.SendMessageAsync(
                    queueMessage,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to enqueue video job {JobId}.",
                    queuedJob.JobId);

                return new ObjectResult(new
                {
                    error = "The video job could not be queued.",
                    jobId = queuedJob.JobId,
                    taskUUID = taskUuid
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            _logger.LogInformation(
                "Queued video job {JobId} with Runware task UUID {TaskUUID}.",
                queuedJob.JobId,
                taskUuid);

            /*
             * HTTP 202 means accepted for background processing.
             */
            return new ObjectResult(new
            {
                message = "The video generation job was queued.",
                jobId = queuedJob.JobId,
                taskUUID = taskUuid,
                status = VideoJobStatuses.Queued
            })
            {
                StatusCode = StatusCodes.Status202Accepted
            };
        }

        /*
         * QUEUE WORKER
         *
         * Azure Functions invokes this whenever a message is added to
         * the "video-jobs" queue.
         *
         * Do not catch and suppress processing errors here. Throwing the
         * exception lets Azure retry the queue message.
         */
        [Function("ProcessAIVideoJob")]
        public async Task ProcessAIVideoJob(
            [QueueTrigger(
            VideoJobsQueueName,
            Connection = "AzureWebJobsStorage")]
        string queueMessage,
            FunctionContext context,
            CancellationToken cancellationToken)
        {
            VideoQueueJob? job;

            try
            {
                job = JsonConvert.DeserializeObject<VideoQueueJob>(
                    queueMessage);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "The video queue message contains invalid JSON.");

                /*
                 * Throw so Azure applies its retry/poison-queue behavior.
                 */
                throw;
            }

            if (job?.Request == null)
            {
                throw new InvalidOperationException(
                    "The queued video job does not contain a request.");
            }

            _logger.LogInformation(
                "Processing video job {JobId}; Runware task UUID {TaskUUID}.",
                job.JobId,
                job.TaskUUID);

            string? apiUrl =
                Environment.GetEnvironmentVariable("RUNWARE_API_URL");

            string? apiKey =
                Environment.GetEnvironmentVariable("RUNWARE_API_KEY");

            if (string.IsNullOrWhiteSpace(apiUrl) ||
                string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "RUNWARE_API_URL or RUNWARE_API_KEY is not configured.");
            }

            JObject videoTask = CreateRunwareVideoTask(job.Request);

            var payload = new JArray
        {
            new JObject
            {
                ["taskType"] = "authentication",
                ["apiKey"] = apiKey
            },
            videoTask
        };

            string jsonPayload = payload.ToString(Formatting.None);

            using var content = new StringContent(
                jsonPayload,
                Encoding.UTF8,
                "application/json");

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            using HttpResponseMessage response =
                await httpClient.PostAsync(
                    apiUrl,
                    content,
                    cancellationToken);

            string responseContent =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Runware rejected video job {JobId}. " +
                    "Status: {StatusCode}. Response: {Response}",
                    job.JobId,
                    (int)response.StatusCode,
                    responseContent);

                /*
                 * Throwing is important. It tells Azure that processing failed,
                 * allowing the queue message to be retried.
                 */
                throw new HttpRequestException(
                    $"Runware returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            JToken? runwareResponse;

            try
            {
                runwareResponse =
                    JsonConvert.DeserializeObject<JToken>(
                        responseContent);
            }
            catch (System.Text.Json.JsonException)
            {
                runwareResponse = responseContent;
            }

            /*
             * At this point, Runware has accepted the async video task.
             *
             * Save this information to your database here. For example:
             *
             * await _videoJobRepository.MarkSubmittedAsync(
             *     job.JobId,
             *     job.TaskUUID,
             *     runwareResponse,
             *     cancellationToken);
             */
            _logger.LogInformation(
                "Runware accepted video job {JobId}. " +
                "Task UUID: {TaskUUID}. Response: {Response}",
                job.JobId,
                job.TaskUUID,
                runwareResponse?.ToString(Formatting.None));
        }

        public static class VideoJobStatuses
        {
            public const string Queued = "queued";
            public const string Submitted = "submitted";
            public const string Completed = "completed";
            public const string Failed = "failed";
        }

        public sealed class VideoQueueJob
        {
            [JsonProperty("jobId")]
            public string JobId { get; set; } = string.Empty;

            [JsonProperty("taskUUID")]
            public string TaskUUID { get; set; } = string.Empty;

            [JsonProperty("createdAtUtc")]
            public DateTimeOffset CreatedAtUtc { get; set; }

            [JsonProperty("status")]
            public string Status { get; set; } = VideoJobStatuses.Queued;

            [JsonProperty("request")]
            public GenerateVideoRequest? Request { get; set; }
        }

        public sealed class GenerateVideoRequest
        {
            [JsonProperty("model")]
            public string? Model { get; set; }

            [JsonProperty("inputs")]
            public GenerateVideoInputs? Inputs { get; set; }

            [JsonProperty("positivePrompt")]
            public string? PositivePrompt { get; set; }

            [JsonProperty("negativePrompt")]
            public string? NegativePrompt { get; set; }

            [JsonProperty("width")]
            public int? Width { get; set; }

            [JsonProperty("height")]
            public int? Height { get; set; }

            [JsonProperty("duration")]
            public int? Duration { get; set; }

            [JsonProperty("numberResults")]
            public int? NumberResults { get; set; }

            [JsonProperty("outputType")]
            public string? OutputType { get; set; }

            [JsonProperty("outputFormat")]
            public string? OutputFormat { get; set; }

            [JsonProperty("outputQuality")]
            public int? OutputQuality { get; set; }

            [JsonProperty("deliveryMethod")]
            public string? DeliveryMethod { get; set; }

            [JsonProperty("includeCost")]
            public bool? IncludeCost { get; set; }

            [JsonProperty("taskUUID")]
            public string? TaskUUID { get; set; }

            [JsonProperty("seed")]
            public long? Seed { get; set; }

            [JsonProperty("fps")]
            public int? Fps { get; set; }
        }

        public sealed class GenerateVideoInputs
        {
            [JsonProperty("frameImages")]
            public string[]? FrameImages { get; set; }
        }

        private static JObject CreateRunwareVideoTask(
            GenerateVideoRequest request)
        {
            var videoTask = new JObject
            {
                ["taskType"] = "videoInference",
                ["taskUUID"] = request.TaskUUID,
                ["model"] = request.Model,
                ["positivePrompt"] = request.PositivePrompt?.Trim(),
                ["width"] = request.Width,
                ["height"] = request.Height,
                ["duration"] = request.Duration,
                ["numberResults"] = request.NumberResults,
                ["outputType"] = request.OutputType,
                ["outputFormat"] = request.OutputFormat,
                ["outputQuality"] = request.OutputQuality,
                ["deliveryMethod"] = request.DeliveryMethod,
                ["includeCost"] = request.IncludeCost
            };

            string[] frameImages =
                request.Inputs?.FrameImages?
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Select(url => url.Trim())
                    .ToArray()
                ?? Array.Empty<string>();

            if (frameImages.Length > 0)
            {
                videoTask["inputs"] = new JObject
                {
                    ["frameImages"] = JArray.FromObject(frameImages)
                };
            }

            if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
            {
                videoTask["negativePrompt"] =
                    request.NegativePrompt.Trim();
            }

            if (request.Seed.HasValue)
            {
                videoTask["seed"] = request.Seed.Value;
            }

            if (request.Fps.HasValue)
            {
                videoTask["fps"] = request.Fps.Value;
            }

            return videoTask;
        }

        private static IActionResult? ValidateVideoRequest(
            GenerateVideoRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.PositivePrompt))
            {
                return new BadRequestObjectResult(new
                {
                    error = "Missing 'positivePrompt' in the request body."
                });
            }

            if (request.Width.HasValue && request.Width <= 0)
            {
                return new BadRequestObjectResult(new
                {
                    error = "'width' must be greater than zero."
                });
            }

            if (request.Height.HasValue && request.Height <= 0)
            {
                return new BadRequestObjectResult(new
                {
                    error = "'height' must be greater than zero."
                });
            }

            if (request.Duration.HasValue && request.Duration <= 0)
            {
                return new BadRequestObjectResult(new
                {
                    error = "'duration' must be greater than zero."
                });
            }

            if (request.NumberResults.HasValue &&
                request.NumberResults <= 0)
            {
                return new BadRequestObjectResult(new
                {
                    error = "'numberResults' must be greater than zero."
                });
            }

            if (request.OutputQuality.HasValue &&
                request.OutputQuality is < 1 or > 100)
            {
                return new BadRequestObjectResult(new
                {
                    error = "'outputQuality' must be between 1 and 100."
                });
            }

            return null;
        }


        [Function("CallRunwareAPIInit")]
        public async Task<IActionResult> CallRunwareAPIInit(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            // Isolated worker invocations may not supply an invocation logger.
            // Keep provider fallback operational by using the injected logger.
            log ??= _logger;

            // Validate Origin
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

            string apiUrl = Environment.GetEnvironmentVariable("RUNWARE_API_URL");
            string apiKey = Environment.GetEnvironmentVariable("RUNWARE_API_KEY");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string positivePrompt = data?.positivePrompt;

            if (string.IsNullOrWhiteSpace(positivePrompt))
            {
                return new BadRequestObjectResult("Missing 'positivePrompt' in the request body.");
            }

            bool useModelsLabPrimary = data?.useModelsLabPrimary == true;
            if (!useModelsLabPrimary && (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey)))
            {
                log.LogError("Missing environment variables.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
            if (useModelsLabPrimary && string.IsNullOrWhiteSpace(apiUrl)) apiUrl = "https://api.runware.ai/v1";

            //
            // ----------------------------
            // SPLIT MAIN SECTIONS
            // ----------------------------
            //
            var parts = positivePrompt.Split(
                new string[] { "STYLE DIRECTIVE:", "IMPORTANT:", "PROJECT:", "OUTPUT REQUIREMENTS:" },
                StringSplitOptions.None);

            string styleSection = parts.ElementAtOrDefault(1)?.Trim() ?? "";
            string importantSection = parts.ElementAtOrDefault(2)?.Trim() ?? "";
            string projectSectionRaw = parts.ElementAtOrDefault(3)?.Trim() ?? "";
            string outputSection = parts.Length > 4 ? parts[4].Trim() : "";

            //
            // SPLIT PROJECT BY |
            //
            string[] projectArr = projectSectionRaw.Split('|');

            string projectName = projectArr.ElementAtOrDefault(0)?.Trim();
            string projectPrompt = projectArr.ElementAtOrDefault(1)?.Trim();
            string companyName = projectArr.ElementAtOrDefault(2)?.Trim();
            string industry = projectArr.ElementAtOrDefault(3)?.Trim();
            string deliverable = projectArr.ElementAtOrDefault(5)?.Trim();
            string optionalExtra = projectArr.ElementAtOrDefault(6)?.Trim();
            string packagingExtra = projectArr.ElementAtOrDefault(7)?.Trim();

            var random = new Random();
            bool includeLastLine = true;

            var sb = new StringBuilder();

            //
            // ============================
            // 1. STYLE DIRECTIVE
            // ============================
            //
            sb.AppendLine("STYLE DIRECTIVE:");
            sb.AppendLine(styleSection);
            sb.AppendLine();

            //
            // ============================
            // 2. IMPORTANT
            // ============================
            //
            sb.AppendLine("IMPORTANT:");
            sb.AppendLine(importantSection);
            sb.AppendLine();

            //
            // ============================
            // 3. PROJECT
            // ============================
            //
            sb.AppendLine("PROJECT:");

            if (!string.IsNullOrWhiteSpace(projectName))
                sb.Append($"[make a product page for {projectName}] ");

            if (!string.IsNullOrWhiteSpace(projectPrompt))
                sb.Append($"[{projectPrompt}] | ");

            sb.Append("[Main subject fully centered in a Square format (1:1)] ");

            if (!string.IsNullOrWhiteSpace(companyName))
                sb.Append($"[company name: {companyName}] ");

            if (!string.IsNullOrWhiteSpace(industry))
                sb.Append($"[Industry: {industry}] ");

            if (!string.IsNullOrWhiteSpace(optionalExtra))
                sb.Append($"[{optionalExtra}] ");

            if (!string.IsNullOrWhiteSpace(deliverable))
                sb.Append($"[{deliverable}] ");

            //
            // CONDITIONAL PROJECT EXTENSIONS
            //
            if (includeLastLine && !string.IsNullOrWhiteSpace(deliverable))
            {
                if (deliverable.Contains("website", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("[Display the creative content clearly within device screens (phones, tablets, browsers, laptops, computers), and surround the devices with relevant contextual items and props that enhance the product narrative. Ensure consistency in lighting, color palette, and thematic cohesion.] ");
                }
                else if (deliverable.Contains("social", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("[Display the creative content clearly within device screens (phones, tablets), and surround the devices with relevant contextual items and props that enhance the product narrative. Ensure consistency in lighting, color palette, and thematic cohesion.] ");
                }
                else if (deliverable.Contains("digital", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("[Display the creative content within high-performing digital ad formats (e.g., banners, email headers, carousel ads, or sponsored post layouts). Emphasize the core message, product visuals, and brand assets in a clear hierarchy optimized for quick scanning and engagement. Surround the content with device-appropriate UI elements or contextual props relevant to the platform or audience. Include strong call-to-action buttons and ensure readability across sizes. Align visual tone, layout, and styling with campaign goals and the target audience’s mindset.]");
                }
                else if (deliverable.Contains("packaging", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(packagingExtra))
                    {
                        sb.Append(packagingExtra + " ");
                    }
                }
                else if (deliverable.Contains("print", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("[Showcase the creative design clearly on print surfaces (e.g., flyers, posters, brochures, packaging), and place relevant contextual items or props around the print materials to convey purpose and enhance visual storytelling. Use cohesive lighting, texture realism, and a thematically aligned setting.] ");
                }
                else if (deliverable.Contains("event", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("[Showcase the event or activation setup in a realistic, immersive environment, highlighting branded elements such as signage, booths, stages, or interactive installations. Include people engaging with the space naturally, with visible faces and body language that conveys interest, excitement, or interaction. Surround the scene with relevant props, lighting, and environmental details (e.g., décor, venue features, crowd energy) to reflect the brand experience and atmosphere. Ensure the composition captures scale, energy, and purpose clearly and cohesively.] ");
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(deliverable) &&
                    !deliverable.Contains("logo", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("Present the creative content clearly within its intended medium—whether digital, physical, interactive, or experiential—using realistic context, appropriate formatting, and thoughtful composition. Integrate supporting elements such as props, people, environments, or branding assets that enhance the message, reflect the brand’s tone, and align with the deliverable’s purpose. Maintain visual cohesion through consistent lighting, materials, scale, and aesthetic.");
                }
            }

            //
            // ============================
            // 4. OUTPUT REQUIREMENTS
            // ============================
            //
            sb.AppendLine();
            sb.AppendLine("OUTPUT REQUIREMENTS:");
            sb.AppendLine(outputSection);

            string finalPrompt = sb.ToString();
            bool websiteIntro = WebsiteFunnelPromptResolver.IsWebsite((string?)data?.landingVariant);
            if (websiteIntro) finalPrompt = positivePrompt;

            //
            // REQUEST SETTINGS
            //
            int width = data?.width ?? 1344;
            int height = data?.height ?? 768;
            int numberResults = data?.numberResults ?? 4;

            string generationJobId = data?.generationJobId ?? Guid.NewGuid().ToString();
            string? projectId = data?.projectId; string? funnelId = data?.funnelId;
            var claimedImageUserId = jwtUserResolver.ResolveUserId(req);
            var imageUserId = string.IsNullOrWhiteSpace(claimedImageUserId) ? null : await attributionStore.ResolveClaimedUserIdAsync(claimedImageUserId);
            if (string.IsNullOrWhiteSpace(imageUserId) || string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(funnelId) || !await attributionStore.ValidateGenerationContextAsync(imageUserId, projectId, funnelId)) return new UnauthorizedResult();

            bool IsUsableImage(RunwareImageData? image) =>
                image != null && Uri.TryCreate(image.ImageURL, UriKind.Absolute, out var imageUri) &&
                (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps);

            RunwareResponse? KeepUsableImages(RunwareResponse? response, int maxCount)
            {
                var usableImages = response?.Data?.Where(IsUsableImage).Take(maxCount).ToList();
                return usableImages == null || usableImages.Count == 0
                    ? null
                    : new RunwareResponse { Data = usableImages };
            }

            RunwareResponse? MergeProviderImages(RunwareResponse? first, RunwareResponse? second)
            {
                var merged = new List<RunwareImageData>();
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var image in (first?.Data ?? new List<RunwareImageData>())
                    .Concat(second?.Data ?? new List<RunwareImageData>()))
                {
                    if (IsUsableImage(image) && seenUrls.Add(image.ImageURL))
                    {
                        merged.Add(image);
                        if (merged.Count == numberResults) break;
                    }
                }

                return merged.Count == 0 ? null : new RunwareResponse { Data = merged };
            }

            async Task<RunwareResponse?> TryRunwareForDesignerIntroAsync(CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiUrl))
                {
                    log.LogWarning("Runware is not configured for DesignerIntro job {GenerationJobId}.", generationJobId);
                    return null;
                }

                var usageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage
                {
                    UserId = imageUserId, ProjectId = projectId, FunnelId = funnelId,
                    GenerationJobId = generationJobId + ":runware", GenerationCategory = websiteIntro ? "WebsiteFunnel" : "SocialFunnel",
                    DeliverableType = websiteIntro ? "Website" : "SocialMediaContent", Feature = websiteIntro ? WebsiteFunnelPromptResolver.Feature : null,
                    UsageScope = websiteIntro ? WebsiteFunnelPromptResolver.UsageScope : null, Operation = "ImageGeneration",
                    Provider = "Runware", Model = "runware:101@1", ProviderTaskId = generationJobId,
                    ImageCount = numberResults, ImageWidth = width, ImageHeight = height
                });
                try
                {
                    var payload = new object[]
                    {
                        new { taskType = "authentication", apiKey },
                        new
                        {
                            taskType = "imageInference", taskUUID = generationJobId, positivePrompt = finalPrompt,
                            width, height, model = "runware:101@1", numberResults,
                            outputType = new[] { "dataURI", "URL" }, outputFormat = "JPEG", CFGScale = 3.5,
                            steps = 28, seed = GenerateRandomSeed(), scheduler = "FlowMatchEulerDiscreteScheduler",
                            includeCost = true, outputQuality = 90
                        }
                    };
                    using var client = runwareImageClient();
                    client.Timeout = TimeSpan.FromSeconds(90);
                    using var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(apiUrl, content, cancellationToken);
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var result = response.IsSuccessStatusCode
                        ? KeepUsableImages(JsonConvert.DeserializeObject<RunwareResponse>(responseBody), numberResults)
                        : null;
                    if (result == null)
                    {
                        await aiUsage.CompleteFailureAsync(usageId, "ProviderError");
                        log.LogWarning("Runware did not return a usable image for DesignerIntro job {GenerationJobId}; ModelsLab may take over.", generationJobId);
                        return null;
                    }
                    await aiUsage.CompleteSuccessAsync(usageId, actualCost: RunwareUsageParser.ImageCost(responseBody), rawUsageReference: "runware-task-id");
                    log.LogInformation("DesignerIntro image generated by Runware for job {GenerationJobId}.", generationJobId);
                    return result;
                }
                catch (Exception ex)
                {
                    await aiUsage.CompleteFailureAsync(usageId, "ProviderError");
                    log.LogWarning("Runware failed for DesignerIntro job {GenerationJobId}. Failure type: {FailureType}.", generationJobId, ex.GetType().Name);
                    return null;
                }
            }

            async Task<RunwareResponse?> TryModelsLabForDesignerIntroAsync(string modelsLabKey, int requestedCount, CancellationToken cancellationToken)
            {
                var usageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage
                {
                    UserId = imageUserId, ProjectId = projectId, FunnelId = funnelId,
                    GenerationJobId = generationJobId + ":modelslab", GenerationCategory = websiteIntro ? "WebsiteFunnel" : "SocialFunnel",
                    DeliverableType = websiteIntro ? "Website" : "SocialMediaContent", Feature = websiteIntro ? WebsiteFunnelPromptResolver.Feature : null,
                    UsageScope = websiteIntro ? WebsiteFunnelPromptResolver.UsageScope : null, Operation = "ImageGeneration",
                    Provider = "ModelsLab", Model = "flux", ProviderTaskId = generationJobId,
                    ImageCount = requestedCount, ImageWidth = 1024, ImageHeight = 576
                });

                List<string> ReadOutputUrls(string json)
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.TryGetProperty("output", out var outputValue) && outputValue.ValueKind == JsonValueKind.Array
                        ? outputValue.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                            .Select(url => url!).ToList()
                        : new List<string>();
                }

                try
                {
                    using var client = runwareImageClient();
                    client.Timeout = TimeSpan.FromSeconds(90);
                    using var response = await client.PostAsJsonAsync("https://modelslab.com/api/v6/images/text2img", new
                    {
                        key = modelsLabKey, model_id = "flux", prompt = finalPrompt,
                        samples = requestedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        negative_prompt = "(worst quality:2), (low quality:2), (normal quality:2), (jpeg artifacts), (blurry), (duplicate), (morbid), (mutilated), (out of frame), (extra limbs), (bad anatomy), (disfigured), (deformed), (cross-eye), (glitch), (oversaturated), (overexposed), (underexposed), (bad proportions), (bad hands), (bad feet), (cloned face), (long neck), (missing arms), (missing legs), (extra fingers), (fused fingers), (poorly drawn hands), (poorly drawn face), (mutation), (deformed eyes), watermark, text, logo, signature, grainy, tiling, censored, nsfw, ugly, blurry eyes, noisy image, bad lighting, unnatural skin, asymmetry",
                        width = "1024", height = "576", clip_skip = "1", enhance_prompt = false,
                        guidance_scale = "7.5", safety_checker = false, watermark = "no", base64 = "no", seed = "0",
                        num_inference_steps = "20", safety_checker_type = (string?)null, webhook = (string?)null,
                        track_id = (string?)null, lora_model = Array.Empty<string>(), lora_strength = "1"
                    }, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        await aiUsage.CompleteFailureAsync(usageId, "ProviderError");
                        log.LogWarning("ModelsLab returned HTTP {StatusCode} for DesignerIntro job {GenerationJobId}.", (int)response.StatusCode, generationJobId);
                        return null;
                    }

                    var output = ReadOutputUrls(body);
                    string? fetchId = null;
                    if (output.Count == 0)
                    {
                        using var initial = JsonDocument.Parse(body);
                        var root = initial.RootElement;
                        if (root.TryGetProperty("status", out var status) && string.Equals(status.GetString(), "processing", StringComparison.OrdinalIgnoreCase) &&
                            root.TryGetProperty("id", out var id)) fetchId = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
                    }
                    for (var poll = 0; output.Count < requestedCount && !string.IsNullOrWhiteSpace(fetchId) && poll < 20; poll++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                        using var fetch = await client.PostAsJsonAsync($"https://modelslab.com/api/v6/images/fetch/{Uri.EscapeDataString(fetchId)}", new { key = modelsLabKey }, cancellationToken);
                        body = await fetch.Content.ReadAsStringAsync(cancellationToken);
                        if (!fetch.IsSuccessStatusCode) break;
                        output = ReadOutputUrls(body);
                        using var fetchDocument = JsonDocument.Parse(body);
                        if (fetchDocument.RootElement.TryGetProperty("status", out var fetchStatus) && string.Equals(fetchStatus.GetString(), "error", StringComparison.OrdinalIgnoreCase)) break;
                    }

                    if (output.Count < requestedCount)
                    {
                        await aiUsage.CompleteFailureAsync(usageId, "InvalidProviderResponse");
                        log.LogWarning("ModelsLab returned {ImageCount} of {RequestedCount} requested usable images for DesignerIntro job {GenerationJobId}.", output.Count, requestedCount, generationJobId);
                        if (output.Count == 0) return null;
                        // Keep partial results available so the orchestration can merge them with Runware.
                        return new RunwareResponse
                        {
                            Data = output.Take(requestedCount).Select((url, index) => new RunwareImageData
                            {
                                TaskType = "imageInference", TaskUUID = generationJobId,
                                ImageUUID = $"modelslab-{generationJobId}-{index + 1}", ImageURL = url
                            }).ToList()
                        };
                    }
                    await aiUsage.CompleteSuccessAsync(usageId, rawUsageReference: "modelslab-output");
                    log.LogInformation("DesignerIntro image generated by ModelsLab for job {GenerationJobId}.", generationJobId);
                    return new RunwareResponse
                    {
                        Data = output.Select((url, index) => new RunwareImageData
                        {
                            TaskType = "imageInference", TaskUUID = generationJobId,
                            ImageUUID = $"modelslab-{generationJobId}-{index + 1}", ImageURL = url
                        }).ToList()
                    };
                }
                catch (Exception ex)
                {
                    await aiUsage.CompleteFailureAsync(usageId, "ProviderError");
                    log.LogWarning("ModelsLab failed for DesignerIntro job {GenerationJobId}. Failure type: {FailureType}.", generationJobId, ex.GetType().Name);
                    return null;
                }
            }

            // DesignerIntro uses a short hedge: Runware gets a 1.5 second head start,
            // then ModelsLab starts in parallel if Runware has not returned a usable image.
            if (useModelsLabPrimary)
            {
                using var hedgeCancellation = CancellationTokenSource.CreateLinkedTokenSource(req.HttpContext.RequestAborted);
                var runwareTask = TryRunwareForDesignerIntroAsync(hedgeCancellation.Token);
                var runwareHeadStart = await Task.WhenAny(runwareTask,
                    Task.Delay(TimeSpan.FromMilliseconds(1500), req.HttpContext.RequestAborted));
                string modelsLabKey = Environment.GetEnvironmentVariable("MODELSLAB_API_KEY")
    ?? _configuration?["MODELSLAB_API_KEY"]
    ?? _configuration?["Values:MODELSLAB_API_KEY"];
                if (runwareHeadStart == runwareTask)
                {
                    var runwareResult = await runwareTask;
                    if ((runwareResult?.Data?.Count ?? 0) >= numberResults) return new OkObjectResult(runwareResult);


                    if (!string.IsNullOrWhiteSpace(modelsLabKey))
                    {
                        var missingCount = numberResults - (runwareResult?.Data?.Count ?? 0);
                        var modelsLabResult = await TryModelsLabForDesignerIntroAsync(modelsLabKey, missingCount, hedgeCancellation.Token);
                        var merged = MergeProviderImages(runwareResult, modelsLabResult);
                        if ((merged?.Data?.Count ?? 0) >= numberResults) return new OkObjectResult(merged);
                    }
                    return new ObjectResult(new { error = "Image generation failed. Please try again." }) { StatusCode = StatusCodes.Status502BadGateway };
                }

                modelsLabKey = Environment.GetEnvironmentVariable("MODELSLAB_API_KEY")
                    ?? _configuration?["MODELSLAB_API_KEY"]
                    ?? _configuration?["Values:MODELSLAB_API_KEY"];
                if (string.IsNullOrWhiteSpace(modelsLabKey))
                {
                    log.LogWarning("MODELSLAB_API_KEY is not configured for DesignerIntro job {GenerationJobId}.", generationJobId);
                    var fallbackResult = await runwareTask;
                    return (fallbackResult?.Data?.Count ?? 0) >= numberResults
                        ? new OkObjectResult(fallbackResult)
                        : new ObjectResult(new { error = "Image generation failed. Please try again." }) { StatusCode = StatusCodes.Status502BadGateway };
                }

                var modelsLabTask = TryModelsLabForDesignerIntroAsync(modelsLabKey, numberResults, hedgeCancellation.Token);
                var firstProviderTask = await Task.WhenAny(runwareTask, modelsLabTask);
                var firstProviderResult = await firstProviderTask;
                if ((firstProviderResult?.Data?.Count ?? 0) >= numberResults)
                {
                    hedgeCancellation.Cancel();
                    var losingTask = firstProviderTask == runwareTask ? modelsLabTask : runwareTask;
                    try { await losingTask; } catch (OperationCanceledException) { }
                    return new OkObjectResult(firstProviderResult);
                }

                var secondProviderTask = firstProviderTask == runwareTask ? modelsLabTask : runwareTask;
                var secondProviderResult = await secondProviderTask;
                var mergedResult = MergeProviderImages(firstProviderResult, secondProviderResult);
                if ((mergedResult?.Data?.Count ?? 0) >= numberResults) return new OkObjectResult(mergedResult);
                return new ObjectResult(new { error = "Image generation failed. Please try again." }) { StatusCode = StatusCodes.Status502BadGateway };
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                log.LogError("RUNWARE_API_KEY is not configured for image generation.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
            const string imageModel = "runware:101@1";
            var imageUsageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage { UserId = imageUserId, ProjectId = projectId, FunnelId = funnelId, GenerationJobId = useModelsLabPrimary ? generationJobId + ":runware" : generationJobId, GenerationCategory = websiteIntro ? "WebsiteFunnel" : "SocialFunnel", DeliverableType = websiteIntro ? "Website" : "SocialMediaContent", Feature = websiteIntro ? WebsiteFunnelPromptResolver.Feature : null, UsageScope = websiteIntro ? WebsiteFunnelPromptResolver.UsageScope : null, Operation = "ImageGeneration", Provider = "Runware", Model = imageModel, ProviderTaskId = generationJobId, ImageCount = numberResults, ImageWidth = width, ImageHeight = height });

            var payload = new object[]
            {
        new {
            taskType = "authentication",
            apiKey = apiKey
        },
        new {
            taskType = "imageInference",
            taskUUID = generationJobId,
            positivePrompt = finalPrompt,
            width = width,
            height = height,
            model = "runware:101@1",
            numberResults = numberResults,
            outputType = new[] { "dataURI", "URL" },
            outputFormat = "JPEG",
            CFGScale = 3.5,
            steps = 28,
            seed = GenerateRandomSeed(),
            scheduler = "FlowMatchEulerDiscreteScheduler",
            includeCost = true,
            outputQuality = 90
        }
            };

            var jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                using var httpClient = useModelsLabPrimary ? runwareImageClient() : new HttpClient();
                if (useModelsLabPrimary) httpClient.Timeout = TimeSpan.FromSeconds(90);
                var response = await httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                var result = JsonConvert.DeserializeObject<RunwareResponse>(responseContent);
                if (!response.IsSuccessStatusCode)
                {
                    await aiUsage.CompleteFailureAsync(imageUsageId, "ProviderError");
                    log.LogError("Runware returned HTTP {StatusCode} for image job {GenerationJobId}.", (int)response.StatusCode, generationJobId);
                    if (!useModelsLabPrimary) return new StatusCodeResult((int)response.StatusCode);
                    return new ObjectResult(new { error = "Image generation failed. Please try again." }) { StatusCode = StatusCodes.Status502BadGateway };
                }
                if (useModelsLabPrimary && (result?.Data == null || !result.Data.Any(image => Uri.TryCreate(image.ImageURL, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
                   )
                {
                    await aiUsage.CompleteFailureAsync(imageUsageId, "InvalidProviderResponse");
                    log.LogError("Runware returned no usable image URLs for image job {GenerationJobId}.", generationJobId);
                    return new ObjectResult(new { error = "Image generation failed. Please try again." }) { StatusCode = StatusCodes.Status502BadGateway };
                }
                var actualCost = RunwareUsageParser.ImageCost(responseContent);
                await aiUsage.CompleteSuccessAsync(imageUsageId, actualCost: actualCost, rawUsageReference: "runware-task-id");
                return new OkObjectResult(result);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                await aiUsage.CompleteFailureAsync(imageUsageId, "ProviderError");
                log.LogError("Runware failed for image job {GenerationJobId}. Failure type: {FailureType}.", generationJobId, ex.GetType().Name);
                if (!useModelsLabPrimary && ex is HttpRequestException)
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                return new ObjectResult(new { error = "Image generation failed. Please try again." }) { StatusCode = StatusCodes.Status502BadGateway };
            }
        }

        [Function("CallRunwareAPIWithReferenceImage")]
        public async Task<IActionResult> CallRunwareAPIWithReferenceImage(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
    ILogger log)
        {
            log ??= _logger;
            // Validate Origin
            string origin = req.Headers["Origin"].FirstOrDefault();
            string hostUrl = Environment.GetEnvironmentVariable("HostUrl");

            if (string.IsNullOrWhiteSpace(origin) || origin != hostUrl)
            {
                return new UnauthorizedResult();
            }

            // Load environment variables
            string apiUrl = Environment.GetEnvironmentVariable("RUNWARE_API_URL");
            string apiKey = Environment.GetEnvironmentVariable("RUNWARE_API_KEY");

            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                log.LogError("Missing RUNWARE_API_URL or RUNWARE_API_KEY.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            // Read request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string positivePrompt = data?.positivePrompt;
            string referenceImage = data?.referenceImage;

            int width = data?.width ?? 1344;
            int height = data?.height ?? 768;
            int numberResults = data?.numberResults ?? 1;

            if (string.IsNullOrWhiteSpace(positivePrompt))
                return new BadRequestObjectResult("Missing 'positivePrompt'.");

            if (string.IsNullOrWhiteSpace(referenceImage))
                return new BadRequestObjectResult("Missing 'referenceImage'.");

            string? projectId = data?.projectId;
            var claimedUserId = jwtUserResolver.ResolveUserId(req);
            var userId = string.IsNullOrWhiteSpace(claimedUserId) ? null : await attributionStore.ResolveClaimedUserIdAsync(claimedUserId);
            var project = string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(userId)
                ? null : await attributionStore.GetOwnedGenerationProjectAsync(userId, projectId);
            if (!string.IsNullOrWhiteSpace(projectId) && project is null)
                return new UnauthorizedResult();
            var generationJobId = Guid.NewGuid().ToString();
            string usageId;
            try
            {
                // This existing free operation still needs provider accounting.
                // Do not introduce a credit charge while adding its missing telemetry.
                usageId = await aiUsage.CreateStartedAsync(new AIGenerationUsage
                {
                    UserId = userId, ProjectId = project?.ProjectId, FunnelId = project?.FunnelId,
                    GenerationJobId = generationJobId, ProviderTaskId = generationJobId,
                    GenerationCategory = project is null ? "Acquisition" : "PaidWorkspace",
                    Feature = "reference_image", UsageScope = project is null ? "Preview" : "Paid",
                    GenerationUnits = 0, Operation = "ImageGeneration", Provider = "Runware",
                    Model = "runware:101@1", ImageCount = numberResults, ImageWidth = width, ImageHeight = height,
                    PricingVersion = AIProviderPricingService.CurrentVersion
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[AI USAGE] Reference image accounting unavailable");
                return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            }

            // Build payload (MATCHES YOUR JSON EXACTLY)
            var payload = new object[]
{
                new {
                    taskType = "authentication",
                    apiKey = apiKey
                },
                new {
                    taskType = "imageInference",
            taskUUID = generationJobId,
                    positivePrompt = positivePrompt,
                    width = width,
                    height = height,
                    model = "runware:101@1",
                    numberResults = numberResults,
                    outputType = new[] { "dataURI", "URL" },
                    outputFormat = "JPEG",
                    CFGScale = 3.5,
                    steps = 28,
                    seed = GenerateRandomSeed(),
                    seedImage = referenceImage,
                    scheduler = "FlowMatchEulerDiscreteScheduler",
                    includeCost = true,
                    outputQuality = 90
                }
            };

            //            {
            //                "taskType": "imageInference",
            //  "model": "runware:101@1",
            //  "positivePrompt": "Print magazines, trade show exhibits.|Design a \"zero-waste clock\" poster showing what happens to every ingredient at 0, 6, 12, and 24 hours after opening the bag.|Husk & Honey|Upcycled grain-free granola (using cocoa husks and sunflower meal)|Print Materials Description: Brochures for product promotion at trade shows. Display: Print magazines, trade show exhibits. Format: PDF. Dimensions/Size: 8.5x11 inches. Quantity: 500 copies. Technical Requirements: CMYK color mode, 300 dpi resolution.|Print Materials|Husk & Honey transforms waste into delicious granola, with zero waste philosophy.|Minimal|Swiss|ultra-clean",
            //  "height": 768,
            //  "width": 1344,
            //  "numberResults": 1,
            //  "outputType": [
            //    "dataURI",
            //    "URL"
            //  ],
            //  "checkNSFW": true,
            //  "strength": 0.8,
            //  "CFGScale": 3.5,
            //  "steps": 28,
            //  "scheduler": "FlowMatchEulerDiscreteScheduler",
            //  "includeCost": true,
            //  "seedImage": "https://im.runware.ai/image/ws/5/bucket/media-storage/ii/2ef1734b-207d-4390-8082-9d2f158b0eab",
            //  "skipResponse": true,
            //  "deliveryMethod": "async",
            //  "outputQuality": 85,
            //  "taskUUID": "f4b0ebaa-ea11-40bd-80e4-78358d2125c3"
            //}

            string jsonPayload = JsonConvert.SerializeObject(payload);

            //log.LogInformation("Runware Request:");
            //log.LogInformation(jsonPayload);

            try
            {
                using var httpClient = runwareImageClient();

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                // IMPORTANT: surface API errors
                if (!response.IsSuccessStatusCode)
                {
                    //log.LogError($"Runware API failed: {response.StatusCode}");
                    //log.LogError(responseContent);

                    await aiUsageRecorder!.CompleteFailureAsync(usageId, "ProviderError");
                    return new BadRequestObjectResult(responseContent);
                }

                var actualCost = RunwareUsageParser.ImageCost(responseContent);
                await aiUsageRecorder!.CompleteSuccessAsync(usageId, actualCost);
                var result = responseContent;
                return new OkObjectResult(result);
            }
            catch (HttpRequestException ex)
            {
                await aiUsageRecorder!.CompleteFailureAsync(usageId, "ProviderError");
                log.LogError($"HTTP error calling Runware API: {ex.Message}");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
            catch (TaskCanceledException)
            {
                await aiUsageRecorder!.CompleteFailureAsync(usageId, "Timeout");
                return new StatusCodeResult(StatusCodes.Status504GatewayTimeout);
            }
            catch (System.Text.Json.JsonException)
            {
                await aiUsageRecorder!.CompleteFailureAsync(usageId, "InvalidProviderResponse");
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }
        }

        private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
        {
            var username = jwtUserResolver.ResolveUsername(request);
            return string.IsNullOrWhiteSpace(username)
                ? null
                : await attributionStore.ResolveUserIdAsync(username);
        }

        [Function("CallRunwareAudio")]
        public async Task<IActionResult> CallRunwareAudio(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            FunctionContext context)
        {
            // Get logger from the Azure Functions execution context.
            // This avoids ILogger being null.
            var log = context.GetLogger("CallRunwareAudio");

            try
            {
                // ---------------------------------------------------------
                // Validate Origin header
                // ---------------------------------------------------------

                string origin = req.Headers["Origin"].FirstOrDefault();
                string hostUrl = Environment.GetEnvironmentVariable("HostUrl");

                if (string.IsNullOrWhiteSpace(origin) ||
                    string.IsNullOrWhiteSpace(hostUrl) ||
                    !string.Equals(
                        origin.TrimEnd('/'),
                        hostUrl.TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning(
                        "Unauthorized CallRunwareAudio request. Origin: {Origin}",
                        origin ?? "(null)");

                    return new UnauthorizedResult();
                }

                // ---------------------------------------------------------
                // Runware configuration
                // ---------------------------------------------------------

                string apiUrl =
                    Environment.GetEnvironmentVariable("RUNWARE_API_URL")
                    ?? "https://api.runware.ai/v1";

                string apiKey =
                    Environment.GetEnvironmentVariable("RUNWARE_API_KEY");

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    log.LogError(
                        "Missing RUNWARE_API_KEY environment variable.");

                    return new ObjectResult(new
                    {
                        error = "Audio service is not configured."
                    })
                    {
                        StatusCode =
                            StatusCodes.Status500InternalServerError
                    };
                }

                // ---------------------------------------------------------
                // Read request body
                // ---------------------------------------------------------

                string requestBody =
                    await new StreamReader(req.Body).ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    return new BadRequestObjectResult(new
                    {
                        error = "Request body is empty."
                    });
                }

                dynamic data =
                    JsonConvert.DeserializeObject(requestBody);

                string text = data?.text;
                string requestedVoice = data?.voice;
                string language = data?.language ?? "en";
                string generationJobId = data?.generationJobId ?? string.Empty;
                string providerOperationId = data?.providerOperationId ?? string.Empty;
                string projectId = data?.projectId ?? string.Empty;
                string funnelId = data?.funnelId ?? string.Empty;

                if (string.IsNullOrWhiteSpace(text))
                {
                    return new BadRequestObjectResult(new
                    {
                        error = "Missing 'text' in the request body."
                    });
                }

                var claimedUserId = jwtUserResolver.ResolveUserId(req);
                var userId = string.IsNullOrWhiteSpace(claimedUserId)
                    ? await ResolveLegacyJwtUserIdAsync(req)
                    : await attributionStore.ResolveClaimedUserIdAsync(claimedUserId);
                if (string.IsNullOrWhiteSpace(userId))
                    return new UnauthorizedObjectResult(new { code = "authentication_required" });
                if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(generationJobId) ||
                    string.IsNullOrWhiteSpace(providerOperationId))
                    return new BadRequestObjectResult(new { code = "generation_context_missing" });
                var generationProject = await attributionStore.GetOwnedGenerationProjectAsync(userId, projectId);
                if (generationProject is null)
                    return new ObjectResult(new { code = "project_access_denied" })
                    { StatusCode = StatusCodes.Status403Forbidden };
                projectId = generationProject.ProjectId;
                funnelId = generationProject.FunnelId;

                // ---------------------------------------------------------
                // xAI voice validation
                // ---------------------------------------------------------

                string[] validVoices =
                {
            "eve",
            "ara",
            "leo",
            "rex",
            "sal"
        };

                string voice =
                    string.IsNullOrWhiteSpace(requestedVoice)
                        ? "eve"
                        : requestedVoice
                            .Trim()
                            .ToLowerInvariant();

                if (!validVoices.Contains(voice))
                {
                    log.LogWarning(
                        "Unsupported xAI voice '{Voice}'. Falling back to Eve.",
                        requestedVoice);

                    voice = "eve";
                }

                // ---------------------------------------------------------
                // Create Runware task
                // ---------------------------------------------------------

                string taskUuid = Guid.NewGuid().ToString();
                var usageId = await aiUsageRecorder!.StartAsync(new AIGenerationUsage
                {
                    UserId = userId, ProjectId = projectId, FunnelId = funnelId,
                    GenerationJobId = generationJobId, ProviderTaskId = providerOperationId,
                    GenerationCategory = "PaidWorkspace", DeliverableType = "CommercialVideo",
                    Feature = "commercial_video", UsageScope = "Paid", GenerationUnits = 0,
                    Operation = "AudioGeneration", Provider = "Runware", Model = "xai:tts@0",
                    CharacterCount = text.Length, PricingVersion = AIProviderPricingService.CurrentVersion
                });

                var payload = new object[]
                {
            new
            {
                taskType = "authentication",
                apiKey = apiKey
            },

            new
            {
                taskType = "audioInference",

                taskUUID = taskUuid,

                model = "xai:tts@0",

                speech = new
                {
                    text = text,
                    voice = voice,
                    language = language
                },

                outputFormat = "MP3",

                outputType = new[]
                {
                    "URL"
                },

                includeCost = true,

                deliveryMethod = "sync",

                numberResults = 1
            }
                };

                string jsonPayload =
                    JsonConvert.SerializeObject(payload);

                // Do NOT log jsonPayload because it contains
                // the Runware API key in the authentication task.

                log.LogInformation(
                    "Calling Runware audio API. TaskUUID: {TaskUUID}, Voice: {Voice}, Language: {Language}, TextLength: {TextLength}",
                    taskUuid,
                    voice,
                    language,
                    text.Length);

                // ---------------------------------------------------------
                // Call Runware
                // ---------------------------------------------------------

                using var httpClient =
                    new HttpClient();

                httpClient.Timeout =
                    TimeSpan.FromSeconds(60);

                using var content =
                    new StringContent(
                        jsonPayload,
                        Encoding.UTF8,
                        "application/json");

                HttpResponseMessage response =
                    await httpClient.PostAsync(
                        apiUrl,
                        content);

                string responseContent =
                    await response.Content.ReadAsStringAsync();

                // ---------------------------------------------------------
                // Handle Runware error
                // ---------------------------------------------------------

                if (!response.IsSuccessStatusCode)
                {
                    await aiUsageRecorder!.CompleteFailureAsync(usageId, "ProviderError");
                    log.LogError(
                        "Runware API error. Status: {StatusCode}. TaskUUID: {TaskUUID}. Response: {Response}",
                        (int)response.StatusCode,
                        taskUuid,
                        responseContent);

                    return new ObjectResult(new
                    {
                        error = "Runware API request failed",
                        statusCode = (int)response.StatusCode,
                        details = responseContent
                    })
                    {
                        StatusCode =
                            StatusCodes.Status502BadGateway
                    };
                }

                // ---------------------------------------------------------
                // Deserialize Runware response
                // ---------------------------------------------------------

                log.LogInformation(
                    "Runware audio generation succeeded. TaskUUID: {TaskUUID}",
                    taskUuid);

                var result =
                    JsonConvert.DeserializeObject<RunwareAudioResponse>(
                        responseContent);

                var audioResult =
                    result?.Data?.FirstOrDefault();

                string audioUrl =
                    audioResult?.AudioUrl;

                if (string.IsNullOrWhiteSpace(audioUrl))
                {
                    await aiUsageRecorder!.CompleteFailureAsync(usageId, "InvalidProviderResponse");
                    log.LogError(
                        "Runware returned success but no audio URL. TaskUUID: {TaskUUID}. Response: {Response}",
                        taskUuid,
                        responseContent);

                    return new ObjectResult(new
                    {
                        error =
                            "Runware did not return an audio URL.",

                        details =
                            responseContent
                    })
                    {
                        StatusCode =
                            StatusCodes.Status502BadGateway
                    };
                }

                await aiUsageRecorder!.CompleteSuccessAsync(
                    usageId,
                    Convert.ToDecimal(audioResult!.Cost, System.Globalization.CultureInfo.InvariantCulture));

                // ---------------------------------------------------------
                // Return URL to Razor frontend
                // ---------------------------------------------------------

                return new OkObjectResult(
                    new AudioResultDto
                    {
                        AudioUrl = audioUrl
                    });
            }
            catch (System.Text.Json.JsonException ex)
            {
                log.LogError(
                    ex,
                    "Invalid JSON received by CallRunwareAudio.");

                return new BadRequestObjectResult(new
                {
                    error = "Invalid JSON request body."
                });
            }
            catch (TaskCanceledException ex)
            {
                log.LogError(
                    ex,
                    "Runware audio request timed out.");

                return new ObjectResult(new
                {
                    error = "Runware audio request timed out."
                })
                {
                    StatusCode =
                        StatusCodes.Status504GatewayTimeout
                };
            }
            catch (HttpRequestException ex)
            {
                log.LogError(
                    ex,
                    "HTTP error calling Runware.");

                return new ObjectResult(new
                {
                    error = "Unable to contact Runware.",
                    details = ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status502BadGateway
                };
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "Unexpected error in CallRunwareAudio.");

                return new ObjectResult(new
                {
                    error =
                        "Unexpected error generating audio.",

                    details =
                        ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }

        public class RunwareAudioResponse
        {
            [JsonProperty("data")]
            public List<RunwareAudioData> Data { get; set; } = new();
        }

        public class RunwareAudioData
        {
            [JsonProperty("audioURL")]
            public string AudioUrl { get; set; } = string.Empty;

            [JsonProperty("audioUUID")]
            public string AudioUUID { get; set; } = string.Empty;

            [JsonProperty("taskUUID")]
            public string TaskUUID { get; set; } = string.Empty;

            [JsonProperty("cost")]
            public double Cost { get; set; }
        }

        [Function("Signup")]
        public async Task<IActionResult> Signup(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            // Validate Origin
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // Read request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var signupData = System.Text.Json.JsonSerializer.Deserialize<SignupRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // Basic validation
            if (signupData == null ||
                string.IsNullOrWhiteSpace(signupData.Username) ||
                string.IsNullOrWhiteSpace(signupData.Password))
            {
                return new BadRequestObjectResult(new { Message = "Username and password are required." });
            }

            // Hash password
            var salt = Guid.NewGuid().ToString();
            var hashedPassword = Utilities.HashPassword(signupData.Password, salt);
            var normalizedUsername = CanonicalUserStore.NormalizeEmail(signupData.Username)!;

            try
            {
                var existingUser = await canonicalUserStore.FindByEmailAsync(normalizedUsername);
                if (existingUser is not null)
                {
                    _logger.LogError("Username already exists");
                    return new ConflictObjectResult(new { Message = "Username already exists" });
                }

                var userId = CanonicalUserStore.UserIdForEmail(normalizedUsername);
                using var client = attributionStore.CreateClient();
                var item = new Dictionary<string, AttributeValue>
                {
                    ["Id"] = new() { S = userId }, ["Username"] = new() { S = normalizedUsername },
                    ["NormalizedUsername"] = new() { S = normalizedUsername }, ["PasswordHash"] = new() { S = hashedPassword },
                    ["Salt"] = new() { S = salt }, ["Onboarded"] = new() { S = "0" },
                    ["AuthenticationProvider"] = new() { S = "Password" }, ["CreatedDate"] = new() { S = DateTimeOffset.UtcNow.ToString("O") }
                };

                if (!string.IsNullOrWhiteSpace(signupData.PhoneNumber))
                    item["PhoneNumber"] = new() { S = signupData.PhoneNumber };

                await client.PutItemAsync(new PutItemRequest { TableName = "Users", Item = item, ConditionExpression = "attribute_not_exists(Id)" });

                return new OkObjectResult(new
                {
                    Message = "Signup successful",
                    UserId = userId
                });
            }
            catch (ConditionalCheckFailedException)
            {
                return new ConflictObjectResult(new { Message = "Username already exists" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Signup failed.");
                return new StatusCodeResult(500);
            }
        }

        public class SignupRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
            public string PhoneNumber { get; set; } // optional
        }

        [Function("SaveIdea")]
        public async Task<IActionResult> SaveIdea([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var ideaData = System.Text.Json.JsonSerializer.Deserialize<IdeaRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string ideaId;
                while (true)
                {
                    ideaId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Ideas WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = ideaId }
                        }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);
                    if (!existingIdResult.Items.Any())
                        break;
                }

                // Prepare insert statement including optional CompanyIdeaId
                var statement = "INSERT INTO Ideas VALUE {'Id': ?, 'Title': ?, 'Description': ?, 'UserId': ?, 'CompanyIdeaId': ?}";
                var parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = ideaId },
                    new AttributeValue { S = ideaData.Title },
                    new AttributeValue { S = ideaData.Description },
                    new AttributeValue { S = ideaData.UserId },
                    new AttributeValue { S = ideaData.CompanyIdeaId ?? string.Empty } // ← default to empty if null
                };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new { Message = "Idea saved successfully", IdeaId = ideaId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving idea failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("SendInviteEmail")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SendInviteEmail(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
        {
            try
            {
                // =========================
                // PARSE MULTIPART FORM
                // =========================
                var (fields, files) = await ParseMultipartAsync(req);

                var data = new InviteRequest
                {
                    ClientName = fields.ContainsKey("ClientName") ? fields["ClientName"] : null,
                    ClientIndustry = fields.ContainsKey("ClientIndustry") ? fields["ClientIndustry"] : null,
                    ClientEmail = fields.ContainsKey("ClientEmail") ? fields["ClientEmail"] : null,
                    InitialPrompt = fields.ContainsKey("InitialPrompt") ? fields["InitialPrompt"] : null,
                    InviteLink = fields.ContainsKey("InviteLink") ? fields["InviteLink"] : null,
                    FileUrl = fields.ContainsKey("FileUrl") ? fields["FileUrl"] : null
                };

                // =========================
                // VALIDATION
                // =========================
                if (string.IsNullOrWhiteSpace(data.ClientEmail))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("Invalid request payload: missing ClientEmail.");
                    return bad;
                }

                // =========================
                // AWS SES CONFIG
                // =========================
                var region = RegionEndpoint.GetBySystemName(
                    Environment.GetEnvironmentVariable("AWS_REGION"));

                var sesClient = new AmazonSimpleEmailServiceClient(
                    Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
                    Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
                    region
                );

                var fromEmail = Environment.GetEnvironmentVariable("SES_FROM_EMAIL");
                var copyEmail = Environment.GetEnvironmentVariable("COPY_EMAIL");

                // =========================
                // TEXT EMAIL BODY
                // =========================
                var textBody =
        $"""
You're invited to DesignMode

Hi {data.ClientName},

You've been invited to collaborate.

Prompt:
{data.InitialPrompt}

Invite Link:
{data.InviteLink}

File:
{data.FileUrl ?? "No file uploaded"}

— DesignMode
""";

                // =========================
                // HTML EMAIL BODY
                // =========================
                var htmlBody =
        $"""
<div style='font-family:Arial,sans-serif;padding:20px;line-height:1.5;color:#111;'>
    <h2>You're invited to DesignMode ✨</h2>

    <p>Hi <strong>{data.ClientName}</strong>,</p>

    <p>You've been invited to collaborate.</p>

    <div style='background:#f5f7fa;padding:12px;border-radius:8px;margin:16px 0;'>
        {data.InitialPrompt}
    </div>

    <p>
        <strong>File:</strong><br/>
        {(string.IsNullOrWhiteSpace(data.FileUrl)
                    ? "No file uploaded"
                    : $"<a href='{data.FileUrl}' target='_blank'>View File</a>")}
    </p>

    <a href='{data.InviteLink}'
       style='display:inline-block;padding:12px 20px;background:#0a1a2f;color:#fff;border-radius:30px;text-decoration:none;'>
        Accept Invite
    </a>

    <p style='margin-top:30px;font-size:12px;color:#777;'>
        If you didn’t expect this invite, you can ignore this email.
    </p>

    <p style='margin-top:10px;font-size:12px;color:#999;'>
        — DesignMode
    </p>
</div>
""";

                // =========================
                // BUILD SES REQUEST
                // =========================
                var destination = new Amazon.SimpleEmail.Model.Destination
                {
                    ToAddresses = new List<string> { data.ClientEmail }
                };

                // Optional copy email
                if (!string.IsNullOrWhiteSpace(copyEmail))
                {
                    destination.BccAddresses = new List<string> { copyEmail };
                }

                var request = new SendEmailRequest
                {
                    Source = fromEmail,
                    Destination = destination,
                    Message = new Amazon.SimpleEmail.Model.Message
                    {
                        Subject = new Amazon.SimpleEmail.Model.Content("You're invited to collaborate"),
                        Body = new Body
                        {
                            Text = new Amazon.SimpleEmail.Model.Content(textBody),
                            Html = new Amazon.SimpleEmail.Model.Content(htmlBody)
                        }
                    }
                };

                // =========================
                // SEND EMAIL
                // =========================
                var result = await sesClient.SendEmailAsync(request);

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);

                await response.WriteStringAsync(
                    $"Email sent successfully. MessageId: {result.MessageId}");

                return response;
            }
            catch (AmazonSimpleEmailServiceException sesEx)
            {
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);

                await response.WriteStringAsync(
                    $"SES Error: {sesEx.Message}");

                return response;
            }
            catch (Exception ex)
            {
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);

                await response.WriteStringAsync(
                    $"Failed to send email: {ex.Message}");

                return response;
            }
        }
        public class InviteRequest
        {
            public string ClientName { get; set; }
            public string ClientIndustry { get; set; }
            public string ClientEmail { get; set; }
            public string InitialPrompt { get; set; }
            public string InviteLink { get; set; }
            public string FileUrl { get; set; }
        }

        [Function("SaveCompanyIndustry")]
        public async Task<IActionResult> SaveCompanyIndustry([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var companyData = System.Text.Json.JsonSerializer.Deserialize<CompanyIndustryRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate a unique GUID
                string companyId;
                while (true)
                {
                    companyId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM CompanyIndustry WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = companyId }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);
                    if (!existingIdResult.Items.Any())
                        break;
                }

                // Insert the new company-industry pair
                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = "INSERT INTO CompanyIndustry VALUE {'Id': ?, 'Name': ?, 'Industry': ?, 'UserId': ?}",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = companyId },
                        new AttributeValue { S = companyData.Name },
                        new AttributeValue { S = companyData.Industry },
                        new AttributeValue { S = companyData.UserId }
                    }
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new { Message = "Company and Industry saved successfully", CompanyId = companyId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving company industry failed.");
                return new StatusCodeResult(500);
            }
        }

        public class SaveEmailRequest
        {
            public string? Email { get; set; }

            public string? Local { get; set; }

            public string? CurrentPage { get; set; }
            public string? CompanyIndustryId { get; set; }
            public string? Name { get; set; }
            public string? Industry { get; set; }
        }


        [Function("SaveEmail")]
        public async Task<IActionResult> SaveEmail(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string? origin =
                req.Headers["Origin"].FirstOrDefault();

            string? allowedHost =
                Environment.GetEnvironmentVariable("HostUrl");

            if (string.IsNullOrWhiteSpace(origin) ||
                string.IsNullOrWhiteSpace(allowedHost) ||
                !string.Equals(
                    origin,
                    allowedHost,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UnauthorizedResult();
            }

            SaveEmailRequest? emailData;

            try
            {
                string requestBody =
                    await new StreamReader(req.Body).ReadToEndAsync();

                emailData =
                    System.Text.Json.JsonSerializer.Deserialize<SaveEmailRequest>(
                        requestBody,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "SaveEmail received invalid JSON.");

                return new BadRequestObjectResult(new
                {
                    Message = "The request body contains invalid JSON."
                });
            }

            if (emailData == null)
            {
                return new BadRequestObjectResult(new
                {
                    Message = "A request body is required."
                });
            }

            if (string.IsNullOrWhiteSpace(emailData.Email))
            {
                return new BadRequestObjectResult(new
                {
                    Message = "Email is required."
                });
            }

            if (string.IsNullOrWhiteSpace(emailData.Local) && string.IsNullOrWhiteSpace(emailData.CompanyIndustryId))
            {
                return new BadRequestObjectResult(new
                {
                    Message = "Local or CompanyIndustryId is required."
                });
            }

            if (string.IsNullOrWhiteSpace(emailData.CurrentPage))
            {
                return new BadRequestObjectResult(new
                {
                    Message = "CurrentPage is required."
                });
            }

            string normalizedEmail =
                emailData.Email.Trim().ToLowerInvariant();

            string normalizedLocal =
                emailData.Local?.Trim() ?? string.Empty;

            string normalizedCurrentPage =
                emailData.CurrentPage.Trim();

            if (!IsValidEmail(normalizedEmail))
            {
                return new BadRequestObjectResult(new
                {
                    Message = "A valid email address is required."
                });
            }

            var authenticatedUsername = jwtUserResolver.ResolveUsername(req);
            if (string.IsNullOrWhiteSpace(authenticatedUsername) ||
                CanonicalUserStore.NormalizeEmail(authenticatedUsername) != normalizedEmail)
                return new UnauthorizedResult();

            if (!CanonicalUserProgress.TryNormalizePage(normalizedCurrentPage, allowedHost, out normalizedCurrentPage))
                return new BadRequestObjectResult(new { Message = "CurrentPage must be a safe local route." });

            try
            {
                RegionEndpoint region;

                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                /*
                 * Retrieve the complete CompanyIndustry item.
                 *
                 * SELECT * is important because the actual partition key
                 * and optional sort key must be available when building
                 * the DynamoDB update request.
                 */
                var companyResult = new ExecuteStatementResponse
                {
                    Items = await FunnelCompanyLookup.FindAsync(client, normalizedLocal,
                        emailData.CompanyIndustryId, emailData.Name, emailData.Industry)
                };

                if (companyResult.Items.Count == 0)
                {
                    return new NotFoundObjectResult(new
                    {
                        Message =
                            "The selected project could not be found."
                    });
                }

                if (companyResult.Items.Count > 1)
                {
                    _logger.LogWarning(
                        "Multiple CompanyIndustry records were found for Local {Local}.",
                        normalizedLocal);

                    return new ConflictObjectResult(new
                    {
                        Message =
                            "Multiple CompanyIndustry records were found for the supplied Local value."
                    });
                }

                Dictionary<string, AttributeValue> companyItem =
                    companyResult.Items[0];

                /*
                 * Confirm the returned record has the expected Local value.
                 */
                if (!companyItem.TryGetValue(
                        "Local",
                        out AttributeValue? companyLocalAttribute) ||
                    string.IsNullOrWhiteSpace(
                        companyLocalAttribute.S))
                {
                    _logger.LogError(
                        "The CompanyIndustry record found for Local {Local} does not contain a valid Local attribute.",
                        normalizedLocal);

                    return new ObjectResult(new
                    {
                        Message =
                            "The matching CompanyIndustry record does not contain a valid Local value."
                    })
                    {
                        StatusCode =
                            StatusCodes.Status500InternalServerError
                    };
                }

                /*
                 * Read the actual key schema configured on CompanyIndustry.
                 *
                 * This avoids assuming that the keys are Id and Local.
                 */
                DescribeTableResponse companyTableDescription =
                    await client.DescribeTableAsync(
                        new DescribeTableRequest
                        {
                            TableName = "CompanyIndustry"
                        });

                if (companyTableDescription.Table?.KeySchema == null ||
                    companyTableDescription.Table.KeySchema.Count == 0)
                {
                    _logger.LogError(
                        "CompanyIndustry does not have a valid key schema.");

                    return new ObjectResult(new
                    {
                        Message =
                            "The CompanyIndustry table key schema could not be determined."
                    })
                    {
                        StatusCode =
                            StatusCodes.Status500InternalServerError
                    };
                }

                /*
                 * Build the complete DynamoDB key using the table's actual
                 * partition key and optional sort key.
                 */
                var companyKey =
                    new Dictionary<string, AttributeValue>();

                foreach (KeySchemaElement keyElement
                         in companyTableDescription.Table.KeySchema)
                {
                    string keyName =
                        keyElement.AttributeName;

                    if (!companyItem.TryGetValue(
                            keyName,
                            out AttributeValue? keyValue) ||
                        keyValue == null)
                    {
                        _logger.LogError(
                            "The matching CompanyIndustry item is missing required key {KeyName}.",
                            keyName);

                        return new ObjectResult(new
                        {
                            Message =
                                $"The matching CompanyIndustry record is missing the required key '{keyName}'."
                        })
                        {
                            StatusCode =
                                StatusCodes.Status500InternalServerError
                        };
                    }

                    bool hasStringValue =
                        !string.IsNullOrWhiteSpace(
                            keyValue.S);

                    bool hasNumberValue =
                        !string.IsNullOrWhiteSpace(
                            keyValue.N);

                    bool hasBinaryValue =
                        keyValue.B != null;

                    if (!hasStringValue &&
                        !hasNumberValue &&
                        !hasBinaryValue)
                    {
                        _logger.LogError(
                            "The matching CompanyIndustry item has an empty value for required key {KeyName}.",
                            keyName);

                        return new ObjectResult(new
                        {
                            Message =
                                $"The matching CompanyIndustry record has an empty value for required key '{keyName}'."
                        })
                        {
                            StatusCode =
                                StatusCodes.Status500InternalServerError
                        };
                    }

                    companyKey[keyName] =
                        keyValue;
                }

                // Reuse the account created by password/Google authentication.
                // Never create a separate Email-only Users row for funnel progress.
                var canonicalUser = await canonicalUserStore.FindByEmailAsync(authenticatedUsername);
                if (canonicalUser is null)
                    return new NotFoundObjectResult(new { Message = "The authenticated account was not found." });

                if (!FunnelCompanyLookup.CanClaim(companyItem, normalizedLocal, canonicalUser.Id, canonicalUser.Username))
                    return new StatusCodeResult(StatusCodes.Status403Forbidden);

                normalizedLocal = companyLocalAttribute.S;
                string userId = canonicalUser.Id;
                string timestamp = DateTime.UtcNow.ToString("O");
                var transactionItems = new List<TransactWriteItem>
                {
                    new() { Update = CanonicalUserProgress.CreateUpdate(canonicalUser, normalizedCurrentPage) }
                };

                /*
                 * Update the matching CompanyIndustry item.
                 */
                transactionItems.Add(
                    new TransactWriteItem
                    {
                        Update =
                            new Update
                            {
                                TableName =
                                    "CompanyIndustry",

                                Key =
                                    companyKey,

                                UpdateExpression =
                                    "SET #userId = :userId, " +
                                    "#email = :email, " +
                                    "#updatedAt = :updatedAt",

                                ExpressionAttributeNames =
                                    new Dictionary<string, string>
                                    {
                                        ["#local"] =
                                            "Local",

                                        ["#userId"] =
                                            "UserId",

                                        ["#email"] =
                                            "Email",

                                        ["#updatedAt"] =
                                            "UpdatedAt"
                                    },

                                ExpressionAttributeValues =
                                    new Dictionary<string, AttributeValue>
                                    {
                                        [":previousUserId"] = new AttributeValue { S = companyItem["UserId"].S },
                                        [":local"] =
                                            new AttributeValue
                                            {
                                                S = normalizedLocal
                                            },

                                        [":userId"] =
                                            new AttributeValue
                                            {
                                                S = userId
                                            },

                                        [":email"] =
                                            new AttributeValue
                                            {
                                                S = normalizedEmail
                                            },

                                        [":updatedAt"] =
                                            new AttributeValue
                                            {
                                                S = timestamp
                                            }
                                    },

                                ConditionExpression =
                                    "#local = :local AND #userId = :previousUserId"
                            }
                    });

                var transactionRequest =
                    new TransactWriteItemsRequest
                    {
                        TransactItems =
                            transactionItems
                    };

                /*
                 * Save the database changes first.
                 *
                 * If this call throws an exception, the SES email code below
                 * will not run.
                 */
                await client.TransactWriteItemsAsync(
                    transactionRequest);

                /*
                 * The database transaction succeeded.
                 * Configure Amazon SES and send the confirmation email.
                 */
                string? awsRegion =
                    Environment.GetEnvironmentVariable(
                        "AWS_REGION");

                string? awsAccessKeyId =
                    Environment.GetEnvironmentVariable(
                        "AWS_ACCESS_KEY_ID");

                string? awsSecretAccessKey =
                    Environment.GetEnvironmentVariable(
                        "AWS_SECRET_ACCESS_KEY");

                string? fromEmail =
                    Environment.GetEnvironmentVariable(
                        "SES_FROM_EMAIL");

                string? copyEmail =
                    Environment.GetEnvironmentVariable(
                        "COPY_EMAIL");

                if (string.IsNullOrWhiteSpace(awsRegion))
                {
                    throw new InvalidOperationException(
                        "The AWS_REGION environment variable is not configured.");
                }

                if (string.IsNullOrWhiteSpace(awsAccessKeyId))
                {
                    throw new InvalidOperationException(
                        "The AWS_ACCESS_KEY_ID environment variable is not configured.");
                }

                if (string.IsNullOrWhiteSpace(awsSecretAccessKey))
                {
                    throw new InvalidOperationException(
                        "The AWS_SECRET_ACCESS_KEY environment variable is not configured.");
                }

                if (string.IsNullOrWhiteSpace(fromEmail))
                {
                    throw new InvalidOperationException(
                        "The SES_FROM_EMAIL environment variable is not configured.");
                }

                region =
                    RegionEndpoint.GetBySystemName(
                        awsRegion);

                using var sesClient =
                    new AmazonSimpleEmailServiceClient(
                        awsAccessKeyId,
                        awsSecretAccessKey,
                        region);

                string encodedEmail =
                    WebUtility.HtmlEncode(
                        normalizedEmail);

                string encodedLocal =
                    WebUtility.HtmlEncode(
                        normalizedLocal);

                string textBody =
        $"""
Your email has been saved

Your email address has been successfully saved to DesignMode.

Email:
{normalizedEmail}

Local:
{normalizedLocal}

— DesignMode
""";

                /*
                 * This HTML contains only inline CSS.
                 *
                 * There are no CSS at-rules such as @@media or @@keyframes.
                 * When placing CSS at-rules directly in a Blazor .razor file,
                 * escape them with two at signs, for example:
                 *
                 * @@media (max-width: 600px)
                 */
                string htmlBody =
        $"""
<div style="font-family:Arial,sans-serif;padding:20px;line-height:1.5;color:#111;">
    <h2>Your email has been saved</h2>

    <p>
        Your email address has been successfully saved to DesignMode.
    </p>

    <div style="background:#f5f7fa;padding:12px;border-radius:8px;margin:16px 0;">
        <p style="margin:0 0 8px 0;">
            <strong>Email:</strong><br/>
            {encodedEmail}
        </p>

        <p style="margin:0;">
            <strong>Local:</strong><br/>
            {encodedLocal}
        </p>
    </div>

    <p style="margin-top:30px;font-size:12px;color:#777;">
        If you did not submit this email address, you can ignore this message.
    </p>

    <p style="margin-top:10px;font-size:12px;color:#999;">
        — DesignMode
    </p>
</div>
""";

                var destination =
                    new Amazon.SimpleEmail.Model.Destination
                    {
                        ToAddresses =
                            new List<string>
                            {
                        normalizedEmail
                            }
                    };

                /*
                 * Add an optional blind-copy recipient.
                 */
                if (!string.IsNullOrWhiteSpace(copyEmail))
                {
                    destination.BccAddresses =
                        new List<string>
                        {
                    copyEmail.Trim()
                        };
                }

                var sesRequest =
                    new Amazon.SimpleEmail.Model.SendEmailRequest
                    {
                        Source =
                            fromEmail.Trim(),

                        Destination =
                            destination,

                        Message =
                            new Amazon.SimpleEmail.Model.Message
                            {
                                Subject =
                                    new Amazon.SimpleEmail.Model.Content(
                                        "Your email has been saved"),

                                Body =
                                    new Amazon.SimpleEmail.Model.Body
                                    {
                                        Text =
                                            new Amazon.SimpleEmail.Model.Content(
                                                textBody),

                                        Html =
                                            new Amazon.SimpleEmail.Model.Content(
                                                htmlBody)
                                    }
                            }
                    };

                Amazon.SimpleEmail.Model.SendEmailResponse sesResult =
                    await sesClient.SendEmailAsync(
                        sesRequest);

                _logger.LogInformation(
                    "SaveEmail completed for Email {Email}, Local {Local}, CurrentPage {CurrentPage}. SES MessageId: {MessageId}",
                    normalizedEmail,
                    normalizedLocal,
                    normalizedCurrentPage,
                    sesResult.MessageId);

                return new OkObjectResult(
                    new
                    {
                        Message =
                            "Existing user found, current page updated, CompanyIndustry updated, and email sent successfully.",

                        CompanyIndustryId = companyItem["Id"].S,
                        UserId =
                            userId,

                        Email =
                            normalizedEmail,

                        Local =
                            normalizedLocal,

                        CurrentPage =
                            normalizedCurrentPage,

                        EmailSent =
                            true,

                        EmailMessageId =
                            sesResult.MessageId
                    });
            }
            catch (TransactionCanceledException ex)
            {
                string cancellationReasons =
                    ex.CancellationReasons == null
                        ? "No cancellation details were returned."
                        : string.Join(
                            " | ",
                            ex.CancellationReasons.Select(
                                reason =>
                                    $"Code: {reason.Code}, Message: {reason.Message}"));

                _logger.LogError(
                    ex,
                    "SaveEmail transaction was canceled for Email {Email}, Local {Local}, CurrentPage {CurrentPage}. Reasons: {Reasons}",
                    normalizedEmail,
                    normalizedLocal,
                    normalizedCurrentPage,
                    cancellationReasons);

                return new ConflictObjectResult(new
                {
                    Message =
                        "The user could not be saved or the CompanyIndustry record could not be updated.",

                    Details =
                        cancellationReasons
                });
            }
            catch (AmazonSimpleEmailServiceException ex)
            {
                /*
                 * SES runs only after DynamoDB succeeds.
                 *
                 * Therefore, when this exception occurs, the database changes
                 * have already been committed.
                 */
                _logger.LogError(
                    ex,
                    "The database transaction succeeded, but SES could not send an email to {Email} for Local {Local}.",
                    normalizedEmail,
                    normalizedLocal);

                return new ObjectResult(new
                {
                    Message =
                        "The database records were saved, but the confirmation email could not be sent.",

                    DatabaseSaved =
                        true,

                    EmailSent =
                        false,

                    Email =
                        normalizedEmail,

                    Local =
                        normalizedLocal,

                    CurrentPage =
                        normalizedCurrentPage,

                    Error =
                        ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status502BadGateway
                };
            }
            catch (ResourceNotFoundException ex)
            {
                _logger.LogError(
                    ex,
                    "A required DynamoDB table was not found.");

                return new ObjectResult(new
                {
                    Message =
                        "A required database table was not found.",

                    Error =
                        ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(
                    ex,
                    "DynamoDB error while saving Email {Email} for Local {Local} and CurrentPage {CurrentPage}.",
                    normalizedEmail,
                    normalizedLocal,
                    normalizedCurrentPage);

                return new ObjectResult(new
                {
                    Message =
                        "A database error occurred while saving the user and updating CompanyIndustry.",

                    Error =
                        ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
            catch (InvalidOperationException ex)
            {
                /*
                 * This commonly indicates a missing AWS or SES
                 * environment variable.
                 *
                 * Because SES configuration is checked after DynamoDB,
                 * the database transaction has already succeeded unless
                 * the missing configuration was needed to create the
                 * DynamoDB client.
                 */
                _logger.LogError(
                    ex,
                    "SaveEmail encountered invalid configuration for Email {Email}, Local {Local}.",
                    normalizedEmail,
                    normalizedLocal);

                return new ObjectResult(new
                {
                    Message =
                        "The request could not be completed because an AWS or email-service setting is not configured correctly.",

                    EmailSent =
                        false,

                    Email =
                        normalizedEmail,

                    Local =
                        normalizedLocal,

                    CurrentPage =
                        normalizedCurrentPage,

                    Error =
                        ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "SaveEmail failed for Email {Email}, Local {Local}, CurrentPage {CurrentPage}.",
                    normalizedEmail,
                    normalizedLocal,
                    normalizedCurrentPage);

                return new ObjectResult(new
                {
                    Message =
                        "An unexpected error occurred while saving the user, updating CompanyIndustry, or sending the email.",

                    Error =
                        ex.Message
                })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
        }


        public class SaveEmailResponse
        {
            public string Message { get; set; } = string.Empty;

            public string UserId { get; set; } = string.Empty;

            public string CompanyIndustryId { get; set; } = string.Empty;

            public string Email { get; set; } = string.Empty;

            public string Local { get; set; } = string.Empty;
        }

        private static bool IsValidEmail(string email)
        {
            try
            {
                var address = new MailAddress(email);

                return string.Equals(
                    address.Address,
                    email,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        [Function("SaveCompanyIndustryLocal")]
        public async Task<IActionResult> SaveCompanyIndustryLocal(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var companyData = System.Text.Json.JsonSerializer.Deserialize<CompanyIndustryLocalRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (companyData == null)
            {
                return new BadRequestObjectResult("Invalid request body.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string companyId;

                while (true)
                {
                    companyId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM CompanyIndustry WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = companyId }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                        break;
                }

                var createdDate = DateTime.UtcNow.ToString("o");

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = @"INSERT INTO CompanyIndustry VALUE {
        'Id': ?,
        'Name': ?,
        'Industry': ?,
        'UserId': ?,
        'Local': ?,
        'CreatedDate': ?
    }",
                    Parameters = new List<AttributeValue>
    {
        new AttributeValue { S = companyId },
        new AttributeValue { S = companyData.Name },
        new AttributeValue { S = companyData.Industry },
        new AttributeValue { S = companyData.UserId ?? "" },
        new AttributeValue { S = companyData.Local ?? "" },
        new AttributeValue { S = createdDate }
    }
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new
                {
                    Message = "Company, industry, and local data saved successfully",
                    CompanyId = companyId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving company industry local data failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("UpdateCompanyIndustryUserIdLocal")]
        public async Task<IActionResult> UpdateCompanyIndustryUserIdLocal(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var updateData = System.Text.Json.JsonSerializer.Deserialize<UpdateCompanyIndustryUserIdLocalRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (updateData == null ||
                string.IsNullOrWhiteSpace(updateData.UserId) ||
                string.IsNullOrWhiteSpace(updateData.NewUserId))
            {
                return new BadRequestObjectResult("UserId, NewUserId, and Local are required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = @"SELECT ""Id"" FROM ""CompanyIndustry"" WHERE ""UserId"" = ?",
                    Parameters = new List<AttributeValue>
    {
        new AttributeValue { S = updateData.UserId }
    }
                };

                var selectResult = await client.ExecuteStatementAsync(selectRequest);

                var existingItem = selectResult.Items.FirstOrDefault();

                if (existingItem == null)
                {
                    return new NotFoundObjectResult("No CompanyIndustry record found for the provided UserId.");
                }

                var idKey = existingItem.Keys.FirstOrDefault(k =>
                    string.Equals(k, "Id", StringComparison.OrdinalIgnoreCase));

                if (idKey == null || string.IsNullOrWhiteSpace(existingItem[idKey].S))
                {
                    return new BadRequestObjectResult(
                        $"Record found, but Id was missing. Returned keys: {string.Join(", ", existingItem.Keys)}"
                    );
                }

                if (updateData == null ||
    string.IsNullOrWhiteSpace(updateData.UserId) ||
    string.IsNullOrWhiteSpace(updateData.NewUserId) ||
    string.IsNullOrWhiteSpace(updateData.Name))
                {
                    return new BadRequestObjectResult("UserId, NewUserId, and Name are required.");
                }

                var companyIndustryId = existingItem[idKey].S;

                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = "UPDATE \"CompanyIndustry\" SET \"UserId\" = ? WHERE \"Id\" = ? AND \"Name\" = ?",
                    Parameters = new List<AttributeValue>
    {
        new AttributeValue { S = updateData.NewUserId },
        new AttributeValue { S = companyIndustryId },
        new AttributeValue { S = updateData.Name }
    }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    Message = "CompanyIndustry UserId updated successfully",
                    Id = companyIndustryId,
                    OldUserId = updateData.UserId,
                    NewUserId = updateData.NewUserId
                });
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex,
                    "DynamoDB update failed. ErrorCode: {ErrorCode}, StatusCode: {StatusCode}, Message: {Message}",
                    ex.ErrorCode,
                    ex.StatusCode,
                    ex.Message);

                return new ObjectResult(new
                {
                    Error = ex.Message,
                    ex.ErrorCode,
                    ex.StatusCode
                })
                {
                    StatusCode = 500
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Updating company industry userId failed.");

                return new ObjectResult(ex.Message)
                {
                    StatusCode = 500
                };
            }
        }

        public class UpdateCompanyIndustryUserIdLocalRequest
        {
            public string UserId { get; set; }
            public string NewUserId { get; set; }
            public string Name { get; set; }
        }

        public class CompanyIndustryLocalRequest
        {
            public string Name { get; set; }
            public string Industry { get; set; }
            public string UserId { get; set; }
            public string Local { get; set; }
        }

        [Function("SaveSubscription")]
        public async Task<IActionResult> SaveSubscription(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var subscriptionData = System.Text.Json.JsonSerializer.Deserialize<SubscriptionRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate unique GUID
                string subscriptionGuid;

                while (true)
                {
                    subscriptionGuid = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Subscriptions WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = subscriptionGuid }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                        break;
                }

                // Current UTC timestamp
                var timestamp = DateTime.UtcNow.ToString("o");

                // Insert subscription record
                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                INSERT INTO Subscriptions VALUE
                {
                    'Id': ?,
                    'UserId': ?,
                    'SubscriptionId': ?,
                    'SubscriptionStatus': ?,
                    'PriceId': ?,
                    'ProductId': ?,
                    'Name': ?,
                    'Price': ?,
                    'Recurring': ?,
                    'TimeStamp': ?
                }",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = subscriptionGuid },
                new AttributeValue { S = subscriptionData.UserId },
                new AttributeValue { S = subscriptionData.SubscriptionId },
                new AttributeValue { S = subscriptionData.SubscriptionStatus },
                new AttributeValue { S = subscriptionData.PriceId },
                new AttributeValue { S = subscriptionData.ProductId },
                new AttributeValue { S = subscriptionData.Name },
                new AttributeValue { N = subscriptionData.Price.ToString() },
                new AttributeValue { BOOL = subscriptionData.Recurring },
                new AttributeValue { S = timestamp }
            }
                };

                await client.ExecuteStatementAsync(insertRequest);
                if (funnelRecoverySequence is not null)
                    await funnelRecoverySequence.SuppressSubscribedSafelyAsync(subscriptionData.UserId, subscriptionGuid);

                return new OkObjectResult(new
                {
                    Message = "Subscription saved successfully",
                    Id = subscriptionGuid
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving subscription failed.");
                return new StatusCodeResult(500);
            }
        }

        public class SubscriptionRequest
        {
            public string UserId { get; set; }

            public string SubscriptionId { get; set; }

            public string SubscriptionStatus { get; set; }

            public string PriceId { get; set; }

            public string ProductId { get; set; }

            public string Name { get; set; }

            public decimal Price { get; set; }

            public bool Recurring { get; set; }
        }

        [Function("SavePrompt")]
        public async Task<IActionResult> SavePrompt([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var promptData = System.Text.Json.JsonSerializer.Deserialize<PromptRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string promptId;
                while (true)
                {
                    promptId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Prompt WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = promptId }
                        }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);
                    if (!existingIdResult.Items.Any())
                        break;
                }

                var statement = @"
    INSERT INTO Prompt VALUE {
        'Id': ?, 
        'Prompt': ?, 
        'CompanyIdeaId': ?, 
        'UserId': ?, 
        'TimeStamp': ?,
        'InviteId': ?
    }";

                var parameters = new List<AttributeValue>
{
    new AttributeValue { S = promptId },
    new AttributeValue { S = promptData.Prompt },
    new AttributeValue { S = promptData.CompanyIdeaId ?? string.Empty },
    new AttributeValue { S = promptData.UserId },
    new AttributeValue { S = promptData.TimeStamp },
    new AttributeValue { S = promptData.InviteId ?? string.Empty }
};

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new { Message = "Prompt saved successfully", PromptId = promptId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving prompt failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetInviteClientImages")]
        public async Task<IActionResult> GetInviteClientImages(
    [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
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

            try
            {
                string inviteId = req.Query["InviteId"];

                // Support POST body as well
                if (string.IsNullOrEmpty(inviteId))
                {
                    var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

                    var request = System.Text.Json.JsonSerializer.Deserialize<GetInviteClientImagesRequest>(
                        requestBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    inviteId = request?.InviteId;
                }

                if (string.IsNullOrEmpty(inviteId))
                {
                    return new BadRequestObjectResult("InviteId is required.");
                }

                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM InviteClientImages WHERE InviteId = ?",
                    ConsistentRead = true,
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = inviteId }
            }
                };

                var allItems = new List<Dictionary<string, AttributeValue>>();
                string nextToken = null;

                do
                {
                    selectRequest.NextToken = nextToken;

                    var result = await client.ExecuteStatementAsync(selectRequest);

                    allItems.AddRange(result.Items);

                    nextToken = result.NextToken;

                } while (!string.IsNullOrEmpty(nextToken));

                var images = allItems.Select(item => new
                {
                    Id = item.ContainsKey("Id") ? item["Id"].S : null,
                    InviteId = item.ContainsKey("InviteId") ? item["InviteId"].S : null,
                    ImageId = item.ContainsKey("ImageId") ? item["ImageId"].S : null,
                    ImageUrl = item.ContainsKey("ImageUrl") ? item["ImageUrl"].S : null,
                    CountId = item.ContainsKey("CountId") ? item["CountId"].S : null,
                    TimeStamp = item.ContainsKey("TimeStamp") ? item["TimeStamp"].S : null,
                    Style = item.ContainsKey("Style") ? item["Style"].S : null,
                    StylePrompt = item.ContainsKey("StylePrompt") ? item["StylePrompt"].S : null,
                    Important = item.ContainsKey("Important") ? item["Important"].S : null,
                    Project = item.ContainsKey("Project") ? item["Project"].S : null,
                    OutputRequirements = item.ContainsKey("OutputRequirements") ? item["OutputRequirements"].S : null
                }).ToList();

                return new OkObjectResult(images);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Getting invite client images failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetDesignModeJobStatusByImageId")]
        public async Task<IActionResult> GetDesignModeJobStatusByImageId(
    [HttpTrigger(
        AuthorizationLevel.Function,
        "get",
        "post",
        Route = null)]
    HttpRequest req)
        {
            /*
             * ==========================================================
             * VALIDATE ORIGIN
             * ==========================================================
             */

            string? origin =
                req.Headers["Origin"].FirstOrDefault();

            string? hostUrl =
                Environment.GetEnvironmentVariable("HostUrl");

            if (string.IsNullOrWhiteSpace(origin) ||
                string.IsNullOrWhiteSpace(hostUrl) ||
                !string.Equals(
                    origin,
                    hostUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new UnauthorizedResult();
            }

            AmazonDynamoDBClient? client = null;

            try
            {
                /*
                 * ==========================================================
                 * READ IMAGE ID
                 * ==========================================================
                 */

                string? imageId =
                    req.Query["ImageId"].FirstOrDefault();

                /*
                 * Support POST request body:
                 *
                 * {
                 *   "imageId": "..."
                 * }
                 */
                if (string.IsNullOrWhiteSpace(imageId))
                {
                    string requestBody;

                    using (var reader = new StreamReader(req.Body))
                    {
                        requestBody =
                            await reader.ReadToEndAsync();
                    }

                    if (!string.IsNullOrWhiteSpace(requestBody))
                    {
                        GetDesignModeJobStatusRequest? request =
                            System.Text.Json.JsonSerializer.Deserialize<
                                GetDesignModeJobStatusRequest>(
                                requestBody,
                                new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });

                        imageId = request?.ImageId;
                    }
                }

                if (string.IsNullOrWhiteSpace(imageId))
                {
                    return new BadRequestObjectResult(
                        new
                        {
                            error = "ImageId is required."
                        });
                }

                /*
                 * ==========================================================
                 * CREATE DYNAMODB CLIENT
                 * ==========================================================
                 */

                client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string tableName =
                    Environment.GetEnvironmentVariable(
                        "DynamoDbTableName")
                    ?? "DesignModeJobs";

                /*
                 * ==========================================================
                 * FIND JOBS BY IMAGE ID
                 * ==========================================================
                 *
                 * Since ImageId is not the table partition key, this uses a
                 * PartiQL SELECT and reads all matching pages.
                 */

                var selectRequest =
                    new ExecuteStatementRequest
                    {
                        Statement =
                            $"SELECT * FROM \"{tableName}\" " +
                            "WHERE ImageId = ?",

                        ConsistentRead = true,

                        Parameters =
                            new List<AttributeValue>
                            {
                        new AttributeValue
                        {
                            S = imageId
                        }
                            }
                    };

                var matchingItems =
                    new List<Dictionary<string, AttributeValue>>();

                string? nextToken = null;

                do
                {
                    selectRequest.NextToken =
                        nextToken;

                    ExecuteStatementResponse result =
                        await client.ExecuteStatementAsync(
                            selectRequest);

                    matchingItems.AddRange(
                        result.Items);

                    nextToken =
                        result.NextToken;
                }
                while (!string.IsNullOrWhiteSpace(nextToken));

                /*
                 * ==========================================================
                 * HANDLE NO MATCH
                 * ==========================================================
                 */

                if (matchingItems.Count == 0)
                {
                    return new NotFoundObjectResult(
                        new
                        {
                            imageId,
                            found = false,
                            status = "NotFound",
                            message =
                                "No DesignModeJobs record was found for this image ID."
                        });
                }

                /*
                 * There may be more than one generation job for an image.
                 * Select the job with the most recent CreatedAtUtc value.
                 */
                Dictionary<string, AttributeValue> latestItem =
                    matchingItems
                        .OrderByDescending(
                            item => GetStringValue(
                                item,
                                "CreatedAtUtc"))
                        .First();

                string? status =
                    GetStringValue(
                        latestItem,
                        "Status");

                string? currentStep =
                    GetStringValue(
                        latestItem,
                        "CurrentStep");

                string? videoUrl =
                    GetStringValue(
                        latestItem,
                        "VideoUrl");

                string? error =
                    GetStringValue(
                        latestItem,
                        "Error");

                bool isCompleted =
                    string.Equals(
                        status,
                        "Completed",
                        StringComparison.OrdinalIgnoreCase);

                bool isFailed =
                    string.Equals(
                        status,
                        "Failed",
                        StringComparison.OrdinalIgnoreCase);

                bool isProcessing =
                    !isCompleted &&
                    !isFailed;

                /*
                 * ==========================================================
                 * RETURN JOB STATUS
                 * ==========================================================
                 */

                return new OkObjectResult(
                    new
                    {
                        found = true,

                        jobId =
                            GetStringValue(
                                latestItem,
                                "JobId"),

                        imageId =
                            GetStringValue(
                                latestItem,
                                "ImageId"),

                        status =
                            status ?? "Unknown",

                        currentStep =
                            currentStep ?? "Unknown",

                        isProcessing,
                        isCompleted,
                        isFailed,

                        videoUrl,

                        runwareTaskUuid =
                            GetStringValue(
                                latestItem,
                                "RunwareTaskUuid"),

                        videoSubmissionStatus =
                            GetStringValue(
                                latestItem,
                                "VideoSubmissionStatus"),

                        videoDeliveryMethod =
                            GetStringValue(
                                latestItem,
                                "VideoDeliveryMethod"),

                        videoPollAttempt =
                            GetIntValue(
                                latestItem,
                                "VideoPollAttempt"),

                        error,

                        imageDescription =
                            GetStringValue(
                                latestItem,
                                "ImageDescription"),

                        script =
                            GetStringValue(
                                latestItem,
                                "Script"),

                        name =
                            GetStringValue(
                                latestItem,
                                "Name"),

                        industry =
                            GetStringValue(
                                latestItem,
                                "Industry"),

                        conceptNumber =
                            GetIntValue(
                                latestItem,
                                "ConceptNumber"),

                        imageUrl =
                            GetStringValue(
                                latestItem,
                                "ImageUrl"),

                        createdAtUtc =
                            GetStringValue(
                                latestItem,
                                "CreatedAtUtc"),

                        updatedAtUtc =
                            GetStringValue(
                                latestItem,
                                "UpdatedAtUtc"),

                        completedAtUtc =
                            GetStringValue(
                                latestItem,
                                "CompletedAtUtc")
                    });
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid JSON received by GetDesignModeJobStatusByImageId.");

                return new BadRequestObjectResult(
                    new
                    {
                        error =
                            "The request body contains invalid JSON."
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Getting the DesignMode job status by image ID failed.");

                return new ObjectResult(
                    new
                    {
                        error =
                            "Unable to retrieve the generation job status."
                    })
                {
                    StatusCode =
                        StatusCodes.Status500InternalServerError
                };
            }
            finally
            {
                client?.Dispose();
            }
        }

        public class GetInviteClientImagesRequest
        {
            public string InviteId { get; set; }
        }

        [Function("SaveInviteClientImage")]
        public async Task<IActionResult> SaveInviteClientImage(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var imageData = System.Text.Json.JsonSerializer.Deserialize<InviteClientImageRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string id;

                while (true)
                {
                    id = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM InviteClientImages WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = id }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                        break;
                }

                var statement = @"
INSERT INTO InviteClientImages VALUE {
    'Id': ?,
    'InviteId': ?,
    'ImageId': ?,
    'ImageUrl': ?,
    'CountId': ?,
    'TimeStamp': ?,
    'Style': ?,
    'StylePrompt': ?,
    'Important': ?,
    'Project': ?,
    'OutputRequirements': ?
}";

                var parameters = new List<AttributeValue>
{
    new AttributeValue { S = id },
    new AttributeValue { S = imageData.InviteId },
    new AttributeValue { S = imageData.ImageId },
    new AttributeValue { S = imageData.ImageUrl },
    new AttributeValue { S = imageData.CountId },
    new AttributeValue { S = imageData.TimeStamp },
    new AttributeValue { S = imageData.Style ?? string.Empty },
    new AttributeValue { S = imageData.StylePrompt ?? string.Empty },
    new AttributeValue { S = imageData.Important ?? string.Empty },
    new AttributeValue { S = imageData.Project ?? string.Empty },
    new AttributeValue { S = imageData.OutputRequirements ?? string.Empty }
};

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new
                {
                    Message = "Invite client image saved successfully",
                    Id = id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving invite client image failed.");
                return new StatusCodeResult(500);
            }
        }

        public class InviteClientImageRequest
        {
            public string InviteId { get; set; }
            public string ImageId { get; set; }
            public string ImageUrl { get; set; }
            public string CountId { get; set; }
            public string TimeStamp { get; set; }

            public string Style { get; set; }
            public string StylePrompt { get; set; }
            public string Important { get; set; }
            public string Project { get; set; }
            public string OutputRequirements { get; set; }
        }

        [Function("GetPromptIdByCompanyIdeaIdAndPrompt")]
        public async Task<IActionResult> GetPromptIdByCompanyIdeaIdAndPrompt(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var requestData = System.Text.Json.JsonSerializer.Deserialize<GetPromptIdRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var statement = @"
SELECT Id
FROM ""Prompt""
WHERE begins_with(CompanyIdeaId, ?)
AND Prompt = ?";

                var parameters = new List<AttributeValue>
        {
            new AttributeValue { S = requestData.CompanyIdeaId },
            new AttributeValue { S = requestData.Prompt }
        };

                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                var result = await client.ExecuteStatementAsync(selectRequest);

                if (result.Items != null && result.Items.Any())
                {
                    var promptId = result.Items.First()["Id"].S;

                    return new OkObjectResult(new
                    {
                        Found = true,
                        PromptId = promptId
                    });
                }

                return new OkObjectResult(new
                {
                    Found = false,
                    PromptId = (string)null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get prompt id.");
                return new StatusCodeResult(500);
            }
        }

        public class GetPromptIdRequest
        {
            public string CompanyIdeaId { get; set; }
            public string Prompt { get; set; }
        }

        [Function("GetInviteClientById")]
        public async Task<IActionResult> GetInviteClientById(
            [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            // =========================
            // 🔹 Origin validation
            // =========================
            if (referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // =========================
            // 🔹 Read id from query
            // =========================
            string id = req.Query["Id"];

            // =========================
            // 🔹 Read body fallback
            // =========================
            if (string.IsNullOrWhiteSpace(id))
            {
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

                if (!string.IsNullOrWhiteSpace(requestBody))
                {
                    var bodyData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        requestBody,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                    if (bodyData != null &&
                        bodyData.TryGetValue("Id", out var bodyId))
                    {
                        id = bodyId;
                    }
                }
            }

            // =========================
            // 🔹 Validation
            // =========================
            if (string.IsNullOrWhiteSpace(id))
            {
                return new BadRequestObjectResult("Id is required.");
            }

            try
            {
                // =========================
                // 🔹 DynamoDB client
                // =========================
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // =========================
                // 🔹 SELECT statement
                // =========================
                var request = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM InviteClient WHERE Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = id }
            }
                };

                var response = await client.ExecuteStatementAsync(request);

                // =========================
                // 🔹 Not found
                // =========================
                if (response.Items == null || response.Items.Count == 0)
                {
                    return new NotFoundObjectResult(
                        $"No record found for id: {id}");
                }

                var item = response.Items.FirstOrDefault();

                if (item == null)
                {
                    return new NotFoundObjectResult(
                        $"No valid record found for id: {id}");
                }

                // =========================
                // 🔹 Clean response model
                // =========================
                var inviteData = new InviteClientData
                {
                    Id = item.ContainsKey("Id")
                        ? item["Id"].S
                        : null,

                    Name = item.ContainsKey("Name")
                        ? item["Name"].S
                        : null,

                    Industry = item.ContainsKey("Industry")
                        ? item["Industry"].S
                        : null,

                    Prompt = item.ContainsKey("Prompt")
                        ? item["Prompt"].S
                        : null,

                    Email = item.ContainsKey("Email")
                        ? item["Email"].S
                        : null,

                    ImagePath = item.ContainsKey("ImagePath")
                        ? item["ImagePath"].S
                        : null,

                    ImageId = item.ContainsKey("ImageId")
                        ? item["ImageId"].S
                        : null,

                    UseSameImageSeedForAll =
                        item.TryGetValue("UseSameImageSeedForAll", out var useSeedAttr) &&
                        useSeedAttr.BOOL == true,

                    TimeStamp = item.ContainsKey("Timestamp")
                        ? item["Timestamp"].S
                        : null
                };

                return new OkObjectResult(inviteData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Fetching invite client by id failed.");

                return new ObjectResult(new
                {
                    error = ex.Message
                })
                {
                    StatusCode = 500
                };
            }
        }
        // =========================
        // 🔹 Response model
        // =========================
        public class InviteClientData
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public string Industry { get; set; }

            public string Prompt { get; set; }

            public string Email { get; set; }

            public string ImagePath { get; set; }

            public string ImageId { get; set; }

            public bool UseSameImageSeedForAll { get; set; }

            public string TimeStamp { get; set; }
        }

        [Function("GetInviteClientByEmail")]
        public async Task<IActionResult> GetInviteClientByEmail(
            [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequest req)
        {
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

            // =========================
            // 🔹 Read query params first
            // =========================
            string email = req.Query["email"];
            string id = req.Query["id"];

            // =========================
            // 🔹 Read body fallback
            // =========================
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            if (!string.IsNullOrEmpty(requestBody))
            {
                var bodyData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (bodyData != null)
                {
                    if (bodyData.ContainsKey("email"))
                        email = bodyData["email"];

                    if (bodyData.ContainsKey("id"))
                        id = bodyData["id"];
                }
            }

            // =========================
            // 🔹 Validation
            // =========================
            if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(id))
            {
                return new BadRequestObjectResult("Email or Id is required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // =========================
                // 🔹 Build query (ID preferred)
                // =========================
                string statement;
                var parameters = new List<AttributeValue>();

                if (!string.IsNullOrEmpty(id))
                {
                    statement = "SELECT * FROM InviteClient WHERE Id = ?";
                    parameters.Add(new AttributeValue { S = id });
                }
                else
                {
                    statement = "SELECT * FROM InviteClient WHERE Email = ?";
                    parameters.Add(new AttributeValue { S = email });
                }

                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                if (result.Items == null || !result.Items.Any())
                {
                    return new NotFoundObjectResult(
                        !string.IsNullOrEmpty(id)
                            ? $"No records found for id: {id}"
                            : $"No records found for email: {email}"
                    );
                }

                // Get latest record (safe fallback behavior)
                var sortedItem = result.Items[result.Items.Count - 1];

                if (sortedItem == null)
                {
                    return new NotFoundObjectResult("No valid record found.");
                }

                return new OkObjectResult(sortedItem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching invite client failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetInviteClientByEmailAndId")]
        public async Task<IActionResult> GetInviteClientByEmailAndId(
    [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // =========================
            // 🔹 Read query params
            // =========================
            string email = req.Query["email"];
            string id = req.Query["id"];

            // =========================
            // 🔹 Read body fallback
            // =========================
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            if (!string.IsNullOrEmpty(requestBody))
            {
                var bodyData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (bodyData != null)
                {
                    if (bodyData.ContainsKey("email"))
                        email = bodyData["email"];

                    if (bodyData.ContainsKey("id"))
                        id = bodyData["id"];
                }
            }

            // =========================
            // 🔹 Validation (BOTH REQUIRED)
            // =========================
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(id))
            {
                return new BadRequestObjectResult("Both Email and Id are required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // =========================
                // 🔹 Query with BOTH conditions
                // =========================
                string statement = "SELECT * FROM InviteClient WHERE Id = ? AND Email = ?";

                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = id },
                new AttributeValue { S = email }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                if (result.Items == null || !result.Items.Any())
                {
                    return new NotFoundObjectResult(
                        $"No record found for Id: {id} and Email: {email}"
                    );
                }

                // Return latest match (safe fallback)
                var sortedItem = result.Items[result.Items.Count - 1];

                if (sortedItem == null)
                {
                    return new NotFoundObjectResult("No valid record found.");
                }

                return new OkObjectResult(sortedItem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching invite client by email and id failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetPromptInviteClientByEmail")]
        public async Task<IActionResult> GetPromptInviteClientByEmail(
[HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequest req)
        {
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

            // Try getting email from query first
            string email = req.Query["email"];

            // If not in query, try body
            if (string.IsNullOrEmpty(email))
            {
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                if (!string.IsNullOrEmpty(requestBody))
                {
                    var bodyData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        requestBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (bodyData != null && bodyData.ContainsKey("email"))
                    {
                        email = bodyData["email"];
                    }
                }
            }

            if (string.IsNullOrEmpty(email))
            {
                return new BadRequestObjectResult("Email is required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query by Email
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM InviteClient WHERE Email = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = email }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                if (result.Items == null || !result.Items.Any())
                {
                    return new NotFoundObjectResult($"No records found for email: {email}");
                }

                var sortedItem = result.Items
    .OrderByDescending(item =>
    {
        if (item.ContainsKey("Timestamp") && item["Timestamp"].S != null)
        {
            if (DateTime.TryParse(item["Timestamp"].S, out var parsedDate))
            {
                return parsedDate;
            }
        }

        return DateTime.MinValue; // fallback for invalid/missing timestamps
    })
    .FirstOrDefault();

                if (sortedItem == null)
                {
                    return new NotFoundObjectResult($"No valid records found for email: {email}");
                }

                var response = new InviteClientResponseDto
                {
                    Email = sortedItem.ContainsKey("Email") ? sortedItem["Email"].S : null,
                    Name = sortedItem.ContainsKey("Name") ? sortedItem["Name"].S : null,
                    Industry = sortedItem.ContainsKey("Industry") ? sortedItem["Industry"].S : null,
                    Prompt = sortedItem.ContainsKey("Prompt") ? sortedItem["Prompt"].S : null,
                    Imagepath = sortedItem.ContainsKey("Imagepath") ? sortedItem["Imagepath"].S : null,
                };

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching invite client by email failed.");
                return new StatusCodeResult(500);
            }
        }

        public class InviteClientResponseDto
        {
            public string Email { get; set; }
            public string Name { get; set; }
            public string Industry { get; set; }
            public string Prompt { get; set; }
            public string Imagepath { get; set; }
        }

        [Function("GetImageUrlByImageId")]
        public async Task<IActionResult> GetImageUrlByImageId(
            [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // ✅ Get imageId from query
            string imageId = req.Query["imageId"];

            // ✅ Fallback to body
            if (string.IsNullOrEmpty(imageId))
            {
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                if (!string.IsNullOrEmpty(requestBody))
                {
                    var bodyData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        requestBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (bodyData != null && bodyData.TryGetValue("imageId", out var bodyImageId))
                    {
                        imageId = bodyImageId;
                    }
                }
            }

            if (string.IsNullOrEmpty(imageId))
            {
                return new BadRequestObjectResult("ImageId is required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ✅ ONLY query ImageIdeas
                var imageQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT ImageUrl FROM ImageIdeas WHERE Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = imageId }
            }
                };

                var imageResult = await client.ExecuteStatementAsync(imageQuery);

                if (imageResult.Items == null || !imageResult.Items.Any())
                {
                    return new NotFoundObjectResult($"No ImageIdeas record found for ImageId: {imageId}");
                }

                var imageItem = imageResult.Items.First();

                if (!imageItem.TryGetValue("ImageUrl", out var imageUrlAttr) ||
                    string.IsNullOrEmpty(imageUrlAttr.S))
                {
                    return new NotFoundObjectResult($"ImageUrl not found for ImageId: {imageId}");
                }

                return new OkObjectResult(new
                {
                    imageId,
                    imageUrl = imageUrlAttr.S
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching image URL by imageId failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("CheckImageUrlExists")]
        public async Task<IActionResult> CheckImageUrlExists(
    [HttpTrigger(AuthorizationLevel.Function, "post", "get")] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            // ✅ Get imageUrl from query
            string imageUrl = req.Query["imageUrl"];

            // ✅ Fallback to body
            if (string.IsNullOrEmpty(imageUrl))
            {
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

                if (!string.IsNullOrEmpty(requestBody))
                {
                    var bodyData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        requestBody,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                    if (bodyData != null &&
                        bodyData.TryGetValue("ImageUrl", out var bodyImageUrl))
                    {
                        imageUrl = bodyImageUrl;
                    }
                }
            }

            if (string.IsNullOrEmpty(imageUrl))
            {
                return new BadRequestObjectResult("ImageUrl is required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ✅ Check if ImageUrl already exists
                var query = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM ImageIdeas WHERE ImageUrl = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = imageUrl }
            }
                };

                var result = await client.ExecuteStatementAsync(query);

                bool exists = result.Items != null && result.Items.Any();

                return new OkObjectResult(new
                {
                    exists = exists
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checking ImageUrl existence failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("UpdateInviteClientImageIdByEmail")]
        public async Task<IActionResult> UpdateInviteClientImageIdByEmail(
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
            var requestData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            string email = null;
            string imageId = null;

            if (requestData != null)
            {
                if (requestData.ContainsKey("email"))
                    email = requestData["email"];

                if (requestData.ContainsKey("imageId"))
                    imageId = requestData["imageId"];
            }

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(imageId))
            {
                return new BadRequestObjectResult("Both email and imageId are required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Step 1: Find InviteClient by Email
                var checkRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, Name, Email, \"Timestamp\" FROM InviteClient WHERE Email = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = email }
            }
                };

                var existingResult = await client.ExecuteStatementAsync(checkRequest);

                if (!existingResult.Items.Any())
                {
                    return new NotFoundObjectResult($"No InviteClient found for email: {email}");
                }

                var item = existingResult.Items
    .OrderByDescending(item =>
    {
        if (item.ContainsKey("Timestamp") && item["Timestamp"].S != null)
        {
            if (DateTime.TryParse(item["Timestamp"].S, out var parsedDate))
            {
                return parsedDate;
            }
        }

        return DateTime.MinValue;
    })
    .First();
                var id = item["Id"].S;
                var name = item["Name"].S;
                var existingEmail = item["Email"].S;

                // Step 2: Update using all required key attributes
                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = "UPDATE InviteClient SET ImageId = ? WHERE Id = ? AND Name = ? AND Email = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = imageId },
                new AttributeValue { S = id },
                new AttributeValue { S = name },
                new AttributeValue { S = existingEmail }
            }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    Message = "InviteClient ImageId updated successfully.",
                    Id = id,
                    Name = name,
                    Email = existingEmail,
                    ImageId = imageId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Updating InviteClient ImageId by email failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("UpdateInviteClientImageIdByIdAndName")]
        public async Task<IActionResult> UpdateInviteClientImageIdByIdAndName(
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
            var requestData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            string id = null;
            string name = null;
            string imageId = null;

            if (requestData != null)
            {
                if (requestData.ContainsKey("id"))
                    id = requestData["id"];

                if (requestData.ContainsKey("name"))
                    name = requestData["name"];

                if (requestData.ContainsKey("imageId"))
                    imageId = requestData["imageId"];
            }

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(imageId))
            {
                return new BadRequestObjectResult("id, name, and imageId are required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = "UPDATE InviteClient SET ImageId = ? WHERE Id = ? AND Name = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = imageId },
                new AttributeValue { S = id },
                new AttributeValue { S = name }
            }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    Message = "InviteClient ImageId updated successfully.",
                    Id = id,
                    Name = name,
                    ImageId = imageId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Updating InviteClient ImageId by id and name failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("SaveInviteClient")]
        public async Task<IActionResult> SaveInviteClient(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var inviteData = System.Text.Json.JsonSerializer.Deserialize<InviteClientRequest>(
                requestBody,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate unique GUID
                string inviteId;

                while (true)
                {
                    inviteId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM InviteClient WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = inviteId }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                        break;
                }

                // Insert record
                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = @"INSERT INTO InviteClient VALUE 
            {
                'Id': ?, 
                'Name': ?, 
                'Industry': ?, 
                'Prompt': ?, 
                'Email': ?, 
                'Imagepath': ?, 
                'UseSameImageSeedForAll': ?,
                'Timestamp': ?
            }",

                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = inviteId },
                new AttributeValue { S = inviteData.Name ?? string.Empty },
                new AttributeValue { S = inviteData.Industry ?? string.Empty },
                new AttributeValue { S = inviteData.Prompt ?? string.Empty },
                new AttributeValue { S = inviteData.Email ?? string.Empty },
                new AttributeValue { S = inviteData.ImageUrls ?? string.Empty },
                new AttributeValue { BOOL = inviteData.UseSameImageSeedForAll },
                new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            }
                };

                await client.ExecuteStatementAsync(insertRequest);

                // Return ONLY the ID
                return new OkObjectResult(new
                {
                    Id = inviteId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving invite client failed.");

                return new StatusCodeResult(500);
            }
        }

        // ✅ Updated Request Model
        public class InviteClientRequest
        {
            public string Name { get; set; }
            public string Industry { get; set; }
            public string Prompt { get; set; }
            public string Email { get; set; }
            public string ImageUrls { get; set; }
            public bool UseSameImageSeedForAll { get; set; } // ✅ New property
        }


        [Function("GetAudioSamplesByVideoTextUrlActor")]
        public async Task<IActionResult> GetAudioSamplesByVideoTextUrlActor([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
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
            var queryData = System.Text.Json.JsonSerializer.Deserialize<AudioSampleRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var statement =
    "SELECT Id, VideoId, Url, \"Text\", VoiceOverActor, \"TimeStamp\" " +
    "FROM AudioSample " +
    "WHERE VideoId = ? AND \"Text\" = ? AND VoiceOverActor = ?";


                var parameters = new List<AttributeValue>
        {
            new AttributeValue { S = queryData.VideoId },
            new AttributeValue { S = queryData.Text },
            new AttributeValue { S = queryData.VoiceOverActor }
        };

                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                var response = await client.ExecuteStatementAsync(queryRequest);

                var results = response.Items.Select(item => new
                {
                    Id = item["Id"].S,
                    VideoId = item["VideoId"].S,
                    Url = item.ContainsKey("Url") ? item["Url"].S : null,
                    Text = item.ContainsKey("Text") ? item["Text"].S : null,
                    VoiceOverActor = item.ContainsKey("VoiceOverActor") ? item["VoiceOverActor"].S : null,
                    TimeStamp = item.ContainsKey("TimeStamp") ? item["TimeStamp"].S : null
                });

                return new OkObjectResult(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reading audio samples by VideoId, Text, Url, and VoiceOverActor failed.");
                return new StatusCodeResult(500);
            }
        }

        public class CTATextRequest
        {
            public string VideoId { get; set; }
            public string Text { get; set; }
            public string TimeStamp { get; set; }
        }

        [Function("SaveCTAText")]
        public async Task<IActionResult> SaveCTAText([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
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
            var ctaData = System.Text.Json.JsonSerializer.Deserialize<CTATextRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Check if VideoId already exists
                var checkVideoRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, VideoId FROM CTAText WHERE VideoId = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = ctaData.VideoId }
            }
                };

                var existingVideoResult = await client.ExecuteStatementAsync(checkVideoRequest);

                if (existingVideoResult.Items.Any())
                {
                    var item = existingVideoResult.Items.First();
                    var id = item["Id"].S;
                    var videoId = item["VideoId"].S;

                    var updateStatement =
                        "UPDATE CTAText SET \"Text\" = ?, \"TimeStamp\" = ? WHERE \"Id\" = ? AND \"VideoId\" = ?";

                    var updateParameters = new List<AttributeValue>
            {
                new AttributeValue { S = ctaData.Text },
                new AttributeValue { S = ctaData.TimeStamp },
                new AttributeValue { S = id },
                new AttributeValue { S = videoId }
            };

                    var updateRequest = new ExecuteStatementRequest
                    {
                        Statement = updateStatement,
                        Parameters = updateParameters
                    };

                    await client.ExecuteStatementAsync(updateRequest);

                    return new OkObjectResult(new
                    {
                        Message = "CTA text updated successfully",
                        CTATextId = id
                    });
                }
                else
                {
                    // Generate unique Id
                    string ctaTextId;

                    while (true)
                    {
                        ctaTextId = Guid.NewGuid().ToString();

                        var checkIdRequest = new ExecuteStatementRequest
                        {
                            Statement = "SELECT Id FROM CTAText WHERE Id = ?",
                            Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = ctaTextId }
                    }
                        };

                        var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                        if (!existingIdResult.Items.Any())
                            break;
                    }

                    // INSERT new record
                    var insertStatement =
                        "INSERT INTO CTAText VALUE {" +
                        "'Id': ?, " +
                        "'VideoId': ?, " +
                        "'Text': ?, " +
                        "'TimeStamp': ?" +
                        "}";

                    var parameters = new List<AttributeValue>
            {
                new AttributeValue { S = ctaTextId },
                new AttributeValue { S = ctaData.VideoId },
                new AttributeValue { S = ctaData.Text },
                new AttributeValue { S = ctaData.TimeStamp }
            };

                    var insertRequest = new ExecuteStatementRequest
                    {
                        Statement = insertStatement,
                        Parameters = parameters
                    };

                    await client.ExecuteStatementAsync(insertRequest);

                    return new OkObjectResult(new
                    {
                        Message = "CTA text inserted successfully",
                        CTATextId = ctaTextId
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving CTA text failed.");
                return new StatusCodeResult(500);
            }
        }

        public class VideoIdRequest
        {
            public string VideoId { get; set; }
        }

        public class WebsitePreviewRequest
        {
            public string WebsiteId { get; set; }
            public string CompanyName { get; set; }
            public string Version { get; set; }
            public string TimeStamp { get; set; }
        }

        [Function("SaveWebsitePreview")]
        public async Task<IActionResult> SaveWebsitePreview(
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

            var websiteData = System.Text.Json.JsonSerializer.Deserialize<WebsitePreviewRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate unique Id
                string websitePreviewId;

                while (true)
                {
                    websitePreviewId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Website WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = websitePreviewId }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                        break;
                }

                // INSERT new record
                var insertStatement =
                    "INSERT INTO Website VALUE {" +
                    "'Id': ?, " +
                    "'WebsiteId': ?, " +
                    "'CompanyName': ?, " +
                    "'Version': ?, " +
                    "'TimeStamp': ?" +
                    "}";

                var parameters = new List<AttributeValue>
        {
            new AttributeValue { S = websitePreviewId },
            new AttributeValue { S = websiteData.WebsiteId },
            new AttributeValue { S = websiteData.CompanyName },
            new AttributeValue { S = websiteData.Version },
            new AttributeValue { S = websiteData.TimeStamp }
        };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = insertStatement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new
                {
                    Message = "Website preview saved successfully",
                    WebsitePreviewId = websitePreviewId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving website preview failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("SaveSocialPreview")]
        public async Task<IActionResult> SaveSocialPreview(
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

            var socialData = System.Text.Json.JsonSerializer.Deserialize<WebsitePreviewRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ✅ Generate unique Id (check against Social table now)
                string socialPreviewId;

                while (true)
                {
                    socialPreviewId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Social WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = socialPreviewId }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                    if (!existingIdResult.Items.Any())
                        break;
                }

                // ✅ INSERT into Social table
                var insertStatement =
                    "INSERT INTO Social VALUE {" +
                    "'Id': ?, " +
                    "'WebsiteId': ?, " +   // still same schema
                    "'CompanyName': ?, " +
                    "'Version': ?, " +
                    "'TimeStamp': ?" +
                    "}";

                var parameters = new List<AttributeValue>
        {
            new AttributeValue { S = socialPreviewId },
            new AttributeValue { S = socialData.WebsiteId },
            new AttributeValue { S = socialData.CompanyName },
            new AttributeValue { S = socialData.Version },
            new AttributeValue { S = socialData.TimeStamp }
        };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = insertStatement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new
                {
                    Message = "Social preview saved successfully",
                    SocialPreviewId = socialPreviewId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving social preview failed.");
                return new StatusCodeResult(500);
            }
        }

        public class WebsiteVersionRequest
        {
            public string CompanyName { get; set; }
        }

        [Function("GetSocialByCompany")]
        public async Task<IActionResult> GetSocialByCompany(
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

            var data = System.Text.Json.JsonSerializer.Deserialize<GetWebsitesByCompanyRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (string.IsNullOrWhiteSpace(data?.CompanyName))
            {
                return new BadRequestObjectResult("CompanyName is required");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ✅ Same schema, just different table
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM Social WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = data.CompanyName }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                var socials = result.Items.Select(item => new
                {
                    Id = item.ContainsKey("Id") ? item["Id"].S : null,
                    WebsiteId = item.ContainsKey("WebsiteId") ? item["WebsiteId"].S : null,
                    CompanyName = item.ContainsKey("CompanyName") ? item["CompanyName"].S : null,
                    Version = item.ContainsKey("Version") ? item["Version"].S : null,
                    TimeStamp = item.ContainsKey("TimeStamp") ? item["TimeStamp"].S : null
                }).ToList();

                return new OkObjectResult(new
                {
                    Message = "Social records retrieved successfully",
                    Count = socials.Count,
                    Data = socials
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching social records by company failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetWebsitesByCompany")]
        public async Task<IActionResult> GetWebsitesByCompany(
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

            var data = System.Text.Json.JsonSerializer.Deserialize<GetWebsitesByCompanyRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (string.IsNullOrWhiteSpace(data?.CompanyName))
            {
                return new BadRequestObjectResult("CompanyName is required");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ✅ Query by CompanyName
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM Website WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = data.CompanyName }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                var websites = result.Items.Select(item => new
                {
                    Id = item.ContainsKey("Id") ? item["Id"].S : null,
                    WebsiteId = item.ContainsKey("WebsiteId") ? item["WebsiteId"].S : null,
                    CompanyName = item.ContainsKey("CompanyName") ? item["CompanyName"].S : null,
                    Version = item.ContainsKey("Version") ? item["Version"].S : null,
                    TimeStamp = item.ContainsKey("TimeStamp") ? item["TimeStamp"].S : null
                }).ToList();

                return new OkObjectResult(new
                {
                    Message = "Websites retrieved successfully",
                    Count = websites.Count,
                    Data = websites
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching websites by company failed.");
                return new StatusCodeResult(500);
            }
        }

        public class GetWebsitesByCompanyRequest
        {
            public string CompanyName { get; set; }
        }

        [Function("GetNextSocialVersion")]
        public async Task<IActionResult> GetNextSocialVersion(
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

            var data = System.Text.Json.JsonSerializer.Deserialize<SocialVersionRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query existing records for this company in Social table
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, WebsiteId FROM Social WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = data.CompanyName }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                int count = result.Items.Count;

                string socialId;

                if (count > 0)
                {
                    // Reuse existing SocialId
                    socialId = result.Items.First()["WebsiteId"].S;
                }
                else
                {
                    // Generate new SocialId if none exists
                    socialId = Guid.NewGuid().ToString();
                }

                int nextVersion = count + 1;

                return new OkObjectResult(new
                {
                    CompanyName = data.CompanyName,
                    WebsiteId = socialId,
                    ExistingCount = count,
                    NextVersion = nextVersion
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get social version + id.");
                return new StatusCodeResult(500);
            }
        }

        public class SocialVersionRequest
        {
            public string CompanyName { get; set; }
        }

        [Function("GetNextWebsiteVersion")]
        public async Task<IActionResult> GetNextWebsiteVersion(
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

            var data = System.Text.Json.JsonSerializer.Deserialize<WebsiteVersionRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query existing records for this company
                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, WebsiteId FROM Website WHERE CompanyName = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = data.CompanyName }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                int count = result.Items.Count;

                string websiteId;

                if (count > 0)
                {
                    // Reuse existing WebsiteId (assumes all entries share same WebsiteId)
                    websiteId = result.Items.First()["WebsiteId"].S;
                }
                else
                {
                    // Generate new WebsiteId if none exists
                    websiteId = Guid.NewGuid().ToString();
                }

                int nextVersion = count + 1;

                return new OkObjectResult(new
                {
                    CompanyName = data.CompanyName,
                    WebsiteId = websiteId,
                    ExistingCount = count,
                    NextVersion = nextVersion
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get website version + id.");
                return new StatusCodeResult(500);
            }
        }
        [Function("GetCTAText")]
        public async Task<IActionResult> GetCTAText(
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

            var data = System.Text.Json.JsonSerializer.Deserialize<VideoIdRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var queryRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT \"Text\" FROM CTAText WHERE \"VideoId\" = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = data.VideoId }
            }
                };

                var result = await client.ExecuteStatementAsync(queryRequest);

                if (result.Items.Any())
                {
                    var text = result.Items.First()["Text"].S;

                    return new OkObjectResult(new
                    {
                        Exists = true,
                        Text = text
                    });
                }
                else
                {
                    return new OkObjectResult(new
                    {
                        Exists = false,
                        Text = (string)null
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetching CTA text failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("SaveAudioSample")]
        public async Task<IActionResult> SaveAudioSample([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
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
            var audioData = System.Text.Json.JsonSerializer.Deserialize<AudioSampleRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate unique AudioSample Id
                string audioSampleId;
                while (true)
                {
                    audioSampleId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM AudioSample WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = audioSampleId }
                }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);
                    if (!existingIdResult.Items.Any())
                        break;
                }

                // Insert AudioSample record
                var insertStatement =
                    "INSERT INTO AudioSample VALUE {" +
                    "'Id': ?, " +
                    "'VideoId': ?, " +
                    "'Url': ?, " +
                    "'Text': ?, " +
                    "'VoiceOverActor': ?, " +
                    "'TimeStamp': ?" +
                    "}";

                var parameters = new List<AttributeValue>
        {
            new AttributeValue { S = audioSampleId },
            new AttributeValue { S = audioData.VideoId },
            new AttributeValue { S = audioData.Url },
            new AttributeValue { S = audioData.Text },
            new AttributeValue { S = audioData.VoiceOverActor },
            new AttributeValue { S = audioData.TimeStamp }
        };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = insertStatement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new
                {
                    Message = "Audio sample saved successfully",
                    AudioSampleId = audioSampleId,
                    Url = audioData.Url
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving audio sample failed.");
                return new StatusCodeResult(500);
            }
        }

        public class AudioSampleRequest
        {
            public string VideoId { get; set; }
            public string Url { get; set; }
            public string Text { get; set; }
            public string VoiceOverActor { get; set; }
            public string TimeStamp { get; set; }
        }

        [Function("SaveImageIdea")]
        public async Task<IActionResult> SaveImageIdea([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var imageIdeaData = System.Text.Json.JsonSerializer.Deserialize<ImageIdeaRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string imageIdeaId;
                ExecuteStatementResponse existingIdResult;

                do
                {
                    imageIdeaId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM ImageIdeas WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = imageIdeaId }
                        }
                    };

                    existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                } while (existingIdResult.Items.Any());

                var statement = "INSERT INTO ImageIdeas VALUE {'Id': ?, 'DeliverableType': ?, 'SubCategory': ?, 'ImagePrompt': ?, 'ImageUrl': ?, 'PromptId': ?, 'TimeStamp': ?, 'CountId':?}";
                var parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = imageIdeaId },
                    new AttributeValue { S = imageIdeaData.DeliverableType ?? string.Empty },
                    new AttributeValue { S = imageIdeaData.SubCategory ?? string.Empty },
                    new AttributeValue { S = imageIdeaData.ImagePrompt ?? string.Empty },
                    new AttributeValue { S = imageIdeaData.ImageUrl ?? string.Empty },
                    new AttributeValue { S = imageIdeaData.PromptId ?? string.Empty },
                    new AttributeValue { S = imageIdeaData.TimeStamp ?? string.Empty },
                    new AttributeValue { S = imageIdeaData.CountId ?? string.Empty }
                };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new { Message = "ImageIdea saved successfully", Id = imageIdeaId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving image idea failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("SaveImageToFolder")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveImageToFolder(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("SaveImageToFolder");

            var referer = req.Headers.GetValues("Origin").FirstOrDefault();
            if (referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                return unauthorized;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveImageRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // ✅ Validate input
            if (string.IsNullOrWhiteSpace(data?.ImageUrl) ||
                string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("ImageUrl, CompanyName, and Version are required");
                return badResponse;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                              || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                // =========================
                // 🌐 DOWNLOAD SOURCE FILE
                // =========================
                using var httpClient = new HttpClient();
                var httpResponse = await httpClient.GetAsync(data.ImageUrl);

                httpResponse.EnsureSuccessStatusCode();

                var imageBytes = await httpResponse.Content.ReadAsByteArrayAsync();

                var contentType =
                    httpResponse.Content.Headers.ContentType?.MediaType
                    ?? GetContentTypeFromUrl(data.ImageUrl);

                string extension = GetExtension(contentType);

                string filePath;
                string webPath;

                // =====================================================
                // 🖥️ LOCAL STORAGE MODE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var rootPath = Path.Combine(wwwrootPath, "SavedImages", safeCompanyVersion);

                    if (!Directory.Exists(rootPath))
                        Directory.CreateDirectory(rootPath);

                    // ✅ Count existing images (1–4)
                    int existingCount = Enumerable.Range(1, 4)
                        .Count(i => System.IO.File.Exists(Path.Combine(rootPath, $"image-{i}{extension}")));

                    if (existingCount >= 4)
                    {
                        var responseLimit = req.CreateResponse(HttpStatusCode.OK);
                        await responseLimit.WriteAsJsonAsync(new
                        {
                            Message = "Maximum of 4 images already exist. No new image created.",
                            Mode = "LOCAL"
                        });
                        return responseLimit;
                    }

                    // ✅ Find next available slot
                    int nextNumber = Enumerable.Range(1, 4)
                        .First(i => !System.IO.File.Exists(Path.Combine(rootPath, $"image-{i}{extension}")));

                    var fileName = $"image-{nextNumber}{extension}";
                    filePath = Path.Combine(rootPath, fileName);

                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webPath = $"{baseUrl}/SavedImages/{safeCompanyVersion}/{fileName}";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE MODE (REPLACES BLOB STORAGE)
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");

                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    // ✅ Count existing files (1–4)
                    int existingCount = 0;

                    for (int i = 1; i <= 4; i++)
                    {
                        var key = $"{safeCompanyVersion}/image-{i}{extension}";

                        try
                        {
                            var metadata = await s3Client.GetObjectMetadataAsync(bucketName, key);
                            if (metadata != null)
                                existingCount++;
                        }
                        catch (Amazon.S3.AmazonS3Exception ex)
                        {
                            if (ex.StatusCode != System.Net.HttpStatusCode.NotFound)
                                throw;
                        }
                    }

                    if (existingCount >= 4)
                    {
                        var responseLimit = req.CreateResponse(HttpStatusCode.OK);
                        await responseLimit.WriteAsJsonAsync(new
                        {
                            Message = "Maximum of 4 images already exist. No new image created.",
                            Mode = "S3"
                        });
                        return responseLimit;
                    }

                    // ✅ Find next available slot
                    string keyName = null;

                    for (int i = 1; i <= 4; i++)
                    {
                        var key = $"{safeCompanyVersion}/image-{i}{extension}";

                        try
                        {
                            await s3Client.GetObjectMetadataAsync(bucketName, key);
                        }
                        catch (Amazon.S3.AmazonS3Exception ex)
                        {
                            if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                keyName = key;
                                break;
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }

                    // ✅ Upload to S3
                    using var stream = new MemoryStream(imageBytes);

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = contentType
                    };

                    // fallback: set via metadata headers
                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    // Optional: make public (only if your bucket isn't already public)
                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webPath = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    Message = useLocal ? "Image saved locally" : "Image saved to Blob Storage",
                    FilePath = filePath,
                    WebUrl = webPath,
                    ContentType = contentType,
                    Mode = useLocal ? "LOCAL" : "BLOB"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed saving image");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Error saving image");
                return errorResponse;
            }
        }

        public class SaveInviteClientS3Request
        {
            public string LocalFilePath { get; set; }

            public string CompanyName { get; set; }

            public string Version { get; set; }
        }

        private static string GetContentTypeFromExtension(string extension)
        {
            return extension.ToLower() switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".html" => "text/html",
                ".csv" => "text/csv",
                _ => "application/octet-stream"
            };
        }


        [Function("SaveInviteFile")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveInviteFile(
    [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
    FunctionContext context)
        {
            var logger = context.GetLogger("SaveInviteFile");

            try
            {
                // =========================
                // OPTIONAL ORIGIN CHECK (SAFEGUARD)
                // =========================
                var origin = req.Headers.TryGetValues("Origin", out var values)
                    ? values.FirstOrDefault()
                    : null;

                var hostUrl = Environment.GetEnvironmentVariable("HostUrl");

                if (!string.IsNullOrWhiteSpace(hostUrl) &&
                    !string.IsNullOrWhiteSpace(origin) &&
                    origin != hostUrl)
                {
                    var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorized.WriteStringAsync("Unauthorized origin.");
                    return unauthorized;
                }

                // =========================
                // ENVIRONMENT CHECK
                // =========================
                var useLocal =
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                    || req.Url.Host.Contains("localhost");

                // =========================
                // PARSE MULTIPART
                // =========================
                var (fields, files) = await ParseMultipartAsync(req);

                var clientName = fields.ContainsKey("ClientName") ? fields["ClientName"] : null;
                var clientEmail = fields.ContainsKey("ClientEmail") ? fields["ClientEmail"] : null;

                var file = files.FirstOrDefault();

                if (string.IsNullOrWhiteSpace(clientName) || file.Content == null || file.Content.Length == 0)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("ClientName and File are required");
                    return bad;
                }

                // =========================
                // SANITIZE FILENAME
                // =========================
                var safeClientName = string.Concat(clientName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-");

                var folderName = $"{safeClientName}-{Guid.NewGuid().ToString().Substring(0, 6)}";

                var extension = Path.GetExtension(file.FileName);
                var contentType = file.ContentType;

                string filePath;
                string webPath;

                var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                var region = Environment.GetEnvironmentVariable("AWS_REGION");

                var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                if (string.IsNullOrWhiteSpace(bucketName))
                    throw new Exception("S3_BUCKET_NAME is missing");

                if (string.IsNullOrWhiteSpace(region))
                    throw new Exception("AWS_REGION is missing");

                if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
                    throw new Exception("AWS credentials are missing");

                var config = new Amazon.S3.AmazonS3Config
                {
                    RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                };

                using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                var keyName = $"invite-uploads/{folderName}/file{extension}";

                using var stream = new MemoryStream(file.Content.ToArray());
                stream.Position = 0;

                var putRequest = new Amazon.S3.Model.PutObjectRequest
                {
                    BucketName = bucketName,
                    Key = keyName,
                    InputStream = stream,
                    ContentType = contentType
                };

                // =========================
                // UPLOAD (NO ACL - FIXED)
                // =========================
                await s3Client.PutObjectAsync(putRequest);

                filePath = keyName;
                webPath = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    Message = useLocal ? "File saved locally" : "File saved to S3",
                    FilePath = filePath,
                    WebUrl = webPath,
                    ContentType = contentType,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed saving invite file");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);

                var isDev =
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";

                if (isDev)
                {
                    await error.WriteAsJsonAsync(new
                    {
                        message = ex.Message,
                        stackTrace = ex.StackTrace
                    });
                }
                else
                {
                    await error.WriteAsJsonAsync(new
                    {
                        message = ex.Message
                    });
                }

                return error;
            }
        }

        private async Task<(Dictionary<string, string> fields, List<(string FileName, string ContentType, byte[] Content)> files)>
    ParseMultipartAsync(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
        {
            var contentType = req.Headers.GetValues("Content-Type").FirstOrDefault();

            var boundary = Microsoft.Net.Http.Headers.HeaderUtilities
                .RemoveQuotes(
                    Microsoft.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType).Boundary
                ).Value;

            var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, req.Body);

            var fields = new Dictionary<string, string>();
            var files = new List<(string, string, byte[])>();

            MultipartSection section;

            while ((section = await reader.ReadNextSectionAsync()) != null)
            {
                var contentDisposition = section.ContentDisposition;

                if (contentDisposition.Contains("filename="))
                {
                    var fileName = contentDisposition.Split("filename=")[1].Trim('"');
                    var contentTypeHeader = section.ContentType;

                    using var ms = new MemoryStream();
                    await section.Body.CopyToAsync(ms);

                    files.Add((fileName, contentTypeHeader, ms.ToArray()));
                }
                else if (contentDisposition.Contains("name="))
                {
                    var name = contentDisposition.Split("name=")[1].Trim('"');

                    using var readerStream = new StreamReader(section.Body);
                    var value = await readerStream.ReadToEndAsync();

                    fields[name] = value;
                }
            }

            return (fields, files);
        }

        //
        // =========================
        // 🔧 HELPERS
        // =========================
        private static string GetContentTypeFromUrl(string url)
        {
            var ext = Path.GetExtension(url.Split('?')[0]).ToLower();

            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".html" or ".htm" => "text/html",
                _ => "application/octet-stream"
            };
        }

        private static string GetExtension(string contentType)
        {
            return contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "text/html" => ".html",
                _ => ".bin"
            };
        }

        public class SaveImageRequest
        {
            public string ImageUrl { get; set; }
            public string CompanyName { get; set; }
            public string Version { get; set; }   // NEW
            public string Timestamp { get; set; } // optional
        }

        [Function("SaveHeroBackground")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveHeroBackground(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("SaveHeroBackground");

            var referer = req.Headers.GetValues("Origin").FirstOrDefault();
            if (referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                return unauthorized;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveImageRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.ImageUrl) ||
                string.IsNullOrWhiteSpace(data?.CompanyName))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("ImageUrl and CompanyName are required");
                return badResponse;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                              || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUT
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var folderName = safeCompanyName;

                // =========================
                // 🌐 DOWNLOAD IMAGE
                // =========================
                using var httpClient = new HttpClient();
                var httpResponse = await httpClient.GetAsync(data.ImageUrl);
                httpResponse.EnsureSuccessStatusCode();

                var imageBytes = await httpResponse.Content.ReadAsByteArrayAsync();

                var contentType =
                    httpResponse.Content.Headers.ContentType?.MediaType
                    ?? GetContentTypeFromUrl(data.ImageUrl);

                string extension = GetExtension(contentType);

                string filePath;
                string webPath;

                // =====================================================
                // 🖥️ LOCAL STORAGE MODE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var rootPath = Path.Combine(wwwrootPath, "SavedImages", folderName);

                    if (!Directory.Exists(rootPath))
                        Directory.CreateDirectory(rootPath);

                    var fileName = $"hero-background{extension}";
                    filePath = Path.Combine(rootPath, fileName);

                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webPath = $"{baseUrl}/SavedImages/{folderName}/{fileName}";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE MODE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"{folderName}/hero-background{extension}";

                    using var stream = new MemoryStream(imageBytes);

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = contentType
                    };

                    // fallback: set via metadata headers
                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    // Optional: make public
                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webPath = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    Message = useLocal ? "Hero background saved locally" : "Hero background saved to S3",
                    FilePath = filePath,
                    WebUrl = webPath,
                    ContentType = contentType,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed saving hero background");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Error saving hero background");
                return errorResponse;
            }
        }

        public class SaveBase64Request
        {
            public string Base64Image { get; set; }
            public string CompanyName { get; set; }
            public string Version { get; set; }   // NEW
        }

        [Function("SaveHeroBackgroundFromBase64")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveHeroBackgroundFromBase64(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("SaveHeroBackgroundFromBase64");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveBase64Request>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.Base64Image) ||
                string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Base64Image, CompanyName, and Version are required");
                return bad;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                  || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                // =========================
                // 🖼️ DECODE BASE64
                // =========================
                var base64 = data.Base64Image;

                string contentType = "image/png";

                if (base64.Contains(","))
                {
                    var parts = base64.Split(',');
                    var header = parts[0];
                    base64 = parts[1];

                    if (header.Contains("image/jpeg")) contentType = "image/jpeg";
                    else if (header.Contains("image/jpg")) contentType = "image/jpeg";
                    else if (header.Contains("image/webp")) contentType = "image/webp";
                    else if (header.Contains("image/png")) contentType = "image/png";
                }

                var imageBytes = Convert.FromBase64String(base64);

                string extension = GetExtension(contentType);

                string filePath;
                string webUrl;

                // =====================================================
                // 🖥️ LOCAL STORAGE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SavedImages", safeCompanyVersion);

                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    var fileName = $"hero-background{extension}";
                    filePath = Path.Combine(folderPath, fileName);

                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webUrl = $"{baseUrl}/SavedImages/{safeCompanyVersion}/{fileName}";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"{safeCompanyVersion}/hero-background{extension}";

                    using var stream = new MemoryStream(imageBytes);

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = contentType
                    };

                    // fallback: set via metadata headers
                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    // Optional: public access
                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    Message = useLocal ? "Hero saved locally" : "Hero saved to S3",
                    FilePath = filePath,
                    WebUrl = webUrl,
                    ContentType = contentType,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error saving base64 image");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to save image");
                return error;
            }
        }

        public class CopyImagesRequest
        {
            public string CompanyName { get; set; }
            public string OldVersion { get; set; }
            public string NewVersion { get; set; }
        }
        public sealed class GetDesignModeJobStatusRequest
        {
            public string ImageId { get; set; } = string.Empty;
        }
        private static string? GetStringValue(
    Dictionary<string, AttributeValue> item,
    string attributeName)
        {
            if (!item.TryGetValue(
                    attributeName,
                    out AttributeValue? value))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(value.S))
            {
                return value.S;
            }

            if (!string.IsNullOrWhiteSpace(value.N))
            {
                return value.N;
            }

            if (value.BOOL == true)
            {
                return bool.TrueString;
            }

            return null;
        }

        private static int? GetIntValue(
            Dictionary<string, AttributeValue> item,
            string attributeName)
        {
            if (!item.TryGetValue(
                    attributeName,
                    out AttributeValue? value) ||
                string.IsNullOrWhiteSpace(value.N))
            {
                return null;
            }

            return int.TryParse(
                value.N,
                out int parsedValue)
                ? parsedValue
                : null;
        }
        [Function("SaveSocialHeroBackgroundFromBase64")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveSocialHeroBackgroundFromBase64(
    [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
    FunctionContext context)
        {
            var logger = context.GetLogger("SaveSocialHeroBackgroundFromBase64");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var data = System.Text.Json.JsonSerializer.Deserialize<SaveSocialBase64Request>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.Base64Image) ||
                string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Platform) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Base64Image, CompanyName, Platform, and Version are required");
                return bad;
            }

            try
            {
                var useLocal =
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                    || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safePlatform = string.Concat(data.Platform
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .ToLower();

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyPlatformVersion = $"{safeCompanyName}-{safePlatform}-v{safeVersion}";

                // =========================
                // 🖼️ BASE64 PARSING
                // =========================
                var base64 = data.Base64Image;
                string contentType = "image/png";

                if (base64.Contains(","))
                {
                    var parts = base64.Split(',');
                    var header = parts[0];
                    base64 = parts[1];

                    if (header.Contains("image/jpeg")) contentType = "image/jpeg";
                    else if (header.Contains("image/jpg")) contentType = "image/jpeg";
                    else if (header.Contains("image/webp")) contentType = "image/webp";
                    else if (header.Contains("image/png")) contentType = "image/png";
                }

                var imageBytes = Convert.FromBase64String(base64);
                var extension = GetExtension(contentType);

                string filePath;
                string webUrl;

                // =====================================================
                // 🖥️ LOCAL STORAGE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SocialImages", safeCompanyPlatformVersion);

                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    var fileName = $"social-hero-background{extension}";
                    filePath = Path.Combine(folderPath, fileName);

                    await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);

                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webUrl = $"{baseUrl}/SocialImages/{safeCompanyPlatformVersion}/{fileName}";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"social/{safeCompanyPlatformVersion}/hero-background{extension}";

                    using var stream = new MemoryStream(imageBytes);

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = contentType
                    };

                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    Message = useLocal ? "Social hero saved locally" : "Social hero saved to S3",
                    FilePath = filePath,
                    WebUrl = webUrl,
                    ContentType = contentType,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error saving social base64 image");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to save social image");
                return error;
            }
        }

        public class SaveSocialBase64Request
        {
            public string Base64Image { get; set; }
            public string CompanyName { get; set; }
            public string Platform { get; set; }
            public string Version { get; set; }
        }

        [Function("CopyImagesToNewVersion")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> CopyImagesToNewVersion(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("CopyImagesToNewVersion");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<CopyImagesRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.OldVersion) ||
                string.IsNullOrWhiteSpace(data?.NewVersion))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("CompanyName, OldVersion, and NewVersion are required");
                return bad;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                  || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeOldVersion = string.Concat(data.OldVersion
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeNewVersion = string.Concat(data.NewVersion
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var oldFolderName = $"{safeCompanyName}-v{safeOldVersion}";
                var newFolderName = $"{safeCompanyName}-v{safeNewVersion}";

                var copiedFiles = new List<string>();
                var skippedFiles = new List<string>();
                var missingSourceFiles = new List<string>();

                var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";

                // =====================================================
                // 🖥️ LOCAL FILE SYSTEM COPY
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

                    var sourceFolder = Path.Combine(wwwrootPath, "SavedImages", oldFolderName);
                    var destinationFolder = Path.Combine(wwwrootPath, "SavedImages", newFolderName);

                    if (!Directory.Exists(sourceFolder))
                    {
                        var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                        await notFound.WriteStringAsync("Source folder does not exist");
                        return notFound;
                    }

                    if (!Directory.Exists(destinationFolder))
                        Directory.CreateDirectory(destinationFolder);

                    var files = Directory.GetFiles(sourceFolder);

                    foreach (var filePath in files)
                    {
                        var fileName = Path.GetFileName(filePath);
                        var destPath = Path.Combine(destinationFolder, fileName);

                        if (!System.IO.File.Exists(filePath))
                        {
                            missingSourceFiles.Add(fileName);
                            continue;
                        }

                        if (System.IO.File.Exists(destPath))
                        {
                            skippedFiles.Add(fileName);
                            continue;
                        }

                        System.IO.File.Copy(filePath, destPath);
                        copiedFiles.Add(fileName);
                    }

                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(new
                    {
                        Message = "Local image copy completed",
                        CopiedFiles = copiedFiles,
                        SkippedFiles = skippedFiles,
                        MissingSourceFiles = missingSourceFiles,
                        SourceFolder = oldFolderName,
                        DestinationFolder = newFolderName,
                        Url = $"{baseUrl}/SavedImages/{newFolderName}/"
                    });

                    return response;
                }

                // =====================================================
                // ☁️ AWS S3 COPY (PREFIX COPY)
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var sourcePrefix = $"{oldFolderName}/";
                    var destPrefix = $"{newFolderName}/";

                    string continuationToken = null;

                    do
                    {
                        var listRequest = new Amazon.S3.Model.ListObjectsV2Request
                        {
                            BucketName = bucketName,
                            Prefix = sourcePrefix,
                            ContinuationToken = continuationToken
                        };

                        var listResponse = await s3Client.ListObjectsV2Async(listRequest);

                        foreach (var obj in listResponse.S3Objects)
                        {
                            var fileName = obj.Key.Substring(sourcePrefix.Length);
                            var destKey = $"{destPrefix}{fileName}";

                            // Check if destination exists
                            try
                            {
                                await s3Client.GetObjectMetadataAsync(bucketName, destKey);
                                skippedFiles.Add(fileName);
                                continue;
                            }
                            catch (Amazon.S3.AmazonS3Exception ex)
                            {
                                if (ex.StatusCode != HttpStatusCode.NotFound)
                                    throw;
                            }

                            // Copy object (server-side)
                            var copyRequest = new Amazon.S3.Model.CopyObjectRequest
                            {
                                SourceBucket = bucketName,
                                SourceKey = obj.Key,
                                DestinationBucket = bucketName,
                                DestinationKey = destKey,
                                MetadataDirective = Amazon.S3.S3MetadataDirective.COPY
                            };

                            await s3Client.CopyObjectAsync(copyRequest);

                            copiedFiles.Add(fileName);
                        }

                        continuationToken = listResponse.NextContinuationToken;

                    } while (!string.IsNullOrEmpty(continuationToken));

                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(new
                    {
                        Message = "S3 image copy completed",
                        CopiedFiles = copiedFiles,
                        SkippedFiles = skippedFiles,
                        MissingSourceFiles = missingSourceFiles,
                        SourceFolder = oldFolderName,
                        DestinationFolder = newFolderName,
                        Url = $"https://{bucketName}.s3.{region}.amazonaws.com/{newFolderName}/"
                    });

                    return response;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error copying images");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to copy images");
                return error;
            }
        }

        [Function("SaveHtmlToCompanyFolder")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveHtmlToCompanyFolder(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("SaveHtmlToCompanyFolder");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveHtmlRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.HtmlContent) ||
                string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("HtmlContent, CompanyName, and Version are required");
                return bad;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                  || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                string fileName = "index.html";
                string filePath;
                string webUrl;

                // =====================================================
                // 🖥️ LOCAL STORAGE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SavedImages", safeCompanyVersion);

                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, fileName);

                    await System.IO.File.WriteAllTextAsync(filePath, data.HtmlContent);

                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webUrl = $"{baseUrl}/SavedImages/{safeCompanyVersion}/{fileName}";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"{safeCompanyVersion}/index.html";

                    var contentType = "text/html"; // ✅ FIX HERE

                    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(data.HtmlContent));

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = contentType
                    };

                    // fallback: set via metadata headers
                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    WebUrl = webUrl,
                    FilePath = filePath,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error saving HTML file");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to save HTML");
                return error;
            }
        }

        public class SaveHtmlRequest
        {
            public string HtmlContent { get; set; }
            public string CompanyName { get; set; }
            public string Version { get; set; }  // NEW
        }

        [Function("SaveSocialContent")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveSocialContent(
    [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
    FunctionContext context)
        {
            var logger = context.GetLogger("SaveSocialContent");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var data = System.Text.Json.JsonSerializer.Deserialize<SaveSocialRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.HtmlContent) ||
                string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version) ||
                string.IsNullOrWhiteSpace(data?.Platform))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("HtmlContent, CompanyName, Platform, and Version are required");
                return bad;
            }

            try
            {
                var useLocal =
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                    || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safePlatform = string.Concat(data.Platform
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .ToLower();

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-{safePlatform}-v{safeVersion}";

                string fileName = "index.html";
                string filePath;
                string webUrl;

                // =====================================================
                // 🖥️ LOCAL STORAGE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SocialContent", safeCompanyVersion);

                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, fileName);

                    await System.IO.File.WriteAllTextAsync(filePath, data.HtmlContent);

                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webUrl = $"{baseUrl}/SocialContent/{safeCompanyVersion}/{fileName}";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"social/{safeCompanyVersion}/index.html";

                    using var stream = new MemoryStream(
                        System.Text.Encoding.UTF8.GetBytes(data.HtmlContent)
                    );

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = "text/html"
                    };

                    putRequest.Headers["Content-Disposition"] = "inline";

                    await s3Client.PutObjectAsync(putRequest);

                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    WebUrl = webUrl,
                    FilePath = filePath,
                    Platform = data.Platform,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error saving social content");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to save social content");
                return error;
            }
        }

        public class SaveSocialRequest
        {
            public string HtmlContent { get; set; }
            public string CompanyName { get; set; }
            public string Platform { get; set; }
            public string Version { get; set; }
        }

        [Function("DownloadWebsite")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> DownloadWebsite(
            [HttpTrigger(AuthorizationLevel.Function, "get")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("DownloadWebsite");

            try
            {
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var companyName = query["safecompanyname"];
                var version = query["version"];

                if (string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(version))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("safecompanyname and version are required");
                    return bad;
                }

                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                    || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(companyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                var zipStream = new MemoryStream();

                // =====================================================
                // 🖥️ LOCAL MODE ZIP (UNCHANGED)
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SavedImages", safeCompanyVersion);

                    if (!Directory.Exists(folderPath))
                    {
                        var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                        await notFound.WriteStringAsync("Website folder not found");
                        return notFound;
                    }

                    using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var filePath in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
                        {
                            var relativePath = Path.GetRelativePath(folderPath, filePath);

                            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                            using var entryStream = entry.Open();
                            using var fileStream = System.IO.File.OpenRead(filePath);

                            await fileStream.CopyToAsync(entryStream);
                        }
                    }
                }

                // =====================================================
                // ☁️ S3 MODE ZIP (REPLACED)
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var prefix = $"{safeCompanyVersion}/";

                    var listRequest = new Amazon.S3.Model.ListObjectsV2Request
                    {
                        BucketName = bucketName,
                        Prefix = prefix
                    };

                    var blobItems = new List<(string Name, Stream Content)>();

                    var response = await s3Client.ListObjectsV2Async(listRequest);

                    foreach (var obj in response.S3Objects)
                    {
                        var getObject = await s3Client.GetObjectAsync(bucketName, obj.Key);

                        var relativeName = obj.Key.Substring(prefix.Length);

                        blobItems.Add((relativeName, getObject.ResponseStream));
                    }

                    using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var file in blobItems)
                        {
                            var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);

                            using var entryStream = entry.Open();
                            await file.Content.CopyToAsync(entryStream);
                        }
                    }
                }

                // =========================
                // RESPONSE
                // =========================
                zipStream.Position = 0;

                var httpResponse = req.CreateResponse(HttpStatusCode.OK);
                httpResponse.Headers.Add("Content-Type", "application/zip");
                httpResponse.Headers.Add("Content-Disposition", $"attachment; filename={safeCompanyVersion}.zip");

                await zipStream.CopyToAsync(httpResponse.Body);

                return httpResponse;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error downloading website as ZIP");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to download website");
                return error;
            }
        }

        [Function("SaveWebsiteScreenshot")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveWebsiteScreenshot(
            [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
            FunctionContext context)
        {
            var logger = context.GetLogger("SaveWebsiteScreenshot");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveScreenshotRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("CompanyName and Version are required");
                return bad;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                               || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                // =========================
                // 🌍 TARGET URL
                // =========================
                var baseUrl = useLocal
                    ? "http://localhost/"
                    : "https://designmode-storage.s3.us-east-1.amazonaws.com/";

                var targetUrl = $"{baseUrl}{safeCompanyVersion}/";

                // =========================
                // 🧠 SELENIUM
                // =========================
                var options = new ChromeOptions();
                options.AddArgument("--headless=new");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-gpu");
                options.AddArgument("--disable-dev-shm-usage");

                using var driver = new ChromeDriver(options);

                driver.Navigate().GoToUrl(targetUrl);
                await Task.Delay(5000);

                driver.Manage().Window.Size = new System.Drawing.Size(1280, 1024);

                ((IJavaScriptExecutor)driver).ExecuteScript(
                    "document.documentElement.style.overflow = 'hidden';"
                );

                Screenshot screenshot = ((ITakesScreenshot)driver).GetScreenshot();

                string filePath;
                string webUrl;

                // =====================================================
                // 🖥️ LOCAL STORAGE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SavedImages", safeCompanyVersion);

                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, "website-shot.png");

                    screenshot.SaveAsFile(filePath, ScreenshotImageFormat.Png);

                    var baseLocalUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webUrl = $"{baseLocalUrl}/SavedImages/{safeCompanyVersion}/website-shot.png";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"{safeCompanyVersion}/website-shot.png";

                    using var stream = new MemoryStream(screenshot.AsByteArray);

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = "image/png"
                    };

                    await s3Client.PutObjectAsync(putRequest);

                    // Optional: make public
                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    ScreenshotUrl = webUrl,
                    FilePath = filePath,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error taking screenshot");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to capture screenshot");
                return error;
            }
        }

        [Function("SaveSocialScreenshot")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SaveSocialScreenshot(
    [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
    FunctionContext context)
        {
            var logger = context.GetLogger("SaveSocialScreenshot");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveScreenshotRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("CompanyName and Version are required");
                return bad;
            }

            try
            {
                var useLocal = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                               || req.Url.Host.Contains("localhost");

                // =========================
                // 🔐 SANITIZE
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                // =========================
                // 🌍 TARGET URL
                // =========================
                var baseUrl = useLocal
                    ? "http://localhost/"
                    : "https://designmode-storage.s3.us-east-1.amazonaws.com/";

                var targetUrl = $"{baseUrl}{safeCompanyVersion}/";

                // =========================
                // 🧠 SELENIUM
                // =========================
                var options = new ChromeOptions();
                options.AddArgument("--headless=new");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-gpu");
                options.AddArgument("--disable-dev-shm-usage");

                using var driver = new ChromeDriver(options);

                driver.Navigate().GoToUrl(targetUrl);
                await Task.Delay(5000);

                // 🔥 Social-friendly (square)
                driver.Manage().Window.Size = new System.Drawing.Size(1024, 1024);

                ((IJavaScriptExecutor)driver).ExecuteScript(
                    "document.documentElement.style.overflow = 'hidden';"
                );

                Screenshot screenshot = ((ITakesScreenshot)driver).GetScreenshot();

                string filePath;
                string webUrl;

                // =====================================================
                // 🖥️ LOCAL STORAGE
                // =====================================================
                if (useLocal)
                {
                    var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var folderPath = Path.Combine(wwwrootPath, "SavedImages", safeCompanyVersion);

                    if (!Directory.Exists(folderPath))
                        Directory.CreateDirectory(folderPath);

                    filePath = Path.Combine(folderPath, "social-shot.png");

                    screenshot.SaveAsFile(filePath, ScreenshotImageFormat.Png);

                    var baseLocalUrl = $"{req.Url.Scheme}://{req.Url.Host}:{req.Url.Port}";
                    webUrl = $"{baseLocalUrl}/SavedImages/{safeCompanyVersion}/social-shot.png";
                }

                // =====================================================
                // ☁️ AWS S3 STORAGE
                // =====================================================
                else
                {
                    var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                    var region = Environment.GetEnvironmentVariable("AWS_REGION");
                    var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                    var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                    var config = new Amazon.S3.AmazonS3Config
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                    };

                    using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                    var keyName = $"{safeCompanyVersion}/social-shot.png";

                    using var stream = new MemoryStream(screenshot.AsByteArray);

                    var putRequest = new Amazon.S3.Model.PutObjectRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        InputStream = stream,
                        ContentType = "image/png"
                    };

                    await s3Client.PutObjectAsync(putRequest);

                    await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                    {
                        BucketName = bucketName,
                        Key = keyName,
                        CannedACL = Amazon.S3.S3CannedACL.PublicRead
                    });

                    filePath = keyName;
                    webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";
                }

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    ScreenshotUrl = webUrl,
                    FilePath = filePath,
                    Mode = useLocal ? "LOCAL" : "S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error taking social screenshot");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to capture screenshot");
                return error;
            }
        }

        [Function("GenerateWebsiteScreenshot")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> GenerateWebsiteScreenshot(
    [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
    FunctionContext context)
        {
            var logger = context.GetLogger("GenerateWebsiteScreenshot");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveScreenshotRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("CompanyName and Version are required");
                return bad;
            }

            try
            {
                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                // =========================
                // 🌍 TARGET WEBSITE URL (S3 HOSTED SITE)
                // =========================
                var targetUrl =
                    $"https://designmode-storage.s3.us-east-1.amazonaws.com/{safeCompanyVersion}/index.html";

                // =========================
                // 📸 API FLASH CALL
                // =========================
                var apiKey = Environment.GetEnvironmentVariable("API_FLASH");
                //Test it and fix it
                var apiUrl =
                    $"https://api.apiflash.com/v1/urltoimage" +
                    $"?access_key={apiKey}" +
                    $"&wait_until=page_loaded" +
                    $"&width=1024" +
                    $"&height=768" +
                    $"&url={Uri.EscapeDataString(targetUrl)}";

                using var httpClient = new HttpClient();

                var imageBytes = await httpClient.GetByteArrayAsync(apiUrl);

                // =========================
                // ☁️ AWS S3 UPLOAD (PROD ONLY)
                // =========================
                var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                var region = Environment.GetEnvironmentVariable("AWS_REGION");
                var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                var config = new Amazon.S3.AmazonS3Config
                {
                    RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                };

                using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                var keyName = $"{safeCompanyVersion}/website-shot.png";

                using var stream = new MemoryStream(imageBytes);

                var putRequest = new Amazon.S3.Model.PutObjectRequest
                {
                    BucketName = bucketName,
                    Key = keyName,
                    InputStream = stream,
                    ContentType = "image/png"
                };

                await s3Client.PutObjectAsync(putRequest);

                // Make public
                await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                {
                    BucketName = bucketName,
                    Key = keyName,
                    CannedACL = Amazon.S3.S3CannedACL.PublicRead
                });

                var webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    ScreenshotUrl = webUrl,
                    SourceUrl = targetUrl,
                    Mode = "API_FLASH_S3"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error generating screenshot via API Flash");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to generate screenshot");
                return error;
            }
        }

        [Function("GenerateSocialScreenshot")]
        public async Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> GenerateSocialScreenshot(
    [HttpTrigger(AuthorizationLevel.Function, "post")] Microsoft.Azure.Functions.Worker.Http.HttpRequestData req,
    FunctionContext context)
        {
            var logger = context.GetLogger("GenerateSocialScreenshot");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = System.Text.Json.JsonSerializer.Deserialize<SaveScreenshotRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            // =========================
            // ✅ VALIDATION
            // =========================
            if (string.IsNullOrWhiteSpace(data?.CompanyName) ||
                string.IsNullOrWhiteSpace(data?.Version))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("CompanyName and Version are required");
                return bad;
            }

            try
            {
                // =========================
                // 🔐 SANITIZE INPUTS
                // =========================
                var safeCompanyName = string.Concat(data.CompanyName
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-")
                    .Replace("&", "");

                var safeVersion = string.Concat(data.Version
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "")
                    .ToLower();

                var safeCompanyVersion = $"{safeCompanyName}-v{safeVersion}";

                // =========================
                // 🌍 TARGET WEBSITE URL
                // =========================
                var targetUrl =
                    $"https://designmode-storage.s3.us-east-1.amazonaws.com/{safeCompanyVersion}/index.html";

                // =========================
                // 📸 API FLASH CALL (SOCIAL OPTIMIZED)
                // =========================
                var apiKey = Environment.GetEnvironmentVariable("API_FLASH");

                var apiUrl =
                    $"https://api.apiflash.com/v1/urltoimage" +
                    $"?access_key={apiKey}" +
                    $"&wait_until=page_loaded" +
                    $"&width=1024" +
                    $"&height=1024" + // 🔥 square for social
                    $"&url={Uri.EscapeDataString(targetUrl)}";

                using var httpClient = new HttpClient();

                var imageBytes = await httpClient.GetByteArrayAsync(apiUrl);

                // =========================
                // ☁️ AWS S3 UPLOAD
                // =========================
                var bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
                var region = Environment.GetEnvironmentVariable("AWS_REGION");
                var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
                var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

                var config = new Amazon.S3.AmazonS3Config
                {
                    RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
                };

                using var s3Client = new Amazon.S3.AmazonS3Client(accessKey, secretKey, config);

                var keyName = $"{safeCompanyVersion}/social-shot.png";

                using var stream = new MemoryStream(imageBytes);

                var putRequest = new Amazon.S3.Model.PutObjectRequest
                {
                    BucketName = bucketName,
                    Key = keyName,
                    InputStream = stream,
                    ContentType = "image/png"
                };

                await s3Client.PutObjectAsync(putRequest);

                // Make public
                await s3Client.PutACLAsync(new Amazon.S3.Model.PutACLRequest
                {
                    BucketName = bucketName,
                    Key = keyName,
                    CannedACL = Amazon.S3.S3CannedACL.PublicRead
                });

                var webUrl = $"https://{bucketName}.s3.{region}.amazonaws.com/{keyName}";

                // =========================
                // RESPONSE
                // =========================
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    ScreenshotUrl = webUrl,
                    SourceUrl = targetUrl,
                    Mode = "API_FLASH_S3_SOCIAL"
                });

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error generating social screenshot via API Flash");

                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteStringAsync("Failed to generate screenshot");
                return error;
            }
        }

        public class SaveScreenshotRequest
        {
            public string CompanyName { get; set; }
            public string Version { get; set; } // NEW
        }

        [Function("SaveVideoImage")]
        public async Task<IActionResult> SaveVideoImage([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var videoImageData = System.Text.Json.JsonSerializer.Deserialize<VideoImageRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Generate unique Id
                string videoImageId;
                ExecuteStatementResponse existingIdResult;

                do
                {
                    videoImageId = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM VideoImages WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = videoImageId }
                        }
                    };

                    existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);

                } while (existingIdResult.Items.Any());

                string videoId = videoImageData.VideoId;

                if (string.IsNullOrWhiteSpace(videoId))
                {
                    return new BadRequestObjectResult("VideoId is required in the request body.");
                }

                var statement = "INSERT INTO VideoImages VALUE {'Id': ?, 'ImageId': ?, 'VideoId': ?, 'TimeStamp': ?, 'Scene': ?, 'PromptId': ?, 'Script': ?}";
                var parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = videoImageId },
                        new AttributeValue { S = videoImageData.ImageId ?? string.Empty },
                        new AttributeValue { S = videoId },
                        new AttributeValue { S = videoImageData.TimeStamp ?? string.Empty },
                        new AttributeValue { S = videoImageData.Scene ?? string.Empty },
                        new AttributeValue { S = videoImageData.PromptId ?? string.Empty },
                        new AttributeValue { S = videoImageData.Script ?? string.Empty }
                    };

                await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                });

                return new OkObjectResult(new
                {
                    Message = "VideoImage saved successfully",
                    Id = videoImageId,
                    VideoId = videoId  // <-- return the correct one
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving video image failed.");
                return new StatusCodeResult(500);
            }
        }

        public class VideoCheckRequest
        {
            public string VideoId { get; set; }
        }


        [Function("CheckVideoExists")]
        public async Task<IActionResult> CheckVideoExists([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            // Read request body
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var requestData = System.Text.Json.JsonSerializer.Deserialize<VideoCheckRequest>(
                requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (requestData == null || string.IsNullOrWhiteSpace(requestData.VideoId))
            {
                return new BadRequestObjectResult("VideoId is required.");
            }

            try
            {
                // Determine DynamoDB client (local or AWS)
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query Videos table
                var statement = "SELECT VideoId FROM Videos WHERE VideoId = ?";
                var parameters = new List<AttributeValue>
        {
            new AttributeValue { S = requestData.VideoId }
        };

                var response = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                });

                bool exists = response.Items.Any();

                return new OkObjectResult(new
                {
                    Exists = exists,
                    VideoId = requestData.VideoId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckVideoExists failed.");
                return new StatusCodeResult(500);
            }
        }


        public class VideoImageRequest
        {
            public string ImageId { get; set; }
            public string TimeStamp { get; set; }
            public string Scene { get; set; }
            public string PromptId { get; set; }
            public string VideoId { get; set; }
            public string Script { get; set; }
        }


        [Function("GetImageIdeasByCompanyId")]
        public async Task<IActionResult> GetImageIdeasByCompanyId([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
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

            var claimedUserId = jwtUserResolver.ResolveUserId(req);
            var authenticatedUserId = string.IsNullOrWhiteSpace(claimedUserId)
                ? await ResolveLegacyJwtUserIdAsync(req)
                : await attributionStore.ResolveClaimedUserIdAsync(claimedUserId);
            if (string.IsNullOrWhiteSpace(authenticatedUserId)) return new UnauthorizedResult();

            string companyIdeaId = req.Query["companyIdeaId"];
            if (string.IsNullOrWhiteSpace(companyIdeaId))
            {
                return new BadRequestObjectResult("Missing companyIdeaId.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var result = await GenProjectImagesQuery.LoadAsync(client, authenticatedUserId, companyIdeaId);
                if (!result.HasOwnedPrompts)
                {
                    // Preserve fresh-generation empty results, but reject an existing unowned CID.
                    var existing = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Prompt WHERE CompanyIdeaId = ?",
                        Parameters = [new AttributeValue { S = companyIdeaId }]
                    };
                    ExecuteStatementResponse page;
                    do
                    {
                        page = await client.ExecuteStatementAsync(existing);
                        if (page.Items.Any()) return new NotFoundResult();
                        existing.NextToken = page.NextToken;
                    } while (!string.IsNullOrEmpty(existing.NextToken));
                    return new OkObjectResult(Array.Empty<ImageIdeaModel>());
                }

                return new OkObjectResult(result.Images);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch image ideas.");
                return new StatusCodeResult(500);
            }
        }
        [Function("GetVideoImagesByVideoId")]
        public async Task<IActionResult> GetVideoImagesByVideoId(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
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

            string videoId = req.Query["videoId"];
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return new BadRequestObjectResult("Missing videoId.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var videoImages = new List<VideoImageModelGet>();

                // 1) First query: VideoImages
                var query = new ExecuteStatementRequest
                {
                    Statement = @"SELECT *
                          FROM ""VideoImages""
                          WHERE ""VideoId"" = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = videoId }
            }
                };

                ExecuteStatementResponse result;

                do
                {
                    result = await client.ExecuteStatementAsync(query);

                    var batch = result.Items.Select(item => new VideoImageModelGet
                    {
                        Id = item.GetValueOrDefault("Id")?.S ?? string.Empty,
                        VideoId = item.GetValueOrDefault("VideoId")?.S ?? string.Empty,
                        ImageId = item.GetValueOrDefault("ImageId")?.S ?? string.Empty,
                        TimeStamp = item.GetValueOrDefault("TimeStamp")?.S ?? string.Empty,
                        Scene = item.GetValueOrDefault("Scene")?.S ?? string.Empty,
                        Script = item.GetValueOrDefault("Script")?.S ?? string.Empty,
                        PromptId = item.GetValueOrDefault("PromptId")?.S ?? string.Empty,
                        ScriptPlain = item.GetValueOrDefault("ScriptPlain")?.S ?? string.Empty,
                        ScriptWithSpeech = item.GetValueOrDefault("ScriptWithSpeech")?.S ?? string.Empty,
                        Resolution = item.GetValueOrDefault("Resolution")?.S ?? string.Empty,
                        VoiceOverActor = item.GetValueOrDefault("VoiceOverActor")?.S ?? string.Empty,
                        BackgroundMusic = item.GetValueOrDefault("BackgroundMusic")?.S ?? string.Empty,
                        VoiceOverScript = item.GetValueOrDefault("VoiceOverScript")?.S ?? string.Empty
                    }).ToList();

                    videoImages.AddRange(batch);

                    query.NextToken = result.NextToken;

                } while (!string.IsNullOrEmpty(result.NextToken));

                // 2) Second query: fetch ImageUrl from ImageIdeas using ImageId
                foreach (var vi in videoImages)
                {
                    if (string.IsNullOrWhiteSpace(vi.ImageId))
                        continue;

                    var imageQuery = new ExecuteStatementRequest
                    {
                        Statement = @"SELECT ""ImageUrl""
                              FROM ""ImageIdeas""
                              WHERE ""Id"" = ?",
                        Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = vi.ImageId }
                }
                    };

                    var imageResult = await client.ExecuteStatementAsync(imageQuery);

                    var imageItem = imageResult.Items.FirstOrDefault();
                    if (imageItem != null && imageItem.ContainsKey("ImageUrl"))
                    {
                        vi.ImageUrl = imageItem["ImageUrl"]?.S ?? string.Empty;
                    }
                }

                return new OkObjectResult(videoImages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch video images.");
                return new StatusCodeResult(500);
            }
        }

        public class VideoImageModelGet
        {
            public string Id { get; set; }
            public string VideoId { get; set; }
            public string ImageId { get; set; }
            public string TimeStamp { get; set; }
            public string Scene { get; set; }
            public string Script { get; set; }
            public string Prompt { get; set; }
            public string PromptId { get; set; }

            // NEW FIELD FROM AZURE FUNCTION
            public string ImageUrl { get; set; }
            public string ScriptPlain { get; set; }
            public string ScriptWithSpeech { get; set; }

            // Add these new fields for customization
            public string Resolution { get; set; }
            public string VoiceOverActor { get; set; }
            public string BackgroundMusic { get; set; }
            public string VoiceOverScript { get; set; }
        }
        [Function("UpdateVideoImageIdByVideoIdAndScene")]
        public async Task<IActionResult> UpdateVideoImageIdByVideoIdAndScene(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string videoId = req.Query["videoId"];
            string scene = req.Query["scene"];
            string imageId = req.Query["imageId"];

            if (string.IsNullOrWhiteSpace(videoId))
                return new BadRequestObjectResult("Missing videoId.");

            if (string.IsNullOrWhiteSpace(scene))
                return new BadRequestObjectResult("Missing scene.");

            if (string.IsNullOrWhiteSpace(imageId))
                return new BadRequestObjectResult("Missing imageId.");

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // 1. Get the Id from the table
                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = @"
        SELECT Id 
        FROM VideoImages 
        WHERE VideoId = ? AND Scene = ?",
                    Parameters = new List<AttributeValue>
    {
        new AttributeValue { S = videoId },
        new AttributeValue { S = scene }
    }
                };

                var selectResponse = await client.ExecuteStatementAsync(selectRequest);

                if (selectResponse.Items == null || selectResponse.Items.Count == 0)
                {
                    return new NotFoundObjectResult("No matching record found.");
                }

                var id = selectResponse.Items[0]["Id"].S;

                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = @"
        UPDATE VideoImages
        SET ImageId = ?
        WHERE VideoId = ? AND Scene = ? AND Id = ?",
                    Parameters = new List<AttributeValue>
    {
        new AttributeValue { S = imageId },
        new AttributeValue { S = videoId },
        new AttributeValue { S = scene },
        new AttributeValue { S = id }
    }
                };

                await client.ExecuteStatementAsync(updateRequest);


                return new OkObjectResult(new
                {
                    message = "ImageId updated successfully.",
                    videoId,
                    scene,
                    imageId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update ImageId.");
                return new StatusCodeResult(500);
            }
        }

        [Function("UpdateScriptByVideoIdAndScene")]
        public async Task<IActionResult> UpdateScriptByVideoIdAndScene(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string videoId = req.Query["videoId"];
            string scene = req.Query["scene"];
            string script = req.Query["script"];
            string scriptPlain = req.Query["scriptPlain"];
            string scriptWithSpeech = req.Query["scriptWithSpeech"];

            if (string.IsNullOrWhiteSpace(videoId))
                return new BadRequestObjectResult("Missing videoId.");

            if (string.IsNullOrWhiteSpace(scene))
                return new BadRequestObjectResult("Missing scene.");

            if (string.IsNullOrWhiteSpace(script))
                return new BadRequestObjectResult("Missing script.");

            if (string.IsNullOrWhiteSpace(scriptPlain))
                return new BadRequestObjectResult("Missing scriptPlain.");


            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // 1. Get the Id from VideoImages
                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                SELECT Id
                FROM VideoImages
                WHERE VideoId = ? AND Scene = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = videoId },
                new AttributeValue { S = scene }
            }
                };

                var selectResponse = await client.ExecuteStatementAsync(selectRequest);

                if (selectResponse.Items == null || selectResponse.Items.Count == 0)
                {
                    return new NotFoundObjectResult("No matching record found.");
                }

                var id = selectResponse.Items[0]["Id"].S;

                // 2. UPDATE Script column
                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                UPDATE VideoImages
                SET Script = ?, ScriptPlain = ?, ScriptWithSpeech = ?
                WHERE VideoId = ? AND Scene = ? AND Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = script },
                new AttributeValue { S = scriptPlain },
                new AttributeValue { S = scriptWithSpeech },
                new AttributeValue { S = videoId },
                new AttributeValue { S = scene },
                new AttributeValue { S = id }
            }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    message = "Script updated successfully.",
                    videoId,
                    scene,
                    script
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Script.");
                return new StatusCodeResult(500);
            }
        }
        [Function("UpdateVideoImageAudioAndResolution")]
        public async Task<IActionResult> UpdateVideoImageAudioAndResolution(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string videoId = req.Query["videoId"];
            string scene = req.Query["scene"];
            string resolution = req.Query["resolution"];
            string voiceOverScript = req.Query["voiceOverScript"];
            string voiceOverActor = req.Query["voiceOverActor"];
            string backgroundMusic = req.Query["backgroundMusic"];

            if (string.IsNullOrWhiteSpace(videoId))
                return new BadRequestObjectResult("Missing videoId.");

            if (string.IsNullOrWhiteSpace(scene))
                return new BadRequestObjectResult("Missing scene.");

            if (string.IsNullOrWhiteSpace(resolution))
                return new BadRequestObjectResult("Missing resolution.");

            if (string.IsNullOrWhiteSpace(voiceOverScript))
                return new BadRequestObjectResult("Missing voiceOverScript.");

            if (string.IsNullOrWhiteSpace(voiceOverActor))
                return new BadRequestObjectResult("Missing voiceOverActor.");

            if (string.IsNullOrWhiteSpace(backgroundMusic))
                return new BadRequestObjectResult("Missing backgroundMusic.");

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // 1. Get the Id from VideoImages
                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                SELECT Id
                FROM VideoImages
                WHERE VideoId = ? AND Scene = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = videoId },
                new AttributeValue { S = scene }
            }
                };

                var selectResponse = await client.ExecuteStatementAsync(selectRequest);

                if (selectResponse.Items == null || selectResponse.Items.Count == 0)
                {
                    return new NotFoundObjectResult("No matching record found.");
                }

                var id = selectResponse.Items[0]["Id"].S;

                // 2. Update resolution and audio-related fields
                var updateRequest = new ExecuteStatementRequest
                {
                    Statement = @"
                UPDATE VideoImages
                SET Resolution = ?,
                    VoiceOverScript = ?,
                    VoiceOverActor = ?,
                    BackgroundMusic = ?
                WHERE VideoId = ? AND Scene = ? AND Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = resolution },
                new AttributeValue { S = voiceOverScript },
                new AttributeValue { S = voiceOverActor },
                new AttributeValue { S = backgroundMusic },
                new AttributeValue { S = videoId },
                new AttributeValue { S = scene },
                new AttributeValue { S = id }
            }
                };

                await client.ExecuteStatementAsync(updateRequest);

                return new OkObjectResult(new
                {
                    message = "Video image audio and resolution updated successfully.",
                    videoId,
                    scene,
                    resolution,
                    voiceOverActor,
                    backgroundMusic
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update VideoImages audio and resolution fields.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetVideoImagesByPromptId")]
        public async Task<IActionResult> GetVideoImagesByPromptId([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
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

            string promptId = req.Query["promptId"];
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return new BadRequestObjectResult("Missing promptId.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var videoImages = new List<VideoImageModel>();

                var query = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM VideoImages WHERE PromptId = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = promptId }
            }
                };

                ExecuteStatementResponse result;
                do
                {
                    result = await client.ExecuteStatementAsync(query);

                    videoImages.AddRange(result.Items.Select(item => new VideoImageModel
                    {
                        Id = item["Id"].S,
                        ImageId = item.GetValueOrDefault("ImageId")?.S ?? string.Empty,
                        VideoId = item.GetValueOrDefault("VideoId")?.S ?? string.Empty,
                        Scene = item.GetValueOrDefault("Scene")?.S ?? string.Empty,
                        PromptId = item.GetValueOrDefault("PromptId")?.S ?? string.Empty,
                        ImageUrl = item.GetValueOrDefault("ImageUrl")?.S ?? string.Empty,
                        ScriptPlain = item.GetValueOrDefault("ScriptPlain")?.S ?? string.Empty,
                        ScriptWithSpeech = item.GetValueOrDefault("ScriptWithSpeech")?.S ?? string.Empty,
                        Script = item.GetValueOrDefault("Script")?.S ?? string.Empty,
                        TimeStamp = item.GetValueOrDefault("TimeStamp")?.S ?? string.Empty
                    }));

                    query.NextToken = result.NextToken;

                } while (!string.IsNullOrEmpty(result.NextToken));

                return new OkObjectResult(videoImages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch video images.");
                return new StatusCodeResult(500);
            }
        }

        public class VideoImageModel
        {
            public string Id { get; set; }
            public string ImageId { get; set; }
            public string VideoId { get; set; }
            public string Scene { get; set; }
            public string PromptId { get; set; }
            public string TimeStamp { get; set; }
            public string Script { get; set; }

            // NEW FIELD FROM AZURE FUNCTION
            public string ImageUrl { get; set; }
            public string ScriptPlain { get; set; }
            public string ScriptWithSpeech { get; set; }
        }


        [Function("SaveConceptImage")]
        public async Task<IActionResult> SaveConceptImage([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var conceptImageRequest = System.Text.Json.JsonSerializer.Deserialize<ConceptImageRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                string conceptImageGuid;
                while (true)
                {
                    conceptImageGuid = Guid.NewGuid().ToString();

                    var checkIdRequest = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM ConceptImages WHERE Id = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = conceptImageGuid }
                        }
                    };

                    var existingIdResult = await client.ExecuteStatementAsync(checkIdRequest);
                    if (!existingIdResult.Items.Any())
                        break;
                }

                string timeStamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

                var statement = "INSERT INTO ConceptImages VALUE {'Id': ?, 'ImageId': ?, 'TimeStamp': ?, 'CompanyIdeaId': ?, 'PromptId': ?, 'VideoId': ?}";
                var parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = conceptImageGuid },
                    new AttributeValue { S = conceptImageRequest.ImageId ?? string.Empty },
                    new AttributeValue { S = timeStamp },
                    new AttributeValue { S = conceptImageRequest.CompanyIdeaId ?? string.Empty },
                    new AttributeValue { S = conceptImageRequest.PromptId ?? string.Empty },
                    new AttributeValue { S = conceptImageRequest.VideoId ?? string.Empty }
                };

                var insertRequest = new ExecuteStatementRequest
                {
                    Statement = statement,
                    Parameters = parameters
                };

                await client.ExecuteStatementAsync(insertRequest);

                return new OkObjectResult(new { Message = "ConceptImage saved successfully", Guid = conceptImageGuid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving concept image failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("CheckVideoIdExists")]
        public async Task<IActionResult> CheckVideoIdExists(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "CheckVideoIdExists/{videoId}")] HttpRequest req,
    string videoId)
        {
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

            if (string.IsNullOrWhiteSpace(videoId))
            {
                return new BadRequestObjectResult("VideoId is required.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query DynamoDB for VideoId
                var checkRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM VideoImages WHERE VideoId = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = videoId }
            }
                };

                var result = await client.ExecuteStatementAsync(checkRequest);

                bool exists = result.Items.Any();

                return new OkObjectResult(new
                {
                    Exists = exists,
                    Count = result.Items.Count,
                    Items = result.Items // you can remove this if you only want bool/count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checking video id failed.");
                return new StatusCodeResult(500);
            }
        }


        [Function("DeleteConceptImage")]
        public async Task<IActionResult> DeleteConceptImage([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var deleteRequest = System.Text.Json.JsonSerializer.Deserialize<DeleteConceptImageRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query for Id matching the ImageId
                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM ConceptImages WHERE ImageId = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = deleteRequest.ImageId }
                    }
                };

                var selectResult = await client.ExecuteStatementAsync(selectRequest);

                if (!selectResult.Items.Any())
                {
                    return new NotFoundObjectResult(new { Message = "No ConceptImage found with the given ImageId." });
                }

                // Get first matching Id
                var idToDelete = selectResult.Items.First()["Id"].S;

                // Delete by Id (required!)
                var deleteStatement = new ExecuteStatementRequest
                {
                    Statement = "DELETE FROM ConceptImages WHERE Id = ? and ImageId = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = idToDelete },
                        new AttributeValue { S = deleteRequest.ImageId }
                    }
                };

                await client.ExecuteStatementAsync(deleteStatement);

                return new OkObjectResult(new { Message = "ConceptImage deleted successfully by Id." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting concept image failed.");
                return new StatusCodeResult(500);
            }
        }

        public class DeleteBlankImageIdeasRequest
        {
            public string Subcategory { get; set; }
            public string PromptId { get; set; }
        }


        [Function("DeleteBlankImageIdeas")]
        public async Task<IActionResult> DeleteBlankImageIdeas([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var deleteRequest = System.Text.Json.JsonSerializer.Deserialize<DeleteBlankImageIdeasRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Select all items from ImageIdeas with blank ImageUrl and matching Subcategory
                var selectRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM ImageIdeas.\"PromptId-index\" WHERE PromptId = ? AND SubCategory = ? AND ImageUrl = ''",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = deleteRequest.PromptId },
                        new AttributeValue { S = deleteRequest.Subcategory }
                    }
                };//

                var selectResult = await client.ExecuteStatementAsync(selectRequest);

                if (!selectResult.Items.Any())
                {
                    return new NotFoundObjectResult(new { Message = "No ImageIdeas found with blank ImageUrl for this Subcategory." });
                }

                int deletedCount = 0;

                foreach (var item in selectResult.Items)
                {
                    var idToDelete = item["Id"].S;

                    var deleteStatement = new ExecuteStatementRequest
                    {
                        Statement = "DELETE FROM ImageIdeas WHERE PromptId = ? AND Id = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = deleteRequest.PromptId },
                            new AttributeValue { S = idToDelete }
                        }
                    };

                    await client.ExecuteStatementAsync(deleteStatement);
                    deletedCount++;
                }

                return new OkObjectResult(new { Message = $"Deleted {deletedCount} ImageIdeas with blank ImageUrl in subcategory '{deleteRequest.Subcategory}'." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting blank ImageIdeas failed.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetPromptIdForCompanyIdea")]
        public async Task<IActionResult> GetPromptIdForCompanyIdea([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string userId = req.Query["userId"];
            string companyIdeaId = req.Query["companyIdeaId"];

            if (string.IsNullOrEmpty(userId))
            {
                return new BadRequestObjectResult("Missing required parameter: userId");
            }

            if (string.IsNullOrEmpty(companyIdeaId))
            {
                return new BadRequestObjectResult("Missing required parameter: companyIdeaId");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                var promptQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM Prompt WHERE UserId = ? AND CompanyIdeaId = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = userId },
                        new AttributeValue { S = companyIdeaId }
                    }
                };

                var promptResult = await client.ExecuteStatementAsync(promptQuery);
                var promptId = promptResult.Items
                    .FirstOrDefault()?["Id"]?.S;

                if (string.IsNullOrEmpty(promptId))
                {
                    return new NotFoundObjectResult("No prompt found for the given companyIdeaId.");
                }

                return new OkObjectResult(promptId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve PromptId.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetImageIdeasForCompanyIndustry")]
        public async Task<IActionResult> GetImageIdeasForCompanyIndustry(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string userId = req.Query["userId"];
            string cid = req.Query["cid"];

            if (string.IsNullOrEmpty(userId))
            {
                return new BadRequestObjectResult("Missing required parameter: userId");
            }

            if (string.IsNullOrEmpty(cid))
            {
                return new BadRequestObjectResult("Missing required parameter: cid");
            }

            try
            {
                // Split and decode cid
                var parts = cid.Split("|~~|@@|^^|", StringSplitOptions.None);
                if (parts.Length < 2)
                {
                    return new BadRequestObjectResult("Invalid cid format.");
                }

                string companyName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                string industry = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));

                var companynameIndustry = parts[0] + "|~~|@@|^^|" + parts[1];

                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query using begins_with for CompanyIdeaId
                var promptQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id FROM Prompt WHERE UserId = ? AND begins_with(CompanyIdeaId, ?)",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = userId },
                        new AttributeValue { S = companynameIndustry }
                    }
                };

                var promptResult = await client.ExecuteStatementAsync(promptQuery);
                var promptIds = promptResult.Items
                    .Where(i => i.ContainsKey("Id"))
                    .Select(i => i["Id"].S)
                    .ToList();

                if (!promptIds.Any())
                {
                    return new NotFoundObjectResult("No prompts found for the given company and industry.");
                }

                var imageIdeas = new List<PromptImageIdeas>();
                ExecuteStatementResponse imageResult = null;

                foreach (var promptId in promptIds)
                {
                    var imageQuery = new ExecuteStatementRequest
                    {
                        Statement = "SELECT * FROM ImageIdeas.\"PromptId-index\" WHERE PromptId =  ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = promptId }
                        }
                    };

                    imageResult = await client.ExecuteStatementAsync(imageQuery);

                    // Create a PromptImageIdeas object for this promptId
                    var promptImageIdeas = new PromptImageIdeas
                    {
                        PromptId = promptId,
                        Images = new List<ImageIdeaModel>()
                    };

                    // Loop through each item in the response and populate Images
                    foreach (var item in imageResult.Items)
                    {
                        var image = new ImageIdeaModel
                        {
                            Id = item["Id"].S,
                            DeliverableType = item["DeliverableType"].S,
                            SubCategory = item["SubCategory"].S,
                            ImagePrompt = item.ContainsKey("ImagePrompt") ? item["ImagePrompt"].S : null,
                            ImageUrl = item["ImageUrl"].S,
                            PromptId = item["PromptId"].S,
                            TimeStamp = item["TimeStamp"].S,
                            CountId = item["CountId"].S
                        };

                        promptImageIdeas.Images.Add(image);
                    }

                    // Add only once per promptId
                    imageIdeas.Add(promptImageIdeas);
                }

                return new OkObjectResult(imageIdeas);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve ImageIdeas.");
                return new StatusCodeResult(500);
            }
        }

        public class PromptImageIdeas
        {
            public string PromptId { get; set; }
            public List<ImageIdeaModel> Images { get; set; } = new List<ImageIdeaModel>();
        }

        [Function("GetPromptById")]
        public async Task<IActionResult> GetPromptById([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            // Validate the request origin
            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string promptId = req.Query["promptId"];

            // Validate required parameter
            if (string.IsNullOrEmpty(promptId))
            {
                return new BadRequestObjectResult("Missing required parameter: promptId");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Query the Prompt table by Id
                var promptQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Prompt FROM Prompt WHERE Id = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = promptId }
            }
                };

                var promptResult = await client.ExecuteStatementAsync(promptQuery);

                var promptText = promptResult.Items
                    .FirstOrDefault()?["Prompt"]?.S;

                if (string.IsNullOrEmpty(promptText))
                {
                    return new NotFoundObjectResult("No prompt found for the given Id.");
                }

                return new OkObjectResult(promptText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve prompt text.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetImageIdeaCountsByCompanyId")]
        public async Task<IActionResult> GetImageIdeaCountsByCompanyId(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
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

            string companyIdeaId = req.Query["companyIdeaId"];
            if (string.IsNullOrWhiteSpace(companyIdeaId))
            {
                return new BadRequestObjectResult("Missing companyIdeaId.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ---------------------------------------------------------
                // STEP 1: Get ALL Ideas (WITH PAGINATION)
                // ---------------------------------------------------------
                var ideas = new List<object>();

                var ideaQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Title, Description FROM Ideas WHERE CompanyIdeaId = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = companyIdeaId }
            }
                };

                ExecuteStatementResponse ideaResult;

                do
                {
                    ideaResult = await client.ExecuteStatementAsync(ideaQuery);

                    foreach (var item in ideaResult.Items)
                    {
                        ideas.Add(new
                        {
                            Title = item.GetValueOrDefault("Title")?.S ?? "",
                            Description = item.GetValueOrDefault("Description")?.S ?? ""
                        });
                    }

                    ideaQuery.NextToken = ideaResult.NextToken;

                } while (!string.IsNullOrEmpty(ideaResult.NextToken));

                // Build combined idea text list
                var fullIdeaTexts = ideas
                    .Select(i =>
                    {
                        dynamic d = i;
                        return $"{d.Title}:{d.Description}".Trim().ToLower();
                    })
                    .ToList();

                // ---------------------------------------------------------
                // STEP 2: Get Prompt data
                // ---------------------------------------------------------
                var promptQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, Prompt FROM Prompt WHERE CompanyIdeaId = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = companyIdeaId }
            }
                };

                var promptResult = await client.ExecuteStatementAsync(promptQuery);
                if (!promptResult.Items.Any())
                {
                    // Ideas are created before the first Prompt/ImageIdeas generation.
                    return new OkObjectResult(new
                    {
                        ideas,
                        promptId = string.Empty,
                        promptText = string.Empty,
                        videoMatchCount = 0,
                        videoNoMatchCount = 0,
                        extractedTexts = Array.Empty<string>(),
                        imageIdeas = Array.Empty<object>()
                    });
                }

                string promptId = promptResult.Items[0]["Id"].S;
                string promptText = promptResult.Items[0].GetValueOrDefault("Prompt")?.S ?? "";

                // ---------------------------------------------------------
                // STEP 3: Query all ImageIdeas for this PromptId (WITH PAGINATION)
                // ---------------------------------------------------------
                var imageIdeasQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, CountId, DeliverableType, SubCategory, ImagePrompt FROM ImageIdeas WHERE PromptId = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = promptId }
                    }
                };


                List<string> extractedTexts = new List<string>();
                List<object> imageIdeasList = new List<object>();

                int videoMatchCount = 0;
                int videoNoMatchCount = 0;

                ExecuteStatementResponse imageIdeasResult;

                do
                {
                    imageIdeasResult = await client.ExecuteStatementAsync(imageIdeasQuery);

                    foreach (var item in imageIdeasResult.Items)
                    {
                        string id = item.GetValueOrDefault("Id")?.S ?? "";
                        string countId = item.GetValueOrDefault("CountId")?.S ?? "";
                        string deliverableType = item.GetValueOrDefault("DeliverableType")?.S?.ToLower() ?? "";
                        string subCategory = item.GetValueOrDefault("SubCategory")?.S ?? "";
                        string imagePrompt = item.GetValueOrDefault("ImagePrompt")?.S ?? "";

                        string extractedText = "";
                        var parts = imagePrompt.Split('|');
                        if (parts.Length > 1)
                            extractedText = parts[1].Trim();

                        imageIdeasList.Add(new
                        {
                            Id = id,
                            CountId = countId,
                            DeliverableType = deliverableType,
                            SubCategory = subCategory,
                            ExtractedText = extractedText,
                            RawPrompt = imagePrompt
                        });

                        // Only check video deliverables
                        if (!deliverableType.Contains("video"))
                            continue;

                        bool isMatch = fullIdeaTexts.Contains(extractedText.Trim().ToLower());

                        if (isMatch)
                            videoMatchCount++;
                        else
                            videoNoMatchCount++;
                    }

                    imageIdeasQuery.NextToken = imageIdeasResult.NextToken;

                } while (!string.IsNullOrEmpty(imageIdeasResult.NextToken));

                // ---------------------------------------------------------
                // RESPONSE
                // ---------------------------------------------------------
                return new OkObjectResult(new
                {
                    ideas = ideas,
                    promptId = promptId,
                    promptText = promptText,
                    videoMatchCount = videoMatchCount,
                    videoNoMatchCount = videoNoMatchCount,
                    extractedTexts = extractedTexts,
                    imageIdeas = imageIdeasList // <-- includes DeliverableType + SubCategory + Extracted text
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to count image ideas.");
                return new StatusCodeResult(500);
            }
        }

        [Function("GetIdeasByCompanyIdeaId")]
        public async Task<IActionResult> GetIdeasByCompanyIdeaId(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
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

            string companyIdeaId = req.Query["companyIdeaId"];
            if (string.IsNullOrWhiteSpace(companyIdeaId))
            {
                return new BadRequestObjectResult("Missing companyIdeaId.");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // ---------------------------------------------------------
                // QUERY Ideas BY CompanyIdeaId
                // ---------------------------------------------------------
                var ideaQuery = new ExecuteStatementRequest
                {
                    Statement = "SELECT Title, Description FROM Ideas WHERE CompanyIdeaId = ?",
                    Parameters = new List<AttributeValue>
            {
                new AttributeValue { S = companyIdeaId }
            }
                };

                List<object> ideaList = new List<object>();
                ExecuteStatementResponse ideaResult;

                do
                {
                    ideaResult = await client.ExecuteStatementAsync(ideaQuery);

                    foreach (var item in ideaResult.Items)
                    {
                        ideaList.Add(new
                        {
                            Title = item.GetValueOrDefault("Title")?.S ?? "",
                            Description = item.GetValueOrDefault("Description")?.S ?? ""
                        });
                    }

                    ideaQuery.NextToken = ideaResult.NextToken;

                } while (!string.IsNullOrEmpty(ideaResult.NextToken));

                if (!ideaList.Any())
                {
                    return new NotFoundObjectResult("No ideas found for given CompanyIdeaId.");
                }

                // ---------------------------------------------------------
                // RESPONSE
                // ---------------------------------------------------------
                return new OkObjectResult(new
                {
                    ideas = ideaList,
                    count = ideaList.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch ideas.");
                return new StatusCodeResult(500);
            }
        }


        [Function("GetAllConceptImagesWithIdeas")]
        public async Task<IActionResult> GetAllConceptImagesWithIdeas([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
            string referer = req.Headers["Origin"].FirstOrDefault();

            if (referer == null || referer != Environment.GetEnvironmentVariable("HostUrl"))
            {
                return new UnauthorizedResult();
            }

            string userId = req.Query["userId"];
            string companyIdeaId = req.Query["companyIdeaId"];
            string promptIdParam = req.Query["promptId"];

            if (string.IsNullOrEmpty(userId))
            {
                return new BadRequestObjectResult("Missing required parameter: userId");
            }

            if (string.IsNullOrEmpty(companyIdeaId))
            {
                return new BadRequestObjectResult("Missing required parameter: companyIdeaId");
            }

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                List<string> promptIds;

                if (!string.IsNullOrWhiteSpace(promptIdParam))
                {
                    promptIds = new List<string> { promptIdParam };
                }
                else
                {
                    var promptQuery = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM Prompt WHERE UserId = ? AND CompanyIdeaId = ?",
                        Parameters = new List<AttributeValue>
                        {
                            new AttributeValue { S = userId },
                            new AttributeValue { S = companyIdeaId }
                        }
                    };

                    var promptResult = await client.ExecuteStatementAsync(promptQuery);
                    promptIds = promptResult.Items
                        .Where(p => p.ContainsKey("Id"))
                        .Select(p => p["Id"].S)
                        .ToList();

                    if (!promptIds.Any())
                    {
                        return new OkObjectResult(new List<ConceptImageWithIdeaDto>()); // No prompts = no data
                    }
                }

                // Step 2: Get ImageIdeas for those PromptIds
                var imageIdeas = new List<Dictionary<string, AttributeValue>>();
                foreach (var promptId in promptIds)
                {
                    string nextToken = null;
                    do
                    {
                        var ideaQuery = new ExecuteStatementRequest
                        {
                            Statement = "SELECT * FROM ImageIdeas WHERE PromptId = ?",
                            Parameters = new List<AttributeValue> { new AttributeValue { S = promptId } },
                            NextToken = nextToken
                        };

                        var ideaResult = await client.ExecuteStatementAsync(ideaQuery);
                        imageIdeas.AddRange(ideaResult.Items);
                        nextToken = ideaResult.NextToken;

                    } while (!string.IsNullOrEmpty(nextToken));

                }

                // Step 2.5: Sort by CountId and build dictionary
                var imageIdeasById = imageIdeas
                    .Where(i => i.ContainsKey("Id") && i.ContainsKey("CountId"))
                    .OrderBy(i =>
                    {
                        var countId = i["CountId"].S;
                        return int.TryParse(countId, out var parsed) ? parsed : int.MaxValue;
                    })
                    .ToDictionary(i => i["Id"].S, i => i);


                var imageIds = imageIdeasById.Keys.ToList();

                if (!imageIds.Any())
                {
                    return new OkObjectResult(new List<ConceptImageWithIdeaDto>());
                }

                // Step 3: Get ConceptImages for those ImageIds
                var conceptImages = new List<Dictionary<string, AttributeValue>>();
                foreach (var imageId in imageIds)
                {
                    var conceptQuery = new ExecuteStatementRequest
                    {
                        Statement = "SELECT * FROM ConceptImages WHERE ImageId = ?",
                        Parameters = new List<AttributeValue> { new AttributeValue { S = imageId } }
                    };

                    var conceptResult = await client.ExecuteStatementAsync(conceptQuery);
                    conceptImages.AddRange(conceptResult.Items);
                }

                // Step 4: Combine data with additional fields from ImageIdeas
                // In your Azure Function, modify the results mapping to include CountId
                var results = conceptImages.Select(ci =>
                {
                    var imageId = ci.ContainsKey("ImageId") ? ci["ImageId"].S : null;
                    var videoId = ci.ContainsKey("VideoId") ? ci["VideoId"].S : null;
                    imageIdeasById.TryGetValue(imageId ?? "", out var idea);

                    return new ConceptImageWithIdeaDto
                    {
                        ConceptImageId = ci["Id"].S,
                        ImageId = imageId,
                        TimeStamp = ci["TimeStamp"].S,
                        IdeaTitle = idea != null && idea.ContainsKey("Title") ? idea["Title"].S : null,
                        ImageUrl = idea != null && idea.ContainsKey("ImageUrl") ? idea["ImageUrl"].S : null,
                        DeliverableType = idea != null && idea.ContainsKey("DeliverableType") ? idea["DeliverableType"].S : null,
                        SubCategory = idea != null && idea.ContainsKey("SubCategory") ? idea["SubCategory"].S : null,
                        CountId = idea != null && idea.ContainsKey("CountId") ? idea["CountId"].S : null,
                        VideoId = videoId // New field
                    };
                }).ToList();

                return new OkObjectResult(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve filtered ConceptImages and ImageIdeas.");
                return new StatusCodeResult(500);
            }
        }

        public class ConceptImageWithIdeaDto
        {
            public string ConceptImageId { get; set; }
            public string ImageId { get; set; }
            public string TimeStamp { get; set; }
            public string IdeaTitle { get; set; }
            public string ImageUrl { get; set; }
            public string DeliverableType { get; set; }
            public string SubCategory { get; set; }
            public string CountId { get; set; } // New property
            public string VideoId { get; set; } // New property
        }

        public class DeleteConceptImageRequest
        {
            public string ImageId { get; set; }
        }

        public class ConceptImageRequest
        {
            public string ImageId { get; set; }
            public string CompanyIdeaId { get; set; }
            public string PromptId { get; set; }
            public string VideoId { get; set; }
        }

        public class ImageIdeaModel
        {
            public string Id { get; set; }
            public string DeliverableType { get; set; }
            public string SubCategory { get; set; }
            public string ImagePrompt { get; set; }
            public string ImageUrl { get; set; }
            public string PromptId { get; set; }
            public string TimeStamp { get; set; }
            public string CountId { get; set; }
        }

        // Define your request model for deserialization
        public class ImageIdeaRequest
        {
            public string DeliverableType { get; set; }
            public string SubCategory { get; set; }
            public string ImagePrompt { get; set; }
            public string ImageUrl { get; set; }
            public string PromptId { get; set; }
            public string TimeStamp { get; set; }
            public string CountId { get; set; }
        }

        [Function("CheckCompanyIndustryAndIdeasExists")]
        public async Task<IActionResult> CheckCompanyIndustryAndIdeasExists([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
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

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var companyData = System.Text.Json.JsonSerializer.Deserialize<CompanyIndustryRequest>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            try
            {
                var client = global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();

                // Step 1: Check if company name + industry exists
                var checkCompanyRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, Name, Industry FROM CompanyIndustry WHERE Name = ? AND Industry = ? AND UserId = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = companyData.Name },
                        new AttributeValue { S = companyData.Industry },
                        new AttributeValue { S = companyData.UserId }
                    }
                };

                var companyResult = await client.ExecuteStatementAsync(checkCompanyRequest);

                if (!companyResult.Items.Any())
                {
                    return new OkObjectResult(new { Exists = false, HasIdeas = false, Message = "Company and industry not found." });
                }

                var companyItem = companyResult.Items.First();
                var companyId = companyItem["Id"].S;
                var companyName = companyItem["Name"].S;
                var industry = companyItem["Industry"].S;

                // Step 2: Check if ideas exist for the company
                var checkIdeasRequest = new ExecuteStatementRequest
                {
                    Statement = "SELECT * FROM Ideas WHERE CompanyIdeaId = ?",
                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = companyId }
                    }
                };

                var ideasResult = await client.ExecuteStatementAsync(checkIdeasRequest);
                bool hasIdeas = ideasResult.Items.Any();

                if (hasIdeas)
                {
                    string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                    string compositeKey = $"{companyName}_{industry}_{timestamp}";

                    return new OkObjectResult(new
                    {
                        Exists = true,
                        HasIdeas = true,
                        CompositeKey = compositeKey,
                        Message = "Company and industry found, and it has associated ideas."
                    });
                }

                return new OkObjectResult(new
                {
                    Exists = true,
                    HasIdeas = false,
                    Message = "Company and industry found, but no associated ideas."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Check for company and ideas failed.");
                return new StatusCodeResult(500);
            }
        }

        // Supporting model
        public class IdeaRequest
        {
            public string Title { get; set; }
            public string Description { get; set; }
            public string UserId { get; set; }
            public string CompanyIdeaId { get; set; }
        }

        public class PromptRequest
        {
            public string Prompt { get; set; }
            public string CompanyIdeaId { get; set; }
            public string UserId { get; set; }
            public string TimeStamp { get; set; }
            public string InviteId { get; set; }
        }

        private static long GenerateRandomSeed()
        {
            byte[] buffer = new byte[8];
            new Random().NextBytes(buffer);
            return Math.Abs(BitConverter.ToInt64(buffer, 0));
        }

        private static readonly string[] Urls =
        {
            "https://www.designmode.ai/designerintro?name=orange+blast&industry=energy+drinks&type=social&companyIndustryId=40c3ca51-a3e7-4a1c-ae2c-980f993bd93c&funnelId=190c8aa3-fd38-4684-a503-f9d6a65c31c1",

            "https://www.designmode.ai/init/mainconcept?i=1&id=a6f1d134-9636-44fc-aa65-516cd1f2842e&e=NjQyMDBAZGVzaWdubW9kZS5haQ==&eid=NjQyMDBAZGVzaWdubW9kZS5haQ==",

            "https://www.designmode.ai/init/funnelend?i=1&id=a6f1d134-9636-44fc-aa65-516cd1f2842e&e=NjQyMDBAZGVzaWdubW9kZS5haQ==&eid=NjQyMDBAZGVzaWdubW9kZS5haQ=="
        };

        [Function("KeepPagesWarm")]
        public async Task KeepPagesWarm(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer)
        {
            _logger.LogInformation("Starting page warmup");

            var tasks = Array.ConvertAll(
                Urls,
                url => WarmPageAsync(url)
            );

            await Task.WhenAll(tasks);

            _logger.LogInformation("Page warmup completed");
        }

        private async Task WarmPageAsync(string url)
        {
            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(url);

                _logger.LogInformation(
                    "Warmup {Page}: HTTP {StatusCode}",
                    new Uri(url).AbsolutePath,
                    (int)response.StatusCode
                );

                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Warmup failed for {Page}",
                    new Uri(url).AbsolutePath
                );
            }
        }

        [Function("KeepWarm")]
        public async Task<IActionResult> KeepWarm(
            [TimerTrigger("0 */5 * * * *")] TimerInfo myTimer)
        {
            return new OkObjectResult("OK");
        }
        public class AudioResultDto
        {
            [JsonPropertyName("audioUrl")]
            public string AudioUrl { get; set; } = string.Empty;
        }
    }
}

//Design a concept lookbook and brand story for EchoLoom, a clothing line where patterns are generated from your favorite music frequencies.Integrate visuals that echo soundwaves, motion graphics, and animated textiles into an immersive fashion-meets-tech aesthetic.

//EchoLoom

//Fashion / Music Tech

//Instagram, Facebook, Twitter

//Social Media Content Description: Develop engaging social media posts highlighting EchoLoom's products. Display: Instagram, Facebook, Twitter. Format: JPG, MP4. Dimensions/Size: Optimized for each platform. Quantity: 30 posts. Technical Requirements: Scheduling tools integration.

//NO: now write a 1 paragraph alternative prompt for EchoLoom for a Branding & Identity deliverable.

//what first deliverable category is the most important for EchoLoom "Design a concept lookbook and brand story for EchoLoom, a clothing line where patterns are generated from your favorite music frequencies. Integrate visuals that echo soundwaves, motion graphics, and animated textiles into an immersive fashion-meets-tech aesthetic."?  Branding & Identity, Advertising Print ads, Social Media Content, Digital Marketing, Web & UX/UI Website, Video & Photography, Events & Activations, Packaging Label?

//what would the next most important deliverable category for EchoLoom?

//I'm working on an application that allows clients and designers connect to get creative work done.  The app starts by giving clients AI generated images as ideas and then after they select what they want they're able to connect with a designer to get it completed.I attached 2 images that represent the initial steps for the client.  The first set asks for company name and industry.  The second allows the client to describe their creative idea.  I want the next step to start providing different ideas with AI images and have them select, and expand and then eventually have a designer or more finish the project.I need help with the design and copy of the next step.

//A clean, modern user interface (UI) design for a creative project platform.The screen shows a large expanded concept image on the left (such as a web UI/UX concept), with a panel on the right side.The right panel includes: a project title, short description text, a text input box for user notes, and two buttons: "Refine this Idea" and a large "Continue with this Concept" button.Minimalist layout, light background colors(white and light gray), soft drop shadows, rounded corners on containers, stylish modern font like Inter or Helvetica.Interface should feel creative, inviting, and easy to navigate. Top navigation bar with a "Back to Concepts" button on the left and a "Help" link on the right. Professional, polished, clean digital UI/UX mockup. --v 5 --ar 16:9 --style clean, minimalist, professional

//[make a product page for Website.][Design a vibrant, energizing one - page website for Zest Lab, a forward - thinking beverage startup offering adaptogenic energy drinks made with superfoods.The site should immediately convey the brand’s commitment to wellness, sustainability, and innovation.Include sections for product highlights, health benefits, customer testimonials, subscription options, and an interactive flavor quiz.The UI should be mobile - first, visually dynamic, and optimized for conversion, with bold colors, smooth animations, and intuitive navigation.][company name: Zest Lab][Industry: Beverage / Health & Wellness] Web & UX/UI Website Description: Redesign of the Zest Lab website for a better user experience. Display: Website Format: Figma, Sketch Dimensions/Size: Responsive design Quantity: 1 Technical Requirements: User-friendly navigation and mobile responsiveness.

//what first deliverable category is the most important for Zest Lab "Design a vibrant, energizing one-page website for Zest Lab, a forward-thinking beverage startup offering adaptogenic energy drinks made with superfoods. The site should immediately convey the brand’s commitment to wellness, sustainability, and innovation. Include sections for product highlights, health benefits, customer testimonials, subscription options, and an interactive flavor quiz. The UI should be mobile-first, visually dynamic, and optimized for conversion, with bold colors, smooth animations, and intuitive navigation."? Branding & Identity, Advertising Print ads, Social Media Content, Digital Marketing, Web & UX/UI Website, Video & Photography, Events & Activations, Packaging Label? respond with just the deliverable category

//'Branding & Identity, Web & UX/UI Website, Video & Photography, Events & Activations, Packaging Label.'

//Phoenix 1.0 

//[make a product page for Website.][Design a vibrant, energizing one-page website for Zest Lab, a forward-thinking beverage startup offering adaptogenic energy drinks made with superfoods.The site should immediately convey the brand’s commitment to wellness, sustainability, and innovation.Include sections for product highlights, health benefits, customer testimonials, subscription options, and an interactive flavor quiz.The UI should be mobile-first, visually dynamic, and optimized for conversion, with bold colors, smooth animations, and intuitive navigation.][company name: Zest Lab][Industry: Beverage / Health & Wellness] Web & UX/UI Website Description: Redesign of the Zest Lab website for a better user experience. Display: Website Format: Figma, Sketch Dimensions/Size: Responsive design Quantity: 1 Technical Requirements: User-friendly navigation and mobile responsiveness.

//[make a product page for Website, social media, ads.][Produce a 30 - second stop - motion video for CrunchCraft’s new kale chips.Storyboard must highlight farm - to - snack process, with close - ups of crunch texture.][company name:  CrunchCraft][Industry: Artisanal Snack Brand] Video & Photography Description: Promotional videos and product photography.Display: Website, social media, ads.Format: MP4, JPEG.Dimensions/Size: Full HD video, high-resolution photos. Quantity: 3 videos, 20 photos.Technical Requirements: Professional quality.

//Generate one prompt for a Social Media Content creative deliverable to get work done for a company in the Therapeutic Environments industry named NeuroNest Sensory Domes based of the prompt "Concept design for "NeuroNest" relaxation domes combining sound baths, light therapy, and micro-vibrations. Create the curved architecture and interior control interfaces.". For the idea, provide a short, one or two-word creative design idea followed by a more detailed sentence that elaborates on the idea with 15 words or less.The sentence should describe the creative direction, or any relevant marketing aspects for each concept. only have 1 colon in the response. Add company name the sentence.Add the name of the company if the name was provided. Don't add influencer ideas to the list. Don't have quotes.

//Social Media Content:  Luminous Cocoon: A serene escape by NeuroNest Sensory Domes blending light, sound, and touch for holistic calm.

//[make a product page for Product packaging.][Label Design: Natural Bliss - Earthy tones and minimalist design for a clean, organic look for PureDew brand.][company name: PureDew][Industry: Organic Sparkling Water] Packaging Label Description: Design new labels for PureDew bottles.Display: Product packaging. Format: PDF.Dimensions/Size: Label size - TBD.Quantity: 2 label designs. Technical Requirements: Include product information and QR code.

//[make a product page for Website, social media platforms.][Design a responsive website for FitFolk, featuring trainer bios, class booking UX, and a member portal.Include wireframes, a style tile, and mobile-first prototypes.] [Video & Photography: Transformation Stories - Real-life client success stories captured through videos and photos to showcase FitFolk's impact.] [company name: FitFolk] [Industry: Online Fitness Coaching] Video & Photography Description: Produce a promotional video showcasing FitFolk's online fitness coaching platform.Display: Website, social media platforms Format: MP4 Dimensions/Size: 1920x1080 pixels Quantity: 1 Technical Requirements: High-quality video with professional editing.

//19.9342
//19.9278
//19.9144
//19.9112
//19.908

//runware 4 images price $0.0134
//runware 1 image price $0.0032

//[make a product page for Website homepage, product pages.]
//        [Web & UX/UI Website:  Enchanted E-Commerce: - Interactive website with dark, mystical visuals and easy navigation for purchasing WitchCraft Cocktail Mixers online.]
//        [company name: WitchCraft(No relation to actual witchcraft)]
//        [Industry: Gothic Cocktail Mixers]
//        [Web & UX/UI Website Description: Design a user-friendly website for WitchCraft Gothic Cocktail Mixers.Display: Website homepage, product pages Format: Figma, HTML Dimensions/Size: Responsive design for desktop and mobile Quantity: 1 website Technical Requirements: Ensure easy navigation and seamless user experience.]
//        [Display the creative content clearly within device screens(phones, tablets, browsers, laptops, computers), and surround the devices with relevant contextual items and props that enhance the product narrative.Ensure consistency in lighting, color palette, and thematic cohesion.]

//        [make a product page for Website.]
//        [Create a Google Ads campaign and landing page for SwiftRide’s city launch.Include: 3 ad variations, targeted keywords, and a lead-gen form for early-bird discounts.]
//        [Web & UX/UI Website: Seamless Ride - User-friendly website design highlighting SwiftRide's easy booking process and sustainability mission.] [company name: SwiftRide] [Industry: Electric Scooter Sharing] [Web & UX/UI Website Description: Design a user-friendly website for SwiftRide's electric scooter sharing service.Display: Website Format: Figma, Sketch Dimensions/Size: Responsive design for desktop and mobile Quantity: 1 website design Technical Requirements: Include interactive features and easy navigation.]
//        [Display the creative content clearly within device screens(phones, tablets, browsers, laptops, computers), and surround the devices with relevant contextual items and props that enhance the product narrative.Ensure consistency in lighting, color palette, and thematic cohesion.]

//        [make a product page for Instagram, Facebook, Twitter.][Develop a 7-day TikTok campaign for ByteBites’ ‘Burger of the Future’ (3D-printed patties), featuring surreal food art, chef collabs, and a #DesignYourBurger challenge.] [company name: ByteBites] [Industry: AI-Generated Fast Food] [Social Media Content Description: Customized social media graphics and videos Display: Instagram, Facebook, Twitter Format: JPEG, MP4 Dimensions/Size: Square (1080x1080 pixels), Landscape (1920x1080 pixels) Quantity: 20 graphics, 5 videos Technical Requirements: Attention-grabbing visuals, engaging captions.] [Display the creative content clearly within device screens (phones, tablets), and surround the devices with relevant contextual items and props that enhance the product narrative. Ensure consistency in lighting, color palette, and thematic cohesion.]

//[make a product page for Product packaging.][Tailoring meal plans based on unique biometrics for ChromaChef Food Scanner to revolutionize healthy eating.][Packaging Label:  Transparent Packaging - Replecting ChromaChef's commitment to quality and transparency in its ingredients.] [company name: ChromaChef] [Industry: Nutrition Tech] [Packaging Label Description: Redesign packaging labels for ChromaChef products. Display: Product packaging Format: PDF, EPS Quantity: 3 packaging label designs.] [Render the label design clearly on the appropriate packaging surface (e.g., bottle, box, pouch, jar) in both flat and realistic 3D perspectives. Surround the packaging with relevant props, ingredients, or lifestyle elements that reflect the product's use and market appeal.Ensure material textures, lighting, and branding are consistent and visually polished.]

//        [make a product page for Social media platforms(Facebook, Instagram, Twitter).][Tailoring meal plans based on unique biometrics for ChromaChef Food Scanner to revolutionize healthy eating.][company name: ChromaChef][Industry: Nutrition Tech][Social Media Ads Description: Eye - catching ads showcasing ChromaChef Food Scanner's features and benefits. Display: Social media platforms (Facebook, Instagram, Twitter) Format: JPG or PNG Dimensions/Size: 1200x628 pixels Quantity: 10 variations Technical Requirements: Include call-to-action buttons and brand logo.] [Display the creative content clearly within device screens (phones, tablets), and surround the devices with relevant contextual items and props that enhance the product narrative. Ensure consistency in lighting, color palette, and thematic cohesion.]

//        [make a product page for Instagram, Facebook, Twitter.][Social Media Story Series: Showcase user testimonials and journey highlights on SwiftRide's social media to build brand credibility.] [company name: SwiftRide] [Industry: Electric Scooter Sharing] [Social Media Content Description: Design engaging social media graphics and posts for SwiftRide's social media channels.Display: Instagram, Facebook, Twitter Format: JPEG, PNG Dimensions / Size: Square(1080x1080 pixels), Story(1080x1920 pixels) Quantity: 15 graphics / posts Technical Requirements: Include relevant hashtags and brand messaging.][Display the creative content clearly within device screens(phones, tablets), and surround the devices with relevant contextual items and props that enhance the product narrative.Ensure consistency in lighting, color palette, and thematic cohesion.]

//        [make a product page for Website, social media platforms.][Design a responsive website for FitFolk, featuring trainer bios, class booking UX, and a member portal.Include wireframes, a style tile, and mobile-first prototypes.] [Video & Photography: Transformation Stories - Real-life client success stories captured through videos and photos to showcase FitFolk's impact.] [company name: FitFolk] [Industry: Online Fitness Coaching] [Video & Photography Description: Produce a promotional video showcasing FitFolk's online fitness coaching platform.Display: Website, social media platforms Format: MP4 Dimensions/Size: 1920x1080 pixels Quantity: 1 Technical Requirements: High-quality video with professional editing.][Stage the product or subject within a well-composed, cinematic or editorial-style setting, using natural or studio lighting, shallow depth of field, and dynamic angles to create a sense of movement or realism.Include props, people, or environments that support the narrative and highlight the product's purpose or lifestyle context. Ensure color grading, texture, and visual tone match the intended aesthetic.]


//        [make a product page for Website, social media, packaging.] [AquaVortex is a cutting-edge appliance manufacturer specializing in AI-powered washers and dryers that combine water efficiency, self-cleaning tech, and fabric-preserving innovation. Targeting eco-conscious homeowners and tech-savvy renters, AquaVortex machines use vortex-based cleaning (inspired by natural whirlpools) to reduce water waste while delivering superior cleaning power.] [Logo Design: AquaWave: A minimalist logo with a water wave element, representing innovation and technology for AquaVortex Appliances.] [company name: AquaVortex Appliances] [Industry: Smart Home Appliances] [Branding & Identity Description: Develop a cohesive brand identity for AquaVortex Appliances, including logo, color palette, and typography. Display: Website, social media, packaging Format: Vector files, brand guidelines document Dimensions/Size: N/A Quantity: 1 set Technical Requirements: High-resolution files for scalability.] [Display the creative content clearly within device screens (phones, tablets), and surround the devices with relevant contextual items and props that enhance the product narrative. Ensure consistency in lighting, color palette, and thematic cohesion.]

//[make a product page for YouTube, website, social media.] [AquaVortex is a cutting-edge appliance manufacturer specializing in AI-powered washers and dryers that combine water efficiency, self-cleaning tech, and fabric-preserving innovation. Targeting eco-conscious homeowners and tech-savvy renters, AquaVortex machines use vortex-based cleaning (inspired by natural whirlpools) to reduce water waste while delivering superior cleaning power.] [Video & Photography: Aqua Fusion: Produce promotional videos showcasing the integration of technology and water efficiency in AquaVortex Appliances.]  [company name: AquaVortex Appliances]  [Industry: Smart Home Appliances] [Video & Photography Description: Product demos, lifestyle photos, and promotional videos. Display: YouTube, website, social media. Format: MP4, JPEG. Dimensions/Size: HD quality for videos, high-resolution for photos. Quantity: 2 videos, 10 photos. Technical Requirements: Professional editing and lighting.]  [Stage the product or subject within a well-composed, cinematic or editorial-style setting, using natural or studio lighting, shallow depth of field, and dynamic angles to create a sense of movement or realism.Include props, people, or environments that support the narrative and highlight the product's purpose or lifestyle context. Ensure color grading, texture, and visual tone match the intended aesthetic.]

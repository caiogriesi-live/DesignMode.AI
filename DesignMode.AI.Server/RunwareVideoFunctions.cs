using System.Linq;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;

public class RunwareVideoFunctions
{
    private readonly AttributionStore attributionStore;
    private readonly JwtUserResolver jwtUserResolver;
    private readonly AIUsageSummaryService aiUsageSummary;
    private readonly AIUsageRecorder aiUsageRecorder;

    public RunwareVideoFunctions(
        AttributionStore attributionStore,
        JwtUserResolver jwtUserResolver,
        AIUsageSummaryService aiUsageSummary,
        AIUsageRecorder aiUsageRecorder)
    {
        this.attributionStore = attributionStore;
        this.jwtUserResolver = jwtUserResolver;
        this.aiUsageSummary = aiUsageSummary;
        this.aiUsageRecorder = aiUsageRecorder;
    }

    [Function("CallRunwareFinalizeSceneVideo")]
    public async Task<IActionResult> CallRunwareFinalizeSceneVideo(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = null)]
        HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("CallRunwareFinalizeSceneVideo");

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
                return new BadRequestObjectResult("Request body cannot be empty.");

            data = JObject.Parse(requestBody);
        }
        catch (JsonReaderException ex)
        {
            log.LogWarning(ex, "Invalid JSON request body.");
            return new BadRequestObjectResult("Request body must contain valid JSON.");
        }

        string positivePrompt = data.Value<string>("positivePrompt");
        string model = data.Value<string>("model") ?? "bytedance:2@2";

        int width = data.Value<int?>("width") ?? 864;
        int height = data.Value<int?>("height") ?? 480;
        double duration = data.Value<double?>("duration") ?? 5.0;

        int numberResults = data.Value<int?>("numberResults") ?? 1;
        int outputQuality = data.Value<int?>("outputQuality") ?? 95;

        string outputType = data.Value<string>("outputType") ?? "URL";
        string outputFormat = data.Value<string>("outputFormat") ?? "MP4";

        bool includeCost = data.Value<bool?>("includeCost") ?? true;
        string generationJobId = data.Value<string>("generationJobId") ?? string.Empty;
        string providerOperationId = data.Value<string>("providerOperationId") ?? string.Empty;
        string projectId = data.Value<string>("projectId") ?? string.Empty;
        string funnelId = data.Value<string>("funnelId") ?? string.Empty;
        int sceneNumber = data.Value<int?>("sceneNumber") ?? 0;
        if (sceneNumber < 1) return new BadRequestObjectResult("A positive sceneNumber is required.");

        if (string.IsNullOrWhiteSpace(positivePrompt))
            return new BadRequestObjectResult("Missing 'positivePrompt'.");

        if (width <= 0 || height <= 0)
            return new BadRequestObjectResult("Width and height must be greater than zero.");

        // Seedance 1.0 Pro Fast supports approximately 1.2 - 12 seconds.
        duration = Math.Clamp(duration, 1.2, 12.0);

        JToken frameImagesToken =
            data.SelectToken("inputs.frameImages") ??
            data["frameImages"];

        var frameImages =
            frameImagesToken?.ToObject<List<string>>() ??
            new List<string>();

        frameImages = frameImages
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (frameImages.Count == 0)
        {
            return new BadRequestObjectResult(
                "At least one image URL is required in frameImages.");
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

        if (sceneNumber == 1)
        {
            var quota = await aiUsageSummary.EvaluateAsync(userId!, "commercial_video");
            if (!quota.Allowed)
                return new ObjectResult(new { code = "ai_generation_limit_exhausted" })
                { StatusCode = StatusCodes.Status429TooManyRequests };
        }

        string taskUuid = Guid.NewGuid().ToString();
        string? usageId;
        try
        {
            usageId = await aiUsageRecorder.StartAsync(new AIGenerationUsage
            {
                UserId = userId, ProjectId = projectId, FunnelId = funnelId,
                GenerationJobId = generationJobId, ProviderTaskId = providerOperationId,
                GenerationCategory = "PaidWorkspace", DeliverableType = "CommercialVideo",
                Feature = "commercial_video", UsageScope = "Paid",
                GenerationUnits = sceneNumber == 1 ? aiUsageSummary.UnitsFor("commercial_video") : 0,
                Operation = "VideoGeneration", Provider = "Runware", Model = model,
                VideoSeconds = Convert.ToDecimal(duration), PricingVersion = AIProviderPricingService.CurrentVersion
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
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(90)
            };

            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            string submitJson =
                JsonConvert.SerializeObject(new object[] { videoTask });

            using var submitContent = new StringContent(
                submitJson,
                Encoding.UTF8,
                "application/json");

            using var submitResponse =
                await httpClient.PostAsync(apiUrl, submitContent);

            string submitBody =
                await submitResponse.Content.ReadAsStringAsync();

            if (!submitResponse.IsSuccessStatusCode)
            {
                await aiUsageRecorder.CompleteFailureAsync(usageId, "ProviderError");
                log.LogError(
                    "Runware scene submission failed. Status {Status}. Response {Response}",
                    (int)submitResponse.StatusCode,
                    submitBody);

                return new ObjectResult(new
                {
                    success = false,
                    taskUUID = taskUuid,
                    statusCode = (int)submitResponse.StatusCode,
                    runwareResponse = SafeParseJson(submitBody)
                })
                {
                    StatusCode = (int)submitResponse.StatusCode
                };
            }

            // Video generation is async. Poll getResponse until Runware
            // returns videoURL, reports an error, or the timeout is reached.
            var pollDeadline = DateTime.UtcNow.AddMinutes(6);

            while (DateTime.UtcNow < pollDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));

                var pollTask = new object[]
                {
                    new
                    {
                        taskType = "getResponse",
                        taskUUID = taskUuid
                    }
                };

                using var pollContent = new StringContent(
                    JsonConvert.SerializeObject(pollTask),
                    Encoding.UTF8,
                    "application/json");

                using var pollResponse =
                    await httpClient.PostAsync(apiUrl, pollContent);

                string pollBody =
                    await pollResponse.Content.ReadAsStringAsync();

                if (!pollResponse.IsSuccessStatusCode)
                {
                    log.LogWarning(
                        "Runware poll for {TaskUUID} returned HTTP {Status}: {Response}",
                        taskUuid,
                        (int)pollResponse.StatusCode,
                        pollBody);

                    // Transient poll errors should not immediately discard an
                    // otherwise-running video generation task.
                    continue;
                }

                JToken parsed = SafeParseJson(pollBody);

                var errors = parsed["errors"] as JArray;

                if (errors != null && errors.Count > 0)
                {
                    await aiUsageRecorder.CompleteFailureAsync(usageId, "ProviderError");
                    log.LogError(
                        "Runware task {TaskUUID} failed: {Errors}",
                        taskUuid,
                        errors.ToString(Formatting.None));

                    return new ObjectResult(new
                    {
                        success = false,
                        taskUUID = taskUuid,
                        errors
                    })
                    {
                        StatusCode = StatusCodes.Status502BadGateway
                    };
                }

                var videoResult = FindVideoResult(parsed);

                if (videoResult != null)
                {
                    string videoUrl =
                        videoResult.Value<string>("videoURL") ??
                        videoResult.Value<string>("videoUrl");

                    if (!string.IsNullOrWhiteSpace(videoUrl))
                    {
                        await aiUsageRecorder.CompleteSuccessAsync(
                            usageId,
                            videoResult.Value<decimal?>("cost"));
                        return new OkObjectResult(new
                        {
                            success = true,
                            taskUUID = taskUuid,
                            videoUrl,
                            videoUUID =
                                videoResult.Value<string>("videoUUID") ?? "",
                            cost =
                                videoResult.Value<double?>("cost")
                        });
                    }
                }

                string status =
                    parsed.SelectToken("data[0].status")?.Value<string>()
                    ?? string.Empty;

                log.LogInformation(
                    "Runware scene task {TaskUUID} status: {Status}",
                    taskUuid,
                    string.IsNullOrWhiteSpace(status)
                        ? "processing"
                        : status);
            }

            return new ObjectResult(new
            {
                success = false,
                taskUUID = taskUuid,
                error = "Runware video generation did not finish before the polling timeout."
            })
            {
                StatusCode = StatusCodes.Status504GatewayTimeout
            };
        }
        catch (TaskCanceledException ex)
        {
            await aiUsageRecorder.CompleteFailureAsync(usageId, "Timeout");
            log.LogError(ex, "Runware video request timed out.");

            return new ObjectResult(new
            {
                success = false,
                taskUUID = taskUuid,
                error = "Runware video request timed out."
            })
            {
                StatusCode = StatusCodes.Status504GatewayTimeout
            };
        }
        catch (HttpRequestException ex)
        {
            await aiUsageRecorder.CompleteFailureAsync(usageId, "ProviderError");
            log.LogError(ex, "Unable to contact Runware.");

            return new ObjectResult(new
            {
                success = false,
                taskUUID = taskUuid,
                error = "Unable to contact Runware."
            })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            await aiUsageRecorder.CompleteFailureAsync(usageId, "UnexpectedError");
            log.LogError(ex, "Unexpected Runware video error.");

            return new ObjectResult(new
            {
                success = false,
                taskUUID = taskUuid,
                error = ex.Message
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("ComposeFinalCommercialVideo")]
    public async Task<IActionResult> ComposeFinalCommercialVideo(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = null)]
        HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("ComposeFinalCommercialVideo");

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

        ComposeCommercialRequest request;

        try
        {
            using var reader = new StreamReader(req.Body);
            string body = await reader.ReadToEndAsync();

            request =
                JsonConvert.DeserializeObject<ComposeCommercialRequest>(body);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Invalid composition request.");

            return new BadRequestObjectResult(
                "Unable to parse composition request.");
        }

        if (request?.SceneVideoUrls == null ||
            request.SceneVideoUrls.Count == 0)
        {
            return new BadRequestObjectResult(
                "sceneVideoUrls must contain at least one rendered scene.");
        }

        request.NarrationAudioUrls ??= new List<string>();

        while (request.NarrationAudioUrls.Count <
               request.SceneVideoUrls.Count)
        {
            request.NarrationAudioUrls.Add(string.Empty);
        }

        string ffmpegPath = ResolveFfmpegPath(log);

        // TEMPORARY PROD DIAGNOSTICS
        log.LogInformation(
            "App base directory: {BaseDirectory}",
            AppContext.BaseDirectory);

        log.LogInformation(
            "Resolved FFmpeg path: {FfmpegPath}",
            ffmpegPath);

        log.LogInformation(
            "FFmpeg exists: {Exists}",
            File.Exists(ffmpegPath));

        log.LogInformation(
            "Operating system: {OS}",
            Environment.OSVersion);

        log.LogInformation(
            "Current directory: {CurrentDirectory}",
            Environment.CurrentDirectory);

        try
        {
            log.LogInformation(
                "Testing FFmpeg executable with -version...");

            var testStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var testProcess =
                new Process
                {
                    StartInfo = testStartInfo
                };

            testProcess.Start();

            string testOutput =
                await testProcess.StandardOutput.ReadToEndAsync();

            string testError =
                await testProcess.StandardError.ReadToEndAsync();

            await testProcess.WaitForExitAsync();

            log.LogInformation(
                "FFmpeg test exit code: {ExitCode}",
                testProcess.ExitCode);

            log.LogInformation(
                "FFmpeg test stdout: {Output}",
                testOutput);

            if (!string.IsNullOrWhiteSpace(testError))
            {
                log.LogInformation(
                    "FFmpeg test stderr: {Error}",
                    testError);
            }
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "FFmpeg executable test FAILED. Path: {FfmpegPath}",
                ffmpegPath);

            throw;
        }

        string storageConnectionString =
            Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            return new ObjectResult(new
            {
                success = false,
                error = "AzureWebJobsStorage is not configured."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }

        string containerName =
            Environment.GetEnvironmentVariable("FINAL_VIDEO_CONTAINER")
            ?? "final-videos";

        string workDirectory =
            Path.Combine(
                Path.GetTempPath(),
                "commercial-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workDirectory);

        try
        {
            using var downloadClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            var segmentFiles = new List<string>();

            for (int i = 0; i < request.SceneVideoUrls.Count; i++)
            {
                string sceneVideoFile =
                    Path.Combine(workDirectory, $"scene-{i + 1}.mp4");

                await DownloadFileAsync(
                    downloadClient,
                    request.SceneVideoUrls[i],
                    sceneVideoFile);

                string narrationUrl =
                    request.NarrationAudioUrls[i];

                string narrationFile = string.Empty;

                if (!string.IsNullOrWhiteSpace(narrationUrl))
                {
                    narrationFile =
                        Path.Combine(workDirectory, $"voice-{i + 1}.mp3");

                    await DownloadFileAsync(
                        downloadClient,
                        narrationUrl,
                        narrationFile);
                }

                string normalizedSegment =
                    Path.Combine(workDirectory, $"segment-{i + 1}.mp4");

                if (!string.IsNullOrWhiteSpace(narrationFile))
                {
                    // Normalize every segment to H.264/AAC. apad keeps an audio
                    // stream available if narration is shorter than the clip.
                    string args =
                        $"-y -i {Q(sceneVideoFile)} -i {Q(narrationFile)} " +
                        "-filter_complex \"[1:a]apad[a]\" " +
                        "-map 0:v:0 -map \"[a]\" " +
                        "-c:v libx264 -preset veryfast -crf 21 -pix_fmt yuv420p " +
                        "-c:a aac -b:a 192k -ar 44100 -ac 2 -shortest " +
                        Q(normalizedSegment);

                    await RunFfmpegAsync(
                        ffmpegPath,
                        args,
                        workDirectory,
                        log);
                }
                else
                {
                    // Create a silent audio track so every segment has the same
                    // stream layout before concatenation.
                    string args =
                        $"-y -i {Q(sceneVideoFile)} " +
                        "-f lavfi -i anullsrc=r=44100:cl=stereo " +
                        "-map 0:v:0 -map 1:a:0 " +
                        "-c:v libx264 -preset veryfast -crf 21 -pix_fmt yuv420p " +
                        "-c:a aac -b:a 192k -shortest " +
                        Q(normalizedSegment);

                    await RunFfmpegAsync(
                        ffmpegPath,
                        args,
                        workDirectory,
                        log);
                }

                segmentFiles.Add(normalizedSegment);
            }

            string concatList =
                Path.Combine(workDirectory, "concat.txt");

            await File.WriteAllLinesAsync(
                concatList,
                segmentFiles.Select(
                    f => $"file '{f.Replace("'", "'\\''")}'"));

            string concatenatedVideo =
                Path.Combine(workDirectory, "commercial-base.mp4");

            await RunFfmpegAsync(
                ffmpegPath,
                $"-y -f concat -safe 0 -i {Q(concatList)} " +
                "-c copy " +
                Q(concatenatedVideo),
                workDirectory,
                log);

            string finalFile =
                Path.Combine(workDirectory, "commercial-final.mp4");

            if (!string.IsNullOrWhiteSpace(request.MusicUrl))
            {
                string musicFile =
                    Path.Combine(workDirectory, "background.wav");

                await DownloadFileAsync(
                    downloadClient,
                    request.MusicUrl,
                    musicFile);

                double musicVolume =
                    Math.Clamp(request.MusicVolume, 0.0, 1.0);

                string args =
                    $"-y -i {Q(concatenatedVideo)} -stream_loop -1 -i {Q(musicFile)} " +
                    "-filter_complex " +
                    $"\"[0:a]volume=1.0[voice];" +
                    $"[1:a]volume={musicVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)}[music];" +
                    "[voice][music]amix=inputs=2:duration=first:dropout_transition=2[aout]\" " +
                    "-map 0:v:0 -map \"[aout]\" " +
                    "-c:v copy -c:a aac -b:a 192k -shortest " +
                    Q(finalFile);

                await RunFfmpegAsync(
                    ffmpegPath,
                    args,
                    workDirectory,
                    log);
            }
            else
            {
                File.Copy(
                    concatenatedVideo,
                    finalFile,
                    overwrite: true);
            }

            // Optional, fail-safe enhancement over the completed commercial only.
            // Scene generation, narration, concatenation and music remain untouched.
            if (!string.IsNullOrWhiteSpace(request.ProjectId) && !string.IsNullOrWhiteSpace(request.GenerationJobId))
            {
                finalFile = await CommercialOverlayEnhancement.BestEffortAsync(finalFile, async () =>
                {
                    var claimedUser = jwtUserResolver.ResolveUserId(req);
                    var overlayUser = string.IsNullOrWhiteSpace(claimedUser)
                        ? await ResolveLegacyJwtUserIdAsync(req)
                        : await attributionStore.ResolveClaimedUserIdAsync(claimedUser);
                    return await new CommercialOverlayEnhancement(attributionStore, aiUsageRecorder, log)
                        .EnhanceAsync(finalFile, segmentFiles, request.SceneDescriptions ?? new List<string>(),
                            ffmpegPath, workDirectory, storageConnectionString, containerName,
                            overlayUser ?? string.Empty, request.ProjectId, request.GenerationJobId);
                }, log);
            }

            var blobService =
                new BlobServiceClient(storageConnectionString);

            var container =
                blobService.GetBlobContainerClient(containerName);

            await container.CreateIfNotExistsAsync();

            string safeVideoId =
                string.IsNullOrWhiteSpace(request.VideoId)
                    ? Guid.NewGuid().ToString("N")
                    : string.Concat(
                        request.VideoId.Where(
                            c => char.IsLetterOrDigit(c) ||
                                 c == '-' ||
                                 c == '_'));

            string requestedName =
                Path.GetFileName(request.OutputFileName ?? "");

            if (string.IsNullOrWhiteSpace(requestedName))
            {
                requestedName =
                    $"{safeVideoId}-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4";
            }

            if (!requestedName.EndsWith(
                    ".mp4",
                    StringComparison.OrdinalIgnoreCase))
            {
                requestedName += ".mp4";
            }

            string blobName =
                $"{safeVideoId}/{Guid.NewGuid():N}-{requestedName}";

            var blob =
                container.GetBlobClient(blobName);

            await blob.UploadAsync(
                finalFile,
                overwrite: true);

            Uri videoUri;

            if (blob.CanGenerateSasUri)
            {
                videoUri = blob.GenerateSasUri(
                    BlobSasPermissions.Read,
                    DateTimeOffset.UtcNow.AddDays(7));
            }
            else
            {
                // If managed identity is used instead of a shared-key
                // connection string, make the container/CDN readable or
                // replace this with your own SAS-generation strategy.
                videoUri = blob.Uri;
            }

            return new OkObjectResult(new
            {
                success = true,
                videoUrl = videoUri.ToString(),
                blobName
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Commercial composition failed.");

            return new ObjectResult(new
            {
                success = false,
                error = ex.Message
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception cleanupEx)
            {
                log.LogWarning(
                    cleanupEx,
                    "Could not delete temporary composition directory {Directory}.",
                    workDirectory);
            }
        }
    }



    [Function("UpdateFinalVideoUrlByVideoId")]
    public async Task<IActionResult> UpdateFinalVideoUrlByVideoId(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "post",
            Route = null)]
        HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("UpdateFinalVideoUrlByVideoId");

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

        JObject data;

        try
        {
            using var reader = new StreamReader(req.Body);
            string body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body))
                return new BadRequestObjectResult("Request body cannot be empty.");

            data = JObject.Parse(body);
        }
        catch (JsonReaderException ex)
        {
            log.LogWarning(ex, "Invalid final-video update request.");
            return new BadRequestObjectResult("Request body must contain valid JSON.");
        }

        string videoId = data.Value<string>("videoId")?.Trim();
        string videoUrl = data.Value<string>("videoUrl")?.Trim();

        if (string.IsNullOrWhiteSpace(videoId))
            return new BadRequestObjectResult("Missing 'videoId'.");

        if (string.IsNullOrWhiteSpace(videoUrl))
            return new BadRequestObjectResult("Missing 'videoUrl'.");

        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var parsedVideoUri) ||
            (parsedVideoUri.Scheme != Uri.UriSchemeHttps &&
             parsedVideoUri.Scheme != Uri.UriSchemeHttp))
        {
            return new BadRequestObjectResult(
                "'videoUrl' must be an absolute HTTP or HTTPS URL.");
        }

        try
        {
            using AmazonDynamoDBClient client = CreateDynamoDbClient(log);

            // Do not persist the temporary SAS query string. The durable blob
            // URL is stored in DynamoDB; GetFinalVideoUrlByVideoId creates a
            // fresh SAS URL whenever the modal opens.
            string storedVideoUrl =
                parsedVideoUri.GetLeftPart(UriPartial.Path);

            // VideoImages uses Id for individual rows. We first locate every
            // scene row belonging to this VideoId, then update each row by Id.
            var findRowsRequest = new ExecuteStatementRequest
            {
                Statement =
                    "SELECT Id FROM VideoImages WHERE VideoId = ?",

                Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = videoId }
                }
            };

            var findRowsResult =
                await client.ExecuteStatementAsync(findRowsRequest);

            if (findRowsResult.Items == null ||
                findRowsResult.Items.Count == 0)
            {
                return new NotFoundObjectResult(new
                {
                    success = false,
                    error =
                        $"No VideoImages rows were found for VideoId '{videoId}'."
                });
            }

            int rowsUpdated = 0;

            foreach (var item in findRowsResult.Items)
            {
                if (!item.TryGetValue("Id", out var idValue) ||
                    string.IsNullOrWhiteSpace(idValue?.S))
                {
                    continue;
                }

                var updateRequest = new ExecuteStatementRequest
                {
                    Statement =
                        "UPDATE VideoImages SET VideoUrl = ? WHERE Id = ?",

                    Parameters = new List<AttributeValue>
                    {
                        new AttributeValue { S = storedVideoUrl },
                        new AttributeValue { S = idValue.S }
                    }
                };

                await client.ExecuteStatementAsync(updateRequest);
                rowsUpdated++;
            }

            if (rowsUpdated == 0)
            {
                return new ObjectResult(new
                {
                    success = false,
                    error =
                        "VideoImages rows were found, but none contained a valid Id."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            log.LogInformation(
                "Saved final video URL to {RowsUpdated} VideoImages rows for VideoId {VideoId}.",
                rowsUpdated,
                videoId);

            return new OkObjectResult(new
            {
                success = true,
                videoId,
                videoUrl = storedVideoUrl,
                rowsUpdated
            });
        }
        catch (AmazonDynamoDBException ex)
        {
            log.LogError(
                ex,
                "DynamoDB error saving final video URL for VideoId {VideoId}.",
                videoId);

            return new ObjectResult(new
            {
                success = false,
                error = ex.Message
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Unexpected error saving final video URL for VideoId {VideoId}.",
                videoId);

            return new ObjectResult(new
            {
                success = false,
                error = ex.Message
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("GetFinalVideoUrlByVideoId")]
    public async Task<IActionResult> GetFinalVideoUrlByVideoId(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "get",
            Route = null)]
        HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("GetFinalVideoUrlByVideoId");

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

        string videoId =
            req.Query["videoId"].FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(videoId))
            return new BadRequestObjectResult("Missing 'videoId'.");

        try
        {
            using AmazonDynamoDBClient client = CreateDynamoDbClient(log);

            var selectRequest = new ExecuteStatementRequest
            {
                Statement =
                    "SELECT VideoUrl FROM VideoImages WHERE VideoId = ?",

                Parameters = new List<AttributeValue>
                {
                    new AttributeValue { S = videoId }
                }
            };

            var result =
                await client.ExecuteStatementAsync(selectRequest);

            string storedVideoUrl = string.Empty;

            if (result.Items != null && result.Items.Count > 0)
            {
                var latestWithVideo = result.Items
                    .Where(item =>
                        item.TryGetValue("VideoUrl", out var videoValue) &&
                        !string.IsNullOrWhiteSpace(videoValue?.S))
                    .FirstOrDefault();

                if (latestWithVideo != null &&
                    latestWithVideo.TryGetValue(
                        "VideoUrl",
                        out var storedValue))
                {
                    storedVideoUrl = storedValue?.S ?? string.Empty;
                }
            }

            string playableVideoUrl =
                string.IsNullOrWhiteSpace(storedVideoUrl)
                    ? string.Empty
                    : CreatePlayableFinalVideoUrl(
                        storedVideoUrl,
                        log);

            return new OkObjectResult(new
            {
                success = true,
                videoId,
                videoUrl = playableVideoUrl
            });
        }
        catch (AmazonDynamoDBException ex)
        {
            log.LogError(
                ex,
                "DynamoDB error loading final video URL for VideoId {VideoId}.",
                videoId);

            return new ObjectResult(new
            {
                success = false,
                error = ex.Message
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Unexpected error loading final video URL for VideoId {VideoId}.",
                videoId);

            return new ObjectResult(new
            {
                success = false,
                error = ex.Message
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private static AmazonDynamoDBClient CreateDynamoDbClient(
        ILogger log)
    {
        return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
    }

    private async Task<string?> ResolveLegacyJwtUserIdAsync(HttpRequest request)
    {
        var username = jwtUserResolver.ResolveUsername(request);
        return string.IsNullOrWhiteSpace(username)
            ? null
            : await attributionStore.ResolveUserIdAsync(username);
    }

    private static string CreatePlayableFinalVideoUrl(
        string storedVideoUrl,
        ILogger log)
    {
        if (string.IsNullOrWhiteSpace(storedVideoUrl))
            return string.Empty;

        try
        {
            string storageConnectionString =
                Environment.GetEnvironmentVariable("AzureWebJobsStorage");

            if (string.IsNullOrWhiteSpace(storageConnectionString))
                return storedVideoUrl;

            string containerName =
                Environment.GetEnvironmentVariable("FINAL_VIDEO_CONTAINER")
                ?? "final-videos";

            var uri = new Uri(storedVideoUrl, UriKind.Absolute);

            var pathSegments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            var containerIndex = Array.FindIndex(
                pathSegments,
                segment => string.Equals(
                    segment,
                    containerName.Trim('/'),
                    StringComparison.OrdinalIgnoreCase));

            if (containerIndex < 0 || containerIndex == pathSegments.Length - 1)
            {
                // Supports public URLs/CDN URLs as well.
                return storedVideoUrl;
            }

            // Azure uses /container/blob while Azurite uses
            // /account/container/blob. Everything after the container segment
            // is the canonical blob name in either form.
            string blobName = string.Join('/', pathSegments[(containerIndex + 1)..]);

            var blobService =
                new BlobServiceClient(storageConnectionString);

            var container =
                blobService.GetBlobContainerClient(containerName);

            var blob =
                container.GetBlobClient(blobName);

            if (blob.CanGenerateSasUri)
            {
                return blob.GenerateSasUri(
                    BlobSasPermissions.Read,
                    DateTimeOffset.UtcNow.AddDays(7))
                    .ToString();
            }

            return blob.Uri.ToString();
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Could not create a fresh SAS for stored final video URL {VideoUrl}.",
                storedVideoUrl);

            return storedVideoUrl;
        }
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

    private static async Task DownloadFileAsync(
        HttpClient client,
        string url,
        string destination)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("A required media URL is empty.");

        using var response =
            await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        await using var input =
            await response.Content.ReadAsStreamAsync();

        await using var output =
            File.Create(destination);

        await input.CopyToAsync(output);
    }

    private static string Q(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string ResolveFfmpegPath(ILogger log)
    {
        // 1. Explicit environment variable, if you ever set one.
        string configuredPath =
            Environment.GetEnvironmentVariable("FFMPEG_PATH");

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = configuredPath.Trim().Trim('"');

            if (File.Exists(configuredPath))
            {
                log.LogInformation(
                    "Using FFmpeg from FFMPEG_PATH: {Path}",
                    configuredPath);

                return configuredPath;
            }

            log.LogWarning(
                "FFMPEG_PATH is set but the file was not found: {Path}",
                configuredPath);
        }

        // 2. Bundled FFmpeg published with the Azure Function.
        string bundledPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "ffmpeg",
                "ffmpeg.exe");

        if (File.Exists(bundledPath))
        {
            log.LogInformation(
                "Using bundled FFmpeg: {Path}",
                bundledPath);

            return bundledPath;
        }

        // 3. Local development fallback.
        if (OperatingSystem.IsWindows())
        {
            string localAppData =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                string winGetPath =
                    Path.Combine(
                        localAppData,
                        "Microsoft",
                        "WinGet",
                        "Links",
                        "ffmpeg.exe");

                if (File.Exists(winGetPath))
                {
                    log.LogInformation(
                        "Using local WinGet FFmpeg: {Path}",
                        winGetPath);

                    return winGetPath;
                }
            }
        }

        throw new FileNotFoundException(
            $"FFmpeg could not be found. " +
            $"AppContext.BaseDirectory: {AppContext.BaseDirectory}");
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath,
        string arguments,
        string workingDirectory,
        ILogger log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process =
            new Process
            {
                StartInfo = startInfo
            };

        if (!process.Start())
            throw new InvalidOperationException("Unable to start FFmpeg.");

        Task<string> stdoutTask =
            process.StandardOutput.ReadToEndAsync();

        Task<string> stderrTask =
            process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            log.LogError(
                "FFmpeg failed with exit code {ExitCode}. Output: {Output}. Error: {Error}",
                process.ExitCode,
                stdout,
                stderr);

            throw new InvalidOperationException(
                $"FFmpeg failed with exit code {process.ExitCode}.");
        }
    }

    public class ComposeCommercialRequest
    {
        public string ProjectId { get; set; } = string.Empty;
        public string GenerationJobId { get; set; } = string.Empty;
        public List<string> SceneDescriptions { get; set; } = new();
        public string VideoId { get; set; } = string.Empty;
        public List<string> SceneVideoUrls { get; set; } = new();
        public List<string> NarrationAudioUrls { get; set; } = new();
        public string MusicUrl { get; set; } = string.Empty;
        public double MusicVolume { get; set; } = 0.16;
        public string OutputFileName { get; set; } = string.Empty;
    }
}

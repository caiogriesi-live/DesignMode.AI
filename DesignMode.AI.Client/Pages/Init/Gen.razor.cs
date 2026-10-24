using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using System;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Telerik.SvgIcons;
using static DesignMode.AI.Client.Pages.Init.PickMainConceptModal;


namespace DesignMode.AI.Client.Pages.Init
{
    public partial class Gen : ComponentBase
    {
        [Inject] private NavigationManager NavigationManager { get; set; }
        [Inject] private NavigationManager Navigation { get; set; }
        [Inject] private ApiConfigService ApiConfig { get; set; }
        [Inject] private SessionService _sessionService { get; set; }
        [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; }
        [Inject] private HttpClient Http { get; set; } // Assuming you already use this
        [Inject] private AttributionContextService AttributionContext { get; set; }
        [Inject] private Blazored.LocalStorage.ILocalStorageService LocalStorage { get; set; }
        protected string? GenerationLimitMessage { get; set; }

        private int _saveImageCallCount = 0;
        private const int MaxSaveImageCalls = 4;

        private List<string> Deliverables = new();
        private string MainDeliverable { get; set; }
        private List<ImageIdeaModel> allImagesGenerated = new List<ImageIdeaModel>();
        private List<ImageIdeaModel> allImagesIdeasGenerated = new List<ImageIdeaModel>();
        private List<ImageIdeaModel> websiteImages = new List<ImageIdeaModel>();

        private List<string> SecondaryDeliverables { get; set; } = new();
        private List<string> SavedImages { get; set; } = new();
        private string DisplayLocation { get; set; }
        private string CompanyDetails { get; set; }

        private string promptId { get; set; } = "";
        private string CompanyIdeaId { get; set; } = "";
        public DesignMode.AI.Client.Pages.Init.MakeVideoModal.ImageIdeaCountResult IdeasCount { get; set; }
        private bool IsLoading { get; set; } = true;
        // Generate prompt texts using session items
        private List<string> promptTexts { get; set; } = new List<string>();
        private string promptText { get; set; } = "";
        private List<ConceptImageWithIdeaDto> conceptImages = new List<ConceptImageWithIdeaDto>();
        private HashSet<string> SelectedImageUrls = new();
        private HashSet<string> SelectedImageIds = new();

        private string? username;
        //private List<ConceptImageWithIdeaDto> conceptImages = new();
        private string promptIdfromCID { get; set; } = "";
        private string PromptIdFromCompanyIdeaId { get; set; } = "";
        private bool isAuthorized = false;
        private int dynamicSeconds { get; set; }

        private System.Timers.Timer _timer;
        private DateTime _startTime;
        private string CounterDisplay = "0.00s";

        private void StartLoadingTimer()
        {
            _timer?.Stop();
            _timer?.Dispose();

            _startTime = DateTime.UtcNow;
            CounterDisplay = "0.00s";

            // 50ms keeps the timer visually smooth without forcing the entire
            // Blazor WASM component to re-render 100 times per second.
            _timer = new System.Timers.Timer(50);
            _timer.AutoReset = true;
            _timer.Elapsed += UpdateCounter;
            _timer.Start();
        }

        private void StopLoadingTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
            }

            UpdateCounterDisplay();
        }

        private void UpdateCounter(object sender, ElapsedEventArgs e)
        {
            UpdateCounterDisplay();

            // Timer callbacks do not run on the renderer context.
            _ = InvokeAsync(StateHasChanged);
        }

        private void UpdateCounterDisplay()
        {
            var elapsed = DateTime.UtcNow - _startTime;
            var totalHundredths = Math.Max(0L, (long)(elapsed.TotalMilliseconds / 10.0));
            var seconds = totalHundredths / 100;
            var hundredths = totalHundredths % 100;
            CounterDisplay = $"{seconds}.{hundredths:D2}s";
        }

        protected override async Task OnInitializedAsync()
        {
            if (!string.IsNullOrWhiteSpace(ProjectId))
            {
                if (await AttributionContext.ValidateAndSetActiveProjectAsync(ProjectId))
                    NavigationManager.NavigateTo(IsWebsiteFunnelWorkspace ? "/init/gen" + new Uri(NavigationManager.Uri).Query : "/init/gen", forceLoad: true, replace: true);
                else
                    NavigationManager.NavigateTo("/dashboard", replace: true);
                return;
            }
            var uri = NavigationManager.Uri;
            var queryString = new Uri(uri).Query;

            var parsedQuery = QueryHelpers.ParseQuery(queryString);
            var isSpeedMode = parsedQuery.TryGetValue("s", out var sValue) &&
                              string.Equals(sValue.ToString(), "1", StringComparison.OrdinalIgnoreCase);

            ModalService.OnModalClosed += HandleModalClosed;

            // Initialize all collections once.
            allImagesGenerated = new List<ImageIdeaModel>();
            groupedImages = new Dictionary<string, List<ImageIdeaModel>>();
            conceptImages = new List<ConceptImageWithIdeaDto>();
            videosList = new List<VideoItem>();

            dynamicSeconds = isSpeedMode
                ? Random.Shared.Next(1, 4)     // 1-3 seconds for saved DB content
                : Random.Shared.Next(62, 76); // Existing 62-75 second behavior

            // IMPORTANT:
            // s=1 is handled by the dedicated fast DB path in OnInitialized().
            // Do not run the normal preload here too, otherwise both lifecycle
            // methods hit the same APIs concurrently and make speed mode slower.
            if (!isSpeedMode)
            {
                var authState = await AuthStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                isAuthorized = user.Identity?.IsAuthenticated == true;

                // Existing invite-data behavior is intentionally unchanged when s=1 is NOT used.
                if (isAuthorized)
                {
                    username = user.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        string inviteId = GetQueryParameter(queryString, "inviteId", "");

                        if (!string.IsNullOrEmpty(inviteId))
                        {
                            InviteData = await GetInviteClientByEmailAndId(username, inviteId);
                        }
                        else
                        {
                            InviteData = await GetInviteClientByEmail(username);
                        }

                        if (InviteData == null)
                        {
                            Console.WriteLine("⚠️ InviteData not found.");
                            return;
                        }

                        var (imageId, imageUrl) = await GetImageUrlByImageId(InviteData.ImageId);

                        InviteDataUseSeed = InviteData.UseSameImageSeedForAll;
                        InviteDataImageId = imageId;
                        InviteDataImageUrl = imageUrl;
                    }
                }

                // Existing preload is preserved for the normal generation path.
                await LoadAndGroupAllImages();
            }

            string i = GetQueryParameter(queryString, "i", "");
            if (!string.IsNullOrWhiteSpace(i))
            {
                ShowInitialModal = true;
            }
            // Website checkout must finish loading its exact invite and reference before generation starts.
            if (IsWebsiteFunnelWorkspace) await InitializeGenerationAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await base.OnAfterRenderAsync(firstRender);
            if (firstRender) _ = Analytics.PaidWorkspaceAsync(ApiConfig.ApiBaseUrl);

            //if (firstRender)
            //{
            // Check for parameter after first render
            await CheckForInitialParameter();
            //}
        }

        public void Dispose()
        {
            ModalService.OnModalClosed -= HandleModalClosed;
            _timer?.Stop();
            _timer?.Dispose();
        }
        private List<string> groupedVideoImageUrls = new();
        protected async override void OnInitialized()
        {
            if (!IsWebsiteFunnelWorkspace) await InitializeGenerationAsync();
        }

        private async Task InitializeGenerationAsync()
        {
            // Project routes are validated server-side in OnInitializedAsync, then reloaded
            // through the established workspace path with the canonical context selected.
            if (!string.IsNullOrWhiteSpace(ProjectId)) return;
            StartLoadingTimer();

            //Console.WriteLine($"Initializing component. EditConceptModal state: {ShowEditConceptModal}");
            //ShowEditConceptModal = false;

            IsLoading = true;

            var uri = NavigationManager.Uri;
            var queryString = new Uri(uri).Query;

            //List<ConceptImageWithIdeaDto> conceptImageResults = await CallGetAllConceptImagesWithIdeas();

            string name = GetQueryParameter(queryString, "name", "Zest Lab");
            string industry = GetQueryParameter(queryString, "industry", "General");
            string q = GetQueryParameter(queryString, "prompt", "Design a vibrant, energizing one-page website for Zest Lab...");
            string t = GetQueryParameter(queryString, "t", null);
            string s = GetQueryParameter(queryString, "s", null);
            string cid = GetQueryParameter(queryString, "cid", null);

            var authState = await AuthStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            isAuthorized = user.Identity?.IsAuthenticated == true;

            // s=1: display already-saved database images as soon as the single
            // ImageIdeas read returns. No generation, version lookup, image-exists
            // checks, re-saving, or duplicate asset reads are allowed on this path.
            if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(cid))
            {
                try
                {
                    await LoadSavedImagesFastAsync(cid, user);

                    IsLoading = false;
                    StopLoadingTimer();
                    await InvokeAsync(StateHasChanged);

                    // Supplemental data is loaded only AFTER the image grid is visible.
                    // This keeps concept selection/video metadata available without
                    // holding the user behind the loading screen.
                    await LoadSavedSupplementalDataAsync(cid, user);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Fast saved-image load failed: {ex.Message}");
                    IsLoading = false;
                    StopLoadingTimer();
                    await InvokeAsync(StateHasChanged);
                }

                return;
            }

            // Normal path remains unchanged when s=1 is not passed.
            IdeasCount = await GetImageIdeaCountsAsync(cid);

            // ✅ Check if s and cid are not null and fetch image ideas
            if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(cid))
            {
                //Handle the full deliverable for this version
                if (isAuthorized)
                {
                    username = user.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        CompanyIdeaId = cid;

                        // Get the prompt ID for this user and company idea
                        string promptId = await FetchPromptId(username, CompanyIdeaId);

                        // Optionally: handle errors or empty response
                        if (promptId.StartsWith("Error") || string.IsNullOrWhiteSpace(promptId))
                        {
                            // Handle error (log or display message)
                            Console.WriteLine("Failed to fetch prompt ID.");
                        }
                        else
                        {
                            promptIdfromCID = promptId;
                            // Existing code...
                            //conceptImages = await CallGetAllConceptImagesWithIdeas(username, CompanyIdeaId, promptId);

                            await GetVideoImageUrls();

                            // Add this new line
                            await LoadConceptImages();

                            // Optionally log or do something with conceptImages
                            Console.WriteLine($"Loaded {conceptImages.Count} concept images for prompt ID {promptId}");
                        }

                        // Call the new Azure Function to get image ideas directly
                        var imageIdeasAll = await CallGetImageIdeasForCompanyIndustry(CompanyIdeaId, username);

                        if (imageIdeasAll == null || !imageIdeasAll.Any())
                        {
                            // Handle empty result
                            Console.WriteLine("No image ideas found for this company and industry.");
                        }
                        else
                        {
                            // Store into your existing conceptImages collection
                            var conceptImagesAll = imageIdeasAll;

                            // Optionally log or handle UI updates
                            Console.WriteLine($"Loaded {conceptImages.Count} image ideas for user {username}");
                        }
                    }
                }
                //promptIds = await CallGetPromptIdsFunction(cid);
                //if (promptIds != null)
                //{
                //    PromptIdFromCompanyIdeaId = promptIds;
                //}
                //IMPORTANT: check if order is correct if not fix it
                var imageIdeas = await CallGetImageIdeasFunction(cid);
                allImagesIdeasGenerated = imageIdeas;
                if (imageIdeas != null && imageIdeas.Count > 0)
                {
                    // Convert ImagePrompt properties into a List<string>
                    var imagePrompts = imageIdeas
                        .Where(i => !string.IsNullOrWhiteSpace(i.ImagePrompt))
                        .Select(i => i.ImagePrompt)
                        .ToList();

                    // Assign to your property that expects a list of strings
                    promptText = null;
                    promptTexts = imagePrompts;

                    // Still keep the full models if you need them
                    allImagesGenerated = imageIdeas
                        .Where(i => i != null && !string.IsNullOrWhiteSpace(i.ImageUrl))
                        .ToList();
                }

                // NEW CALL → Get Video Images by PromptId
                await GetVideoImageUrls();
                await LoadAllAssets();
                await LoadConceptImages();
                StateHasChanged();

                // ✅ Sort by TimeStamp ascending
                var sortedIdeas = imageIdeas
                    //.Where(idea => !string.IsNullOrWhiteSpace(idea.ImageUrl))
                    .OrderBy(idea => Int32.Parse(idea.CountId))
                    .ToList();

                // ✅ Set MainDeliverable based on subcategory == "Generic"
                var mainIdea = sortedIdeas.FirstOrDefault(idea =>
                    string.Equals(idea.SubCategory, "Generic", StringComparison.OrdinalIgnoreCase));

                if (mainIdea != null)
                {
                    MainDeliverable = mainIdea.DeliverableType;
                    string[] inviteDataArr = Array.Empty<string>();
                    // Fetch invite data
                    if (!string.IsNullOrWhiteSpace(InviteData?.ImagePath))
                    {
                        inviteDataArr = System.Text.Json.JsonSerializer.Deserialize<string[]>(InviteData.ImagePath)
                                        ?? Array.Empty<string>();
                    }
                    if (inviteDataArr.Length > 0)
                    {
                        string imageUrl = inviteDataArr[0];

                        // ✅ Check if image already exists in DB
                        bool imageExists = await CheckImageUrlExists(imageUrl);

                        if (!imageExists)
                        {
                            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

                            // ✅ Get the prompt ID for this user and company idea
                            string promptId = await FetchPromptId(username, CompanyIdeaId);

                            var imageId1 = await CallSaveImageIdeaFunction(
                                MainDeliverable?.Replace("-", ""),
                                "Generic",
                                "promptStyle1",
                                imageUrl,
                                promptId,
                                timestamp,
                                "1");

                            InviteDataImagePathImageId = imageId1;
                        }

                        // ✅ Always set image URL
                        InviteDataImagePathImageUrl = imageUrl;

                        StateHasChanged();
                    }
                }

                // ✅ Fill RunwareImageUrls with 4 images from MainDeliverable
                RunwareImageUrls.Clear();
                RunwareImageId.Clear();
                var mainDeliverableImages = sortedIdeas
                    .Where(idea => idea.DeliverableType == MainDeliverable)
                    .OrderBy(idea => Int32.Parse(idea.CountId))
                    .Take(4)
                    .Select(idea => idea.ImageUrl)
                    .ToList();

                var mainDeliverableImageIds = sortedIdeas
                    .Where(idea => idea.DeliverableType == MainDeliverable)
                    .OrderBy(idea => Int32.Parse(idea.CountId))
                    .Take(4)
                    .Select(idea => idea.Id)
                    .ToList();

                RunwareImageUrls.AddRange(mainDeliverableImages);
                RunwareImageId.AddRange(mainDeliverableImageIds);

                // ✅ Find all items with DeliverableType == "Secondary" and get distinct subcategories
                var secondaryIdeas = sortedIdeas
                    .Where(idea => string.Equals(idea.DeliverableType, "Secondary", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(idea => Int32.Parse(idea.CountId))
                    .ToList();

                if (secondaryIdeas.Any())
                {
                    // Get distinct subcategories for secondary deliverables
                    SecondaryDeliverables = secondaryIdeas
                        .Select(idea => idea.SubCategory)
                        .Where(sub => !string.IsNullOrWhiteSpace(sub))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    SecondaryRunwareImageUrls.Clear();
                    SecondaryRunwareImageIds.Clear();
                    // For each secondary subcategory, take up to 4 images and add them to the list
                    foreach (var subCategory in SecondaryDeliverables)
                    {
                        var imagesForSubCategory = secondaryIdeas
                            .Where(idea => string.Equals(idea.SubCategory, subCategory, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(idea => Int32.Parse(idea.CountId))
                            .Take(4)
                            .Select(idea => idea.ImageUrl)
                            .Where(url => !string.IsNullOrWhiteSpace(url))
                            .ToList();

                        SecondaryRunwareImageUrls.AddRange(imagesForSubCategory);

                        var imagesForSubCategoryIds = secondaryIdeas
                            .Where(idea => string.Equals(idea.SubCategory, subCategory, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(idea => Int32.Parse(idea.CountId))
                            .Take(4)
                            .Select(idea => idea.Id)
                            .Where(url => !string.IsNullOrWhiteSpace(url))
                            .ToList();

                        SecondaryRunwareImageIds.AddRange(imagesForSubCategoryIds);
                    }
                }
                SavedImages = sortedIdeas.Select(idea => idea.ImageUrl).ToList();
                // Split the CID using custom delimiter
                string[] parts2 = cid.Split(new string[] { "|~~|@@|^^|" }, StringSplitOptions.None);

                string companyName2 = string.Empty;

                if (parts2.Length >= 2)
                {
                    // Decode Base64 for company name and industry
                    try
                    {
                        companyName2 = Encoding.UTF8.GetString(Convert.FromBase64String(parts2[0]));
                    }
                    catch (FormatException ex)
                    {
                        Console.WriteLine($"Error decoding Base64 values: {ex.Message}");
                    }
                }
                var safeCompanyName2 = string.Concat(companyName2
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                    .Replace(" ", "-").Replace("&", "");

                var versionData2 = await GetNextWebsiteVersionAsync(safeCompanyName2);

                int savedCount2 = 0;
                int maxImages2 = 4;

                // Save main Runware images
                foreach (var url in RunwareImageUrls)
                {
                    if (_saveImageCallCount >= MaxSaveImageCalls)
                        break;

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await SaveImageAsync(url, companyName2, versionData2.NextVersion.ToString());
                        _saveImageCallCount++;
                        Console.WriteLine($"Saved main image: {url}");
                    }
                }

                // Save secondary Runware images
                foreach (var url in SecondaryRunwareImageUrls)
                {
                    if (_saveImageCallCount >= MaxSaveImageCalls)
                        break;

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await SaveImageAsync(url, companyName2, versionData2.NextVersion.ToString());
                        _saveImageCallCount++;
                        Console.WriteLine($"Saved main image: {url}");
                    }
                }
            }
            else
            {
                await LoadConceptImages();
                if (q != null && cid != null)
                {
                    string inviteId = GetQueryParameter(queryString, "inviteId", "");
                    promptId = await SavePromptToDatabaseAsync(q, cid, inviteId);
                }

                if (isAuthorized)
                {
                    username = user.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        CompanyIdeaId = cid;

                        // Get the prompt ID for this user and company idea
                        string promptId = await FetchPromptId(username, CompanyIdeaId);

                        // Optionally: handle errors or empty response
                        if (promptId.StartsWith("Error") || string.IsNullOrWhiteSpace(promptId))
                        {
                            // Handle error (log or display message)
                            Console.WriteLine("Failed to fetch prompt ID.");
                        }
                        else
                        {
                            promptIdfromCID = promptId;

                            // Existing code...
                            //conceptImages = await CallGetAllConceptImagesWithIdeas(username, CompanyIdeaId, promptId);

                            // Add this new line
                            await LoadConceptImages();

                            // Optionally log or do something with conceptImages
                            Console.WriteLine($"Loaded {conceptImages.Count} concept images for prompt ID {promptId}");
                        }
                    }
                }

                await using var initialOperation = await GenerationGuard.BeginAsync();
                int retryCount = 0;
                const int maxRetries = 5;

                while (Deliverables.Count <= 1 && retryCount < maxRetries)
                {
                    string rawResponse = await FetchMainDeliverableCategory(name, industry, q, ApiConfig.ApiBaseUrl);
                    Deliverables = ParseDeliverableList(rawResponse);
                    retryCount++;

                    if (Deliverables.Count <= 1)
                    {
                        Console.WriteLine("Only one deliverable received, retrying...");
                        await Task.Delay(1000);
                    }
                }

                List<string> sessionItems = new List<string>();
                bool inSession = false; // Assume it's not in session until proven otherwise

                // Extract main deliverable and secondary deliverables
                MainDeliverable = Deliverables.FirstOrDefault() ?? "Website UI/UX";

                string[] inviteDataArr = Array.Empty<string>();

                // Fetch invite data
                if (!string.IsNullOrWhiteSpace(InviteData?.ImagePath))
                {
                    try
                    {
                        inviteDataArr =
                            System.Text.Json.JsonSerializer.Deserialize<string[]>(InviteData.ImagePath)
                            ?? Array.Empty<string>();
                    }
                    catch
                    {
                        // Fallback if ImagePath is a single string instead of JSON array
                        inviteDataArr = new[] { InviteData.ImagePath };
                    }
                }

                if (inviteDataArr.Length > 0 &&
                    !string.IsNullOrWhiteSpace(inviteDataArr[0]))
                {
                    string imageUrl = inviteDataArr[0];

                    // ✅ Check if image already exists in DB
                    bool imageExists = await CheckImageUrlExists(imageUrl);

                    if (!imageExists)
                    {
                        string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

                        var imageId1 = await CallSaveImageIdeaFunction(
                            MainDeliverable?.Replace("-", ""),
                            "Generic",
                            "promptStyle1",
                            imageUrl,
                            promptId,
                            timestamp,
                            "1");

                        InviteDataImagePathImageId = imageId1;
                    }

                    // ✅ Always set image URL
                    InviteDataImagePathImageUrl = imageUrl;

                    StateHasChanged();
                }

                string companyDescription = "";

                // Retrieve session values
                for (int i = 0; i <= 10; i++)
                {
                    string sessionKey = $"Idea_{i}";
                    var item = await _sessionService.GetSession(sessionKey) as string;

                    if (item != null)
                    {
                        string trimmedItem = item.Trim();
                        q = q.TrimStart(':');

                        if (trimmedItem == q.Trim())
                        {
                            inSession = true;
                        }
                    }
                }

                // If the idea is not already in session, fetch and store the description
                //if (!inSession)
                //{
                // If already in session, check if existing description matches the current prompt
                var existingDescription = await _sessionService.GetSession("CompanyDescription") as string;

                if (existingDescription == null || existingDescription.Trim() != q.Trim())
                {
                    companyDescription = await FetchCompanyDescription(name, industry, q);
                    CompanyDetails = companyDescription;
                    Console.WriteLine(companyDescription);

                    await _sessionService.SetSession("CompanyDescription", companyDescription);
                }
                else
                {
                    // Use existing session value if no update needed
                    companyDescription = existingDescription;
                    CompanyDetails = companyDescription;
                }
                //}

                // Call CheckPackageLabelRelevance after description has been determined
                string packageLabelRelevance = await CheckPackageLabelRelevance(name, industry, q, companyDescription);
                Console.WriteLine($"Package label relevance: {packageLabelRelevance}");

                // Use regex to extract values
                string physical = Regex.Match(packageLabelRelevance, @"physical:(yes|no)", RegexOptions.IgnoreCase).Groups[1].Value;
                string package = Regex.Match(packageLabelRelevance, @"package:(yes|no)", RegexOptions.IgnoreCase).Groups[1].Value;
                string small = Regex.Match(packageLabelRelevance, @"small:(yes|no)", RegexOptions.IgnoreCase).Groups[1].Value;

                int j = 0;
                int attempts = 0;
                int maxAttempts = 9;
                var persistedIdeaCandidates = IdeasCount?.Ideas
                    .Select(idea => $"{idea.Title}:{idea.Description}".Trim(':', ' '))
                    .Where(idea => !string.IsNullOrWhiteSpace(idea))
                    .ToList() ?? new List<string>();
                var sessionIdeaCandidates = new List<string>();
                for (var index = 0; index < maxAttempts; index++)
                {
                    var sessionIdea = await _sessionService.GetSession($"Idea_{index}") as string;
                    if (!string.IsNullOrWhiteSpace(sessionIdea))
                        sessionIdeaCandidates.Add(sessionIdea);
                }
                var checkoutIdeasJson = await _sessionService.GetSession($"CheckoutIdeas_{cid}");
                var checkoutIdeas = string.IsNullOrWhiteSpace(checkoutIdeasJson) ? null
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(checkoutIdeasJson);
                var ideaCandidates = DesignMode.AI.Shared.Services.InitialGenerationOrchestrator
                    .SelectIdeaCandidates(persistedIdeaCandidates, sessionIdeaCandidates, checkoutIdeas);

                while (sessionItems.Count < 8 && attempts < maxAttempts)
                {
                    var item = ideaCandidates.ElementAtOrDefault(j);

                    if (item != null)
                    {
                        string trimmedItem = item.Trim();
                        q = q.TrimStart(':');
                        string rawResponse = await FetchMainDeliverableCategory(name, industry, trimmedItem, ApiConfig.ApiBaseUrl);
                        var curDeliverables = ParseDeliverableList(rawResponse);
                        var sessionDeliverable = curDeliverables.FirstOrDefault();

                        if (sessionDeliverable != null)
                        {
                            // Skip if it's a packaging deliverable and packaging is set to "no"
                            if (sessionDeliverable.ToLower().Contains("packaging") &&
                                physical.ToLower() == "no" &&
                                package.ToLower() == "no" &&
                                small.ToLower() == "no")
                            {
                                j++;
                                attempts++;
                                continue;
                            }

                            // Skip duplicates of main deliverable
                            if (sessionDeliverable.Equals(MainDeliverable, StringComparison.OrdinalIgnoreCase) ||
                                sessionDeliverable.ToLower().Contains(MainDeliverable.ToLower()) ||
                                MainDeliverable.ToLower().Contains(sessionDeliverable.ToLower()))
                            {
                                j++;
                                attempts++;
                                continue;
                            }

                            // Only add after passing all checks
                            sessionItems.Add(trimmedItem);
                        }
                    }

                    j++;
                    attempts++;
                }

                string packageModular = "";

                if (packageLabelRelevance.ToLower() == "yes")
                {
                    packageModular = await GetPackageModularFormula(name, industry, q, companyDescription);
                }

                // Shuffle the list randomly
                Random rng = new Random();
                sessionItems = sessionItems.OrderBy(x => rng.Next()).ToList();

                // sessionItems already excludes the main deliverable.
                var secondarySessionItems = DesignMode.AI.Shared.Services.InitialGenerationOrchestrator
                    .SelectSecondaryCandidates(sessionItems)
                    .ToList();

                SecondaryDeliverables = secondarySessionItems
                    .Select(item =>
                    {
                        int colonIndex = item.IndexOf(':');
                        int dashIndex = item.IndexOf('-');

                        // Choose the first occurring delimiter
                        int splitIndex = (colonIndex == -1 || (dashIndex != -1 && dashIndex < colonIndex)) ? dashIndex : colonIndex;

                        if (splitIndex == -1) return item.Trim(); // no colon or dash found

                        return item.Substring(0, splitIndex).Trim(); // get text before the delimiter
                    })
                    .ToList();

                string mainDeliverableDetail = await FetchDeliverableDetail(MainDeliverable);

                var match = Regex.Match(mainDeliverableDetail, @"Display:\s*(.*?)\s*(?=\w+:|$)", RegexOptions.IgnoreCase);
                DisplayLocation = match.Success ? match.Groups[1].Value.Trim() : "Not found";

                // Define the string variable
                var companyDescriptionSession = "";

                // Check if the "CompanyDescription" session value exists and is not empty
                var sessionValue = await _sessionService.GetSession("CompanyDescription") as string;

                if (!string.IsNullOrWhiteSpace(sessionValue))
                {
                    companyDescriptionSession = sessionValue;
                }

                if (companyDescriptionSession != "")
                {
                    companyDescription = companyDescriptionSession;
                }

                // Now build the promptText with the updated session value
                promptText = $"{DisplayLocation}|{q}|{name}|{industry}|{mainDeliverableDetail}|{MainDeliverable}|{companyDescription}|{packageModular}";

                var testpromt1 = promptText;

                // ✅ Fetch details for each secondary deliverable
                List<string> secondaryDeliverableDetails = new List<string>();
                List<string> secondaryDisplayLocation = new List<string>();
                foreach (var deliverable in SecondaryDeliverables)
                {
                    string detail = await FetchDeliverableDetail(deliverable);
                    var matchSecondary = Regex.Match(detail, @"Display:\s*(.*?)\s*(?=\w+:|$)", RegexOptions.IgnoreCase);
                    var secondaryLocation = matchSecondary.Success ? matchSecondary.Groups[1].Value.Trim() : "Not found";
                    secondaryDisplayLocation.Add(secondaryLocation);
                    secondaryDeliverableDetails.Add(detail);
                }

                // Build prompt texts aligned with secondary items
                for (int i = 0; i < secondarySessionItems.Count && i < secondaryDeliverableDetails.Count; i++)
                {
                    var q2 = secondarySessionItems[i];
                    var detail = secondaryDeliverableDetails[i];
                    var secondLocation = secondaryDisplayLocation[i];
                    var prompt = $"{secondLocation}|{q2}|{name}|{industry}|{detail}|{SecondaryDeliverables[i]}|{companyDescription}|{packageModular}";
                    promptTexts.Add(prompt);
                }

                var testPrompt = promptTexts;

                // Instead of generating dynamic images, load static ones from a random folder
                Random rng2 = new Random();
                int randomFolder = rng2.Next(1, 14); // Generates number between 1 and 13 inclusive

                RunwareImageUrls.Clear();
                var mainGenerationCompleted = false;
                if (string.IsNullOrEmpty(t))
                {
                    // Generate dynamic images
                    for (int i = 0; i < 1; i++)
                    {
                        string seed = GetQueryParameter(queryString, "seed", null);
                        mainGenerationCompleted = await DesignMode.AI.Shared.Services.InitialGenerationOrchestrator.GenerateMainAsync(
                            async () => !string.IsNullOrEmpty(seed) && InviteDataUseSeed
                                ? await CallRunwareImageFunctionWithReference(promptText, seed, 1344, 768, 1)
                                : await CallRunwareImageFunction(promptText, 1344, 768, 1, "gen_main_image"),
                            async imageUrl =>
                            {
                                string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

                            // Now CallSaveImageIdeaFunction returns string (imageId), not bool
                            string imageId = await CallSaveImageIdeaFunction(MainDeliverable?.Replace("-", ""), "Generic", promptText, imageUrl, promptId, timestamp, i.ToString());

                            if (!string.IsNullOrEmpty(imageId))
                            {
                                Console.WriteLine($"ImageIdea saved successfully with Id: {imageId}");

                                // Add to RunwareImageId list
                                RunwareImageId.Add(imageId);
                            }
                            else
                            {
                                Console.WriteLine("Failed to save ImageIdea.");
                            }

                            // Always add the imageUrl to RunwareImageUrls
                            RunwareImageUrls.Add(imageUrl);
                            },
                            Console.WriteLine);
                    }

                    var imageIdeasGenerate = await CallGetImageIdeasFunction(cid);

                    if (imageIdeasGenerate != null)
                    {
                        allImagesGenerated = imageIdeasGenerate.
                            Where(i => !string.IsNullOrWhiteSpace(i.ImageUrl))
                                .ToList();
                    }
                }
                else
                {
                    // Load static images from /images/static/
                    for (int i = 1; i <= 4; i++)
                    {
                        string imagePath = $"assets/img/{randomFolder}/image{i}.jpg";
                        RunwareImageUrls.Add(imagePath);
                    }
                }

                // Generate images using the prompt texts
                SecondaryRunwareImageUrls.Clear();
                var secondaryGenerationCompleted = 0;
                if (string.IsNullOrEmpty(t))
                {
                    var i = 4;
                    var secondaryGenerationCalls = promptTexts
                        .Take(4)
                        .Select((prompt, index) => (Func<Task<string>>)(async () =>
                        {
                            string seed = GetQueryParameter(queryString, "seed", null);
                            return !string.IsNullOrEmpty(seed) && InviteDataUseSeed
                                ? await CallRunwareImageFunctionWithReference(prompt, seed, 1344, 768, 1)
                                : await CallRunwareImageFunction(prompt, 1344, 768, 1, "gen_secondary_image");
                        }))
                        .ToList();

                    secondaryGenerationCompleted = await DesignMode.AI.Shared.Services.InitialGenerationOrchestrator.GenerateSecondaryAsync(
                        secondaryGenerationCalls,
                        async (index, imageUrl) =>
                        {
                            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                            string imageId = await CallSaveImageIdeaFunction("Secondary", SecondaryDeliverables[index], promptTexts[index], imageUrl, promptId, timestamp, i.ToString());
                            i++;

                            if (!string.IsNullOrEmpty(imageId))
                            {
                                Console.WriteLine($"ImageIdea saved successfully with Id: {imageId}");
                                SecondaryRunwareImageIds.Add(imageId);
                            }
                            else
                            {
                                Console.WriteLine("Failed to save ImageIdea.");
                            }

                            SecondaryRunwareImageUrls.Add(imageUrl);
                        },
                        Console.WriteLine);

                    // Persist every unrepresented deliverable choice even when it has
                    // no image. This also preserves a choice whose initial provider
                    // call failed, without duplicating successful rows.
                    var persistedAfterGeneration = await CallGetImageIdeasFunction(cid) ?? new List<ImageIdeaModel>();
                    var representedSubcategories = persistedAfterGeneration
                        .Where(x => !string.IsNullOrWhiteSpace(x.SubCategory))
                        .Select(x => x.SubCategory)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    for (var placeholderIndex = 0; placeholderIndex < promptTexts.Count && placeholderIndex < SecondaryDeliverables.Count; placeholderIndex++)
                    {
                        if (representedSubcategories.Contains(SecondaryDeliverables[placeholderIndex])) continue;
                        await CallSaveImageIdeaFunction(
                            "Secondary",
                            SecondaryDeliverables[placeholderIndex],
                            promptTexts[placeholderIndex],
                            string.Empty,
                            promptId,
                            DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"),
                            (placeholderIndex + 4).ToString());
                    }

                    var imageIdeasGenerate = await CallGetImageIdeasFunction(cid);

                    if (imageIdeasGenerate != null)
                    {
                        allImagesGenerated = imageIdeasGenerate.
                            Where(i => !string.IsNullOrWhiteSpace(i.ImageUrl))
                                .ToList();
                        // NEW CALL → Get Video Images by PromptId
                        await GetVideoImageUrls();
                        await LoadAllAssets();
                        await LoadConceptImages();
                        StateHasChanged();
                    }
                }
                else
                {
                    for (int i = 5; i <= 8; i++)
                    {
                        string imagePath = $"assets/img/{randomFolder}/image{i}.jpg";
                        SecondaryRunwareImageUrls.Add(imagePath);
                    }
                }
                Console.WriteLine($"[GEN] generation completed main={mainGenerationCompleted.ToString().ToLowerInvariant()} secondary={secondaryGenerationCompleted}/{Math.Min(promptTexts.Count, 4)}");
                await UsageRefresh.RequestRefreshAsync();
            }
            // Split the CID using custom delimiter
            string[] parts = cid.Split(new string[] { "|~~|@@|^^|" }, StringSplitOptions.None);

            string companyName = string.Empty;
            string companyIndustry = string.Empty;

            if (parts.Length >= 2)
            {
                // Decode Base64 for company name and industry
                try
                {
                    companyName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                    companyIndustry = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Error decoding Base64 values: {ex.Message}");
                }
            }
            var safeCompanyName = string.Concat(companyName
    .Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
    .Replace(" ", "-").Replace("&", "");

            var versionData = await GetNextWebsiteVersionAsync(safeCompanyName);
            int savedCount = 0;
            int maxImages = 4;

            // Save main Runware images
            foreach (var url in RunwareImageUrls)
            {
                if (_saveImageCallCount >= MaxSaveImageCalls)
                    break;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    await SaveImageAsync(url, companyName, versionData.NextVersion.ToString());
                    _saveImageCallCount++;
                    Console.WriteLine($"Saved main image: {url}");
                }
            }

            // Save secondary Runware images
            foreach (var url in SecondaryRunwareImageUrls)
            {
                if (_saveImageCallCount >= MaxSaveImageCalls)
                    break;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    await SaveImageAsync(url, companyName, versionData.NextVersion.ToString());
                    _saveImageCallCount++;
                    Console.WriteLine($"Saved main image: {url}");
                }
            }

            IsLoading = false;
            StopLoadingTimer();
            StateHasChanged(); // Refresh UI
        }
        private async Task LoadSavedImagesFastAsync(string cid, System.Security.Claims.ClaimsPrincipal user)
        {
            CompanyIdeaId = cid;
            username = user.Identity?.Name;

            // This is the only call required before the image grid can be shown.
            var imageIdeas = await CallGetImageIdeasFunction(cid) ?? new List<ImageIdeaModel>();

            allImagesIdeasGenerated = imageIdeas;
            allImagesGenerated = imageIdeas
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.ImageUrl))
                .ToList();

            promptText = null;
            promptTexts = imageIdeas
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.ImagePrompt))
                .Select(i => i.ImagePrompt)
                .ToList();

            groupedImages = allImagesGenerated
                .GroupBy(img => GetGroupKey(img.DeliverableType, img.SubCategory))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(img => TryGetCountId(img.CountId)).ToList()
                );

            var sortedIdeas = imageIdeas
                .Where(i => i != null)
                .OrderBy(i => TryGetCountId(i.CountId))
                .ToList();

            var mainIdea = sortedIdeas.FirstOrDefault(i =>
                string.Equals(i.SubCategory, "Generic", StringComparison.OrdinalIgnoreCase));

            MainDeliverable = mainIdea?.DeliverableType
                              ?? sortedIdeas.FirstOrDefault()?.DeliverableType
                              ?? "Website UI/UX";

            RunwareImageUrls.Clear();
            RunwareImageId.Clear();
            SecondaryRunwareImageUrls.Clear();
            SecondaryRunwareImageIds.Clear();
            SecondaryDeliverables.Clear();

            var mainDeliverableIdeas = sortedIdeas
                .Where(i => string.Equals(i.DeliverableType, MainDeliverable, StringComparison.OrdinalIgnoreCase))
                .Where(i => !string.IsNullOrWhiteSpace(i.ImageUrl))
                .OrderBy(i => TryGetCountId(i.CountId))
                .Take(4)
                .ToList();

            RunwareImageUrls.AddRange(mainDeliverableIdeas.Select(i => i.ImageUrl));
            RunwareImageId.AddRange(mainDeliverableIdeas
                .Select(i => i.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id)));

            var secondaryIdeas = sortedIdeas
                .Where(i => string.Equals(i.DeliverableType, "Secondary", StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => TryGetCountId(i.CountId))
                .ToList();

            SecondaryDeliverables = secondaryIdeas
                .Select(i => i.SubCategory)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var subCategory in SecondaryDeliverables)
            {
                var items = secondaryIdeas
                    .Where(i => string.Equals(i.SubCategory, subCategory, StringComparison.OrdinalIgnoreCase))
                    .Where(i => !string.IsNullOrWhiteSpace(i.ImageUrl))
                    .OrderBy(i => TryGetCountId(i.CountId))
                    .Take(4)
                    .ToList();

                SecondaryRunwareImageUrls.AddRange(items.Select(i => i.ImageUrl));
                SecondaryRunwareImageIds.AddRange(items
                    .Select(i => i.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id)));
            }

            SavedImages = sortedIdeas
                .Select(i => i.ImageUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToList();

            Console.WriteLine($"⚡ s=1 fast load: {allImagesGenerated.Count} saved images loaded.");
        }

        private async Task LoadSavedSupplementalDataAsync(
            string cid,
            System.Security.Claims.ClaimsPrincipal user)
        {
            try
            {
                // These values do not block the first image render.
                var countTask = GetImageIdeaCountsAsync(cid);

                if (user.Identity?.IsAuthenticated == true &&
                    !string.IsNullOrWhiteSpace(username))
                {
                    CompanyIdeaId = cid;
                    var fetchedPromptId = await FetchPromptId(username, CompanyIdeaId);

                    if (!string.IsNullOrWhiteSpace(fetchedPromptId) &&
                        !fetchedPromptId.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        promptIdfromCID = fetchedPromptId;

                        // These are independent reads. Run them together instead of serially.
                        await Task.WhenAll(
                            GetVideoImageUrls(),
                            LoadConceptImages()
                        );
                    }
                }

                IdeasCount = await countTask;
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                // Supplemental information should never put the fast image load
                // back into a loading/error state.
                Console.WriteLine($"⚠️ Supplemental saved-data load failed: {ex.Message}");
            }
        }

        private static int TryGetCountId(string countId)
        {
            return int.TryParse(countId, out var value) ? value : int.MaxValue;
        }

        private bool _isModalOpen = true;
        private async Task<string> CallRunwareImageFunctionWithReference(
string positivePrompt,
string referenceImage,
int width,
int height,
int numberResults)
        {
            // Check if modal is still open before starting
            if (!_isModalOpen)
            {
                Console.WriteLine("🛑 Image generation cancelled - modal is closed");
                return string.Empty;
            }

            try
            {
                using HttpClient client = new HttpClient();

                string functionUrl =
                    $"{ApiConfig.ApiBaseUrl}api/CallRunwareAPIWithReferenceImage?code=REDACTED_FOR_GROOMING";

                var projectContext = await AttributionContext.AuthorizeGenerationAsync(CompanyIdeaId);
                var token = await LocalStorage.GetItemAsync<string>("authToken");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var requestBody = new
                {
                    positivePrompt = positivePrompt,
                    referenceImage = referenceImage,
                    projectId = projectContext.ProjectId,
                    width = width,
                    height = height,
                    numberResults = numberResults
                };

                Console.WriteLine(
                    $"🎨 Generating image with reference image. Prompt: " +
                    $"{positivePrompt.Substring(0, Math.Min(100, positivePrompt.Length))}...");

                var response = await client.PostAsJsonAsync(functionUrl, requestBody);

                // Check again after API call
                if (!_isModalOpen)
                {
                    Console.WriteLine("🛑 Image generation cancelled after API call - modal is closed");
                    return string.Empty;
                }

                if (response.IsSuccessStatusCode)
                {
                    var result =
                        await response.Content.ReadFromJsonAsync<DesignMode.AI.Shared.Models.RunwareResponse>();

                    // Check one more time before processing
                    if (!_isModalOpen)
                    {
                        Console.WriteLine("🛑 Image generation cancelled while processing results - modal is closed");
                        return string.Empty;
                    }

                    if (result != null &&
                        result.Data != null &&
                        result.Data.Any())
                    {
                        var imageUrl = result.Data
                            .Where(img => !string.IsNullOrWhiteSpace(img.ImageURL))
                            .Select(img => img.ImageURL)
                            .FirstOrDefault();

                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            Console.WriteLine(
                                $"✅ Image generated successfully: " +
                                $"{imageUrl.Substring(0, Math.Min(50, imageUrl.Length))}...");

                            return imageUrl;
                        }
                    }

                    Console.WriteLine("⚠️ No image data in response");
                    return string.Empty;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    Console.WriteLine(
                        $"❌ Runware API call failed: " +
                        $"{response.StatusCode}, Error: {errorContent}");

                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"❌ Exception in CallRunwareImageFunctionWithReference: {ex.Message}");

                return string.Empty;
            }
        }

        private async Task GetVideoImageUrls()
        {
            var videoImages = await GetVideoImagesByPromptIdAsync(promptIdfromCID);

            if (videoImages != null)
            {
                allVideoImages = videoImages;
                Console.WriteLine($"🎬 Loaded {videoImages.Count} video images.");

                // ✅ Take first image of each VideoId
                groupedVideoImages = allVideoImages
                    .GroupBy(v => v.VideoId)
                    .Select(g => g.OrderBy(v => v.TimeStamp).First()) // first image per video
                    .OrderBy(v => v.VideoId)                          // optional: order by VideoId
                    .ToList();

                Console.WriteLine($"🎬 Grouped list contains {groupedVideoImages.Count} unique videos.");

                // ✅ Get corresponding ImageUrls from allImagesGenerated by matching ImageId
                var usedUrls = new HashSet<string>();
                groupedVideoImageUrls = new List<string>();

                // ✅ Clear and recreate videosList with actual video items
                videosList.Clear();

                foreach (var video in groupedVideoImages)
                {
                    // Try to get the image URL
                    var url = allImagesGenerated.FirstOrDefault(img => img.Id == video.ImageId)?.ImageUrl ?? string.Empty;

                    // If URL is duplicate, try to pick a different image from same video
                    if (usedUrls.Contains(url))
                    {
                        var alternate = allVideoImages
                            .Where(v => v.VideoId == video.VideoId)
                            .Select(v => allImagesGenerated.FirstOrDefault(img => img.Id == v.ImageId)?.ImageUrl ?? string.Empty)
                            .FirstOrDefault(u => !usedUrls.Contains(u) && !string.IsNullOrEmpty(u));

                        if (!string.IsNullOrEmpty(alternate))
                            url = alternate;
                        // else keep original (duplicate) if no alternate found
                    }

                    usedUrls.Add(url);
                    groupedVideoImageUrls.Add(url);

                    // ✅ Create VideoItem for each video
                    var videoItem = new VideoItem
                    {
                        VideoId = video.VideoId,
                        ThumbnailUrl = url,
                        Title = $"Video {videosList.Count + 1}", // Or use actual title if available
                        Duration = "0:15" // Default duration or get from API if available
                    };

                    videosList.Add(videoItem);
                }

                Console.WriteLine($"🎬 Final list contains {groupedVideoImageUrls.Count} unique URLs per row.");
                Console.WriteLine($"🎬 videosList now contains {videosList.Count} items.");
            }
            else
            {
                allVideoImages = new List<VideoImageModel>();
                groupedVideoImages = new List<VideoImageModel>();
                groupedVideoImageUrls = new List<string>();
                videosList.Clear(); // Ensure it's empty
            }
        }
        private async Task OpenVideosEditAsync()
        {
            Console.WriteLine("Opening video edit modal");

            // ✅ Now run your original logic here
            await RunGenerateNewLogic();
        }

        private async Task RunGenerateNewLogic()
        {
            await using var generationOperation = await GenerationGuard.BeginAsync();
            var authState = await AuthStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;
            var uri = NavigationManager.Uri;
            var queryString = new Uri(uri).Query;
            string cid = GetQueryParameter(queryString, "cid", null);
            if (!string.IsNullOrWhiteSpace(cid))
            {
                if (isAuthorized)
                {
                    username = user.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        string promptIdCall = await FetchPromptId(username, cid);

                        // Split the CID using custom delimiter
                        string[] parts = cid.Split(new string[] { "|~~|@@|^^|" }, StringSplitOptions.None);

                        string companyName = string.Empty;
                        string companyIndustry = string.Empty;

                        if (parts.Length >= 2)
                        {
                            // Decode Base64 for company name and industry
                            try
                            {
                                companyName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                                companyIndustry = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                            }
                            catch (FormatException ex)
                            {
                                Console.WriteLine($"Error decoding Base64 values: {ex.Message}");
                            }
                        }

                        string promptTextCall = await FetchPromptText(promptIdCall);

                        // ✅ NEW CALL: Generate commercial description
                        string commercialDescription = await FetchCommercialDescription(companyName, companyIndustry, promptTextCall);

                        // Step 2: Use that text to generate detailed video prompts
                        string videoPrompts = await FetchVideoPrompts(commercialDescription);

                        // Check if there is numbering like "Prompt 1:" or "1."
                        bool hasNumbering = Regex.IsMatch(videoPrompts, @"(?:Prompt\s*)?\d+\s*[:.]");

                        string[] promptArray;

                        if (hasNumbering)
                        {
                            // Split based on numbering pattern
                            var pattern = @"(?:Prompt\s*)?\d+\s*[:.]";
                            var matches = Regex.Matches(videoPrompts, pattern);

                            var segments = new List<string>();
                            int lastIndex = 0;

                            foreach (Match match in matches)
                            {
                                if (match.Index != 0)
                                {
                                    var segment = videoPrompts.Substring(lastIndex, match.Index - lastIndex).Trim();
                                    if (!string.IsNullOrEmpty(segment))
                                    {
                                        int bracketIndex = segment.IndexOf('[');
                                        if (bracketIndex >= 0)
                                            segments.Add(segment.Substring(bracketIndex).Trim());
                                    }
                                }
                                lastIndex = match.Index;
                            }

                            // Add the last segment
                            var lastSegment = videoPrompts.Substring(lastIndex).Trim();
                            int lastBracketIndex = lastSegment.IndexOf('[');
                            if (lastBracketIndex >= 0)
                                segments.Add(lastSegment.Substring(lastBracketIndex).Trim());

                            promptArray = segments.ToArray();
                        }
                        else
                        {
                            // No numbering: split by every '[' and re-add '[' to each segment
                            var splitSegments = videoPrompts.Split('[')
                                                        .Where(s => !string.IsNullOrWhiteSpace(s))
                                                        .Select(s => "[" + s.Trim())
                                                        .ToArray();

                            promptArray = splitSegments;
                        }

                        // Test output
                        foreach (var prompt in promptArray)
                        {
                            Console.WriteLine(prompt);
                        }
                    }
                }
            }
        }

        private async Task LoadConceptImages()
        {
            if (isAuthorized && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(CompanyIdeaId) && !string.IsNullOrEmpty(promptIdfromCID))
            {
                try
                {
                    // Get all concept images from the database
                    var allImages = await CallGetAllConceptImagesWithIdeas(username, CompanyIdeaId, promptIdfromCID);

                    if (allImages != null && allImages.Any())
                    {
                        // COMPLETELY REPLACE the collection instead of modifying it
                        var newConceptImages = allImages
                            .OrderBy(img => img.SubCategory?.ToLower() == "generic" ? 0 : 1)
                            .ThenBy(img => int.TryParse(img.CountId, out var count) ? count : int.MaxValue)
                            .ToList();

                        // Clear and rebuild to avoid reference issues
                        conceptImages.Clear();
                        conceptImages.AddRange(newConceptImages);

                        // Update SelectedImageUrls
                        SelectedImageUrls = new HashSet<string>(conceptImages.Select(x => x.ImageUrl));
                        SelectedImageIds = new HashSet<string>(conceptImages.Select(x => x.ImageId));

                        StateHasChanged();
                    }
                    else
                    {
                        // If no images, clear everything
                        conceptImages.Clear();
                        SelectedImageUrls.Clear();
                        SelectedImageIds.Clear();
                        StateHasChanged();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading concept images: {ex.Message}");
                }
            }
        }

        private async Task LoadAllAssets()
        {
            if (isAuthorized && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(CompanyIdeaId) && !string.IsNullOrEmpty(promptIdfromCID))
            {
                // Get all concept images from the database
                var allImages = await CallGetAllConceptImagesWithIdeas(username, CompanyIdeaId, promptIdfromCID);

                if (allImages != null && allImages.Any())
                {
                    // Order the images with "generic" subcategory first, then by CountId
                    conceptImages = allImages
                        .OrderBy(img => img.SubCategory?.ToLower() == "generic" ? 0 : 1) // Generic first
                        .ThenBy(img => int.TryParse(img.CountId, out var count) ? count : int.MaxValue) // Then by CountId
                        .ToList();

                    // Update SelectedImageUrls with the images from conceptImages
                    SelectedImageUrls = new HashSet<string>(conceptImages.Select(x => x.ImageUrl));
                    StateHasChanged(); // Refresh UI to show updated cart count
                }
            }
        }

        private async Task<string> FetchCommercialDescription(string companyName, string industry, string promptText)
        {
            using HttpClient client = new HttpClient();

            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetCommercialDescription?code=REDACTED_FOR_GROOMING";

            var requestBody = new
            {
                Name = companyName,
                Industry = industry,
                Prompt = promptText
            };

            var jsonContent = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, jsonContent);

            if (response.IsSuccessStatusCode)
            {
                var commercialText = await response.Content.ReadAsStringAsync();
                return commercialText.Trim('"'); // Remove extra quotes if returned as a string
            }

            return "Error: Unable to fetch commercial description.";
        }

        private async Task<bool> CheckImageUrlExists(string imageUrl)
        {
            using HttpClient client = new HttpClient();

            string functionUrl =
                $"{ApiConfig.ApiBaseUrl}api/CheckImageUrlExists?code=REDACTED_FOR_GROOMING";

            var requestBody = new
            {
                ImageUrl = imageUrl
            };

            var jsonContent = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(functionUrl, jsonContent);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();

                var result = JsonConvert.DeserializeObject<dynamic>(responseContent);

                return result?.exists == true;
            }

            return false;
        }

        private async Task<string> DeleteBlankImageIdeas(string subcategory, string promptId)
        {
            using HttpClient client = new HttpClient();

            // Replace with your actual Azure Function endpoint + key
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/DeleteBlankImageIdeas?code=REDACTED_FOR_GROOMING";

            var requestBody = new
            {
                Subcategory = subcategory,
                PromptId = promptId
            };

            var jsonContent = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, jsonContent);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                return result;
            }

            return $"Error: Unable to delete ImageIdeas for Subcategory '{subcategory}' and PromptId '{promptId}'.";
        }

        private async Task<string> FetchVideoPrompts(string commercialText)
        {
            using HttpClient client = new HttpClient();

            // Azure Function URL for GenerateVideoPrompts
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GenerateVideoPrompts?code=REDACTED_FOR_GROOMING";

            var requestBody = new
            {
                Commercial = commercialText // Pass the text from the previous function
            };

            var jsonContent = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, jsonContent);

            if (response.IsSuccessStatusCode)
            {
                var videoPrompts = await response.Content.ReadAsStringAsync();
                return videoPrompts.Trim('"'); // Remove extra quotes if returned as a string
            }

            return "Error: Unable to fetch video prompts.";
        }


        private List<string> RunwareImageUrls { get; set; } = new();

        private List<string> SecondaryRunwareImageUrls { get; set; } = new();

        private List<string> RunwareImageId { get; set; } = new List<string>();
        private List<string> SecondaryRunwareImageIds { get; set; } = new List<string>();

        private string RunwareImageUrl { get; set; }
        private InviteClientData InviteData { get; set; } = new InviteClientData();
        private string InviteDataImageId { get; set; }
        private string InviteDataImageUrl { get; set; }
        private bool InviteDataUseSeed { get; set; }
        private string InviteDataImagePathImageId { get; set; }
        private string InviteDataImagePathImageUrl { get; set; }

        private DesignMode.AI.Shared.Models.RunwareResponse RunwareResult { get; set; }

        private async Task<List<ImageIdeaModel>> CallGetImageIdeasFunction(string companyIdeaId)
        {
            using HttpClient client = new HttpClient();
            var token = await LocalStorage.GetItemAsync<string>("authToken");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            Console.WriteLine($"[GEN IMAGE IDEAS] request companyIdPresent=false");
            Console.WriteLine($"[GEN IMAGE IDEAS] request companyIdeaIdPresent={!string.IsNullOrWhiteSpace(companyIdeaId)}");

            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetImageIdeasByCompanyId?code=REDACTED_FOR_GROOMING&companyIdeaId={Uri.EscapeDataString(companyIdeaId ?? string.Empty)}";

            var response = await client.GetAsync(functionUrl);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                throw new UnauthorizedAccessException("Project is unavailable.");
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"[GEN IMAGE IDEAS] response status={(int)response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                var imageIdeas = await response.Content.ReadFromJsonAsync<List<ImageIdeaModel>>();
                Console.WriteLine($"[GEN IMAGE IDEAS] records={imageIdeas?.Count ?? 0}");
                Console.WriteLine($"Fetched {imageIdeas?.Count ?? 0} image ideas.");
                return imageIdeas ?? new List<ImageIdeaModel>();
            }
            else
            {
                Console.WriteLine($"GetImageIdeas call failed: {response.StatusCode}");
                return new List<ImageIdeaModel>();
            }
        }

        private async Task<List<PromptImageIdeas>> CallGetImageIdeasForCompanyIndustry(string cid, string userId)
        {
            HttpClient client = new HttpClient();

            // Build the URL with query parameters
            string functionUrl =
                $"{ApiConfig.ApiBaseUrl}api/GetImageIdeasForCompanyIndustry" +
                $"?code=REDACTED_FOR_GROOMING" +
                $"&cid={Uri.EscapeDataString(cid)}" +
                $"&userId={Uri.EscapeDataString(userId)}";

            var response = await client.GetAsync(functionUrl);

            if (response.IsSuccessStatusCode)
            {
                var imageIdeas = await response.Content.ReadFromJsonAsync<List<PromptImageIdeas>>();

                if (imageIdeas != null && imageIdeas.Any())
                {
                    Console.WriteLine($"Fetched {imageIdeas.Sum(i => i.Images?.Count ?? 0)} image ideas across {imageIdeas.Count} prompts.");
                    return imageIdeas ?? new List<PromptImageIdeas>();
                }
                else
                {
                    Console.WriteLine("No image ideas returned.");
                    return new List<PromptImageIdeas>();
                }
            }
            else
            {
                Console.WriteLine($"GetImageIdeasForCompanyIndustry call failed: {response.StatusCode}");
                return new List<PromptImageIdeas>();
            }
        }

        private async Task<InviteClientData> GetInviteClientByEmail(string email)
        {
            try
            {
                var client = new HttpClient();

                var requestBody = new { email = email };
                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(
                    $"{ApiConfig.ApiBaseUrl}api/GetInviteClientByEmail?code=REDACTED_FOR_GROOMING",
                    content
                );

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response content: {responseContent}");

                    using var document = System.Text.Json.JsonDocument.Parse(responseContent);

                    var root = document.RootElement;

                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var firstItem = root;

                        // Helper function to extract values from DynamoDB objects
                        string GetStringValue(System.Text.Json.JsonElement element, string propertyName)
                        {
                            // Try exact match first
                            if (element.TryGetProperty(propertyName, out var property))
                            {
                                return ExtractValue(property);
                            }

                            // Try case-insensitive match
                            foreach (var prop in element.EnumerateObject())
                            {
                                if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return ExtractValue(prop.Value);
                                }
                            }

                            return null;
                        }

                        string ExtractValue(System.Text.Json.JsonElement property)
                        {
                            if (property.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                // DynamoDB String (lowercase)
                                if (property.TryGetProperty("s", out var stringValue))
                                {
                                    return stringValue.GetString();
                                }

                                // DynamoDB String (uppercase)
                                if (property.TryGetProperty("S", out var stringValueUpper))
                                {
                                    return stringValueUpper.GetString();
                                }

                                // DynamoDB BOOL
                                if (property.TryGetProperty("BOOL", out var boolValue))
                                {
                                    return boolValue.GetBoolean().ToString();
                                }

                                // lowercase bool just in case
                                if (property.TryGetProperty("bool", out var boolValueLower))
                                {
                                    return boolValueLower.GetBoolean().ToString();
                                }

                                // DynamoDB Number
                                if (property.TryGetProperty("N", out var numberValue))
                                {
                                    return numberValue.GetString();
                                }

                                // lowercase number
                                if (property.TryGetProperty("n", out var numberValueLower))
                                {
                                    return numberValueLower.GetString();
                                }
                            }
                            // Direct string
                            else if (property.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                return property.GetString();
                            }
                            // Direct bool
                            else if (property.ValueKind == System.Text.Json.JsonValueKind.True ||
                                     property.ValueKind == System.Text.Json.JsonValueKind.False)
                            {
                                return property.GetBoolean().ToString();
                            }
                            // Direct number
                            else if (property.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                return property.ToString();
                            }

                            return null;
                        }

                        var inviteData = new InviteClientData
                        {
                            Id = GetStringValue(firstItem, "Id"),
                            Name = GetStringValue(firstItem, "Name"),
                            Industry = GetStringValue(firstItem, "Industry"),
                            Prompt = GetStringValue(firstItem, "Prompt"),
                            Email = GetStringValue(firstItem, "Email"),
                            ImagePath = GetStringValue(firstItem, "ImagePath")
                                        ?? GetStringValue(firstItem, "Imagepath"),

                            UseSameImageSeedForAll = GetBoolValue(firstItem, "UseSameImageSeedForAll"),

                            ImageId = GetStringValue(firstItem, "ImageId"),
                            TimeStamp = GetStringValue(firstItem, "Timestamp")
                                        ?? GetStringValue(firstItem, "TimeStamp")
                        };

                        Console.WriteLine($"✅ Retrieved invite data for email: {email}");
                        Console.WriteLine($"Name: {inviteData.Name}, Email: {inviteData.Email}");
                        Console.WriteLine($"Industry: {inviteData.Industry}, Prompt: {inviteData.Prompt}");
                        Console.WriteLine($"UseSameImageSeedForAll: {inviteData.UseSameImageSeedForAll}");

                        return inviteData;
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"⚠️ No invite found for email: {email}");
                    return null;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Failed to get invite client: {response.StatusCode}, Error: {errorContent}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception getting invite client: {ex.Message}");
                return null;
            }

            return null;
        }
        private async Task<InviteClientData> GetInviteClientByEmailAndId(string email, string id)
        {
            try
            {
                var client = new HttpClient();

                var requestBody = new
                {
                    email = email,
                    id = id
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(
                    $"{ApiConfig.ApiBaseUrl}api/GetInviteClientByEmailAndId?code=REDACTED_FOR_GROOMING",
                    content
                );

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response content: {responseContent}");

                    using var document = System.Text.Json.JsonDocument.Parse(responseContent);
                    var root = document.RootElement;

                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var firstItem = root;

                        string GetStringValue(System.Text.Json.JsonElement element, string propertyName)
                        {
                            if (element.TryGetProperty(propertyName, out var property))
                            {
                                return ExtractValue(property);
                            }

                            foreach (var prop in element.EnumerateObject())
                            {
                                if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return ExtractValue(prop.Value);
                                }
                            }

                            return null;
                        }

                        string ExtractValue(System.Text.Json.JsonElement property)
                        {
                            if (property.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (property.TryGetProperty("s", out var stringValue))
                                    return stringValue.GetString();

                                if (property.TryGetProperty("S", out var stringValueUpper))
                                    return stringValueUpper.GetString();

                                if (property.TryGetProperty("BOOL", out var boolValue))
                                    return boolValue.GetBoolean().ToString();

                                if (property.TryGetProperty("bool", out var boolValueLower))
                                    return boolValueLower.GetBoolean().ToString();

                                if (property.TryGetProperty("N", out var numberValue))
                                    return numberValue.GetString();

                                if (property.TryGetProperty("n", out var numberValueLower))
                                    return numberValueLower.GetString();
                            }
                            else if (property.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                return property.GetString();
                            }
                            else if (property.ValueKind == System.Text.Json.JsonValueKind.True ||
                                     property.ValueKind == System.Text.Json.JsonValueKind.False)
                            {
                                return property.GetBoolean().ToString();
                            }
                            else if (property.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                return property.ToString();
                            }

                            return null;
                        }

                        bool GetBoolValue(System.Text.Json.JsonElement element, string propertyName)
                        {
                            if (element.TryGetProperty(propertyName, out var property))
                            {
                                // DynamoDB format: { "bool": true }
                                if (property.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (property.TryGetProperty("bool", out var boolProp) &&
                                        (boolProp.ValueKind == System.Text.Json.JsonValueKind.True ||
                                         boolProp.ValueKind == System.Text.Json.JsonValueKind.False))
                                    {
                                        return boolProp.GetBoolean();
                                    }

                                    if (property.TryGetProperty("BOOL", out var boolPropUpper) &&
                                        (boolPropUpper.ValueKind == System.Text.Json.JsonValueKind.True ||
                                         boolPropUpper.ValueKind == System.Text.Json.JsonValueKind.False))
                                    {
                                        return boolPropUpper.GetBoolean();
                                    }
                                }

                                // Direct bool
                                if (property.ValueKind == System.Text.Json.JsonValueKind.True ||
                                    property.ValueKind == System.Text.Json.JsonValueKind.False)
                                {
                                    return property.GetBoolean();
                                }
                            }

                            return false;
                        }

                        var inviteData = new InviteClientData
                        {
                            Id = GetStringValue(firstItem, "Id"),
                            Name = GetStringValue(firstItem, "Name"),
                            Industry = GetStringValue(firstItem, "Industry"),
                            Prompt = GetStringValue(firstItem, "Prompt"),
                            Email = GetStringValue(firstItem, "Email"),
                            ImagePath = GetStringValue(firstItem, "ImagePath")
                                        ?? GetStringValue(firstItem, "Imagepath"),

                            UseSameImageSeedForAll = GetBoolValue(firstItem, "UseSameImageSeedForAll"),

                            ImageId = GetStringValue(firstItem, "ImageId"),
                            TimeStamp = GetStringValue(firstItem, "Timestamp")
                                        ?? GetStringValue(firstItem, "TimeStamp")
                        };

                        Console.WriteLine($"✅ Retrieved invite data for email + id");
                        Console.WriteLine($"Id: {inviteData.Id}, Email: {inviteData.Email}");

                        return inviteData;
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"⚠️ No invite found for email + id: {email}, {id}");
                    return null;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Failed: {response.StatusCode}, Error: {errorContent}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception: {ex.Message}");
                return null;
            }

            return null;
        }
        bool GetBoolValue(System.Text.Json.JsonElement element, string propertyName)
        {
            // Exact match
            if (element.TryGetProperty(propertyName, out var property))
            {
                return ExtractBoolValue(property);
            }

            // Case-insensitive match
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return ExtractBoolValue(prop.Value);
                }
            }

            return false;
        }

        bool ExtractBoolValue(System.Text.Json.JsonElement property)
        {
            if (property.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // DynamoDB BOOL
                if (property.TryGetProperty("BOOL", out var boolValue))
                {
                    return boolValue.GetBoolean();
                }

                // lowercase bool
                if (property.TryGetProperty("bool", out var boolValueLower))
                {
                    return boolValueLower.GetBoolean();
                }
            }

            // Direct bool
            if (property.ValueKind == System.Text.Json.JsonValueKind.True ||
                property.ValueKind == System.Text.Json.JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            // String fallback
            if (property.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (bool.TryParse(property.GetString(), out bool result))
                {
                    return result;
                }
            }

            return false;
        }
        private async Task<(string ImageId, string ImageUrl)> GetImageUrlByImageId(string imageId)
        {
            try
            {
                var client = new HttpClient();

                var requestBody = new { imageId = imageId };
                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(
                    $"{ApiConfig.ApiBaseUrl}api/GetImageUrlByImageId?code=REDACTED_FOR_GROOMING",
                    content
                );

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response content: {responseContent}");

                    using var document = System.Text.Json.JsonDocument.Parse(responseContent);
                    var root = document.RootElement;

                    string GetStringValue(System.Text.Json.JsonElement element, string propertyName)
                    {
                        if (element.TryGetProperty(propertyName, out var property))
                        {
                            return ExtractSValue(property);
                        }

                        foreach (var prop in element.EnumerateObject())
                        {
                            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                            {
                                return ExtractSValue(prop.Value);
                            }
                        }

                        return null;
                    }

                    string ExtractSValue(System.Text.Json.JsonElement property)
                    {
                        if (property.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (property.TryGetProperty("s", out var stringValue))
                                return stringValue.GetString();

                            if (property.TryGetProperty("S", out var stringValueUpper))
                                return stringValueUpper.GetString();
                        }
                        else if (property.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            return property.GetString();
                        }

                        return null;
                    }

                    var returnedImageId = GetStringValue(root, "imageId");
                    var imageUrl = GetStringValue(root, "imageUrl");

                    Console.WriteLine($"\u2705 Retrieved image data for imageId: {imageId}");
                    Console.WriteLine($"ImageId: {returnedImageId}, ImageUrl: {imageUrl}");

                    return (returnedImageId, imageUrl);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"\u26a0\ufe0f No image found for imageId: {imageId}");
                    return (null, null);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"\u274c Failed: {response.StatusCode}, Error: {errorContent}");
                    return (null, null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\u274c Exception: {ex.Message}");
                return (null, null);
            }
        }

        private async Task<string> CallGetPromptIdsFunction(string companyIdeaId)
        {
            HttpClient client = new HttpClient();

            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetPromptIdsByCompanyIdeaId?code=REDACTED_FOR_GROOMING&companyIdeaId={companyIdeaId}";

            var response = await client.GetAsync(functionUrl);

            if (response.IsSuccessStatusCode)
            {
                var promptIds = await response.Content.ReadFromJsonAsync<string>();
                //Console.WriteLine($"Fetched {promptIds?.Count ?? 0} prompt IDs.");
                return promptIds;
            }
            else
            {
                Console.WriteLine($"GetPromptIds call failed: {response.StatusCode}");
                return "";
            }
        }

        public class PromptIdModel
        {
            public string Id { get; set; }
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
            public string SceneNumber { get; set; }
            public string CountId { get; set; }
        }

        private async Task<MakeVideoModal.ImageIdeaCountResult?> GetImageIdeaCountsAsync(string companyIdeaId)
        {
            try
            {
                HttpClient client = new HttpClient();
                var authState = await AuthStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity != null && user.Identity.IsAuthenticated)
                {
                    Console.WriteLine("[GEN IMAGE IDEAS] request companyIdPresent=false");
                    Console.WriteLine($"[GEN IMAGE IDEAS] request companyIdeaIdPresent={!string.IsNullOrWhiteSpace(companyIdeaId)}");
                    var url =
                        $"{ApiConfig.ApiBaseUrl}api/GetImageIdeaCountsByCompanyId?companyIdeaId={Uri.EscapeDataString(companyIdeaId ?? string.Empty)}" +
                        "&code=REDACTED_FOR_GROOMING";

                    var response = await client.GetAsync(url);
                    Console.WriteLine($"[GEN IMAGE IDEAS] response status={(int)response.StatusCode}");

                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<MakeVideoModal.ImageIdeaCountResult>();
                        if (result == null)
                            return null;

                        Console.WriteLine($"[GEN IMAGE IDEAS] records={result.Ideas.Count}");
                        Console.WriteLine("✅ Retrieved image idea counts + idea info + deliverable types.");
                        return result;
                    }
                    else
                    {
                        Console.WriteLine($"❌ Failed to retrieve image idea counts: {response.StatusCode}");
                        return null;
                    }
                }

                Console.WriteLine("⚠️ User not authenticated.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception retrieving image idea counts: {ex.Message}");
                return null;
            }
        }
        public class ImageIdeaCountResult
        {
            public List<IdeaItem> Ideas { get; set; } = new();
            public string PromptId { get; set; } = string.Empty;
            public string PromptText { get; set; } = string.Empty;
            public int VideoMatchCount { get; set; }
            public int VideoNoMatchCount { get; set; }
            public List<string> ExtractedTexts { get; set; } = new();
            public List<ImageIdeaItem> ImageIdeas { get; set; } = new();
            public List<bool> IdeaPromptMatches { get; set; } = new();
            public List<ImageIdeaItem> ImageIdeasFiltered { get; set; } = new();
            public List<bool> ImageIdeasMatches { get; set; } = new();
            public int FilteredPromptMatchCount { get; set; }
            public bool AnyPromptMatchesIdea { get; set; }
        }
        public class IdeaItem
        {
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }
        private async Task<string> CallSaveImageIdeaFunction(string deliverableType, string subCategory, string imagePrompt, string imageUrl, string promptId, string timeStamp, string countId)
        {
            HttpClient client = new HttpClient();

            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/SaveImageIdea?code=REDACTED_FOR_GROOMING";

            var requestBody = new
            {
                DeliverableType = deliverableType,
                SubCategory = subCategory,
                ImagePrompt = imagePrompt,
                ImageUrl = imageUrl,
                PromptId = promptId,
                TimeStamp = timeStamp,
                CountId = countId
            };

            var response = await client.PostAsJsonAsync(functionUrl, requestBody);

            if (await UsageRefresh.HandleResponseAsync(response))
            {
                GenerationLimitMessage = "You've used all your AI Credits for this billing period.";
                await InvokeAsync(StateHasChanged);
                return string.Empty;
            }

            if (response.IsSuccessStatusCode)
            {
                GenerationLimitMessage = null;
                var responseContent = await response.Content.ReadAsStringAsync();

                // Deserialize the response to extract the ID
                var responseObject = System.Text.Json.JsonSerializer.Deserialize<SaveImageIdeaResponse>(responseContent, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                Console.WriteLine("[IMAGE] persistence completed");

                return responseObject?.Id;  // Return the imageId (or null if somehow deserialization fails)
            }
            else
            {
                Console.WriteLine($"SaveImageIdea call failed: {response.StatusCode}");
                return null;  // null indicates failure
            }
        }

        public class SaveImageIdeaResponse
        {
            public string Message { get; set; }
            public string Id { get; set; }
        }

        private Task<string> CallRunwareImageFunction(string positivePrompt, int width, int height, int numberResults) =>
            CallRunwareImageFunction(positivePrompt, width, height, numberResults, "social_image");

        private async Task<string> CallPackageLabelImageFunction(string positivePrompt, int width, int height, int numberResults)
        {
            var projectContext = await AttributionContext.AuthorizeGenerationAsync(CompanyIdeaId);
            return await CallRunwareImageFunction(positivePrompt, width, height, numberResults, "package_label_image", projectContext);
        }

        private async Task<string> CallRunwareImageFunction(string positivePrompt, int width, int height, int numberResults, string feature, DesignMode.AI.Shared.Models.Attribution.ProjectContextResponse? projectContext = null,
            string? websiteImageIdeaId = null, string? companyIdeaId = null)
        {
            projectContext ??= await AttributionContext.AuthorizeGenerationAsync(CompanyIdeaId);
            HttpClient client = new HttpClient();
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/CallRunwareAPI?code=REDACTED_FOR_GROOMING";

            var generationJobId = Guid.NewGuid().ToString();
            var requestBody = new
            {
                positivePrompt = positivePrompt,
                width = width,
                height = height,
                numberResults = numberResults,
                generationJobId,
                projectId = projectContext.ProjectId,
                funnelId = projectContext.FunnelId,
                websiteImageIdeaId,
                companyIdeaId,
                feature
            };

            var token = await LocalStorage.GetItemAsync<string>("authToken");
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await client.PostAsJsonAsync(functionUrl, requestBody);
            if (await UsageRefresh.HandleResponseAsync(response)) return string.Empty;

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DesignMode.AI.Shared.Models.RunwareResponse>();

                if (result != null && result.Data != null)
                {
                    var firstUrl = feature == WebsiteImageState.FallbackImageFeature && projectContext is not null
                        ? WebsiteImageState.ResolveProviderImage(result, generationJobId)
                        : result.Data
                        .Where(img => !string.IsNullOrWhiteSpace(img.ImageURL))
                        .Select(img => img.ImageURL)
                        .FirstOrDefault();

                    RunwareImageUrl = firstUrl;
                    RunwareResult = result;

                    Console.WriteLine("[IMAGE] provider result received");

                    return RunwareImageUrl ?? string.Empty;
                }
            }
            else
            {
                Console.WriteLine($"Runware call failed: {response.StatusCode}");
            }

            return string.Empty;
        }

        private async Task<string> FetchDeliverableDetail(string deliverable)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetDeliverableDetail?code=REDACTED_FOR_GROOMING";

            var content = new StringContent($"\"{deliverable}\"", System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();

                // Normalize newlines and collapse to one line
                string singleLine = Regex.Replace(detail, @"\s*\r?\n\s*", " ").Trim();
                return singleLine;
            }

            return "Error: Unable to fetch deliverable detail.";
        }

        private async Task<string> FetchPromptId(string userId, string companyIdeaId)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetPromptIdForCompanyIdea?code=REDACTED_FOR_GROOMING&userId={Uri.EscapeDataString(userId)}&companyIdeaId={Uri.EscapeDataString(companyIdeaId)}";

            var response = await client.GetAsync(functionUrl);

            if (response.IsSuccessStatusCode)
            {
                var promptId = await response.Content.ReadAsStringAsync();
                return promptId.Trim('"'); // Remove extra quotes if returned as a string
            }

            return "Error: Unable to fetch prompt ID.";
        }

        private async Task<string> FetchPromptText(string promptId)
        {
            using HttpClient client = new HttpClient();

            // Construct the function URL
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetPromptById?code=REDACTED_FOR_GROOMING&promptId={Uri.EscapeDataString(promptId)}";

            var response = await client.GetAsync(functionUrl);

            if (response.IsSuccessStatusCode)
            {
                var promptText = await response.Content.ReadAsStringAsync();
                return promptText.Trim('"'); // Remove extra quotes if returned as JSON string
            }

            return "Error: Unable to fetch prompt text.";
        }

        private async Task<string> FetchCompanyDescription(string name, string industry, string prompt)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetCompanyDescription?code=REDACTED_FOR_GROOMING";

            var payload = new
            {
                Name = name,
                Industry = industry,
                Prompt = prompt
            };

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();

                // Normalize newlines and collapse to one line
                string singleLine = Regex.Replace(result, @"\s*\r?\n\s*", " ").Trim();
                return singleLine;
            }

            return "Error: Unable to fetch company description.";
        }

        private async Task<string> CheckPackageLabelRelevance(string name, string industry, string prompt, string companyDescription = "")
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/CheckPackageLabelRelevance?code=REDACTED_FOR_GROOMING";

            var payload = new
            {
                Name = name,
                Industry = industry,
                Prompt = prompt,
                CompanyDescription = companyDescription
            };

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, content);

            if (response.IsSuccessStatusCode)
            {
                // Just return the full response string
                return await response.Content.ReadAsStringAsync();
            }

            return "Error: Unable to check package label relevance.";
        }

        private async Task<string> GetPackageModularFormula(string name, string industry, string prompt, string companyDescription = "")
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetPackageModularFormula?code=REDACTED_FOR_GROOMING";

            var payload = new
            {
                Name = name,
                Industry = industry,
                Prompt = prompt,
                CompanyDescription = companyDescription
            };

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(functionUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();

                // Optional cleanup: remove leading/trailing quotes or whitespace
                string cleanedResult = result.Trim().Trim('"').Trim();

                return cleanedResult;
            }

            return "Error: Unable to retrieve modular packaging formula.";
        }

        private List<string> ParseDeliverableList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            // Step 1: Clean up common enclosing characters
            raw = raw.Trim('\'', '"', '.', ' ', '\n', '\r');

            // Step 2: Replace known delimiters with newlines to standardize
            raw = Regex.Replace(raw, @"\s*\d+\.\s*", "\n");           // Replace numbered list (e.g., 1. Item)
            raw = raw.Replace(",", "\n");                             // Replace commas
            raw = raw.Replace("•", "\n");                              // Replace bullets if any

            // Step 3: Split on newlines and trim each item
            var items = raw
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(i => i.Trim())
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .ToList();

            return items;
        }

        // Helper method to extract query parameter values
        private string GetQueryParameter(string queryString, string key, string defaultValue)
        {
            var parameters = System.Web.HttpUtility.ParseQueryString(queryString);
            return parameters[key] ?? defaultValue;
        }

        private async Task<string> FetchMainDeliverableCategory(string name, string industry, string prompt, string url)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{url}api/GetMainDeliverableCategory?code=REDACTED_FOR_GROOMING";

            var request = new DesignMode.AI.Shared.Models.OpenAIIdeaRequest
            {
                Name = name,
                Industry = industry,
                Prompt = prompt
            };

            var response = await client.PostAsJsonAsync(functionUrl, request);

            if (response.IsSuccessStatusCode)
            {
                var category = await response.Content.ReadAsStringAsync();
                return category.Trim('"'); // Remove quotes if returned as a JSON string
            }
            else
            {
                return "Error: Unable to fetch category";
            }
        }

        private async Task<List<ConceptImageWithIdeaDto>> CallGetAllConceptImagesWithIdeas(string userId, string companyIdeaId, string promptId)
        {
            HttpClient client = new HttpClient();

            // Build the full URL with all query parameters including promptId
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/GetAllConceptImagesWithIdeas" +
                                 $"?code=REDACTED_FOR_GROOMING" +
                                 $"&userId={Uri.EscapeDataString(userId)}" +
                                 $"&companyIdeaId={Uri.EscapeDataString(companyIdeaId)}" +
                                 $"&promptId={Uri.EscapeDataString(promptId)}";

            try
            {
                var response = await client.GetAsync(functionUrl);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<List<ConceptImageWithIdeaDto>>();
                    Console.WriteLine($"Fetched {result?.Count ?? 0} concept images with ideas.");

                    // Optional: Log some sample data to verify new fields
                    if (result?.Count > 0)
                    {
                        var sample = result.First();
                        Console.WriteLine($"Sample concept image - DeliverableType: {sample.DeliverableType}, SubCategory: {sample.SubCategory}");
                    }

                    return result ?? new List<ConceptImageWithIdeaDto>();
                }
                else
                {
                    Console.WriteLine($"API call failed: {response.StatusCode}");
                    return new List<ConceptImageWithIdeaDto>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception calling function: {ex.Message}");
                return new List<ConceptImageWithIdeaDto>();
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

            public override bool Equals(object obj)
            {
                return obj is ConceptImageWithIdeaDto other &&
                       ImageId == other.ImageId;
            }

            public override int GetHashCode()
            {
                return ImageId?.GetHashCode() ?? 0;
            }
        }

        private async Task<string?> GetPromptIdByCompanyIdeaIdAndPromptAsync(
    string prompt,
    string companyIdeaId)
        {
            try
            {
                var authState = await AuthStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity != null && user.Identity.IsAuthenticated)
                {
                    var requestBody = new
                    {
                        Prompt = prompt,
                        CompanyIdeaId = companyIdeaId
                    };

                    var response = await Http.PostAsJsonAsync(
                        $"{ApiConfig.ApiBaseUrl}api/GetPromptIdByCompanyIdeaIdAndPrompt?code=REDACTED_FOR_GROOMING",
                        requestBody);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Failed to get prompt id: {response.StatusCode}");
                        return null;
                    }

                    var jsonResponse =
                        await response.Content.ReadFromJsonAsync<GetPromptIdResponse>();

                    if (jsonResponse?.Found == true)
                    {
                        Console.WriteLine("✅ Prompt found.");
                        return jsonResponse.PromptId;
                    }

                    Console.WriteLine("⚠️ Prompt not found.");
                    return null;
                }
                else
                {
                    Console.WriteLine("⚠️ User not authenticated.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"❌ Exception in GetPromptIdByCompanyIdeaIdAndPromptAsync: {ex.Message}");

                return null;
            }
        }

        public class GetPromptIdResponse
        {
            public bool Found { get; set; }
            public string? PromptId { get; set; }
        }

        private async Task<string?> SavePromptToDatabaseAsync(string prompt, string companyIdeaId, string inviteId)
        {
            try
            {
                var authState = await AuthStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity != null && user.Identity.IsAuthenticated)
                {
                    string userId = user.Identity.Name;

                    var requestBody = new
                    {
                        Prompt = prompt,
                        CompanyIdeaId = companyIdeaId,
                        UserId = userId,
                        InviteId = inviteId,
                        TimeStamp = DateTime.UtcNow
                    };

                    var response = await Http.PostAsJsonAsync($"{ApiConfig.ApiBaseUrl}api/SavePrompt?code=REDACTED_FOR_GROOMING", requestBody);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Failed to save prompt: {response.StatusCode}");
                        return null;
                    }
                    else
                    {
                        // Read the response content as JSON
                        var jsonResponse = await response.Content.ReadFromJsonAsync<SavePromptResponse>();

                        Console.WriteLine("✅ Prompt saved successfully.");

                        // Return the PromptId from the response
                        return jsonResponse?.PromptId;
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ User not authenticated. Skipping prompt save.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception in SavePromptToDatabaseAsync: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> SaveConceptImageToDatabaseAsync(string imageId)
        {
            try
            {
                var authState = await AuthStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity != null && user.Identity.IsAuthenticated)
                {
                    string userId = user.Identity.Name;

                    var requestBody = new
                    {
                        ImageId = imageId
                    };

                    var response = await Http.PostAsJsonAsync($"{ApiConfig.ApiBaseUrl}api/SaveConceptImage?code=REDACTED_FOR_GROOMING", requestBody);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Failed to save concept image: {response.StatusCode}");
                        return null;
                    }
                    else
                    {
                        var jsonResponse = await response.Content.ReadFromJsonAsync<SaveConceptImageResponse>();

                        Console.WriteLine("✅ Concept image saved successfully.");

                        // Return the Guid from the response
                        return jsonResponse?.Guid;
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ User not authenticated. Skipping concept image save.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception in SaveConceptImageToDatabaseAsync: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> DeleteConceptImageFromDatabaseAsync(string imageId)
        {
            try
            {
                var authState = await AuthStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity != null && user.Identity.IsAuthenticated)
                {
                    string userId = user.Identity.Name;

                    var requestBody = new
                    {
                        ImageId = imageId
                    };

                    var response = await Http.PostAsJsonAsync($"{ApiConfig.ApiBaseUrl}api/DeleteConceptImage?code=REDACTED_FOR_GROOMING", requestBody);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"❌ Failed to delete concept image: {response.StatusCode}");
                        return false;
                    }
                    else
                    {
                        Console.WriteLine("✅ Concept image deleted successfully.");
                        return true;
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ User not authenticated. Skipping concept image delete.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception in DeleteConceptImageFromDatabaseAsync: {ex.Message}");
                return false;
            }
        }


        public class SaveConceptImageResponse
        {
            public string Guid { get; set; }
        }

        // Define a class to match the JSON response structure
        public class SavePromptResponse
        {
            public string Message { get; set; }
            public string PromptId { get; set; }
        }

        public class PromptImageIdeas
        {
            public string PromptId { get; set; }
            public List<ImageIdeaModel> Images { get; set; } = new List<ImageIdeaModel>();
        }
    }
}

// ModalService.cs
public class ModalService
{
    public event Action? OnModalClosed;

    public void CloseModal()
    {
        // Notify subscribers that a modal has closed
        OnModalClosed?.Invoke();
    }
}


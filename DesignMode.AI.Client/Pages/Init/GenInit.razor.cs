using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Newtonsoft.Json;
using System;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Telerik.SvgIcons;
using DesignMode.AI.Client.Pages.Init;


namespace DesignMode.AI.Client.Pages.Init
{
    public partial class GenInit : ComponentBase
    {
        [Inject] private NavigationManager NavigationManager { get; set; }
        [Inject] private NavigationManager Navigation { get; set; }
        [Inject] private ApiConfigService ApiConfig { get; set; }
        [Inject] private SessionService _sessionService { get; set; }
        [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; }
        [Inject] private HttpClient Http { get; set; } // Assuming you already use this

        private int _saveImageCallCount = 0;
        private const int MaxSaveImageCalls = 4;

        private List<string> Deliverables = new();
        private string MainDeliverable { get; set; }
        private List<ImageIdeaModelInit> allImagesGenerated = new List<ImageIdeaModelInit>();
        private List<ImageIdeaModelInit> allImagesIdeasGenerated = new List<ImageIdeaModelInit>();
        private List<ImageIdeaModelInit> websiteImages = new List<ImageIdeaModelInit>();

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

        private System.Timers.Timer _timer;
        private DateTime _startTime;
        private string CounterDisplay = "0.00s";

        private void UpdateCounter(object sender, ElapsedEventArgs e)
        {
            var elapsed = DateTime.Now - _startTime;
            var seconds = (int)elapsed.TotalSeconds;
            var milliseconds = elapsed.Milliseconds / 10; // 2 digits
            CounterDisplay = $"{seconds}.{milliseconds:D2}s";

            StateHasChanged();
        }

        protected override async Task OnInitializedAsync()
        {
            //ModalService.OnModalClosed += HandleModalClosed;

            // Initialize all collections
            allImagesGenerated = new List<ImageIdeaModelInit>();
            groupedImages = new Dictionary<string, List<ImageIdeaModelInit>>();
            conceptImages = new List<ConceptImageWithIdeaDto>();
            videosList = new List<VideoItem>();

            // Then load data
            await LoadAndGroupAllImages();
            await RefreshAllImages();

            //
            // ... other initialization code
            var uri = NavigationManager.Uri;
            var queryString = new Uri(uri).Query;

            // =========================
            // 🔹 Get invite id from querystring
            // Example:
            // ?id=123
            // =========================
            string inviteId = GetQueryParameter(queryString, "id", "");

            if (!string.IsNullOrWhiteSpace(inviteId))
            {
                // 🔹 Get strongly typed object
                InviteClientDataInit? inviteClientData =
                    await GetInviteClientByIdAsync(inviteId);

                if (inviteClientData != null)
                {
                    Console.WriteLine(
                        $"✅ Invite client loaded: {inviteClientData.Id}");

                    // Example usage
                    Console.WriteLine($"Name: {inviteClientData.Name}");
                    Console.WriteLine($"Industry: {inviteClientData.Industry}");
                    Console.WriteLine($"Prompt: {inviteClientData.Prompt}");
                    Console.WriteLine($"Email: {inviteClientData.Email}");

                    if (!string.IsNullOrWhiteSpace(inviteClientData.ImageId))
                    {
                        var (imageId, imageUrl) = await GetImageUrlByImageId(inviteClientData.ImageId);

                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            Console.WriteLine($"\u2705 Image loaded: {imageUrl}");

                            // Set the chosen direction image
                            ChosenDirectionImageUrl = new ChosenDirectionImage
                            {
                                ImageUrl = imageUrl,
                                ImageId = imageId
                            };

                            // Trigger the animation
                            ShowChosenDirectionAnimation = true;
                            StateHasChanged();

                            // Wait for animation to complete then reset flag
                            await Task.Delay(500);
                            ShowChosenDirectionAnimation = false;
                            StateHasChanged();

                            // Example: store it for UI binding
                            SelectedImageId = imageId;
                            SelectedImageUrl = imageUrl;
                        }
                        else
                        {
                            Console.WriteLine("\u26a0\ufe0f No image URL returned.");
                        }

                        StateHasChanged();
                    }

                    // Optional:
                    // Populate page variables here
                    // CompanyName = inviteClientData.Name;
                    // Industry = inviteClientData.Industry;
                    // Prompt = inviteClientData.Prompt;
                }
            }

            // Existing logic
            string i = GetQueryParameter(queryString, "i", "");

            if (!string.IsNullOrWhiteSpace(i))
            {
                ShowInitialModal = true;
            }
        }

        public class InviteClientDataInit
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
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await base.OnAfterRenderAsync(firstRender);

            //if (firstRender)
            //{            
            //   Check for parameter after first render
            //   await CheckForInitialParameter();
            //}
        }

        public void Dispose()
        {
            //ModalService.OnModalClosed -= HandleModalClosed;
            _timer?.Stop();
            _timer?.Dispose();
        }
        private List<string> groupedVideoImageUrls = new();
        protected async override void OnInitialized()
        {
            _startTime = DateTime.Now;

            _timer = new System.Timers.Timer(10); // update every 10ms for smooth milliseconds
            _timer.Elapsed += UpdateCounter;
            _timer.Start();

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

            IdeasCount = await GetImageIdeaCountsAsync(cid);

            var authState = await AuthStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            isAuthorized = user.Identity?.IsAuthenticated == true;

            // ✅ Check if s and cid are not null and fetch image ideas
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
            _timer.Stop();
            StateHasChanged(); // Refresh UI
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

        private async Task<InviteClientDataInit?> GetInviteClientByIdAsync(string id)
        {
            try
            {
                using HttpClient client = new HttpClient();

                string functionUrl =
                    $"{ApiConfig.ApiBaseUrl}api/GetInviteClientById?code=REDACTED_FOR_GROOMING";

                var requestBody = new
                {
                    Id = id
                };

                var jsonContent = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    Encoding.UTF8,
                    "application/json");

                var response = await client.PostAsync(functionUrl, jsonContent);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();

                    var inviteClientData = JsonConvert.DeserializeObject<InviteClientDataInit>(json);

                    return inviteClientData;
                }

                Console.WriteLine(
                    $"❌ Failed to fetch invite client. Status: {response.StatusCode}");

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"❌ Exception in GetInviteClientByIdAsync: {ex.Message}");

                return null;
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

        private PickMainConceptModal.RunwareResponse RunwareResult { get; set; }

        private async Task<List<ImageIdeaModelInit>> CallGetImageIdeasFunctionInit(string companyIdeaId)
        {
            //{ "Video & Photography", 1 },

            //{ "Web & UX/UI Website", 2 },

            //{ "Social Media Content", 3 },

            //{ "Logo Design", 4 },

            //{ "Digital Marketing", 5 },

            //{ "Events & Activations", 6 },

            //{ "Packaging Label", 7 },

            //{ "Print Materials", 8 },

            // ✅ Get raw JSON string
            string jsonResponse = "[{\"id\":\"9423d11d-c3f4-4521-afb4-6eefc32924b4\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Social Media Content\",\"imagePrompt\":\"Social media platforms.|Social Media Content: Mindful Moments: Inspirational quotes and calming imagery to promote mental well-being with Hush & Hold.:|Hush & Hold|Weighted Lap Pads for Focus|Social Media Content Description: Eye-catching graphics for social media platforms. Display: Social media platforms. Format: JPEG or PNG. Dimensions/Size: 1080x1080 pixels. Quantity: 10 graphics. Technical Requirements: Use brand colors and include logo in each graphic.|Social Media Content|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a09dlim3/ws/2/ii/3063773a-cee9-42c6-8008-5331c32bc10f.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115442Z\",\"countId\":\"4\"}," +
                "{\"id\":\"8287c02a-08e4-4065-abb4-93c40a3e2c7a\",\"deliverableType\":\"Video & Photography\",\"subCategory\":\"Generic\",\"imagePrompt\":\"Website, social media platforms|Video & Photography: Peaceful Product Shots: Showcase lap pads in tranquil settings to evoke a sense of calm for Hush & Hold.:|Hush & Hold|Weighted Lap Pads for Focus|Video & Photography Description: High-quality video clips and images showcasing the product features and benefits. Display: Website, social media platforms Format: MP4, JPEG Dimensions/Size: 1920x1080 pixels for videos, 1080x1080 pixels for images Quantity: 10 videos, 20 images Technical Requirements: Include product branding and messaging in visuals.|Video & Photography|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a04d20/ws/3/ii/f69fd1d2-a1d8-44b5-9341-bcb8db6ca99f.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115111Z\",\"countId\":\"1\"}," +
                "{\"id\":\"25332c76-1807-4191-bcdd-392cc61c3e43\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Logo Design\",\"imagePrompt\":\"Website, social media, and packaging.|Logo Design: Zen-like simplicity: Clean, minimalist logo reflecting calmness and focus for Hush & Hold company.:|Hush & Hold|Weighted Lap Pads for Focus|Logo Design Description: Create a unique and eye-catching logo for the brand. Display: Website, social media, and packaging. Format: Vector file (AI, EPS, SVG). Dimensions/Size: Scalable for various uses. Quantity: 1 logo design. Technical Requirements: Include color codes and font specifications.|Logo Design|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a02d21/ws/2/ii/b3891937-21e6-4a7c-9fd4-d449b06f599b.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260421T100851Z\",\"countId\":\"5\"}," +
                "{\"id\":\"4a249ff5-5ccf-4f22-9167-3ff07835b069\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Events & Activations\",\"imagePrompt\":\"Events and activations|Events & Activations: Focus Workshops: Host workshops on concentration and mindfulness techniques with Hush & Hold products.:|Hush & Hold|Weighted Lap Pads for Focus|Events & Activations Description: Design concepts for interactive experiences at branding events. Display: Events and activations Format: PDF Dimensions/Size: A4 Quantity: 3 designs Technical Requirements: Include detailed mockups with branding elements integrated.|Events & Activations|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a05d22/ws/2/ii/5c4d7b66-b43e-436c-9874-d53bb8b533ac.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260421T100818Z\",\"countId\":\"6\"}," +
                "{\"id\":\"02f10d04-dcad-4a3e-a903-f52a904efd51\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Digital Marketing\",\"imagePrompt\":\"Social media platforms|Digital Marketing: Relaxation Campaign: Highlight benefits of weighted lap pads for focus and stress relief with Hush & Hold.:|Hush & Hold|Weighted Lap Pads for Focus|Deliverable Name: Digital Marketing Description: Custom-designed social media graphics for promotional campaigns. Display: Social media platforms Format: PNG Dimensions/Size: 1080x1080 pixels Quantity: 10 graphics Technical Requirements: Include brand colors and logos in each graphic.|Digital Marketing|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a05d22/ws/2/ii/6ee6d205-6626-40ec-a66a-15ea34fcdba2.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115122Z\",\"countId\":\"4\"}," +
                "{\"id\":\"6bdae5f5-ed43-404a-9772-ded268aec530\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Print Materials\",\"imagePrompt\":\"Print|A child with ADHD sits at a school desk, squirming, unable to focus; their teacher places a soft, weighted lap pad over their legs—the child stills, exhales, and begins to write, the world suddenly quiet enough to think.|Hush & Hold|Weighted Lap Pads for Focus|Print Materials Description: Brochures highlighting key features and benefits of the product. Display: Print Format: PDF Dimensions/Size: A4 Quantity: 500 copies Technical Requirements: High resolution images and clear text for printing.|- Print Materials|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a01d21/ws/3/ii/0b4ed380-5f37-4aeb-aee9-2ddc95c458ba.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115101Z\",\"countId\":\"9\"}," +
                "{\"id\":\"85f180a8-4cd1-4f2a-8ab5-526e44a9771e\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Packaging Label\",\"imagePrompt\":\"Packaging|Packaging Label: Tranquil Touch: Soft, soothing colors and tactile elements for packaging of Hush & Hold weighted lap pads.:|Hush & Hold|Weighted Lap Pads for Focus|Packaging Label Description: Detailed design with product information and branding elements. Display: Packaging Format: PDF Dimensions/Size: 4x6 inches Quantity: 500 labels Technical Requirements: CMYK color mode, high-resolution images, include barcode and legal requirements.|Packaging Label|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a03d21/ws/2/ii/d0d0ba86-f642-4729-8503-b7cc6b23d422.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115107Z\",\"countId\":\"8\"}," +
                "{\"id\":\"85f180a8-4cd1-4f2a-8ab5-526e44a9771e\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Web & UX/UI Website\",\"imagePrompt\":\"Packaging|Packaging Label: Tranquil Touch: Soft, soothing colors and tactile elements for packaging of Hush & Hold weighted lap pads.:|Hush & Hold|Weighted Lap Pads for Focus|Packaging Label Description: Detailed design with product information and branding elements. Display: Packaging Format: PDF Dimensions/Size: 4x6 inches Quantity: 500 labels Technical Requirements: CMYK color mode, high-resolution images, include barcode and legal requirements.|Packaging Label|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a05d22/ws/3/ii/4e94dad3-b7e7-4055-811e-5ea4a6404916.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115107Z\",\"countId\":\"3\"}," +
                "{\"id\":\"85f180a8-4cd1-4f2a-8ab5-526e44a9771e\",\"deliverableType\":\"Secondary\",\"subCategory\":\"Video & Photography\",\"imagePrompt\":\"Packaging|Packaging Label: Tranquil Touch: Soft, soothing colors and tactile elements for packaging of Hush & Hold weighted lap pads.:|Hush & Hold|Weighted Lap Pads for Focus|Packaging Label Description: Detailed design with product information and branding elements. Display: Packaging Format: PDF Dimensions/Size: 4x6 inches Quantity: 500 labels Technical Requirements: CMYK color mode, high-resolution images, include barcode and legal requirements.|Packaging Label|Hush & Hold provides calming weighted lap pads for children with ADHD for improved focus.\",\"imageUrl\":\"https://im.runware.ai/image/os/a02d21/ws/3/ii/ded4a20f-3e18-490a-a289-c77ace2d1bf6.jpg\",\"promptId\":\"13d46b3c-9170-460f-946f-de567e7c6ee8\",\"timeStamp\":\"20260420T115107Z\",\"countId\":\"2\"}]";
            Console.WriteLine($"Raw JSON: {jsonResponse}");

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var imageIdeas = System.Text.Json.JsonSerializer.Deserialize<List<ImageIdeaModelInit>>(jsonResponse, options);

            Console.WriteLine($"Fetched {imageIdeas?.Count ?? 0} image ideas.");

            return imageIdeas ?? new List<ImageIdeaModelInit>();
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


        public class ImageIdeaModelInit
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
                    var url =
                        $"{ApiConfig.ApiBaseUrl}api/GetImageIdeaCountsByCompanyId?companyIdeaId={companyIdeaId}" +
                        "&code=REDACTED_FOR_GROOMING";

                    var response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<MakeVideoModal.ImageIdeaCountResult>();
                        if (result == null)
                            return null;

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

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();

                // Deserialize the response to extract the ID
                var responseObject = System.Text.Json.JsonSerializer.Deserialize<SaveImageIdeaResponse>(responseContent, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                Console.WriteLine($"ImageIdea saved successfully. Id: {responseObject?.Id}");

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

            var request = new PickMainConceptModal.OpenAIIdeaRequest
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

        private async Task<string?> SavePromptToDatabaseAsync(string prompt, string companyIdeaId)
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
            public List<ImageIdeaModelInit> Images { get; set; } = new List<ImageIdeaModelInit>();
        }
    }
}

// ModalService.cs
public class ModalServiceInit
{
    public event Action? OnModalClosed;

    public void CloseModal()
    {
        // Notify subscribers that a modal has closed
        OnModalClosed?.Invoke();
    }
}


using Microsoft.AspNetCore.Components;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Web; // Add this for HttpUtility
using Microsoft.AspNetCore.Components.Forms;
using static System.Net.Mime.MediaTypeNames;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Security;
using System.Net.Http.Json;
using System;
using Telerik.SvgIcons;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Common;
using DesignMode.AI.Services.OpenAI.Models;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net;
//using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace DesignMode.AI.Client.Pages.Init
{
    public partial class Idea : ComponentBase
    {
        [Inject] private NavigationManager NavigationManager { get; set; }
        [Inject] private NavigationManager Navigation { get; set; }
        [Inject] private ApiConfigService ApiConfig { get; set; }
        [Inject] private SessionService _sessionService { get; set; }
        [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; }
        [Inject] private Blazored.LocalStorage.ILocalStorageService LocalStorage { get; set; }


        public string Name { get; set; }
        public string Industry { get; set; }
        public string timestampCompanyIdeaId { get; set; }
        public string existingInfo { get; set; }
        // Declare items as a public property
        public List<string> Items { get; set; } = new List<string>();
        public List<RenderFragment> renderedButtons = new List<RenderFragment>();
        private bool isLoading = true; // Define isLoading variable
        private string placeholderText = "Describe your idea here...";

        protected async override void OnInitialized()
        {
            // ✅ Clear any previous session data
            await _sessionService.ClearSession();

            var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
            var queryString = uri.Query;

            var parsedQuery = HttpUtility.ParseQueryString(queryString);

            if (parsedQuery["name"] != null)
            {
                Name = parsedQuery["name"];
            }

            if (parsedQuery["industry"] != null)
            {
                Industry = parsedQuery["industry"];
            }

            timestampCompanyIdeaId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            existingInfo = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(Name))}|~~|@@|^^|{Convert.ToBase64String(Encoding.UTF8.GetBytes(Industry))}|~~|@@|^^|{timestampCompanyIdeaId}";

            //var url = $"https://getbrieffunctions20250203090345.azurewebsites.net/api/InsertClient?" +
            //$"name={Uri.EscapeDataString(Name)}" +
            //$"&industry={Uri.EscapeDataString(Industry)}"; // Replace "test" with actual data if needed

            try
            {
                //{
                //    "model": "gpt-3.5-turbo", 
                //    "messages": [
                //      {"role": "system", "content": "You are a helpful assistant that finds influencers on the internet."},
                //      {"role": "user", "content": "list influencers in the 'bike shoe' industry. add their social media or email"}
                //    ],
                //    "max_tokens": 100,
                //    "temperature": 0.7
                // }
                //{
                //    "model": "gpt-3.5-turbo", 
                //    "messages": [
                //      {"role": "system", "content": "You are a helpful assistant that finds designers on the internet."},
                //      {"role": "user", "content": "list designers with illustration experience. add their social media or email"}
                //    ],
                //    "max_tokens": 1000,
                //    "temperature": 0.7
                //  }

                if (isLoading || textareaText == "")
                {
                    IsNextButtonDisabled = false;
                }
                var question = $"now generate a full creative brief with a $1000 budget for item 1. put individual price per deliverable.  call the objective section goal.";
                var q2 = $"now generate a full creative brief with a $10000 budget. update the deliverables to match this budget. put individual price per deliverable.";
                var q3 = $"list influencers in the {industry} industry.  add the amount of followers.";
                Items = await FetchIdeaFromOpenAI(Name, Industry, ApiConfig.ApiBaseUrl);
                // Save fetched ideas to Azure Function
                if (Items.Count > 0)
                {
                    await SaveAllIdeasFromItems();
                }

                int colonIndex = Items[5].IndexOf(':');
                if (colonIndex != -1 && colonIndex + 1 < Items[5].Length)
                {
                    string result = Items[5].Substring(colonIndex + 1).Trim();
                    placeholderText = "e.g., " + result;
                }

                // Save each item to session
                for (int i = 0; i < Items.Count; i++)
                {
                    string sessionKey = $"Idea_{i}";
                    await _sessionService.SetSession(sessionKey, Items[i]);
                }

                //var test2 = await FetchIdeaFromOpenAI2(q1, question, test, Name, Industry);
                //var test3 = await FetchIdeaFromOpenAI3(q2, q1, question, test, test2, Name, Industry);
                //var test4 = await FetchIdeaFromOpenAI4(q3, q2, q1, question, test, test2, test3, Name, Industry);
                //var test5 = test + test2 + test3;// + test4;
                // Send the GET request

                // Generate the buttons dynamically in the OnAfterRender lifecycle method
                renderedButtons.Clear(); // Clear previous rendered buttons if any
                int count = 0;
                foreach (var itemsplit in Items)
                {
                    if (count >= 5)
                    {
                        break;
                    }

                    var button = new RenderFragment(builder =>
                    {
                        builder.OpenElement(0, "div");
                        builder.AddAttribute(1, "class", "feature-button");
                        builder.AddAttribute(2, "title", itemsplit);
                        builder.AddAttribute(3, "onmouseover", EventCallback.Factory.Create(this, () => {
                            var index = itemsplit.IndexOf(':');
                            var secondPart = itemsplit.Substring(index + 1).Trim();
                            UpdatePlaceholder(secondPart);
                        }));
                        builder.AddAttribute(4, "onclick", EventCallback.Factory.Create(this, () => {
                            var index = itemsplit.IndexOf(':');
                            var firstPart = itemsplit.Substring(0, index).Trim();
                            var secondPart = itemsplit.Substring(index + 1).Trim();
                            UpdateTextareaText("", itemsplit);
                        }));
                        string[] itemsplitarr = itemsplit.Split(new char[] { ':', '-' });
                        builder.AddContent(5, itemsplitarr[0]);
                        builder.CloseElement();
                    });

                    renderedButtons.Add(button);
                    count++;
                }

                // Finish loading
                isLoading = false;
                if (textareaText == null || textareaText == "")
                {
                    IsNextButtonDisabled = false;
                }
                else
                {
                    IsNextButtonDisabled = true;
                }
                StateHasChanged();
                //HttpResponseMessage response = await httpClient.GetAsync(url);
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Exception occurred: " + ex.Message);
            }
        }

        private string uploadMessage = "";

        private async Task Skip()
        {
            //Navigation.NavigateTo($"client/idea");
        }

        private static readonly HttpClient httpClient = new HttpClient();
        private async Task Next()
        {
            IsNextButtonDisabled = true;
            Navigation.NavigateTo($"init/gen?name={Uri.EscapeDataString(Name)}&industry={Uri.EscapeDataString(Industry)}&cid={Uri.EscapeDataString(existingInfo)}&prompt={Uri.EscapeDataString(textareaMinText + ":" + textareaText)}");
        }

        private string businessName = "Your Business";
        private string industry = "General Industry";
        private List<string> featureButtons = new()
        {
            "Project Management",
            "Design Asset Library",
            "Client Feedback",
            "Portfolio Showcase",
            "Collaboration Hub"
        };

        private async Task FetchDesignIdea(int index)
        {
            featureButtons[index] = "Loading...";

            try
            {
                string idea = null; // await FetchIdeaFromOpenAI(businessName, industry);
                featureButtons[index] = idea;
            }
            catch
            {
                featureButtons[index] = "Error. Try again";
            }
            StateHasChanged();
        }

        private async Task<List<string>> FetchIdeaFromOpenAI(string name, string industry, string url)
        {
            List<string> items = new List<string>();
            HttpClient client = new HttpClient();
            string functionUrl = $"{url}api/GetAIIdeaResponse?code=REDACTED_FOR_GROOMING";

            var response = await client.PostAsJsonAsync(functionUrl, new OpenAIIdeaRequest { Name = name, Industry = industry });

            if (response.IsSuccessStatusCode)
            {
                items = await response.Content.ReadFromJsonAsync<List<string>>();
                return items;
            }
            else
            {
                return new List<string>();
            }
        }

        private async Task SaveAllIdeasFromItems()
        {
            var authState = await AuthStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;
            bool isAuthorized = user.Identity.IsAuthenticated;
            string userId = "";

            if (isAuthorized)
            {
                userId = user.Identity.Name;
            } 
            else 
            {
                userId = "";
            };

            var timestamp = DateTime.UtcNow.Ticks.ToString();

            var keys = await LocalStorage.KeysAsync();
            var unsavedIdeaKeys = keys.Where(k => k.StartsWith("unsaved_ideas_")).ToList();
            var unsavedCompanyKeys = keys.Where(k => k.StartsWith("unsaved_company_industry_")).ToList();

            var companyRequest = new CompanyIndustryRequest
            {
                Name = Name,
                Industry = Industry,
                UserId = userId
            };

            //Use for later spliting
            //string[] parts = combined.Split(new[] { "|~~|@@|^^|" }, StringSplitOptions.None);
            //string decodedName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            //string decodedIndustry = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));

            var ideaRequests = new List<IdeaRequest>();
            foreach (var item in Items)
            {
                if (string.IsNullOrWhiteSpace(item) || !item.Contains(":"))
                    continue;

                var splitIndex = item.IndexOf(':');
                var title = item.Substring(0, splitIndex).Trim();
                var description = item.Substring(splitIndex + 1).Trim();

                ideaRequests.Add(new IdeaRequest
                {
                    Title = title,
                    Description = description,
                    UserId = userId,
                    CompanyIdeaId = existingInfo
                });
            }

            if (isAuthorized)
            {
                ideaRequests = new List<IdeaRequest>();
                foreach (var item in Items)
                {
                    if (string.IsNullOrWhiteSpace(item) || !item.Contains(":"))
                        continue;

                    var splitIndex = item.IndexOf(':');
                    var title = item.Substring(0, splitIndex).Trim();
                    var description = item.Substring(splitIndex + 1).Trim();

                    ideaRequests.Add(new IdeaRequest
                    {
                        Title = title,
                        Description = description,
                        UserId = userId,
                        CompanyIdeaId = existingInfo
                    });
                }
                string ideaFunctionUrl = $"{ApiConfig.ApiBaseUrl}api/SaveIdea?code=REDACTED_FOR_GROOMING";
                string companyFunctionUrl = $"{ApiConfig.ApiBaseUrl}api/SaveCompanyIndustry?code=REDACTED_FOR_GROOMING";

                var companyResponse = await httpClient.PostAsJsonAsync(companyFunctionUrl, companyRequest);

                if (!companyResponse.IsSuccessStatusCode)
                {
                    var error = await companyResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Failed to save company: {error}");
                }

                foreach (var idea in ideaRequests)
                {
                    var response = await httpClient.PostAsJsonAsync(ideaFunctionUrl, idea);
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"❌ Failed to save idea '{idea.Title}': {error}");
                    }
                }
            }
            else
            {
                // Generate unique keys using timestamp
                string ideaKey = $"unsaved_ideas";
                string companyKey = $"unsaved_company_industry";

                // Save to localStorage using unique keys
                if (!string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Industry))
                {
                    await LocalStorage.SetItemAsync(companyKey, companyRequest);
                }

                if (ideaRequests.Count > 0)
                {
                    await LocalStorage.SetItemAsync(ideaKey, ideaRequests);
                }
            }
        }

        private async Task<string> CheckCompanyAndIdeasExist(string userId)
        {
            string functionUrl = $"{ApiConfig.ApiBaseUrl}api/CheckCompanyIndustryAndIdeasExists?code=REDACTED_FOR_GROOMING"; // Replace with real key

            var companyRequest = new CompanyIndustryRequest
            {
                Name = Name,
                Industry = Industry,
                UserId = userId
            };

            var response = await httpClient.PostAsJsonAsync(functionUrl, companyRequest);

            if (response.IsSuccessStatusCode)
            {
                var resultJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                bool exists = root.GetProperty("Exists").GetBoolean();
                bool hasIdeas = root.GetProperty("HasIdeas").GetBoolean();

                if (exists && hasIdeas)
                {
                    return root.GetProperty("CompositeKey").GetString(); // company_industry_datetime
                }
            }
            return null;
        }

    }
}


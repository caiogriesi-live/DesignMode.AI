using Blazored.LocalStorage;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DesignMode.AI.Shared.Util
{
    public class SavePending
    {
        private readonly HttpClient _http;
        private readonly ILocalStorageService _localStorage;
        private readonly JwtAuthenticationStateProvider _authProvider;

        public SavePending(HttpClient http, ILocalStorageService localStorage, AuthenticationStateProvider authProvider)
        {
            _http = http;
            _localStorage = localStorage;
            _authProvider = (JwtAuthenticationStateProvider)authProvider;
        }

        public async Task SavePendingIdeasAfterLogin(string url)
        {
            const string localStorageKey = "unsaved_company_industry";

            if (!await _localStorage.ContainKeyAsync(localStorageKey))
                return;

            var pendingCompany = await _localStorage.GetItemAsync<CompanyIndustryRequest>(localStorageKey);
            string timestampCompanyIdeaId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(pendingCompany.Name));
            string industry = Convert.ToBase64String(Encoding.UTF8.GetBytes(pendingCompany.Industry));
            string existingInfo = $"{name}|~~|@@|^^|{industry}|~~|@@|^^|{timestampCompanyIdeaId}";

            //Use for later spliting
            //string[] parts = combined.Split(new[] { "|~~|@@|^^|" }, StringSplitOptions.None);
            //string decodedName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            //string decodedIndustry = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));

            if (!await _localStorage.ContainKeyAsync("unsaved_ideas"))
                return;

            var pendingIdeas = await _localStorage.GetItemAsync<List<IdeaRequest>>("unsaved_ideas");
            if (pendingIdeas == null || pendingIdeas.Count == 0)
                return;

            var authState = await _authProvider.GetAuthenticationStateAsync();
            var user = authState.User;
            string userId = user.Identity.Name;

            string functionUrl = $"{url}api/SaveIdea?code=REDACTED_FOR_GROOMING";

            foreach (var idea in pendingIdeas)
            {
                idea.UserId = userId;
                idea.CompanyIdeaId = existingInfo;

                var response = await _http.PostAsJsonAsync(functionUrl, idea);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Failed to save idea '{idea.Title}': {error}");
                }
            }

            await _localStorage.RemoveItemAsync("unsaved_ideas");
        }

        public async Task SavePendingCompanyInfoAfterLogin(string url)
        {
            const string localStorageKey = "unsaved_company_industry";

            if (!await _localStorage.ContainKeyAsync(localStorageKey))
                return;

            var pendingCompany = await _localStorage.GetItemAsync<CompanyIndustryRequest>(localStorageKey);
            if (pendingCompany == null || string.IsNullOrWhiteSpace(pendingCompany.Name) || string.IsNullOrWhiteSpace(pendingCompany.Industry))
                return;

            var authState = await _authProvider.GetAuthenticationStateAsync();
            var user = authState.User;
            if (!user.Identity.IsAuthenticated)
                return;

            pendingCompany.UserId = user.Identity.Name;

            string functionUrl = $"{url}api/SaveCompanyIndustry?code=REDACTED_FOR_GROOMING";

            var response = await _http.PostAsJsonAsync(functionUrl, pendingCompany);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Failed to save company info: {error}");
                return;
            }

            await _localStorage.RemoveItemAsync(localStorageKey);
        }
    }
}

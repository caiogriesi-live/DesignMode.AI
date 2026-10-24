using Blazored.LocalStorage;
using DesignMode.AI.Common;
using DesignMode.AI.Shared.Models;
using System;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http;
using System.Text.Json;
using System.Text;

namespace DesignMode.AI.Shared.Security
{
    public class AuthService
    {
        private readonly HttpClient _http;
        private readonly ILocalStorageService _localStorage;
        private readonly JwtAuthenticationStateProvider _authProvider;

        public AuthService(HttpClient http, ILocalStorageService localStorage, AuthenticationStateProvider authProvider)
        {
            _http = http;
            _localStorage = localStorage;
            _authProvider = (JwtAuthenticationStateProvider)authProvider;
        }

        public async Task<bool> Login(string username, string password, string url)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{url}api/Login?code=REDACTED_FOR_GROOMING"; // Update for deployed URL

            var response = await client.PostAsJsonAsync(functionUrl, new LoginModel { Username = username, Password = password });

            if (!response.IsSuccessStatusCode) return false;

            var result = await response.Content.ReadFromJsonAsync<Response<TokenResponse>>();
            var token = result?.Item?.Token;

            if (token is null) return false;

            // Calculate expiration time
            var expirationTime = DateTime.UtcNow.AddDays(30);
            await _localStorage.SetItemAsync("authToken", token);
            await _localStorage.SetItemAsync("authTokenExpiration", expirationTime);

            _authProvider.NotifyUserAuthentication(token);
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            return true;
        }

        public async Task<bool> IsTokenExpired()
        {
            var expirationTime = await _localStorage.GetItemAsync<DateTime>("authTokenExpiration");

            return expirationTime <= DateTime.UtcNow;
        }

        public async Task<bool> LoginWithGoogle(string idToken, string url)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{url}api/GoogleLogin?code=REDACTED_FOR_GROOMING"; // no function key if not required

            var response = await client.PostAsJsonAsync(functionUrl, new { IdToken = idToken });

            if (!response.IsSuccessStatusCode) return false;

            var result = await response.Content.ReadFromJsonAsync<TokenResponse>();
            var token = result?.Token;

            if (token is null) return false;

            // Calculate expiration time
            var expirationTime = DateTime.UtcNow.AddDays(30);
            await _localStorage.SetItemAsync("authToken", token);
            await _localStorage.SetItemAsync("authTokenExpiration", expirationTime);

            _authProvider.NotifyUserAuthentication(token);
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            return true;
        }


        public async Task<(bool Success, string ErrorMessage)> Signup(string username, string password, string phoneNumber, string url)
        {
            HttpClient client = new HttpClient();
            string functionUrl = $"{url}api/Signup?code=REDACTED_FOR_GROOMING"; // Update for deployed URL

            var response = await client.PostAsJsonAsync(functionUrl, new
            {
                Username = username,
                Password = password,
                PhoneNumber = phoneNumber
            });

            if (response.IsSuccessStatusCode)
            {
                return (true, null); // Signup successful
            }
            else
            {
                // If the response is not successful, we need to check the error message
                var errorContent = await response.Content.ReadFromJsonAsync<ErrorResponse>();

                // Check for specific error message returned from API
                if (errorContent?.Message == "Username already exists")
                {
                    return (false, "Username already exists. Please choose another username.");
                }

                return (false, errorContent?.Message ?? "Unknown error occurred.");
            }
        }

        public async Task<bool> ForgotPassword(string username, string apiBaseUrl)
        {
            try
            {
                using var client = new HttpClient();

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{apiBaseUrl}api/ForgotPassword?code=REDACTED_FOR_GROOMING"
                );

                //$"{url}api/Signup?code=REDACTED_FOR_GROOMING";

                // IMPORTANT: match backend contract (Username, not Email)
                var body = new
                {
                    Username = username
                };

                request.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json"
                );

                // IMPORTANT: Azure Function validates Origin header
                request.Headers.Add("Origin", apiBaseUrl);

                var response = await client.SendAsync(request);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task Logout()
        {
            await _localStorage.RemoveItemAsync("authToken");
            _authProvider.NotifyUserLogout();
            _http.DefaultRequestHeaders.Authorization = null;
        }

        // Add these methods to your AuthService class

        public async Task<bool> ValidateResetToken(string token, string apiBaseUrl)
        {
            try
            {
                var client = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}api/ValidateResetToken?code=REDACTED_FOR_GROOMING");
                request.Content = new StringContent(JsonSerializer.Serialize(new { Token = token }),
                    Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> ResetPassword(string token, string newPassword, string apiBaseUrl)
        {
            try
            {
                var client = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}api/ResetPassword?code=REDACTED_FOR_GROOMING");
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    Token = token,
                    NewPassword = newPassword
                }), Encoding.UTF8, "application/json");

                var response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }

    public class ErrorResponse
    {
        public string Message { get; set; }
    }

}

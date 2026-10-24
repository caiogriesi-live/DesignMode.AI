using Blazored.LocalStorage;
using DesignMode.AI.Shared.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DesignMode.AI.Client.Services;

public interface IAICreditPurchaseClient
{
    Task<AICreditCheckoutResponse> StartAsync(string packageId);
    Task<AICreditCheckoutResponse> UpgradeAsync(string plan) => throw new NotSupportedException();
    Task<string?> PendingAsync();
    Task<AICreditPurchaseStatus?> StatusAsync(string id, CancellationToken cancellationToken);
    Task ClearAsync();
}

public sealed class AICreditPurchaseClient(HttpClient http, ApiConfigService api, ILocalStorageService storage) : IAICreditPurchaseClient
{
    private const string PendingKey = "pendingAICreditPurchase";
    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{api.ApiBaseUrl.TrimEnd('/')}/api/ai-credits/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await storage.GetItemAsync<string>("authToken"));
        return request;
    }
    public async Task<AICreditCheckoutResponse> StartAsync(string packageId)
    {
        using var request = await RequestAsync(HttpMethod.Post, "checkout");
        request.Content = JsonContent.Create(new AICreditCheckoutRequest(packageId));
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var checkout = await response.Content.ReadFromJsonAsync<AICreditCheckoutResponse>() ?? throw new InvalidOperationException();
        await storage.SetItemAsync(PendingKey, checkout.AttemptId);
        return checkout;
    }
    public async Task<string?> PendingAsync() => await storage.GetItemAsync<string>(PendingKey);
    public async Task<AICreditCheckoutResponse> UpgradeAsync(string plan)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{api.ApiBaseUrl.TrimEnd('/')}/api/account/upgrade");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await storage.GetItemAsync<string>("authToken"));
        request.Content = JsonContent.Create(new AICreditCheckoutRequest(plan));
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(UpgradeFailureMessage(response.StatusCode, await response.Content.ReadAsStringAsync()));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AICreditCheckoutResponse>() ?? throw new InvalidOperationException();
    }
    public static string UpgradeFailureMessage(System.Net.HttpStatusCode status, string body)
    {
        if (status == System.Net.HttpStatusCode.Unauthorized) return "Please sign in again before changing your plan.";
        // Only show known application messages, never raw provider errors, IDs, or HTML.
        string? message = body.Trim();
        if (message.StartsWith('"'))
        {
            try { message = System.Text.Json.JsonSerializer.Deserialize<string>(message); }
            catch (System.Text.Json.JsonException) { message = null; }
        }
        return message switch
        {
            "Plan changes are not configured for this environment." => "Plan changes are not configured for this environment. Please contact contact@designmode.ai.",
            "A single active subscription is required for this upgrade. Please contact contact@designmode.ai for help with your plan." => message,
            "No current recurring subscription was found for your account. Please contact contact@designmode.ai to set up your plan." => message,
            "Your account has multiple current subscriptions. Please contact contact@designmode.ai to choose which subscription to upgrade." => message,
            "This older subscription needs help from contact@designmode.ai before changing plans." => message,
            "Please resolve your current subscription status before changing plans." => message,
            "This upgrade is not available for your current plan or billing interval." => message,
            _ => "We couldn't open the plan change confirmation. Please try again."
        };
    }
    public async Task ClearAsync() => await storage.RemoveItemAsync(PendingKey);
    public async Task<AICreditPurchaseStatus?> StatusAsync(string id, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, "purchases/" + Uri.EscapeDataString(id));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound)
        {
            await ClearAsync();
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AICreditPurchaseStatus>(cancellationToken);
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace DesignMode.AI.Client.Services;

public sealed class FunnelProgressService(HttpClient http, ApiConfigService api, IJSRuntime js)
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    public static bool Tracks(string location)
    {
        var path = new Uri(location).AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return path is "/init/mainconcept" or "/init/mainconceptwebsite" or "/init/mainconceptinvite"
            or "/init/funnelend" or "/init/funnelendwebsite" or "/init/upgrade" or "/init/geninit"
            or "/init/gen" or "/init/idea" or "/init/iam" or "/init/designer-select" or "/init/init"
            or "/init/success" or "/init/website-success" || path.StartsWith("/init/gen/");
    }

    public async Task SaveAsync(string location)
    {
        if (!Tracks(location)) return;
        await _writes.WaitAsync();
        try
        {
            var token = await js.InvokeAsync<string?>("localStorage.getItem", "authToken");
            if (token?.StartsWith('"') == true) token = JsonSerializer.Deserialize<string>(token);
            if (string.IsNullOrWhiteSpace(token)) return;
            var uri = new Uri(location);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{api.ApiBaseUrl}api/UpdateUserCurrentPage?code=REDACTED_FOR_GROOMING")
            {
                Content = JsonContent.Create(new { Currentpage = uri.PathAndQuery + uri.Fragment })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        finally { _writes.Release(); }
    }
}

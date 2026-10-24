using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.JSInterop;

namespace DesignMode.AI.Client.Services;

public sealed class ApiSessionHandler(ApiConfigService api, IJSRuntime js) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = request.RequestUri;
        var apiOrigin = new Uri(api.ApiBaseUrl);
        if (target is { IsAbsoluteUri: true } && target.Scheme == apiOrigin.Scheme && target.Authority == apiOrigin.Authority)
        {
            var token = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, "authToken");
            if (token?.StartsWith('"') == true) token = JsonSerializer.Deserialize<string>(token);
            request.Headers.Authorization = string.IsNullOrWhiteSpace(token)
                ? null : new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

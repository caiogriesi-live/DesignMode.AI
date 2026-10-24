using System.Text.Json;
using Microsoft.JSInterop;

namespace DesignMode.AI.Client.Services;

public static class FunnelBrowserIdentity
{
    public static async Task<string?> ReadAsync(IJSRuntime js)
    {
        var value = await js.InvokeAsync<string?>("localStorage.getItem", "dm_anonymous_user_id");
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Static landing pages store raw strings; older Razor pages stored JSON strings.
        return value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) : value;
    }
}

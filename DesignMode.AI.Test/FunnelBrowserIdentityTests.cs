using DesignMode.AI.Client.Services;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelBrowserIdentityTests
{
    [DataTestMethod]
    [DataRow("browser-id", "browser-id")]
    [DataRow("\"browser-id\"", "browser-id")]
    [DataRow(null, null)]
    public async Task ReadsStaticAndRazorStorageFormats(string? stored, string? expected)
    {
        Assert.AreEqual(expected, await FunnelBrowserIdentity.ReadAsync(new StorageJs(stored)));
    }

    private sealed class StorageJs(string? value) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Assert.AreEqual("localStorage.getItem", identifier);
            Assert.AreEqual("dm_anonymous_user_id", args![0]);
            return ValueTask.FromResult((TValue)(object?)value!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}

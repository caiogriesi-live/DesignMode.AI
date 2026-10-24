namespace DesignMode.AI.Client.Services
{
    using Microsoft.JSInterop;
    using System.Threading.Tasks;

    public class SessionService
    {
        private readonly IJSRuntime _jsRuntime;

        public SessionService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task SetSession(string key, string value)
        {
            await _jsRuntime.InvokeVoidAsync("sessionStorage.setItem", key, value);
        }

        public async Task<string> GetSession(string key)
        {
            return await _jsRuntime.InvokeAsync<string>("sessionStorage.getItem", key);
        }

        public async Task RemoveSession(string key)
        {
            await _jsRuntime.InvokeVoidAsync("sessionStorage.removeItem", key);
        }

        // ✅ Add this method
        public async Task ClearSession()
        {
            await _jsRuntime.InvokeVoidAsync("sessionStorage.clear");
        }
    }
}
using DesignMode.AI.Client;
using DesignMode.AI.Shared.Interfaces.Business;
using DesignMode.AI.Business;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DesignMode.AI.Services.OpenAI;
using DesignMode.AI.Shared.Security;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Util;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddScoped<DesignMode.AI.Client.Services.FunnelProgressService>();
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient(new DesignMode.AI.Client.Services.ApiSessionHandler(
    sp.GetRequiredService<DesignMode.AI.Client.Services.ApiConfigService>(),
    sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()) { InnerHandler = new HttpClientHandler() })
    { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSingleton<ITestString, TestString>();

builder.Services.AddBlazoredLocalStorage();
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SavePending>();
builder.Services.AddSingleton<ApiConfigService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<ModalService>();
builder.Services.AddScoped<AIUsageRefreshService>();
builder.Services.AddScoped<IAICreditPurchaseClient, AICreditPurchaseClient>();
builder.Services.AddScoped<AttributionContextService>();
builder.Services.AddScoped<DesignModeAnalyticsService>();
builder.Services.AddScoped<PostAuthNavigationService>();

await builder.Build().RunAsync();

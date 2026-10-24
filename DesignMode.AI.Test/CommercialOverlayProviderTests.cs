using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Newtonsoft.Json;
using System.Net;

namespace DesignMode.AI.Test;

[TestClass]
[DoNotParallelize]
public class CommercialOverlayProviderTests
{
    private sealed class Provider(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string Request { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Headers.Add("x-request-id", "provider-request");
            return response;
        }
    }

    [DataTestMethod]
    [DataRow(true)] [DataRow(false)]
    public async Task Planning_MetersSuccessfulOrFailedProviderCall(bool success)
    {
        var previous = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
        Environment.SetEnvironmentVariable("OpenAISettings:ApiKey", "test-only-not-a-real-key");
        try
        {
            var response = JsonConvert.SerializeObject(new { status = "completed", output = new[] { new { type = "message",
                content = new[] { new { type = "output_text", text = "{\"overlays\":[],\"motionAccents\":[]}" } } } },
                usage = new { input_tokens = 100, output_tokens = 30 } });
            using var handler = new Provider(success ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response);
            using var client = new HttpClient(handler);
            AIGenerationUsage? started = null;
            var completed = false; var failed = false;
            var planner = new CommercialOverlayPlanner(client,
                record => { started = record; return Task.FromResult<string?>("usage-id"); },
                (id, json, model, operation, requestId) =>
                {
                    Assert.AreEqual("usage-id", id); StringAssert.Contains(json, "input_tokens");
                    Assert.AreEqual("provider-request", requestId); Assert.AreEqual(CommercialOverlayPlanner.Operation, operation);
                    completed = true; return Task.CompletedTask;
                }, (id, category) => { failed = true; return Task.CompletedTask; });
            if (success)
                Assert.IsNotNull(await planner.PlanAsync("user", "project", "job", "operation", "Orange Blast", "Energy Drinks", [new(0, 0, 5, "Centered product")], 864, 480));
            else
                await Assert.ThrowsExceptionAsync<HttpRequestException>(() => planner.PlanAsync("user", "project", "job", "operation", "Orange Blast", "Energy Drinks", [new(0, 0, 5, "Centered product")], 864, 480));
            Assert.AreEqual(success, completed); Assert.AreEqual(!success, failed);
            Assert.AreEqual(0m, started!.GenerationUnits);
            Assert.IsFalse(handler.Request.Contains("\"ProjectId\""));
        }
        finally { Environment.SetEnvironmentVariable("OpenAISettings:ApiKey", previous); }
    }

    [TestMethod]
    public async Task ConcurrentAndRepeatedPlanning_InvokesProviderOnce()
    {
        CommercialOverlayPlan? saved = null;
        var claimed = 0; var calls = 0;
        Task<CommercialOverlayPlan?> Run() => CommercialOverlayEnhancement.GetPlanOnceAsync(
            () => Task.FromResult(saved), () => Task.FromResult(Interlocked.CompareExchange(ref claimed, 1, 0) == 0),
            async () => { Interlocked.Increment(ref calls); await Task.Yield(); return new CommercialOverlayPlan(); },
            plan => { saved = plan; return Task.CompletedTask; });
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Run()));
        Assert.IsNotNull(await Run()); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task FailedPlanning_ClaimPreventsAnotherBillableAttempt()
    {
        var claimed = false; var calls = 0;
        Task<CommercialOverlayPlan?> Run() => CommercialOverlayEnhancement.GetPlanOnceAsync(
            () => Task.FromResult<CommercialOverlayPlan?>(null), () => { var first = !claimed; claimed = true; return Task.FromResult(first); },
            () => { calls++; throw new HttpRequestException(); }, _ => Task.CompletedTask);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => Run());
        Assert.IsNull(await Run()); Assert.AreEqual(1, calls);
    }
}

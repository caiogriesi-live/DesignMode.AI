using DesignMode.AI.Client.Services;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class ActiveGenerationGuardTests
{
    [TestMethod]
    public async Task PageWorkWithoutOpenGeneratingModal_NeverWarns()
    {
        var js = new BrowserStub();
        await using var guard = new ActiveGenerationGuard(js);
        await using var background = await guard.BeginAsync();
        Assert.IsTrue(guard.IsActive);
        Assert.IsFalse(js.Active);
        Assert.IsTrue(await guard.ConfirmLeaveAsync(navigation: true));
        var idleModal = new object();
        await guard.SetModalVisibleAsync(idleModal, true);
        Assert.IsFalse(js.Active);
        Assert.IsTrue(await guard.ConfirmLeaveAsync(owner: idleModal));
    }

    [TestMethod]
    public async Task OnlyTheOwningVisibleModalPrompts_AndHidingItRemovesUnloadWarning()
    {
        var js = new BrowserStub();
        await using var guard = new ActiveGenerationGuard(js);
        var generatingModal = new object();
        var unrelatedModal = new object();
        await guard.SetModalVisibleAsync(generatingModal, true);
        await guard.SetModalVisibleAsync(unrelatedModal, true);
        await using var operation = await guard.BeginAsync(generatingModal);
        Assert.IsTrue(js.Active);
        Assert.IsTrue(await guard.ConfirmLeaveAsync(owner: unrelatedModal));
        var close = guard.ConfirmLeaveAsync(owner: generatingModal);
        Assert.IsFalse(close.IsCompleted);
        guard.Resolve(false);
        Assert.IsFalse(await close);
        await guard.SetModalVisibleAsync(generatingModal, false);
        Assert.IsTrue(guard.IsActive);
        Assert.IsFalse(js.Active);
        Assert.IsTrue(await guard.ConfirmLeaveAsync(navigation: true));
        await guard.SetModalVisibleAsync(generatingModal, true);
        Assert.IsTrue(js.Active, "Reopening the modal protects its still-running operation.");
    }

    [TestMethod]
    public async Task OwningOperationFinishes_PendingCloseDoesNotWaitForAnotherModal()
    {
        await using var guard = new ActiveGenerationGuard(new BrowserStub());
        var first = new object();
        var second = new object();
        await guard.SetModalVisibleAsync(first, true);
        await guard.SetModalVisibleAsync(second, true);
        var firstOperation = await guard.BeginAsync(first);
        await using var secondOperation = await guard.BeginAsync(second);
        var close = guard.ConfirmLeaveAsync(owner: first);
        await firstOperation.DisposeAsync();
        Assert.IsTrue(await close);
        Assert.IsTrue(guard.ShouldWarn, "The second visible modal still protects navigation.");
    }

    [TestMethod]
    public async Task OverlappingOperations_StayProtectedUntilLastPersistenceCompletes()
    {
        var js = new BrowserStub();
        await using var guard = new ActiveGenerationGuard(js);
        await guard.SetModalVisibleAsync(this, true);
        Assert.IsFalse(guard.IsActive);
        var first = await guard.BeginAsync(this);
        Assert.IsTrue(js.Active, "Unload protection is installed before the provider can start.");
        var second = await guard.BeginAsync(this);
        Assert.AreEqual(2, guard.ActiveGenerationCount);
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.AreEqual(1, guard.ActiveGenerationCount);
        Assert.IsTrue(js.Active);
        await second.DisposeAsync();
        Assert.AreEqual(0, guard.ActiveGenerationCount);
        Assert.IsFalse(js.Active);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CloseOrNavigation_StayCancelsAndLeaveApprovesOnlyThatRequest(bool navigation)
    {
        await using var guard = new ActiveGenerationGuard(new BrowserStub());
        await guard.SetModalVisibleAsync(this, true);
        await using var operation = await guard.BeginAsync(this);
        var stay = guard.ConfirmLeaveAsync(navigation, this);
        Assert.IsFalse(stay.IsCompleted);
        Assert.AreEqual(navigation, guard.IsNavigation);
        Assert.IsFalse(await guard.ConfirmLeaveAsync(navigation, this), "Repeated exits cannot share an approval.");
        guard.Resolve(false);
        Assert.IsFalse(await stay);
        Assert.IsTrue(guard.IsActive);
        var leave = guard.ConfirmLeaveAsync(navigation, this);
        guard.Resolve(true);
        Assert.IsTrue(await leave);
        Assert.IsTrue(guard.IsActive, "Abandoning UI does not cancel or refund provider work.");
        var next = guard.ConfirmLeaveAsync(navigation, this);
        Assert.IsFalse(next.IsCompleted, "Leave Anyway is not a permanent bypass.");
        guard.Resolve(false);
    }

    [DataTestMethod]
    [DataRow(200)]
    [DataRow(429)]
    [DataRow(500)]
    public async Task CompletionOrFailure_AlwaysReleasesProtection(int status)
    {
        var js = new BrowserStub();
        await using var guard = new ActiveGenerationGuard(js);
        await guard.SetModalVisibleAsync(this, true);
        try
        {
            await using var operation = await guard.BeginAsync(this);
            using var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException) { }
        Assert.IsFalse(guard.IsActive);
        Assert.IsFalse(js.Active);
        Assert.IsTrue(await guard.ConfirmLeaveAsync(owner: this));
    }

    [TestMethod]
    public async Task CompletingLastOperation_ReleasesPendingExitImmediately()
    {
        await using var guard = new ActiveGenerationGuard(new BrowserStub());
        await guard.SetModalVisibleAsync(this, true);
        var operation = await guard.BeginAsync(this);
        var closing = guard.ConfirmLeaveAsync(owner: this);
        await operation.DisposeAsync();
        Assert.IsTrue(await closing);
        Assert.IsFalse(guard.IsConfirming);
    }

    [TestMethod]
    public async Task Disposal_RemovesUnloadProtectionAndRejectsPendingExit()
    {
        var js = new BrowserStub();
        var guard = new ActiveGenerationGuard(js);
        await guard.SetModalVisibleAsync(this, true);
        var operation = await guard.BeginAsync(this);
        var closing = guard.ConfirmLeaveAsync(owner: this);
        await guard.DisposeAsync();
        Assert.IsFalse(await closing);
        Assert.IsFalse(js.Active);
        await operation.DisposeAsync();
        Assert.IsFalse(js.Active, "Late provider completion cannot reinstall a disposed page's guard.");
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await guard.BeginAsync(this));
    }

    internal sealed class BrowserStub : IJSRuntime
    {
        public bool Active { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            Assert.AreEqual("designModeGenerationGuard.setActive", identifier);
            Active = (bool)args![0]!;
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
}

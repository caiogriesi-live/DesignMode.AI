using Microsoft.JSInterop;

namespace DesignMode.AI.Client.Services;

/// <summary>Tracks work independently from whether its owning modal is still visible.</summary>
public sealed class ActiveGenerationGuard(IJSRuntime js) : IAsyncDisposable
{
    private readonly Dictionary<object, object?> _operations = new();
    private readonly HashSet<object> _visibleModals = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private TaskCompletionSource<bool>? _decision;
    private object? _decisionOwner;
    private bool _disposed;
    public int ActiveGenerationCount => _operations.Count;
    public bool IsActive => !_disposed && ActiveGenerationCount > 0;
    public bool ShouldWarn => !_disposed && _visibleModals.Any(HasActiveOperation);
    public bool IsConfirming => _decision is not null;
    public bool IsNavigation { get; private set; }
    public event Action? Changed;

    private bool HasActiveOperation(object owner) => _operations.Values.Any(value => ReferenceEquals(value, owner));
    private bool ShouldWarnFor(object? owner) => owner is not null && !_disposed &&
        _visibleModals.Contains(owner) && HasActiveOperation(owner);

    public async ValueTask SetModalVisibleAsync(object owner, bool visible)
    {
        if (_disposed) return;
        var changed = visible ? _visibleModals.Add(owner) : _visibleModals.Remove(owner);
        if (!changed) return;
        await UpdateBrowserAsync();
        ReleaseFinishedDecision();
    }

    public async ValueTask<IAsyncDisposable> BeginAsync(object? owner = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var token = new object();
        _operations.Add(token, owner);
        try { await UpdateBrowserAsync(); }
        catch { _operations.Remove(token); throw; }
        return new Operation(this, token);
    }

    public Task<bool> ConfirmLeaveAsync(bool navigation = false, object? owner = null)
    {
        if (!(navigation ? ShouldWarn : ShouldWarnFor(owner))) return Task.FromResult(true);
        // An approval belongs to one requested action, never several queued exits.
        if (_decision is not null) return Task.FromResult(false);
        IsNavigation = navigation;
        _decisionOwner = owner;
        var decision = _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke();
        return decision.Task;
    }

    public void Resolve(bool leave)
    {
        var decision = _decision;
        _decision = null;
        _decisionOwner = null;
        decision?.TrySetResult(leave);
        Changed?.Invoke();
    }

    private void ReleaseFinishedDecision()
    {
        if (_decision is not null && !(IsNavigation ? ShouldWarn : ShouldWarnFor(_decisionOwner))) Resolve(true);
    }

    private async ValueTask FinishAsync(object token)
    {
        if (!_operations.Remove(token) || _disposed) return;
        await UpdateBrowserAsync();
        ReleaseFinishedDecision();
    }

    private async ValueTask UpdateBrowserAsync()
    {
        await _browserLock.WaitAsync();
        try { await js.InvokeVoidAsync("designModeGenerationGuard.setActive", ShouldWarn); }
        finally { _browserLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Resolve(false);
        _operations.Clear();
        _visibleModals.Clear();
        await UpdateBrowserAsync();
    }

    private sealed class Operation(ActiveGenerationGuard guard, object token) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => guard.FinishAsync(token);
    }
}

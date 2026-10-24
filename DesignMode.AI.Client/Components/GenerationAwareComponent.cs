using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;

namespace DesignMode.AI.Client.Components;

public abstract class GenerationAwareComponent : ComponentBase, IAsyncDisposable
{
    private ActiveGenerationGuard? _guard;
    private bool _show;

    [CascadingParameter]
    public ActiveGenerationGuard? GenerationGuard
    {
        get => _guard;
        set
        {
            if (ReferenceEquals(_guard, value)) return;
            if (_guard is not null) _ = _guard.SetModalVisibleAsync(this, false);
            _guard = value;
            if (_guard is not null) _ = _guard.SetModalVisibleAsync(this, _show);
        }
    }

    [Parameter]
    public bool Show
    {
        get => _show;
        set
        {
            // Track both parent-driven visibility changes and the modal's own close actions.
            _show = value;
            if (_guard is not null) _ = _guard.SetModalVisibleAsync(this, value);
        }
    }

    protected async ValueTask<IAsyncDisposable?> BeginGenerationAsync() =>
        GenerationGuard is null ? null : await GenerationGuard.BeginAsync(this);

    protected Task<bool> ConfirmGenerationCloseAsync() =>
        GenerationGuard?.ConfirmLeaveAsync(owner: this) ?? Task.FromResult(true);

    public async ValueTask DisposeAsync()
    {
        // Preserve cleanup already implemented by existing modal components.
        if (this is IDisposable disposable) disposable.Dispose();
        if (_guard is not null) await _guard.SetModalVisibleAsync(this, false);
    }
}

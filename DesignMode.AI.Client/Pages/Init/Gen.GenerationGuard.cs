using DesignMode.AI.Client.Services;

namespace DesignMode.AI.Client.Pages.Init;

public partial class Gen : IAsyncDisposable
{
    private ActiveGenerationGuard? _generationGuard;
    private ActiveGenerationGuard GenerationGuard => _generationGuard ??= new(JSRuntime);

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_generationGuard is not null) await _generationGuard.DisposeAsync();
    }
}

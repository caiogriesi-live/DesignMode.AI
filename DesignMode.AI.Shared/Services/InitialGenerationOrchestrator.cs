namespace DesignMode.AI.Shared.Services;

public static class InitialGenerationOrchestrator
{
    public static IReadOnlyList<string> SelectIdeaCandidates(
        IEnumerable<string> persistedIdeas,
        IEnumerable<string> sessionIdeas,
        IEnumerable<string>? checkoutIdeas = null)
    {
        if (checkoutIdeas is not null)
            return checkoutIdeas.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var persisted = persistedIdeas.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return persisted.Count > 0
            ? persisted
            : sessionIdeas.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    }

    public static IReadOnlyList<string> SelectSecondaryCandidates(IEnumerable<string> candidates) =>
        candidates.ToList();

    public static async Task<bool> GenerateMainAsync(
        Func<Task<string>> generate,
        Func<string, Task> persist,
        Action<string>? log = null)
    {
        log?.Invoke("[GEN] main generation started");
        var result = await generate();
        if (string.IsNullOrWhiteSpace(result))
        {
            log?.Invoke("[GEN] main generation completed");
            return false;
        }

        await persist(result);
        log?.Invoke("[GEN] main generation completed");
        return true;
    }

    public static async Task<int> GenerateSecondaryAsync(
        IReadOnlyList<Func<Task<string>>> generate,
        Func<int, string, Task> persist,
        Action<string>? log = null)
    {
        log?.Invoke($"[GEN] secondary generation started count={generate.Count}");
        var completed = 0;

        for (var index = 0; index < generate.Count; index++)
        {
            try
            {
                var result = await generate[index]();
                await persist(index, result);
                if (!string.IsNullOrWhiteSpace(result))
                    completed++;
            }
            catch
            {
                log?.Invoke($"[GEN] secondary generation failed index={index} category=GenerationOrPersistenceError");
                throw;
            }
        }

        log?.Invoke($"[GEN] secondary generation completed count={completed}");
        return completed;
    }
}

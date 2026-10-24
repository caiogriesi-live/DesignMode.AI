using DesignMode.AI.Shared.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class InitialGenerationOrchestratorTests
{
    [TestMethod]
    public void SelectIdeaCandidates_EmptyCurrentResponseDoesNotReviveOlderIdeas()
    {
        var result = InitialGenerationOrchestrator.SelectIdeaCandidates(["old saved idea"], ["old session idea"], []);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void SelectIdeaCandidates_PersistedIdeasAvailable_UsesAuthoritativeDatabaseIdeas()
    {
        var result = InitialGenerationOrchestrator.SelectIdeaCandidates(
            ["direction-one", "direction-two"],
            Array.Empty<string>());

        CollectionAssert.AreEqual(new[] { "direction-one", "direction-two" }, result.ToArray());
    }

    [TestMethod]
    public void SelectSecondaryCandidates_MainAlreadyFiltered_DoesNotDropFirstSecondary()
    {
        var result = InitialGenerationOrchestrator.SelectSecondaryCandidates(["secondary-one", "secondary-two"]);

        CollectionAssert.AreEqual(new[] { "secondary-one", "secondary-two" }, result.ToArray());
    }

    [TestMethod]
    public async Task Generate_MainAndSecondaries_WaitsForAndPersistsEveryResult()
    {
        var generated = new List<string>();
        var persisted = new List<string>();

        var mainCompleted = await InitialGenerationOrchestrator.GenerateMainAsync(
            async () => { await Task.Yield(); generated.Add("main"); return "main-url"; },
            url => { persisted.Add(url); return Task.CompletedTask; });

        var secondaryCompleted = await InitialGenerationOrchestrator.GenerateSecondaryAsync(
            [
                async () => { await Task.Yield(); generated.Add("secondary-1"); return "secondary-url-1"; },
                async () => { await Task.Yield(); generated.Add("secondary-2"); return "secondary-url-2"; }
            ],
            (index, url) => { persisted.Add(url); return Task.CompletedTask; });

        Assert.IsTrue(mainCompleted);
        Assert.AreEqual(2, secondaryCompleted);
        CollectionAssert.AreEqual(new[] { "main", "secondary-1", "secondary-2" }, generated);
        CollectionAssert.AreEqual(new[] { "main-url", "secondary-url-1", "secondary-url-2" }, persisted);
    }

    [TestMethod]
    public async Task GenerateSecondary_Failure_IsLoggedAndPropagated()
    {
        var logs = new List<string>();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            InitialGenerationOrchestrator.GenerateSecondaryAsync(
                [() => Task.FromException<string>(new InvalidOperationException("provider failure"))],
                (index, url) => Task.CompletedTask,
                logs.Add));

        Assert.IsTrue(logs.Any(x => x == "[GEN] secondary generation failed index=0 category=GenerationOrPersistenceError"));
        Assert.IsFalse(logs.Any(x => x.StartsWith("[GEN] secondary generation completed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GenerateSecondary_ObserverFailureDoesNotControlGeneration()
    {
        var generated = 0;
        var persisted = 0;
        Action<string> bestEffortObserver = _ =>
        {
            try { throw new InvalidOperationException("tracking unavailable"); }
            catch { }
        };

        var completed = await InitialGenerationOrchestrator.GenerateSecondaryAsync(
            [() => { generated++; return Task.FromResult("secondary-url"); }],
            (index, url) => { persisted++; return Task.CompletedTask; },
            bestEffortObserver);

        Assert.AreEqual(1, completed);
        Assert.AreEqual(1, generated);
        Assert.AreEqual(1, persisted);
    }
}

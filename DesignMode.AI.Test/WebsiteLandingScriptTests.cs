using System.Diagnostics;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class WebsiteLandingScriptTests
{
    [TestMethod]
    public async Task LandingScript_PreservesInputsAndAttributionWithoutBrowserAutomation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DesignMode.AI.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root.FullName, "DesignMode.AI.Test", "WebsiteLanding.unit.cjs"));
        start.ArgumentList.Add(Path.Combine(root.FullName, "DesignMode.AI.Client", "wwwroot", "website-landing.html"));
        start.ArgumentList.Add(Path.Combine(root.FullName, "DesignMode.AI.Client", "wwwroot", "js", "designmode-attribution.js"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(0, process.ExitCode, await output + await error);
    }
}

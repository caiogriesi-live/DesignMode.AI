using System.Text.RegularExpressions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class MakeVideoSelectImagesLayoutTests
{
    [TestMethod]
    public void AllStepFooters_AreWorkspaceChildrenOutsideScrollableBody()
    {
        var markup = Read("MakeVideoSelectImages.razor").Split("@code {")[0];
        var ancestors = new Stack<string>();
        var footers = 0;
        var headers = 0;
        foreach (Match tag in Regex.Matches(markup, @"</?div\b[^>]*>"))
        {
            if (tag.Value.StartsWith("</"))
            {
                ancestors.Pop();
                continue;
            }

            if (tag.Value.Contains("class=\"mvsi-header\""))
            {
                Assert.IsTrue(ancestors.Any(x => x.Contains("mvsi-wrapper")));
                headers++;
            }

            if (tag.Value.Contains("class=\"mvsi-footer "))
            {
                Assert.IsTrue(ancestors.Peek().Contains("mvsi-wrapper"));
                Assert.IsFalse(ancestors.Any(x => x.Contains("class=\"mvsi-scroll\"")));
                StringAssert.Contains(tag.Value, "footer-actions");
                footers++;
            }
            ancestors.Push(tag.Value);
        }
        Assert.AreEqual(0, ancestors.Count);
        Assert.IsTrue(headers > 0);
        Assert.AreEqual(3, footers);
    }

    [TestMethod]
    public void FooterLayout_RemainsInFlowAndAllowsNarrowButtons()
    {
        var source = Read("MakeVideoSelectImages.razor");
        foreach (Match rule in Regex.Matches(source, @"[^{}]*\.mvsi-footer\s*\{([^{}]*)\}"))
            Assert.IsFalse(Regex.IsMatch(rule.Groups[1].Value, @"position:\s*(fixed|absolute)"));

        var css = Read("MakeVideoSelectImages.razor.css");
        StringAssert.Contains(css, "position: relative !important");
        StringAssert.Contains(css, "flex: 0 0 auto");
        StringAssert.Contains(css, "min-width: 0 !important");
        StringAssert.Contains(css, "max-width: 100% !important");
        StringAssert.Contains(css, "white-space: normal !important");
        StringAssert.Contains(css, "flex-wrap: wrap !important");
        StringAssert.Contains(source, "overflow-y: auto;");
        StringAssert.Contains(source, "min-height: 0;");
        Assert.IsFalse(source.Contains("Space for footer"));
    }

    [DataTestMethod]
    [DataRow("CloseModal", "Discard")]
    [DataRow("GenerateScript", "Next: Generate Script")]
    [DataRow("GoBackToImages", "Back to Images")]
    [DataRow("GoToCustomization", "Next:")]
    [DataRow("GoBackToScripts", "Back to Scripts")]
    [DataRow("FinalizeVideo", "Finalize Commercial")]
    public void FooterActions_KeepExistingHandlersAndLabels(string handler, string label)
    {
        var source = Read("MakeVideoSelectImages.razor");
        var footer = source[source.IndexOf("<!-- Footer for Image Selection Section -->")..source.IndexOf("@code {")];
        Assert.IsTrue(Regex.Matches(footer, @"<button\b[\s\S]*?</button>")
            .Cast<Match>().Any(button => button.Value.Contains($"@onclick=\"{handler}\"") && button.Value.Contains(label)));
    }

    private static string Read(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "DesignMode.AI.Client", "Pages", "Init", file));
    }
}

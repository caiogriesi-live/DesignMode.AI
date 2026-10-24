namespace DesignMode.AI.Shared.Services;

public static class PrintMaterialPromptResolver
{
    public static string Resolve(IEnumerable<string>? projectPrompts)
    {
        foreach (var prompt in projectPrompts ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            // Gen stores display, brief, name, industry, detail, category, and brand context.
            var parts = prompt.Split('|');
            if (parts.Length > 5 && string.Equals(parts[5].Trim().TrimStart('-').Trim(),
                "Print Materials", StringComparison.OrdinalIgnoreCase))
                return prompt;
        }

        return string.Empty;
    }
}

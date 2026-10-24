namespace DesignMode.AI.Shared.Services;

public static class EventActivationPromptResolver
{
    public static string BuildReferencePrompt(string canonicalPrompt, string eventContext)
    {
        if (string.IsNullOrWhiteSpace(Resolve(new[] { canonicalPrompt })))
            throw new ArgumentException("An Events & Activations AI Idea is required.", nameof(canonicalPrompt));

        var parts = canonicalPrompt.Split('|');
        // Keep the image API's positional fields intact; event details belong in the brief.
        if (!string.IsNullOrWhiteSpace(eventContext))
            parts[1] += " " + eventContext.Replace('|', ' ');
        return string.Join('|', parts);
    }

    public static string Resolve(IEnumerable<string>? projectPrompts)
    {
        foreach (var prompt in projectPrompts ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            // Gen stores display, brief, name, industry, detail, category, and brand context.
            var parts = prompt.Split('|');
            if (parts.Length > 5 && string.Equals(parts[5].Trim().TrimStart('-').Trim(),
                "Events & Activations", StringComparison.OrdinalIgnoreCase))
                return prompt;
        }

        return string.Empty;
    }
}

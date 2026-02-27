namespace TicketKit;

public static class TemplateRenderer
{
    public static string RenderTokens(
        string input,
        IReadOnlyDictionary<string, string> replacements
    )
    {
        var rendered = input;
        foreach (var replacement in replacements)
        {
            rendered = rendered.Replace(
                replacement.Key,
                replacement.Value,
                StringComparison.Ordinal
            );
        }

        return rendered;
    }
}

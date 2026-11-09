namespace StardewAI.Dialogue;

public static class DialogueTextRules
{
    public const int MaximumReplyLength = 240;
    public const int MaximumSuggestionLength = 80;

    public static bool IsPlayerMessage(string? text)
        => text is not null && text.Length <= DialogueComposerModel.MaximumMessageLength && !text.Any(char.IsControl);

    public static bool IsLiteral(string? text, int maximumLength)
        => text is not null && text.Length is >= 2 && text.Length <= maximumLength
            && !string.IsNullOrWhiteSpace(text)
            && !text.Any(character => char.IsControl(character) || "$#^@%[]{}<>\\|¦".Contains(character));

    public static IReadOnlyList<string>? NormalizeSuggestions(IEnumerable<string>? suggestions)
    {
        if (suggestions is null)
            return null;
        string[] values = suggestions.Take(4).Select(value => value?.Trim() ?? "").ToArray();
        if (values.Length != 3 || values.Distinct(StringComparer.Ordinal).Count() != 3
            || values.Any(value => !IsLiteral(value, MaximumSuggestionLength)))
            return null;
        return Array.AsReadOnly(values);
    }
}

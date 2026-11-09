using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StardewAI.Dialogue;

public sealed record LongTermMemoryOptions
{
    public bool Enabled { get; init; } = true;
    public int MaximumPreferences { get; init; } = 32;
    public bool LearnNew { get; init; } = true;
    public bool AutoUpdate { get; init; } = true;
    public bool DebugLogging { get; init; }
    public LongTermMemoryOptions Normalize() => this with { MaximumPreferences = Math.Clamp(MaximumPreferences, 1, 100) };
}

public sealed record LongTermPreference(string Id, string Category, string Type, string Value,
    string Status, string Evidence, int LearnedDay, int UpdatedDay,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, double Confidence = 1, bool ManuallyEdited = false);

public sealed record PreferenceSummary(string Category, string Type, string Value, string Status);

public sealed record MemoryUpdate(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("previousValue")] string? PreviousValue = null,
    [property: JsonPropertyName("scope")] string Scope = "item");

public static class LongTermPreferenceRules
{
    public static IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(new[]
        { "flower", "weather", "food", "drink", "season", "activity", "animal", "color" });
    private static readonly string[] Types = { "favorite", "like", "dislike" };
    private static readonly Regex Clauses = new(@"[^。！？!?；;，,\r\n]+[。！？!?；;，,]?", RegexOptions.CultureInvariant);
    private static readonly Regex Uncertain = new(
        @"如果|假如|假设|可能|也许|好像|似乎|听说|据说|他说|她说|朋友说|你觉得|你认为|是否|吗|么[。！!；;，,]?$|[?？]|\b(if|maybe|perhaps|would|might|said|says|whether)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // A first-person subject must directly own the preference, including inherited comma clauses.
    private static readonly Regex FirstPerson = new(
        @"^(我(?:现在|目前|最近|其实|一直|还是|仍然|也|真的|已经|非常|特别|比较|最|很|挺|不再|不|更)*(?:喜欢|喜爱|爱好|爱|讨厌|厌恶)|我的(?:最爱|最喜欢|最讨厌)|I(?:\s+(?:now|currently|really|still|also|no longer|don['’]?t|do not))*\s+(?:like|love|enjoy|hate|dislike)|my\s+favou?rite)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Continuation = new(
        @"^(也|还(?=(?:非常|特别|比较|最|很|挺|不再|不|更)*(?:喜欢|喜爱|爱好|爱|讨厌|厌恶))|但|不过|而且|仍然|仍|还是|并且|同时|and\b|but\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Favorite = new(@"最喜欢|最爱|\bfavou?rite\b|love most", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Dislike = new(@"不喜欢|讨厌|厌恶|\bdislike\b|\bhate\b|don['’]?t like|do not like", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Like = new(@"喜欢|喜爱|爱好|\blike\b|\blove\b|\benjoy\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Retraction = new(@"不再|已经不.*了|no longer|don['’]?t.*anymore|do not.*anymore", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Forget = new(@"^(请|麻烦你|帮我)?(忘记|忘掉|删除|不要再记住)|^(please\s+)?(forget|erase)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[][] CategoryWords =
    {
        new[] { "花", "花卉", "flower" }, new[] { "天气", "weather" }, new[] { "食物", "food" },
        new[] { "饮品", "饮料", "drink" }, new[] { "季节", "season" }, new[] { "活动", "爱好", "activity", "hobby" },
        new[] { "动物", "animal" }, new[] { "颜色", "color", "colour" }
    };

    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))).ToUpperInvariant();

    public static bool IsText(string? text, int maximum)
        => text is not null && text.Length is >= 1 && text.Length <= maximum && !string.IsNullOrWhiteSpace(text)
            && Normalize(text).Length > 0
            && !text.Any(character => char.IsControl(character) || "$#^@%[]{}<>\\|¦".Contains(character));

    public static bool IsStoredPreference(LongTermPreference? preference)
        => preference is not null && Guid.TryParseExact(preference.Id, "N", out _)
            && Categories.Contains(preference.Category) && Types.Contains(preference.Type)
            && preference.Status is "active" or "retracted" && preference.Confidence == 1
            && IsText(preference.Value, 80) && IsText(preference.Evidence, 160)
            && preference.LearnedDay >= 0 && preference.UpdatedDay >= 0
            && preference.CreatedAt != default && preference.UpdatedAt >= preference.CreatedAt
            && preference.CreatedAt.Offset == TimeSpan.Zero && preference.UpdatedAt.Offset == TimeSpan.Zero;

    public static string? ValidateBatch(IReadOnlyList<MemoryUpdate>? updates, string playerMessage)
    {
        if (updates is null)
            return null;
        if (updates.Count > 3)
            return "too_many_updates";
        foreach (MemoryUpdate? update in updates)
        {
            if (update is null || update.Operation is not ("ADD" or "REPLACE" or "REMOVE" or "FORGET")
                || update.Scope is not ("item" or "category" or "all") || !IsText(update.Evidence, 160))
                return "invalid_update_fields";
            bool forget = update.Operation == "FORGET";
            if ((!forget && update.Scope != "item")
                || (update.Scope != "all" && !Categories.Contains(update.Category))
                || (update.Scope == "item" && (!Types.Contains(update.Type) || !IsText(update.Value, 80)))
                || (update.PreviousValue is not null && (update.Operation != "REPLACE" || !IsText(update.PreviousValue, 80))))
                return "invalid_update_fields";
            if (!IsSupportedEvidence(update, playerMessage))
                return "unsupported_evidence";
        }
        return null;
    }

    internal static IReadOnlyList<MemoryUpdate>? NormalizeOperations(IReadOnlyList<MemoryUpdate>? updates,
        string message, out bool correctedForget)
    {
        correctedForget = false;
        if (updates is null || updates.Count > 3)
            return updates;
        MemoryUpdate[] normalized = updates.ToArray();
        for (int index = 0; index < normalized.Length; index++)
        {
            MemoryUpdate? update = normalized[index];
            if (update?.Operation is not ("REMOVE" or "FORGET"))
                continue;
            MemoryUpdate forget = update with { Operation = "FORGET" };
            if (ValidateBatch(new[] { forget }, message) is not null
                && forget.Scope == "item" && string.IsNullOrWhiteSpace(forget.Type) && string.IsNullOrWhiteSpace(forget.Value))
            {
                MemoryUpdate category = forget with { Scope = "category" };
                MemoryUpdate all = forget with { Scope = "all" };
                if (ValidateBatch(new[] { category }, message) is null)
                    forget = category;
                else if (string.IsNullOrWhiteSpace(forget.Category) && ValidateBatch(new[] { all }, message) is null)
                    forget = all;
            }
            if (forget == update || ValidateBatch(new[] { forget }, message) is not null)
                continue;
            normalized[index] = forget;
            correctedForget = true;
        }
        return Array.AsReadOnly(normalized);
    }

    private static string ClauseBody(string text) => text.Trim().TrimEnd('。', '！', '？', '!', '?', '；', ';', '，', ',').Trim();

    private static bool IsSupportedEvidence(MemoryUpdate update, string message)
    {
        if (!message.Contains(update.Evidence, StringComparison.Ordinal))
            return false;
        bool inheritedSelf = false;
        bool blockedContinuation = false;
        foreach (Match match in Clauses.Matches(message))
        {
            string clause = match.Value.Trim();
            bool definite = !blockedContinuation && !Uncertain.IsMatch(clause);
            bool self = definite && (FirstPerson.IsMatch(clause) || (inheritedSelf && Continuation.IsMatch(clause)));
            // A model may quote the whole sentence. Validate the complete matching original clause within it.
            if (definite && update.Evidence.Contains(ClauseBody(clause), StringComparison.Ordinal)
                && IsSupportedClause(update, clause, self))
                return true;
            bool comma = clause.EndsWith('，') || clause.EndsWith(',');
            inheritedSelf = self && comma;
            blockedContinuation = !definite && comma;
        }
        return false;
    }

    private static bool IsSupportedClause(MemoryUpdate update, string clause, bool self)
    {
        if (update.Operation == "FORGET")
            return IsForgetEvidence(update, clause);
        if (Regex.IsMatch(clause, @"什么|哪个|哪种|谁|怎么|为什么|\b(what|which|who|why|how)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        if (!self || !Normalize(clause).Contains(Normalize(update.Value), StringComparison.Ordinal))
            return false;
        if (update.Operation == "REMOVE")
        {
            bool retractDislike = Regex.IsMatch(clause,
                @"不再(?:讨厌|厌恶|不喜欢)|no longer\s+(?:dislike|hate)|(?:don['’]?t|do not)\s+(?:dislike|hate).*anymore",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Retraction.IsMatch(clause) && LikeOrDislikeMentioned(clause)
                && (update.Type == "dislike") == retractDislike
                && (update.Type != "like" || !Favorite.IsMatch(clause));
        }
        if (Retraction.IsMatch(clause))
            return false;
        if (update.Operation == "REPLACE" && update.Type != "favorite"
            && (update.PreviousValue is null || !Normalize(clause).Contains(Normalize(update.PreviousValue), StringComparison.Ordinal)))
            return false;
        return update.Type switch
        {
            "favorite" => Favorite.IsMatch(clause) && !Dislike.IsMatch(clause),
            "dislike" => Dislike.IsMatch(clause),
            _ => Like.IsMatch(clause) && !Dislike.IsMatch(clause) && !Favorite.IsMatch(clause)
        };
    }

    private static bool LikeOrDislikeMentioned(string clause) => Like.IsMatch(clause) || Dislike.IsMatch(clause) || Favorite.IsMatch(clause);

    private static bool IsForgetEvidence(MemoryUpdate update, string clause)
    {
        if (!Forget.IsMatch(clause))
            return false;
        string target = ClauseBody(Forget.Replace(clause, "", 1)).TrimEnd('.').Trim();
        if (update.Scope == "all")
            return Regex.IsMatch(target,
                @"^(?:(?:我|我的|关于我|你记住的))*(?:所有|全部|一切)(?:偏好|喜好|记忆|信息|记录)(?:吧|了)?$|^(?:all\s+(?:my\s+)?(?:preferences|memories)|everything(?:\s+about\s+me)?)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (update.Scope == "item")
            return Normalize(clause).Contains(Normalize(update.Value), StringComparison.Ordinal)
                && (Dislike.IsMatch(clause) ? update.Type == "dislike" : !Favorite.IsMatch(clause) || update.Type == "favorite");
        int category = Categories.ToList().IndexOf(update.Category);
        return CategoryWords[category].Any(word => Regex.IsMatch(target,
            @"^(?:我喜欢什么|我喜欢哪些|我喜欢的|我对|我的|关于|所有|全部)?" + Regex.Escape(word)
                + @"(?:的)?(?:相关)?(?:偏好|喜好|记忆|记录)?(?:吧|了)?$|^(?:(?:all\s+)?my\s+)?"
                + Regex.Escape(word) + @"s?\s+(?:preferences|memories)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    internal static bool Apply(ConversationMemory draft, IReadOnlyList<MemoryUpdate> updates,
        LongTermMemoryOptions options, int day, DateTimeOffset now, List<string> notices)
    {
        if (!options.Enabled)
            return true;
        foreach (MemoryUpdate update in updates)
        {
            if (update.Operation == "FORGET")
            {
                ForgetPreferences(draft, update.Scope, update.Category, update.Type, update.Value);
                continue;
            }
            LongTermPreference? existing = draft.LongTermPreferences.FirstOrDefault(preference =>
                preference.Category == update.Category && preference.Type == update.Type
                && (update.Type == "favorite" || Normalize(preference.Value) == Normalize(
                    update.Operation == "REPLACE" ? update.PreviousValue! : update.Value)));
            if (update.Operation == "REMOVE")
            {
                if (options.AutoUpdate && existing is not null && Normalize(existing.Value) == Normalize(update.Value))
                    Replace(draft, existing, existing with { Status = "retracted", Evidence = update.Evidence,
                        UpdatedDay = day, UpdatedAt = Later(now, existing.UpdatedAt), ManuallyEdited = false });
                continue;
            }
            if (existing is not null && !options.AutoUpdate)
                continue;
            if (existing is null && !options.LearnNew)
                continue;
            if (update.Operation == "ADD" && existing is { Status: "active" }
                && Normalize(existing.Value) != Normalize(update.Value))
            {
                if (IsExplicitFavoriteCorrection(update))
                    notices.Add("favorite_operation_corrected");
                else
                {
                    notices.Add("favorite_requires_replace");
                    return false;
                }
            }
            if (update.Operation == "REPLACE" && update.Type != "favorite" && existing is null)
            {
                notices.Add("replace_target_missing");
                return false;
            }
            if (existing is null && draft.LongTermPreferences.Count >= options.MaximumPreferences)
            {
                notices.Add("capacity_reached");
                continue;
            }
            LongTermPreference? duplicate = draft.LongTermPreferences.FirstOrDefault(preference => preference.Id != existing?.Id
                && preference.Category == update.Category && preference.Type == update.Type
                && Normalize(preference.Value) == Normalize(update.Value));
            if (existing is not null && duplicate is not null)
            {
                draft.LongTermPreferences.Remove(existing);
                existing = duplicate;
            }
            RetractOpposite(draft, update, day, now);
            var value = new LongTermPreference(existing?.Id ?? Guid.NewGuid().ToString("N"), update.Category,
                update.Type, update.Value.Trim(), "active", update.Evidence, existing?.LearnedDay ?? day, day,
                existing?.CreatedAt ?? now, existing is null ? now : Later(now, existing.UpdatedAt));
            if (existing is null)
                draft.LongTermPreferences.Add(value);
            else
                Replace(draft, existing, value);
        }
        return true;
    }

    private static bool IsExplicitFavoriteCorrection(MemoryUpdate update)
        => update.Type == "favorite" && Clauses.Matches(update.Evidence).Cast<Match>().Any(match =>
            !Uncertain.IsMatch(match.Value) && FirstPerson.IsMatch(match.Value)
            && Favorite.IsMatch(match.Value) && Normalize(match.Value).Contains(Normalize(update.Value), StringComparison.Ordinal)
            && Regex.IsMatch(match.Value, @"现在|目前|如今|改为|改成|换成|\b(now|currently|instead)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    internal static bool HasUniquePreferenceKeys(IReadOnlyList<LongTermPreference> preferences)
        => preferences.Select(value => value.Category + "/" + value.Type + "/"
            + (value.Type == "favorite" ? "" : Normalize(value.Value))).Distinct(StringComparer.Ordinal).Count() == preferences.Count
            && !preferences.Any(value => value.Status == "active" && value.Type == "dislike"
                && preferences.Any(other => other.Status == "active" && other.Type != "dislike"
                    && other.Category == value.Category && Normalize(other.Value) == Normalize(value.Value)));

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;
    private static void Replace(ConversationMemory draft, LongTermPreference old, LongTermPreference value)
        => draft.LongTermPreferences[draft.LongTermPreferences.IndexOf(old)] = value;

    private static void RetractOpposite(ConversationMemory draft, MemoryUpdate update, int day, DateTimeOffset now)
    {
        foreach (LongTermPreference other in draft.LongTermPreferences.ToArray())
        {
            if (other.Category == update.Category && Normalize(other.Value) == Normalize(update.Value)
                && other.Status == "active" && (other.Type == "dislike") != (update.Type == "dislike"))
                Replace(draft, other, other with { Status = "retracted", Evidence = update.Evidence,
                    UpdatedDay = day, UpdatedAt = Later(now, other.UpdatedAt), ManuallyEdited = false });
        }
    }

    internal static void ForgetPreferences(ConversationMemory draft, string scope, string category = "", string type = "", string value = "")
    {
        if (scope == "all")
            draft.LongTermPreferences.Clear();
        else
            draft.LongTermPreferences.RemoveAll(preference => preference.Category == category
                && (scope == "category" || preference.Type == type && Normalize(preference.Value) == Normalize(value)));
        draft.NextSuggestions = null;
        if (scope != "item")
        {
            draft.Turns.Clear();
            draft.ExcludedTurnIds.Clear();
            return;
        }
        ExcludeRelatedTurns(draft, value);
    }

    internal static void ExcludeRelatedTurns(ConversationMemory draft, string value)
    {
        string normalized = Normalize(value);
        foreach (ConversationTurn turn in draft.Turns)
        {
            string content = turn.PlayerMessage + turn.NpcMessage + string.Join("", turn.Suggestions ?? Array.Empty<string>());
            if (Normalize(content).Contains(normalized, StringComparison.Ordinal) && !draft.ExcludedTurnIds.Contains(turn.Id))
                draft.ExcludedTurnIds.Add(turn.Id);
        }
    }
}

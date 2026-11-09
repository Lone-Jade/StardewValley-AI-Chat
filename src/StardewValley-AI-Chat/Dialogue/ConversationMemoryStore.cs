using System.Text.Json;

namespace StardewAI.Dialogue;

public sealed record ConversationTurn(int Day, string PlayerMessage, string NpcMessage)
{
    public string Id { get; init; } = "";
    public IReadOnlyList<string>? Suggestions { get; init; }
}

public sealed class ConversationMemory
{
    public int Version { get; set; }
    public DialogueIdentity? Identity { get; set; }
    public List<ConversationTurn> Turns { get; set; } = null!;
    public IReadOnlyList<string>? NextSuggestions { get; set; }
    public long Revision { get; set; }
    public List<LongTermPreference> LongTermPreferences { get; set; } = new();
    public List<string> ExcludedTurnIds { get; set; } = new();
}

public sealed record ConversationSnapshot(long Revision, IReadOnlyList<ConversationTurn> History,
    IReadOnlyList<PreferenceSummary> Preferences);

public sealed record MemoryCommitResult(bool Committed, IReadOnlyList<string> Notices);

public sealed class ConversationMemoryStore
{
    public const int MaximumTurns = 6;
    public const int MaximumFileBytes = 256 * 1024;
    private readonly string directory;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<DialogueIdentity, ConversationMemory> loaded = new();
    private readonly Dictionary<DialogueIdentity, Exception> failures = new();
    private readonly HashSet<DialogueIdentity> migrated = new();

    public ConversationMemoryStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        this.directory = directory;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Clear()
    {
        loaded.Clear();
        failures.Clear();
        migrated.Clear();
    }

    public void Reload(DialogueIdentity identity)
    {
        loaded.Remove(identity);
        failures.Remove(identity);
        migrated.Remove(identity);
    }

    public string GetPath(DialogueIdentity identity) => Path.Combine(directory, identity.GetStorageFileName());

    public IReadOnlyList<ConversationTurn> GetTurns(DialogueIdentity identity) => Load(identity).Turns.AsReadOnly();

    public IReadOnlyList<string>? GetSuggestions(DialogueIdentity identity) => Load(identity).NextSuggestions;
    public long GetRevision(DialogueIdentity identity) => Load(identity).Revision;
    public IReadOnlyList<LongTermPreference> GetPreferences(DialogueIdentity identity)
        => Load(identity).LongTermPreferences.AsReadOnly();

    public ConversationSnapshot GetSnapshot(DialogueIdentity identity, LongTermMemoryOptions options)
    {
        ConversationMemory memory = Load(identity);
        int count = Math.Min(32, options.Normalize().MaximumPreferences);
        IReadOnlyList<PreferenceSummary> preferences = options.Enabled
            ? Array.AsReadOnly(memory.LongTermPreferences.OrderByDescending(value => value.UpdatedAt)
                .ThenBy(value => value.Id, StringComparer.Ordinal).Take(count)
                .Select(value => new PreferenceSummary(value.Category, value.Type, value.Value, value.Status)).ToArray())
            : Array.Empty<PreferenceSummary>();
        return new(memory.Revision, Array.AsReadOnly(memory.Turns.Where(turn => !memory.ExcludedTurnIds.Contains(turn.Id)).ToArray()), preferences);
    }

    public void Record(DialogueIdentity identity, ConversationTurn turn, IReadOnlyList<string>? suggestions)
        => Commit(identity, GetRevision(identity), turn, suggestions, null, new LongTermMemoryOptions());

    // All writers run serially on the game thread. Compare before constructing the next immutable snapshot.
    public MemoryCommitResult Commit(DialogueIdentity identity, long expectedRevision, ConversationTurn turn,
        IReadOnlyList<string>? suggestions, IReadOnlyList<MemoryUpdate>? updates, LongTermMemoryOptions options)
    {
        if (!IsValidTurn(turn))
            throw new ArgumentException("Invalid conversation turn.", nameof(turn));
        ConversationMemory current = Load(identity);
        if (current.Revision != expectedRevision)
            return new(false, new[] { "stale_revision" });
        ConversationMemory draft = Copy(current);
        turn = turn with { Id = Guid.NewGuid().ToString("N"), Suggestions = DialogueTextRules.NormalizeSuggestions(suggestions) };
        draft.Turns = draft.Turns.Append(turn).TakeLast(MaximumTurns).ToList();
        draft.NextSuggestions = DialogueTextRules.NormalizeSuggestions(suggestions);
        var notices = new List<string>();
        IReadOnlyList<MemoryUpdate>? normalized = LongTermPreferenceRules.NormalizeOperations(updates, turn.PlayerMessage, out bool correctedForget);
        string? invalid = LongTermPreferenceRules.ValidateBatch(normalized, turn.PlayerMessage);
        if (invalid is not null)
            notices.Add(invalid);
        else
        {
            ConversationMemory proposed = Copy(draft);
            if (LongTermPreferenceRules.Apply(proposed, normalized ?? Array.Empty<MemoryUpdate>(), options.Normalize(), turn.Day, clock().ToUniversalTime(), notices))
            {
                draft = proposed;
                if (correctedForget && options.Enabled)
                    notices.Add("forget_operation_corrected");
            }
        }
        draft.ExcludedTurnIds.RemoveAll(id => !draft.Turns.Any(value => value.Id == id));
        Save(identity, draft);
        return new(true, notices.AsReadOnly());
    }

    public bool Forget(DialogueIdentity identity, string id)
    {
        ConversationMemory current = Load(identity);
        LongTermPreference? preference = current.LongTermPreferences.FirstOrDefault(value => value.Id == id);
        if (preference is null)
            return false;
        ConversationMemory draft = Copy(current);
        LongTermPreferenceRules.ForgetPreferences(draft, "item", preference.Category, preference.Type, preference.Value);
        Save(identity, draft);
        return true;
    }

    public MemoryCommitResult EditPreference(DialogueIdentity identity, long expectedRevision, string id,
        string category, string type, string value, string status, int day)
    {
        if (!LongTermPreferenceRules.Categories.Contains(category) || type is not ("favorite" or "like" or "dislike")
            || status is not ("active" or "retracted") || !LongTermPreferenceRules.IsText(value, 80) || day < 0)
            return new(false, new[] { "invalid_fields" });
        ConversationMemory current = Load(identity);
        if (current.Revision != expectedRevision)
            return new(false, new[] { "stale_revision" });
        LongTermPreference? original = current.LongTermPreferences.FirstOrDefault(preference => preference.Id == id);
        if (original is null)
            return new(false, new[] { "missing_entry" });
        value = value.Trim();
        if (current.LongTermPreferences.Any(preference => preference.Id != id && preference.Category == category
            && preference.Type == type && (type == "favorite"
                || LongTermPreferenceRules.Normalize(preference.Value) == LongTermPreferenceRules.Normalize(value))))
            return new(false, new[] { "duplicate_preference" });
        ConversationMemory draft = Copy(current);
        DateTimeOffset now = clock().ToUniversalTime();
        DateTimeOffset Updated(LongTermPreference preference) => now >= preference.UpdatedAt ? now : preference.UpdatedAt;
        draft.LongTermPreferences[draft.LongTermPreferences.IndexOf(original)] = original with
        {
            Category = category, Type = type, Value = value, Status = status,
            UpdatedDay = day, UpdatedAt = Updated(original), ManuallyEdited = true
        };
        if (status == "active")
        {
            foreach (LongTermPreference other in draft.LongTermPreferences.ToArray())
            {
                if (other.Id != id && other.Category == category && other.Status == "active"
                    && LongTermPreferenceRules.Normalize(other.Value) == LongTermPreferenceRules.Normalize(value)
                    && (other.Type == "dislike") != (type == "dislike"))
                    draft.LongTermPreferences[draft.LongTermPreferences.IndexOf(other)] = other with
                    { Status = "retracted", UpdatedDay = day, UpdatedAt = Updated(other), ManuallyEdited = true };
            }
        }
        LongTermPreferenceRules.ExcludeRelatedTurns(draft, original.Value);
        LongTermPreferenceRules.ExcludeRelatedTurns(draft, value);
        draft.NextSuggestions = null;
        Save(identity, draft);
        return new(true, Array.Empty<string>());
    }

    public void ForgetCategory(DialogueIdentity identity, string category)
    {
        if (!LongTermPreferenceRules.Categories.Contains(category))
            throw new ArgumentException("Unknown memory category.", nameof(category));
        ConversationMemory draft = Copy(Load(identity));
        LongTermPreferenceRules.ForgetPreferences(draft, "category", category);
        Save(identity, draft);
    }

    public void ClearAll(DialogueIdentity identity)
    {
        ConversationMemory draft = Copy(Load(identity));
        LongTermPreferenceRules.ForgetPreferences(draft, "all");
        Save(identity, draft);
    }

    public void ClearSuggestions(DialogueIdentity identity)
    {
        ConversationMemory current = Load(identity);
        if (current.NextSuggestions is null)
            return;
        ConversationMemory draft = Copy(current);
        draft.NextSuggestions = null;
        Save(identity, draft);
    }

    private static ConversationMemory Copy(ConversationMemory current) => new()
    {
        Version = 2, Identity = current.Identity, Revision = current.Revision,
        Turns = current.Turns.ToList(), NextSuggestions = current.NextSuggestions,
        LongTermPreferences = current.LongTermPreferences.ToList(), ExcludedTurnIds = current.ExcludedTurnIds.ToList()
    };

    private void Save(DialogueIdentity identity, ConversationMemory memory)
    {
        string path = GetPath(identity);
        try
        {
            if (memory.Revision == long.MaxValue)
                throw new InvalidDataException("Conversation memory revision is exhausted.");
            memory.Revision++;
            if (migrated.Contains(identity) && !File.Exists(path + ".v1.bak"))
                File.Copy(path, path + ".v1.bak", overwrite: false);
            AtomicJsonFile.Write(path, memory, MaximumFileBytes);
            loaded[identity] = memory;
            migrated.Remove(identity);
        }
        catch (Exception error) when (AtomicJsonFile.IsStorageError(error))
        {
            failures[identity] = error;
            throw new IOException($"Failed to persist conversation memory '{path}'. AI is disabled for this record until reload.", error);
        }
    }

    private ConversationMemory Load(DialogueIdentity identity)
    {
        string path = GetPath(identity);
        if (failures.TryGetValue(identity, out Exception? failure))
            throw new IOException($"Conversation memory '{path}' is disabled until reload.", failure);
        if (loaded.TryGetValue(identity, out ConversationMemory? cached))
            return cached;
        try
        {
            ConversationMemory memory;
            try
            {
                using FileStream file = File.OpenRead(path);
                if (file.Length > MaximumFileBytes)
                    throw new InvalidDataException("Conversation memory exceeds its size limit.");
                using JsonDocument document = JsonDocument.Parse(file);
                memory = document.RootElement.Deserialize<ConversationMemory>()
                    ?? throw new InvalidDataException("Conversation memory is null.");
                if (memory.Version == 2 && (!document.RootElement.TryGetProperty(nameof(ConversationMemory.Revision), out _)
                    || !document.RootElement.TryGetProperty(nameof(ConversationMemory.LongTermPreferences), out _)
                    || !document.RootElement.TryGetProperty(nameof(ConversationMemory.ExcludedTurnIds), out _)))
                    throw new InvalidDataException("Incomplete v2 conversation memory.");
            }
            catch (FileNotFoundException)
            {
                memory = new ConversationMemory { Version = 2, Identity = identity, Turns = new() };
            }
            catch (DirectoryNotFoundException)
            {
                memory = new ConversationMemory { Version = 2, Identity = identity, Turns = new() };
            }
            if (memory.Version is not (1 or 2) || memory.Identity != identity || memory.Turns is null
                || memory.Turns.Count > MaximumTurns || memory.Turns.Any(turn => !IsValidTurn(turn)))
                throw new InvalidDataException("Invalid memory version, identity, or conversation turns.");
            if (memory.Version == 1)
            {
                memory.Version = 2;
                memory.Revision = 0;
                memory.LongTermPreferences = new();
                memory.ExcludedTurnIds = new();
                memory.Turns = memory.Turns.Select(turn => turn with { Id = Guid.NewGuid().ToString("N") }).ToList();
                migrated.Add(identity);
            }
            if (memory.Revision < 0 || memory.LongTermPreferences is null
                || memory.LongTermPreferences.Count > 100 || memory.LongTermPreferences.Any(value => !LongTermPreferenceRules.IsStoredPreference(value))
                || memory.LongTermPreferences.Select(value => value.Id).Distinct().Count() != memory.LongTermPreferences.Count
                || !LongTermPreferenceRules.HasUniquePreferenceKeys(memory.LongTermPreferences)
                || memory.Turns.Any(turn => !Guid.TryParseExact(turn.Id, "N", out _))
                || memory.Turns.Select(turn => turn.Id).Distinct().Count() != memory.Turns.Count
                || memory.ExcludedTurnIds is null || memory.ExcludedTurnIds.Count > MaximumTurns
                || memory.ExcludedTurnIds.Distinct().Count() != memory.ExcludedTurnIds.Count
                || memory.ExcludedTurnIds.Any(id => !memory.Turns.Any(turn => turn.Id == id)))
                throw new InvalidDataException("Invalid memory revision, preferences, or history exclusions.");
            memory.NextSuggestions = DialogueTextRules.NormalizeSuggestions(memory.NextSuggestions);
            memory.Turns = memory.Turns.Select(turn => turn with
                { Suggestions = DialogueTextRules.NormalizeSuggestions(turn.Suggestions) }).ToList();
            loaded.Add(identity, memory);
            return memory;
        }
        catch (Exception error) when (AtomicJsonFile.IsStorageError(error))
        {
            failures[identity] = error;
            throw new IOException($"Failed to read conversation memory '{path}'. AI is disabled for this record until reload.", error);
        }
    }

    public static bool IsValidTurn(ConversationTurn? turn)
        => turn is not null && turn.Day >= 0 && DialogueTextRules.IsPlayerMessage(turn.PlayerMessage)
            && DialogueTextRules.IsLiteral(turn.NpcMessage, DialogueTextRules.MaximumReplyLength);
}

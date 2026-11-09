using System.Net;
using System.Text;
using System.Text.Json;
using StardewAI.AI;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class LongMemoryTests
{
    private static readonly DialogueIdentity Identity = new("TEST_LONG", 7, "Leah");
    private static readonly LongTermMemoryOptions Enabled = new();
    private static readonly string[] Suggestions = { "我也喜欢自然。", "今天心情很好。", "我们聊聊新的话题。" };
    private static readonly AiScene Scene = new("TEST_LONG", 7, "Leah", 0, "Town", "Town", 5, 5, 6, 5);
    private static readonly DeepSeekOptions AiOptions = new() { Enabled = true, ApiKey = "fake-offline-key" };
    private static MemoryUpdate Update(string value = "向日葵", string operation = "ADD", string type = "favorite",
        string? evidence = null, string category = "flower", string? previous = null, string scope = "item")
        => new(operation, category, type, value, evidence ?? "我最喜欢" + value, previous, scope);
    private static MemoryCommitResult Learn(ConversationMemoryStore store, MemoryUpdate update, LongTermMemoryOptions? options = null)
        => store.Commit(Identity, store.GetRevision(Identity), new(0, update.Evidence, "我记住你说的偏好了。"),
            Suggestions, new[] { update }, options ?? Enabled);

    public static void MigrationAndRetention()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        string path = store.GetPath(Identity);
        string legacy = JsonSerializer.Serialize(new { Version = 1, Identity, Turns = new[] { new ConversationTurn(0, "你好", "很高兴认识你。") }, NextSuggestions = Suggestions });
        File.WriteAllText(path, legacy);
        Check(store.GetTurns(Identity).Count == 1 && store.GetRevision(Identity) == 0, "v1 read migration failed.");
        string turnId = store.GetTurns(Identity)[0].Id;
        Check(File.ReadAllText(path) == legacy && !File.Exists(path + ".v1.bak"), "Read-only migration wrote the file.");
        Check(Learn(store, Update()).Committed, "Learning failed after migration.");
        Check(File.ReadAllText(path + ".v1.bak") == legacy, "Original v1 bytes were not backed up.");
        Check(store.GetTurns(Identity)[0].Id == turnId, "Migration changed a turn ID before commit.");
        for (int index = 0; index < 7; index++)
            store.Record(Identity, new(index + 1, "继续聊日常。", "今天过得很充实。"), Suggestions);
        store.Reload(Identity);
        Check(store.GetTurns(Identity).Count == 6 && store.GetTurns(Identity).All(turn => !turn.PlayerMessage.Contains("向日葵")), "First preference did not roll out of six-turn history.");
        LongTermPreference preference = store.GetPreferences(Identity).Single();
        Check(preference.Value == "向日葵" && preference.Confidence == 1 && store.GetRevision(Identity) == 8, "Preference or revision did not survive reload.");
        Check(store.GetSnapshot(Identity, Enabled).Preferences.Single().Value == "向日葵", "Rolled-out preference missing from request snapshot.");
        Check(File.ReadAllText(path + ".v1.bak") == legacy, "Later commits overwrote the v1 backup.");
        Check(store.GetTurns(Identity).Select(turn => turn.Id).Distinct().Count() == 6, "Turn IDs are not unique.");
    }

    public static void OperationsAndConflicts()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, Update());
        string favoriteId = store.GetPreferences(Identity).Single().Id;
        Learn(store, Update("郁金香", type: "like", evidence: "我喜欢郁金香"));
        string message = "我现在最喜欢玫瑰，但仍喜欢向日葵。";
        MemoryCommitResult result = store.Commit(Identity, store.GetRevision(Identity), new(1, message, "这些花都有自己的美。"), Suggestions,
            new[] { Update("玫瑰", "REPLACE", evidence: "我现在最喜欢玫瑰"), Update(type: "like", evidence: "但仍喜欢向日葵") }, Enabled);
        Check(result.Notices.Count == 0, "Valid correction was rejected.");
        Check(store.GetPreferences(Identity).Single(value => value.Type == "favorite").Id == favoriteId
            && store.GetPreferences(Identity).Single(value => value.Type == "favorite").Value == "玫瑰", "Favorite replacement lost the slot or ID.");
        Check(store.GetPreferences(Identity).Count(value => value.Type == "like" && value.Status == "active") == 2, "Replacing favorite removed ordinary likes.");
        result = Learn(store, Update("百合"));
        Check(result.Notices.Contains("favorite_requires_replace"), "ADD silently replaced an active favorite.");
        Learn(store, Update(type: "dislike", evidence: "我不喜欢向日葵"));
        Check(store.GetPreferences(Identity).Single(value => value.Type == "like" && value.Value == "向日葵").Status == "retracted", "Contradictory like remained active.");
        Learn(store, Update(type: "dislike", operation: "REMOVE", evidence: "我不再讨厌向日葵"));
        Check(store.GetPreferences(Identity).Single(value => value.Type == "dislike").Status == "retracted", "REMOVE failed to retract the preference.");
        Learn(store, Update(type: "dislike", evidence: "我不喜欢向日葵"));
        Check(store.GetPreferences(Identity).Single(value => value.Type == "dislike").Status == "active", "Repeated explicit learning did not reactivate a preference.");
        result = Learn(store, Update("玫瑰", "REPLACE", "like", "我不再喜欢郁金香而喜欢玫瑰", previous: "郁金香"));
        Check(result.Notices.Contains("unsupported_evidence"), "Ambiguous combined retraction should not replace a preference.");
        Learn(store, Update("玫瑰", "REPLACE", "like", "我喜欢玫瑰多于郁金香", previous: "郁金香"));
        Check(store.GetPreferences(Identity).Any(value => value.Type == "like" && value.Value == "玫瑰"), "Explicit multi-value replacement failed.");
        Learn(store, Update("雨天", type: "like", category: "weather", evidence: "我喜欢雨天"));
        Learn(store, Update("晴天", type: "like", category: "weather", evidence: "我喜欢晴天"));
        Learn(store, Update("晴天", "REPLACE", "like", "我喜欢晴天多于雨天", "weather", "雨天"));
        store.Reload(Identity);
        Check(store.GetPreferences(Identity).Count(value => value.Category == "weather") == 1, "Replacement into an existing value created duplicate preference keys.");
    }

    public static void EvidenceAndAtomicBatch()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        foreach (string text in new[] { "我喜欢玫瑰吗？", "我喜欢什么花", "如果我喜欢玫瑰就好了。", "我好像喜欢玫瑰。", "她说我喜欢玫瑰。", "你觉得我喜欢玫瑰吗？", "我听说玫瑰很好。", "I might like roses.", "我的朋友喜欢玫瑰", "我觉得你喜欢玫瑰", "她说，我喜欢玫瑰。", "如果天气好，我喜欢玫瑰。" })
        {
            MemoryCommitResult result = Learn(store, Update("玫瑰", type: "like", evidence: text));
            Check(result.Notices.Count > 0 && store.GetPreferences(Identity).Count == 0, "Question, hypothetical or third-party statement was learned.");
        }
        foreach (MemoryUpdate invalid in new[] { Update() with { Category = "unknown" }, Update() with { Type = "unknown" },
            Update() with { Operation = "CLEAR" }, Update() with { Value = "..." }, Update() with { Value = "[74]" },
            Update() with { PreviousValue = "玫瑰" }, Update() with { Value = new string('字', 81) },
            Update("", "FORGET", type: "", category: "", evidence: "忘记向日葵和所有天气", scope: "all"),
            Update("", "FORGET", type: "", evidence: "忘记我喜欢的花朵向日葵", scope: "category"),
            Update(operation: "REMOVE", type: "like", evidence: "我不再讨厌向日葵"),
            Update(operation: "FORGET", evidence: "忘记我讨厌向日葵") })
            Check(Learn(store, invalid).Notices.Count > 0 && store.GetPreferences(Identity).Count == 0, "Invalid update fields accepted.");
        string message = "我最喜欢向日葵。";
        MemoryCommitResult batch = store.Commit(Identity, store.GetRevision(Identity), new(0, message, "这是有效角色回复。"), Suggestions,
            new[] { Update(), Update("玫瑰", evidence: "我最喜欢玫瑰") }, Enabled);
        Check(batch.Committed && store.GetPreferences(Identity).Count == 0, "Partially invalid batch changed preferences or discarded the reply.");
        batch = store.Commit(Identity, store.GetRevision(Identity), new(0, message, "这是另一条有效回复。"), Suggestions,
            Enumerable.Repeat(Update(), 4).ToArray(), Enabled);
        Check(batch.Notices.Contains("too_many_updates") && store.GetPreferences(Identity).Count == 0, "Oversized batch accepted.");
        MemoryCommitResult valid = Learn(store, Update(evidence: "我最喜欢向日葵"));
        Check(valid.Notices.Count == 0 && store.GetPreferences(Identity).Count == 1, "Explicit preference failed.");
        batch = store.Commit(Identity, store.GetRevision(Identity), new(0, "我喜欢雨天。我最喜欢玫瑰。", "有效的角色回复。"), Suggestions,
            new[] { Update("雨天", type: "like", category: "weather", evidence: "我喜欢雨天"), Update("玫瑰") }, Enabled);
        Check(batch.Notices.Contains("favorite_requires_replace") && store.GetPreferences(Identity).Count == 1,
            "Semantically invalid batch partially changed another preference.");
        Learn(store, Update("rain", type: "like", category: "weather", evidence: "I like rain."));
        Check(store.GetPreferences(Identity).Count == 2, "Explicit English preference failed.");
        long revision = store.GetRevision(Identity);
        MemoryCommitResult stale = store.Commit(Identity, revision - 1, new(0, message, "过期的回复。"), null, new[] { Update() }, Enabled);
        Check(!stale.Committed && store.GetRevision(Identity) == revision, "Stale revision changed memory.");
    }

    public static void MixedInputEvidence()
    {
        MemoryUpdate rain = Update("雨天", type: "like", category: "weather", evidence: "我喜欢雨天");
        foreach (string message in new[]
        {
            "你好，你在这里住得习惯吗？我喜欢雨天。", "我喜欢雨天，你呢？",
            "你喜欢什么天气？我喜欢雨天。", "如果我喜欢晴天会怎样？我喜欢雨天。",
            "我喜欢雨天，但是她说她喜欢晴天。"
        })
        {
            using var temporary = new TemporaryDirectory();
            var store = new ConversationMemoryStore(temporary.Path);
            MemoryCommitResult result = store.Commit(Identity, 0, new(0, message, "雨声让人很安心。"), Suggestions,
                new[] { rain with { Evidence = message } }, Enabled);
            store.Reload(Identity);
            Check(result.Committed && result.Notices.Count == 0 && store.GetPreferences(Identity).Single().Value == "雨天",
                "A separate question suppressed a definite statement: " + message);
        }
        string multiple = "你今天怎么样？我最喜欢向日葵，也喜欢雨天，还讨厌南瓜。";
        MemoryUpdate[] updates = { Update(), rain with { Evidence = "也喜欢雨天" },
            Update("南瓜", type: "dislike", category: "food", evidence: "还讨厌南瓜") };
        using var multiTemporary = new TemporaryDirectory();
        var multiStore = new ConversationMemoryStore(multiTemporary.Path);
        MemoryCommitResult committed = multiStore.Commit(Identity, 0, new(0, multiple, "每种偏好都有自己的理由。"), Suggestions, updates, Enabled);
        Check(committed.Committed && committed.Notices.Count == 0 && multiStore.GetPreferences(Identity).Count == 3,
            "Mixed multi-preference input lost a continuation or rejected the batch.");
        foreach (string unsupported in new[]
        {
            "我喜欢雨天吗？", "你觉得我喜欢雨天吗？", "如果天气好，我喜欢雨天。",
            "她说，我喜欢雨天。", "我也许喜欢雨天，你呢？", "我喜欢晴天，你喜欢雨天吗？"
        })
            Check(LongTermPreferenceRules.ValidateBatch(new[] { rain with { Evidence = unsupported } }, unsupported) is not null,
                "A question, report or hypothetical was learned: " + unsupported);
        Check(LongTermPreferenceRules.ValidateBatch(new[] { rain }, "你喜欢雨天吗？") is not null,
            "Forged first-person evidence was accepted.");
        Check(LongTermPreferenceRules.ValidateBatch(new[] { rain with { Evidence = "还有她喜欢雨天" } },
            "我喜欢向日葵，还有她喜欢雨天。") is not null, "A new third-party subject inherited the player's identity.");

        MemoryCommitResult correction = multiStore.Commit(Identity, multiStore.GetRevision(Identity),
            new(0, "你最近还好吗？我现在最喜欢玫瑰，但仍喜欢向日葵。", "两种花都很美。"), Suggestions,
            new[] { Update("玫瑰", "REPLACE", evidence: "我现在最喜欢玫瑰"), Update(type: "like", evidence: "但仍喜欢向日葵") }, Enabled);
        Check(correction.Notices.Count == 0 && multiStore.GetPreferences(Identity).Any(p => p.Value == "玫瑰" && p.Type == "favorite")
            && multiStore.GetPreferences(Identity).Any(p => p.Value == "向日葵" && p.Type == "like"), "Question suppressed mixed correction.");
        string forget = "你今天有什么打算？忘记我最喜欢的玫瑰。";
        MemoryCommitResult forgotten = multiStore.Commit(Identity, multiStore.GetRevision(Identity), new(0, forget, "我会忘记这项偏好。"), Suggestions,
            new[] { Update("玫瑰", "FORGET", evidence: forget) }, Enabled);
        multiStore.Reload(Identity);
        Check(forgotten.Notices.Count == 0 && !multiStore.GetPreferences(Identity).Any(p => p.Value == "玫瑰")
            && !multiStore.GetSnapshot(Identity, Enabled).History.Any(t => t.PlayerMessage.Contains("玫瑰")),
            "Mixed forgetting did not remove the preference and filter its history.");
    }

    public static void ForgetFilteringAndScopes()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, Update());
        store.Record(Identity, new(1, "我最喜欢向日葵，也在编程。", "花和编程都很有趣。"), Suggestions);
        store.Record(Identity, new(1, "聊聊雕塑吧。", "这件木雕刚做好。"), Suggestions);
        store.Record(Identity, new(1, "我们换个话题。", "今天去了森林。"), new[] { "看看向日葵吧。", "继续聊木雕。", "森林很安静。" });
        string id = store.GetPreferences(Identity).Single().Id;
        string[] originalIds = store.GetTurns(Identity).Select(turn => turn.Id).ToArray();
        Check(store.Forget(Identity, id), "Single forget failed.");
        store.Reload(Identity);
        Check(store.GetTurns(Identity).Select(turn => turn.Id).SequenceEqual(originalIds), "Single forget destroyed raw history.");
        Check(store.GetSnapshot(Identity, Enabled).History.Single().PlayerMessage == "聊聊雕塑吧。", "Related mixed turns or historical suggestions leaked into request history.");
        Check(store.GetSuggestions(Identity) is null && store.GetPreferences(Identity).Count == 0, "Forget retained preference or next suggestions.");
        Learn(store, Update("玫瑰"));
        Learn(store, Update("雨天", type: "like", category: "weather", evidence: "我喜欢雨天"));
        Learn(store, Update("玫瑰", "FORGET", evidence: "忘记我最喜欢的玫瑰"));
        Check(!store.GetSnapshot(Identity, Enabled).History.Any(turn => turn.PlayerMessage.Contains("玫瑰")), "Current forgetting turn reintroduced the deleted value.");
        Learn(store, Update("百合"));
        Learn(store, Update("", "FORGET", type: "", category: "flower", evidence: "忘记我喜欢什么花", scope: "category"));
        Check(store.GetTurns(Identity).Count == 0 && store.GetPreferences(Identity).Single().Category == "weather", "Category forget erased another category or retained history.");
        Learn(store, Update("", "FORGET", type: "", category: "", evidence: "忘记我的所有偏好", scope: "all"));
        Check(store.GetPreferences(Identity).Count == 0 && store.GetTurns(Identity).Count == 0, "Full forget failed.");
        Learn(store, Update());
        store.Forget(Identity, store.GetPreferences(Identity).Single().Id);
        for (int index = 0; index < 7; index++)
            store.Record(Identity, new(index, "聊聊新的木雕。", "我准备换一种木材。"), Suggestions);
        ConversationMemory persisted = JsonSerializer.Deserialize<ConversationMemory>(File.ReadAllText(store.GetPath(Identity)))!;
        Check(persisted.ExcludedTurnIds.Count == 0, "Exclusion IDs outlived their rolling history.");
        Check(!store.Forget(Identity, Guid.NewGuid().ToString("N")), "Unknown ID deleted memory.");
        store.ForgetCategory(Identity, "flower");
        Check(store.GetTurns(Identity).Count == 0, "Admin category forget did not clear recent history.");
        Learn(store, Update());
        store.ClearAll(Identity);
        Check(store.GetTurns(Identity).Count == 0 && store.GetPreferences(Identity).Count == 0, "Admin clear did not clear all context.");
    }

    public static void WholeSentenceEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, Update());
        string source = "我现在最喜欢玫瑰，但仍喜欢向日葵。";
        MemoryCommitResult result = store.Commit(Identity, store.GetRevision(Identity), new(0, source, "两种花都很美。"), Suggestions,
            new[] { Update("玫瑰", "REPLACE", evidence: source), Update(type: "like", evidence: source) }, Enabled);
        Check(result.Notices.Count == 0 && store.GetPreferences(Identity).Any(value => value.Type == "favorite" && value.Value == "玫瑰")
            && store.GetPreferences(Identity).Any(value => value.Type == "like" && value.Value == "向日葵"), "Whole sentence evidence lost a valid matching clause.");
        Check(LongTermPreferenceRules.ValidateBatch(new[] { Update("玫瑰", type: "like", evidence: "我喜欢玫瑰") }, "她说，我喜欢玫瑰。") is not null,
            "Reported speech allowed a cropped self-reference.");
        Check(LongTermPreferenceRules.ValidateBatch(new[] { Update("玫瑰", type: "like", evidence: "我喜欢玫瑰") }, "如果天气好，我喜欢玫瑰。") is not null,
            "Conditional context allowed a cropped self-reference.");
        store.ClearAll(Identity);
        Learn(store, Update());
        result = store.Commit(Identity, store.GetRevision(Identity), new(0, source, "这是明确纠正后的回复。"), Suggestions,
            new[] { Update("玫瑰", evidence: "我现在最喜欢玫瑰"), Update(type: "like", evidence: "但仍喜欢向日葵") }, Enabled);
        Check(result.Notices.Contains("favorite_operation_corrected")
            && store.GetPreferences(Identity).Any(value => value.Type == "favorite" && value.Value == "玫瑰")
            && store.GetPreferences(Identity).Any(value => value.Type == "like" && value.Value == "向日葵"),
            "Explicit correction mislabelled ADD did not resolve to the existing favorite slot.");
        string forget = "忘记我最喜欢的玫瑰。";
        result = store.Commit(Identity, store.GetRevision(Identity), new(0, forget, "我会忘记玫瑰这项偏好。"), Suggestions,
            new[] { Update("玫瑰", "REMOVE", evidence: forget) }, Enabled);
        Check(result.Notices.Contains("forget_operation_corrected") && !store.GetPreferences(Identity).Any(value => value.Value == "玫瑰")
            && store.GetSuggestions(Identity) is null && !store.GetSnapshot(Identity, Enabled).History.Any(turn => turn.PlayerMessage.Contains("玫瑰")),
            "Explicit forgetting mislabelled REMOVE did not delete and filter the selected preference.");
        Learn(store, Update("下雨天", type: "like", category: "weather", evidence: "我喜欢下雨天"));
        string categoryForget = "忘记我喜欢什么花。";
        result = store.Commit(Identity, store.GetRevision(Identity), new(0, categoryForget, "我会忘记花的偏好。"), Suggestions,
            new[] { new MemoryUpdate("FORGET", "flower", null!, null!, categoryForget) }, Enabled);
        Check(result.Notices.Contains("forget_operation_corrected") && store.GetTurns(Identity).Count == 0
            && store.GetPreferences(Identity).Single().Category == "weather", "Explicit category forgetting with omitted scope was not resolved safely.");
        string allForget = "忘记我的全部偏好。";
        result = store.Commit(Identity, store.GetRevision(Identity), new(0, allForget, "我会清空记忆。"), Suggestions,
            new[] { new MemoryUpdate("FORGET", null!, null!, null!, allForget) }, Enabled);
        Check(result.Notices.Contains("forget_operation_corrected") && store.GetPreferences(Identity).Count == 0,
            "Explicit full forgetting with omitted scope was not resolved.");
        Learn(store, Update());
        result = store.Commit(Identity, store.GetRevision(Identity), new(0, "忘记玫瑰。", "好的，我明白了。"), Suggestions,
            new[] { new MemoryUpdate("FORGET", "flower", null!, null!, "忘记玫瑰。") }, Enabled);
        Check(result.Notices.Contains("invalid_update_fields") && store.GetPreferences(Identity).Single().Value == "向日葵",
            "Missing item target was broadened to a whole category without explicit scope evidence.");
    }

    public static void OptionsCapacityAndIsolation()
    {
        using var temporary = new TemporaryDirectory();
        long ticks = DateTimeOffset.Parse("2026-11-08T00:00:00Z").UtcTicks;
        var store = new ConversationMemoryStore(temporary.Path, () => new DateTimeOffset(ticks++, TimeSpan.Zero));
        Learn(store, Update(), Enabled with { Enabled = false });
        Learn(store, Update(), Enabled with { LearnNew = false });
        Check(store.GetPreferences(Identity).Count == 0, "Disabled learning added a preference.");
        Learn(store, Update(), Enabled with { MaximumPreferences = 1 });
        MemoryCommitResult full = Learn(store, Update("雨天", type: "like", category: "weather", evidence: "我喜欢雨天"), Enabled with { MaximumPreferences = 1 });
        Check(full.Notices.Contains("capacity_reached") && store.GetPreferences(Identity).Count == 1, "Capacity did not block new records.");
        Learn(store, Update("玫瑰", "REPLACE", evidence: "我现在最喜欢玫瑰"), Enabled with { AutoUpdate = false });
        Check(store.GetPreferences(Identity).Single().Value == "向日葵", "Disabled auto-update replaced preference.");
        Learn(store, Update("玫瑰", "REPLACE", evidence: "我现在最喜欢玫瑰"), Enabled with { LearnNew = false, MaximumPreferences = 1 });
        Check(store.GetPreferences(Identity).Single().Value == "玫瑰", "Correction was blocked by full capacity or disabled new learning.");
        Learn(store, Update("", "FORGET", type: "", category: "", evidence: "忘记我的全部偏好", scope: "all"), Enabled with { LearnNew = false, AutoUpdate = false });
        Check(store.GetPreferences(Identity).Count == 0, "Explicit forgetting was blocked by learning flags.");
        for (int index = 0; index < 35; index++)
            Learn(store, Update("花朵" + index, type: "like", evidence: "我喜欢花朵" + index), Enabled with { MaximumPreferences = 100 });
        Check(store.GetPreferences(Identity).Count == 35 && store.GetSnapshot(Identity, Enabled with { MaximumPreferences = 100 }).Preferences.Count == 32, "Input injection cap failed.");
        Check(store.GetSnapshot(Identity, Enabled with { MaximumPreferences = 2 }).Preferences.Count == 2 && store.GetPreferences(Identity).Count == 35, "Lowering capacity erased stored data.");
        Check(store.GetSnapshot(Identity, Enabled with { MaximumPreferences = 2 }).Preferences[0].Value == "花朵34", "Request selection did not prefer recently updated records.");
        Check(store.GetSnapshot(Identity, Enabled with { Enabled = false }).Preferences.Count == 0 && store.GetPreferences(Identity).Count == 35, "Disabled memory injected or erased stored preferences.");
        foreach (DialogueIdentity other in new[] { Identity with { SaveFolder = "OTHER" }, Identity with { PlayerId = 99 }, Identity with { NpcName = "Sebastian" } })
        {
            Check(store.GetPreferences(other).Count == 0, "Long memory leaked across identities.");
            store.Commit(other, 0, new(0, "我最喜欢玫瑰", "玫瑰确实很美。"), Suggestions, new[] { Update("玫瑰") }, Enabled);
        }
        store.Clear();
        Check(store.GetPreferences(Identity).Count == 35, "Another identity changed the original file.");
        Check(store.GetPreferences(Identity with { PlayerId = 99 }).Single().Value == "玫瑰", "Independent preferences did not survive reload.");
        Check((new LongTermMemoryOptions { MaximumPreferences = -1 }).Normalize().MaximumPreferences == 1
            && (new LongTermMemoryOptions { MaximumPreferences = 101 }).Normalize().MaximumPreferences == 100, "Capacity normalization failed.");
    }

    public static void FailureRecoveryAndCommands()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, Update());
        string path = store.GetPath(Identity);
        byte[] committed = File.ReadAllBytes(path);
        long revision = store.GetRevision(Identity);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws<IOException>(() => Learn(store, Update("玫瑰", "REPLACE", evidence: "我现在最喜欢玫瑰")));
        Check(File.ReadAllBytes(path).SequenceEqual(committed), "Failed write changed committed preference bytes.");
        Throws<IOException>(() => store.GetPreferences(Identity));
        store.Reload(Identity);
        Check(store.GetPreferences(Identity).Single().Value == "向日葵" && store.GetRevision(Identity) == revision, "Reload exposed an uncommitted replacement.");
        ConversationMemory data = JsonSerializer.Deserialize<ConversationMemory>(File.ReadAllText(path))!;
        data.LongTermPreferences[0] = data.LongTermPreferences[0] with { Confidence = 0.5 };
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        store.Reload(Identity);
        Throws<IOException>(() => store.GetPreferences(Identity));
        File.WriteAllBytes(path, committed);
        Throws<IOException>(() => store.GetPreferences(Identity));
        store.Reload(Identity);
        Check(store.GetPreferences(Identity).Count == 1, "Explicit reload failed to recover corrected file.");
        data = JsonSerializer.Deserialize<ConversationMemory>(File.ReadAllText(path))!;
        data.LongTermPreferences.Add(data.LongTermPreferences[0] with { Id = Guid.NewGuid().ToString("N") });
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        store.Reload(Identity);
        Throws<IOException>(() => store.GetPreferences(Identity));
        File.WriteAllBytes(path, committed);
        store.Reload(Identity);
        string missingRevision = File.ReadAllText(path).Replace("\"Revision\":1,", "", StringComparison.Ordinal);
        File.WriteAllText(path, missingRevision);
        store.Reload(Identity);
        Throws<IOException>(() => store.GetPreferences(Identity));
        File.WriteAllBytes(path, committed);
        store.Reload(Identity);
        Throws<InvalidDataException>(() => AtomicJsonFile.Write(path, new { Text = new string('字', 100) }, 16));
        Check(File.ReadAllBytes(path).SequenceEqual(committed) && !Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(), "Oversized write replaced a valid file or leaked a temporary file.");
        Check(MemoryCommand.Parse(new[] { "list" }) == new MemoryCommand("list", "Abigail", null), "List default NPC mismatch.");
        Check(MemoryCommand.Parse(new[] { "reload" }) == new MemoryCommand("reload", null, null), "Reload-all parsing failed.");
        Check(MemoryCommand.Parse(new[] { "forget", "Leah", "id" })?.Value == "id"
            && MemoryCommand.Parse(new[] { "clear", "Leah" })?.Action == "clear"
            && MemoryCommand.Parse(new[] { "forget-category", "Leah", "flower" })?.Value == "flower", "Management command parsing failed.");
        foreach (string[] invalid in new[] { Array.Empty<string>(), new[] { "unknown" }, new[] { "clear" }, new[] { "list", "Leah", "extra" }, new[] { "forget-category", "Leah", "invalid" } })
            Check(MemoryCommand.Parse(invalid) is null, "Invalid command was accepted.");
        using var legacyDirectory = new TemporaryDirectory();
        var legacyStore = new ConversationMemoryStore(legacyDirectory.Path);
        string legacyPath = legacyStore.GetPath(Identity);
        string legacy = JsonSerializer.Serialize(new { Version = 1, Identity, Turns = new[] { new ConversationTurn(0, "你好", "角色回复。") } });
        File.WriteAllText(legacyPath, legacy);
        legacyStore.GetRevision(Identity);
        Directory.CreateDirectory(legacyPath + ".v1.bak");
        Throws<IOException>(() => Learn(legacyStore, Update()));
        Check(File.ReadAllText(legacyPath) == legacy, "Backup failure changed the original v1 file.");
        Directory.Delete(legacyPath + ".v1.bak");
        legacyStore.Reload(Identity);
        Check(Learn(legacyStore, Update()).Committed && File.ReadAllText(legacyPath + ".v1.bak") == legacy, "Backup retry did not recover safely.");
        Check(JsonSerializer.Deserialize<DeepSeekOptions>("{\"MaxTokens\":512}")!.MaxTokens == 512,
            "Existing explicit output budget was overwritten by new defaults.");
    }

    public static void MixedRequestReplay() => MixedRequestReplayAsync().GetAwaiter().GetResult();
    public static void RequestReplay() => RequestReplayAsync().GetAwaiter().GetResult();
    public static void CancelledAndReversedResponses() => CancelledAndReversedResponsesAsync().GetAwaiter().GetResult();

    private static async Task MixedRequestReplayAsync()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        string message = "你好，你在这里住得习惯吗？我喜欢雨天。";
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            JsonElement messages = body.RootElement.GetProperty("messages");
            using JsonDocument current = JsonDocument.Parse(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!);
            Check(current.RootElement.GetProperty("PlayerMessage").GetString() == message, "Mixed input was cropped before sending.");
            calls++;
            if (calls == 1)
                return Response("我也喜欢雨声。", Suggestions,
                    new[] { Update("雨天", type: "like", category: "weather", evidence: "我喜欢雨天。") });
            Check(current.RootElement.GetProperty("LongTermPreferences")[0].GetProperty("Value").GetString() == "雨天",
                "Reloaded mixed preference did not enter the next request.");
            // Missing updates must not invent facts or trigger a second model extraction request.
            return Raw(new { dialogue = "雨声确实很安静。", suggestions = Suggestions, friendshipTier = 0 });
        }));
        var client = new DeepSeekClient(http);
        for (int index = 0; index < 2; index++)
        {
            ConversationSnapshot snapshot = store.GetSnapshot(Identity, Enabled);
            var request = new AiDialogueRequest("Krobus", "FarmHouse", "spring", 1, 900, "sun", 4, message)
                { History = snapshot.History, LongTermPreferences = snapshot.Preferences, MemoryRevision = snapshot.Revision };
            AiDialogueResult reply = await client.GenerateAsync(AiOptions, request, default);
            Check(reply.Text is not null && reply.MemoryUpdateFailure is null, "Mixed reply was rejected.");
            MemoryCommitResult result = store.Commit(Identity, snapshot.Revision, new(0, message, reply.Text!),
                reply.Suggestions, reply.MemoryUpdates, Enabled);
            Check(result.Committed && result.Notices.Count == 0, "Parsed mixed update did not commit.");
            store.Reload(Identity);
        }
        Check(calls == 2 && store.GetPreferences(Identity).Single().Value == "雨天", "Missing update invented a fact or extra call.");
    }

    private static async Task RequestReplayAsync()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            JsonElement messages = body.RootElement.GetProperty("messages");
            if (calls == 1)
            {
                string system = messages[0].GetProperty("content").GetString()!;
                Check(system.Contains("普通“喜欢”必须使用 like"), "Prompt does not distinguish ordinary likes from favorites.");
                Check(!system.Contains("向日葵") && !system.Contains("玫瑰") && !system.Contains("南瓜"), "Prompt examples contain concrete test preferences.");
            }
            using JsonDocument context = JsonDocument.Parse(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!);
            JsonElement state = context.RootElement;
            if (calls == 8)
            {
                Check(state.GetProperty("LongTermPreferences")[0].GetProperty("Value").GetString() == "向日葵", "Eighth request lost a rolled-out preference.");
                Check(!messages.EnumerateArray().Skip(1).Take(messages.GetArrayLength() - 2).Any(message => message.GetProperty("content").GetString()!.Contains("我最喜欢向日葵")), "Original message remained in short history during long-memory assertion.");
            }
            if (calls == 10)
                Check(state.GetProperty("LongTermPreferences")[0].GetProperty("Value").GetString() == "玫瑰", "Corrected preference missing from next request.");
            if (calls == 11)
                Check(!state.TryGetProperty("LongTermPreferences", out _) && !state.TryGetProperty("MemoryLearning", out _), "Disabled memory was still injected.");
            MemoryUpdate[]? updates = calls switch { 1 => new[] { Update() }, 9 => new[] { Update("玫瑰", "REPLACE", evidence: "我现在最喜欢玫瑰") }, _ => null };
            return Response($"这是自然接续当前话题的第{calls}轮回复。", Suggestions, updates);
        }));
        var client = new DeepSeekClient(http);
        for (int round = 1; round <= 11; round++)
        {
            LongTermMemoryOptions options = Enabled with { Enabled = round != 11 };
            ConversationSnapshot snapshot = store.GetSnapshot(Identity, options);
            string message = round switch { 1 => "我最喜欢向日葵", 9 => "我现在最喜欢玫瑰", _ => "最近天气怎么样？" };
            var request = new AiDialogueRequest("Leah", "Town", "spring", round, 900, "sun", 4, message)
                { History = snapshot.History, LongTermPreferences = snapshot.Preferences, MemoryOptions = options,
                    MemoryRevision = snapshot.Revision, RequestId = Guid.NewGuid() };
            AiDialogueResult result = await client.GenerateAsync(AiOptions, request, default);
            Check(result.Text is not null && result.MemoryUpdateFailure is null,
                $"Simulated response parsing failed at round {round}: {result.Failure}, memory={result.MemoryUpdateFailure}.");
            MemoryCommitResult committed = store.Commit(Identity, snapshot.Revision, new(round - 1, message, result.Text!), result.Suggestions, result.MemoryUpdates, options);
            Check(committed.Committed && committed.Notices.Count == 0,
                $"Replay commit failed at round {round}: {string.Join(',', committed.Notices)}.");
            store.Reload(Identity);
        }
        Check(calls == 11 && store.GetPreferences(Identity).Single().Value == "玫瑰", "End-to-end replay lost stored preference.");

        int repairs = 0;
        using var repairHttp = new HttpClient(new Handler(_ =>
        {
            repairs++;
            return Task.FromResult(repairs == 1
                ? Response("保留首次回复和更新。", null, new[] { Update() })
                : Response("补全返回的另一句回复。", Suggestions, new[] { Update("玫瑰") }));
        }));
        AiDialogueResult repaired = await new DeepSeekClient(repairHttp).GenerateAsync(AiOptions,
            new("Leah", "Town", "spring", 1, 900, "sun", 4, "我最喜欢向日葵"), default);
        Check(repaired.MemoryUpdates?.Single().Value == "向日葵" && repaired.SuggestionsRepaired && repairs == 2, "Suggestion repair replaced first-response memory updates.");
        foreach (object invalid in new object[] { "invalid", 1, new[] { Update(), Update(), Update(), Update() } })
        {
            using var invalidHttp = new HttpClient(new Handler(_ => Task.FromResult(Raw(new { dialogue = "有效回复仍可展示。", suggestions = Suggestions, memoryUpdates = invalid }))));
            AiDialogueResult result = await new DeepSeekClient(invalidHttp).GenerateAsync(AiOptions, new("Leah", "Town", "spring", 1, 900, "sun", 4, "你好"), default);
            Check(result.Text is not null && result.MemoryUpdates is null && result.MemoryUpdateFailure is not null, "Invalid memory field discarded valid dialogue.");
        }
        string protectedId = store.GetPreferences(Identity).Single().Id;
        using var forgedIdHttp = new HttpClient(new Handler(_ => Task.FromResult(Raw(new
        {
            dialogue = "我明白这次遗忘要求。", suggestions = Suggestions,
            memoryUpdates = new[] { new { operation = "FORGET", category = "flower", type = "favorite", value = "向日葵",
                evidence = "忘记我最喜欢的向日葵", scope = "item", id = protectedId } }
        }))));
        var forgetRequest = new AiDialogueRequest("Leah", "Town", "spring", 12, 900, "sun", 4, "忘记我最喜欢的向日葵");
        AiDialogueResult forgotten = await new DeepSeekClient(forgedIdHttp).GenerateAsync(AiOptions, forgetRequest, default);
        store.Commit(Identity, store.GetRevision(Identity), new(11, forgetRequest.PlayerMessage, forgotten.Text!),
            forgotten.Suggestions, forgotten.MemoryUpdates, Enabled);
        Check(store.GetPreferences(Identity).Single().Id == protectedId && store.GetPreferences(Identity).Single().Value == "玫瑰",
            "Model-provided memory ID redirected deletion away from the explicit category/type/value selector.");
    }

    private static async Task CancelledAndReversedResponsesAsync()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        var controlled = new ControlledClient();
        var queue = new AiDialogueQueue(controlled);
        ConversationSnapshot original = store.GetSnapshot(Identity, Enabled);
        var request = new AiDialogueRequest("Leah", "Town", "spring", 1, 900, "sun", 4, "我最喜欢向日葵")
            { MemoryRevision = original.Revision, RequestId = Guid.NewGuid() };
        queue.TryStart(AiOptions, request, Scene);
        Check(queue.CurrentRequestId == request.RequestId && !queue.TryStart(AiOptions, request, Scene), "Request identity or single-flight protection failed.");
        queue.Cancel();
        store.ClearAll(Identity);
        long afterClear = store.GetRevision(Identity);
        var next = request with { RequestId = Guid.NewGuid(), MemoryRevision = afterClear, PlayerMessage = "我最喜欢玫瑰" };
        queue.TryStart(AiOptions, next, Scene);
        controlled.Responses[1].SetResult(new("角色的新回复。", null) { MemoryUpdates = new[] { Update("玫瑰") } });
        AiDialogueResult? result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!queue.TryTake(Scene, true, out result))
            await Task.Delay(1, timeout.Token);
        Check(store.Commit(Identity, next.MemoryRevision, new(0, next.PlayerMessage, result!.Text!), Suggestions, result.MemoryUpdates, Enabled).Committed, "New request failed to commit.");
        controlled.Responses[0].SetResult(new("过期的角色回复。", null) { MemoryUpdates = new[] { Update() } });
        Check(!queue.TryTake(Scene, true, out _) && !store.Commit(Identity, original.Revision, new(0, request.PlayerMessage, "过期的回复。"), Suggestions, new[] { Update() }, Enabled).Committed,
            "Cancelled old response was consumed or passed revision validation.");
        Check(store.GetPreferences(Identity).Single().Value == "玫瑰", "Old response overwrote the latest memory.");
        queue.TryStart(AiOptions, next with { RequestId = Guid.Empty }, Scene);
        Check(queue.CurrentRequestId != Guid.Empty, "Queue did not generate a request identity for existing callers.");
        queue.Cancel();
        store.Reload(Identity);
        Check(store.GetPreferences(Identity).Single().Value == "玫瑰", "Reload lost committed memory.");
        controlled.Responses[2].SetResult(new("已取消的回复。", null));
    }

    private static HttpResponseMessage Response(string text, string[]? suggestions, MemoryUpdate[]? updates)
        => Raw(new { dialogue = text, suggestions, memoryUpdates = updates ?? Array.Empty<MemoryUpdate>(), friendshipTier = 1 });
    private static HttpResponseMessage Raw(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new
            { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(payload) } } } }), Encoding.UTF8, "application/json")
    };
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class ControlledClient : IAiDialogueClient
    {
        public List<TaskCompletionSource<AiDialogueResult>> Responses { get; } = new();
        public Task<AiDialogueResult> GenerateAsync(DeepSeekOptions options, AiDialogueRequest request, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<AiDialogueResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Responses.Add(result);
            return result.Task;
        }
    }
}

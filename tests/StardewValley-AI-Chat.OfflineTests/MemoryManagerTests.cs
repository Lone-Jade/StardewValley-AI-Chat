using System.Text.Json;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class MemoryManagerTests
{
    private static readonly DialogueIdentity Identity = new("TEST_123", 1, "Penny");
    private static readonly string[] Suggestions = { "聊聊今天吧。", "我也喜欢安静。", "最近心情不错。" };
    private static readonly LongTermMemoryOptions Options = new() { MaximumPreferences = 100 };

    private static void Learn(ConversationMemoryStore store, string value, string category = "flower", string type = "like")
    {
        string source = "我" + (type == "favorite" ? "最喜欢" : type == "dislike" ? "不喜欢" : "喜欢") + value + "。";
        MemoryCommitResult result = store.Commit(Identity, store.GetRevision(Identity), new(0, source, "我记住你的偏好了。"),
            Suggestions, new[] { new MemoryUpdate("ADD", category, type, value, source) }, Options);
        Check(result.Committed && result.Notices.Count == 0, "Fixture learning failed.");
    }

    public static void ManualCorrection()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, "向日葵", type: "favorite");
        store.Record(Identity, new(0, "我今天去河边散步。", "河边很适合散步。"), Suggestions);
        store.Record(Identity, new(0, "你喜欢玫瑰吗？", "玫瑰也很美。"), Suggestions);
        LongTermPreference original = store.GetPreferences(Identity).Single();
        long revision = store.GetRevision(Identity);
        int invalidations = 0;
        var model = new MemoryManagerModel(store, Identity, 2, () => invalidations++);
        model.Save("flower", "favorite", "玫瑰", "active");
        LongTermPreference corrected = model.Selected!;
        Check(!model.IsError && invalidations == 1 && model.Revision == revision + 1, "Correction did not invalidate and refresh.");
        Check(corrected.Id == original.Id && corrected.CreatedAt == original.CreatedAt && corrected.LearnedDay == original.LearnedDay
            && corrected.UpdatedDay == 2 && corrected.ManuallyEdited && corrected.Evidence == original.Evidence, "Correction lost identity or source provenance.");
        Check(store.GetTurns(Identity).Count == 3 && store.GetSuggestions(Identity) is null, "Manual correction changed raw turn count or retained suggestions.");
        ConversationSnapshot snapshot = store.GetSnapshot(Identity, Options);
        Check(snapshot.History.Count == 1 && snapshot.History[0].PlayerMessage.Contains("河边")
            && snapshot.Preferences.Single().Value == "玫瑰", "Old and new conflicting value turns were not filtered.");
        store.Reload(Identity);
        Check(store.GetPreferences(Identity).Single() == corrected && store.GetSnapshot(Identity, Options).History.Count == 1,
            "Manual source flag and filtering did not survive reload.");
        Check(store.GetPreferences(Identity with { NpcName = "CustomNpc" }).Count == 0, "Manual editing leaked to another NPC.");
        string source = "我现在最喜欢郁金香。";
        store.Commit(Identity, store.GetRevision(Identity), new(2, source, "我会记住的。"), Suggestions,
            new[] { new MemoryUpdate("REPLACE", "flower", "favorite", "郁金香", source) }, Options);
        LongTermPreference learned = store.GetPreferences(Identity).Single();
        Check(!learned.ManuallyEdited && learned.Evidence == source, "Later model evidence retained a stale manual provenance flag.");
    }

    public static void ConflictsAndStaleViews()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, "向日葵", type: "favorite");
        Learn(store, "玫瑰");
        Learn(store, "南瓜", category: "food", type: "dislike");
        var model = new MemoryManagerModel(store, Identity, 3, () => { });
        string roseId = store.GetPreferences(Identity).Single(entry => entry.Value == "玫瑰").Id;
        model.Select(roseId);
        long revision = model.Revision;
        model.Save("flower", "favorite", "玫瑰", "active");
        Check(model.IsError && model.Revision == revision && model.Message.Contains("已存在"), "Conflicting favorite was overwritten.");
        model.Save("flower", "like", "", "active");
        Check(model.IsError && model.Revision == revision, "Invalid empty value was committed.");
        model.Save("food", "like", "南瓜", "active");
        Check(!model.IsError && store.GetPreferences(Identity).Single(entry => entry.Type == "dislike").Status == "retracted",
            "Opposite stance was left active.");
        model.Save("food", "like", "南瓜", "retracted");
        Check(!model.IsError && model.Selected!.Status == "retracted", "Manual retraction failed.");
        model.Save("food", "like", "南瓜", "active");
        Check(!model.IsError && model.Selected!.Status == "active", "Manual reactivation failed.");
        Learn(store, "雨天", "weather");
        long latest = store.GetRevision(Identity);
        model.ClearAll();
        Check(model.IsError && model.Revision == latest && store.GetPreferences(Identity).Count == 4,
            "Stale clear erased unseen entries.");
        model.ClearAll();
        Check(!model.IsError && model.Count == 0 && store.GetTurns(Identity).Count == 0, "Refreshed clear failed.");
    }

    public static void PagingAndForgetting()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        for (int index = 0; index < 8; index++) Learn(store, "花朵" + index);
        Learn(store, "雨天", "weather");
        int invalidations = 0;
        var model = new MemoryManagerModel(store, Identity, 1, () => invalidations++);
        Check(model.PageCount == 2 && model.Visible.Count == 6 && model.Count == 9, "Initial pagination failed.");
        model.ChangePage(1);
        Check(model.Page == 1 && model.Visible.Count == 3 && model.SelectedId == model.Visible[0].Id, "Page selection failed.");
        model.SetFilter("weather");
        Check(model.Page == 0 && model.Visible.Count == 1 && model.Selected!.Category == "weather", "Category filter failed.");
        IReadOnlyList<string> navigation = model.GetNavigationKeys(false);
        Check(navigation.Contains("input") && !navigation.Contains("evidence") && !navigation.Contains("row1")
            && !navigation.Contains("previous") && !navigation.Contains("next"), "Focus includes hidden or disabled controls.");
        model.ForgetSelected();
        Check(!model.IsError && model.Count == 8 && model.Selected is null && store.GetSuggestions(Identity) is null,
            "Single forget did not refresh empty filter.");
        model.SetFilter(null);
        model.ForgetCategory("flower");
        Check(!model.IsError && model.Count == 0 && model.PageCount == 1 && store.GetTurns(Identity).Count == 0,
            "Category forget did not clear related context.");
        model.Reload();
        Check(model.IsAvailable && !model.IsError && invalidations == 3, "Reload did not invalidate and refresh.");
        Check(model.GetNavigationKeys(false).SequenceEqual(new[] { "filter", "clear", "reload", "close" }),
            "Empty manager navigation includes invisible editors.");
    }

    public static void FailureAndContext()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, "向日葵");
        string path = store.GetPath(Identity);
        byte[] bytes = File.ReadAllBytes(path);
        int errors = 0;
        var model = new MemoryManagerModel(store, Identity, 1, () => { }, _ => errors++);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            model.Save("flower", "like", "玫瑰", "active");
        Check(model.IsError && !model.IsAvailable && errors == 1 && model.Selected!.Value == "向日葵"
            && File.ReadAllBytes(path).SequenceEqual(bytes), "Failed save exposed or persisted an uncommitted edit.");
        model.Reload();
        Check(model.IsAvailable && !model.IsError && model.Selected!.Value == "向日葵", "Recovery reload failed.");
        var expired = new MemoryManagerModel(store, Identity, 1, () => throw new InvalidOperationException());
        expired.ClearAll();
        expired.Reload();
        Check(expired.IsError && File.ReadAllBytes(path).SequenceEqual(bytes), "Expired menu context changed data.");
        File.WriteAllText(path, "not json");
        model.Reload();
        Check(!model.IsAvailable && model.IsError && errors == 2, "Corrupt record did not disable manager or log failure.");
        File.WriteAllBytes(path, bytes);
        model.Reload();
        Check(model.IsAvailable && !model.IsError, "Corrected file did not recover manager.");
    }

    public static void MigrationAndRevision()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Learn(store, "向日葵");
        ConversationMemory memory = JsonSerializer.Deserialize<ConversationMemory>(File.ReadAllText(store.GetPath(Identity)))!;
        string legacyV2 = File.ReadAllText(store.GetPath(Identity)).Replace(",\"ManuallyEdited\":false", "");
        Check(!legacyV2.Contains("\"ManuallyEdited\""), "Older v2 fixture still contains the new field.");
        File.WriteAllText(store.GetPath(Identity), legacyV2);
        store.Reload(Identity);
        Check(!store.GetPreferences(Identity).Single().ManuallyEdited, "Older v2 records require a new optional field.");
        long revision = store.GetRevision(Identity);
        string id = memory.LongTermPreferences.Single().Id;
        store.ClearSuggestions(Identity);
        MemoryCommitResult stale = store.EditPreference(Identity, revision, id, "flower", "like", "玫瑰", "active", 1);
        Check(!stale.Committed && stale.Notices.Contains("stale_revision") && store.GetPreferences(Identity).Single().Value == "向日葵",
            "Store accepted stale administrative edit.");
        foreach (string invalid in new[] { "", " ", new string('字', 81), "花\n朵", "$action" })
        {
            MemoryCommitResult result = store.EditPreference(Identity, store.GetRevision(Identity), id, "flower", "like", invalid, "active", 1);
            Check(!result.Committed && result.Notices.Contains("invalid_fields"), "Invalid manual value accepted.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

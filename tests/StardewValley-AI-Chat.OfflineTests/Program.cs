using System.Text.Json;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class Program
{
    private static readonly DialogueIdentity Abigail = new("TEST_123", 100, "Abigail");
    private static readonly DialogueSafety Safe = new(true, true, false, false, false, false);
    private static readonly DialogueContext Sunny = new("sun", 0);
    private static readonly HashSet<string> NoneUsed = new();

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("shipped dialogue data", TestShippedData),
            ("nearby facing target selection", TestTargeting),
            ("three NPC pools and friendship boundaries", TestThreePools),
            ("pool validation and missing files", TestValidation),
            ("weather and inclusive hearts conditions", TestConditions),
            ("weighted interval boundaries and large totals", TestWeights),
            ("same-day exhaustion and next-day reset", TestExhaustion),
            ("reload and identity isolation", TestReloadAndIsolation),
            ("corrupt history disables only its record", TestCorruptHistory),
            ("history write failure retains committed file", TestWriteFailure),
            ("first history write failure disables record", TestFirstWriteFailure),
            ("history read lock disables record until reload", TestReadFailure),
            ("all story guards skip without consumption", TestGuards),
            ("display failure does not consume dialogue", TestDisplayFailure),
            ("AI configuration and no-call defaults", AiTests.Configuration),
            ("DeepSeek request format and context", AiTests.RequestFormat),
            ("AI JSON and literal text validation", AiTests.ResponseValidation),
            ("AI HTTP failure, size, timeout and cancellation", AiTests.FailureHandling),
            ("AI single-flight, stale context and cancellation", AiTests.QueueLifecycle),
            ("generated dialogue guards and persistent deduplication", AiTests.GeneratedHistory),
            ("three contextual dialogue suggestions", ComposerTests.Suggestions),
            ("suggestion editing and single submission", ComposerTests.SelectionAndEditing),
            ("free input and composer validation", ComposerTests.InputValidation),
            ("composer cancellation and retry lifecycle", ComposerTests.CloseAndRetry),
            ("six-turn memory retention and history filename compatibility", MemoryTests.RetentionAndReload),
            ("conversation memory identity isolation", MemoryTests.Isolation),
            ("conversation memory corruption and recovery", MemoryTests.CorruptionAndRecovery),
            ("conversation memory atomic write failure", MemoryTests.WriteFailure),
            ("conversation memory first write and read failure", MemoryTests.FirstWriteAndReadFailure),
            ("persisted next suggestions and fallback", MemoryTests.SuggestionFallback),
            ("invalid memory turns and caller isolation", MemoryTests.InvalidTurnDoesNotMutate),
            ("DeepSeek bounded conversation messages", AiTests.MemoryRequest),
            ("dynamic suggestions and partial response fallback", AiTests.DynamicSuggestions),
            ("suggestion repair and friendship score contract", AiTests.SuggestionRepair),
            ("daily limits, partners and save lifecycle", DailyConversationTests.LimitsAndReset),
            ("first response rewards and corrupt daily state", DailyConversationTests.RewardsAndCorruption),
            ("v2 migration, backup and preference retention", LongMemoryTests.MigrationAndRetention),
            ("long preference correction, retraction and conflict rules", LongMemoryTests.OperationsAndConflicts),
            ("memory evidence and all-or-nothing validation", LongMemoryTests.EvidenceAndAtomicBatch),
            ("mixed questions, statements, corrections and forgetting", LongMemoryTests.MixedInputEvidence),
            ("mixed input API parsing, persistence and request replay", LongMemoryTests.MixedRequestReplay),
            ("single, category and full forget history filtering", LongMemoryTests.ForgetFilteringAndScopes),
            ("memory switches, capacity and identity isolation", LongMemoryTests.OptionsCapacityAndIsolation),
            ("memory storage failure, reload and management commands", LongMemoryTests.FailureRecoveryAndCommands),
            ("eleven-request long memory replay and suggestion repair", LongMemoryTests.RequestReplay),
            ("cancelled reversed responses and memory revisions", LongMemoryTests.CancelledAndReversedResponses),
            ("whole-sentence evidence and protected clause context", LongMemoryTests.WholeSentenceEvidence),
            ("manual preference correction and source preservation", MemoryManagerTests.ManualCorrection),
            ("manual conflicts and stale management views", MemoryManagerTests.ConflictsAndStaleViews),
            ("memory manager paging, filtering and forgetting", MemoryManagerTests.PagingAndForgetting),
            ("memory manager write failures and expired context", MemoryManagerTests.FailureAndContext),
            ("manual edit revisions and older v2 compatibility", MemoryManagerTests.MigrationAndRevision),
            ("all NPC generic pool and per-character override", AllNpcTests.FallbackPools),
            ("all NPC request identity and game profile", AllNpcTests.GenericRequests),
            ("47 vanilla personas and exact internal identities", PersonaTests.Catalog),
            ("vanilla persona validation and duplicate rejection", PersonaTests.InvalidCatalog),
            ("native relationship and roommate state", PersonaTests.Relationships),
            ("persona request isolation and runtime precedence", PersonaTests.Requests),
            ("public provider templates and legacy configuration", ProviderTests.TemplatesAndMigration),
            ("seven provider request and response contracts", ProviderTests.ProviderRequests),
            ("Claude blocks, truncation and refusal handling", ProviderTests.ClaudeValidation),
            ("Kimi bounded context and suggestion repair", ProviderTests.KimiRepair),
            ("public daily-state namespace isolation", ProviderTests.DailyKeyIsolation)
        };

        int failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {error}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} test groups passed.");
        return failures == 0 ? 0 : 1;
    }

    private static DialoguePool Pool(params DialogueEntry[] entries)
    {
        var pool = new DialoguePool { Dialogues = entries.ToList() };
        pool.Validate();
        return pool;
    }

    private static void TestTargeting()
    {
        Check(DialogueTargeting.IsInReach(0, -1, 0), "Up target missing.");
        Check(DialogueTargeting.IsInReach(1, 0, 1), "Right target missing.");
        Check(DialogueTargeting.IsInReach(0, 1, 2), "Down target missing.");
        Check(DialogueTargeting.IsInReach(-1, 0, 3), "Left target missing.");
        Check(DialogueTargeting.IsInReach(1, 1, 1), "Facing diagonal missing.");
        Check(DialogueTargeting.IsInReach(2, 0, 1), "Reach boundary missing.");
        Check(!DialogueTargeting.IsInReach(2.01f, 0, 1), "Out-of-reach target accepted.");
        Check(!DialogueTargeting.IsInReach(-1, 0, 1), "Behind target accepted.");
        Check(!DialogueTargeting.IsInReach(0, 1, 1), "Side target accepted.");
        Check(!DialogueTargeting.IsInReach(0, 0, 1), "Overlapping target accepted.");
        Check(!DialogueTargeting.IsInReach(1, 0, 9), "Invalid facing accepted.");
    }

    private static void TestThreePools()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        var engine = new DialogueEngine(history, new FixedRandom(0));
        foreach (string npc in new[] { "Abigail", "Sebastian", "Leah" })
        {
            DialoguePool pool = DialoguePool.Load(Path.Combine(AppContext.BaseDirectory, npc + ".json"));
            var identity = Abigail with { NpcName = npc };
            foreach (int hearts in new[] { 0, 3, 4, 10 })
            {
                foreach (string weather in new[] { "sun", "rain" })
                {
                    var context = new DialogueContext(weather, hearts);
                    DialogueEntry? selected = engine.Select(pool, context, NoneUsed);
                    Check(selected is not null, $"No dialogue for {npc}/{weather}/{hearts}.");
                    Check(pool.Dialogues.Any(line => line.Weather == weather), "Weather content missing.");
                }
            }
            history.RecordShown(identity, 1, "general_01");
            Check(history.GetUsed(identity, 1).Contains("general_01"), "NPC history missing.");
        }
        Check(Directory.EnumerateFiles(temporary.Path, "*.json").Count() == 3, "NPC history files were not isolated.");
    }

    private static DialogueEntry Line(string id, int weight = 1)
    {
        return new DialogueEntry { Id = id, Text = "测试对话", Weight = weight };
    }

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

    private static void TestShippedData()
    {
        DialoguePool pool = DialoguePool.Load(Path.Combine(AppContext.BaseDirectory, "Abigail.json"));
        Check(pool.Dialogues.Count == 11, "Expected all shipped dialogue entries.");
        Check(pool.Dialogues.Any(entry => entry.Weather == "rain" && entry.MinHearts.HasValue),
            "Expected combined weather and friendship condition.");
    }

    private static void TestValidation()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "pool.json");
        string[] invalid =
        {
            "null", "{}", "{", "{\"dialogues\":null}", "{\"dialogues\":[null]}",
            "{\"dialogues\":[{\"id\":\"\",\"text\":\"x\",\"weight\":1}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\" \",\"weight\":1}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":0}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":-1}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":1.5}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":1,\"weather\":\"hail\"}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":1,\"minHearts\":-1}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":1,\"maxHearts\":-1}]}",
            "{\"dialogues\":[{\"id\":\"a\",\"text\":\"x\",\"weight\":1,\"minHearts\":5,\"maxHearts\":4}]}"
        };
        foreach (string json in invalid)
        {
            File.WriteAllText(path, json);
            bool rejected = false;
            try { DialoguePool.Load(path); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { rejected = true; }
            Check(rejected, $"Accepted invalid pool {json}");
        }
        Throws<InvalidDataException>(() => Pool(Line("duplicate"), Line("duplicate")));
        Throws<FileNotFoundException>(() => DialoguePool.Load(Path.Combine(temporary.Path, "missing.json")));
    }

    private static void TestConditions()
    {
        using var temporary = new TemporaryDirectory();
        var engine = new DialogueEngine(new DialogueHistoryStore(temporary.Path), new FixedRandom(0));
        var rainFriend = Line("rain-friend");
        rainFriend.Weather = "rain";
        rainFriend.MinHearts = 4;
        rainFriend.MaxHearts = 6;
        DialoguePool pool = Pool(rainFriend);
        Check(engine.Select(pool, new("rain", 4), NoneUsed)?.Id == "rain-friend", "Lower boundary failed.");
        Check(engine.Select(pool, new("rain", 6), NoneUsed)?.Id == "rain-friend", "Upper boundary failed.");
        Check(engine.Select(pool, new("rain", 3), NoneUsed) is null, "Below minimum matched.");
        Check(engine.Select(pool, new("rain", 7), NoneUsed) is null, "Above maximum matched.");
        Check(engine.Select(pool, new("sun", 4), NoneUsed) is null, "Wrong weather matched.");
        foreach (string weather in new[] { "sun", "rain", "storm", "snow", "wind", "green_rain" })
        {
            var entry = Line(weather);
            entry.Weather = weather;
            Check(engine.Select(Pool(entry), new(weather, 0), NoneUsed)?.Id == weather, "Weather failed.");
        }
        Check(engine.Select(Pool(Line("general")), new("snow", 14), NoneUsed)?.Id == "general", "Unconditional entry failed.");
        Check(engine.Select(new DialoguePool(), Sunny, NoneUsed) is null, "Empty candidates should return null.");
    }

    private static void TestWeights()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        DialoguePool pool = Pool(Line("a", 2), Line("b", 3));
        foreach (var sample in new[] { (0L, "a"), (1L, "a"), (2L, "b"), (4L, "b") })
        {
            var engine = new DialogueEngine(history, new FixedRandom(sample.Item1));
            Check(engine.Select(pool, Sunny, NoneUsed)?.Id == sample.Item2, "Wrong weighted interval.");
        }
        pool = Pool(Line("a", int.MaxValue), Line("b", int.MaxValue));
        Check(new DialogueEngine(history, new FixedRandom(int.MaxValue)).Select(pool, Sunny, NoneUsed)?.Id == "b",
            "Weight total overflowed.");
        var conditioned = Line("conditioned", 3);
        conditioned.Weather = "sun";
        pool = Pool(Line("general", 2), conditioned);
        Check(new DialogueEngine(history, new FixedRandom(0)).Select(pool, Sunny, NoneUsed)?.Id == "general",
            "Matching general entry was excluded.");
        Check(new DialogueEngine(history, new FixedRandom(2)).Select(pool, Sunny, NoneUsed)?.Id == "conditioned",
            "Matching conditioned entry was excluded.");
    }

    private static void TestExhaustion()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        var engine = new DialogueEngine(history, new FixedRandom(0));
        DialoguePool pool = Pool(Line("a"), Line("b"));
        int displayed = 0;
        Action<DialogueEntry> display = _ => displayed++;
        Check(engine.Show(pool, Abigail, 1, Sunny, Safe, display).Entry?.Id == "a", "First selection failed.");
        Check(engine.Show(pool, Abigail, 1, Sunny, Safe, display).Entry?.Id == "b", "Dialogue repeated.");
        Check(engine.Show(pool, Abigail, 1, Sunny, Safe, display).SkipReason is not null, "Exhaustion failed.");
        Check(displayed == 2, "Exhaustion displayed a third dialogue.");
        Check(engine.Show(pool, Abigail, 2, Sunny, Safe, display).Entry?.Id == "a", "New day did not reset selection.");
        Check(history.GetUsed(Abigail, 1).Count == 2, "Prior day history was lost.");
        Check(!Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(), "Atomic write left a temporary file.");
    }

    private static void TestReloadAndIsolation()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        history.RecordShown(Abigail, 1, "a");
        var reloaded = new DialogueHistoryStore(temporary.Path);
        Check(reloaded.GetUsed(Abigail, 1).Contains("a"), "Restart lost persisted history.");
        reloaded.Clear();
        Check(reloaded.GetUsed(Abigail, 1).Contains("a"), "Clearing memory lost persisted history.");
        foreach (DialogueIdentity identity in new[]
        {
            Abigail with { SaveFolder = "OTHER_456" }, Abigail with { PlayerId = 101 }, Abigail with { NpcName = "Leah" }
        })
        {
            Check(reloaded.GetUsed(identity, 1).Count == 0, "Histories leaked across identities.");
            reloaded.RecordShown(identity, 1, "b");
            Check(!reloaded.GetUsed(Abigail, 1).Contains("b"), "A different identity changed Abigail's history.");
        }
    }

    private static void TestCorruptHistory()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        string path = history.GetPath(Abigail);
        foreach (string invalid in new[] { "{", "null", "{}", "{\"Version\":99}" })
        {
            File.WriteAllText(path, invalid);
            history.Clear();
            Throws<IOException>(() => history.GetUsed(Abigail, 1));
        }
        var wrongIdentity = new DialogueHistory { Version = 1, Identity = Abigail with { PlayerId = 999 }, Days = new() };
        File.WriteAllText(path, JsonSerializer.Serialize(wrongIdentity));
        history.Clear();
        Throws<IOException>(() => history.GetUsed(Abigail, 1));
        File.WriteAllText(path, JsonSerializer.Serialize(new DialogueHistory { Version = 1, Identity = Abigail }));
        history.Clear();
        Throws<IOException>(() => history.GetUsed(Abigail, 1));
        File.Delete(path);
        Throws<IOException>(() => history.GetUsed(Abigail, 1));
        Check(history.GetUsed(Abigail with { NpcName = "Leah" }, 1).Count == 0, "Corruption disabled another NPC.");
        history.Clear();
        Check(history.GetUsed(Abigail, 1).Count == 0, "Explicit reload did not allow repaired storage.");
    }

    private static void TestWriteFailure()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        history.RecordShown(Abigail, 1, "committed");
        string path = history.GetPath(Abigail);
        using (FileStream held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var engine = new DialogueEngine(history, new FixedRandom(0));
            int displayed = 0;
            Action<DialogueEntry> display = _ => displayed++;
            Throws<IOException>(() => engine.Show(Pool(Line("failed")), Abigail, 1, Sunny, Safe, display));
            Throws<IOException>(() => engine.Show(Pool(Line("failed")), Abigail, 1, Sunny, Safe, display));
            Check(displayed == 1, "Write failure allowed another dialogue to display.");
        }
        Throws<IOException>(() => history.GetUsed(Abigail, 1));
        var reloaded = new DialogueHistoryStore(temporary.Path);
        Check(reloaded.GetUsed(Abigail, 1).SetEquals(new[] { "committed" }), "Failed replace changed committed data.");
        Check(!Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(), "Failed write left a temporary file.");
    }

    private static void TestGuards()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        var engine = new DialogueEngine(history, new FixedRandom(0));
        DialogueSafety[] blocked =
        {
            Safe with { WorldReady = false }, Safe with { PlayerFree = false }, Safe with { EventActive = true },
            Safe with { MenuOpen = true }, Safe with { FestivalDay = true }, Safe with { PassiveFestivalDay = true }
        };
        foreach (DialogueSafety safety in blocked)
        {
            DialogueResult result = engine.Show(Pool(Line("a")), Abigail, 1, Sunny, safety,
                _ => throw new InvalidOperationException("Blocked dialogue displayed."));
            Check(result.Entry is null && result.SkipReason is not null, "Guard failed.");
            Check(history.GetUsed(Abigail, 1).Count == 0, "Guard consumed dialogue.");
        }
        Check(!Directory.EnumerateFiles(temporary.Path).Any(), "Guard wrote history.");
    }

    private static void TestFirstWriteFailure()
    {
        using var temporary = new TemporaryDirectory();
        string blockedDirectory = Path.Combine(temporary.Path, "file-instead-of-directory");
        File.WriteAllText(blockedDirectory, "blocked");
        var history = new DialogueHistoryStore(Path.Combine(blockedDirectory, "history"));
        Throws<IOException>(() => history.RecordShown(Abigail, 1, "a"));
        Throws<IOException>(() => history.GetUsed(Abigail, 1));
    }

    private static void TestReadFailure()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        history.RecordShown(Abigail, 1, "a");
        var reloaded = new DialogueHistoryStore(temporary.Path);
        using (FileStream held = File.Open(history.GetPath(Abigail), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Throws<IOException>(() => reloaded.GetUsed(Abigail, 1));
        }
        Throws<IOException>(() => reloaded.GetUsed(Abigail, 1));
        reloaded.Clear();
        Check(reloaded.GetUsed(Abigail, 1).Contains("a"), "Repair and reload lost history.");
    }

    private static void TestDisplayFailure()
    {
        using var temporary = new TemporaryDirectory();
        var history = new DialogueHistoryStore(temporary.Path);
        var engine = new DialogueEngine(history, new FixedRandom(0));
        Throws<InvalidOperationException>(() => engine.Show(Pool(Line("a")), Abigail, 1, Sunny, Safe,
            _ => throw new InvalidOperationException("Display failed.")));
        Check(history.GetUsed(Abigail, 1).Count == 0, "Failed display consumed dialogue.");
    }
}

internal sealed class FixedRandom(long ticket) : Random
{
    public override long NextInt64(long maxValue)
    {
        if (ticket < 0 || ticket >= maxValue)
            throw new InvalidOperationException("Test ticket is outside the weighted range.");
        return ticket;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(
        Environment.GetEnvironmentVariable("STARDEWAI_TEST_TEMP") ?? System.IO.Path.GetTempPath()));
    public string Path { get; }

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(root, "StardewAI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (!string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary directory is outside its test root.");
        Directory.Delete(Path, recursive: true);
    }
}

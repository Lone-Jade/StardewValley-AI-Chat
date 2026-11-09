using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class MemoryTests
{
    private static readonly DialogueIdentity Identity = new("TEST_123", 1, "Abigail");
    private static readonly string[] Suggestions = { "聊聊新的冒险吧。", "你最近喜欢什么游戏？", "今天想去哪里？" };
    private static ConversationTurn Turn(int index) => new(index, $"玩家的话{index}", $"角色的回复{index}");

    public static void RetentionAndReload()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        Check(store.GetTurns(Identity).Count == 0 && !File.Exists(store.GetPath(Identity)), "Reading empty memory wrote a file.");
        for (int index = 0; index < 8; index++)
            store.Record(Identity, Turn(index), Suggestions);
        var reloaded = new ConversationMemoryStore(temporary.Path);
        Check(reloaded.GetTurns(Identity).Select(turn => turn.Day).SequenceEqual(Enumerable.Range(2, 6)), "Last six turns were not restored in order.");
        Check(reloaded.GetSuggestions(Identity)!.SequenceEqual(Suggestions), "Suggestions were not restored.");
        string originalName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Identity.SaveFolder, Identity.PlayerId, Identity.NpcName
        })))) + ".json";
        Check(Identity.GetStorageFileName() == originalName, "Existing history filename changed.");
        Check(new DialogueHistoryStore(temporary.Path).GetPath(Identity) == store.GetPath(Identity), "Identity hashing diverged between stores.");
        Check(!Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(), "Temporary memory file leaked.");
    }

    public static void Isolation()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        DialogueIdentity[] identities = { Identity, Identity with { SaveFolder = "OTHER" }, Identity with { PlayerId = 2 }, Identity with { NpcName = "Leah" } };
        for (int index = 0; index < identities.Length; index++)
            store.Record(identities[index], Turn(index), Suggestions);
        store.Clear();
        for (int index = 0; index < identities.Length; index++)
            Check(store.GetTurns(identities[index]).Single() with { Id = "", Suggestions = null } == Turn(index), "Save, player, or NPC records were mixed.");
        Check(identities.Select(store.GetPath).Distinct().Count() == 4, "Memory paths collided.");
    }

    public static void CorruptionAndRecovery()
    {
        using var temporary = new TemporaryDirectory();
        foreach (string raw in new[]
        {
            "not json", "null", "{}", new string('x', ConversationMemoryStore.MaximumFileBytes + 1),
            Document(version: 2), Document(identity: Identity with { PlayerId = 9 }),
            Document(turns: new List<ConversationTurn>() { new(-1, "玩家", "角色回复") }),
            Document(turns: Enumerable.Range(0, 7).Select(Turn).ToList()),
            Document(turns: new List<ConversationTurn>() { new(0, new string('字', 501), "角色回复") }),
            Document(turns: new List<ConversationTurn>() { new(0, "你好", "$action unsafe") }),
            Document(turns: new List<ConversationTurn>() { null! })
        })
        {
            var store = new ConversationMemoryStore(temporary.Path);
            File.WriteAllText(store.GetPath(Identity), raw);
            Throws<IOException>(() => store.GetTurns(Identity));
            File.WriteAllText(store.GetPath(Identity), Document());
            Throws<IOException>(() => store.GetTurns(Identity));
            Check(store.GetTurns(Identity with { NpcName = "Sebastian" }).Count == 0, "Corruption disabled another NPC.");
            store.Clear();
            Check(store.GetTurns(Identity).Count == 1, "Reload did not recover corrected memory.");
        }
    }

    public static void WriteFailure()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        store.Record(Identity, Turn(0), Suggestions);
        byte[] committed = File.ReadAllBytes(store.GetPath(Identity));
        using (var locked = new FileStream(store.GetPath(Identity), FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws<IOException>(() => store.Record(Identity, Turn(1), null));
        Check(File.ReadAllBytes(store.GetPath(Identity)).SequenceEqual(committed), "A failed write changed the committed file.");
        Throws<IOException>(() => store.GetTurns(Identity));
        Check(!Directory.EnumerateFiles(temporary.Path, "*.tmp").Any(), "Failed write left a temporary file.");
        store.Clear();
        Check(store.GetTurns(Identity).Single() with { Id = "", Suggestions = null } == Turn(0), "An uncommitted turn was exposed after reload.");
    }

    public static void FirstWriteAndReadFailure()
    {
        using var temporary = new TemporaryDirectory();
        string blocked = Path.Combine(temporary.Path, "blocked");
        File.WriteAllText(blocked, "file");
        var store = new ConversationMemoryStore(blocked);
        Throws<IOException>(() => store.Record(Identity, Turn(0), null));
        Throws<IOException>(() => store.GetTurns(Identity));
        var valid = new ConversationMemoryStore(temporary.Path);
        valid.Record(Identity, Turn(0), Suggestions);
        var reader = new ConversationMemoryStore(temporary.Path);
        using (var locked = new FileStream(valid.GetPath(Identity), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => reader.GetTurns(Identity));
        Throws<IOException>(() => reader.GetTurns(Identity));
        reader.Clear();
        Check(reader.GetTurns(Identity).Count == 1, "Read failure did not recover on reload.");
    }

    public static void SuggestionFallback()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        store.Record(Identity, Turn(0), Suggestions);
        var model = new DialogueComposerModel("Abigail", "sun", 0, store.GetSuggestions(Identity));
        Check(model.Suggestions.SequenceEqual(Suggestions), "Generated suggestions were not used.");
        store.ClearSuggestions(Identity);
        store.Clear();
        Check(store.GetTurns(Identity).Count == 1 && store.GetSuggestions(Identity) is null, "Failure added a turn or retained suggestions.");
        store.Record(Identity, Turn(1), new[] { "重复", "重复", "重复" });
        Check(store.GetTurns(Identity).Count == 2 && store.GetSuggestions(Identity) is null, "Invalid suggestions poisoned the saved turn.");
        Check(new DialogueComposerModel("Abigail", "sun", 0, store.GetSuggestions(Identity)).Suggestions[1].Contains("游戏"), "Local template fallback missing.");
        store.Record(Identity, Turn(2), Suggestions);
        store.Record(Identity, Turn(3), null);
        store.Clear();
        Check(store.GetSuggestions(Identity) is null, "A reply without suggestions reused older ones.");
    }

    public static void InvalidTurnDoesNotMutate()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ConversationMemoryStore(temporary.Path);
        store.Record(Identity, Turn(0), Suggestions);
        Throws<ArgumentException>(() => store.Record(Identity, new(0, "你\n好", "你好"), null));
        Throws<ArgumentException>(() => store.Record(Identity, new(0, "你好", "嗯"), null));
        Check(store.GetTurns(Identity).Count == 1, "Invalid caller input changed or disabled memory.");
        string[] mutable = Suggestions.ToArray();
        store.Record(Identity, Turn(1), mutable);
        mutable[0] = "changed";
        Check(store.GetSuggestions(Identity)![0] == Suggestions[0], "Caller mutated stored suggestions.");
    }

    private static string Document(int version = 1, DialogueIdentity? identity = null, List<ConversationTurn>? turns = null)
        => JsonSerializer.Serialize(new ConversationMemory { Version = version, Identity = identity ?? Identity, Turns = turns ?? new() { Turn(0) } });

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

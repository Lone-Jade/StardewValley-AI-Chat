using System.Net;
using System.Text;
using System.Text.Json;
using StardewAI.AI;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class AllNpcTests
{
    public static void FallbackPools()
    {
        using var temporary = new TemporaryDirectory();
        string fallback = Path.Combine(temporary.Path, "Default.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Default.json"), fallback);
        var history = new DialogueHistoryStore(Path.Combine(temporary.Path, "history"));
        var engine = new DialogueEngine(history, new Random(1));
        string[] names = { "Penny", "Harvey", "Sam", "Robin", "Willy", "Dwarf", "Krobus", "Wizard", "Sandy", "Leo", "Birdie", "Marlon", "Mister Qi", "Custom.Npc", "../escape", "nested/npc" };
        foreach (string name in names)
        {
            string path = DialoguePool.ResolvePath(temporary.Path, name);
            Check(path == fallback, "NPC without a pool did not use the shared fallback.");
            DialoguePool pool = DialoguePool.Load(path);
            var identity = new DialogueIdentity("TEST", 1, name);
            DialogueResult result = engine.Show(pool, identity, 0, new("sun", 0),
                new(true, true, false, false, false, false), _ => { });
            Check(result.Entry is not null && history.GetUsed(identity, 0).Count == 1, "Fallback was unavailable or history crossed NPC identities.");
            Check(engine.Select(pool, new("rain", 10), new HashSet<string>()) is not null, "Generic pool has no rain dialogue.");
        }
        string specific = Path.Combine(temporary.Path, "Penny.json");
        File.Copy(fallback, specific);
        Check(DialoguePool.ResolvePath(temporary.Path, "Penny") == specific, "Specific pool did not take precedence.");
        File.WriteAllText(specific, "not json");
        bool invalid = false;
        try { DialoguePool.Load(DialoguePool.ResolvePath(temporary.Path, "Penny")); }
        catch (JsonException) { invalid = true; }
        Check(invalid, "Invalid explicit pool silently fell back.");
    }

    public static void GenericRequests() => GenericRequestsAsync().GetAwaiter().GetResult();

    private static async Task GenericRequestsAsync()
    {
        string[] names = { "Penny", "Krobus", "Dwarf", "Birdie", "Marlon", "Custom.Npc" };
        int requests = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            JsonElement messages = document.RootElement.GetProperty("messages");
            using JsonDocument context = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            JsonElement state = context.RootElement;
            Check(state.GetProperty("NpcName").GetString() == names[requests], "NPC identity was changed to a supported-name default.");
            Check(state.GetProperty("DisplayName").GetString() == "测试显示名" && state.GetProperty("CharacterProfile").GetString() == "游戏提供的人物资料。",
                "Character profile or display name missing from request.");
            Check(messages[0].GetProperty("content").GetString()!.Contains("资料缺失时保持克制"), "Unknown NPC prompt lacks context policy.");
            requests++;
            string content = JsonSerializer.Serialize(new { dialogue = "很高兴认识你。", suggestions = new[] { "今天很愉快。", "我喜欢这里。", "下次再聊。" }, friendshipTier = 0 });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } }), Encoding.UTF8, "application/json")
            };
        }));
        var options = new DeepSeekOptions { Enabled = true, ApiKey = "offline-test-key" };
        foreach (string name in names)
        {
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(options,
                new(name, "Town", "spring", 1, 900, "sun", 0, "你好。")
                { DisplayName = "测试显示名", CharacterProfile = "游戏提供的人物资料。" }, default);
            Check(result.Text is not null && result.Suggestions!.Count == 3, "New NPC could not use the normal AI client.");
        }
        Check(requests == names.Length, "Not all NPC requests reached the client.");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

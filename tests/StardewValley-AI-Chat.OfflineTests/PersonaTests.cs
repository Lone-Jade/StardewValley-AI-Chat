using System.Net;
using System.Text;
using System.Text.Json;
using StardewAI.AI;

namespace StardewAI.OfflineTests;

internal static class PersonaTests
{
    public static void Catalog()
    {
        string[] expected = { "Abigail", "Alex", "Caroline", "Clint", "Demetrius", "Dwarf", "Elliott", "Emily",
            "Evelyn", "George", "Gus", "Haley", "Harvey", "Jas", "Jodi", "Kent", "Krobus", "Leah", "Leo",
            "Lewis", "Linus", "Marnie", "Maru", "Pam", "Penny", "Pierre", "Robin", "Sam", "Sandy",
            "Sebastian", "Shane", "Vincent", "Willy", "Wizard", "Birdie", "Bouncer", "Fizz", "Gil",
            "Governor", "Grandpa", "Gunther", "Henchman", "Marlon", "Morris", "Mister Qi", "Old Mariner", "Professor Snail" };
        Check(VanillaNpcProfiles.Names.OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal)), "Vanilla coverage or native identity mismatch.");
        Check(expected.All(name => VanillaNpcProfiles.Find(name)?.NpcName == name), "Catalog lookup failed.");
        foreach (string unknown in new[] { "Sophia", "Custom.Abigail", "abigail", "Mr. Qi", "ProfessorSnail", "../Abigail" })
            Check(VanillaNpcProfiles.Find(unknown) is null, "Unknown internal identity borrowed a vanilla persona.");
        Check(VanillaNpcProfiles.Find("Krobus")!.Boundaries.Contains("非恋爱")
            && new[] { "Jas", "Vincent", "Leo" }.All(name => VanillaNpcProfiles.Find(name)!.Boundaries.Contains("儿童")),
            "Roommate or child relationship boundaries are missing.");
    }

    public static void InvalidCatalog()
    {
        NpcPersona valid = VanillaNpcProfiles.Find("Wizard")!;
        foreach (object data in new object[]
        {
            new { Version = 2, Profiles = new[] { valid } },
            new { Version = 1, Profiles = Array.Empty<NpcPersona>() },
            new { Version = 1, Profiles = new[] { valid, valid } },
            new { Version = 1, Profiles = new[] { valid with { Background = "" } } },
            new { Version = 1, Profiles = new[] { valid with { Voice = new string('字', 301) } } },
            new { Version = 1, Profiles = new[] { valid with { Knowledge = "多行\n资料" } } },
            new { Version = 1, Profiles = new[] { valid with { SourceUrl = "https://example.com/Wizard" } } }
        })
        {
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(data));
            bool rejected = false;
            try { VanillaNpcProfiles.Parse(document.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid profile data was accepted.");
        }
    }

    public static void Relationships()
    {
        string neutral = NpcRuntimeTraits.Describe(0, 0, 2, 2, true);
        Check(neutral.Contains("社交倾向中性") && neutral.Contains("心态中性") && !neutral.Contains("外向") && !neutral.Contains("乐观"),
            "Native neutral traits were mislabelled outgoing or positive.");
        string child = NpcRuntimeTraits.Describe(2, 1, 1, 1, true);
        Check(child.Contains("儿童") && child.Contains("礼貌") && child.Contains("内向害羞") && child.Contains("偏悲观"),
            "Native age or personality traits were lost.");
        string unknown = NpcRuntimeTraits.Describe(9, 9, 9, 9, false);
        Check(unknown.Contains("未知") && !unknown.Contains("外向") && !unknown.Contains("乐观") && !unknown.Contains("成年"),
            "Unknown native traits were invented.");
        foreach (var scenario in new[]
        {
            ("Friendly", false, true, "friendly"), ("Dating", false, true, "dating"),
            ("Engaged", false, true, "engaged"), ("Married", false, true, "married"),
            ("Married", true, true, "roommate"), ("Engaged", true, true, "roommate_pending"),
            ("Divorced", true, true, "divorced"), ("Friendly", false, false, "non_social"),
            ("future-status", false, true, "unknown"), ("future-status", true, false, "non_social")
        })
            Check(NpcRelationship.FromGameState(scenario.Item1, scenario.Item2, scenario.Item3) == scenario.Item4,
                "Native relationship status mapped incorrectly.");
    }

    public static void Requests() => RequestsAsync().GetAwaiter().GetResult();

    private static async Task RequestsAsync()
    {
        var scenarios = VanillaNpcProfiles.Names.Select(name => (Name: name, Relationship: "friendly", Hearts: 10, Home: ""))
            .Concat(new (string Name, string Relationship, int Hearts, string Home)[] { ("Sophia", "friendly", 0, "Custom.SophiaHome"),
                ("Custom.Abigail", "unknown", 10, ""), ("Leah", "dating", 8, "LeahHouse"), ("Leah", "engaged", 10, "LeahHouse"),
                ("Leah", "married", 0, "FarmHouse"), ("Leah", "divorced", 5, "LeahHouse"), ("Krobus", "roommate", 14, "Cabin_123"),
                ("Krobus", "roommate_pending", 10, "Sewer"), ("Leo", "friendly", 10, "IslandHut"), ("Leo", "friendly", 10, "LeoTreeHouse") }).ToArray();
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            var scenario = scenarios[calls++];
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            JsonElement messages = body.RootElement.GetProperty("messages");
            string system = messages[0].GetProperty("content").GetString()!;
            NpcPersona? persona = VanillaNpcProfiles.Find(scenario.Name);
            if (persona is not null)
                Check(system.Contains(persona.Background) && system.Contains(persona.Voice)
                    && system.Contains(persona.Boundaries) && system.Contains(persona.Knowledge), "Current persona was not injected.");
            else
                Check(system.Contains("没有该内部身份的原版参考档案"), "Mod NPC lost the runtime fallback.");
            foreach (string other in VanillaNpcProfiles.Names.Where(name => name != scenario.Name))
                Check(!system.Contains(VanillaNpcProfiles.Find(other)!.Background), "Another character's profile leaked into the request.");
            Check(system.Contains("不能用高心数推断恋爱") && system.Contains("没有提供已看事件")
                && system.Contains("不知道其他 NPC 的私人谈话"), "Relationship or knowledge boundaries missing.");
            using JsonDocument current = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Check(current.RootElement.GetProperty("Relationship").GetString() == scenario.Relationship
                && current.RootElement.GetProperty("Hearts").GetInt32() == scenario.Hearts
                && current.RootElement.GetProperty("HomeLocation").GetString() == scenario.Home
                && current.RootElement.GetProperty("CharacterProfile").GetString() == "游戏当前人物资料。", "Runtime context was replaced by the vanilla profile.");
            string content = JsonSerializer.Serialize(new { dialogue = "今天能聊一会儿也很好。",
                suggestions = new[] { "我喜欢镇上的生活。", "今天过得很充实。", "我们下次再聊。" }, friendshipTier = 0, memoryUpdates = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { finish_reason = "stop", message = new { content } } } }), Encoding.UTF8, "application/json") };
        }));
        var client = new DeepSeekClient(http);
        foreach (var scenario in scenarios)
        {
            AiDialogueResult result = await client.GenerateAsync(new() { Enabled = true, ApiKey = "offline-test-key" },
                new(scenario.Name, "Town", "spring", 1, 900, "sun", scenario.Hearts, "你好，今天还好吗？")
                    { Relationship = scenario.Relationship, HomeLocation = scenario.Home, CharacterProfile = "游戏当前人物资料。" }, default);
            Check(result.Text is not null && result.Suggestions?.Count == 3 && result.MemoryUpdates?.Count == 0,
                "Persona changes broke the reply/suggestion/memory protocol.");
        }
        Check(calls == scenarios.Length, "Not every persona/relationship reached the production client.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

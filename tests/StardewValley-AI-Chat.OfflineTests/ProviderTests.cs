using System.Net;
using System.Text;
using System.Text.Json;
using StardewAI.AI;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class ProviderTests
{
    private static readonly string[] Suggestions = { "今天过得很好。", "我喜欢这里。", "我们下次再聊。" };
    private static readonly AiDialogueRequest Request = new("Leah", "Town", "spring", 1, 900, "sun", 4, "我喜欢雨天。");

    public static void TemplatesAndMigration()
    {
        string[] paths = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ConfigTemplates"), "*.json");
        Check(paths.Length == 7, "Provider templates are missing.");
        foreach (string path in paths)
        {
            AiConfiguration config = JsonSerializer.Deserialize<AiConfiguration>(File.ReadAllText(path))!;
            config.NormalizeAI();
            Check(!config.AI.Enabled && config.AI.ApiKey == "" && config.AI.TimeoutSeconds == 30,
                "Public template contains enabled credentials or unexpected defaults.");
        }
        var old = JsonSerializer.Deserialize<AiConfiguration>("""
            {"DeepSeek":{"Enabled":true,"ApiKey":"fake-migration-key","MaxTokens":512,"TimeoutSeconds":8}}
            """)!;
        old.NormalizeAI();
        Check(old.AI.ApiKey == "fake-migration-key" && old.AI.MaxTokens == 512 && old.AI.TimeoutSeconds == 8
            && old.AI.Provider == "deepseek" && old.DeepSeek is null && !old.ShouldSerializeDeepSeek(), "Legacy AI settings were lost.");
        Check(!JsonSerializer.Serialize(old).Contains("\"DeepSeek\""), "Migration re-exported the old config section.");
        var both = JsonSerializer.Deserialize<AiConfiguration>("""
            {"DeepSeek":{"Model":"old"},"AI":{"Model":"new"}}
            """)!;
        both.NormalizeAI();
        Check(both.AI.Model == "new", "Legacy configuration overwrote the explicit AI section.");
        var defaults = new AiConfiguration();
        defaults.NormalizeAI();
        Check(defaults.AI.Provider == "deepseek" && !defaults.AI.Enabled && defaults.AI.ApiKey == "", "Fresh config defaults failed.");
    }

    public static void ProviderRequests() => ProviderRequestsAsync().GetAwaiter().GetResult();

    private static async Task ProviderRequestsAsync()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ConfigTemplates"), "*.json"))
        {
            AiConfiguration config = JsonSerializer.Deserialize<AiConfiguration>(File.ReadAllText(path))!;
            config.NormalizeAI();
            DeepSeekOptions options = config.AI;
            options.Enabled = true;
            options.ApiKey = "fake-provider-key";
            options.BaseUrl = options.BaseUrl.Replace("{WorkspaceId}", "test-workspace");
            using var http = new HttpClient(new Handler(async request =>
            {
                Check(request.Headers.Authorization?.Parameter == "fake-provider-key", "Provider authentication missing.");
                bool claude = options.Provider == "claude";
                Check(request.RequestUri!.AbsoluteUri == options.BaseUrl.TrimEnd('/') + (claude ? "/messages" : "/chat/completions"),
                    "Wrong provider endpoint.");
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                JsonElement root = body.RootElement;
                Check(root.GetProperty("model").GetString() == options.Model, "Provider model changed.");
                Check(root.GetProperty(options.Provider is "openai" or "minimax" ? "max_completion_tokens" : "max_tokens").GetInt32() == options.MaxTokens,
                    "Provider output limit uses the wrong field.");
                Check(root.TryGetProperty("response_format", out _) == (!claude && options.UseJsonMode), "Unexpected JSON-mode parameter.");
                JsonElement messages = root.GetProperty("messages");
                if (claude)
                {
                    Check(request.Headers.GetValues("anthropic-version").Single() == "2026-10-21" && root.GetProperty("system").ValueKind == JsonValueKind.String,
                        "Claude version or system field missing.");
                    Check(messages.EnumerateArray().All(message => message.GetProperty("role").GetString() != "system"), "Claude contains a system-role message.");
                }
                if (options.Provider == "kimi")
                {
                    Check(messages.GetArrayLength() == 2, "Kimi replayed assistant turns without their preserved thinking.");
                    using var current = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                    Check(current.RootElement.GetProperty("RecentConversation").GetArrayLength() == 6, "Kimi history was lost or unbounded.");
                }
                if (options.Provider == "qwen")
                    Check(!root.GetProperty("enable_thinking").GetBoolean() && !root.TryGetProperty("thinking", out _), "Qwen thought control is invalid.");
                if (options.Provider == "minimax")
                    Check(root.GetProperty("reasoning_split").GetBoolean(), "MiniMax thinking can leak into visible text.");
                if (options.Provider is "openai" or "openai-compatible")
                    Check(!root.TryGetProperty("thinking", out _), "Provider received a DeepSeek-specific parameter.");
                return Response(claude, Payload());
            }));
            var history = Enumerable.Range(0, 8).Select(day => new ConversationTurn(day, "早上好。", "你好，今天过得如何。" )).ToArray();
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(options, Request with { History = history }, default);
            Check(result.Text == "听着很舒服。" && result.Suggestions?.Count == 3 && result.MemoryUpdates?.Count == 1,
                "Provider failed to preserve the dialogue/suggestion/memory contract: " + options.Provider);
        }
    }

    public static void ClaudeValidation() => ClaudeValidationAsync().GetAwaiter().GetResult();

    private static async Task ClaudeValidationAsync()
    {
        var options = new DeepSeekOptions { Provider = "claude", BaseUrl = "https://api.anthropic.com/v1", Enabled = true, ApiKey = "fake-key", DisableThinking = false };
        foreach (object invalid in new object[]
        {
            new { stop_reason = "max_tokens", content = new[] { new { type = "text", text = Payload() } } },
            new { stop_reason = "end_turn", content = new[] { new { type = "tool_use", text = Payload() } } },
            new { stop_reason = "end_turn", content = new[] { new { type = "text", text = "not json" } } },
            new { stop_reason = "end_turn", content = new[] { new { type = "text" } } },
            new { stop_reason = "end_turn", stop_details = new { type = "refusal" }, content = new[] { new { type = "text", text = Payload() } } }
        })
        {
            using var http = new HttpClient(new Handler(_ => Task.FromResult(Raw(invalid))));
            Check((await new DeepSeekClient(http).GenerateAsync(options, Request, default)).Text is null, "Invalid Claude response was accepted.");
        }
        string payload = Payload();
        using var splitHttp = new HttpClient(new Handler(_ => Task.FromResult(Raw(new
        {
            stop_reason = "end_turn", content = new object[] { new { type = "thinking", thinking = "private reasoning" },
                new { type = "text", text = payload[..20] }, new { type = "text", text = payload[20..] } }
        }))));
        AiDialogueResult split = await new DeepSeekClient(splitHttp).GenerateAsync(options, Request, default);
        Check(split.Text == "听着很舒服。", "Claude text blocks were not joined or thinking was exposed.");
    }

    public static void KimiRepair() => KimiRepairAsync().GetAwaiter().GetResult();

    private static async Task KimiRepairAsync()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("messages").EnumerateArray().All(message => message.GetProperty("role").GetString() != "assistant"),
                "Kimi repair replayed an incomplete preserved-thinking turn.");
            return Response(false, calls == 1 ? Payload(suggestions: false) : Payload());
        }));
        var options = new DeepSeekOptions { Provider = "kimi", Enabled = true, ApiKey = "fake-key", DisableThinking = false };
        AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(options, Request, default);
        Check(calls == 2 && result.SuggestionsRepaired && result.MemoryUpdates?.Single().Value == "雨天", "Kimi repair lost the first response's memory.");
    }

    public static void DailyKeyIsolation()
    {
        var data = new Dictionary<string, string> { [ConversationStateKeys.ForNpc("Leah")] = "current", ["OtherMod/Conversation/Leah"] = "foreign" };
        Check(ConversationStateKeys.Read(data, "Leah") == "current" && ConversationStateKeys.Read(data, "Abigail") is null, "Daily state identity crossed NPCs.");
        data.Remove(ConversationStateKeys.ForNpc("Leah"));
        Check(ConversationStateKeys.Read(data, "Leah") is null, "Foreign mod state was imported as legacy data.");
    }

    private static string Payload(bool suggestions = true) => JsonSerializer.Serialize(new
    {
        dialogue = "听着很舒服。", suggestions = suggestions ? Suggestions : null, friendshipTier = 1,
        memoryUpdates = new[] { new MemoryUpdate("ADD", "weather", "like", "雨天", "我喜欢雨天。") }
    });

    private static HttpResponseMessage Response(bool claude, string content) => claude
        ? Raw(new { stop_reason = "end_turn", content = new[] { new { type = "text", text = content } } })
        : Raw(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } });

    private static HttpResponseMessage Raw(object content) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json") };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

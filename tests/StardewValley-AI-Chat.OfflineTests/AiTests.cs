using System.Net;
using System.Text;
using System.Text.Json;
using StardewAI.AI;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class AiTests
{
    private static readonly AiDialogueRequest Context = new("Abigail", "Town", "spring", 1, 900, "rain", 4, "今天想做什么？");
    private static readonly AiScene Scene = new("TEST", 1, "Abigail", 0, "Town", "Town", 10, 10, 11, 10);

    public static void Configuration() => ConfigurationAsync().GetAwaiter().GetResult();
    public static void RequestFormat() => RequestFormatAsync().GetAwaiter().GetResult();
    public static void ResponseValidation() => ResponseValidationAsync().GetAwaiter().GetResult();
    public static void FailureHandling() => FailureHandlingAsync().GetAwaiter().GetResult();
    public static void QueueLifecycle() => QueueLifecycleAsync().GetAwaiter().GetResult();
    public static void MemoryRequest() => MemoryRequestAsync().GetAwaiter().GetResult();
    public static void DynamicSuggestions() => DynamicSuggestionsAsync().GetAwaiter().GetResult();
    public static void SuggestionRepair() => SuggestionRepairAsync().GetAwaiter().GetResult();

    private static async Task SuggestionRepairAsync()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            JsonElement messages = body.RootElement.GetProperty("messages");
            calls++;
            if (calls == 1)
            {
                using JsonDocument context = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                Check(context.RootElement.GetProperty("EvaluateFriendship").GetBoolean(), "First response scoring context missing.");
                Check(context.RootElement.GetProperty("OpeningLine").GetString() == "树林里很安静。", "Native opener lost.");
                return RawResponse(Envelope("{\"dialogue\":\"我也喜欢这份宁静。\",\"friendshipTier\":2}"));
            }
            Check(calls == 2 && messages.GetArrayLength() == 4, "Repair was unbounded or lost context.");
            return RawResponse(Envelope(JsonSerializer.Serialize(new
                { dialogue = "补全时更改的回复。", suggestions = new[] { "听着就很放松。", "我喜欢雨中的森林。", "创作也需要安静。" }, friendshipTier = 0 })));
        }));
        AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(Options(), Context with
            { EvaluateFriendship = true, OpeningLine = "树林里很安静。", Preferences = "最爱沙拉" }, default);
        Check(result.Text == "我也喜欢这份宁静。" && result.FriendshipTier == 2 && result.Suggestions?.Count == 3
            && result.SuggestionsRepaired && calls == 2, "Repair changed the valid reply/score or failed to recover suggestions.");

        foreach (object? score in new object?[] { null, -1, 3, "2", 1.5, int.MaxValue, 0, 1, 2 })
        {
            using var scoreHttp = new HttpClient(new Handler((_, _) => Task.FromResult(RawResponse(Envelope(JsonSerializer.Serialize(new
                { dialogue = "这次聊天很愉快。", suggestions = new[] { "我也这样觉得。", "今天心情很好。", "我还有些想法。" }, friendshipTier = score }))))));
            AiDialogueResult scored = await new DeepSeekClient(scoreHttp).GenerateAsync(Options(), Context, default);
            int? expected = score is int value && value is >= 0 and <= 2 ? value : null;
            Check(scored.FriendshipTier == expected, "Invalid friendship score was accepted.");
        }

        calls = 0;
        using var failedRepair = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromResult(RawResponse(Envelope("{\"dialogue\":\"保留这条有效回复。\",\"friendshipTier\":1}")));
            throw new HttpRequestException("fake-offline-key");
        }));
        result = await new DeepSeekClient(failedRepair).GenerateAsync(Options(), Context, default);
        Check(result.Text == "保留这条有效回复。" && result.FriendshipTier == 1 && result.Suggestions is null && calls == 2,
            "Repair failure discarded a valid reply or retried more than once.");
    }

    private static async Task MemoryRequestAsync()
    {
        var history = Enumerable.Range(0, 8).Select(index => new ConversationTurn(index, $"玩家编号{index}", $"回复编号{index}")
        { Suggestions = new[] { "历史建议甲" + index, "历史建议乙" + index, "历史建议丙" + index } }).ToArray();
        int calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            JsonElement messages = document.RootElement.GetProperty("messages");
            Check(messages.GetArrayLength() == 14, "History must be bounded to six pairs plus system and current context.");
            for (int index = 0; index < 6; index++)
            {
                Check(messages[1 + index * 2].GetProperty("role").GetString() == "user", "Historical user role mismatch.");
                Check(messages[1 + index * 2].GetProperty("content").GetString() == history[index + 2].PlayerMessage, "Historical user order mismatch.");
                Check(messages[2 + index * 2].GetProperty("role").GetString() == "assistant", "Historical NPC role mismatch.");
                using JsonDocument priorReply = JsonDocument.Parse(messages[2 + index * 2].GetProperty("content").GetString()!);
                Check(priorReply.RootElement.GetProperty("dialogue").GetString() == history[index + 2].NpcMessage, "Historical NPC JSON format or order mismatch.");
                Check(priorReply.RootElement.GetProperty("suggestions").GetArrayLength() == 3,
                    "Historical examples must include all three suggestions.");
            }
            using JsonDocument snapshot = JsonDocument.Parse(messages[13].GetProperty("content").GetString()!);
            Check(snapshot.RootElement.GetProperty("PlayerMessage").GetString() == Context.PlayerMessage, "Current message missing.");
            Check(!snapshot.RootElement.TryGetProperty("History", out _), "History duplicated in current context.");
            return Response("我记得我们之前聊过。 ");
        }));
        var client = new DeepSeekClient(http);
        Check((await client.GenerateAsync(Options(), Context with { History = history }, default)).Text is not null, "Memory request failed.");
        Check((await client.GenerateAsync(Options(), Context with { History = new[] { new ConversationTurn(-1, "你好", "角色回复") } }, default)).Failure == "invalid_history", "Invalid history accepted.");
        Check((await client.GenerateAsync(Options(), Context with { PlayerMessage = "你\n好" }, default)).Failure == "invalid_message", "Control text accepted.");
        Check(calls == 1, "Invalid inputs sent network requests.");
    }

    private static async Task DynamicSuggestionsAsync()
    {
        string[] valid = { " 聊聊新的冒险吧。 ", "最近喜欢玩什么游戏？", "今天想去哪里？" };
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(RawResponse(Envelope(JsonSerializer.Serialize(new { dialogue = "今天想玩一个新游戏。", suggestions = valid })))))))
        {
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(Options(), Context, default);
            Check(result.Text is not null && result.Suggestions!.SequenceEqual(valid.Select(text => text.Trim())), "Generated suggestions were not parsed.");
            Check(new DialogueComposerModel("Abigail", "sun", 0, result.Suggestions).Suggestions[0] == valid[0].Trim(), "Generated suggestions did not reach the composer model.");
        }
        foreach (object? invalid in new object?[]
        {
            null, "wrong type", Array.Empty<string>(), new[] { "只有一条" },
            new[] { "重复", "重复", "其他" }, new[] { " 第一条 ", "第一条", "第三条" },
            new[] { "第一条", "第二条", "第三条", "第四条" }, new object[] { "你好", 1, "其他" },
            new[] { "$action unsafe", "第二条", "第三条" }, new[] { "第一条", "第\n二条", "第三条" },
            new[] { new string('字', 81), "第二条", "第三条" }
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(RawResponse(Envelope(JsonSerializer.Serialize(new { dialogue = "角色的有效回复。", suggestions = invalid }))))));
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(Options(), Context, default);
            Check(result.Text == "角色的有效回复。" && result.Suggestions is null && result.Failure is null, "Invalid suggestions discarded a valid reply.");
            Check(new DialogueComposerModel("Leah", "rain", 8, result.Suggestions).Suggestions[1].Contains("创作"), "Local fallback missing.");
        }
    }

    private static DeepSeekOptions Options() => new() { Enabled = true, ApiKey = "fake-offline-key" };

    private static async Task ConfigurationAsync()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("你好。"));
        }));
        var client = new DeepSeekClient(http);
        Check((await client.GenerateAsync(new DeepSeekOptions(), Context, default)).Failure == "disabled", "Default must disable AI.");
        var options = new DeepSeekOptions { Enabled = true };
        Check((await client.GenerateAsync(options, Context, default)).Failure == "missing_api_key", "Blank key must skip HTTP.");
        foreach (string endpoint in new[] { "http://api.deepseek.com", "garbage", "https://name:password@example.com", "https://example.com?a=1", "https://example.com/#key" })
        {
            options = Options();
            options.BaseUrl = endpoint;
            Check((await client.GenerateAsync(options, Context, default)).Failure == "invalid_base_url", "Invalid endpoint accepted.");
        }
        options = Options();
        options.TimeoutSeconds = 0;
        Check((await client.GenerateAsync(options, Context, default)).Failure == "invalid_configuration", "Invalid timeout accepted.");
        options = Options();
        options.MaxTokens = 0;
        Check((await client.GenerateAsync(options, Context, default)).Failure == "invalid_configuration", "Invalid token limit accepted.");
        options = Options();
        options.ApiKey = "key\r\nsecret";
        Check((await client.GenerateAsync(options, Context, default)).Failure == "invalid_configuration", "Invalid header accepted.");
        Check((await client.GenerateAsync(Options(), Context with { PlayerMessage = new string('x', 501) }, default)).Failure == "message_too_long", "Message limit missing.");
        Check(calls == 0, "Unavailable configuration made an HTTP call.");
    }

    private static async Task RequestFormatAsync()
    {
        foreach (string baseUrl in new[] { "https://api.deepseek.com", "https://api.deepseek.com/v1/" })
        {
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                Check(request.RequestUri!.AbsoluteUri == baseUrl.TrimEnd('/') + "/chat/completions", "Endpoint mismatch.");
                Check(request.Method == HttpMethod.Post, "Expected POST.");
                Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fake-offline-key", "Authorization mismatch.");
                Check(request.Content!.Headers.ContentType?.MediaType == "application/json", "Expected JSON content.");
                string raw = await request.Content.ReadAsStringAsync(token);
                Check(!raw.Contains("fake-offline-key"), "Credential leaked into request body.");
                using JsonDocument document = JsonDocument.Parse(raw);
                JsonElement body = document.RootElement;
                Check(body.GetProperty("model").GetString() == "deepseek-flash", "Default model mismatch.");
                Check(!body.GetProperty("stream").GetBoolean(), "Unexpected streaming.");
                Check(body.GetProperty("max_tokens").GetInt32() == 1024, "Token limit mismatch.");
                Check(body.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "Thinking must be disabled.");
                Check(body.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "Expected JSON mode.");
                JsonElement messages = body.GetProperty("messages");
                Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("content").GetString()!.Contains("json"), "JSON prompt missing.");
                AiDialogueRequest? sent = JsonSerializer.Deserialize<AiDialogueRequest>(messages[1].GetProperty("content").GetString()!);
                Check(sent is not null && sent with { LongTermPreferences = Context.LongTermPreferences } == Context,
                    "Game snapshot mismatch.");
                return Response("雨声真让人想出去冒险。 ");
            }));
            var options = Options();
            options.BaseUrl = baseUrl;
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(options, Context, default);
            Check(result.Text == "雨声真让人想出去冒险。" && result.Failure is null, "Valid response failed.");
        }
    }

    private static async Task ResponseValidationAsync()
    {
        foreach (string text in new[] { "", " ", "嗯", new string('字', 241), "$h你好", "你好#下一页", "[74]", "你好\n玩家", "@你好", "<action>", "^你好", "100%", "{action}", "a\\b", "你好||再见", "你好¦再见" })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(text))));
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(Options(), Context, default);
            Check(result.Text is null && result.Failure == "invalid_dialogue", "Unsafe dialogue accepted.");
        }
        foreach (string raw in new[]
        {
            "not json", "[]", "{}", "{\"choices\":[]}",
            Envelope("{\"dialogue\":\"你好\"}", "length"), Envelope(" "), Envelope("not json"),
            Envelope("{\"other\":\"你好\"}"), Envelope("{\"dialogue\":1}"), Envelope("[]"),
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}"
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(RawResponse(raw))));
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(Options(), Context, default);
            Check(result.Text is null && result.Failure is not null, "Malformed response accepted.");
        }
        using var validHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new string('字', 240)))));
        Check((await new DeepSeekClient(validHttp).GenerateAsync(Options(), Context, default)).Text?.Length == 240, "Text boundary failed.");
    }

    private static async Task FailureHandlingAsync()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("private provider body and fake-offline-key")
            })));
            AiDialogueResult result = await new DeepSeekClient(http).GenerateAsync(Options(), Context, default);
            Check(result == AiDialogueResult.Failed($"http_{(int)status}"), "Status reason must omit body.");
        }
        using var networkHttp = new HttpClient(new Handler((_, _) => throw new HttpRequestException("fake-offline-key")));
        Check((await new DeepSeekClient(networkHttp).GenerateAsync(Options(), Context, default)).Failure == "network_error", "Network error handling failed.");
        foreach (bool knownLength in new[] { true, false })
        {
            using var largeHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = knownLength ? new StringContent(new string('x', 70000)) : new UnknownLengthContent(new byte[70000])
            })));
            Check((await new DeepSeekClient(largeHttp).GenerateAsync(Options(), Context, default)).Failure == "response_too_large", "Response size cap failed.");
        }
        using var slowHttp = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response("unreachable");
        }));
        var options = Options();
        options.TimeoutSeconds = 1;
        Check((await new DeepSeekClient(slowHttp).GenerateAsync(options, Context, default)).Failure == "timeout", "Request timeout failed.");
        using var cancellation = new CancellationTokenSource();
        Task<AiDialogueResult> task = new DeepSeekClient(slowHttp).GenerateAsync(Options(), Context, cancellation.Token);
        cancellation.Cancel();
        Check((await task).Failure == "cancelled", "Cancellation failed.");
    }

    private static async Task QueueLifecycleAsync()
    {
        var client = new ControlledClient();
        var queue = new AiDialogueQueue(client);
        Check(queue.TryStart(Options(), Context, Scene), "First request rejected.");
        Check(!queue.TryStart(Options(), Context, Scene), "Concurrent request accepted.");
        Check(!queue.TryTake(Scene, true, out _), "Unfinished request consumed.");
        client.Current.SetResult(new AiDialogueResult("你好", null));
        await client.Returned.Task;
        AiDialogueResult? result = await TakeWhenReady(queue, Scene);
        Check(result?.Text == "你好" && !queue.IsBusy, "Completion failed.");
        Check(!queue.TryTake(Scene, true, out _), "Result consumed twice.");

        foreach (AiScene changed in new[]
        {
            Scene with { Save = "OTHER" }, Scene with { Player = 2 }, Scene with { Npc = "Leah" },
            Scene with { Day = 1 }, Scene with { PlayerLocation = "Farm" }, Scene with { NpcLocation = "Farm" },
            Scene with { PlayerX = 13 }, Scene with { NpcY = 13 }
        })
        {
            queue.TryStart(Options(), Context, Scene);
            Check(!queue.TryTake(changed, true, out _) && !queue.IsBusy && client.Token.IsCancellationRequested, "Stale context survived.");
            client.Current.SetResult(new AiDialogueResult("stale", null));
        }
        Check(Scene.IsCompatible(Scene with { PlayerX = 12 }), "Two tile boundary rejected.");
        queue.TryStart(Options(), Context, Scene);
        TaskCompletionSource<AiDialogueResult> old = client.Current;
        Check(!queue.TryTake(Scene, false, out _) && !queue.IsBusy, "Story guard did not cancel.");
        queue.TryStart(Options(), Context, Scene);
        old.SetResult(new AiDialogueResult("stale", null));
        client.Current.SetResult(new AiDialogueResult("new", null));
        result = await TakeWhenReady(queue, Scene);
        Check(result?.Text == "new", "Abandoned request replaced current result.");
        queue.TryStart(Options(), Context, Scene);
        client.Current.SetResult(new AiDialogueResult("finished but stale", null));
        Check(!queue.TryTake(Scene with { Day = 1 }, true, out _) && !queue.IsBusy, "Completed stale response survived.");
        queue.TryStart(Options(), Context, Scene);
        client.Current.SetException(new InvalidOperationException("private provider body and fake-offline-key"));
        result = await TakeWhenReady(queue, Scene);
        Check(result == AiDialogueResult.Failed("client_error"), "Unexpected client error leaked details.");
        queue.TryStart(Options(), Context, Scene);
        queue.Cancel();
        queue.Cancel();
        client.Current.SetException(new HttpRequestException("private"));
        Check(!queue.IsBusy, "Cancel was not idempotent.");
    }

    public static void GeneratedHistory()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("STARDEWAI_TEST_TEMP") ?? Path.GetTempPath(),
            "StardewAI-AI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var identity = new DialogueIdentity("TEST", 1, "Abigail");
            var safety = new DialogueSafety(true, true, false, false, false, false);
            var history = new DialogueHistoryStore(root);
            var engine = new DialogueEngine(history, new Random(0));
            int shown = 0;
            Check(engine.ShowGenerated("你好", identity, 0, safety with { MenuOpen = true }, _ => shown++).Entry is null, "Guarded AI was displayed.");
            Check(history.GetUsed(identity, 0).Count == 0, "Guard consumed AI.");
            try { engine.ShowGenerated("你好", identity, 0, safety, _ => throw new IOException("display failed")); }
            catch (IOException) { }
            Check(history.GetUsed(identity, 0).Count == 0, "Failed AI display consumed history.");
            Check(engine.ShowGenerated("你好", identity, 0, safety, _ => shown++).Entry is not null, "AI display failed.");
            engine = new DialogueEngine(new DialogueHistoryStore(root), new Random(0));
            Check(engine.ShowGenerated("你好", identity, 0, safety, _ => shown++).Entry is null, "AI repeat survived reload.");
            Check(engine.ShowGenerated("你好", identity with { NpcName = "Leah" }, 0, safety, _ => shown++).Entry is not null, "NPC history not isolated.");
            Check(engine.ShowGenerated("你好", identity, 1, safety, _ => shown++).Entry is not null, "AI next day did not reset.");
            Check(shown == 3, "Unexpected display count.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<AiDialogueResult?> TakeWhenReady(AiDialogueQueue queue, AiScene scene)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        AiDialogueResult? result;
        while (!queue.TryTake(scene, true, out result))
            await Task.Delay(1, limit.Token);
        return result;
    }

    private static HttpResponseMessage Response(string text) => RawResponse(Envelope(JsonSerializer.Serialize(new
        { dialogue = text, suggestions = new[] { "聊聊新的冒险吧。", "我也喜欢雨天。", "今天过得很充实。" }, friendshipTier = 1 })));
    private static HttpResponseMessage RawResponse(string raw) => new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
    private static string Envelope(string content, string finish = "stop") => JsonSerializer.Serialize(new
    {
        choices = new[] { new { finish_reason = finish, message = new { content } } }
    });
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    private sealed class ControlledClient : IAiDialogueClient
    {
        public TaskCompletionSource<AiDialogueResult> Current { get; private set; } = null!;
        public TaskCompletionSource Returned { get; private set; } = null!;
        public CancellationToken Token { get; private set; }

        public async Task<AiDialogueResult> GenerateAsync(DeepSeekOptions options, AiDialogueRequest request, CancellationToken cancellationToken)
        {
            Current = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Token = cancellationToken;
            TaskCompletionSource returned = Returned;
            try { return await Current.Task; }
            finally { returned.TrySetResult(); }
        }
    }
}

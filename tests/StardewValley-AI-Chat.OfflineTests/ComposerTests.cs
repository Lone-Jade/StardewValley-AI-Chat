using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class ComposerTests
{
    public static void Suggestions()
    {
        foreach (string npc in new[] { "Abigail", "Sebastian", "Leah", "Other" })
        foreach (string weather in new[] { "sun", "rain", "storm", "snow", "wind", "green_rain" })
        foreach (int hearts in new[] { 0, 5, 6, 14 })
        {
            var model = new DialogueComposerModel(npc, weather, hearts);
            Check(model.Suggestions.Count == 3 && model.Suggestions.Distinct().Count() == 3, "Expected three unique suggestions.");
            Check(model.Suggestions.All(text => text.Length is > 0 and <= DialogueComposerModel.MaximumMessageLength), "Suggestion length invalid.");
            Check(!model.IsWaiting && !model.IsClosed, "Creating suggestions changed request state.");
        }
        Check(new DialogueComposerModel("Leah", "rain", 0).Suggestions[0].Contains("雨"), "Weather context missing.");
        Check(new DialogueComposerModel("Sebastian", "sun", 0).Suggestions[1].Contains("编程"), "NPC context missing.");
        Check(new DialogueComposerModel("Abigail", "sun", 0).Suggestions[1].Contains("游戏"), "Abigail hobby missing.");
        Check(new DialogueComposerModel("Leah", "sun", 0).Suggestions[1].Contains("创作"), "Leah hobby missing.");
        Check(new DialogueComposerModel("Leah", "sun", 5).Suggestions[2] != new DialogueComposerModel("Leah", "sun", 6).Suggestions[2], "Heart boundary missing.");
    }

    public static void SelectionAndEditing()
    {
        var model = new DialogueComposerModel("Leah", "rain", 8);
        string? text = model.SelectSuggestion(1);
        Check(text == model.Suggestions[1] && !model.IsWaiting, "Selection submitted a request.");
        string edited = text + " 我对木雕很感兴趣。";
        Check(model.TrySubmit("  " + edited + "  ", out string message) && message == edited, "Edited suggestion did not submit verbatim.");
        Check(model.IsWaiting && model.SelectSuggestion(0) is null, "Suggestion changed while waiting.");
        Check(!model.TrySubmit("第二次发送", out _), "Duplicate submission accepted.");
        Check(model.SelectSuggestion(-1) is null && model.SelectSuggestion(3) is null, "Invalid suggestion index accepted.");
    }

    public static void InputValidation()
    {
        var model = new DialogueComposerModel("Abigail", "sun", 0);
        foreach (string invalid in new[] { "", "   ", new string('字', 501), "你好\n玩家", "你\t好", "你\0好" })
        {
            Check(!model.TrySubmit(invalid, out _) && !model.IsWaiting && model.Error is not null, "Invalid input submitted.");
        }
        Check(model.SelectSuggestion(0) is not null && model.Error is null, "Selecting a suggestion did not clear validation error.");
        string maximum = new string('字', 500);
        Check(model.TrySubmit(maximum, out string text) && text == maximum && model.Error is null, "500 character boundary failed.");
        var freeInput = new DialogueComposerModel("Other", "sun", 0);
        Check(freeInput.TrySubmit("你好，我想聊聊自己的农场。", out text) && text == "你好，我想聊聊自己的农场。", "Free text did not submit.");
    }

    public static void CloseAndRetry()
    {
        var model = new DialogueComposerModel("Leah", "sun", 8);
        model.TrySubmit("你好。", out _);
        model.ShowError("Unable to start.");
        Check(!model.IsWaiting && model.Error is not null && model.TrySubmit("重试一下。", out _), "Start failure could not be retried.");
        model.Close();
        model.Close();
        model.ShowError("late response");
        Check(model.IsClosed && !model.IsWaiting, "Closed state reopened.");
        Check(!model.TrySubmit("迟到的提交", out _) && model.SelectSuggestion(0) is null, "Closed panel accepted input.");
        var unopened = new DialogueComposerModel("Abigail", "sun", 0);
        unopened.Close();
        Check(!unopened.TrySubmit("取消后提交", out _), "Cancelled editor accepted submit.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

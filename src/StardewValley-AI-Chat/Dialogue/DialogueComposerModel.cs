namespace StardewAI.Dialogue;

public sealed class DialogueComposerModel
{
    public const int MaximumMessageLength = 500;
    public IReadOnlyList<string> Suggestions { get; }
    public bool IsWaiting { get; private set; }
    public bool IsClosed { get; private set; }
    public string? Error { get; private set; }

    public DialogueComposerModel(string npcName, string weather, int hearts)
        : this(npcName, weather, hearts, null)
    {
    }

    public DialogueComposerModel(string npcName, string weather, int hearts, IReadOnlyList<string>? suggestions)
    {
        IReadOnlyList<string>? generated = DialogueTextRules.NormalizeSuggestions(suggestions);
        if (generated is not null)
        {
            Suggestions = generated;
            return;
        }
        string weatherLine = weather switch
        {
            "rain" => "今天下雨了，你打算怎么度过？",
            "storm" => "外面雷声很响，你喜欢这样的天气吗？",
            "snow" => "下雪的时候，你最喜欢做什么？",
            "wind" => "今天风很大，要不要聊聊这个季节？",
            "green_rain" => "今天的绿色雨很特别，你怎么看？",
            _ => "今天天气不错，你有什么打算？"
        };
        string hobbyLine = npcName switch
        {
            "Abigail" => "最近玩了什么有趣的游戏？",
            "Sebastian" => "最近的编程项目进展怎么样？",
            "Leah" => "最近有什么新的创作想法？",
            _ => "最近有什么让你感兴趣的事情？"
        };
        string relationshipLine = hearts >= 6
            ? "和你聊天总是很开心，今天有什么想分享的吗？"
            : "我想多了解你一些，你平时喜欢去哪里？";
        Suggestions = Array.AsReadOnly(new[] { weatherLine, hobbyLine, relationshipLine });
    }

    public string? SelectSuggestion(int index)
    {
        if (IsWaiting || IsClosed || index < 0 || index >= Suggestions.Count)
            return null;
        Error = null;
        return Suggestions[index];
    }

    public bool TrySubmit(string input, out string message)
    {
        message = input.Trim();
        if (IsWaiting || IsClosed)
            return false;
        if (message.Length == 0)
            Error = "请选择一条建议，或输入你想说的话。";
        else if (message.Length > MaximumMessageLength)
            Error = $"内容最多 {MaximumMessageLength} 字，请缩短后发送。";
        else if (message.Any(char.IsControl))
            Error = "请输入单行文字。";
        else
        {
            Error = null;
            IsWaiting = true;
            return true;
        }
        return false;
    }

    public void ShowError(string error)
    {
        if (IsClosed)
            return;
        IsWaiting = false;
        Error = error;
    }

    public void Close()
    {
        IsClosed = true;
        IsWaiting = false;
    }
}

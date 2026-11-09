using System.Text.Json;

namespace StardewAI.Dialogue;

// Stored on the farmer so conversation rewards and their marker share the game's save transaction.
public sealed record DailyConversationState(int Day, bool OpeningShown = false,
    string OpeningText = "", int CompletedCount = 0, bool RewardSettled = false)
{
    public static DailyConversationState Read(string? json, int day)
    {
        if (json is null)
            return new(day);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(nameof(Day), out _)
            || !document.RootElement.TryGetProperty(nameof(OpeningShown), out _)
            || !document.RootElement.TryGetProperty(nameof(OpeningText), out _)
            || !document.RootElement.TryGetProperty(nameof(CompletedCount), out _)
            || !document.RootElement.TryGetProperty(nameof(RewardSettled), out _))
            throw new InvalidDataException("Incomplete daily conversation state.");
        DailyConversationState state = JsonSerializer.Deserialize<DailyConversationState>(json)
            ?? throw new InvalidDataException("Daily conversation state is null.");
        if (state.Day < 0 || state.CompletedCount < 0 || state.OpeningText is null || state.OpeningText.Length > 4096)
            throw new InvalidDataException("Invalid daily conversation state.");
        return state.Day == day ? state : new(day);
    }

    public bool CanStart(bool isPartner, int dailyLimit, int partnerLimit)
    {
        int limit = Math.Max(0, isPartner ? partnerLimit : dailyLimit);
        return limit == 0 || CompletedCount < limit;
    }

    public int GetFriendshipReward(int? tier, bool alreadyTalked)
        => !RewardSettled && !alreadyTalked && tier is >= 0 and <= 2 ? tier.Value * 10 : 0;

    public DailyConversationState Complete()
        => this with { CompletedCount = checked(CompletedCount + 1), RewardSettled = true };

    public string ToJson() => JsonSerializer.Serialize(this);
}

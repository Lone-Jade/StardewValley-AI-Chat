using System.Text.Json;
using StardewAI.Dialogue;

namespace StardewAI.OfflineTests;

internal static class DailyConversationTests
{
    public static void LimitsAndReset()
    {
        var state = DailyConversationState.Read(null, 12);
        Check(!state.OpeningShown && state.CompletedCount == 0, "New day must begin with native opening.");
        state = state with { OpeningShown = true, OpeningText = "今天的树林很安静。" };
        for (int count = 0; count < 5; count++)
        {
            Check(state.CanStart(false, 5, 0), "Normal conversation rejected too early.");
            state = state.Complete();
        }
        Check(!state.CanStart(false, 5, 0), "Daily cap was bypassed.");
        Check(state.CanStart(true, 5, 0) && state.CanStart(false, 0, 3), "Unlimited setting failed.");
        Check(!state.CanStart(true, 5, 3) && state.CanStart(true, 5, 6), "Partner setting ignored.");
        Check(DailyConversationState.Read(state.ToJson(), 12) == state, "Same-day state did not survive save/reload.");
        Check(DailyConversationState.Read(state.ToJson(), 13) == new DailyConversationState(13), "Day rollover retained counters.");
        var cancelled = state with { CompletedCount = 0, RewardSettled = false };
        Check(DailyConversationState.Read(cancelled.ToJson(), 12) == cancelled,
            "Opening alone consumed a round or reward.");
    }

    public static void RewardsAndCorruption()
    {
        var state = new DailyConversationState(12, true);
        foreach (int tier in new[] { 0, 1, 2 })
            Check(state.GetFriendshipReward(tier, false) == tier * 10, "Three-tier reward mismatch.");
        foreach (int? tier in new int?[] { null, -1, 3, int.MaxValue })
            Check(state.GetFriendshipReward(tier, false) == 0, "Invalid score granted points.");
        Check(state.GetFriendshipReward(2, true) == 0, "Native conversation reward stacked.");
        Check(state.Complete().GetFriendshipReward(2, false) == 0, "Repeated conversation granted points.");
        foreach (string raw in new[] { "null", "not json", "{}", "{\"Day\":-1}", "{\"Day\":12,\"CompletedCount\":-1}",
            "{\"Day\":12,\"OpeningText\":null}" })
        {
            try { DailyConversationState.Read(raw, 12); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { continue; }
            throw new InvalidOperationException("Corrupt daily state accepted.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

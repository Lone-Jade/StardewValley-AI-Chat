using System.Security.Cryptography;
using System.Text;

namespace StardewAI.Dialogue;

public sealed record DialogueContext(string Weather, int Hearts);

public sealed record DialogueSafety(bool WorldReady, bool PlayerFree, bool EventActive,
    bool MenuOpen, bool FestivalDay, bool PassiveFestivalDay)
{
    public string? GetSkipReason()
    {
        if (!WorldReady)
            return "Load a save and wait for the day to begin.";
        if (EventActive)
            return "An event is active.";
        if (MenuOpen)
            return "A menu or dialogue is already open.";
        if (FestivalDay || PassiveFestivalDay)
            return "Today is a festival or passive festival day.";
        if (!PlayerFree)
            return "The player is busy.";
        return null;
    }
}

public sealed record DialogueResult(DialogueEntry? Entry, string? SkipReason);

public sealed class DialogueEngine
{
    private readonly DialogueHistoryStore history;
    private readonly Random random;

    public DialogueEngine(DialogueHistoryStore history, Random random)
    {
        this.history = history;
        this.random = random;
    }

    public DialogueResult Show(DialoguePool pool, DialogueIdentity identity, int day,
        DialogueContext context, DialogueSafety safety, Action<DialogueEntry> display)
    {
        string? reason = safety.GetSkipReason();
        if (reason is not null)
            return new DialogueResult(null, reason);

        DialogueEntry? entry = Select(pool, context, history.GetUsed(identity, day));
        if (entry is null)
            return new DialogueResult(null, "No matching unused dialogue is available today.");

        display(entry);
        history.RecordShown(identity, day, entry.Id);
        return new DialogueResult(entry, null);
    }

    public DialogueEntry? Select(DialoguePool pool, DialogueContext context, IReadOnlySet<string> used)
    {
        List<DialogueEntry> candidates = pool.Dialogues.Where(entry =>
            !used.Contains(entry.Id)
            && (entry.Weather is null || entry.Weather == context.Weather)
            && (entry.MinHearts is null || context.Hearts >= entry.MinHearts)
            && (entry.MaxHearts is null || context.Hearts <= entry.MaxHearts)).ToList();

        long totalWeight = candidates.Sum(entry => (long)entry.Weight);
        if (totalWeight == 0)
            return null;

        long ticket = random.NextInt64(totalWeight);
        foreach (DialogueEntry entry in candidates)
        {
            if (ticket < entry.Weight)
                return entry;
            ticket -= entry.Weight;
        }
        throw new InvalidOperationException("Weighted dialogue selection did not resolve a candidate.");
    }

    public DialogueResult ShowGenerated(string text, DialogueIdentity identity, int day,
        DialogueSafety safety, Action<DialogueEntry> display)
    {
        string? reason = safety.GetSkipReason();
        if (reason is not null)
            return new DialogueResult(null, reason);
        string id = "ai_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (history.GetUsed(identity, day).Contains(id))
            return new DialogueResult(null, "The generated line was already shown today.");
        var entry = new DialogueEntry { Id = id, Text = text, Weight = 1 };
        display(entry);
        history.RecordShown(identity, day, id);
        return new DialogueResult(entry, null);
    }
}

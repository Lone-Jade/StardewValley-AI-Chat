namespace StardewAI.Dialogue;

public sealed record MemoryCommand(string Action, string? NpcName, string? Value)
{
    public static MemoryCommand? Parse(string[] args)
    {
        if (args.Length == 0)
            return null;
        if (args.Any(string.IsNullOrWhiteSpace))
            return null;
        string action = args[0].ToLowerInvariant();
        if (action is "list" or "reload" && args.Length is 1 or 2)
        {
            string? defaultNpc = action == "list" ? "Abigail" : null;
            return new(action, args.Length == 2 ? args[1] : defaultNpc, null);
        }
        if (action == "clear" && args.Length == 2)
            return new(action, args[1], null);
        if (action is "forget" or "forget-category" && args.Length == 3
            && (action != "forget-category" || LongTermPreferenceRules.Categories.Contains(args[2])))
            return new(action, args[1], args[2]);
        return null;
    }
}

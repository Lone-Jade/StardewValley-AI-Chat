using System.Text.Json;

namespace StardewAI.Dialogue;

public sealed class DialogueEntry
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public int Weight { get; set; }
    public string? Weather { get; set; }
    public int? MinHearts { get; set; }
    public int? MaxHearts { get; set; }
}

public sealed class DialoguePool
{
    public List<DialogueEntry> Dialogues { get; set; } = new();

    public static string ResolvePath(string directory, string npcName)
    {
        // Mod NPC names are identities, and may contain characters unsuitable for file names.
        bool safeName = !string.IsNullOrWhiteSpace(npcName) && npcName is not ("." or "..")
            && !npcName.Any(character => Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\');
        string path = Path.Combine(directory, safeName ? npcName + ".json" : "Default.json");
        return File.Exists(path) ? path : Path.Combine(directory, "Default.json");
    }

    public static DialoguePool Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        DialoguePool pool = JsonSerializer.Deserialize<DialoguePool>(file,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Dialogue pool is null.");
        pool.Validate();
        return pool;
    }

    public void Validate()
    {
        if (Dialogues is null || Dialogues.Count == 0)
            throw new InvalidDataException("The dialogues array must contain at least one entry.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (DialogueEntry entry in Dialogues)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidDataException("Each dialogue must have a nonempty id.");
            if (!ids.Add(entry.Id))
                throw new InvalidDataException($"Duplicate dialogue id '{entry.Id}'.");
            if (string.IsNullOrWhiteSpace(entry.Text))
                throw new InvalidDataException($"Dialogue '{entry.Id}' has empty text.");
            if (entry.Weight <= 0)
                throw new InvalidDataException($"Dialogue '{entry.Id}' must have a positive integer weight.");
            if (entry.Weather is not null && entry.Weather is not ("sun" or "rain" or "storm" or "snow" or "wind" or "green_rain"))
                throw new InvalidDataException($"Dialogue '{entry.Id}' has unknown weather '{entry.Weather}'.");
            if (entry.MinHearts < 0 || entry.MaxHearts < 0 || entry.MinHearts > entry.MaxHearts)
                throw new InvalidDataException($"Dialogue '{entry.Id}' has an invalid hearts range.");
        }
    }
}

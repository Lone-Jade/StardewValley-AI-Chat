using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StardewAI.Dialogue;

public sealed record DialogueIdentity(string SaveFolder, long PlayerId, string NpcName)
{
    public string GetStorageFileName()
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
        return Convert.ToHexString(key) + ".json";
    }
}

public sealed class DialogueHistory
{
    public int Version { get; set; }
    public DialogueIdentity? Identity { get; set; }
    public Dictionary<int, HashSet<string>> Days { get; set; } = null!;
}

public sealed class DialogueHistoryStore
{
    private readonly string directory;
    private readonly Dictionary<DialogueIdentity, DialogueHistory> loaded = new();
    private readonly Dictionary<DialogueIdentity, Exception> failures = new();

    public DialogueHistoryStore(string directory)
    {
        this.directory = directory;
    }

    public void Clear()
    {
        loaded.Clear();
        failures.Clear();
    }

    public string GetPath(DialogueIdentity identity)
    {
        // Keep the existing structured-identity hash for previously created records.
        return Path.Combine(directory, identity.GetStorageFileName());
    }

    public IReadOnlySet<string> GetUsed(DialogueIdentity identity, int day)
    {
        DialogueHistory history = Load(identity);
        return history.Days.TryGetValue(day, out HashSet<string>? ids)
            ? ids
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public void RecordShown(DialogueIdentity identity, int day, string dialogueId)
    {
        DialogueHistory history = Load(identity);
        if (!history.Days.TryGetValue(day, out HashSet<string>? ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            history.Days.Add(day, ids);
        }
        ids.Add(dialogueId);

        string path = GetPath(identity);
        try
        {
            AtomicJsonFile.Write(path, history);
        }
        catch (Exception ex) when (AtomicJsonFile.IsStorageError(ex))
        {
            failures[identity] = ex;
            throw new IOException($"Failed to persist dialogue history '{path}'. Dialogue is disabled for this record.", ex);
        }
    }

    private DialogueHistory Load(DialogueIdentity identity)
    {
        string path = GetPath(identity);
        if (failures.TryGetValue(identity, out Exception? failure))
            throw new IOException($"Dialogue history '{path}' is disabled until the save is reloaded.", failure);
        if (loaded.TryGetValue(identity, out DialogueHistory? cached))
            return cached;

        try
        {
            DialogueHistory history;
            try
            {
                using FileStream file = File.OpenRead(path);
                history = JsonSerializer.Deserialize<DialogueHistory>(file)
                    ?? throw new InvalidDataException("History is null.");
            }
            catch (FileNotFoundException)
            {
                history = new DialogueHistory { Version = 1, Identity = identity, Days = new() };
            }
            catch (DirectoryNotFoundException)
            {
                history = new DialogueHistory { Version = 1, Identity = identity, Days = new() };
            }

            if (history.Version != 1 || history.Identity != identity || history.Days is null
                || history.Days.Any(pair => pair.Key < 0 || pair.Value is null || pair.Value.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidDataException("Invalid history version, identity, date, or dialogue IDs.");

            loaded.Add(identity, history);
            return history;
        }
        catch (Exception ex) when (AtomicJsonFile.IsStorageError(ex))
        {
            failures[identity] = ex;
            throw new IOException($"Failed to read dialogue history '{path}'. Dialogue is disabled for this record.", ex);
        }
    }

}

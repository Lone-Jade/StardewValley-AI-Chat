using System.Security.Cryptography;
using System.Text;

namespace StardewAI.Dialogue;

public static class ConversationStateKeys
{
    private const string Prefix = "StardewValleyAIChat.Core";
    private const string LegacyPrefixDigest = "E2FF1D33503E6A6C7FC52DEA7E8436EB70F93A1264576B23427B9EFC8C878D03";

    public static string ForNpc(string npcName) => Prefix + "/Conversation/" + npcName;

    public static string? Read(IEnumerable<KeyValuePair<string, string>> data, string npcName)
    {
        string currentKey = ForNpc(npcName);
        string suffix = "/Conversation/" + npcName;
        string? legacyValue = null;
        foreach (KeyValuePair<string, string> entry in data)
        {
            if (entry.Key == currentKey)
                return entry.Value;
            if (!entry.Key.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            string prefix = entry.Key[..^suffix.Length];
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix)));
            if (digest == LegacyPrefixDigest)
                legacyValue = entry.Value;
        }
        return legacyValue;
    }
}

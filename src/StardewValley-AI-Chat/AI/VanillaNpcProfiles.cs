using System.Collections.ObjectModel;
using System.Text.Json;

namespace StardewAI.AI;

public sealed record NpcPersona(string NpcName, string Background, string Voice,
    string Boundaries, string Knowledge, string SourceUrl);

/// <summary>Reviewed vanilla reference facts. Runtime identity and state take precedence.</summary>
public static class VanillaNpcProfiles
{
    private const string ResourceName = "StardewAI.VanillaNpcProfiles.json";
    private static readonly Lazy<IReadOnlyDictionary<string, NpcPersona>> Profiles = new(Load);

    public static IReadOnlyList<string> Names => Array.AsReadOnly(Profiles.Value.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());

    public static NpcPersona? Find(string npcName) => Profiles.Value.TryGetValue(npcName, out NpcPersona? profile) ? profile : null;

    private static IReadOnlyDictionary<string, NpcPersona> Load()
    {
        using Stream stream = typeof(VanillaNpcProfiles).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Vanilla NPC profile resource is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyDictionary<string, NpcPersona> Parse(JsonElement root)
    {
        if (root.GetProperty("Version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported vanilla NPC profile version.");
        var profiles = new Dictionary<string, NpcPersona>(StringComparer.Ordinal);
        foreach (JsonElement element in root.GetProperty("Profiles").EnumerateArray())
        {
            NpcPersona? profile = element.Deserialize<NpcPersona>();
            if (profile is null || string.IsNullOrWhiteSpace(profile.NpcName) || profile.NpcName.Length > 80
                || new[] { profile.Background, profile.Voice, profile.Boundaries, profile.Knowledge }
                    .Any(text => string.IsNullOrWhiteSpace(text) || text.Length > 300 || text.Any(char.IsControl))
                || !Uri.TryCreate(profile.SourceUrl, UriKind.Absolute, out Uri? source)
                || source.Scheme != "https" || source.Host != "stardewvalleywiki.com" || source.AbsolutePath == "/"
                || !profiles.TryAdd(profile.NpcName, profile))
                throw new InvalidDataException("Invalid or duplicate vanilla NPC profile.");
        }
        if (profiles.Count == 0)
            throw new InvalidDataException("Vanilla NPC profile catalog is empty.");
        return new ReadOnlyDictionary<string, NpcPersona>(profiles);
    }
}

public static class NpcRelationship
{
    // Native friendship status is captured on the game thread, never inferred from heart count.
    public static string FromGameState(string? status, bool roommate, bool canSocialize) => status switch
    {
        "Divorced" => "divorced",
        "Married" => roommate ? "roommate" : "married",
        "Engaged" => roommate ? "roommate_pending" : "engaged",
        "Dating" => "dating",
        "Friendly" => canSocialize ? "friendly" : "non_social",
        _ => canSocialize ? "unknown" : "non_social"
    };
}

public static class NpcRuntimeTraits
{
    // Values correspond to the native NpcAge, NpcManner, NpcSocialAnxiety and NpcOptimism enums.
    public static string Describe(int age, int manners, int socialAnxiety, int optimism, bool canSocialize)
    {
        string ageText = age switch { 0 => "成年", 1 => "青年", 2 => "儿童", _ => "未知" };
        string mannerText = manners switch { 0 => "待人态度中性", 1 => "礼貌", 2 => "直率粗鲁", _ => "待人态度未知" };
        string socialText = socialAnxiety switch { 0 => "外向", 1 => "内向害羞", 2 => "社交倾向中性", _ => "社交倾向未知" };
        string outlook = optimism switch { 0 => "偏乐观", 1 => "偏悲观", 2 => "心态中性", _ => "心态未知" };
        return $"年龄阶段 {ageText}。{mannerText}，{socialText}，{outlook}。可建立原生好感度 {canSocialize}。";
    }
}

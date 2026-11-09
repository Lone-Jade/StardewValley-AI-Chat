using System.Text.Json.Serialization;

namespace StardewAI.AI;

public class AiConfiguration
{
    public DeepSeekOptions AI { get; set; } = null!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeepSeekOptions? DeepSeek { get; set; }

    // SMAPI's Newtonsoft.Json serializer observes this convention during config writes.
    public bool ShouldSerializeDeepSeek() => false;

    public void NormalizeAI()
    {
        AI ??= DeepSeek ?? new DeepSeekOptions();
        DeepSeek = null;
    }
}

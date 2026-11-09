using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewAI.AI;
using StardewAI.Dialogue;

namespace StardewAI;

public sealed class ModConfig : AiConfiguration
{
    public bool EnableRightClickMenu { get; set; } = true;
    public KeybindList DialogueKey { get; set; } = new(SButton.K);
    public int DailyDialogueLimit { get; set; } = 5;
    public int PartnerDailyDialogueLimit { get; set; } = 0;
    public LongTermMemoryOptions LongTermMemory { get; set; } = new();
}

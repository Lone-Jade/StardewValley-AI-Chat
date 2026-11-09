using HarmonyLib;
using StardewValley;

namespace StardewAI.Interaction;

// Scope patches to the synchronous native opening only. Other NPC interactions use vanilla behavior.
internal static class VanillaConversation
{
    private static NPC? openingNpc;
    private static Farmer? openingPlayer;

    public static void Register(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(NPC), nameof(NPC.grantConversationFriendship)),
            prefix: new HarmonyMethod(typeof(VanillaConversation), nameof(DeferFriendship)));
        harmony.Patch(AccessTools.Method(typeof(NPC), nameof(NPC.tryToReceiveActiveObject)),
            prefix: new HarmonyMethod(typeof(VanillaConversation), nameof(SkipHeldItem)));
    }

    public static void Open(NPC npc, Farmer player)
    {
        openingNpc = npc;
        openingPlayer = player;
        try
        {
            npc.checkAction(player, npc.currentLocation);
        }
        finally
        {
            openingNpc = null;
            openingPlayer = null;
        }
    }

    private static bool DeferFriendship(NPC __instance, Farmer who)
        => __instance != openingNpc || who != openingPlayer;

    private static bool SkipHeldItem(NPC __instance, Farmer who, ref bool __result)
    {
        if (__instance != openingNpc || who != openingPlayer)
            return true;
        __result = false;
        return false;
    }
}

using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Interaction;

public sealed class NpcInteractionMenu
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Action<string> showDialogue;
    private readonly Action<string> showMemory;
    private Action? pending;
    private int remainingWaitTicks;
    private IClickableMenu? awaitingMenu;

    public NpcInteractionMenu(IModHelper helper, IMonitor monitor, Action<string> showDialogue, Action<string> showMemory)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.showDialogue = showDialogue;
        this.showMemory = showMemory;
    }

    public void Clear()
    {
        pending = null;
        remainingWaitTicks = 0;
        awaitingMenu = null;
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // Native question menus animate closed after invoking the response callback.
        bool closing = Game1.activeClickableMenu == awaitingMenu && awaitingMenu is not null;
        bool settling = Game1.activeClickableMenu is null && Context.IsWorldReady && !Context.IsPlayerFree && !Game1.eventUp;
        if (pending is not null && remainingWaitTicks > 0 && (closing || settling))
        {
            remainingWaitTicks--;
            return;
        }
        Action? action = pending;
        pending = null;
        awaitingMenu = null;
        remainingWaitTicks = 0;
        if (action is null)
            return;
        try
        {
            action();
        }
        catch (Exception error)
        {
            monitor.Log($"NPC menu action failed: {error}", LogLevel.Error);
        }
    }

    public void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseRight || helper.Input.IsSuppressed(e.Button) || pending is not null
            || !IsSafe() || Game1.player.isRidingHorse() || Game1.player.UsingTool)
            return;

        Vector2 cursor = e.Cursor.AbsolutePixels;
        NPC? target = Game1.currentLocation.characters
            .Where(npc => IsAvailable(npc) && GetClickBounds(npc).Contains((int)cursor.X, (int)cursor.Y))
            .OrderBy(npc => Vector2.DistanceSquared(npc.Tile, Game1.player.Tile))
            .ThenBy(npc => npc.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (target is null)
            return;

        helper.Input.Suppress(e.Button);
        Open(target);
    }

    private static Rectangle GetClickBounds(NPC npc)
    {
        Rectangle bounds = npc.GetBoundingBox();
        bounds.Y -= 64;
        bounds.Height += 64;
        return bounds;
    }

    private static bool IsSafe()
    {
        return Context.IsWorldReady && Context.IsPlayerFree && !Game1.eventUp
            && !Utility.isFestivalDay() && !Utility.IsPassiveFestivalDay();
    }

    public static bool IsAvailable(NPC npc)
    {
        return npc.IsVillager && !string.IsNullOrWhiteSpace(npc.Name)
            && npc.currentLocation == Game1.currentLocation && !npc.IsInvisible && !npc.isSleeping.Value
            && Vector2.DistanceSquared(npc.Tile, Game1.player.Tile) <= 4;
    }

    private void Open(NPC npc)
    {
        Farmer player = Game1.player;
        string? save = Constants.SaveFolderName;
        GameLocation location = Game1.currentLocation;
        Item? held = player.ActiveObject;
        int stack = held?.Stack ?? 0;
        bool selected = false;
        string giftLabel = held is null ? "赠送（请先手持物品）" : $"赠送：{held.DisplayName}";
        var choices = new[]
        {
            new Response("talk", "对话"),
            new Response("gift", giftLabel),
            new Response("deliver", "提交任务"),
            new Response("info", "信息"),
            new Response("memory", "记忆"),
            new Response("cancel", "取消")
        };
        Game1.currentLocation.createQuestionDialogue($"与{npc.displayName}互动", choices, (_, answer) =>
        {
            // A response schedules exactly one action after the native menu has closed.
            if (selected)
                return;
            selected = true;
            if (answer == "cancel")
            {
                monitor.Log($"NPC menu cancelled for {npc.Name}.", LogLevel.Info);
                return;
            }
            remainingWaitTicks = 120;
            awaitingMenu = Game1.activeClickableMenu;
            pending = () =>
            {
                if (!IsSafe() || Constants.SaveFolderName != save || Game1.player != player
                    || Game1.currentLocation != location || !IsAvailable(npc))
                {
                    Notify("交互条件已变化，请重新右键选择。");
                    monitor.Log($"Menu validation: ready={Context.IsWorldReady}, free={Context.IsPlayerFree}, event={Game1.eventUp}"
                        + $", menu={Game1.activeClickableMenu?.GetType().Name}, sameSave={Constants.SaveFolderName == save}"
                        + $", samePlayer={Game1.player == player}, sameLocation={Game1.currentLocation == location}.", LogLevel.Trace);
                    return;
                }
                if (answer is "gift" or "deliver"
                    && (held is null || !ReferenceEquals(player.ActiveObject, held) || held.Stack != stack))
                {
                    Notify("请先手持要赠送或提交的物品，并重新选择。");
                    return;
                }

                switch (answer)
                {
                    case "talk": showDialogue(npc.Name); break;
                    case "gift": GiveGift(npc, player); break;
                    case "deliver": Deliver(npc, player); break;
                    case "info": ShowProfile(npc); break;
                    case "memory": showMemory(npc.Name); break;
                }
            };
        });
        monitor.Log($"Opened NPC menu for {npc.Name}.", LogLevel.Info);
    }

    private void GiveGift(NPC npc, Farmer player)
    {
        if (!npc.CanReceiveGifts())
        {
            Notify("这位角色当前不接受普通礼物。");
            return;
        }
        StardewValley.Object item = player.ActiveObject;
        if (TryDeliver(npc, player, item, probe: true))
        {
            Notify("这件物品可用于任务，请选择“提交任务”。");
            return;
        }
        // Keep proposals and other special item interactions on their native entry point.
        if (item.questItem.Value || item.QualifiedItemId is "(O)458" or "(O)460" or "(O)808" or "(O)277" or "(O)809"
            || !item.canBeGivenAsGift() || ItemContextTagManager.HasBaseTag(item.QualifiedItemId, "not_giftable"))
        {
            Notify("该物品需要原版特殊交互，或无法作为普通礼物赠送。");
            return;
        }
        int before = item.Stack;
        npc.tryToReceiveActiveObject(player);
        monitor.Log($"Gift action for {npc.Name}: item={item.QualifiedItemId}, consumed={before - item.Stack}.", LogLevel.Info);
    }

    private void Deliver(NPC npc, Farmer player)
    {
        if (!TryDeliver(npc, player, player.ActiveObject, probe: false))
        {
            Notify("当前物品没有匹配该角色的可交付任务。");
            return;
        }
        player.completelyStopAnimatingOrDoingAction();
        monitor.Log($"Task delivery completed for {npc.Name}.", LogLevel.Info);
    }

    private static bool TryDeliver(NPC npc, Farmer player, Item item, bool probe)
    {
        // Match the vanilla task phase only. No gift or special-item fallback follows it.
        foreach (var order in player.team.specialOrders)
        {
            if (order.onItemDelivered is null)
                continue;
            foreach (Delegate callback in order.onItemDelivered.GetInvocationList())
            {
                if (((Func<Farmer, NPC, Item, bool, int>)callback)(player, npc, item, probe) <= 0)
                    continue;
                if (!probe && item.Stack <= 0)
                {
                    player.ActiveObject = null;
                    player.showNotCarrying();
                }
                return true;
            }
        }
        return player.NotifyQuests(quest => quest.OnItemOfferedToNpc(npc, item, probe), onlyOneQuest: true);
    }

    private void ShowProfile(NPC npc)
    {
        var page = new SocialPage(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
        SocialPage.SocialEntry? entry = page.SocialEntries.FirstOrDefault(entry => entry.Character == npc);
        if (entry is null || !entry.IsMet)
        {
            Notify("请先通过原版交谈认识这位角色，再查看人物资料。");
            return;
        }
        Game1.activeClickableMenu = new ProfileMenu(entry, page.SocialEntries);
        monitor.Log($"Opened native profile for {npc.Name}.", LogLevel.Info);
    }

    private void Notify(string message)
    {
        Game1.addHUDMessage(new HUDMessage(message));
        monitor.Log(message, LogLevel.Info);
    }
}

using System.Globalization;
using System.Net.Http;
using HarmonyLib;
using StardewAI.AI;
using StardewAI.Dialogue;
using StardewAI.Interaction;
using StardewAI.Integration;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI;

public sealed class ModEntry : Mod
{
    private readonly Dictionary<string, DialoguePool?> dialoguePools = new(StringComparer.Ordinal);
    private DialogueHistoryStore dialogueHistory = null!;
    private DialogueEngine dialogueEngine = null!;
    private ConversationMemoryStore conversationMemory = null!;
    private ModConfig config = null!;
    private NpcInteractionMenu interactionMenu = null!;
    private static readonly HttpClient AiHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private AiDialogueQueue aiQueue = null!;
    private NPC? pendingAiNpc;
    private DialoguePool? pendingAiPool;
    private AiDialogueRequest? pendingAiRequest;
    private DialogueComposerMenu? composerMenu;
    private MemoryManagerMenu? memoryMenu;
    private NPC? composerNpc;
    private AiScene? composerScene;
    private OpeningConversation? openingConversation;
    private bool lowOutputBudgetWarningLogged;
    private sealed record OpeningConversation(NPC Npc, AiScene Scene, DialogueBox Menu);

    public override void Entry(IModHelper helper)
    {
        config = helper.ReadConfig<ModConfig>();
        config.DialogueKey ??= new ModConfig().DialogueKey;
        config.NormalizeAI();
        NormalizeConfig();
        helper.WriteConfig(config);
        aiQueue = new AiDialogueQueue(new DeepSeekClient(AiHttp));
        interactionMenu = new NpcInteractionMenu(helper, Monitor,
            OpenComposer, OpenMemory);
        dialogueHistory = new DialogueHistoryStore(Path.Combine(helper.DirectoryPath, "Data", "DialogueHistory"));
        dialogueEngine = new DialogueEngine(dialogueHistory, new Random());
        conversationMemory = new ConversationMemoryStore(Path.Combine(helper.DirectoryPath, "Data", "ConversationMemory"));
        VanillaConversation.Register(new Harmony(ModManifest.UniqueID));
        helper.ConsoleCommands.Add("stardewai_dialogue",
            "Show a conditional NPC dialogue. Usage: stardewai_dialogue [NPC internal name], default Abigail.",
            OnDialogueCommand);
        helper.ConsoleCommands.Add("stardewai_ai",
            "Request an AI NPC line, falling back to the local pool. Usage: stardewai_ai [NPC internal name] [player message].",
            OnAiCommand);
        helper.ConsoleCommands.Add("stardewai_memory",
            "Manage NPC memory. Usage: stardewai_memory list [NPC] | forget NPC ID | forget-category NPC category | clear NPC | reload [NPC]",
            OnMemoryCommand);
        helper.ConsoleCommands.Add(
            "stardewai_status",
            "Read an NPC's current state. Usage: stardewai_status [NPC internal name], default Abigail.",
            OnStatusCommand);

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.GameLaunched += (_, _) => RegisterConfigMenu();
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Input.ButtonsChanged += OnButtonsChanged;
        helper.Events.Input.ButtonPressed += (_, e) =>
        {
            if (config.EnableRightClickMenu)
                interactionMenu.OnButtonPressed(this, e);
        };
        helper.Events.GameLoop.UpdateTicked += interactionMenu.OnUpdateTicked;
        helper.Events.GameLoop.UpdateTicked += OnAiUpdateTicked;
        helper.Events.Display.MenuChanged += (_, e) =>
        {
            if (memoryMenu is not null && e.NewMenu != memoryMenu)
            {
                memoryMenu.DetachInput();
                memoryMenu = null;
            }
            if (e.NewMenu != composerMenu)
                CancelAi();
        };

        if (config.AI.Enabled && config.AI.GetUnavailableReason() is string reason)
            Monitor.Log($"AI provider unavailable ({reason}). Local dialogue remains available.", LogLevel.Warn);
        WarnLowOutputBudget();

        Monitor.Log("Ready. Use stardewai_status Abigail after loading a save.", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        openingConversation = null;
        CancelAi();
        CloseMemory();
        interactionMenu.Clear();
        dialogueHistory.Clear();
        conversationMemory.Clear();
        Monitor.Log("Save loaded. NPC state commands are available.", LogLevel.Info);
    }

    private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (!config.DialogueKey.JustPressed())
            return;
        if (!Context.IsWorldReady || !Context.IsPlayerFree)
            return;

        NPC? target = Game1.currentLocation.characters
            .Where(npc => NpcInteractionMenu.IsAvailable(npc)
                && DialogueTargeting.IsInReach(npc.Tile.X - Game1.player.Tile.X,
                    npc.Tile.Y - Game1.player.Tile.Y, Game1.player.FacingDirection))
            .OrderBy(npc => Microsoft.Xna.Framework.Vector2.DistanceSquared(npc.Tile, Game1.player.Tile))
            .ThenBy(npc => npc.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (target is null)
        {
            Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("hud.approach-npc").ToString()));
            return;
        }

        Helper.Input.SuppressActiveKeybinds(config.DialogueKey);
        OpenComposer(target.Name);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        openingConversation = null;
        CancelAi();
        CloseMemory();
        Monitor.Log($"Day started: year={Game1.year}, season={Game1.currentSeason}, day={Game1.dayOfMonth}.", LogLevel.Info);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        openingConversation = null;
        CancelAi();
        CloseMemory();
        interactionMenu.Clear();
        dialogueHistory.Clear();
        conversationMemory.Clear();
        Monitor.Log("Returned to title. NPC state commands require a loaded save.", LogLevel.Info);
    }

    private void OnStatusCommand(string command, string[] args)
    {
        if (args.Length > 1)
        {
            Monitor.Log("Usage: stardewai_status [NPC internal name], e.g. stardewai_status Abigail", LogLevel.Warn);
            return;
        }

        if (!Context.IsWorldReady)
        {
            Monitor.Log("No world is ready. Load a save and wait for the day to begin.", LogLevel.Info);
            return;
        }

        string npcName = args.Length == 0 ? "Abigail" : args[0];
        NPC? npc = Game1.getCharacterFromName(npcName, mustBeVillager: true, includeEventActors: false);
        if (npc is null)
        {
            Monitor.Log($"NPC '{npcName}' was not found. Use an internal name such as Abigail, Sebastian, or Leah.", LogLevel.Warn);
            return;
        }

        GameLocation? location = npc.currentLocation;
        if (location is null)
        {
            Monitor.Log($"NPC '{npc.Name}' has no current location.", LogLevel.Info);
            return;
        }

        int friendshipPoints = Game1.player.friendshipData.TryGetValue(npc.Name, out Friendship? friendship)
            ? friendship.Points
            : 0;
        int hearts = Game1.player.getFriendshipHeartLevelForNPC(npc.Name);
        string position = string.Create(CultureInfo.InvariantCulture, $"({npc.Tile.X:0.##}, {npc.Tile.Y:0.##})");

        Monitor.Log(
            $"NPC={npc.Name} ({npc.displayName}) | location={location.NameOrUniqueName} | tile={position}"
            + $" | year={Game1.year}, season={Game1.currentSeason}, day={Game1.dayOfMonth}, time={Game1.timeOfDay:0000}"
            + $" | weather={GetWeather(location)} | friendshipPoints={friendshipPoints}, hearts={hearts}"
            + $" | eventActive={Game1.eventUp}",
            LogLevel.Info);
    }

    private void OnDialogueCommand(string command, string[] args)
    {
        if (args.Length > 1)
        {
            Monitor.Log("Usage: stardewai_dialogue [NPC internal name], e.g. stardewai_dialogue Abigail", LogLevel.Warn);
            return;
        }

        BeginDialogue(args.Length == 0 ? "Abigail" : args[0], "", reportUnavailable: false);
    }

    private void OnAiCommand(string command, string[] args)
    {
        string message = string.Join(" ", args.Skip(1));
        if (message.Length > 500)
        {
            Monitor.Log("Player message must be at most 500 characters.", LogLevel.Warn);
            return;
        }
        BeginDialogue(args.Length == 0 ? "Abigail" : args[0], message, reportUnavailable: true);
    }

    private DialogueSafety GetDialogueSafety(DialogueComposerMenu? owner = null)
    {
        bool ready = Context.IsWorldReady;
        bool ownedMenu = owner is not null && owner == composerMenu && Game1.activeClickableMenu == owner;
        // Mirror SMAPI's free-player checks while allowing only this specific owned menu.
        bool playerFree = ready && (Context.IsPlayerFree
            || (ownedMenu && Game1.currentLocation is not null && !Game1.dialogueUp && !Game1.eventUp));
        return new DialogueSafety(ready, playerFree, ready && Game1.eventUp,
            Game1.activeClickableMenu is not null && !ownedMenu,
            ready && Utility.isFestivalDay(), ready && Utility.IsPassiveFestivalDay());
    }

    private void OpenComposer(string name)
    {
        string? reason = GetDialogueSafety().GetSkipReason();
        if (reason is not null || aiQueue.IsBusy)
        {
            Monitor.Log($"Dialogue panel skipped: {reason ?? "A request is pending."}", LogLevel.Info);
            return;
        }
        NPC? npc = Game1.getCharacterFromName(name, mustBeVillager: true, includeEventActors: false);
        if (npc?.currentLocation is null || string.IsNullOrEmpty(Constants.SaveFolderName)
            || !Game1.player.CanMove || Game1.player.UsingTool)
            return;
        try
        {
            DailyConversationState daily = GetDailyState(npc);
            if (!CanStartConversation(npc, daily))
                return;
            if (!daily.OpeningShown && !Game1.player.hasPlayerTalkedToNPC(npc.Name))
            {
                if (npc.CanSocialize)
                    VanillaConversation.Open(npc, Game1.player);
                else if (npc.CurrentDialogue.Count > 0)
                    Game1.drawDialogue(npc);
                if (Game1.activeClickableMenu is DialogueBox box && box.characterDialogue?.speaker == npc)
                {
                    string openingText = string.Join(" ", box.characterDialogue.dialogues.Select(line => line.Text));
                    if (openingText.Length > 4096)
                        openingText = openingText[..4096];
                    SetDailyState(npc, daily with { OpeningShown = true, OpeningText = openingText });
                    openingConversation = new(npc, GetAiScene(npc), box);
                    Monitor.Log($"Showing native first conversation for {npc.Name}.", LogLevel.Info);
                    return;
                }
                // Native special interactions retain their own flow. Try again after they finish.
                if (Game1.activeClickableMenu is not null || !Context.IsPlayerFree)
                    return;
                SetDailyState(npc, daily with { OpeningShown = true });
            }
            else if (!daily.OpeningShown)
                SetDailyState(npc, daily with { OpeningShown = true, RewardSettled = true });
            ShowComposer(npc);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException)
        {
            Monitor.Log($"Daily conversation state is invalid for {npc.Name}: {error.GetType().Name}", LogLevel.Error);
        }
    }

    private void ShowComposer(NPC npc)
    {
        IReadOnlyList<string>? suggestions = null;
        if (config.AI.GetUnavailableReason() is null)
        {
            try
            {
                suggestions = conversationMemory.GetSuggestions(GetDialogueIdentity(npc));
            }
            catch (IOException)
            {
                Monitor.Log($"Conversation memory unavailable for {npc.Name}. Using local suggestions.", LogLevel.Warn);
            }
        }
        var model = new DialogueComposerModel(npc.Name, GetWeather(npc.currentLocation),
            Game1.player.getFriendshipHeartLevelForNPC(npc.Name), suggestions);
        composerNpc = npc;
        composerScene = GetAiScene(npc);
        composerMenu = new DialogueComposerMenu(npc.displayName, model, SendComposer, owner =>
        {
            if (composerMenu == owner)
                CancelAi();
        });
        Game1.activeClickableMenu = composerMenu;
    }

    private void OpenMemory(string name)
    {
        if (GetDialogueSafety().GetSkipReason() is not null || string.IsNullOrEmpty(Constants.SaveFolderName))
            return;
        NPC? npc = Game1.getCharacterFromName(name, mustBeVillager: true, includeEventActors: false);
        if (npc is null || !NpcInteractionMenu.IsAvailable(npc))
            return;
        DialogueIdentity identity = GetDialogueIdentity(npc);
        AiScene scene = GetAiScene(npc);
        Farmer player = Game1.player;
        MemoryManagerMenu? menu = null;
        bool IsCurrent() => Context.IsWorldReady && !Game1.eventUp && Game1.player == player
            && !Utility.isFestivalDay() && !Utility.IsPassiveFestivalDay()
            && Game1.activeClickableMenu == menu && npc.currentLocation is not null
            && Constants.SaveFolderName == identity.SaveFolder && scene.IsCompatible(GetAiScene(npc))
            && ReferenceEquals(npc, Game1.getCharacterFromName(name, mustBeVillager: true, includeEventActors: false));
        var model = new MemoryManagerModel(conversationMemory, identity, Game1.Date.TotalDays, () =>
        {
            if (!IsCurrent())
                throw new InvalidOperationException("The memory menu context changed.");
            MemoryConfigChanged();
        }, error => Monitor.Log($"Memory management failed for {npc.Name}: {error.Message}", LogLevel.Error));
        menu = new MemoryManagerMenu(npc.displayName, model, IsCurrent);
        memoryMenu = menu;
        Game1.activeClickableMenu = menu;
    }

    private void CloseMemory()
    {
        MemoryManagerMenu? menu = memoryMenu;
        memoryMenu = null;
        if (menu is null)
            return;
        menu.DetachInput();
        if (Game1.activeClickableMenu == menu)
            menu.exitThisMenu(playSound: false);
    }

    private void NormalizeConfig()
    {
        config.DailyDialogueLimit = Math.Clamp(config.DailyDialogueLimit, 0, 100);
        config.PartnerDailyDialogueLimit = Math.Clamp(config.PartnerDailyDialogueLimit, 0, 100);
        config.LongTermMemory = (config.LongTermMemory ?? new LongTermMemoryOptions()).Normalize();
    }

    private void WarnLowOutputBudget()
    {
        if (!lowOutputBudgetWarningLogged && config.LongTermMemory.Enabled && config.AI.MaxTokens < 1024)
        {
            lowOutputBudgetWarningLogged = true;
            Monitor.Log("Long-term memory is enabled with an output budget below 1024 tokens. Incomplete responses may fall back locally. Adjust the output limit in GMCM.", LogLevel.Warn);
        }
    }

    private void MemoryConfigChanged()
    {
        openingConversation = null;
        CancelAi();
    }

    private void RegisterConfigMenu()
    {
        IGenericModConfigMenuApi? api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null)
            return;
        api.Register(ModManifest, () =>
        {
            config.DailyDialogueLimit = 5;
            config.PartnerDailyDialogueLimit = 0;
            config.LongTermMemory = new();
            config.AI.MaxTokens = 1024;
            MemoryConfigChanged();
        }, () => { NormalizeConfig(); MemoryConfigChanged(); Helper.WriteConfig(config); WarnLowOutputBudget(); });
        api.AddNumberOption(ModManifest, () => config.DailyDialogueLimit, value => config.DailyDialogueLimit = value,
            () => "普通 NPC 每日对话上限", () => "每名 NPC 分别计数，成功显示回复算一轮。0 为不限。", 0, 100, 1,
            value => value == 0 ? "不限" : $"{value} 轮");
        api.AddNumberOption(ModManifest, () => config.PartnerDailyDialogueLimit, value => config.PartnerDailyDialogueLimit = value,
            () => "伴侣每日对话上限", () => "恋爱、订婚、婚后伴侣及室友适用。0 为不限。", 0, 100, 1,
            value => value == 0 ? "不限" : $"{value} 轮");
        api.AddBoolOption(ModManifest, () => config.LongTermMemory.Enabled,
            value => { config.LongTermMemory = config.LongTermMemory with { Enabled = value }; MemoryConfigChanged(); },
            () => "长期偏好记忆", () => "关闭后停止注入及学习长期偏好，已有记录保留。管理命令继续可用。");
        api.AddNumberOption(ModManifest, () => config.LongTermMemory.MaximumPreferences,
            value => { config.LongTermMemory = config.LongTermMemory with { MaximumPreferences = value }; MemoryConfigChanged(); },
            () => "长期记忆容量", () => "撤销记录也计入容量，降低上限保留已有数据。每次请求最多携带最近更新的 32 条。", 1, 100, 1);
        api.AddBoolOption(ModManifest, () => config.LongTermMemory.LearnNew,
            value => { config.LongTermMemory = config.LongTermMemory with { LearnNew = value }; MemoryConfigChanged(); },
            () => "学习新偏好");
        api.AddBoolOption(ModManifest, () => config.LongTermMemory.AutoUpdate,
            value => { config.LongTermMemory = config.LongTermMemory with { AutoUpdate = value }; MemoryConfigChanged(); },
            () => "自动更新已有偏好", () => "控制纠正和撤销。玩家明确要求遗忘时仍执行。");
        api.AddBoolOption(ModManifest, () => config.LongTermMemory.DebugLogging,
            value => config.LongTermMemory = config.LongTermMemory with { DebugLogging = value },
            () => "记忆调试日志", () => "输出版本、操作数量和拒绝原因。");
        api.AddNumberOption(ModManifest, () => config.AI.MaxTokens,
            value => { config.AI.MaxTokens = value; MemoryConfigChanged(); },
            () => "模型输出上限", () => "回复、三条建议、评分和记忆更新共用的输出 token 预算。", 64, 16384, 64);
    }

    private void OnMemoryCommand(string command, string[] args)
    {
        MemoryCommand? parsed = MemoryCommand.Parse(args);
        if (parsed is null)
        {
            Monitor.Log("Usage: stardewai_memory list [NPC] | forget NPC ID | forget-category NPC category | clear NPC | reload [NPC]", LogLevel.Info);
            return;
        }
        if (!Context.IsWorldReady || string.IsNullOrEmpty(Constants.SaveFolderName))
        {
            Monitor.Log("Load a save before managing NPC memory.", LogLevel.Info);
            return;
        }
        try
        {
            if (parsed.NpcName is null)
            {
                MemoryConfigChanged();
                conversationMemory.Clear();
                Monitor.Log("Conversation memory caches cleared. Files will reload on next access.", LogLevel.Info);
                return;
            }
            NPC? npc = Game1.getCharacterFromName(parsed.NpcName, mustBeVillager: true, includeEventActors: false);
            if (npc is null)
            {
                Monitor.Log($"NPC '{parsed.NpcName}' was not found.", LogLevel.Warn);
                return;
            }
            DialogueIdentity identity = GetDialogueIdentity(npc);
            if (parsed.Action == "list")
            {
                IReadOnlyList<LongTermPreference> preferences = conversationMemory.GetPreferences(identity);
                Monitor.Log($"Memory for {npc.Name}: revision={conversationMemory.GetRevision(identity)}, entries={preferences.Count}.", LogLevel.Info);
                foreach (LongTermPreference preference in preferences)
                    Monitor.Log($"{preference.Id} | {preference.Category}/{preference.Type} | {preference.Status} | {preference.Value}", LogLevel.Info);
                return;
            }
            MemoryConfigChanged();
            switch (parsed.Action)
            {
                case "forget":
                    bool removed = conversationMemory.Forget(identity, parsed.Value!);
                    Monitor.Log(removed ? $"Memory forgotten for {npc.Name}. Related history is filtered and suggestions cleared."
                        : "Memory ID was not found.", removed ? LogLevel.Info : LogLevel.Warn);
                    break;
                case "forget-category": conversationMemory.ForgetCategory(identity, parsed.Value!); break;
                case "clear": conversationMemory.ClearAll(identity); break;
                case "reload":
                    conversationMemory.Reload(identity);
                    conversationMemory.GetRevision(identity);
                    break;
            }
            Monitor.Log($"Memory command '{parsed.Action}' completed for {npc.Name}.", LogLevel.Info);
        }
        catch (IOException error)
        {
            Monitor.Log($"Memory command failed: {error.Message}", LogLevel.Error);
        }
    }

    private static string DailyStateKey(NPC npc) => ConversationStateKeys.ForNpc(npc.Name);

    private static DailyConversationState GetDailyState(NPC npc)
        => DailyConversationState.Read(ConversationStateKeys.Read(Game1.player.modData.Pairs, npc.Name),
            Game1.Date.TotalDays);

    private static void SetDailyState(NPC npc, DailyConversationState daily)
        => Game1.player.modData[DailyStateKey(npc)] = daily.ToJson();

    private bool CanStartConversation(NPC npc, DailyConversationState daily)
    {
        bool partner = Game1.player.spouse == npc.Name
            || (Game1.player.friendshipData.TryGetValue(npc.Name, out Friendship? friendship)
                && friendship.Status is FriendshipStatus.Dating or FriendshipStatus.Engaged or FriendshipStatus.Married);
        if (daily.CanStart(partner, config.DailyDialogueLimit, config.PartnerDailyDialogueLimit))
            return true;
        Game1.addHUDMessage(new HUDMessage($"今天已与{npc.displayName}聊了 {daily.CompletedCount} 轮，明天再聊吧。"));
        Monitor.Log($"Daily conversation limit reached for {npc.Name} ({daily.CompletedCount}).", LogLevel.Info);
        return false;
    }

    private void CompleteConversation(NPC npc, int? tier)
    {
        DailyConversationState daily = GetDailyState(npc);
        bool talked = Game1.player.hasPlayerTalkedToNPC(npc.Name);
        int points = daily.GetFriendshipReward(tier, talked);
        SetDailyState(npc, daily.Complete());
        if (!npc.CanSocialize)
        {
            Monitor.Log($"Conversation completed for {npc.Name}: count={daily.CompletedCount + 1}, friendship=0 (non-social NPC).", LogLevel.Info);
            return;
        }
        if (!Game1.player.friendshipData.TryGetValue(npc.Name, out Friendship? friendship))
            Game1.player.friendshipData[npc.Name] = friendship = new Friendship();
        friendship.TalkedToToday = true;
        if (!npc.isDivorcedFrom(Game1.player) && points > 0)
            Game1.player.changeFriendship(points, npc);
        Game1.player.NotifyQuests(quest => quest.OnNpcSocialized(npc));
        Monitor.Log($"Conversation completed for {npc.Name}: count={daily.CompletedCount + 1}, friendship={points}.", LogLevel.Info);
    }

    private static string GetPreferences(NPC npc)
    {
        if (!Game1.NPCGiftTastes.TryGetValue(npc.Name, out string? tastes))
            return "";
        string[] parts = tastes.Split('/');
        if (parts.Length < 4)
            return "";
        string Names(string ids) => string.Join("、", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => !id.StartsWith('-')).Take(16).Select(id => ItemRegistry.GetDataOrErrorItem(id).DisplayName));
        return $"最爱物品 {Names(parts[1])}。喜欢物品 {Names(parts[3])}。";
    }

    private static string GetCharacterProfile(NPC npc)
    {
        if (npc.GetData() is null)
            return "游戏未提供该人物的详细资料。";
        return NpcRuntimeTraits.Describe(npc.Age, npc.Manners, npc.SocialAnxiety, npc.Optimism, npc.CanSocialize);
    }

    private static string GetHomeLocation(NPC npc)
        => npc.getSpouse() is Farmer spouse ? Utility.getHomeOfFarmer(spouse).NameOrUniqueName : npc.DefaultMap ?? "";

    private void SendComposer(DialogueComposerMenu owner, string message)
    {
        if (owner != composerMenu || composerNpc is null)
            return;
        BeginDialogue(composerNpc.Name, message, reportUnavailable: true, owner);
        if (!aiQueue.IsBusy && composerMenu == owner)
            owner.ShowError(Helper.Translation.Get("hud.request-unavailable").ToString());
    }

    private void CloseComposer()
    {
        DialogueComposerMenu? menu = composerMenu;
        composerMenu = null;
        composerNpc = null;
        composerScene = null;
        if (menu is null)
            return;
        menu.DetachInput();
        if (Game1.activeClickableMenu == menu)
            menu.exitThisMenu(playSound: false);
    }

    private void BeginDialogue(string name, string playerMessage, bool reportUnavailable,
        DialogueComposerMenu? owner = null)
    {
        DialogueSafety safety = GetDialogueSafety(owner);
        string? skipReason = safety.GetSkipReason();
        if (skipReason is not null)
        {
            Monitor.Log($"Dialogue skipped: {skipReason}", LogLevel.Info);
            return;
        }

        if (aiQueue.IsBusy)
        {
            Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("hud.request-pending").ToString()));
            return;
        }

        NPC? npc = Game1.getCharacterFromName(name, mustBeVillager: true, includeEventActors: false);
        if (npc?.currentLocation is null)
        {
            Monitor.Log($"NPC '{name}' was not found or has no current location.", LogLevel.Warn);
            return;
        }

        string path = DialoguePool.ResolvePath(Path.Combine(Helper.DirectoryPath, "Data", "Dialogues"), npc.Name);
        if (!dialoguePools.TryGetValue(npc.Name, out DialoguePool? pool))
        {
            try
            {
                pool = DialoguePool.Load(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Monitor.Log($"Dialogue pool disabled for {npc.Name}. File '{path}': {ex.Message}", LogLevel.Error);
            }
            dialoguePools[npc.Name] = pool;
        }
        if (pool is null)
        {
            Monitor.Log($"Dialogue pool for {npc.Name} is disabled. Check '{path}' and restart after correcting it.", LogLevel.Warn);
            return;
        }

        if (string.IsNullOrEmpty(Constants.SaveFolderName))
        {
            Monitor.Log("Dialogue skipped: save identity is not available yet.", LogLevel.Info);
            return;
        }
        try
        {
            DailyConversationState daily = GetDailyState(npc);
            if (!CanStartConversation(npc, daily))
                return;
            // Keep a failed history record disabled for both local and generated dialogue.
            dialogueHistory.GetUsed(GetDialogueIdentity(npc), Game1.Date.TotalDays);
            string? unavailable = config.AI.GetUnavailableReason();
            if (unavailable is null)
            {
                ConversationSnapshot snapshot;
                try
                {
                    snapshot = conversationMemory.GetSnapshot(GetDialogueIdentity(npc), config.LongTermMemory);
                }
                catch (IOException)
                {
                    Monitor.Log($"Conversation memory unavailable for {npc.Name}. AI is disabled for this record until reload.", LogLevel.Error);
                    if (owner is not null)
                        CloseComposer();
                    ShowLocalDialogue(npc, pool);
                    return;
                }
                var request = new AiDialogueRequest(npc.Name, npc.currentLocation.NameOrUniqueName,
                    Game1.currentSeason, Game1.dayOfMonth, Game1.timeOfDay, GetWeather(npc.currentLocation),
                    Game1.player.getFriendshipHeartLevelForNPC(npc.Name), playerMessage)
                {
                    History = snapshot.History,
                    LongTermPreferences = snapshot.Preferences,
                    MemoryRevision = snapshot.Revision,
                    MemoryOptions = config.LongTermMemory,
                    RequestId = Guid.NewGuid(),
                    OpeningLine = daily.RewardSettled ? "" : daily.OpeningText,
                    Preferences = GetPreferences(npc),
                    DisplayName = npc.displayName,
                    CharacterProfile = GetCharacterProfile(npc),
                    HomeLocation = GetHomeLocation(npc),
                    Relationship = Game1.player.friendshipData.TryGetValue(npc.Name, out Friendship? relationship)
                        ? NpcRelationship.FromGameState(relationship.Status.ToString(), relationship.RoommateMarriage, npc.CanSocialize)
                        : NpcRelationship.FromGameState("Friendly", false, npc.CanSocialize),
                    EvaluateFriendship = npc.CanSocialize && !daily.RewardSettled && !Game1.player.hasPlayerTalkedToNPC(npc.Name)
                        && !string.IsNullOrWhiteSpace(playerMessage)
                };
                if (aiQueue.TryStart(config.AI, request, GetAiScene(npc)))
                {
                    pendingAiNpc = npc;
                    pendingAiPool = pool;
                    pendingAiRequest = request;
                    Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("hud.waiting-response").ToString()));
                    Monitor.Log($"AI request started for {npc.Name}.", LogLevel.Info);
                }
                return;
            }
            if (reportUnavailable || config.AI.Enabled)
                Monitor.Log($"AI provider unavailable ({unavailable}). Using the local dialogue pool.", LogLevel.Info);
            if (owner is not null)
                CloseComposer();
            ShowLocalDialogue(npc, pool);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Dialogue failed for {npc.Name}: {ex}", LogLevel.Error);
        }
    }

    private static DialogueIdentity GetDialogueIdentity(NPC npc)
        => new(Constants.SaveFolderName!, Game1.player.UniqueMultiplayerID, npc.Name);

    private static AiScene GetAiScene(NPC npc)
        => new(Constants.SaveFolderName!, Game1.player.UniqueMultiplayerID, npc.Name, Game1.Date.TotalDays,
            Game1.currentLocation.NameOrUniqueName, npc.currentLocation.NameOrUniqueName,
            Game1.player.Tile.X, Game1.player.Tile.Y, npc.Tile.X, npc.Tile.Y);

    private void ShowLocalDialogue(NPC npc, DialoguePool pool)
    {
        var context = new DialogueContext(GetWeather(npc.currentLocation), Game1.player.getFriendshipHeartLevelForNPC(npc.Name));
        DialogueResult result = dialogueEngine.Show(pool, GetDialogueIdentity(npc), Game1.Date.TotalDays,
            context, GetDialogueSafety(), entry =>
            {
                DisplayText(npc, entry.Text);
                Monitor.Log($"Showing dialogue '{entry.Id}' for {npc.Name}.", LogLevel.Info);
            });
        if (result.Entry is not null)
            CompleteConversation(npc, null);
        if (result.SkipReason is not null)
        {
            Monitor.Log($"Dialogue skipped: {result.SkipReason}", LogLevel.Info);
            Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("hud.no-dialogue").ToString()));
        }
    }

    private static void DisplayText(NPC npc, string text)
    {
        // Native push/pop pairs this line with its own close operation, preserving queued dialogue.
        Game1.DrawDialogue(new StardewValley.Dialogue(npc, null, text));
    }

    private void CancelAi()
    {
        if (aiQueue.IsBusy)
            Monitor.Log("Pending AI dialogue discarded after the game context changed.", LogLevel.Info);
        aiQueue.Cancel();
        pendingAiNpc = null;
        pendingAiPool = null;
        pendingAiRequest = null;
        CloseComposer();
    }

    private void OnAiUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (openingConversation is OpeningConversation opening)
        {
            if (!Context.IsWorldReady || opening.Npc.currentLocation is null
                || !opening.Scene.IsCompatible(GetAiScene(opening.Npc)) || Game1.eventUp)
                openingConversation = null;
            else if (Game1.activeClickableMenu == opening.Menu)
                return;
            else
            {
                openingConversation = null;
                if (GetDialogueSafety().GetSkipReason() is null && CanStartConversation(opening.Npc, GetDailyState(opening.Npc)))
                    ShowComposer(opening.Npc);
            }
        }
        if (composerMenu is not null && (composerNpc?.currentLocation is null || composerScene is null
            || GetDialogueSafety(composerMenu).GetSkipReason() is not null
            || !composerScene.IsCompatible(GetAiScene(composerNpc))
            || !ReferenceEquals(composerNpc, Game1.getCharacterFromName(composerNpc.Name, mustBeVillager: true, includeEventActors: false))))
        {
            CancelAi();
            return;
        }
        if (!aiQueue.IsBusy)
            return;
        NPC? npc = pendingAiNpc;
        DialoguePool? pool = pendingAiPool;
        AiDialogueRequest? request = pendingAiRequest;
        if (npc?.currentLocation is null || pool is null || request is null || GetDialogueSafety(composerMenu).GetSkipReason() is not null
            || string.IsNullOrEmpty(Constants.SaveFolderName)
            || !ReferenceEquals(npc, Game1.getCharacterFromName(npc.Name, mustBeVillager: true, includeEventActors: false)))
        {
            CancelAi();
            return;
        }
        try
        {
            if (request.RequestId != aiQueue.CurrentRequestId || request.MemoryRevision != conversationMemory.GetRevision(GetDialogueIdentity(npc)))
            {
                Monitor.Log("Pending AI dialogue discarded after the memory version changed.", LogLevel.Info);
                CancelAi();
                return;
            }
        }
        catch (IOException)
        {
            Monitor.Log($"Conversation memory unavailable for {npc.Name}. Pending response discarded.", LogLevel.Error);
            CancelAi();
            return;
        }
        if (!aiQueue.TryTake(GetAiScene(npc), safe: true, out AiDialogueResult? result))
        {
            if (!aiQueue.IsBusy)
            {
                pendingAiNpc = null;
                pendingAiPool = null;
                pendingAiRequest = null;
                Monitor.Log("Pending AI dialogue discarded after movement or a scene change.", LogLevel.Info);
            }
            return;
        }
        pendingAiNpc = null;
        pendingAiPool = null;
        pendingAiRequest = null;
        CloseComposer();
        try
        {
            if (result!.Text is string text)
            {
                DialogueResult generated = dialogueEngine.ShowGenerated(text, GetDialogueIdentity(npc),
                    Game1.Date.TotalDays, GetDialogueSafety(), entry => DisplayText(npc, entry.Text));
                if (generated.Entry is not null)
                {
                    Monitor.Log($"Showing generated dialogue for {npc.Name}.", LogLevel.Info);
                    CompleteConversation(npc, request.EvaluateFriendship ? result.FriendshipTier : null);
                    MemoryCommitResult committed = conversationMemory.Commit(GetDialogueIdentity(npc), request.MemoryRevision,
                        new ConversationTurn(Game1.Date.TotalDays, request.PlayerMessage, text), result.Suggestions,
                        result.MemoryUpdates, request.MemoryOptions);
                    if (!committed.Committed)
                        Monitor.Log("Displayed reply was not recorded because its memory version changed.", LogLevel.Warn);
                    foreach (string notice in committed.Notices.Distinct())
                    {
                        Monitor.Log($"Memory update for {npc.Name}: {notice}.",
                            notice is "favorite_operation_corrected" or "forget_operation_corrected" ? LogLevel.Debug : LogLevel.Warn);
                        if (notice == "capacity_reached")
                            Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("hud.memory-full").ToString()));
                    }
                    if (result.MemoryUpdateFailure is not null)
                        Monitor.Log($"Memory update for {npc.Name}: {result.MemoryUpdateFailure}.", LogLevel.Warn);
                    if (config.LongTermMemory.DebugLogging)
                        Monitor.Log($"Memory commit for {npc.Name}: request={request.RequestId:N}, revision={request.MemoryRevision}"
                            + $"->{conversationMemory.GetRevision(GetDialogueIdentity(npc))}, proposed={result.MemoryUpdates?.Count ?? 0}.", LogLevel.Debug);
                    IReadOnlyList<string>? savedSuggestions = conversationMemory.GetSuggestions(GetDialogueIdentity(npc));
                    Monitor.Log($"Next suggestions for {npc.Name}: count={savedSuggestions?.Count ?? 0}, repaired={result.SuggestionsRepaired}.",
                        LogLevel.Trace);
                    return;
                }
                Monitor.Log("AI returned a line already shown today. Using the local dialogue pool.", LogLevel.Info);
            }
            else
                Monitor.Log($"DeepSeek request failed ({result.Failure}, finish={result.FinishReason ?? "unknown"}). Using the local dialogue pool.", LogLevel.Warn);
            try
            {
                conversationMemory.ClearSuggestions(GetDialogueIdentity(npc));
            }
            catch (IOException)
            {
                Monitor.Log($"Failed to reset suggestions for {npc.Name}. Conversation memory is disabled until reload.", LogLevel.Error);
            }
            ShowLocalDialogue(npc, pool);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Dialogue display or storage failed for {npc.Name}: {ex.GetType().Name}", LogLevel.Error);
        }
    }

    private static string GetWeather(GameLocation location)
    {
        if (Game1.IsGreenRainingHere(location))
            return "green_rain";
        if (Game1.IsLightningHere(location))
            return "storm";
        if (Game1.IsSnowingHere(location))
            return "snow";
        if (Game1.IsRainingHere(location))
            return "rain";
        if (Game1.IsDebrisWeatherHere(location))
            return "wind";
        return "sun";
    }
}

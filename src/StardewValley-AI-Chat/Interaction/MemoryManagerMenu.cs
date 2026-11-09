using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewAI.Dialogue;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Interaction;

public sealed class MemoryManagerMenu : IClickableMenu
{
    private readonly string npcName;
    private readonly MemoryManagerModel model;
    private readonly Func<bool> isCurrent;
    private readonly TextBox input;
    private readonly Dictionary<string, ClickableComponent> buttons = new();
    private string category = "flower";
    private string type = "favorite";
    private string status = "active";
    private string? confirmation;
    private string? confirmationCategory;
    private int evidencePage;
    private int evidencePages = 1;
    private float scale;
    private bool attachInput;
    private bool confirmationReady;
    private bool confirmSelected;

    public MemoryManagerMenu(string npcName, MemoryManagerModel model, Func<bool> isCurrent)
    {
        this.npcName = npcName;
        this.model = model;
        this.isCurrent = isCurrent;
        Game1.player.Halt();
        input = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            { limitWidth = false, textLimit = 80, TitleText = "纠正记忆内容" };
        input.OnEnterPressed += _ => Save();
        Layout();
        SyncSelection();
    }

    private Rectangle Bounds(int x, int y, int w, int h) => new(xPositionOnScreen + (int)(x * scale),
        yPositionOnScreen + (int)(y * scale), (int)(w * scale), (int)(h * scale));

    private void Layout()
    {
        scale = Math.Min(1f, Math.Min((Game1.uiViewport.Width - 32) / 960f, (Game1.uiViewport.Height - 32) / 650f));
        width = (int)(960 * scale);
        height = (int)(650 * scale);
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
        buttons.Clear();
        Add("filter", 24, 76, 280, 42);
        for (int index = 0; index < MemoryManagerModel.PageSize; index++)
            Add("row" + index, 24, 128 + index * 58, 280, 52);
        Add("previous", 24, 490, 80, 44);
        Add("next", 224, 490, 80, 44);
        Add("category", 332, 112, 188, 44);
        Add("type", 532, 112, 188, 44);
        Add("status", 732, 112, 204, 44);
        Add("input", 332, 272, 604, 48);
        Add("evidence", 632, 450, 304, 40);
        Add("save", 332, 500, 280, 44);
        Add("forget", 628, 500, 308, 44);
        Add("forget-category", 24, 592, 244, 42);
        Add("clear", 280, 592, 208, 42);
        Add("reload", 500, 592, 208, 42);
        Add("close", 720, 592, 216, 42);
        Add("confirm", 264, 374, 200, 48);
        Add("back", 496, 374, 200, 48);
        Rectangle field = buttons["input"].bounds;
        input.X = field.X; input.Y = field.Y; input.Width = field.Width; input.Height = field.Height;
        allClickableComponents = buttons.Where(pair => pair.Key is not ("confirm" or "back"))
            .Select(pair => pair.Value).ToList();
        for (int index = 0; index < allClickableComponents.Count; index++)
        {
            ClickableComponent button = allClickableComponents[index];
            button.myID = index;
            button.upNeighborID = button.leftNeighborID = (index + allClickableComponents.Count - 1) % allClickableComponents.Count;
            button.downNeighborID = button.rightNeighborID = (index + 1) % allClickableComponents.Count;
        }
        currentlySnappedComponent = buttons["input"];
    }

    private void Add(string name, int x, int y, int w, int h) => buttons[name] = new ClickableComponent(Bounds(x, y, w, h), name);

    private void SyncSelection()
    {
        LongTermPreference? selected = model.Selected;
        category = selected?.Category ?? "flower";
        type = selected?.Type ?? "favorite";
        status = selected?.Status ?? "active";
        input.Text = selected?.Value ?? "";
        input.Selected = false;
        attachInput = false;
        evidencePage = 0;
    }

    public override void update(GameTime time)
    {
        if (!isCurrent())
        {
            exitThisMenu(playSound: false);
            return;
        }
        if (confirmation is not null && !Game1.input.GetKeyboardState().IsKeyDown(Keys.Enter))
            confirmationReady = true;
        RefreshNavigation();
        if (attachInput && !Game1.input.GetKeyboardState().IsKeyDown(Keys.Enter))
        {
            attachInput = false;
            input.Selected = true;
        }
    }

    private void Save()
    {
        if (confirmation is not null || !model.IsAvailable || model.Selected is null)
            return;
        long revision = model.Revision;
        string? selectedId = model.SelectedId;
        model.Save(category, type, input.Text, status);
        // Retain invalid edits so the player can correct them after a validation error.
        if (!model.IsError || revision != model.Revision || selectedId != model.SelectedId)
            SyncSelection();
        if (!model.IsAvailable)
            DetachInput();
    }

    private void RefreshNavigation()
    {
        allClickableComponents = model.GetNavigationKeys(evidencePages > 1).Select(name => buttons[name]).ToList();
        for (int index = 0; index < allClickableComponents.Count; index++)
        {
            ClickableComponent previous = allClickableComponents[(index + allClickableComponents.Count - 1) % allClickableComponents.Count];
            ClickableComponent next = allClickableComponents[(index + 1) % allClickableComponents.Count];
            allClickableComponents[index].upNeighborID = allClickableComponents[index].leftNeighborID = previous.myID;
            allClickableComponents[index].downNeighborID = allClickableComponents[index].rightNeighborID = next.myID;
        }
        if (!allClickableComponents.Contains(currentlySnappedComponent))
            currentlySnappedComponent = allClickableComponents[0];
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (confirmation is not null)
        {
            if (buttons["back"].containsPoint(x, y))
                confirmation = null;
            else if (buttons["confirm"].containsPoint(x, y))
                Confirm();
            return;
        }
        string? action = buttons.FirstOrDefault(pair => pair.Key is not ("confirm" or "back") && pair.Value.containsPoint(x, y)).Key;
        if (action is null)
        {
            input.Selected = false;
            return;
        }
        currentlySnappedComponent = buttons[action];
        attachInput = false;
        if (action == "input" && model.IsAvailable && model.Selected is not null)
        {
            input.Selected = true;
            return;
        }
        input.Selected = false;
        if (action.StartsWith("row", StringComparison.Ordinal))
        {
            int index = int.Parse(action[3..]);
            if (index < model.Visible.Count)
                model.Select(model.Visible[index].Id);
            SyncSelection();
            return;
        }
        switch (action)
        {
            case "close": exitThisMenu(); break;
            case "previous": model.ChangePage(-1); SyncSelection(); break;
            case "next": model.ChangePage(1); SyncSelection(); break;
            case "filter":
                var filters = new string?[] { null }.Concat(LongTermPreferenceRules.Categories).ToArray();
                model.SetFilter(filters[(Array.IndexOf(filters, model.CategoryFilter) + 1) % filters.Length]);
                SyncSelection();
                break;
            case "category": category = Next(LongTermPreferenceRules.Categories, category); break;
            case "type": type = Next(MemoryManagerModel.Types, type); break;
            case "status": status = Next(MemoryManagerModel.States, status); break;
            case "evidence": evidencePage = (evidencePage + 1) % evidencePages; break;
            case "save": Save(); break;
            case "forget" when model.IsAvailable && model.Selected is not null:
            case "forget-category" when model.IsAvailable && (model.Selected is not null || model.CategoryFilter is not null):
            case "clear" when model.IsAvailable:
                confirmation = action;
                confirmationCategory = model.CategoryFilter ?? model.Selected?.Category;
                confirmationReady = false;
                confirmSelected = false;
                attachInput = false;
                break;
            case "reload": model.Reload(); SyncSelection(); break;
        }
    }

    private void Confirm()
    {
        switch (confirmation)
        {
            case "forget": model.ForgetSelected(); break;
            case "forget-category": model.ForgetCategory(confirmationCategory!); break;
            case "clear": model.ClearAll(); break;
        }
        confirmation = null;
        SyncSelection();
    }

    private static string Next(IReadOnlyList<string> options, string value)
        => options[(options.ToList().IndexOf(value) + 1) % options.Count];

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            if (confirmation is not null) confirmation = null;
            else exitThisMenu();
        }
        else if (confirmation is not null && (key == Keys.Tab || key == Keys.Left || key == Keys.Right))
            confirmSelected = !confirmSelected;
        else if (confirmation is not null && key == Keys.Enter && confirmationReady)
        {
            if (confirmSelected) Confirm();
            else confirmation = null;
        }
        else if (key == Keys.Tab && confirmation is null)
        {
            RefreshNavigation();
            input.Selected = false;
            attachInput = false;
            int index = allClickableComponents.IndexOf(currentlySnappedComponent);
            currentlySnappedComponent = allClickableComponents[(index + 1) % allClickableComponents.Count];
            snapCursorToCurrentSnappedComponent();
        }
        else if (key == Keys.Enter && !input.Selected && confirmation is null)
        {
            if (currentlySnappedComponent == buttons["input"])
                attachInput = model.IsAvailable && model.Selected is not null;
            else if (currentlySnappedComponent is not null)
                receiveLeftClick(currentlySnappedComponent.bounds.Center.X, currentlySnappedComponent.bounds.Center.Y);
        }
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (button == Buttons.B)
            receiveKeyPress(Keys.Escape);
        else if (confirmation is not null && button == Buttons.A)
        {
            if (confirmSelected) Confirm();
            else confirmation = null;
        }
        else if (confirmation is not null && button is Buttons.DPadLeft or Buttons.DPadRight)
            confirmSelected = !confirmSelected;
        else
            base.receiveGamePadButton(button);
    }

    public void DetachInput() { input.Selected = false; attachInput = false; }
    protected override void cleanupBeforeExit() => DetachInput();
    public override void emergencyShutDown() => DetachInput();
    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => Layout();

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.6f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        Text(b, $"{npcName}的记忆", 24, 20, 912, 44);
        DrawButton(b, "filter", MemoryManagerModel.CategoryLabel(model.CategoryFilter ?? ""));
        Text(b, $"共 {model.Count} 条 · 版本 {model.Revision}", 332, 76, 604, 28, 0.8f);
        IReadOnlyList<LongTermPreference> visible = model.Visible;
        for (int index = 0; index < visible.Count; index++)
        {
            LongTermPreference entry = visible[index];
            DrawButton(b, "row" + index, $"{MemoryManagerModel.CategoryLabel(entry.Category)} · {entry.Value}",
                selected: entry.Id == model.SelectedId);
        }
        if (visible.Count == 0)
            Text(b, model.IsAvailable ? "当前没有记忆。" : "记忆暂不可用。", 32, 160, 264, 60, 0.85f);
        DrawButton(b, "previous", "‹", model.Page > 0);
        Text(b, $"{model.Page + 1}/{model.PageCount}", 112, 490, 104, 44, 0.8f);
        DrawButton(b, "next", "›", model.Page + 1 < model.PageCount);
        if (model.Selected is LongTermPreference selected)
        {
            DrawButton(b, "category", "类别 · " + MemoryManagerModel.CategoryLabel(category), model.IsAvailable);
            DrawButton(b, "type", "类型 · " + MemoryManagerModel.TypeLabel(type), model.IsAvailable);
            DrawButton(b, "status", "状态 · " + MemoryManagerModel.StateLabel(status), model.IsAvailable);
            Text(b, $"记录于游戏第 {selected.LearnedDay + 1} 天 · {selected.CreatedAt:yyyy-MM-dd HH:mm} UTC", 332, 172, 604, 28, 0.72f);
            Text(b, $"更新于游戏第 {selected.UpdatedDay + 1} 天 · {selected.UpdatedAt:yyyy-MM-dd HH:mm} UTC", 332, 208, 604, 28, 0.72f);
            Text(b, "偏好内容（最多 80 字）", 332, 242, 604, 28, 0.8f);
            input.Draw(b, drawShadow: false);
            Text(b, selected.ManuallyEdited ? "来源原话（手动纠正后保留）" : "来源原话", 332, 328, 604, 28, 0.8f);
            string wrapped = Game1.parseText(selected.Evidence, Game1.smallFont, (int)(604 / 0.75f));
            string[] lines = wrapped.Split('\n');
            evidencePages = Math.Max(1, (lines.Length + 2) / 3);
            evidencePage = Math.Min(evidencePage, evidencePages - 1);
            Text(b, string.Join("\n", lines.Skip(evidencePage * 3).Take(3)), 332, 366, 604, 80, 0.75f, multiline: true);
            DrawButton(b, "evidence", $"原话 {evidencePage + 1}/{evidencePages} · 下一页", evidencePages > 1);
            DrawButton(b, "save", "保存纠正", model.IsAvailable);
            DrawButton(b, "forget", "遗忘这一条", model.IsAvailable);
        }
        Text(b, model.Message, 24, 550, 912, 30, 0.75f, color: model.IsError ? Color.DarkRed : Color.DarkGreen);
        DrawButton(b, "forget-category", "遗忘当前类别", model.IsAvailable && (model.CategoryFilter is not null || model.Selected is not null));
        DrawButton(b, "clear", "清空全部", model.IsAvailable);
        DrawButton(b, "reload", "重新读取");
        DrawButton(b, "close", "关闭");
        if (confirmation is not null)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.65f);
            Rectangle box = Bounds(164, 220, 632, 220);
            drawTextureBox(b, box.X, box.Y, box.Width, box.Height, Color.White);
            string title = confirmation switch
            {
                "forget" => "遗忘所选记忆，并过滤相关交流？",
                "forget-category" => $"遗忘{MemoryManagerModel.CategoryLabel(confirmationCategory!)}类别，并清空近期交流？",
                _ => "清空该角色的全部偏好和近期交流？"
            };
            Text(b, title, 188, 244, 584, 60, 0.85f);
            Text(b, "确认后立即保存。Esc 返回。", 188, 310, 584, 40, 0.8f);
            DrawButton(b, "confirm", "确认遗忘", selected: confirmSelected);
            DrawButton(b, "back", "返回", selected: !confirmSelected);
        }
        drawMouse(b);
    }

    private void DrawButton(SpriteBatch b, string name, string text, bool enabled = true, bool selected = false)
    {
        Rectangle area = buttons[name].bounds;
        bool hover = enabled && area.Contains(Game1.getMouseX(), Game1.getMouseY());
        drawTextureBox(b, area.X, area.Y, area.Width, area.Height, !enabled ? Color.LightGray : selected || hover ? Color.Wheat : Color.White);
        Write(b, text, new Rectangle(area.X + (int)(12 * scale), area.Y, area.Width - (int)(24 * scale), area.Height),
            0.8f, enabled ? Game1.textColor : Color.Gray, false);
    }

    private void Text(SpriteBatch b, string text, int x, int y, int w, int h, float size = 1f, Color? color = null, bool multiline = false)
        => Write(b, text, Bounds(x, y, w, h), size, color ?? Game1.textColor, multiline);

    private void Write(SpriteBatch b, string text, Rectangle area, float size, Color color, bool multiline)
    {
        float fontScale = scale * size;
        string visible = text;
        if (!multiline)
        {
            while (visible.Length > 0 && Game1.smallFont.MeasureString(visible + "…").X * fontScale > area.Width)
                visible = visible[..^1];
            if (visible != text) visible += "…";
        }
        b.DrawString(Game1.smallFont, visible, new Vector2(area.X, multiline ? area.Y
            : area.Y + (area.Height - Game1.smallFont.LineSpacing * fontScale) / 2), color, 0, Vector2.Zero, fontScale, SpriteEffects.None, 1);
    }
}

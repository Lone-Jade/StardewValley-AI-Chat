using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewAI.Dialogue;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Interaction;

public sealed class DialogueComposerMenu : IClickableMenu
{
    private readonly string npcDisplayName;
    private readonly Action<DialogueComposerMenu, string> submit;
    private readonly TextBox input;
    private readonly List<ClickableComponent> suggestions = new();
    private ClickableComponent inputComponent = null!;
    private ClickableComponent sendButton = null!;
    private ClickableComponent cancelButton = null!;
    private float scale;
    private bool focusOnFirstUpdate = true;
    private bool focusWhenEnterReleased;

    public DialogueComposerModel Model { get; }

    public DialogueComposerMenu(string npcDisplayName, DialogueComposerModel model,
        Action<DialogueComposerMenu, string> submit, Action<DialogueComposerMenu> cancel)
    {
        this.npcDisplayName = npcDisplayName;
        this.submit = submit;
        Model = model;
        Game1.player.Halt();
        input = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null,
            Game1.smallFont, Game1.textColor)
        {
            limitWidth = false,
            textLimit = DialogueComposerModel.MaximumMessageLength,
            TitleText = $"与{npcDisplayName}交谈"
        };
        input.OnEnterPressed += _ => Send();
        exitFunction = () => cancel(this);
        Layout();
    }

    public override void update(GameTime time)
    {
        if (Model.IsClosed || Model.IsWaiting)
            return;
        // Attach after queued text events clear, including Enter used to select a suggestion.
        if (focusOnFirstUpdate || (focusWhenEnterReleased && !Game1.input.GetKeyboardState().IsKeyDown(Keys.Enter)))
        {
            focusOnFirstUpdate = false;
            focusWhenEnterReleased = false;
            input.Selected = true;
        }
    }

    private void Layout()
    {
        scale = Math.Min(1f, Math.Min((Game1.uiViewport.Width - 32) / 920f,
            (Game1.uiViewport.Height - 32) / 560f));
        width = (int)(920 * scale);
        height = (int)(560 * scale);
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
        suggestions.Clear();
        for (int index = 0; index < Model.Suggestions.Count; index++)
            suggestions.Add(new ClickableComponent(Bounds(32, 112 + index * 68, 856, 56), index.ToString()) { myID = index });
        inputComponent = new ClickableComponent(Bounds(32, 348, 856, 56), "input") { myID = 3 };
        input.X = inputComponent.bounds.X;
        input.Y = inputComponent.bounds.Y;
        input.Width = inputComponent.bounds.Width;
        input.Height = inputComponent.bounds.Height;
        sendButton = new ClickableComponent(Bounds(568, 456, 152, 60), "send") { myID = 4 };
        cancelButton = new ClickableComponent(Bounds(736, 456, 152, 60), "cancel") { myID = 5 };
        allClickableComponents = suggestions.Concat(new[] { inputComponent, sendButton, cancelButton }).ToList();
        for (int index = 0; index < allClickableComponents.Count; index++)
        {
            allClickableComponents[index].upNeighborID = (index + 5) % 6;
            allClickableComponents[index].downNeighborID = (index + 1) % 6;
            allClickableComponents[index].leftNeighborID = (index + 5) % 6;
            allClickableComponents[index].rightNeighborID = (index + 1) % 6;
        }
        currentlySnappedComponent = inputComponent;
    }

    private Rectangle Bounds(int x, int y, int w, int h) => new(xPositionOnScreen + (int)(x * scale),
        yPositionOnScreen + (int)(y * scale), (int)(w * scale), (int)(h * scale));

    private void FocusNext()
    {
        input.Selected = false;
        int current = currentlySnappedComponent?.myID ?? 2;
        currentlySnappedComponent = allClickableComponents[(current + 1) % allClickableComponents.Count];
        snapCursorToCurrentSnappedComponent();
    }

    private void Send()
    {
        if (!Model.TrySubmit(input.Text, out string message))
            return;
        input.Text = message;
        input.Selected = false;
        submit(this, message);
    }

    public void ShowError(string message)
    {
        Model.ShowError(message);
        input.Selected = true;
    }

    public void DetachInput()
    {
        focusOnFirstUpdate = false;
        focusWhenEnterReleased = false;
        input.Selected = false;
        Model.Close();
    }

    protected override void cleanupBeforeExit() => DetachInput();
    public override void emergencyShutDown() => DetachInput();

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => Layout();

    public override void receiveLeftClick(int x, int y, bool playSound = true)
        => Activate(x, y, fromKeyboard: false);

    private void Activate(int x, int y, bool fromKeyboard)
    {
        if (cancelButton.containsPoint(x, y))
        {
            exitThisMenu();
            return;
        }
        if (Model.IsWaiting)
            return;
        for (int index = 0; index < suggestions.Count; index++)
        {
            if (!suggestions[index].containsPoint(x, y))
                continue;
            input.Text = Model.SelectSuggestion(index)!;
            FocusInput(fromKeyboard);
            Game1.playSound("smallSelect");
            return;
        }
        if (sendButton.containsPoint(x, y))
            Send();
        else if (inputComponent.containsPoint(x, y))
            FocusInput(fromKeyboard);
        else
            input.Selected = false;
    }

    private void FocusInput(bool waitForEnterRelease)
    {
        focusWhenEnterReleased = waitForEnterRelease;
        input.Selected = !waitForEnterRelease;
        currentlySnappedComponent = inputComponent;
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
            exitThisMenu();
        else if (!Model.IsWaiting && key == Keys.Tab)
            FocusNext();
        else if (!Model.IsWaiting && !input.Selected && key == Keys.Enter)
        {
            Rectangle bounds = currentlySnappedComponent.bounds;
            Activate(bounds.Center.X, bounds.Center.Y, fromKeyboard: true);
        }
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (button == Buttons.B)
            exitThisMenu();
        else
            base.receiveGamePadButton(button);
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.6f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        Text(b, $"与{npcDisplayName}交谈", Bounds(32, 24, 856, 44), Game1.textColor);
        Text(b, "选择建议后可以编辑，也可以直接输入。", Bounds(32, 72, 856, 28), Color.DimGray, 0.8f);
        for (int index = 0; index < suggestions.Count; index++)
            DrawButton(b, suggestions[index], $"{index + 1}  {Model.Suggestions[index]}", !Model.IsWaiting);
        Text(b, "你想说的话", Bounds(32, 316, 856, 28), Game1.textColor, 0.8f);
        input.Draw(b, drawShadow: false);
        string status = Model.IsWaiting ? "正在等待角色回应……可以取消本次请求。"
            : Model.Error ?? $"{input.Text.Length}/{DialogueComposerModel.MaximumMessageLength} 字 · Enter 发送 · Esc 取消";
        Text(b, status, Bounds(32, 412, 856, 28), Model.Error is null ? Color.DimGray : Color.DarkRed, 0.8f);
        DrawButton(b, sendButton, "发送", !Model.IsWaiting);
        DrawButton(b, cancelButton, Model.IsWaiting ? "取消请求" : "取消", true);
        drawMouse(b);
    }

    private void DrawButton(SpriteBatch b, ClickableComponent button, string label, bool enabled)
    {
        bool hover = enabled && button.containsPoint(Game1.getMouseX(), Game1.getMouseY());
        Rectangle bounds = button.bounds;
        drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            enabled ? (hover ? Color.Wheat : Color.White) : Color.LightGray);
        Text(b, label, new Rectangle(bounds.X + (int)(16 * scale), bounds.Y, bounds.Width - (int)(32 * scale), bounds.Height),
            enabled ? Game1.textColor : Color.Gray, 0.85f);
    }

    private void Text(SpriteBatch b, string text, Rectangle area, Color color, float fontScale = 1f)
    {
        float size = scale * fontScale;
        string visible = text;
        while (visible.Length > 0 && Game1.smallFont.MeasureString(visible + "…").X * size > area.Width)
            visible = visible[..^1];
        if (visible != text)
            visible += "…";
        b.DrawString(Game1.smallFont, visible, new Vector2(area.X,
            area.Y + (area.Height - Game1.smallFont.LineSpacing * size) / 2), color,
            0, Vector2.Zero, size, SpriteEffects.None, 1);
    }
}

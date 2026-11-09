namespace StardewAI.Dialogue;

public sealed class MemoryManagerModel
{
    public const int PageSize = 6;
    public static IReadOnlyList<string> Types { get; } = Array.AsReadOnly(new[] { "favorite", "like", "dislike" });
    public static IReadOnlyList<string> States { get; } = Array.AsReadOnly(new[] { "active", "retracted" });
    private readonly ConversationMemoryStore store;
    private readonly DialogueIdentity identity;
    private readonly Action beforeChange;
    private readonly Action<Exception>? reportError;
    private readonly int day;
    private IReadOnlyList<LongTermPreference> entries = Array.Empty<LongTermPreference>();
    public long Revision { get; private set; }
    public int Page { get; private set; }
    public string? CategoryFilter { get; private set; }
    public string? SelectedId { get; private set; }
    public string Message { get; private set; } = "选择一条记忆查看或纠正。";
    public bool IsError { get; private set; }
    public bool IsAvailable { get; private set; }
    public int Count => entries.Count;
    public IReadOnlyList<LongTermPreference> Filtered => entries.Where(entry => CategoryFilter is null || entry.Category == CategoryFilter).ToArray();
    public int PageCount => Math.Max(1, (Filtered.Count + PageSize - 1) / PageSize);
    public IReadOnlyList<LongTermPreference> Visible => Filtered.Skip(Page * PageSize).Take(PageSize).ToArray();
    public LongTermPreference? Selected => entries.FirstOrDefault(entry => entry.Id == SelectedId);

    public IReadOnlyList<string> GetNavigationKeys(bool pagedEvidence)
    {
        var keys = new List<string> { "filter" };
        keys.AddRange(Enumerable.Range(0, Visible.Count).Select(index => "row" + index));
        if (Page > 0) keys.Add("previous");
        if (Page + 1 < PageCount) keys.Add("next");
        if (Selected is not null)
        {
            if (IsAvailable) keys.AddRange(new[] { "category", "type", "status", "input" });
            if (pagedEvidence) keys.Add("evidence");
            if (IsAvailable) keys.AddRange(new[] { "save", "forget" });
        }
        if (IsAvailable)
        {
            if (CategoryFilter is not null || Selected is not null) keys.Add("forget-category");
            keys.Add("clear");
        }
        keys.AddRange(new[] { "reload", "close" });
        return keys.AsReadOnly();
    }

    public MemoryManagerModel(ConversationMemoryStore store, DialogueIdentity identity, int day, Action beforeChange,
        Action<Exception>? reportError = null)
    {
        this.store = store;
        this.identity = identity;
        this.day = day;
        this.beforeChange = beforeChange;
        this.reportError = reportError;
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            entries = store.GetPreferences(identity).OrderByDescending(entry => entry.UpdatedAt)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
            Revision = store.GetRevision(identity);
            IsAvailable = true;
            Page = Math.Min(Page, PageCount - 1);
            int selectedIndex = Filtered.ToList().FindIndex(entry => entry.Id == SelectedId);
            if (selectedIndex >= 0)
                Page = selectedIndex / PageSize;
            else
                SelectedId = Visible.FirstOrDefault()?.Id;
        }
        catch (IOException error)
        {
            reportError?.Invoke(error);
            IsAvailable = false;
            SetMessage("记忆读取失败。请查看日志，修正文件后点击重新读取。", true);
        }
    }

    public void Select(string id)
    {
        if (Visible.Any(entry => entry.Id == id))
            SelectedId = id;
    }

    public void SetFilter(string? category)
    {
        if (category is not null && !LongTermPreferenceRules.Categories.Contains(category))
            return;
        CategoryFilter = category;
        Page = 0;
        SelectedId = Visible.FirstOrDefault()?.Id;
    }

    public void ChangePage(int offset)
    {
        Page = Math.Clamp(Page + offset, 0, PageCount - 1);
        SelectedId = Visible.FirstOrDefault()?.Id;
    }

    public void Save(string category, string type, string value, string status)
        => Change(() =>
        {
            MemoryCommitResult result = store.EditPreference(identity, Revision, SelectedId ?? "", category, type, value, status, day);
            return result.Committed ? null : result.Notices.FirstOrDefault() switch
            {
                "stale_revision" => "记录已更新，请重新选择后保存。",
                "duplicate_preference" => "该偏好或该类别的最爱已存在，请编辑已有记录。",
                "missing_entry" => "该记录已被删除。",
                _ => "保存失败，请检查类别、类型、状态和内容（1～80 字）。"
            };
        }, "记忆纠正已保存，相关旧交流已过滤。");

    public void ForgetSelected() => Change(() => store.Forget(identity, SelectedId ?? "") ? null : "该记录已被删除。",
        "单条遗忘已保存，相关旧交流已过滤。");

    public void ForgetCategory(string category) => Change(() => { store.ForgetCategory(identity, category); return null; },
        "类别遗忘已保存，近期交流及建议已清空。");

    public void ClearAll() => Change(() => { store.ClearAll(identity); return null; }, "全部记忆已清空。");

    public void Reload()
    {
        try
        {
            beforeChange();
            store.Reload(identity);
            Refresh();
            if (IsAvailable)
                SetMessage("记忆已重新读取。", false);
        }
        catch (InvalidOperationException)
        {
            SetMessage("场景已变化，请重新打开记忆面板。", true);
        }
    }

    private void Change(Func<string?> change, string success)
    {
        if (!IsAvailable)
            return;
        try
        {
            beforeChange();
            if (store.GetRevision(identity) != Revision)
            {
                Refresh();
                SetMessage("记录已更新，请重新选择后操作。", true);
                return;
            }
            string? error = change();
            Refresh();
            if (IsAvailable)
                SetMessage(error ?? success, error is not null);
        }
        catch (IOException error)
        {
            reportError?.Invoke(error);
            IsAvailable = false;
            SetMessage("保存失败，改动未提交。请查看日志，修正后重新读取。", true);
        }
        catch (InvalidOperationException)
        {
            SetMessage("场景已变化，请重新打开记忆面板。", true);
        }
    }

    private void SetMessage(string message, bool error) { Message = message; IsError = error; }

    public static string CategoryLabel(string category) => category switch
    {
        "flower" => "花", "weather" => "天气", "food" => "食物", "drink" => "饮品",
        "season" => "季节", "activity" => "活动", "animal" => "动物", "color" => "颜色", _ => "全部类别"
    };
    public static string TypeLabel(string type) => type switch { "favorite" => "最爱", "like" => "喜欢", _ => "不喜欢" };
    public static string StateLabel(string state) => state == "active" ? "有效" : "已撤销";
}

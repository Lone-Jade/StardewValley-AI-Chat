namespace StardewAI.AI;

public sealed class DeepSeekOptions
{
    public string Provider { get; set; } = "deepseek";
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-flash";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxTokens { get; set; } = 1024;
    public bool UseJsonMode { get; set; } = true;
    public bool DisableThinking { get; set; } = true;
    public string ReasoningEffort { get; set; } = "";

    public string? GetUnavailableReason()
    {
        if (!Enabled)
            return "disabled";
        if (string.IsNullOrWhiteSpace(ApiKey))
            return "missing_api_key";
        if (Provider is not ("deepseek" or "openai" or "claude" or "minimax" or "qwen" or "kimi" or "openai-compatible"))
            return "invalid_provider";
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || BaseUrl.Contains('{') || BaseUrl.Contains('}'))
            return "invalid_base_url";
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 100
            || ApiKey.Any(char.IsControl) || TimeoutSeconds is < 1 or > 60
            || MaxTokens is < 64 or > 16384
            || ReasoningEffort is not ("" or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            return "invalid_configuration";
        return null;
    }
}

# AI 服务配置

首次公测默认选择 DeepSeek。安装目录中的 `config.example.json` 与 `ConfigTemplates/config.deepseek.example.json` 提供相同默认配置。

## 导入模板

1. 退出游戏，在模组目录中选取服务模板，复制为同目录下的 `config.json`。
2. 填写 `AI.ApiKey`，将 `AI.Enabled` 改为 `true`。
3. 核对服务地址、账号支持的模型与输出预算，然后启动游戏。

模板为完整配置文件，含每日上限、记忆及交互设置。已有配置需要保留这些设置时，只替换其中的 `AI` 对象。旧版 `DeepSeek` 对象会自动迁移到 `AI`，同时存在时使用 `AI`。

## 模板索引

| 服务 | 模板 | 默认模型 | 基础地址 |
| --- | --- | --- | --- |
| DeepSeek | config.deepseek.example.json | deepseek-flash | https://api.deepseek.com |
| Claude | config.claude.example.json | claude-sonnet-5-5 | https://api.anthropic.com/v1 |
| OpenAI / ChatGPT | config.openai.example.json | gpt-6-luna | https://api.openai.com/v1 |
| MiniMax | config.minimax.example.json | MiniMax-M3 | https://api.minimax.io/v1 |
| Qwen / 通义千问 | config.qwen.example.json | qwen-plus | 按工作空间与区域填写 |
| Kimi | config.kimi.example.json | kimi-k3.1-pro | https://api.moonshot.ai/v1 |
| OpenAI 兼容接口 | config.openai-compatible.example.json | 按服务填写 | 按服务填写 |

上述模型与协议依据准备公测时的官方文档填写。账号可用模型和区域地址以服务控制台为准。Qwen 模板中的 `{WorkspaceId}` 需要替换为实际工作空间 ID，通用模板的地址与模型也需要替换。

## 字段

| 字段 | 用途 |
| --- | --- |
| Provider | deepseek、claude、openai、minimax、qwen、kimi 或 openai-compatible |
| Enabled | 启用远程生成 |
| ApiKey | 所选服务的本地 API 密钥 |
| BaseUrl | API 基础地址，客户端追加 messages 或 chat/completions 路径 |
| Model | 账号可调用的模型 ID |
| TimeoutSeconds | 每轮请求超时，范围 1～60 秒 |
| MaxTokens | 输出预算，范围 64～16384，包含服务计入预算的思考输出 |
| UseJsonMode | 向兼容接口发送 JSON 输出参数 |
| DisableThinking | 向支持此控制的服务发送关闭思考参数 |
| ReasoningEffort | 服务支持时发送指定推理强度，留空采用服务默认值 |

回复、三条建议、评分与记忆更新共用输出预算。默认 DeepSeek 预算为 1024，Claude 和 Kimi 模板为 4096。模板请求超时均为 30 秒，旧配置继续使用原有显式值。可以在 Generic Mod Config Menu 调整输出上限。

Claude 通过 Messages API 调用，系统提示单独传入，客户端读取 text 内容块并校验停止原因。OpenAI 使用 Chat Completions，模板使用 reasoning_effort=none。MiniMax 模板使用可关闭思考的 MiniMax-M3，并启用 reasoning_split，读取最终回答。Qwen 模板关闭思考模式。

Kimi 模板采用当前公开模型，推理强度设为 low。近期交流作为有界上下文随当前玩家消息发送，客户端使用最终 content 解析 JSON。更换模型时，核对该模型对思考模式及输出预算的要求。

启用后，人物状态、当前消息、近期交流和已启用的偏好记忆会发送到所配置的服务。密钥保存在自己的 `config.json` 中。报告问题时提供服务名、模型和错误代码，并从日志与截图中删除密钥和私人内容。

## 官方资料

- DeepSeek 的请求参数、思考模式与 JSON 输出格式。 [JSON Output](https://api-docs.deepseek.com/guides/json_mode/) · [Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
- Claude 的 Messages 协议、认证与模型列表。 [Messages](https://platform.claude.com/docs/en/api/messages/create) · [Authentication](https://platform.claude.com/docs/en/manage-claude/authentication) · [Models](https://platform.claude.com/docs/en/models/overview)
- OpenAI 的 Chat Completions 和模板模型。 [API Reference](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) · [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna)
- MiniMax 的兼容协议及模型思考控制。 [OpenAI SDK](https://platform.minimax.io/docs/api-reference/text-openai-api)
- Qwen 的工作空间地址及兼容调用。 [OpenAI compatibility](https://help.aliyun.com/en/model-studio/compatibility-of-openai-with-dashscope)
- Kimi 的基础地址、模型与思考协议。 [Quickstart](https://platform.kimi.ai/docs/get-your-api-key) · [Models](https://platform.kimi.ai/docs/models) · [Thinking](https://platform.kimi.ai/docs/guide/use-thinking-models)

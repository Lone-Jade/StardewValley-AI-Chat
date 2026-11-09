# StardewValley-AI-Chat

公测版本 0.1.0-beta.1，适用于 Stardew Valley 1.6.15 和 SMAPI 4.5.2。

## 安装

1. 安装 SMAPI，将压缩包中的整个 `StardewValley-AI-Chat` 文件夹解压到游戏的 `Mods` 目录。
2. 打开模组文件夹中的 `config.json`，填写 `AI.ApiKey`，将 `AI.Enabled` 改为 `true`，然后通过 SMAPI 启动游戏。
3. 默认服务为 DeepSeek。更换服务时，从 `ConfigTemplates` 选取模板，将其中的 `AI` 对象复制到自己的配置中。具体字段与官方文档见 `docs/API_CONFIGURATION.md`。

配置模板中的密钥均为空。Qwen 模板需要填写实际工作空间地址，通用模板需要填写自己的服务地址与模型。

## 操作

靠近并面向 NPC 按 K，或右键选择“对话”。每日首聊先显示原版开场，随后可以选择三条建议或自行输入文字。Enter 发送，Esc 关闭或取消等待。右键“记忆”可查看、纠正或遗忘人物记住的偏好。

普通 NPC 默认每日 5 轮，伴侣默认不限。安装 Generic Mod Config Menu 后可在设置页调整上限、长期记忆与输出预算。中文输入可配合 Input Method Fix 使用。

启用远程生成后，人物状态、玩家输入、近期交流与已启用的偏好记忆会发送到配置的服务端。近期交流和长期偏好保存在模组目录的 `Data/ConversationMemory`，升级前请备份自己的配置与记忆。

## 包内文件

`i18n/default.json` 提供常用提示文本，默认简体中文。`assets/icon.png` 为项目图标。`Data/Dialogues` 提供接口关闭或失败时使用的本地对话池，`ConfigTemplates` 提供服务配置模板。

人物设定及来源见 `docs/NPC_PERSONAS.md`，测试步骤见 `docs/TESTING.md`。反馈时请提供版本、服务、模型、NPC 与复现步骤，并清理日志中的密钥、私人台词和本机路径。

## 许可证与开发

项目采用 MIT 许可证，完整条款见 `LICENSE`。本项目使用 ChatGPT / Codex 完成开发。

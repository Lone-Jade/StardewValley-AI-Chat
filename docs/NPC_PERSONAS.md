# 原版 NPC 人物设定

2026-11-09 核对。覆盖 [Stardew Valley Wiki 村民清单](https://stardewvalleywiki.com/Villagers)中的 46 名人物，另补老水手，共 47 份档案。每份包含背景、说话风格、人物边界、知识范围和来源链接。事实经过简要改写，语气与边界是本项目为对话设计的约束，不是 Wiki 对模型行为的承诺；没有收录 Wiki 原文段落、游戏台词或图片。

## 使用方式与优先级

源码数据位于 [Vanilla.json](../src/StardewValley-AI-Chat/Data/NpcProfiles/Vanilla.json)，构建时嵌入 DLL。修改后需要重新编译，玩家安装包无需额外下载或放置人物文件，也不会在运行中访问 Wiki。

每次请求只注入内部名精确匹配的人物档案。`Mister Qi`、`Professor Snail` 和 `Old Mariner` 使用游戏内部名，不用 Wiki 标题或肖像文件名匹配。模组人物如 `Sophia`、`Custom.Abigail` 继续使用其游戏人物资料，不借用原版的相似姓名。深度改写原版人物的模组仍可能需要后续专门适配。

游戏当前人物资料、原版开场、地点、居所和原生关系状态优先于参考背景。已婚或室友人物的居所从所属玩家的房屋读取，其他人物使用游戏当前默认归位地图；不会把角色当前站立地点当成住址。没有提供的事件、世界解锁和任务进度保持未知，不按心数假定已经发生。

请求新增 `Relationship`，区分普通来往、交往、订婚、婚姻、室友、待同住室友、离婚、非社交和未知。心数表达熟悉程度，不能独立建立恋爱或婚姻。儿童保持符合年龄的交流，科罗布斯的室友关系保持非恋爱性质。[参考：Wiki 友谊机制](https://stardewvalleywiki.com/Friendship)、[科罗布斯](https://stardewvalleywiki.com/Krobus)

请求新增 `HomeLocation`，用于表达当前居所；雷欧在姜岛与大陆树屋两个阶段使用不同地图。该字段只确认居所，不证明搬家事件的其他细节或任何共同经历。[参考：雷欧](https://stardewvalleywiki.com/Leo)

人物能介绍自己确实提供的原生服务，但物品交付、找回、购买和任务结算仍须通过游戏菜单执行。个人秘密、传言与未发生的共同经历不作为确定事实。设定约束同时用于回复及原有好感度评估，0/10/20 点规则保持原有实现。

## 人物索引

以下风格是实现方向，完整约束见数据文件。每项链接为核对资料的来源。

| 内部名 | 风格与边界重点 | 资料来源 |
| --- | --- | --- |
| Abigail | 好奇、直率；冒险有兴致，不确认血缘传言 | [Wiki](https://stardewvalleywiki.com/Abigail) |
| Alex | 运动热情与自尊；不在初识时倾倒家庭伤痛 | [Wiki](https://stardewvalleywiki.com/Alex) |
| Elliott | 感性但简练；浪漫气质不自动建立恋爱 | [Wiki](https://stardewvalleywiki.com/Elliott) |
| Harvey | 温和、谨慎；诊疗隐私与医生身份 | [Wiki](https://stardewvalleywiki.com/Harvey) |
| Sam | 热情、随和；音乐兴趣与家人关系 | [Wiki](https://stardewvalleywiki.com/Sam) |
| Sebastian | 内敛、简短；个人空间与编程工作 | [Wiki](https://stardewvalleywiki.com/Sebastian) |
| Shane | 防备中有善意；不把困扰写成全部人格 | [Wiki](https://stardewvalleywiki.com/Shane) |
| Emily | 热情、手工兴趣；精神体验不等于全知 | [Wiki](https://stardewvalleywiki.com/Emily) |
| Haley | 直白、重视摄影；熟悉后自然变化 | [Wiki](https://stardewvalleywiki.com/Haley) |
| Leah | 独立、自然观察与雕塑；不预设展览和前任剧情 | [Wiki](https://stardewvalleywiki.com/Leah) |
| Maru | 好奇、技术表达清楚；不冒充主治医生 | [Wiki](https://stardewvalleywiki.com/Maru) |
| Penny | 耐心、略拘谨；阅读与教学 | [Wiki](https://stardewvalleywiki.com/Penny) |
| Caroline | 平和、家庭与茶；不确认家族猜测 | [Wiki](https://stardewvalleywiki.com/Caroline) |
| Clint | 害羞而务实；铁匠服务与感情分寸 | [Wiki](https://stardewvalleywiki.com/Clint) |
| Demetrius | 理性、重观察；科学知识与家庭边界 | [Wiki](https://stardewvalleywiki.com/Demetrius) |
| Dwarf | 直接、对人类好奇；不全知古代技术 | [Wiki](https://stardewvalleywiki.com/Dwarf) |
| Evelyn | 温暖、乐观；园艺与烘焙 | [Wiki](https://stardewvalleywiki.com/Evelyn) |
| George | 直率、尊重尊严；轮椅与私人经历 | [Wiki](https://stardewvalleywiki.com/George) |
| Gus | 宽厚、热情；餐吧服务与顾客隐私 | [Wiki](https://stardewvalleywiki.com/Gus) |
| Jas | 害羞、儿童视角；游戏与学习 | [Wiki](https://stardewvalleywiki.com/Jas) |
| Jodi | 务实、关心家庭；也有自己的生活空间 | [Wiki](https://stardewvalleywiki.com/Jodi) |
| Kent | 克制、适应平静生活；不虚构战史或军衔 | [Wiki](https://stardewvalleywiki.com/Kent) |
| Krobus | 谨慎、温和；非恋爱室友与文化差异 | [Wiki](https://stardewvalleywiki.com/Krobus) |
| Leo | 儿童与鹦鹉视角；当前居所由游戏提供 | [Wiki](https://stardewvalleywiki.com/Leo) |
| Lewis | 和气、熟练；镇务与私人生活分开 | [Wiki](https://stardewvalleywiki.com/Lewis) |
| Linus | 平静、重自主；不预设需要施舍或住房 | [Wiki](https://stardewvalleywiki.com/Linus) |
| Marnie | 温厚、动物照顾；交易与感情边界 | [Wiki](https://stardewvalleywiki.com/Marnie) |
| Pam | 粗犷、朴实；不猜测巴士及住房进度 | [Wiki](https://stardewvalleywiki.com/Pam) |
| Pierre | 精明、热络；商业与家庭隐私 | [Wiki](https://stardewvalleywiki.com/Pierre) |
| Robin | 爽朗、能干；木工知识与原生建造入口 | [Wiki](https://stardewvalleywiki.com/Robin) |
| Sandy | 欢迎来客；不编造真实姓名或赌场权限 | [Wiki](https://stardewvalleywiki.com/Sandy) |
| Vincent | 活泼、儿童视角；朋友与家庭日常 | [Wiki](https://stardewvalleywiki.com/Vincent) |
| Willy | 老练、朴实；渔业与未确认的修船进度 | [Wiki](https://stardewvalleywiki.com/Willy) |
| Wizard | 庄重、神秘而清楚；魔法不是读心或全知 | [Wiki](https://stardewvalleywiki.com/Wizard) |
| Birdie | 海边长者与个人回忆；不预设任务完成 | [Wiki](https://stardewvalleywiki.com/Birdie) |
| Bouncer | 少言、坚决；聊天不能绕过准入条件 | [Wiki](https://stardewvalleywiki.com/Bouncer) |
| Fizz | 熟练推销；聊天不能改变完美度 | [Wiki](https://stardewvalleywiki.com/Fizz) |
| Gil | 寡言、慵懒；奖励取决于原生讨伐记录 | [Wiki](https://stardewvalleywiki.com/Gil) |
| Governor | 正式、礼貌；来访官员与居民隐私 | [Wiki](https://stardewvalleywiki.com/Governor) |
| Grandpa | 慈爱、平和；保留特殊出现方式 | [Wiki](https://stardewvalleywiki.com/Grandpa) |
| Gunther | 礼貌、专注；考古判断与原生捐赠流程 | [Wiki](https://stardewvalleywiki.com/Gunther) |
| Henchman | 谨慎、固执；看守职责与任务放行 | [Wiki](https://stardewvalleywiki.com/Henchman) |
| Marlon | 沉稳、务实；准确介绍失物找回服务 | [Wiki](https://stardewvalleywiki.com/Marlon) |
| Morris | 职业化、精明；不假定玩家选择商业路线 | [Wiki](https://stardewvalleywiki.com/Morris) |
| Mister Qi | 从容、有谜意；不主动剧透或发放奖励 | [Wiki](https://stardewvalleywiki.com/Mr._Qi) |
| Professor Snail | 好奇、研究者；捐赠与调查答案的分寸 | [Wiki](https://stardewvalleywiki.com/Professor_Snail) |
| Old Mariner | 老练、含蓄；不替玩家决定或执行求婚 | [Wiki](https://stardewvalleywiki.com/Old_Mariner) |

## 验证边界

离线测试核对档案完整性、重复与非法数据、内部名匹配、逐人物注入隔离，以及关系和居所的传递。模拟响应验证回复、三条建议与记忆协议兼容。少量真实接口抽查及发现的问题见 [测试记录](TESTING.md)。

档案齐备不等于所有人物已通过实际游戏交互验证。特殊人物、事件角色和独立服务角色仍受原有 `NPC.IsVillager`、距离、菜单与剧情保护约束，本次不改变它们的出现或入口。提示词也不能保证模型每次都完全遵守人设，后续仍需逐人物、多轮抽查。

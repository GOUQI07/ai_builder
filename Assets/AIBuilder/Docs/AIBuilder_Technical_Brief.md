# AI Builder Demo 技术简述

## Prompt 结构
文本生成通过 OpenAI-compatible Responses 接口调用，输入包含当前节点标题、正文、玩家选择、选择意图和四项数值。模型被要求只返回 JSON：`storyText`、`leftChoice`、`rightChoice`、`statDelta`、`imagePrompt`、`summaryTags`。`statDelta` 单项限制在 `-15` 到 `15`，运行时还会再次夹取，避免数值崩坏。

图片生成固定使用 `gpt-image-2`，提示词聚焦“低多边形、黑暗童话、王权式卡牌、扁平色块”。主干节点使用本地程序化预制图，首次生成分支或约 30% 分支尝试实时生图。

## 容错防卡机制
运行时从环境变量或本机未入库配置读取 `OPENAI_API_KEY`、`AI_BUILDER_TEXT_MODEL` 等配置。无 Key、无文本模型、网络超时、非 JSON、字段缺失、图片失败时都会自动切到本地 Mock 或占位图。所有 AI 请求都有超时、取消和异常捕获，失败不会阻塞剧情推进。

## 缓存命中逻辑
缓存 key 由 `sourceNodeId + choiceId + statsBand` 组成，数值按十位分桶，模拟“相似状态下复用同一分支”。首次偏离主干会调用 AI/Mock 生成分支并写入 `Application.persistentDataPath/AIBuilder/node_cache.json`；二次相同选择直接读取缓存，界面显示 `Cache Hit`，不再发起生成。

## 数值判定方案
四项数值为生命、武力、财富、信仰，默认 `70/50/50/50`。主干选择使用策划配置的 `statHint`，生成分支使用 AI 返回的 `statDelta`。生命、财富、信仰任一归零即 Game Over；武力归零只作为剧情压力，不单独结束游戏。

## 编辑器与演示
Unity 菜单 `AI Builder/Node Editor` 可编辑 3 个主干节点、左右选项、预制图引用和数值变化，并预览本地分支缓存网络。Demo 运行后选择偏离主干的选项可演示首次生成，再重开选择相同分支可演示缓存命中。

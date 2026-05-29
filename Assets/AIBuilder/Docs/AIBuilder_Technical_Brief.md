# AI Builder 技术简述

## AI Prompt 结构

文本分支使用 OpenAI-compatible Responses 接口。运行时 prompt 明确角色为黑暗童话互动视觉小说的分支作者，并输入当前节点标题、正文、玩家选择、选择意图和四项状态值。模型必须只返回 JSON，字段顺序为：`storyText`、`leftChoice`、`rightChoice`、`statDelta{life,force,wealth,faith}`、`imagePrompt`、`panoramaPrompt`、`locationTag`、`moodTag`、`majorEventTag`、`summaryTags`。正文与选项要求简体中文，正文短于约 80 个汉字；图片和全景图 prompt 使用英文视觉描述，便于风格稳定。

Story Importer 另有三类改编 prompt：分块摘要、章节大纲、主干锚点。它们都要求 JSON-only 输出，并禁止长篇复制源文本。图片 prompt 会统一套用低多边形、王权式卡牌 UI、哑光纸感、低饱和红/金/羊皮纸/炭黑/蓝绿色调，并排除写实、Logo、文字和现代 UI。

## 容错防卡机制

配置从代码默认值、本机 JSON、`.env`/系统环境变量逐层覆盖；API Key 只从环境读取。无 Key、模型缺失、文本模型疑似图片模型、未知 Provider、网络超时、非 JSON、字段缺失、图片失败都会进入 Mock 或占位图兜底。文本、图片、全景图、远程缓存请求均设置超时、取消、异常捕获和失败日志；运行中会用 `busy`、请求取消令牌、60 秒选择倒计时、缺节点 Overlay、Game Over/章节完成兜底，保证交互状态可收束。

运行时分支生成支持 streaming 文本解析，但分支进入不会长等流式输出：优先使用已完成预测或缓存；若同 key 任务仍在跑，就把“首字机会”压到极短，立即展示 Draft 本地预设分支，让玩家继续操作。后台 AI 继续完成并覆盖 Draft 缓存，但不替换当前画面。若 streaming 或完整文本失败，会回退 Mock/Draft。图片与全景图是后台队列，文本先落地，视觉资源慢或失败不会卡住选项链路。Story Importer 的摘要、大纲和锚点生成也都有 Mock fallback，长任务可以取消，并把错误写入 job 状态。

预测式预生成也属于防卡策略：每次节点展示都会做两层 lookahead，检查当前节点以及后续主干可达节点的左右选项；凡 `nextMainlineNodeId` 为空、没有后续主干目标的选项，都会提前排队生成 AI 分支文本。拖拽不是批量入口，只在玩家接近某一侧提交方向时，把当前指向的无后续节点分支提升到高优先级。预测任务按 cache key 存入 `textPredictionTasks` 去重，普通预测受并发限流，玩家提交时复用同一任务，不重复请求。系统还会提前安排 `statJudgementTasks` 数值裁决预判，并在分支文本落地或缓存命中后排队补卡牌图、全景图。

缓存和资源层也有防卡：Rejected 缓存不复用，Draft 可被正式生成覆盖；本地缓存读取失败会忽略并重建，保存使用临时文件落盘；远程缓存是可选层，读写、上传、下载失败只跳过并退回本地。图片/全景图队列用 queued/running key 去重、并发泵执行、状态记录 `Queued/Generating/Generated/Reused/Failed/Skipped`，同语义图可复用。配置、章节数、锚点数、图片比例、超时、statDelta、文本长度、标签长度都会归一化或夹取，避免脏输入拖垮流程。

## 缓存命中逻辑

分支缓存 key 为 `storyId|sourceNodeId|choiceId|statsBand`，其中 `statsBand` 按生命、武力、财富、信仰的十位分桶生成，例如 `7-5-5-5`。同一故事、同一节点、同一选择、相近数值状态会复用同一分支；不同 storyId 自动隔离。命中且状态不是 `Rejected` 时直接读取 `node_cache.json` 或远程缓存，界面显示 `Cache Hit`，不再调用文本模型。

图片缓存与全景图缓存使用更粗的语义 key：`storyId|img_style_v2|chapter|location|mood|event` 与 `storyId|pano_style_v2|chapter|location|mood|event`。实时生图比例不是临时随机，而是对 key 做稳定哈希；默认卡牌图 30%、全景图 15%，并可保证每个故事首张图/首张全景图优先生成。远程缓存为可选层，读写或资源上传失败会退回本地缓存。

## 数值判定方案

四项数值为生命、武力、财富、信仰，默认 `70/50/50/50`，最终值始终夹取在 `0..100`。主干节点使用策划配置的 `statHint`；AI 分支生成返回保守 `statDelta`，运行时再次夹取在 `-5..5`。为了避免每次点击都抖动数值，系统每 2-3 次选择才触发一次“局势裁决”。

局势裁决 prompt 根据近期事件和当前状态返回 `statDelta` 与中文理由，允许 `-100..100` 的严重后果，但要求每个非零变化都由具体事件因果支撑。普通压力建议 `1..5`，明显代价 `6..15`，重大危机 `16..40`，灾难事件 `41..100`。真实裁决失败时改用本地保守规则。生命、财富、信仰任一归零即 Game Over；武力归零只表示战斗能力崩溃，不单独结束游戏。

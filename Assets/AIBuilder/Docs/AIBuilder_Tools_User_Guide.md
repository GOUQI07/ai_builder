# AI Builder 工具使用教程

本文档覆盖当前工程内 `AI Builder` 菜单下的所有策划、配置、联调、审核与运行演示工具。建议按“配置 AI -> 导入故事 -> 生成主干 -> 审核导出 -> 运行 Demo -> 查看缓存”的顺序使用。

## 0. 使用前准备

### 打开工程和场景

1. 使用 Unity 6 打开工程 `E:\Unity_Projects\ai_builder`。
2. 打开场景 `Assets/Scenes/SampleScene.unity`。
3. 等待编译完成，确认 Console 没有编译错误。

### 关键资产与输出位置

| 类型 | 路径 |
| --- | --- |
| AI 配置示例 | `Assets/AIBuilder/Data/ai_provider.example.json` |
| 本机 AI 配置 | `Application.persistentDataPath/AIBuilder/ai_provider.json` |
| 故事工程草稿 | `Assets/AIBuilder/Data/story_project.json` |
| 运行时主干节点 | `Assets/AIBuilder/Resources/mainline_nodes.json` |
| 角色立绘预设 | `Assets/AIBuilder/Resources/character_portrait_presets.json` |
| 运行时节点缓存 | `Application.persistentDataPath/AIBuilder/node_cache.json` |
| 运行时图片/全景图/Smoke 图 | `Application.persistentDataPath/AIBuilder` |
| 角色立绘生成图 | `Application.persistentDataPath/AIBuilder/PortraitPresets/style_v2` |

### 配置优先级

配置读取顺序为：代码默认值 -> 本机 JSON -> `.env` 或系统环境变量。系统环境变量与 `.env` 同名时，系统环境变量优先。

密钥不要写入仓库。推荐只在环境变量中配置 `OPENAI_API_KEY`，本机 JSON 只保存模型名、开关、比例、超时等非密钥字段。

## 1. AI Settings

入口：`AI Builder/AI Settings`

用途：配置文本模型、图片模型、运行时生图比例、全景图比例、远程节点缓存，以及保存/重载本机配置。

### Provider 区域

| 字段 | 说明 |
| --- | --- |
| Provider Type | `openai_compatible` 使用真实接口；`mock` 使用本地兜底生成。未知值会自动回退到 Mock。 |
| Base URL | 文本 Responses 接口的基础地址，例如 `https://api.asxs.top/v1`。 |
| Wire API | 文本接口路径，默认 `responses`。 |
| Text Model | 文本生成模型。为空或看起来是图片模型时，文本真实调用会跳过并回退。 |
| Image Model | 图片生成模型，默认 `gpt-image-2`。 |
| Image Base URL | 图片接口专用 Base URL；为空时复用 `Base URL`。 |
| Image Endpoint | 图片接口相对路径，默认 `images/generations`。 |
| Image Endpoint URL | 完整图片接口 URL；配置后优先级最高。 |
| API Key Env Name | 文本 API Key 环境变量名，默认 `OPENAI_API_KEY`。 |
| Image API Key Env Name | 图片 API Key 环境变量名；为空时复用文本 Key。 |
| Reasoning Effort | 传给文本请求的推理强度；空值表示不传。 |
| Timeout Seconds | 文本请求超时，运行时会夹取在 5-120 秒。 |
| Image Timeout Seconds | 图片请求超时，运行时会夹取在 8-240 秒。 |
| Image Size / Resolution / Quality / Output Format | 图片生成参数，默认 `1:1`、`1k`、`low`、`png`。 |
| Panorama Image Size | 全景图比例；如果填成方图会被修正为 `16:9`。 |
| Disable Response Storage | 是否关闭服务端响应存储，默认开启关闭存储。 |

窗口只显示 API Key 的 `Found/Missing` 状态，不显示密钥内容。

### Runtime Image Policy 区域

| 字段 | 说明 |
| --- | --- |
| Enable Runtime Images | 是否允许运行时生成卡牌图片。关闭后文本分支仍可生成。 |
| Image Generation Ratio | 非首张实时卡牌图的触发比例，0-1。默认 `0.3`。 |
| Guarantee First Image | 若当前故事还没有成功生成过卡牌图，首个新分支强制尝试生图。 |
| Enable Runtime Panoramas | 是否允许运行时生成 16:9 背景全景图。 |
| Panorama Generation Ratio | 普通分支全景图触发比例，默认 `0.15`。 |
| Guarantee First Panorama | 若当前故事还没有成功生成过全景图，首个可生成节点强制尝试。 |

图片与全景图不是每次随机抛硬币，而是基于 cache key 的稳定哈希判定；同一个分支 key 每次结果一致，便于复现和控成本。

### Remote Node Cache 区域

| 字段 | 说明 |
| --- | --- |
| Enable Remote Cache | 开启后使用远程缓存 Store；未配置 Base URL 时仍为本地缓存。 |
| Cache Base URL | 远程节点缓存服务地址。 |
| Cache API Key Env | 远程缓存 Bearer Token 的环境变量名。 |
| Cache Timeout Seconds | 远程缓存请求超时，2-60 秒。 |

远程缓存是可选能力。开启后仍会保留本地落盘副本；远程读写失败只记录 warning，不阻塞 Demo。

### 常用操作

1. 打开窗口后先确认 `API Key` 与 `Image API Key` 显示 `Found`。
2. 填入文本模型与图片模型。
3. 设置图片比例、全景图比例和超时。
4. 点击 `Save Local Config` 保存到本机 `persistentDataPath`。
5. 点击 `Copy Path` 可复制本机配置路径，便于检查 JSON。
6. 修改 `.env` 或系统环境变量后，重载窗口或重新运行 Demo 以确认覆盖生效。

## 2. Story Importer

入口：

- `AI Builder/Story Importer/Open Window`
- `AI Builder/Story Importer/Validate Project`
- `AI Builder/Story Importer/Export Mainline`

用途：把小说、名著片段或策划稿导入为可玩的稳定主干。它会把源文本分块，总结为章节大纲，再为每章生成 2-3 个必经主干节点。

### 推荐完整流程

1. 打开 `AI Builder/Story Importer/Open Window`。
2. 在 Project 区域设置：
   - `Title`：项目标题。
   - `Project Id`：故事缓存命名前缀；影响缓存隔离。
   - `Target Chapters`：目标章节数，限制为 1-300。
   - `Min Anchors` / `Max Anchors`：每章主干锚点数，限制为 1-5。
   - `Chunk Characters`：源文本分块长度，限制为 1000-20000。
3. 在 Source Import 区域点击 `Import .txt/.md`，或粘贴文本后点击 `Import Pasted Text`。
4. 先点击 `Summarize All Pending Chunks`，让 AI 或 Mock 总结所有分块。
5. 点击 `Build Chapter Outline`，生成章节列表。
6. 点击 `Generate Missing Anchors` 或 `Generate All`，为章节生成锚点节点。
7. 人工检查 Details 区域的章节、锚点、左右选项和数值。
8. 对可用内容点击 `Approve Chapter`、`Approve Anchor`，或点击 `Approve All` 批量通过。
9. 点击工具栏 `Validate`，确认没有 error。
10. 点击 `Export Mainline`，导出到 `Assets/AIBuilder/Resources/mainline_nodes.json`。

### Source Import 区域

| 操作 | 说明 |
| --- | --- |
| Show Text | 展开/收起粘贴文本输入框。 |
| Import .txt/.md | 从磁盘选择文本或 Markdown 文件。 |
| Import Pasted Text | 使用粘贴框内容创建源文本分块。 |

导入后会重建 `sourceChunks`，清空旧 summaries 与 chapters，并根据文本内容生成新的 `projectId`。如果已经做过人工编辑，导入前请确认是否需要保留旧版本。

### Chunks 区域

| 操作 | 说明 |
| --- | --- |
| Summarize Selected Chunk | 只总结当前选中的分块。 |
| Summarize Next Pending Chunk | 总结下一个未总结或失败分块。 |
| Summarize All Pending Chunks | 批量总结全部待处理分块。 |

AI 总结输出字段包括标题、摘要、角色、地点、冲突和时间线备注。后续的章节大纲、角色立绘预设和默认全景图都会读取这些摘要信息。

### Chapters 区域

| 操作 | 说明 |
| --- | --- |
| Build Chapter Outline | 根据 summaries 生成章节大纲。长任务超时会比普通文本请求更长。 |
| Generate Anchors For Selected | 为选中章节生成主干锚点。 |
| Generate Missing Anchors | 只为锚点不足的非驳回章节补齐。 |
| Generate All | 为所有非驳回章节重新生成锚点。 |

章节状态包括 `Draft`、`Approved`、`Rejected`、`Failed`。导出时会跳过 `Rejected` 章节。

### Details 区域

Details 区域可直接编辑选中分块、章节和锚点。

章节可编辑字段包括 `Id`、`Index`、`Title`、`Status`、`Source Chunks`、`Summary`。锚点可编辑字段包括 `Node Id`、`Title`、`Status`、`Mainline Choice`、`Image Ref`、`Image Prompt`、`Stability Note`、`Body`、左右选项的 `Label`、`Intent`、`Next Mainline` 和四项 `statHint`。

`Mainline Choice` 决定哪个方向继续主干；另一侧通常作为偏离主干的 AI 分支入口。`statHint` 是主干选择的直接数值提示，运行时会夹取，避免单次变化过大。

### Default Background Panorama

用途：为故事生成一个默认 16:9 背景全景图。

| 操作 | 说明 |
| --- | --- |
| Auto on Import | 导入源文本后自动排队默认全景图状态。 |
| Rebuild Prompt | 根据摘要、地点、冲突、代表性源文本重建全景图 prompt。 |
| Generate Panorama | 调用图片模型生成默认全景图。 |

生成失败时记录 warning，并将状态置为 `Failed` 或 `SkippedUnavailable`，不会影响主干导出。

### Portrait Presets 面板

Story Importer 内置角色立绘预设入口：

| 操作 | 说明 |
| --- | --- |
| Rebuild Portrait Presets | 根据 summaries 中的 `characters[]` 生成/合并角色预设。 |
| Generate Missing Portraits | 调用图片模型补齐缺失角色图。 |
| Validate Portrait Presets | 输出检测到的角色数、预设数、已有图和缺失图。 |

角色预设会尽量保留已有 `id`、别名、prompt 和图片引用，避免覆盖人工修订。

### 校验与导出规则

`Validate Project` 会检查：

- 是否有可导出的非驳回章节。
- 每个非驳回章节是否至少有 `Min Anchors` 个非驳回锚点。
- 锚点是否有标题、正文、左右选项、选择文案。
- `mainlineChoice` 是否为 `left` 或 `right`。
- 未通过审核的章节/锚点会给 warning；结构缺失会给 error。

只有没有 error 时才建议执行 `Export Mainline`。导出结果会成为运行时 Demo 读取的主干节点。

## 3. Visual Node Editor

入口：`AI Builder/Visual Node Editor`

用途：可视化编辑主干节点，查看玩家或测试生成的分支缓存，并对分支做待审、通过、驳回状态管理。

### 界面结构

| 区域 | 说明 |
| --- | --- |
| 顶部工具栏 | 刷新、保存主干、保存分支审核、视图切换、展开/收起详情。 |
| 左侧列表 | 主干节点列表和分支缓存列表。 |
| 中央 Graph View | 主干节点与分支节点关系图。 |
| 右侧属性与审核 | 编辑节点字段、查看缓存 key、修改审核状态。 |

顶部摘要会显示主干数量、分支数量、待审数量、通过数量、驳回数量。

### 主干节点编辑

1. 在左侧选择一个主干节点。
2. 在右侧编辑 `Id`、`Chapter`、`Title`、`Mainline Index`、`Image Ref`、正文和左右选项。
3. `Next Mainline` 用于指定该方向是否继续到下一个主干节点。
4. 点击 `保存主干` 写回 `Assets/AIBuilder/Resources/mainline_nodes.json`。
5. 点击 `新增节点` 可在当前主干后追加一个基础节点。

主干节点适合做策划可控的稳定骨架；偏离主干的内容应通过运行时 AI 生成并进入分支缓存。

### 分支缓存审核

1. 运行 Demo 触发偏离主干选择，生成分支缓存。
2. 打开 Visual Node Editor，刷新列表。
3. 用搜索框按标题、来源节点、选择或 cache key 搜索。
4. 用下拉筛选 `全部分支`、`PendingReview`、`Approved`、`Rejected`。
5. 选择分支后检查：
   - `Cache Key`
   - `Source Node`
   - `Choice`
   - `Image Status` / `Image Key`
   - `Panorama Status` / `Panorama Key`
   - 分支标题、正文和左右选项。
6. 点击 `待审`、`通过`、`驳回` 修改状态。
7. 点击 `保存` 或顶部保存分支按钮写回 `node_cache.json`。

运行时不会复用 `Rejected` 分支；再次触发同一选择时会重新生成。`Approved` 与 `PendingReview` 可用于本地复用，远程服务可根据自己的复用策略选择只返回通过内容。

### Graph View 使用技巧

- 点击图中的分支节点会同步右侧详情。
- 点击节点上的 `详情` 可展开正文、来源、图片状态和全景图状态。
- 顶部 `展开全部详情` 适合截图或录屏展示节点网络。
- 分支颜色体现审核状态：待审、通过、驳回会使用不同色彩。

## 4. AI Smoke Test

入口：

- `AI Builder/AI Smoke Test/Text`
- `AI Builder/AI Smoke Test/Image`

用途：在进入 Play Mode 前，快速验证真实 AI 配置是否可用。

### Text Smoke

执行后会：

1. 读取 `AiProviderSettings`。
2. 检查 Provider、API Key、Text Model 是否可用。
3. 从主干图中取第一个可玩节点和一个选项。
4. 调用文本服务生成分支 JSON。
5. 检查 `storyText`、`leftChoice`、`rightChoice` 是否有效。

Console 结果：

- `text smoke passed`：真实文本模型可用。
- `text smoke skipped`：配置缺失或文本模型不可用。
- `reached the mock fallback`：接口失败或返回格式异常，已降级。
- `failed safely`：超时或异常被捕获，不会卡死编辑器。

### Image Smoke

执行后会：

1. 检查图片 API Key 与 Image Model。
2. 发送一条固定的王权风格图片 prompt。
3. 成功后把 PNG 写入 `Application.persistentDataPath/AIBuilder/ai_builder_image_smoke_*.png`。

Console 结果：

- `image smoke passed: <path>`：图片模型可用，路径为生成图。
- `image smoke skipped`：配置缺失。
- `image smoke failed safely`：图片请求失败或超时，但流程安全退出。

## 5. Portrait Presets

入口：

- `AI Builder/Portrait Presets/Rebuild From Story Project`
- `AI Builder/Portrait Presets/Generate Missing Portrait Images`
- `AI Builder/Portrait Presets/Validate`

用途：从故事摘要中提取角色，建立可复用的角色立绘预设，并为缺图角色批量生成头像。

### Rebuild From Story Project

读取 `Assets/AIBuilder/Data/story_project.json`，从 summaries 的 `characters[]` 中解析角色名、别名和描述，生成 `character_portrait_presets.json`。

合并规则：

- 优先保留已有预设的 `id`、`imagePrompt`、`imageRef`、`spriteKey`。
- 根据别名、包含关系和已有 ID 匹配旧预设。
- 为中文或非 ASCII 角色名生成稳定 hash ID。
- 自动补充常见别名变体，例如去掉“们”。

### Generate Missing Portrait Images

读取当前角色预设，只为缺图角色调用图片模型。默认并发为 2，内部会限制在 1-4。

输出位置：

`Application.persistentDataPath/AIBuilder/PortraitPresets/style_v2/<presetId>.png`

失败不会中断批量任务，会记录失败数量和 warning。

### Validate

输出：

- `detectedCharacterCount`：从故事中检测到的角色数量。
- `presetCount`：当前预设数量。
- `existingImageCount`：已有图片数量。
- `missingImageCount`：缺失图片数量。

## 6. Run Local Validation

入口：`AI Builder/Run Local Validation`

用途：执行本地结构和策略自检，不依赖真实 AI。

它会检查：

- 主干图至少有 3 个节点，首节点有左右选项。
- 数值增减会被夹取，生命/财富/信仰归零会 Game Over，武力单独归零不会结束游戏。
- 分支 cache key 对相同状态稳定，对不同故事隔离。
- 驳回缓存不会复用。
- 图片 key 与全景图 key 基于语义标签稳定生成且彼此隔离。
- Provider 工厂能正确创建真实服务或 Mock 服务。
- 图片比例、全景图比例、超时、图片参数会夹取到安全范围。
- Story Importer 的导入、规范化、校验、导出链路可用。
- 角色立绘预设构建与别名匹配可用。
- 示例配置文件不存在 API Key 明文。

建议每次修改 AI 配置、缓存逻辑、数值逻辑、Story Importer 或 Visual Node Editor 后运行一次。

## 7. 运行时 Demo

入口：打开 `Assets/Scenes/SampleScene.unity` 后进入 Play Mode。

用途：演示《王权》式滑动选择、AI 分支生成、30% 实时生图、全景图、数值生存和缓存复用。

### 基本交互

- 中央卡牌展示剧情图片和当前节点。
- 向左/向右滑动或选择按钮触发不同选项。
- 若选项连接到主干节点，立即进入主干。
- 若选项没有主干目标，运行时按 cache key 查询缓存；未命中则生成分支。
- 状态栏展示 Provider、文本/图片/全景图模式、故事 ID、缓存数量、图片数量、队列数量和审核统计。
- `清缓存` 按钮清除本机节点缓存，便于重新演示首次生成。

### 预测式预生成机制

Demo 不只是在玩家确认选择后才生成内容。它会在后台预测可能被玩家触发的分支，提前把文本、数值裁决和视觉资源准备好，从而减少等待。

| 触发点 | 行为 |
| --- | --- |
| 显示任意可玩节点 | 调用两层 lookahead，检查当前节点及后续主干可达节点的左右选项；凡 `nextMainlineNodeId` 为空、没有后续主干目标的选项，都会提前排队生成 AI 分支文本。 |
| 玩家拖拽接近某一侧 | 当拖拽强度达到约 `0.42` 时，只把当前指向的无后续节点分支提升为高优先级预测；拖拽本身不是“所有分支”的批量入口。 |
| 预测任务已存在 | 使用 `textPredictionTasks` 按 cache key 去重，多个入口不会重复请求同一分支。 |
| 后台预测并发 | 普通预测受 `RuntimeTextPredictionConcurrency = 2` 限制；玩家真正提交的分支优先级更高，会绕过普通预测限流。 |
| 数值预判 | 节点显示后会为左右选项提前安排 `statJudgementTasks`，真正选择时优先读取已完成的裁决结果。 |
| 图片/全景图回填 | 分支文本生成或缓存命中后，会排队补图和补全景；空闲时还会扫描最近最多 18 个分支做 backfill。 |
| 分支进入兜底 | 进入新分支时优先使用已完成预测或缓存；若同 key 任务还没完成，就把流式文本的首字机会压到极短，立即展示 Draft 本地预设分支。后台 AI 继续跑，完成后覆盖 Draft 缓存，不替换当前画面。 |

这个机制和缓存配合：如果预生成任务先完成，玩家真正选择时会直接取已经落入缓存的 `NodeCacheEntry`；如果预生成还没完成，则选择流程复用同一个任务并立刻展示 Draft，不再另开请求。

### 容错防卡总览

| 层级 | 防卡机制 |
| --- | --- |
| 交互层 | 使用 `busy` 避免重复提交；新请求会取消旧 `requestCancellation`；节点缺失显示 Overlay；无后续主干时进入章节完成；生命/财富/信仰归零进入 Game Over；60 秒倒计时保证选择节奏。 |
| 配置层 | 默认配置、本机 JSON、`.env`/系统环境变量逐层覆盖；未知 Provider 自动回退 Mock；文本模型疑似图片模型时不走文本真实调用；超时、比例、图片数量、轮询间隔等都会夹取。 |
| 文本层 | 文本请求有超时和取消；JSON 提取失败、字段缺失或接口异常会回退 Mock；生成结果会压缩正文、选项、标签，并夹取 `statDelta`。 |
| 分支进入 | 优先使用已完成预测或缓存；任务未完成时立即展示 Draft 本地预设分支。后台 AI 完成后覆盖 Draft 缓存，不替换当前画面。 |
| 预测层 | 节点展示时两层 lookahead 批量预生成所有无后续主干目标的分支；拖拽阈值只给当前指向分支提优先级；`textPredictionTasks` 去重、普通预测并发限流、玩家提交复用同一任务。 |
| 数值层 | `statJudgementTasks` 提前预判；真实裁决失败时走本地保守规则；数值最终夹取到 `0..100`，Game Over 规则明确。 |
| 缓存层 | cache key 稳定；`Rejected` 不复用；`Draft` 可被正式结果覆盖；缓存读取异常会忽略并重建；本地保存使用临时文件落盘。 |
| 资源层 | 图片/全景图独立后台队列，不阻塞文本；queued/running key 去重；同语义图片可复用；失败记录状态并继续流程。 |
| 远程层 | 远程缓存可选；读写、上传、下载失败只 warning 并退回本地；远程资源会先下载成本地文件再渲染。 |
| 编辑器层 | Story Importer 摘要/大纲/锚点失败回退 Mock；长任务可 Cancel；项目导入会规范化 ID、章节数、锚点数和选项结构；导出前有 Validate。 |

### 缓存演示流程

1. 进入 Play Mode。
2. 点击 `清缓存`。
3. 在主干节点选择一个偏离主干的选项。
4. 首次触发时，Demo 会显示生成中状态，文本成功后写入缓存。
5. 回到相同节点，以相同数值分桶和相同选项再次触发。
6. 状态文案应显示 `Cache Hit`，并直接读取缓存内容，不重复请求文本模型。

### 图片与全景图

卡牌图使用 `imageCacheKey = storyId + img_style_v2 + chapter + location + mood + event`。全景图使用 `pano_style_v2` 独立 key。语义相似的分支可复用同一张图或全景图。

如果图片模型不可用，分支文本仍会继续推进；图片状态会变为 `SkippedUnavailable`、`SkippedByPolicy` 或 `Failed`，并使用预制/占位视觉兜底。

### 数值生存

默认数值为：

- 生命 `70`
- 武力 `50`
- 财富 `50`
- 信仰 `50`

主干选择使用策划配置的 `statHint`，偏离主干使用 AI 返回的 `statDelta`，并叠加每 2-3 次选择触发一次的局势裁决。生命、财富、信仰任一归零即 Game Over；武力归零只代表战斗能力崩溃，不单独结束游戏。

## 8. Remote Node Cache

相关文档：

- `Assets/AIBuilder/Docs/AIBuilder_Remote_Node_Cache_API.md`
- `Assets/AIBuilder/Docs/README_Remote_Node_Cache_Backend.md`

用途：把本地 `node_cache.json` 的分支节点、图片 URL、全景图 URL 扩展为多玩家共享缓存，从而降低重复 AI 调用成本。

### Unity 侧启用步骤

1. 在 `AI Settings` 中开启 `Enable Remote Cache`。
2. 填写 `Cache Base URL`。
3. 如服务需要鉴权，填写 `Cache API Key Env`，并在系统环境变量或 `.env` 中放入该变量对应的 token。
4. 设置 `Cache Timeout Seconds`，建议 8-15 秒。
5. 运行 Demo 触发分支，观察 Console 中是否有 remote cache warning。

### 远程 API 摘要

| 接口 | 说明 |
| --- | --- |
| `GET /branch-cache/{cacheKey}` | 查询分支缓存。命中时返回 `entry`，未命中返回 `hit:false`。 |
| `PUT /branch-cache/{cacheKey}` | 按 cache key 幂等写入分支缓存。 |
| `POST /assets` | 上传图片或全景图资源，返回可下载 URL。 |

Unity 客户端会把远程资源下载到本地缓存后再渲染。远程读写、上传、下载失败均只降级为本地行为。

## 9. 推荐交付演示顺序

1. 打开 `AI Settings` 展示文本/图片模型、比例、超时和 Key 状态。
2. 执行 `Run Local Validation`，证明本地逻辑自检通过。
3. 执行 `AI Smoke Test/Text` 和 `AI Smoke Test/Image`，证明真实 AI 可用或展示安全降级。
4. 打开 `Story Importer`，展示导入、分块、摘要、章节、锚点、审核和导出。
5. 打开 `Visual Node Editor`，展示主干图与分支审核。
6. 进入 Play Mode，清缓存后演示首次偏离主干生成分支。
7. 再次触发同一选择，演示 `Cache Hit`。
8. 在 Visual Node Editor 中把该分支改为 `Rejected`，再次触发，演示驳回缓存不复用。

## 10. 常见问题

### Text Smoke 跳过

检查：

- `OPENAI_API_KEY` 是否存在。
- `AI_BUILDER_TEXT_MODEL` 或 AI Settings 的 `Text Model` 是否为空。
- Text Model 是否误填为 `gpt-image-*`。
- Provider Type 是否为 `mock`。

### Image Smoke 失败

检查：

- 图片 Key 是否存在；未配置专用图片 Key 时会复用文本 Key。
- `Image Model` 是否为空。
- `Image Endpoint URL` 或 `Image Base URL + Image Endpoint` 是否正确。
- `Image Timeout Seconds` 是否过短。

### Story Importer 输出是 Mock

说明真实文本配置不可用或请求失败，系统自动回退。先运行 Text Smoke，确认 Console warning，再检查 Base URL、模型名、Key、网络和超时。

### 分支没有生成图片

可能原因：

- `Enable Runtime Images` 为 false。
- 图片模型不可用。
- `Image Generation Ratio` 判定未命中。
- 当前故事已有首张图，且 `Guarantee First Image` 不再触发。
- 同一 `imageCacheKey` 已有生成图，会直接复用。

### 第二次选择没有 Cache Hit

检查：

- 是否是同一个故事 ID。
- 是否是同一个 source node 和 choice id。
- 四项数值是否落在同一十位分桶。
- 分支是否被标记为 `Rejected`。
- 是否点击过 `清缓存`。

### 导出主干失败

运行 `Validate Project`，优先修复 error：

- 没有非驳回章节。
- 某章非驳回锚点少于 `Min Anchors`。
- 锚点缺标题、正文、左右选项或选择文案。
- `mainlineChoice` 无效。

### 不想真实调用 AI

把 `Provider Type` 设为 `mock`，或移除模型名/API Key。Demo、Story Importer 和 Smoke Test 都会安全降级，适合离线演示 UI 和缓存流程。

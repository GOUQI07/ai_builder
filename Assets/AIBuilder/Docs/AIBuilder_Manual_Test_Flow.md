# AI Builder Manual Test Flow

> 目标：验证真实 AI 联调、文本/图片模型分离、30% 图片策略配置化、缓存复用和失败回退，不只停留在 smoke test。

## 测试前准备

1. 打开 Unity 项目 `E:\Unity_Projects\ai_builder`。
2. 打开 `Assets/Scenes/SampleScene.unity`。
3. 确认 Console 清空，且没有编译错误。
4. 确认本机配置来源：
   - 配置优先级是：代码默认值 < `Application.persistentDataPath/AIBuilder/ai_provider.json` < `.env` / 系统环境变量。
   - 如果 `.env` 中已有 `AI_BUILDER_TEXT_MODEL`、`AI_BUILDER_IMAGE_MODEL` 等字段，Unity 菜单 `AI Builder/AI Settings` 保存的 JSON 会被 `.env` 覆盖。
5. 不在截图、录屏、文档中展示 API key。只允许记录 `found/missing` 状态。

推荐基线配置：

```env
AI_BUILDER_PROVIDER_TYPE=openai_compatible
AI_BUILDER_TEXT_MODEL=gpt-5.5
AI_BUILDER_IMAGE_MODEL=gpt-image-2
AI_BUILDER_REASONING_EFFORT=low
AI_BUILDER_TIMEOUT_SECONDS=20
AI_BUILDER_IMAGE_TIMEOUT_SECONDS=60
AI_BUILDER_ENABLE_RUNTIME_IMAGES=true
AI_BUILDER_IMAGE_GENERATION_RATIO=0.3
AI_BUILDER_GUARANTEE_FIRST_GENERATED_IMAGE=true
```

## 通过标准总览

- Demo 场景可进入并正常交互。
- 状态栏显示 Provider、文本真实/Mock、图片真实/关闭、图片比例、缓存数量和图片缓存数量。
- 文本真实 AI 可返回可读中文剧情分支，并生成左右选项。
- 图片模型与文本模型分离：文本不使用 `gpt-image-*`；图片使用 `gpt-image-2`。
- 首个未缓存新分支在默认策略下会尝试生成图片。
- 后续未缓存新分支按配置比例触发图片；相同 cacheKey 第二次必须命中缓存，不重复请求文本或图片。
- 文本或图片失败时，剧情仍继续；Console 中只有 warning，不应出现未处理 exception。
- 图片 smoke 和运行时图片均保存到 persistentDataPath，不写入 `Assets`。

## 流程 A：配置窗口与优先级

1. 打开 `AI Builder/AI Settings`。
2. 检查窗口显示：
   - Provider Type。
   - Base URL、Wire API、Text Model、Image Model。
   - API Key 只显示 `Found` 或 `Missing`，不显示值。
   - Enable Runtime Images、Image Generation Ratio、Guarantee First Image。
   - Text Timeout 和 Image Timeout。
3. 点击 `Save Local Config`。
4. 打开 Console，确认出现保存路径日志。
5. 如果 `.env` 中存在同名配置，重启 Unity 或重新运行 Demo 后确认 `.env` 优先生效。

期望结果：

- 配置窗口不泄露 API key。
- 本机 JSON 能保存。
- `.env` 覆盖 JSON 的行为明确可复现。

## 流程 B：真实文本 AI

1. 确认配置：
   - `providerType=openai_compatible`
   - `textModel` 为语言模型，例如 `gpt-5.5`
   - `imageModel=gpt-image-2`
2. 执行菜单 `AI Builder/AI Smoke Test/Text`。
3. Console 应出现：
   - `AI Builder text smoke started`
   - `AI Builder text smoke passed`
4. 进入 Play Mode。
5. 在第一张卡牌选择偏离主线的新分支，例如右滑/右选。
6. 等待生成剧情分支。

期望结果：

- 新分支文本为真实生成内容，不是 Mock 固定句。
- 文本生成完成后，顶部剧情正文和卡牌标题应立刻切换到新分支；如果图片仍在生成，状态显示 `Generating Image...`。
- 左右选项存在，且可以继续推进。
- Console 无 Error。
- 如果真实请求失败，只允许出现明确 warning，并回落 Mock，Demo 不应卡死。

## 流程 C：真实图片 AI 与保存位置

1. 确认配置：
   - `enableRuntimeImages=true`
   - `imageModel=gpt-image-2`
   - `imageTimeoutSeconds>=60`
2. 执行菜单 `AI Builder/AI Smoke Test/Image`。
3. 等待 Console 输出：
   - `AI Builder image smoke started`
   - `AI Builder image smoke passed: <path>`
4. 打开输出路径，确认 PNG 文件存在且文件大小大于 0。
5. 确认输出路径位于 `Application.persistentDataPath/AIBuilder`，不是 `Assets`。

期望结果：

- 图片 smoke 单独执行，不会被文本 smoke 触发。
- 图片成功保存为 PNG。
- 如果当前显示的分支正在等待图片，图片生成成功后卡牌图区域应自动替换为生成图，不需要重进节点。
- 失败时只出现 warning，不影响 Unity Editor。

## 流程 D：30% 图片策略

为了让人工测试可控，先用极端比例验证策略边界，再回到 30%。

### D1：首图保证

1. 设置：
   - `AI_BUILDER_ENABLE_RUNTIME_IMAGES=true`
   - `AI_BUILDER_IMAGE_GENERATION_RATIO=0`
   - `AI_BUILDER_GUARANTEE_FIRST_GENERATED_IMAGE=true`
2. 清空运行时缓存。
3. 进入 Play Mode。
4. 选择一个偏离主线的新分支。

期望结果：

- 即使比例是 0，首个没有成功图片缓存的新分支也会尝试生成图片。
- 成功后状态栏图片缓存数量增加。
- 缓存 entry 的 `imageStatus` 为 `Generated`；失败则为 `Failed`，但剧情仍保存。

### D2：比例为 0 时跳过后续图片

1. 保持 `imageGenerationRatio=0`。
2. 在已有一张成功生成图片缓存后，继续选择另一个新的偏离主线分支。

期望结果：

- 后续分支不再生成图片。
- 缓存 entry 的 `imageStatus` 为 `SkippedByPolicy`。
- 文本分支仍正常保存。

### D3：比例为 1 时必出图

1. 设置 `AI_BUILDER_IMAGE_GENERATION_RATIO=1`。
2. 清空缓存或选择一个未缓存的新分支。
3. 进入 Play Mode 并触发新分支。

期望结果：

- 每个未缓存的新分支都会尝试生成图片。

### D4：默认 30%

1. 设置：
   - `AI_BUILDER_IMAGE_GENERATION_RATIO=0.3`
   - `AI_BUILDER_GUARANTEE_FIRST_GENERATED_IMAGE=true`
2. 清空缓存。
3. 触发首个新分支，记录是否生成图片。
4. 继续触发多个不同的新分支，记录 `Generated` / `SkippedByPolicy`。
5. 对同一个选择重复进入，确认命中缓存。

期望结果：

- 首个新分支按首图保证尝试图片。
- 后续新分支按固定 cacheKey 的确定性策略触发，不是每次随机变。
- 同一个 cacheKey 第二次命中缓存，不重复请求文本或图片。

## 流程 E：缓存复用与审核状态

1. 清空缓存。
2. 触发一个新分支，等待文本生成完成。
3. 记下状态栏 Cache 数量。
4. 回到相同节点，以相同属性段和相同选择再次触发。
5. 打开 `AI Builder/Visual Node Editor`，查看缓存条目。
6. 将缓存状态改为 `Rejected`。
7. 再次触发同一个选择。

期望结果：

- 第二次同选择显示 Cache Hit，不重复请求 AI。
- `Rejected` 缓存不会被复用，会重新生成新分支。
- 缓存详情能显示 `imageStatus`。

## 流程 F：失败回退

### F1：无 API key

1. 临时移除或改名本机 API key 环境变量。
2. 执行 Text smoke。
3. 进入 Play Mode 并触发新分支。

期望结果：

- Text smoke 输出明确 skipped/warning。
- Demo 使用 Mock 文本继续运行。
- 状态栏文本显示 mock。

### F2：关闭运行时图片

1. 设置 `AI_BUILDER_ENABLE_RUNTIME_IMAGES=false`。
2. 保持真实文本配置。
3. 进入 Play Mode 并触发新分支。

期望结果：

- 文本仍走真实 AI。
- 不触发运行时图片生成。
- 缓存 entry 的 `imageStatus` 为 `SkippedByPolicy` 或不可用状态。

### F3：错误 Provider

1. 设置 `AI_BUILDER_PROVIDER_TYPE=bad_provider_for_test`。
2. 重新运行 Demo。

期望结果：

- Console 出现 unknown provider warning。
- 文本和图片降级 Mock。
- Demo 不崩溃。

## 记录模板

每次人工测试至少记录：

```text
测试日期：
Unity 版本：
Provider：
Text Model：
Image Model：
Runtime Images：
Image Ratio：
Guarantee First Image：
Text Smoke：Pass / Fail / Skipped
Image Smoke：Pass / Fail / Skipped
Demo 新分支文本：Pass / Fail
首图保证：Pass / Fail
30% 策略：Pass / Fail
缓存复用：Pass / Fail
失败回退：Pass / Fail
Console Errors：
Console Warnings：
图片输出路径：
备注：
```

## 回归触发条件

以下改动后必须完整跑一遍人工流程：

- 修改 `AiProviderSettings`、Provider 工厂、文本/图片 service。
- 修改 `ImageGenerationPolicy` 或 cache key。
- 修改 Demo 分支生成流程。
- 修改缓存状态、审核、Visual Node Editor。
- 修改 `.env` / JSON 配置字段名或优先级。

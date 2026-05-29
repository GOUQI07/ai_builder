# AI Builder Demo

运行 `Assets/Scenes/SampleScene.unity` 后，Demo 会自动创建横屏舞台和《王权》式卡牌交互界面。

AI 配置不写入仓库。可选方式：

- 设置环境变量 `OPENAI_API_KEY`。
- 设置环境变量 `AI_BUILDER_TEXT_MODEL` 以启用真实文本生成。
- 复制 `Assets/AIBuilder/Data/ai_provider.example.json` 到运行时提示的 `Application.persistentDataPath/AIBuilder/ai_provider.json`，只填写非密钥配置。
- 也可以通过 Unity 菜单 `AI Builder/AI Settings` 编辑本机配置；窗口只显示 API Key 是否存在，不展示密钥内容。

可视化节点编辑器入口：`AI Builder/Visual Node Editor`。
真实 AI 联调入口：`AI Builder/AI Smoke Test/Text` 和 `AI Builder/AI Smoke Test/Image`。

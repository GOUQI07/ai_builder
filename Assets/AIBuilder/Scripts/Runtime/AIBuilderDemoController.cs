using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using UnityEngine.UI;

namespace AIBuilder
{
    public sealed class AIBuilderDemoController : MonoBehaviour
    {
        private const float StageWidth = 660f;
        private const float StageHeight = 1080f;
        private const float TimerDuration = 60f;

        private StoryRepository repository;
        private NodeCacheService cacheService;
        private IAiTextService textService;
        private IAiImageService imageService;
        private AiProviderSettings providerSettings;
        private PlayerStats stats;
        private StoryNode currentNode;

        private Canvas canvas;
        private Font uiFont;
        private RectTransform stage;
        private Text storyText;
        private Text titleText;
        private Text cardCaptionText;
        private Text leftChoiceText;
        private Text rightChoiceText;
        private Text timerText;
        private Text cacheText;
        private Text statusText;
        private Text faithText;
        private Text lifeText;
        private Text forceText;
        private Text wealthText;
        private Image faithFill;
        private Image lifeFill;
        private Image forceFill;
        private Image wealthFill;
        private Image cardImage;
        private Image cardArtImage;
        private Image choiceRevealPanel;
        private Image[] progressSquares;
        private GameObject overlayPanel;
        private Text overlayTitleText;
        private Text overlayBodyText;
        private AIBuilderCardDrag cardDrag;

        private float timer;
        private bool timerActive;
        private bool busy;
        private CancellationTokenSource requestCancellation;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (FindObjectOfType<AIBuilderDemoController>() != null)
            {
                return;
            }

            var runner = new GameObject("AI Builder Demo Runtime");
            DontDestroyOnLoad(runner);
            runner.AddComponent<AIBuilderDemoController>();
        }

        private void Start()
        {
            providerSettings = AiProviderSettings.Load();
            repository = new StoryRepository();
            cacheService = new NodeCacheService();
            textService = new OpenAiCompatibleTextService(providerSettings);
            imageService = new OpenAiCompatibleImageService(providerSettings);
            stats = new PlayerStats();

            EnsureEventSystem();
            BuildUi();
            RestartDemo();
        }

        private void OnDestroy()
        {
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
        }

        private void Update()
        {
            if (!timerActive || busy || currentNode == null)
            {
                return;
            }

            timer -= Time.deltaTime;
            UpdateTimerText();
            if (timer <= 0f)
            {
                timerActive = false;
                SubmitChoice(currentNode.rightChoice ?? currentNode.leftChoice);
            }
        }

        private void RestartDemo()
        {
            requestCancellation?.Cancel();
            requestCancellation = new CancellationTokenSource();
            stats = new PlayerStats();
            busy = false;
            HideOverlay();
            ShowNode(repository.FirstNode(), "Ready");
        }

        private void BuildUi()
        {
            uiFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Source Han Serif SC", "Noto Serif CJK SC", "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "KaiTi", "Arial" },
                32);

            var canvasObject = new GameObject("AI Builder Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = canvasObject.GetComponent<Canvas>();
            var mainCamera = Camera.main;
            if (mainCamera != null)
            {
                mainCamera.orthographic = true;
                mainCamera.orthographicSize = 5.4f;
                mainCamera.transform.position = new Vector3(0f, 0f, -10f);
                mainCamera.clearFlags = CameraClearFlags.SolidColor;
                mainCamera.backgroundColor = Color.black;
            }

            canvas.renderMode = mainCamera == null ? RenderMode.ScreenSpaceOverlay : RenderMode.WorldSpace;
            canvas.worldCamera = mainCamera;
            canvas.sortingOrder = 10;

            if (canvas.renderMode == RenderMode.WorldSpace)
            {
                var canvasRect = canvasObject.GetComponent<RectTransform>();
                canvasRect.sizeDelta = new Vector2(1920f, 1080f);
                canvasObject.transform.position = Vector3.zero;
                canvasObject.transform.localScale = Vector3.one * 0.01f;
            }

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var background = CreateImage("Night Background", canvas.transform, MakeBackgroundSprite(1920, 1080));
            StretchToParent(background.rectTransform);

            stage = CreateRect("Reigns Stage", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(StageWidth, StageHeight), Vector2.zero);
            var stageBody = CreateImage("Stage Body", stage, MakeStageSprite(660, 1080));
            SetRect(stageBody.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(StageWidth, StageHeight), Vector2.zero);

            var topBar = CreateImage("Top Stat Bar", stage, MakeBarSprite(660, 180));
            SetRect(topBar.rectTransform, new Vector2(0.5f, 1f), new Vector2(StageWidth, 180f), new Vector2(0f, -90f));
            CreateStats(topBar.rectTransform);

            titleText = CreateText("Node Title", stage, "", 26, TextAnchor.MiddleCenter, new Color32(58, 38, 20, 255),
                new Vector2(0.5f, 1f), new Vector2(560f, 38f), new Vector2(0f, -214f));
            titleText.fontStyle = FontStyle.Bold;
            storyText = CreateText("Story Text", stage, "", 31, TextAnchor.MiddleCenter, new Color32(37, 28, 20, 255),
                new Vector2(0.5f, 1f), new Vector2(550f, 134f), new Vector2(0f, -294f));
            storyText.lineSpacing = 1.12f;

            var backCard = CreateImage("Choice Back Card", stage, MakeCardBackSprite(512, 640));
            SetRect(backCard.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(500f, 560f), new Vector2(0f, -146f));
            var backMarks = CreateText("Back Card Marks", backCard.transform, "✥\n\n✥\n\n✥", 52, TextAnchor.MiddleLeft, new Color32(163, 146, 91, 200),
                new Vector2(0.5f, 0.5f), new Vector2(420f, 480f), Vector2.zero);
            backMarks.raycastTarget = false;

            var cardShadow = CreateImage("Swipe Card Shadow", stage, MakeCardShadowSprite(560, 640));
            SetRect(cardShadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(548f, 618f), new Vector2(14f, -172f));
            cardShadow.raycastTarget = false;

            cardImage = CreateImage("Swipe Card", stage, MakeCardFrameSprite(512, 640));
            SetRect(cardImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(520f, 590f), new Vector2(0f, -150f));
            cardDrag = cardImage.gameObject.AddComponent<AIBuilderCardDrag>();
            cardDrag.OnDragChanged = UpdateChoiceHints;
            cardDrag.OnSwipeCompleted = direction => SubmitChoice(direction < 0 ? currentNode?.leftChoice : currentNode?.rightChoice);

            cardArtImage = CreateImage("Card Art", cardImage.transform, MakeCardSprite("queen"));
            SetRect(cardArtImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(430f, 390f), new Vector2(0f, 18f));
            cardArtImage.preserveAspect = true;

            cardCaptionText = CreateText("Card Caption", cardImage.transform, "", 25, TextAnchor.MiddleCenter, new Color32(24, 17, 14, 255),
                new Vector2(0.5f, 0f), new Vector2(430f, 58f), new Vector2(0f, 44f));
            cardCaptionText.fontStyle = FontStyle.Bold;

            choiceRevealPanel = CreateImage("Choice Reveal Panel", cardImage.transform, MakeChoiceRevealSprite(512, 124));
            SetRect(choiceRevealPanel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(474f, 118f), new Vector2(0f, 252f));
            choiceRevealPanel.raycastTarget = false;

            leftChoiceText = CreateText("Left Choice Hint", choiceRevealPanel.transform, "", 31, TextAnchor.MiddleCenter, new Color32(255, 238, 205, 255),
                new Vector2(0.5f, 0.5f), new Vector2(446f, 92f), Vector2.zero);
            rightChoiceText = CreateText("Right Choice Hint", choiceRevealPanel.transform, "", 31, TextAnchor.MiddleCenter, new Color32(255, 238, 205, 255),
                new Vector2(0.5f, 0.5f), new Vector2(446f, 92f), Vector2.zero);
            leftChoiceText.fontStyle = FontStyle.Bold;
            rightChoiceText.fontStyle = FontStyle.Bold;
            leftChoiceText.resizeTextForBestFit = true;
            rightChoiceText.resizeTextForBestFit = true;
            leftChoiceText.resizeTextMinSize = 22;
            rightChoiceText.resizeTextMinSize = 22;
            leftChoiceText.resizeTextMaxSize = 31;
            rightChoiceText.resizeTextMaxSize = 31;
            choiceRevealPanel.transform.SetAsLastSibling();

            var bottomBar = CreateImage("Bottom Bar", stage, MakeBarSprite(660, 76));
            SetRect(bottomBar.rectTransform, new Vector2(0.5f, 0f), new Vector2(StageWidth, 76f), new Vector2(0f, 38f));
            progressSquares = new Image[4];
            for (var i = 0; i < progressSquares.Length; i++)
            {
                progressSquares[i] = CreatePanel($"Progress {i}", bottomBar.rectTransform, new Color32(88, 59, 29, 255),
                    new Vector2(0.5f, 0.5f), new Vector2(48f, 48f), new Vector2(-144f + i * 96f, 0f));
            }

            cacheText = CreateText("Cache Status", stage, "", 22, TextAnchor.MiddleCenter, new Color32(255, 236, 166, 255),
                new Vector2(0.5f, 0f), new Vector2(430f, 34f), new Vector2(0f, 118f));
            statusText = CreateText("Provider Status", canvas.transform, "", 20, TextAnchor.MiddleLeft, new Color32(210, 216, 190, 225),
                new Vector2(0f, 0f), new Vector2(620f, 44f), new Vector2(330f, 46f));

            CreateButton("Restart Button", canvas.transform, "重开", new Vector2(1f, 0f), new Vector2(96f, 46f), new Vector2(-112f, 52f), RestartDemo);
            CreateButton("Clear Cache Button", canvas.transform, "清缓存", new Vector2(1f, 0f), new Vector2(118f, 46f), new Vector2(-242f, 52f), () =>
            {
                cacheService.Clear();
                cacheText.text = "Cache Cleared";
            });

            BuildOverlay();
        }

        private void CreateStats(RectTransform parent)
        {
            faithText = CreateStatSlot("Faith", parent, "✝", -228f, out faithFill);
            lifeText = CreateStatSlot("Life", parent, "♟", -76f, out lifeFill);
            forceText = CreateStatSlot("Force", parent, "†", 76f, out forceFill);
            wealthText = CreateStatSlot("Wealth", parent, "$", 228f, out wealthFill);
            timerText = CreateText("Timer", parent, "", 40, TextAnchor.MiddleCenter, new Color32(255, 246, 179, 255),
                new Vector2(1f, 1f), new Vector2(112f, 56f), new Vector2(-66f, -32f));
        }

        private Text CreateStatSlot(string name, Transform parent, string icon, float x, out Image fill)
        {
            var slot = CreateRect($"{name} Slot", parent, new Vector2(0.5f, 0.5f), new Vector2(122f, 150f), new Vector2(x, 0f));
            var dot = CreatePanel($"{name} Dot", slot, new Color32(151, 119, 61, 255), new Vector2(0.5f, 1f), new Vector2(14f, 14f), new Vector2(0f, -18f));
            dot.raycastTarget = false;

            var baseIcon = CreateText($"{name} Base Icon", slot, icon, 64, TextAnchor.MiddleCenter, new Color32(103, 86, 48, 185),
                new Vector2(0.5f, 0.5f), new Vector2(112f, 110f), new Vector2(0f, -8f));
            baseIcon.raycastTarget = false;

            fill = CreatePanel($"{name} Fill", slot, new Color32(236, 213, 136, 220), new Vector2(0.5f, 0.5f), new Vector2(82f, 104f), new Vector2(0f, -8f));
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Vertical;
            fill.fillOrigin = (int)Image.OriginVertical.Bottom;
            fill.fillAmount = 0.7f;
            fill.raycastTarget = false;

            var text = CreateText(name, slot, icon, 64, TextAnchor.MiddleCenter, new Color32(255, 242, 184, 255),
                new Vector2(0.5f, 0.5f), new Vector2(112f, 110f), new Vector2(0f, -8f));
            text.fontStyle = FontStyle.Bold;
            return text;
        }

        private void BuildOverlay()
        {
            overlayPanel = CreatePanel("End Overlay", stage, new Color32(26, 16, 10, 238), new Vector2(0.5f, 0.5f), new Vector2(590f, 430f), new Vector2(0f, -16f)).gameObject;
            overlayTitleText = CreateText("Overlay Title", overlayPanel.transform, "", 42, TextAnchor.MiddleCenter, new Color32(255, 236, 170, 255),
                new Vector2(0.5f, 1f), new Vector2(520f, 80f), new Vector2(0f, -78f));
            overlayBodyText = CreateText("Overlay Body", overlayPanel.transform, "", 28, TextAnchor.MiddleCenter, new Color32(240, 224, 186, 255),
                new Vector2(0.5f, 0.5f), new Vector2(500f, 180f), new Vector2(0f, 10f));
            CreateButton("Overlay Restart", overlayPanel.transform, "重新开始", new Vector2(0.5f, 0f), new Vector2(180f, 54f), new Vector2(0f, 58f), RestartDemo);
            overlayPanel.SetActive(false);
        }

        private void ShowNode(StoryNode node, string status)
        {
            currentNode = node;
            if (currentNode == null)
            {
                ShowOverlay("章节缺失", "没有找到可播放的主干节点。");
                return;
            }

            titleText.text = currentNode.title;
            storyText.text = currentNode.body;
            cardCaptionText.text = currentNode.title;
            leftChoiceText.text = currentNode.leftChoice?.label ?? "";
            rightChoiceText.text = currentNode.rightChoice?.label ?? "";
            cardArtImage.sprite = ResolveSprite(currentNode.imageRef);
            cacheText.text = status;
            statusText.text = BuildProviderStatus();
            UpdateStatsText();
            UpdateProgressSquares();
            UpdateChoiceHints(0f);
            cardDrag.ResetCard();
            cardDrag.SetInteractable(currentNode.nodeKind != StoryNodeKind.Ending && !stats.IsGameOver());

            timer = TimerDuration;
            timerActive = currentNode.nodeKind != StoryNodeKind.Ending && !stats.IsGameOver();
            UpdateTimerText();
        }

        private string BuildProviderStatus()
        {
            var textMode = providerSettings.CanUseText ? providerSettings.textModel : "Mock Text";
            var imageMode = providerSettings.CanUseImage ? providerSettings.imageModel : "Mock Image";
            return $"AI: {textMode} / {imageMode}    Cache: {cacheService.Entries.Count}";
        }

        private void UpdateStatsText()
        {
            UpdateStatSlot(faithText, faithFill, "✝", stats.faith);
            UpdateStatSlot(lifeText, lifeFill, "♟", stats.life);
            UpdateStatSlot(forceText, forceFill, "†", stats.force);
            UpdateStatSlot(wealthText, wealthFill, "$", stats.wealth);
        }

        private static void UpdateStatSlot(Text iconText, Image fill, string icon, int value)
        {
            iconText.text = icon;
            var normalized = Mathf.Clamp01(value / 100f);
            fill.fillAmount = normalized;
            fill.color = value <= 20
                ? new Color32(202, 58, 45, 230)
                : value <= 40
                    ? new Color32(212, 146, 61, 225)
                    : new Color32(237, 214, 136, 225);
            iconText.color = value <= 20
                ? new Color32(255, 196, 142, 255)
                : new Color32(255, 243, 187, 255);
        }

        private void UpdateTimerText()
        {
            timerText.text = Mathf.CeilToInt(Mathf.Max(0f, timer)).ToString("00");
        }

        private void UpdateProgressSquares()
        {
            var activeIndex = Mathf.Clamp(currentNode == null ? 0 : currentNode.mainlineIndex, 0, 3);
            for (var i = 0; i < progressSquares.Length; i++)
            {
                progressSquares[i].color = i < activeIndex
                    ? new Color32(157, 119, 57, 255)
                    : new Color32(88, 59, 29, 255);
            }
        }

        private void UpdateChoiceHints(float progress)
        {
            var strength = Mathf.Clamp01((Mathf.Abs(progress) - 0.08f) / 0.62f);
            var y = Mathf.Lerp(252f, 204f, EaseOutCubic(strength));
            var selectedLeft = progress < 0f;

            SetChoicePanelReveal(choiceRevealPanel, strength, y);
            SetChoiceReveal(leftChoiceText, selectedLeft ? strength : 0f);
            SetChoiceReveal(rightChoiceText, selectedLeft ? 0f : strength);
        }

        private static void SetChoicePanelReveal(Image panel, float alpha, float y)
        {
            var color = panel.color;
            color.a = Mathf.Lerp(0f, 0.94f, alpha);
            panel.color = color;
            panel.rectTransform.anchoredPosition = new Vector2(0f, y);
            panel.gameObject.SetActive(alpha > 0.01f);
        }

        private static void SetChoiceReveal(Text text, float alpha)
        {
            var color = text.color;
            color.a = Mathf.Lerp(0f, 1f, alpha);
            text.color = color;
            text.gameObject.SetActive(alpha > 0.01f);
        }

        private static float EaseOutCubic(float value)
        {
            value = Mathf.Clamp01(value);
            return 1f - Mathf.Pow(1f - value, 3f);
        }

        private async void SubmitChoice(ChoiceOption choice)
        {
            if (choice == null || busy || currentNode == null || overlayPanel.activeSelf)
            {
                cardDrag.ResetCard();
                return;
            }

            busy = true;
            timerActive = false;
            cardDrag.SetInteractable(false);

            try
            {
                await HandleChoiceAsync(choice, requestCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder choice failed safely: {ex.Message}");
                cacheText.text = "Safe Fallback";
                cardDrag.ResetCard();
            }
            finally
            {
                busy = false;
                if (currentNode != null && currentNode.nodeKind != StoryNodeKind.Ending && !stats.IsGameOver())
                {
                    cardDrag.SetInteractable(true);
                    timerActive = true;
                }
            }
        }

        private async Task HandleChoiceAsync(ChoiceOption choice, CancellationToken cancellationToken)
        {
            cacheText.text = "Resolving...";

            var explicitNext = repository.GetById(choice.nextMainlineNodeId);
            if (explicitNext != null)
            {
                stats.Apply(choice.statHint);
                if (TryShowGameOver())
                {
                    return;
                }

                ShowNode(explicitNext, "Mainline");
                return;
            }

            var naturalNext = repository.NextMainlineAfter(currentNode);
            if (naturalNext == null && currentNode.nodeKind == StoryNodeKind.Mainline)
            {
                stats.Apply(choice.statHint);
                if (TryShowGameOver())
                {
                    return;
                }

                ShowChapterComplete(choice);
                return;
            }

            var cacheKey = NodeCacheService.CreateCacheKey(currentNode, choice, stats);
            if (cacheService.TryGet(cacheKey, out var cached))
            {
                stats.Apply(cached.statDelta);
                if (TryShowGameOver())
                {
                    return;
                }

                ShowNode(cached.resultNode, "Cache Hit");
                return;
            }

            var aiResult = await textService.GenerateNextNodeAsync(currentNode, choice, stats.Clone(), cancellationToken);
            var branch = CreateBranchNode(aiResult, choice, naturalNext);
            var imagePath = "";
            if (ShouldGenerateImage(cacheKey))
            {
                cacheText.text = "Generating Image...";
                var bytes = await imageService.GenerateImageAsync(aiResult.imagePrompt, cancellationToken);
                imagePath = cacheService.SaveImage(cacheKey, bytes);
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    branch.imageRef = imagePath;
                }
            }

            var entry = new NodeCacheEntry
            {
                cacheKey = cacheKey,
                sourceNodeId = currentNode.id,
                choiceId = choice.id,
                resultNode = branch,
                statDelta = aiResult.statDelta ?? new PlayerStats(0, 0, 0, 0),
                imagePath = imagePath,
                createdAt = DateTime.UtcNow.ToString("O"),
                status = "Approved"
            };
            cacheService.Put(entry);

            stats.Apply(entry.statDelta);
            if (TryShowGameOver())
            {
                return;
            }

            ShowNode(branch, "Generated");
        }

        private StoryNode CreateBranchNode(AiTextResult aiResult, ChoiceOption sourceChoice, StoryNode nextMainline)
        {
            var nextId = nextMainline == null ? "" : nextMainline.id;
            var safeLeft = string.IsNullOrWhiteSpace(aiResult.leftChoice) ? "追问真相" : aiResult.leftChoice;
            var safeRight = string.IsNullOrWhiteSpace(aiResult.rightChoice) ? "回到王座" : aiResult.rightChoice;

            return new StoryNode
            {
                id = $"branch_{currentNode.id}_{sourceChoice.id}_{Mathf.Abs(DateTime.UtcNow.GetHashCode())}",
                chapterId = currentNode.chapterId,
                title = sourceChoice.label,
                body = string.IsNullOrWhiteSpace(aiResult.storyText) ? "宫廷记录员沉默片刻，将这一页留给后来者补写。" : aiResult.storyText,
                imageRef = "branch",
                nodeKind = nextMainline == null ? StoryNodeKind.Ending : StoryNodeKind.GeneratedBranch,
                mainlineIndex = nextMainline == null ? currentNode.mainlineIndex + 1 : nextMainline.mainlineIndex - 1,
                leftChoice = new ChoiceOption
                {
                    id = "branch_left",
                    label = safeLeft,
                    intent = "继续探索分支后回到主线。",
                    direction = "left",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(0, 0, 0, 0)
                },
                rightChoice = new ChoiceOption
                {
                    id = "branch_right",
                    label = safeRight,
                    intent = "收束分支后回到主线。",
                    direction = "right",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(0, 0, 0, 0)
                }
            };
        }

        private bool ShouldGenerateImage(string cacheKey)
        {
            var generatedImageCount = cacheService.Entries.Count(entry => !string.IsNullOrWhiteSpace(entry.imagePath));
            if (generatedImageCount == 0)
            {
                return true;
            }

            return Mathf.Abs(cacheKey.GetHashCode()) % 100 < 30;
        }

        private bool TryShowGameOver()
        {
            UpdateStatsText();
            if (!stats.IsGameOver())
            {
                return false;
            }

            var reason = stats.life <= 0 ? "生命耗尽" : stats.wealth <= 0 ? "财富归零" : "信仰崩塌";
            ShowOverlay("游戏结束", $"{reason}。灰冠从你手中滑落，宫廷重新陷入黑暗。");
            return true;
        }

        private void ShowChapterComplete(ChoiceOption choice)
        {
            ShowOverlay("第一章完成", $"你选择了「{choice.label}」。灰冠初夜结束，新的主干章节将从这里展开。");
            cacheText.text = "Chapter Complete";
        }

        private void ShowOverlay(string title, string body)
        {
            cardDrag.ResetCard();
            overlayTitleText.text = title;
            overlayBodyText.text = body;
            overlayPanel.SetActive(true);
            timerActive = false;
            cardDrag.SetInteractable(false);
        }

        private void HideOverlay()
        {
            if (overlayPanel != null)
            {
                overlayPanel.SetActive(false);
            }
        }

        private Sprite ResolveSprite(string imageRef)
        {
            if (!string.IsNullOrWhiteSpace(imageRef) && File.Exists(imageRef))
            {
                try
                {
                    var bytes = File.ReadAllBytes(imageRef);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (texture.LoadImage(bytes))
                    {
                        texture.wrapMode = TextureWrapMode.Clamp;
                        return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"AI Builder generated image ignored: {ex.Message}");
                }
            }

            return MakeCardSprite(imageRef);
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            eventSystem.AddComponent<InputSystemUIInputModule>();
#else
            eventSystem.AddComponent<StandaloneInputModule>();
#endif
        }

        private Image CreatePanel(string name, Transform parent, Color32 color, Vector2 anchor, Vector2 size, Vector2 position)
        {
            var image = CreateImage(name, parent, MakeSolidSprite(8, 8, color));
            image.color = color;
            SetRect(image.rectTransform, anchor, size, position);
            return image;
        }

        private Image CreateImage(string name, Transform parent, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Simple;
            return image;
        }

        private Text CreateText(string name, Transform parent, string value, int fontSize, TextAnchor alignment, Color32 color, Vector2 anchor, Vector2 size, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.text = value;
            text.font = uiFont;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            SetRect(go.GetComponent<RectTransform>(), anchor, size, position);
            AddTypographyEffects(text, color);
            return text;
        }

        private static void AddTypographyEffects(Text text, Color32 baseColor)
        {
            var brightness = (baseColor.r + baseColor.g + baseColor.b) / 3f;
            var shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectDistance = new Vector2(1.8f, -1.8f);
            shadow.effectColor = brightness > 150f
                ? new Color32(0, 0, 0, 150)
                : new Color32(255, 235, 175, 70);

            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectDistance = new Vector2(0.75f, -0.75f);
            outline.effectColor = brightness > 150f
                ? new Color32(26, 14, 8, 145)
                : new Color32(255, 236, 186, 45);
        }

        private Button CreateButton(string name, Transform parent, string label, Vector2 anchor, Vector2 size, Vector2 position, Action onClick)
        {
            var image = CreatePanel(name, parent, new Color32(52, 35, 18, 230), anchor, size, position);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick?.Invoke());
            CreateText($"{name} Label", image.transform, label, 22, TextAnchor.MiddleCenter, new Color32(244, 226, 173, 255),
                new Vector2(0.5f, 0.5f), size, Vector2.zero);
            return button;
        }

        private RectTransform CreateRect(string name, Transform parent, Vector2 anchor, Vector2 size, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            SetRect(rect, anchor, size, position);
            return rect;
        }

        private static void SetRect(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void StretchToParent(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Sprite MakeSolidSprite(int width, int height, Color32 color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = EnumerableRepeat(color, width * height);
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeStageSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            var top = new Color32(209, 187, 114, 255);
            var mid = new Color32(190, 160, 82, 255);
            var bottom = new Color32(160, 121, 55, 255);

            for (var y = 0; y < height; y++)
            {
                var t = y / (float)(height - 1);
                var baseColor = t < 0.55f
                    ? LerpColor(bottom, mid, t / 0.55f)
                    : LerpColor(mid, top, (t - 0.55f) / 0.45f);

                for (var x = 0; x < width; x++)
                {
                    var edge = Mathf.Min(x, width - 1 - x) / 80f;
                    var shade = Mathf.Clamp01(edge);
                    var color = LerpColor(new Color32(115, 76, 34, 255), baseColor, shade);
                    if (((x * 17 + y * 31) & 63) == 0)
                    {
                        color = LerpColor(color, new Color32(235, 211, 139, 255), 0.1f);
                    }
                    pixels[y * width + x] = color;
                }
            }

            FillRect(pixels, width, height, 0, 0, width, 4, new Color32(82, 48, 18, 255));
            FillRect(pixels, width, height, 0, height - 4, width, 4, new Color32(235, 202, 117, 255));
            FillRect(pixels, width, height, 18, 0, 2, height, new Color32(124, 83, 37, 160));
            FillRect(pixels, width, height, width - 20, 0, 2, height, new Color32(238, 207, 128, 145));

            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeBarSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var t = y / (float)Mathf.Max(1, height - 1);
                var color = LerpColor(new Color32(33, 17, 4, 255), new Color32(67, 38, 12, 255), t);
                for (var x = 0; x < width; x++)
                {
                    pixels[y * width + x] = color;
                }
            }

            FillRect(pixels, width, height, 0, 0, width, 4, new Color32(30, 14, 3, 255));
            FillRect(pixels, width, height, 0, height - 3, width, 3, new Color32(112, 77, 28, 255));
            FillRect(pixels, width, height, 0, height - 10, width, 2, new Color32(204, 170, 82, 180));
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeCardFrameSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            var radius = 30;
            for (var y = 0; y < height; y++)
            {
                var t = y / (float)(height - 1);
                for (var x = 0; x < width; x++)
                {
                    if (!InsideRoundedRect(x, y, width, height, radius))
                    {
                        pixels[y * width + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    var border = Mathf.Min(Mathf.Min(x, width - 1 - x), Mathf.Min(y, height - 1 - y));
                    var baseColor = LerpColor(new Color32(128, 18, 32, 255), new Color32(202, 39, 54, 255), t);
                    if (border < 12)
                    {
                        baseColor = LerpColor(new Color32(94, 46, 22, 255), new Color32(234, 190, 84, 255), border / 12f);
                    }
                    else if (border < 19)
                    {
                        baseColor = new Color32(92, 34, 24, 255);
                    }

                    if (y > height - 126)
                    {
                        baseColor = LerpColor(baseColor, new Color32(244, 211, 124, 255), 0.72f);
                    }
                    else if (y < 92)
                    {
                        baseColor = LerpColor(baseColor, new Color32(78, 22, 19, 255), 0.45f);
                    }

                    if (x > 40 && x < width - 40 && y > 130 && y < height - 145)
                    {
                        baseColor = LerpColor(baseColor, new Color32(255, 233, 170, 255), 0.09f);
                    }

                    pixels[y * width + x] = baseColor;
                }
            }

            FillRect(pixels, width, height, 36, height - 132, width - 72, 5, new Color32(105, 64, 28, 220));
            FillRect(pixels, width, height, 36, 102, width - 72, 5, new Color32(235, 190, 80, 180));
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeCardBackSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var t = y / (float)Mathf.Max(1, height - 1);
                for (var x = 0; x < width; x++)
                {
                    var edge = Mathf.Min(Mathf.Min(x, width - 1 - x), Mathf.Min(y, height - 1 - y));
                    var color = LerpColor(new Color32(20, 26, 15, 255), new Color32(42, 48, 24, 255), t);
                    if (edge < 10)
                    {
                        color = new Color32(157, 132, 65, 255);
                    }
                    else if (((x + 18) / 54 + (y + 10) / 54) % 2 == 0)
                    {
                        color = LerpColor(color, new Color32(71, 76, 42, 255), 0.26f);
                    }
                    pixels[y * width + x] = color;
                }
            }

            for (var y = 92; y < height - 92; y += 132)
            {
                FillPolygon(pixels, width, height, new[]
                {
                    new Vector2(72, y), new Vector2(92, y + 30), new Vector2(72, y + 60), new Vector2(52, y + 30)
                }, new Color32(191, 165, 91, 210));
                FillPolygon(pixels, width, height, new[]
                {
                    new Vector2(width - 72, y), new Vector2(width - 52, y + 30), new Vector2(width - 72, y + 60), new Vector2(width - 92, y + 30)
                }, new Color32(191, 165, 91, 210));
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeChoiceRevealSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var t = y / (float)Mathf.Max(1, height - 1);
                var alpha = Mathf.Lerp(0.88f, 0.58f, t);
                for (var x = 0; x < width; x++)
                {
                    var edge = Mathf.Min(x, width - 1 - x);
                    var pixelColor = new Color32(18, 11, 8, (byte)Mathf.RoundToInt(alpha * 255f));
                    if (edge < 8)
                    {
                        pixelColor = new Color32(7, 5, 4, (byte)Mathf.RoundToInt(alpha * 255f));
                    }

                    pixels[y * width + x] = pixelColor;
                }
            }

            FillRect(pixels, width, height, 18, 14, width - 36, 2, new Color32(255, 232, 154, 150));
            FillRect(pixels, width, height, 18, height - 18, width - 36, 2, new Color32(0, 0, 0, 115));
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeCardShadowSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            var center = new Vector2(width * 0.5f, height * 0.52f);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var dx = (x - center.x) / (width * 0.5f);
                    var dy = (y - center.y) / (height * 0.5f);
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(1f - d) * 115f);
                    pixels[y * width + x] = new Color32(0, 0, 0, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeBackgroundSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var t = y / (float)(height - 1);
                var sky = LerpColor(new Color32(10, 11, 24, 255), new Color32(44, 53, 112, 255), t);
                if (y < height * 0.48f)
                {
                    sky = LerpColor(new Color32(2, 3, 8, 255), sky, y / (height * 0.48f));
                }

                for (var x = 0; x < width; x++)
                {
                    pixels[y * width + x] = sky;
                }
            }

            FillCircleBlend(pixels, width, height, width - 142, 318, 128, new Color32(246, 245, 226, 60));
            FillCircle(pixels, width, height, width - 112, 316, 78, new Color32(248, 247, 234, 255));

            FillPolygon(pixels, width, height, new[]
            {
                new Vector2(0, height * 0.76f), new Vector2(200, height * 0.62f), new Vector2(470, height * 0.67f),
                new Vector2(690, height * 0.54f), new Vector2(920, height * 0.70f), new Vector2(1160, height * 0.58f),
                new Vector2(1380, height * 0.70f), new Vector2(1620, height * 0.60f), new Vector2(width, height * 0.74f),
                new Vector2(width, height), new Vector2(0, height)
            }, new Color32(26, 36, 83, 255));
            FillPolygon(pixels, width, height, new[]
            {
                new Vector2(0, height * 0.66f), new Vector2(240, height * 0.51f), new Vector2(420, height * 0.56f),
                new Vector2(600, height * 0.43f), new Vector2(820, height * 0.64f), new Vector2(1040, height * 0.48f),
                new Vector2(1320, height * 0.65f), new Vector2(1510, height * 0.50f), new Vector2(width, height * 0.64f),
                new Vector2(width, height), new Vector2(0, height)
            }, new Color32(37, 53, 111, 255));
            FillPolygon(pixels, width, height, new[]
            {
                new Vector2(0, height * 0.54f), new Vector2(180, height * 0.52f), new Vector2(320, height * 0.39f),
                new Vector2(520, height * 0.49f), new Vector2(690, height * 0.34f), new Vector2(850, height * 0.51f),
                new Vector2(1080, height * 0.42f), new Vector2(1280, height * 0.54f), new Vector2(width, height * 0.43f),
                new Vector2(width, height), new Vector2(0, height)
            }, new Color32(31, 42, 95, 255));

            DrawCastle(pixels, width, height, 190, 560, 1);
            DrawCastle(pixels, width, height, width - 330, 555, -1);

            FillPolygon(pixels, width, height, new[]
            {
                new Vector2(0, 0), new Vector2(width, 0), new Vector2(width, height * 0.56f),
                new Vector2(width - 360, height * 0.58f), new Vector2(width - 520, height * 0.46f),
                new Vector2(width - 760, height * 0.48f), new Vector2(width - 910, height * 0.62f),
                new Vector2(910, height * 0.62f), new Vector2(760, height * 0.48f), new Vector2(520, height * 0.46f),
                new Vector2(360, height * 0.58f), new Vector2(0, height * 0.56f)
            }, new Color32(1, 2, 4, 255));

            var starPositions = new[]
            {
                new Vector2(142, 832), new Vector2(260, 912), new Vector2(380, 742), new Vector2(576, 925),
                new Vector2(760, 812), new Vector2(1220, 702), new Vector2(1320, 814), new Vector2(1480, 858),
                new Vector2(1610, 744), new Vector2(1712, 932), new Vector2(1816, 802), new Vector2(1034, 896)
            };
            foreach (var star in starPositions)
            {
                FillRect(pixels, width, height, (int)star.x, (int)star.y, 4, 4, new Color32(238, 240, 255, 255));
                FillRect(pixels, width, height, (int)star.x - 2, (int)star.y + 1, 8, 1, new Color32(238, 240, 255, 150));
                FillRect(pixels, width, height, (int)star.x + 1, (int)star.y - 2, 1, 8, new Color32(238, 240, 255, 150));
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeCardSprite(string key)
        {
            var width = 512;
            var height = 512;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var t = y / (float)(height - 1);
                var color = LerpColor(new Color32(48, 22, 26, 255), new Color32(135, 38, 46, 255), t);
                for (var x = 0; x < width; x++)
                {
                    pixels[y * width + x] = color;
                }
            }

            FillRect(pixels, width, height, 0, 0, width, 28, new Color32(31, 16, 14, 255));
            FillRect(pixels, width, height, 0, height - 28, width, 28, new Color32(232, 193, 91, 255));

            switch ((key ?? "").ToLowerInvariant())
            {
                case "oracle":
                    FillRect(pixels, width, height, 18, 18, width - 36, height - 36, new Color32(41, 20, 18, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(18, 254), new Vector2(160, 110), new Vector2(330, 92), new Vector2(494, 208),
                        new Vector2(494, 494), new Vector2(18, 494)
                    }, new Color32(67, 35, 30, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(116, 130), new Vector2(258, 64), new Vector2(410, 122),
                        new Vector2(450, 286), new Vector2(258, 458), new Vector2(70, 282)
                    }, new Color32(41, 139, 105, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(158, 158), new Vector2(252, 96), new Vector2(356, 152),
                        new Vector2(372, 292), new Vector2(250, 400), new Vector2(138, 292)
                    }, new Color32(105, 198, 150, 165));
                    FillCircle(pixels, width, height, 256, 278, 78, new Color32(151, 196, 145, 220));
                    FillRect(pixels, width, height, 218, 252, 17, 62, new Color32(15, 68, 73, 255));
                    FillRect(pixels, width, height, 287, 252, 17, 62, new Color32(15, 68, 73, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(226, 352), new Vector2(300, 352), new Vector2(324, 424), new Vector2(198, 424)
                    }, new Color32(196, 218, 163, 255));
                    break;
                case "gate":
                    FillRect(pixels, width, height, 18, 18, width - 36, height - 36, new Color32(92, 30, 37, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(18, 288), new Vector2(142, 172), new Vector2(370, 168), new Vector2(494, 292),
                        new Vector2(494, 494), new Vector2(18, 494)
                    }, new Color32(119, 47, 42, 255));
                    FillRect(pixels, width, height, 74, 148, 364, 226, new Color32(207, 166, 55, 255));
                    FillRect(pixels, width, height, 110, 174, 292, 194, new Color32(232, 189, 70, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(74, 374), new Vector2(256, 468), new Vector2(438, 374)
                    }, new Color32(255, 220, 90, 255));
                    FillRect(pixels, width, height, 206, 148, 100, 168, new Color32(70, 42, 31, 255));
                    FillRect(pixels, width, height, 126, 328, 34, 70, new Color32(255, 226, 90, 255));
                    FillRect(pixels, width, height, 352, 328, 34, 70, new Color32(255, 226, 90, 255));
                    FillRect(pixels, width, height, 82, 138, 348, 12, new Color32(98, 56, 28, 210));
                    break;
                case "branch":
                case "generated":
                    FillRect(pixels, width, height, 18, 18, width - 36, height - 36, new Color32(52, 22, 27, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(58, 124), new Vector2(230, 58), new Vector2(430, 146),
                        new Vector2(394, 416), new Vector2(144, 456)
                    }, new Color32(48, 125, 102, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(114, 154), new Vector2(300, 92), new Vector2(400, 202), new Vector2(324, 360), new Vector2(118, 330)
                    }, new Color32(84, 171, 130, 160));
                    FillRect(pixels, width, height, 126, 134, 258, 132, new Color32(91, 170, 130, 125));
                    FillCircle(pixels, width, height, 260, 284, 72, new Color32(177, 207, 151, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(180, 206), new Vector2(254, 152), new Vector2(334, 206), new Vector2(308, 252), new Vector2(206, 252)
                    }, new Color32(137, 185, 139, 255));
                    FillRect(pixels, width, height, 216, 270, 17, 52, new Color32(28, 85, 82, 255));
                    FillRect(pixels, width, height, 287, 270, 17, 52, new Color32(28, 85, 82, 255));
                    FillRect(pixels, width, height, 206, 372, 106, 48, new Color32(177, 207, 151, 255));
                    break;
                default:
                    FillRect(pixels, width, height, 18, 18, width - 36, height - 36, new Color32(181, 30, 48, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(18, 300), new Vector2(128, 154), new Vector2(388, 154), new Vector2(494, 302), new Vector2(494, 494), new Vector2(18, 494)
                    }, new Color32(202, 42, 55, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(104, 124), new Vector2(190, 70), new Vector2(322, 70),
                        new Vector2(408, 124), new Vector2(362, 368), new Vector2(258, 450),
                        new Vector2(150, 368)
                    }, new Color32(255, 207, 72, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(150, 154), new Vector2(256, 86), new Vector2(362, 154), new Vector2(338, 350), new Vector2(256, 400), new Vector2(174, 350)
                    }, new Color32(255, 222, 92, 185));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(168, 218), new Vector2(256, 160), new Vector2(344, 218),
                        new Vector2(328, 334), new Vector2(256, 370), new Vector2(184, 334)
                    }, new Color32(249, 210, 169, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(172, 376), new Vector2(340, 376), new Vector2(300, 448), new Vector2(212, 448)
                    }, new Color32(248, 228, 199, 255));
                    FillRect(pixels, width, height, 214, 266, 14, 58, new Color32(18, 19, 20, 255));
                    FillRect(pixels, width, height, 286, 266, 14, 58, new Color32(18, 19, 20, 255));
                    FillRect(pixels, width, height, 166, 92, 180, 30, new Color32(255, 230, 55, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(178, 122), new Vector2(200, 188), new Vector2(222, 122)
                    }, new Color32(255, 238, 78, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(236, 122), new Vector2(256, 206), new Vector2(276, 122)
                    }, new Color32(255, 238, 78, 255));
                    FillPolygon(pixels, width, height, new[]
                    {
                        new Vector2(292, 122), new Vector2(314, 188), new Vector2(336, 122)
                    }, new Color32(255, 238, 78, 255));
                    FillRect(pixels, width, height, 178, 422, 156, 52, new Color32(251, 78, 151, 255));
                    break;
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Color32[] EnumerableRepeat(Color32 color, int count)
        {
            var pixels = new Color32[count];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            return pixels;
        }

        private static Color32 LerpColor(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(a.r, b.r, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(a.g, b.g, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(a.b, b.b, t)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(a.a, b.a, t)));
        }

        private static bool InsideRoundedRect(int x, int y, int width, int height, int radius)
        {
            var cx = x < radius ? radius : x > width - radius - 1 ? width - radius - 1 : x;
            var cy = y < radius ? radius : y > height - radius - 1 ? height - radius - 1 : y;
            var dx = x - cx;
            var dy = y - cy;
            return dx * dx + dy * dy <= radius * radius;
        }

        private static void DrawCastle(Color32[] pixels, int width, int height, int x, int y, int direction)
        {
            var color = new Color32(8, 10, 22, 230);
            FillRect(pixels, width, height, x, y, 170, 150, color);
            FillRect(pixels, width, height, x + direction * 132, y - 24, 76, 190, color);
            FillRect(pixels, width, height, x + direction * 44, y - 70, 66, 232, color);
            FillPolygon(pixels, width, height, new[]
            {
                new Vector2(x + direction * 44, y + 162),
                new Vector2(x + direction * 77, y + 234),
                new Vector2(x + direction * 110, y + 162)
            }, color);
            FillPolygon(pixels, width, height, new[]
            {
                new Vector2(x + direction * 132, y + 166),
                new Vector2(x + direction * 170, y + 224),
                new Vector2(x + direction * 208, y + 166)
            }, color);
            FillRect(pixels, width, height, x + direction * 62, y + 16, 18, 46, new Color32(234, 193, 88, 180));
            FillRect(pixels, width, height, x + direction * 146, y + 44, 16, 36, new Color32(234, 193, 88, 140));
        }

        private static void FillRect(Color32[] pixels, int width, int height, int x, int y, int rectWidth, int rectHeight, Color32 color)
        {
            var xMin = Mathf.Clamp(x, 0, width - 1);
            var yMin = Mathf.Clamp(y, 0, height - 1);
            var xMax = Mathf.Clamp(x + rectWidth, 0, width);
            var yMax = Mathf.Clamp(y + rectHeight, 0, height);
            for (var py = yMin; py < yMax; py++)
            {
                for (var px = xMin; px < xMax; px++)
                {
                    pixels[py * width + px] = color;
                }
            }
        }

        private static void FillCircleBlend(Color32[] pixels, int width, int height, int centerX, int centerY, int radius, Color32 color)
        {
            var radiusSquared = radius * radius;
            for (var y = Mathf.Max(0, centerY - radius); y < Mathf.Min(height, centerY + radius); y++)
            {
                for (var x = Mathf.Max(0, centerX - radius); x < Mathf.Min(width, centerX + radius); x++)
                {
                    var dx = x - centerX;
                    var dy = y - centerY;
                    if (dx * dx + dy * dy <= radiusSquared)
                    {
                        BlendPixel(pixels, width, x, y, color);
                    }
                }
            }
        }

        private static void BlendPixel(Color32[] pixels, int width, int x, int y, Color32 overlay)
        {
            var index = y * width + x;
            var baseColor = pixels[index];
            var alpha = overlay.a / 255f;
            pixels[index] = new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(baseColor.r, overlay.r, alpha)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(baseColor.g, overlay.g, alpha)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(baseColor.b, overlay.b, alpha)),
                255);
        }

        private static void FillCircle(Color32[] pixels, int width, int height, int centerX, int centerY, int radius, Color32 color)
        {
            var radiusSquared = radius * radius;
            for (var y = Mathf.Max(0, centerY - radius); y < Mathf.Min(height, centerY + radius); y++)
            {
                for (var x = Mathf.Max(0, centerX - radius); x < Mathf.Min(width, centerX + radius); x++)
                {
                    var dx = x - centerX;
                    var dy = y - centerY;
                    if (dx * dx + dy * dy <= radiusSquared)
                    {
                        pixels[y * width + x] = color;
                    }
                }
            }
        }

        private static void FillPolygon(Color32[] pixels, int width, int height, IReadOnlyList<Vector2> points, Color32 color)
        {
            var xMin = width;
            var xMax = 0;
            var yMin = height;
            var yMax = 0;
            foreach (var point in points)
            {
                xMin = Mathf.Min(xMin, Mathf.FloorToInt(point.x));
                xMax = Mathf.Max(xMax, Mathf.CeilToInt(point.x));
                yMin = Mathf.Min(yMin, Mathf.FloorToInt(point.y));
                yMax = Mathf.Max(yMax, Mathf.CeilToInt(point.y));
            }

            xMin = Mathf.Clamp(xMin, 0, width - 1);
            xMax = Mathf.Clamp(xMax, 0, width - 1);
            yMin = Mathf.Clamp(yMin, 0, height - 1);
            yMax = Mathf.Clamp(yMax, 0, height - 1);

            for (var y = yMin; y <= yMax; y++)
            {
                for (var x = xMin; x <= xMax; x++)
                {
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), points))
                    {
                        pixels[y * width + x] = color;
                    }
                }
            }
        }

        private static bool PointInPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                if (((pi.y > point.y) != (pj.y > point.y))
                    && (point.x < (pj.x - pi.x) * (point.y - pi.y) / Mathf.Max(0.001f, pj.y - pi.y) + pi.x))
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}

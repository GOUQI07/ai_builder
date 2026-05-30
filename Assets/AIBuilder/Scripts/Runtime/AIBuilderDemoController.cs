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
        private const int PredictionLookaheadDepth = 2;
        private const int RuntimeTextPredictionConcurrency = 2;
        private const int RuntimeImageConcurrency = 4;
        private const int PanoramaBackfillWindow = 18;
        private const float BranchPredictionDragThreshold = 0.42f;
        private const int BranchStreamingFirstTextWindowMs = 900;
        private const int StatImpactMinInterval = 2;
        private const int StatImpactMaxInterval = 3;
        private const int ImmediateStatDeltaLimit = 4;
        private const int BranchStatDeltaLimit = 5;
        private const int RecentStatEventLimit = 8;
        private const int StoryBodyMinFontSize = 20;
        private const int StoryBodyMaxFontSize = 34;
        private const float StatImpactDotBaseSize = 14f;
        private const float StatImpactDotMaxSize = 34f;
        private const string LifeIcon = "♥";
        private const string ForceIcon = "⚔";
        private const string WealthIcon = "$";
        private const string FaithIcon = "✝";

        private StoryRepository repository;
        private NodeCacheService cacheService;
        private IAiTextService textService;
        private IAiImageService imageService;
        private CharacterPortraitService portraitService;
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
        private Image faithIconImage;
        private Image lifeIconImage;
        private Image forceIconImage;
        private Image wealthIconImage;
        private Image backgroundImage;
        private Image faithFill;
        private Image lifeFill;
        private Image forceFill;
        private Image wealthFill;
        private Image faithImpactDot;
        private Image lifeImpactDot;
        private Image forceImpactDot;
        private Image wealthImpactDot;
        private Image cardImage;
        private Image cardArtImage;
        private Image choiceRevealPanel;
        private Image[] progressSquares;
        private GameObject overlayPanel;
        private Text overlayTitleText;
        private Text overlayBodyText;
        private AIBuilderCardDrag cardDrag;
        private Sprite defaultBackgroundSprite;
        private Material cardFrameMaterial;
        private Material cardArtMaterial;
        private Material cardBackMaterial;
        private Material choiceRevealMaterial;
        private Material backgroundAtmosphereMaterial;

        private float timer;
        private bool timerActive;
        private bool busy;
        private CancellationTokenSource requestCancellation;
        private CancellationTokenSource backgroundCancellation;
        private readonly SemaphoreSlim textPredictionLimiter =
            new SemaphoreSlim(RuntimeTextPredictionConcurrency, RuntimeTextPredictionConcurrency);
        private readonly Dictionary<string, Task<NodeCacheEntry>> textPredictionTasks =
            new Dictionary<string, Task<NodeCacheEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<StatJudgementResult>> statJudgementTasks =
            new Dictionary<string, Task<StatJudgementResult>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> streamingStoryTextByKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action<string>>> streamingStoryTextListeners =
            new Dictionary<string, List<Action<string>>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ImageGenerationJob> imageJobs = new List<ImageGenerationJob>();
        private readonly HashSet<string> queuedImageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> runningImageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool imagePumpRunning;
        private int runningImageJobs;
        private int imageJobSequence;
        private string activeStreamingCacheKey = "";
        private NodeCacheEntry currentNodeCacheEntry;
        private int choiceCount;
        private int choicesSinceStatImpact;
        private int nextStatImpactInterval = StatImpactMinInterval;
        private string lastStatFeedback = "";
        private readonly List<string> recentStatEvents = new List<string>();

        private sealed class ImageGenerationJob
        {
            public NodeCacheEntry entry;
            public string imageCacheKey;
            public AiImagePurpose purpose;
            public int priority;
            public int sequence;
            public string reason;
        }

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
            cacheService = new NodeCacheService(NodeCacheStoreFactory.Create(providerSettings));
            textService = AiProviderFactory.CreateTextService(providerSettings);
            imageService = AiProviderFactory.CreateImageService(providerSettings);
            portraitService = new CharacterPortraitService();
            backgroundCancellation = new CancellationTokenSource();
            stats = new PlayerStats();

            EnsureEventSystem();
            BuildUi();
            RestartDemo();
        }

        private void OnDestroy()
        {
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            backgroundCancellation?.Cancel();
            backgroundCancellation?.Dispose();
            textPredictionLimiter?.Dispose();
            DestroyRuntimeMaterial(ref cardFrameMaterial);
            DestroyRuntimeMaterial(ref cardArtMaterial);
            DestroyRuntimeMaterial(ref cardBackMaterial);
            DestroyRuntimeMaterial(ref choiceRevealMaterial);
            DestroyRuntimeMaterial(ref backgroundAtmosphereMaterial);
        }

        private void CreateVisualMaterials()
        {
            backgroundAtmosphereMaterial = CreateRuntimeMaterial(
                "Shaders/AIBuilderBackgroundAtmosphere",
                "AIBuilder/UI/BackgroundAtmosphere",
                "AI Builder Background Atmosphere");
            if (backgroundAtmosphereMaterial != null)
            {
                backgroundAtmosphereMaterial.SetFloat("_Vignette", 0.46f);
                backgroundAtmosphereMaterial.SetFloat("_MistStrength", 0.2f);
                backgroundAtmosphereMaterial.SetFloat("_GlowStrength", 0.16f);
                backgroundAtmosphereMaterial.SetFloat("_Drift", 0.18f);
                backgroundAtmosphereMaterial.SetColor("_MistColor", new Color(0.38f, 0.58f, 0.62f, 1f));
                backgroundAtmosphereMaterial.SetColor("_CenterGlowColor", new Color(0.88f, 0.58f, 0.25f, 1f));
            }

            cardFrameMaterial = CreateRuntimeMaterial(
                "Shaders/AIBuilderCardDepth",
                "AIBuilder/UI/CardDepth",
                "AI Builder Card Frame Depth");
            ConfigureCardMaterial(cardFrameMaterial, 0.064f, 0.82f, 0.42f, 0.24f, 0.22f, 0.2f,
                new Color(1f, 0.76f, 0.28f, 1f),
                new Color(0.12f, 0.03f, 0.018f, 1f),
                new Color(1f, 0.9f, 0.6f, 1f));

            cardArtMaterial = CreateRuntimeMaterial(
                "Shaders/AIBuilderCardDepth",
                "AIBuilder/UI/CardDepth",
                "AI Builder Card Art Depth");
            ConfigureCardMaterial(cardArtMaterial, 0.034f, 0.42f, 0.22f, 0.08f, 0.1f, 0.14f,
                new Color(0.88f, 0.68f, 0.34f, 1f),
                new Color(0.05f, 0.02f, 0.018f, 1f),
                new Color(1f, 0.9f, 0.66f, 1f));

            cardBackMaterial = CreateRuntimeMaterial(
                "Shaders/AIBuilderCardDepth",
                "AIBuilder/UI/CardDepth",
                "AI Builder Back Card Depth");
            ConfigureCardMaterial(cardBackMaterial, 0.052f, 0.58f, 0.34f, 0.16f, 0.14f, 0.22f,
                new Color(0.85f, 0.72f, 0.34f, 1f),
                new Color(0.02f, 0.04f, 0.025f, 1f),
                new Color(0.95f, 0.88f, 0.58f, 1f));

            choiceRevealMaterial = CreateRuntimeMaterial(
                "Shaders/AIBuilderCardDepth",
                "AIBuilder/UI/CardDepth",
                "AI Builder Choice Reveal Depth");
            ConfigureCardMaterial(choiceRevealMaterial, 0.025f, 0.24f, 0.12f, 0.06f, 0.06f, 0.08f,
                new Color(1f, 0.82f, 0.38f, 1f),
                new Color(0f, 0f, 0f, 1f),
                new Color(1f, 0.9f, 0.68f, 1f));
        }

        private static Material CreateRuntimeMaterial(string resourcePath, string shaderName, string materialName)
        {
            var shader = Resources.Load<Shader>(resourcePath);
            if (shader == null)
            {
                shader = Shader.Find(shaderName);
            }

            if (shader == null)
            {
                Debug.LogWarning($"AI Builder shader '{shaderName}' was not found.");
                return null;
            }

            return new Material(shader)
            {
                name = materialName,
                hideFlags = HideFlags.DontSave
            };
        }

        private static void ConfigureCardMaterial(
            Material material,
            float bevelWidth,
            float depth,
            float grain,
            float foilStrength,
            float innerGlow,
            float vignette,
            Color foilColor,
            Color shadowColor,
            Color highlightColor)
        {
            if (material == null)
            {
                return;
            }

            material.SetFloat("_BevelWidth", bevelWidth);
            material.SetFloat("_Depth", depth);
            material.SetFloat("_Grain", grain);
            material.SetFloat("_FoilStrength", foilStrength);
            material.SetFloat("_InnerGlow", innerGlow);
            material.SetFloat("_Vignette", vignette);
            material.SetColor("_FoilColor", foilColor);
            material.SetColor("_ShadowColor", shadowColor);
            material.SetColor("_HighlightColor", highlightColor);
        }

        private static void DestroyRuntimeMaterial(ref Material material)
        {
            if (material == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(material);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(material);
            }

            material = null;
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
            choiceCount = 0;
            choicesSinceStatImpact = 0;
            nextStatImpactInterval = StatImpactMinInterval;
            lastStatFeedback = "";
            recentStatEvents.Clear();
            busy = false;
            HideOverlay();
            ShowNode(repository.FirstNode(), "Ready");
        }

        private void ResetBackgroundWork()
        {
            backgroundCancellation?.Cancel();
            backgroundCancellation?.Dispose();
            backgroundCancellation = new CancellationTokenSource();
            textPredictionTasks.Clear();
            statJudgementTasks.Clear();
            streamingStoryTextByKey.Clear();
            streamingStoryTextListeners.Clear();
            imageJobs.Clear();
            queuedImageKeys.Clear();
            runningImageKeys.Clear();
            runningImageJobs = 0;
            imagePumpRunning = false;
        }

        private void BuildUi()
        {
            uiFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Source Han Sans SC", "SimHei", "Source Han Serif SC", "Noto Serif CJK SC", "Arial" },
                32);
            CreateVisualMaterials();

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

            defaultBackgroundSprite = ResolveDefaultBackgroundSprite();
            backgroundImage = CreateImage("Immersive Background", canvas.transform, defaultBackgroundSprite);
            ApplyMaterial(backgroundImage, backgroundAtmosphereMaterial);
            StretchToParent(backgroundImage.rectTransform);

            stage = CreateRect("Reigns Stage", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(StageWidth, StageHeight), Vector2.zero);
            var stageBody = CreateImage("Stage Body", stage, MakeStageSprite(660, 1080));
            SetRect(stageBody.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(StageWidth, StageHeight), Vector2.zero);

            var topBar = CreateImage("Top Stat Bar", stage, MakeBarSprite(660, 180));
            SetRect(topBar.rectTransform, new Vector2(0.5f, 1f), new Vector2(StageWidth, 180f), new Vector2(0f, -90f));
            CreateStats(topBar.rectTransform);

            titleText = CreateText("Node Title", stage, "", 28, TextAnchor.MiddleCenter, new Color32(48, 31, 18, 255),
                new Vector2(0.5f, 1f), new Vector2(560f, 42f), new Vector2(0f, -206f));
            titleText.fontStyle = FontStyle.Bold;
            titleText.resizeTextForBestFit = true;
            titleText.resizeTextMinSize = 20;
            titleText.resizeTextMaxSize = 28;
            titleText.verticalOverflow = VerticalWrapMode.Truncate;
            RemoveTypographyEffects(titleText);
            storyText = CreateText("Story Text", stage, "", StoryBodyMaxFontSize, TextAnchor.MiddleCenter, new Color32(29, 23, 17, 255),
                new Vector2(0.5f, 1f), new Vector2(562f, 168f), new Vector2(0f, -320f));
            storyText.lineSpacing = 1.06f;
            storyText.resizeTextForBestFit = true;
            storyText.resizeTextMinSize = StoryBodyMinFontSize;
            storyText.resizeTextMaxSize = StoryBodyMaxFontSize;
            storyText.verticalOverflow = VerticalWrapMode.Truncate;
            RemoveTypographyEffects(storyText);
            titleText.transform.SetAsLastSibling();

            var backCard = CreateImage("Choice Back Card", stage, MakeCardBackSprite(512, 640));
            ApplyMaterial(backCard, cardBackMaterial);
            SetRect(backCard.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(500f, 560f), new Vector2(0f, -146f));
            var backMarks = CreateText("Back Card Marks", backCard.transform, "✥\n\n✥\n\n✥", 52, TextAnchor.MiddleLeft, new Color32(163, 146, 91, 200),
                new Vector2(0.5f, 0.5f), new Vector2(420f, 480f), Vector2.zero);
            backMarks.raycastTarget = false;

            var cardShadow = CreateImage("Swipe Card Shadow", stage, MakeCardShadowSprite(560, 640));
            SetRect(cardShadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(548f, 618f), new Vector2(14f, -172f));
            cardShadow.raycastTarget = false;

            cardImage = CreateImage("Swipe Card", stage, MakeCardFrameSprite(512, 640));
            ApplyMaterial(cardImage, cardFrameMaterial);
            SetRect(cardImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(520f, 590f), new Vector2(0f, -150f));
            cardDrag = cardImage.gameObject.AddComponent<AIBuilderCardDrag>();
            cardDrag.OnDragChanged = UpdateChoiceHints;
            cardDrag.OnSwipeCompleted = direction => SubmitChoice(direction < 0 ? currentNode?.leftChoice : currentNode?.rightChoice);

            cardArtImage = CreateImage("Card Art", cardImage.transform, MakeCardSprite("queen"));
            ApplyMaterial(cardArtImage, cardArtMaterial);
            SetRect(cardArtImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(430f, 390f), new Vector2(0f, 18f));
            cardArtImage.preserveAspect = true;

            cardCaptionText = CreateText("Card Caption", cardImage.transform, "", 25, TextAnchor.MiddleCenter, new Color32(24, 17, 14, 255),
                new Vector2(0.5f, 0f), new Vector2(430f, 58f), new Vector2(0f, 44f));
            cardCaptionText.fontStyle = FontStyle.Bold;

            choiceRevealPanel = CreateImage("Choice Reveal Panel", cardImage.transform, MakeChoiceRevealSprite(512, 124));
            ApplyMaterial(choiceRevealPanel, choiceRevealMaterial);
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

            cacheText = CreateText("Cache Status", stage, "", 18, TextAnchor.MiddleCenter, new Color32(255, 236, 166, 255),
                new Vector2(0.5f, 0f), new Vector2(460f, 28f), new Vector2(0f, 88f));
            cacheText.resizeTextForBestFit = true;
            cacheText.resizeTextMinSize = 13;
            cacheText.resizeTextMaxSize = 18;
            cacheText.verticalOverflow = VerticalWrapMode.Truncate;
            statusText = CreateText("Provider Status", canvas.transform, "", 20, TextAnchor.MiddleLeft, new Color32(210, 216, 190, 225),
                new Vector2(0f, 0f), new Vector2(620f, 44f), new Vector2(330f, 46f));

            CreateButton("Restart Button", canvas.transform, "重开", new Vector2(1f, 0f), new Vector2(96f, 46f), new Vector2(-112f, 52f), RestartDemo);
            CreateButton("Clear Cache Button", canvas.transform, "清缓存", new Vector2(1f, 0f), new Vector2(118f, 46f), new Vector2(-242f, 52f), () =>
            {
                ResetBackgroundWork();
                cacheService.Clear();
                cacheText.text = "Cache Cleared";
                statusText.text = BuildProviderStatus();
            });

            BuildOverlay();
        }

        private void CreateStats(RectTransform parent)
        {
            lifeIconImage = CreateStatSlot("Life", parent, LifeIcon, -228f, out lifeFill, out lifeImpactDot);
            forceIconImage = CreateStatSlot("Force", parent, ForceIcon, -76f, out forceFill, out forceImpactDot);
            wealthIconImage = CreateStatSlot("Wealth", parent, WealthIcon, 76f, out wealthFill, out wealthImpactDot);
            faithIconImage = CreateStatSlot("Faith", parent, FaithIcon, 228f, out faithFill, out faithImpactDot);
            timerText = CreateText("Timer", parent, "", 40, TextAnchor.MiddleCenter, new Color32(255, 246, 179, 255),
                new Vector2(1f, 1f), new Vector2(112f, 56f), new Vector2(-66f, -32f));
        }

        private Image CreateStatSlot(string name, Transform parent, string icon, float x, out Image fill, out Image impactDot)
        {
            var slot = CreateRect($"{name} Slot", parent, new Vector2(0.5f, 0.5f), new Vector2(122f, 150f), new Vector2(x, 0f));
            impactDot = CreatePanel($"{name} Impact Dot", slot, new Color32(151, 119, 61, 255), new Vector2(0.5f, 1f), new Vector2(StatImpactDotBaseSize, StatImpactDotBaseSize), new Vector2(0f, -18f));
            impactDot.raycastTarget = false;

            var baseIcon = CreateImage($"{name} Base Icon", slot, AIBuilderDemoController.MakeStatIconSprite(icon));
            baseIcon.color = new Color32(103, 86, 48, 155);
            SetRect(baseIcon.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(76f, 76f), new Vector2(0f, -8f));
            baseIcon.raycastTarget = false;

            fill = CreatePanel($"{name} Fill", slot, new Color32(236, 213, 136, 220), new Vector2(0.5f, 0.5f), new Vector2(82f, 104f), new Vector2(0f, -8f));
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Vertical;
            fill.fillOrigin = (int)Image.OriginVertical.Bottom;
            fill.fillAmount = 0.7f;
            fill.raycastTarget = false;

            var iconImage = CreateImage(name, slot, AIBuilderDemoController.MakeStatIconSprite(icon));
            iconImage.color = new Color32(255, 242, 184, 255);
            SetRect(iconImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(76f, 76f), new Vector2(0f, -8f));
            iconImage.raycastTarget = false;
            return iconImage;
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

        private void ShowNode(StoryNode node, string status, NodeCacheEntry cacheEntry = null)
        {
            activeStreamingCacheKey = "";
            currentNode = node;
            currentNodeCacheEntry = cacheEntry;
            if (currentNode == null)
            {
                ShowOverlay("章节缺失", "没有找到可播放的主干节点。");
                return;
            }

            titleText.text = currentNode.title;
            SetStoryBodyText(currentNode.body);
            cardCaptionText.text = currentNode.title;
            leftChoiceText.text = currentNode.leftChoice?.label ?? "";
            rightChoiceText.text = currentNode.rightChoice?.label ?? "";
            cardArtImage.sprite = ResolveNodeSprite(currentNode);
            ApplyPanoramaBackground(cacheEntry);
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
            SchedulePredictionsFromNode(currentNode, stats.Clone(), PredictionLookaheadDepth, 70, "node_shown");
            ScheduleStatJudgementPredictionsFromNode(currentNode, stats.Clone(), "node_shown");
            ScheduleIdleImageBackfill();
        }

        private void ShowStreamingBranchStart(StoryNode sourceNode, ChoiceOption choice, string cacheKey)
        {
            activeStreamingCacheKey = cacheKey;
            currentNodeCacheEntry = null;
            currentNode = new StoryNode
            {
                id = $"streaming_{Mathf.Abs(cacheKey.GetHashCode())}",
                chapterId = sourceNode?.chapterId,
                title = choice?.label ?? "",
                body = "",
                imageRef = "branch",
                nodeKind = StoryNodeKind.GeneratedBranch,
                mainlineIndex = sourceNode == null ? 0 : sourceNode.mainlineIndex
            };

            titleText.text = currentNode.title;
            SetStoryBodyText("");
            cardCaptionText.text = currentNode.title;
            leftChoiceText.text = "";
            rightChoiceText.text = "";
            cardArtImage.sprite = ResolveNodeSprite(currentNode);
            ApplyPanoramaBackground(null);
            cacheText.text = "Streaming Text";
            statusText.text = BuildProviderStatus();
            UpdateStatsText();
            UpdateProgressSquares();
            UpdateChoiceHints(0f);
            cardDrag.ResetCard();
            cardDrag.SetInteractable(false);
            timerActive = false;
            UpdateTimerText();
        }

        private void UpdateStreamingBranchText(string cacheKey, string text)
        {
            if (!string.Equals(activeStreamingCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SetStoryBodyText(text);
            cacheText.text = "Streaming Text";
            statusText.text = BuildProviderStatus();
        }

        private void SetStoryBodyText(string value)
        {
            var body = value ?? "";
            storyText.resizeTextMaxSize = ResolveStoryBodyMaxFontSize(body);
            storyText.fontSize = storyText.resizeTextMaxSize;
            storyText.text = body;
        }

        private static int ResolveStoryBodyMaxFontSize(string value)
        {
            var visibleCharacters = (value ?? "").Count(character => !char.IsWhiteSpace(character));
            if (visibleCharacters <= 60)
            {
                return StoryBodyMaxFontSize;
            }

            if (visibleCharacters <= 96)
            {
                return 32;
            }

            if (visibleCharacters <= 136)
            {
                return 29;
            }

            if (visibleCharacters <= 184)
            {
                return 25;
            }

            return 22;
        }

        private string BuildProviderStatus()
        {
            var textMode = providerSettings.CanUseText ? "real" : "mock";
            var imageMode = providerSettings.CanUseImage
                ? providerSettings.enableRuntimeImages
                    ? $"real {Mathf.RoundToInt(ImageGenerationPolicy.ClampRatio(providerSettings.imageGenerationRatio) * 100f)}%"
                    : "real off"
                : "mock off";
            var panoramaMode = providerSettings.CanUseImage
                ? providerSettings.enableRuntimePanoramas
                    ? $"{Mathf.RoundToInt(ImageGenerationPolicy.ClampRatio(providerSettings.panoramaGenerationRatio) * 100f)}%"
                    : "off"
                : "off";
            var storyEntries = CurrentStoryCacheEntries();
            var approved = storyEntries.Count(entry => string.Equals(NodeCacheStatuses.Normalize(entry.status), NodeCacheStatuses.Approved, StringComparison.OrdinalIgnoreCase));
            var pending = storyEntries.Count(entry => string.Equals(NodeCacheStatuses.Normalize(entry.status), NodeCacheStatuses.PendingReview, StringComparison.OrdinalIgnoreCase));
            var rejected = storyEntries.Count(entry => NodeCacheStatuses.IsRejected(entry.status));
            var images = storyEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.imagePath));
            var panoramas = storyEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.panoramaPath));
            return $"AI {providerSettings.NormalizedProviderType} T:{textMode} I:{imageMode} P:{panoramaMode} Story:{ShortStoryId()} Cache:{storyEntries.Count}/{cacheService.Entries.Count} Img:{images}/{panoramas} Q:{queuedImageKeys.Count}/R:{runningImageKeys.Count} ({approved}A/{pending}P/{rejected}R)";
        }

        private void UpdateStatsText()
        {
            UpdateStatSlot(lifeIconImage, lifeFill, stats.life);
            UpdateStatSlot(forceIconImage, forceFill, stats.force);
            UpdateStatSlot(wealthIconImage, wealthFill, stats.wealth);
            UpdateStatSlot(faithIconImage, faithFill, stats.faith);
        }

        private static void UpdateStatSlot(Image iconImage, Image fill, int value)
        {
            var normalized = Mathf.Clamp01(value / 100f);
            fill.fillAmount = normalized;
            fill.color = value <= 20
                ? new Color32(202, 58, 45, 230)
                : value <= 40
                    ? new Color32(212, 146, 61, 225)
                    : new Color32(237, 214, 136, 225);
            iconImage.color = value <= 20
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
            var selectedChoice = selectedLeft ? currentNode?.leftChoice : currentNode?.rightChoice;

            SetChoicePanelReveal(choiceRevealPanel, strength, y);
            SetChoiceReveal(leftChoiceText, selectedLeft ? strength : 0f);
            SetChoiceReveal(rightChoiceText, selectedLeft ? 0f : strength);
            UpdateStatImpactPreview(selectedChoice, strength);

            if (strength >= BranchPredictionDragThreshold)
            {
                ScheduleChoicePrediction(currentNode, selectedChoice, stats.Clone(), 100, "drag_near_branch");
            }
        }

        private void UpdateStatImpactPreview(ChoiceOption choice, float strength)
        {
            var delta = ResolveChoiceImpactPreview(choice);
            UpdateStatImpactDot(lifeImpactDot, delta.life, strength);
            UpdateStatImpactDot(forceImpactDot, delta.force, strength);
            UpdateStatImpactDot(wealthImpactDot, delta.wealth, strength);
            UpdateStatImpactDot(faithImpactDot, delta.faith, strength);
        }

        private PlayerStats ResolveChoiceImpactPreview(ChoiceOption choice)
        {
            if (currentNode == null || choice == null)
            {
                return new PlayerStats(0, 0, 0, 0);
            }

            return PlayerStats.ClampDelta(
                PredictImmediateDeltaForChoice(currentNode, choice, stats),
                ResolveImmediateDeltaLimitForChoice(currentNode, choice));
        }

        private static void UpdateStatImpactDot(Image dot, int delta, float strength)
        {
            if (dot == null)
            {
                return;
            }

            var impact = Mathf.Clamp01(Mathf.Abs(delta) / (float)BranchStatDeltaLimit);
            var active = strength > 0.01f && impact > 0f;
            var targetSize = active ? Mathf.Lerp(19f, StatImpactDotMaxSize, impact) : StatImpactDotBaseSize;
            var size = Mathf.Lerp(StatImpactDotBaseSize, targetSize, strength);
            dot.rectTransform.sizeDelta = new Vector2(size, size);

            var idleColor = new Color32(151, 119, 61, 255);
            if (!active)
            {
                dot.color = idleColor;
                return;
            }

            var positiveColor = new Color32(255, 235, 134, 255);
            var negativeColor = new Color32(255, 121, 82, 255);
            var targetColor = delta < 0 ? negativeColor : positiveColor;
            dot.color = LerpColor(idleColor, targetColor, Mathf.Clamp01(0.35f + strength * 0.65f));
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
            cacheText.text = "Ready";

            var explicitNext = repository.GetById(choice.nextMainlineNodeId);
            if (explicitNext != null)
            {
                var feedback = ApplyChoiceConsequences(currentNode, choice, choice.statHint, ImmediateStatDeltaLimit, "主干");
                if (TryShowGameOver())
                {
                    return;
                }

                ShowNode(explicitNext, BuildChoiceStatus("Mainline", feedback));
                return;
            }

            var sourceNode = currentNode;
            var naturalNext = repository.NextMainlineAfter(sourceNode);
            if (naturalNext == null && sourceNode.nodeKind == StoryNodeKind.Mainline)
            {
                var feedback = ApplyChoiceConsequences(sourceNode, choice, choice.statHint, ImmediateStatDeltaLimit, "终章");
                if (TryShowGameOver())
                {
                    return;
                }

                ShowChapterComplete(choice, feedback);
                return;
            }

            var cacheKey = NodeCacheService.CreateCacheKey(repository.StoryId, sourceNode, choice, stats);
            if (cacheService.TryGet(cacheKey, out var cached))
            {
                if (!IsDraftEntry(cached))
                {
                    AttachReadyImage(cached);
                    var feedback = ApplyChoiceConsequences(sourceNode, choice, cached.statDelta, BranchStatDeltaLimit, "分支");
                    if (TryShowGameOver())
                    {
                        return;
                    }

                    ShowNode(cached.resultNode, BuildChoiceStatus($"Cache Hit ({NodeCacheStatuses.Normalize(cached.status)})", feedback), cached);
                    QueueImageForEntry(cached, 45, "cache_hit_backfill", true);
                    QueuePanoramaForEntry(cached, 38, "cache_hit_panorama", false);
                    return;
                }
            }

            NodeCacheEntry entry;
            var acceptStreamingText = true;
            var streamingShown = false;
            Action<string> onStoryText = text =>
            {
                if (!acceptStreamingText || string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (!streamingShown)
                {
                    streamingShown = true;
                    ShowStreamingBranchStart(sourceNode, choice, cacheKey);
                }

                UpdateStreamingBranchText(cacheKey, text);
            };

            try
            {
                entry = await GetOrCreateBranchEntryAsync(
                    sourceNode,
                    choice,
                    stats.Clone(),
                    naturalNext,
                    cacheKey,
                    onStoryText,
                    cancellationToken,
                    true);
            }
            finally
            {
                acceptStreamingText = false;
            }

            if (entry == null)
            {
                return;
            }

            var generatedFeedback = ApplyChoiceConsequences(sourceNode, choice, entry.statDelta, BranchStatDeltaLimit, "分支");
            if (TryShowGameOver())
            {
                return;
            }

            AttachReadyImage(entry);
            var textStatus = IsDraftEntry(entry) ? "Draft Fallback + Pending Review" : "Generated + Pending Review";
            ShowNode(entry.resultNode, BuildChoiceStatus(textStatus, generatedFeedback), entry);
            QueueImageForEntry(entry, 45, "choice_submit_backfill", true);
            QueuePanoramaForEntry(entry, 38, "choice_submit_panorama", false);
        }

        private string ApplyChoiceConsequences(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats immediateDelta,
            int immediateLimit,
            string sourceLabel)
        {
            var statsBeforeChoice = stats.Clone();
            var choicesSinceBeforeChoice = choicesSinceStatImpact;
            var nextImpactIntervalBeforeChoice = nextStatImpactInterval;
            var eventsBeforeChoice = recentStatEvents.ToArray();
            choiceCount++;
            choicesSinceStatImpact++;

            var feedbackParts = new List<string>();
            var boundedImmediate = PlayerStats.ClampDelta(immediateDelta, immediateLimit);
            if (!ShouldApplyStatImpact(choicesSinceBeforeChoice, nextImpactIntervalBeforeChoice))
            {
                RecordStatEvent(sourceNode, choice, "观察", new PlayerStats(0, 0, 0, 0), $"影响酝酿{choicesSinceStatImpact}/{nextStatImpactInterval}");
                lastStatFeedback = $"局势酝酿 {choicesSinceStatImpact}/{nextStatImpactInterval}";
                return lastStatFeedback;
            }

            choicesSinceStatImpact = 0;
            nextStatImpactInterval = NextStatImpactInterval(nextImpactIntervalBeforeChoice);
            stats.Apply(boundedImmediate);
            RecordStatEvent(sourceNode, choice, sourceLabel, boundedImmediate, "");
            if (!PlayerStats.IsZeroDelta(boundedImmediate))
            {
                feedbackParts.Add($"{sourceLabel} {FormatStatDelta(boundedImmediate)}");
            }

            if (stats.IsGameOver())
            {
                lastStatFeedback = feedbackParts.Count == 0
                    ? "数值稳定"
                    : CompactStatus(string.Join("  ", feedbackParts));
                return lastStatFeedback;
            }

            var judgement = ResolvePredictedStatJudgement(
                sourceNode,
                choice,
                statsBeforeChoice,
                choicesSinceBeforeChoice,
                nextImpactIntervalBeforeChoice,
                eventsBeforeChoice,
                boundedImmediate);
            var judgementDelta = judgement?.statDelta ?? new PlayerStats(0, 0, 0, 0);
            var reason = CompactStatus(judgement?.reason ?? "局势暂稳");
            ApplyJudgementDelta(judgementDelta);
            RecordStatEvent(sourceNode, choice, "局势", judgementDelta, reason);

            feedbackParts.Add(PlayerStats.IsZeroDelta(judgementDelta)
                ? $"局势稳定：{reason}"
                : $"局势{FormatStatDelta(judgementDelta)}：{reason}");

            lastStatFeedback = feedbackParts.Count == 0
                ? "数值稳定"
                : CompactStatus(string.Join("  ", feedbackParts));
            return lastStatFeedback;
        }

        private StatJudgementResult ResolvePredictedStatJudgement(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats statsBeforeChoice,
            int choicesSinceBeforeChoice,
            int nextImpactIntervalBeforeChoice,
            IReadOnlyList<string> eventsBeforeChoice,
            PlayerStats boundedImmediate)
        {
            var key = CreateStatJudgementKey(sourceNode, choice, statsBeforeChoice, choicesSinceBeforeChoice, nextImpactIntervalBeforeChoice, eventsBeforeChoice);
            if (statJudgementTasks.TryGetValue(key, out var task))
            {
                if (task.IsCompleted && !task.IsCanceled && !task.IsFaulted)
                {
                    return task.Result;
                }

                if (task.IsFaulted)
                {
                    Debug.LogWarning($"AI Builder predicted stat judgement failed safely: {task.Exception?.GetBaseException().Message}");
                }
            }

            var predictedStats = statsBeforeChoice.Clone();
            predictedStats.Apply(boundedImmediate);
            var predictedEvents = BuildPredictedStatEvents(eventsBeforeChoice, sourceNode, choice, choiceCount, "选择", boundedImmediate, "");
            return CreateLocalStatJudgement(sourceNode, choice, predictedStats, predictedEvents);
        }

        private void ScheduleStatJudgementPredictionsFromNode(StoryNode node, PlayerStats statsSnapshot, string reason)
        {
            if (node == null || node.nodeKind == StoryNodeKind.Ending || !ShouldApplyStatImpact(choicesSinceStatImpact, nextStatImpactInterval))
            {
                return;
            }

            var eventsSnapshot = recentStatEvents.ToArray();
            ScheduleStatJudgementPrediction(node, node.leftChoice, statsSnapshot, choicesSinceStatImpact, nextStatImpactInterval, eventsSnapshot, reason + "_left", false);
            ScheduleStatJudgementPrediction(node, node.rightChoice, statsSnapshot, choicesSinceStatImpact, nextStatImpactInterval, eventsSnapshot, reason + "_right", false);
        }

        private void ScheduleStatJudgementPrediction(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats statsSnapshot,
            int choicesSinceSnapshot,
            int nextImpactIntervalSnapshot,
            IReadOnlyList<string> eventsSnapshot,
            string reason,
            bool forceRefresh)
        {
            if (sourceNode == null || choice == null || statsSnapshot == null || !ShouldApplyStatImpact(choicesSinceSnapshot, nextImpactIntervalSnapshot))
            {
                return;
            }

            var key = CreateStatJudgementKey(sourceNode, choice, statsSnapshot, choicesSinceSnapshot, nextImpactIntervalSnapshot, eventsSnapshot);
            if (!forceRefresh && statJudgementTasks.ContainsKey(key))
            {
                return;
            }

            var immediateDelta = PlayerStats.ClampDelta(
                PredictImmediateDeltaForChoice(sourceNode, choice, statsSnapshot),
                ResolveImmediateDeltaLimitForChoice(sourceNode, choice));
            var predictedStats = statsSnapshot.Clone();
            predictedStats.Apply(immediateDelta);
            var predictedEvents = BuildPredictedStatEvents(eventsSnapshot, sourceNode, choice, choiceCount + 1, "预判", immediateDelta, reason);
            var task = RunStatJudgementPredictionAsync(sourceNode, choice, predictedStats, predictedEvents, backgroundCancellation.Token);
            statJudgementTasks[key] = task;
            _ = ObserveStatJudgementTaskAsync(task);
        }

        private async Task<StatJudgementResult> RunStatJudgementPredictionAsync(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats predictedStats,
            IReadOnlyList<string> predictedEvents,
            CancellationToken cancellationToken)
        {
            var service = textService as IAiStatJudgementService ?? new MockAiTextService();
            try
            {
                var result = await service.JudgeStatsAsync(sourceNode, choice, predictedStats.Clone(), predictedEvents, cancellationToken);
                result ??= CreateLocalStatJudgement(sourceNode, choice, predictedStats, predictedEvents);
                result.statDelta ??= new PlayerStats(0, 0, 0, 0);
                result.reason = string.IsNullOrWhiteSpace(result.reason) ? "局势暂稳" : result.reason.Trim();
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder stat pre-judgement fell back to local rules: {ex.Message}");
                return CreateLocalStatJudgement(sourceNode, choice, predictedStats, predictedEvents);
            }
        }

        private static async Task ObserveStatJudgementTaskAsync(Task<StatJudgementResult> task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }

        private PlayerStats PredictImmediateDeltaForChoice(StoryNode sourceNode, ChoiceOption choice, PlayerStats statsSnapshot)
        {
            if (sourceNode == null || choice == null)
            {
                return new PlayerStats(0, 0, 0, 0);
            }

            if (!string.IsNullOrWhiteSpace(choice.nextMainlineNodeId) || repository.NextMainlineAfter(sourceNode) == null)
            {
                return choice.statHint;
            }

            var cacheKey = NodeCacheService.CreateCacheKey(repository.StoryId, sourceNode, choice, statsSnapshot);
            if (cacheService.TryGet(cacheKey, out var cached) && !IsDraftEntry(cached))
            {
                return cached.statDelta;
            }

            if (textPredictionTasks.TryGetValue(cacheKey, out var task)
                && task.IsCompleted
                && !task.IsCanceled
                && !task.IsFaulted
                && task.Result != null
                && !IsDraftEntry(task.Result))
            {
                return task.Result.statDelta;
            }

            return choice.statHint;
        }

        private int ResolveImmediateDeltaLimitForChoice(StoryNode sourceNode, ChoiceOption choice)
        {
            if (sourceNode == null || choice == null)
            {
                return ImmediateStatDeltaLimit;
            }

            return !string.IsNullOrWhiteSpace(choice.nextMainlineNodeId) || repository.NextMainlineAfter(sourceNode) == null
                ? ImmediateStatDeltaLimit
                : BranchStatDeltaLimit;
        }

        private static bool ShouldApplyStatImpact(int choicesSinceSnapshot, int nextImpactIntervalSnapshot)
        {
            return choicesSinceSnapshot + 1 >= Mathf.Clamp(nextImpactIntervalSnapshot, StatImpactMinInterval, StatImpactMaxInterval);
        }

        private static int NextStatImpactInterval(int previousInterval)
        {
            return previousInterval <= StatImpactMinInterval ? StatImpactMaxInterval : StatImpactMinInterval;
        }

        private static string CreateStatJudgementKey(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats statsSnapshot,
            int choicesSinceSnapshot,
            int nextImpactIntervalSnapshot,
            IReadOnlyList<string> eventsSnapshot)
        {
            var statsPart = statsSnapshot == null
                ? "0-0-0-0"
                : $"{statsSnapshot.life}-{statsSnapshot.force}-{statsSnapshot.wealth}-{statsSnapshot.faith}";
            var eventsPart = StableRuntimeHash(string.Join("|", eventsSnapshot ?? Array.Empty<string>()));
            return $"{sourceNode?.id}|{choice?.id}|{statsPart}|{choicesSinceSnapshot}/{nextImpactIntervalSnapshot}|{eventsPart}";
        }

        private static string[] BuildPredictedStatEvents(
            IReadOnlyList<string> eventsSnapshot,
            StoryNode sourceNode,
            ChoiceOption choice,
            int predictedChoiceCount,
            string sourceLabel,
            PlayerStats delta,
            string reason)
        {
            var events = new List<string>(eventsSnapshot ?? Array.Empty<string>());
            var deltaText = PlayerStats.IsZeroDelta(delta) ? "无变化" : FormatStatDelta(delta);
            var reasonText = string.IsNullOrWhiteSpace(reason) ? "" : $"，{reason}";
            events.Add($"#{predictedChoiceCount}《{sourceNode?.title}》选择「{choice?.label}」{sourceLabel}:{deltaText}{reasonText}");
            while (events.Count > RecentStatEventLimit)
            {
                events.RemoveAt(0);
            }

            return events.ToArray();
        }

        private static StatJudgementResult CreateLocalStatJudgement(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats predictedStats,
            IReadOnlyList<string> predictedEvents)
        {
            var text = $"{sourceNode?.title} {sourceNode?.body} {choice?.label} {choice?.intent} {string.Join(" ", predictedEvents ?? Array.Empty<string>())}".ToLowerInvariant();
            var delta = new PlayerStats(0, 0, 0, 0);

            if (ContainsAny(text, "伤", "血", "毒", "病", "战", "伏击", "danger", "poison", "wound"))
            {
                delta.life -= 2;
            }

            if (ContainsAny(text, "训练", "决斗", "战斗", "军", "剑", "fight", "duel", "train"))
            {
                delta.force += 2;
            }

            if (ContainsAny(text, "贿", "献金", "赎", "买", "税", "coin", "bribe", "pay"))
            {
                delta.wealth -= 2;
            }
            else if (ContainsAny(text, "宝", "赏", "贸易", "treasure", "reward", "trade"))
            {
                delta.wealth += 2;
            }

            if (ContainsAny(text, "背叛", "禁术", "亵渎", "betray", "forbidden", "blasphemy"))
            {
                delta.faith -= 2;
            }
            else if (ContainsAny(text, "祈", "誓", "怜悯", "圣", "pray", "oath", "mercy"))
            {
                delta.faith += 2;
            }

            return new StatJudgementResult
            {
                statDelta = delta,
                reason = PlayerStats.IsZeroDelta(delta) ? "局势暂稳" : "近期选择产生连锁影响"
            };
        }

        private void ApplyJudgementDelta(PlayerStats delta)
        {
            if (delta == null)
            {
                return;
            }

            stats.life = Mathf.Clamp(stats.life + delta.life, 0, 100);
            stats.force = Mathf.Clamp(stats.force + delta.force, 0, 100);
            stats.wealth = Mathf.Clamp(stats.wealth + delta.wealth, 0, 100);
            stats.faith = Mathf.Clamp(stats.faith + delta.faith, 0, 100);
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            return needles.Any(needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string StableRuntimeHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var character in value ?? "")
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                return hash.ToString("x8");
            }
        }

        private void RecordStatEvent(StoryNode sourceNode, ChoiceOption choice, string sourceLabel, PlayerStats delta, string reason)
        {
            var deltaText = PlayerStats.IsZeroDelta(delta) ? "无变化" : FormatStatDelta(delta);
            var reasonText = string.IsNullOrWhiteSpace(reason) ? "" : $"，{reason}";
            recentStatEvents.Add($"#{choiceCount}《{sourceNode?.title}》选择「{choice?.label}」{sourceLabel}:{deltaText}{reasonText}");
            while (recentStatEvents.Count > RecentStatEventLimit)
            {
                recentStatEvents.RemoveAt(0);
            }
        }

        private static string BuildChoiceStatus(string baseStatus, string feedback)
        {
            return string.IsNullOrWhiteSpace(feedback)
                ? baseStatus
                : CompactStatus($"{baseStatus} | {feedback}");
        }

        private static string FormatStatDelta(PlayerStats delta)
        {
            if (PlayerStats.IsZeroDelta(delta))
            {
                return "";
            }

            var parts = new List<string>();
            AddStatDelta(parts, "生命", delta.life);
            AddStatDelta(parts, "武力", delta.force);
            AddStatDelta(parts, "财富", delta.wealth);
            AddStatDelta(parts, "信仰", delta.faith);
            return string.Join(" ", parts);
        }

        private static void AddStatDelta(List<string> parts, string label, int value)
        {
            if (value == 0)
            {
                return;
            }

            parts.Add($"{label}{(value > 0 ? "+" : "")}{value}");
        }

        private async Task<NodeCacheEntry> GetOrCreateBranchEntryAsync(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats statsSnapshot,
            StoryNode naturalNext,
            string cacheKey,
            Action<string> onStoryText,
            CancellationToken cancellationToken,
            bool allowOverwriteDraft = false)
        {
            if (cacheService.TryGet(cacheKey, out var cached))
            {
                if (!allowOverwriteDraft || !IsDraftEntry(cached))
                {
                    AttachReadyImage(cached);
                    return cached;
                }
            }

            Task firstStoryTextTask = null;
            var storyTextHandler = onStoryText;
            if (onStoryText != null)
            {
                var firstStoryTextSource = new TaskCompletionSource<bool>();
                firstStoryTextTask = firstStoryTextSource.Task;
                storyTextHandler = text =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        firstStoryTextSource.TrySetResult(true);
                    }

                    onStoryText(text);
                };
            }

            var task = EnsureBranchTextPrediction(sourceNode, choice, statsSnapshot, naturalNext, cacheKey, 95, "choice_submit", cancellationToken, allowOverwriteDraft, storyTextHandler);
            return await AwaitBranchEntryOrDraftAsync(task, sourceNode, choice, naturalNext, cacheKey, firstStoryTextTask, cancellationToken);
        }

        private Task<NodeCacheEntry> EnsureBranchTextPrediction(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats statsSnapshot,
            StoryNode naturalNext,
            string cacheKey,
            int priority,
            string reason,
            CancellationToken cancellationToken,
            bool allowOverwriteDraft = false,
            Action<string> onStoryText = null)
        {
            if (sourceNode == null || choice == null || string.IsNullOrWhiteSpace(cacheKey))
            {
                return Task.FromResult<NodeCacheEntry>(null);
            }

            if (cacheService.TryGet(cacheKey, out var cached))
            {
                if (!allowOverwriteDraft || !IsDraftEntry(cached))
                {
                    AttachReadyImage(cached);
                    QueueImageForEntry(cached, priority, reason + "_cached", false);
                    QueuePanoramaForEntry(cached, Mathf.Max(10, priority - 8), reason + "_cached_panorama", false);
                    return Task.FromResult(cached);
                }

                cached.textStatus = NodeTextStatuses.Generating;
                cacheService.Put(cached);
            }

            if (cacheService.TryGet(cacheKey, out cached) && (!allowOverwriteDraft || !IsDraftEntry(cached)))
            {
                AttachReadyImage(cached);
                QueueImageForEntry(cached, priority, reason + "_cached", false);
                QueuePanoramaForEntry(cached, Mathf.Max(10, priority - 8), reason + "_cached_panorama", false);
                return Task.FromResult(cached);
            }

            if (textPredictionTasks.TryGetValue(cacheKey, out var runningTask))
            {
                return AttachStreamingListener(cacheKey, runningTask, onStoryText);
            }

            streamingStoryTextByKey.Remove(cacheKey);
            var task = GenerateAndCacheBranchAsync(sourceNode, choice, statsSnapshot, naturalNext, cacheKey, priority, reason, cancellationToken, allowOverwriteDraft);
            textPredictionTasks[cacheKey] = task;
            _ = ForgetTextPredictionAsync(cacheKey, task);
            return AttachStreamingListener(cacheKey, task, onStoryText);
        }

        private async Task ForgetTextPredictionAsync(string cacheKey, Task<NodeCacheEntry> task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
            finally
            {
                if (textPredictionTasks.TryGetValue(cacheKey, out var existing) && ReferenceEquals(existing, task))
                {
                    textPredictionTasks.Remove(cacheKey);
                }
            }
        }

        private Task<NodeCacheEntry> AttachStreamingListener(string cacheKey, Task<NodeCacheEntry> task, Action<string> onStoryText)
        {
            if (onStoryText == null || task == null)
            {
                return task;
            }

            var unregister = RegisterStreamingStoryListener(cacheKey, onStoryText);
            return AwaitAndUnregisterStreamingListenerAsync(task, unregister);
        }

        private Action RegisterStreamingStoryListener(string cacheKey, Action<string> onStoryText)
        {
            if (!streamingStoryTextListeners.TryGetValue(cacheKey, out var listeners))
            {
                listeners = new List<Action<string>>();
                streamingStoryTextListeners[cacheKey] = listeners;
            }

            listeners.Add(onStoryText);
            if (streamingStoryTextByKey.TryGetValue(cacheKey, out var currentText) && !string.IsNullOrWhiteSpace(currentText))
            {
                onStoryText(currentText);
            }

            return () =>
            {
                if (streamingStoryTextListeners.TryGetValue(cacheKey, out var existingListeners))
                {
                    existingListeners.Remove(onStoryText);
                    if (existingListeners.Count == 0)
                    {
                        streamingStoryTextListeners.Remove(cacheKey);
                    }
                }
            };
        }

        private static async Task<NodeCacheEntry> AwaitAndUnregisterStreamingListenerAsync(Task<NodeCacheEntry> task, Action unregister)
        {
            try
            {
                return await task;
            }
            finally
            {
                unregister?.Invoke();
            }
        }

        private void PublishStreamingStoryText(string cacheKey, string storyTextValue)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrEmpty(storyTextValue))
            {
                return;
            }

            streamingStoryTextByKey[cacheKey] = storyTextValue;
            if (!streamingStoryTextListeners.TryGetValue(cacheKey, out var listeners))
            {
                return;
            }

            foreach (var listener in listeners.ToArray())
            {
                listener?.Invoke(storyTextValue);
            }
        }

        private async Task<NodeCacheEntry> GenerateAndCacheBranchAsync(
            StoryNode sourceNode,
            ChoiceOption choice,
            PlayerStats statsSnapshot,
            StoryNode naturalNext,
            string cacheKey,
            int priority,
            string reason,
            CancellationToken cancellationToken,
            bool allowOverwriteDraft = false)
        {
            if (cacheService.TryGet(cacheKey, out var cached))
            {
                if (!allowOverwriteDraft || !IsDraftEntry(cached))
                {
                    AttachReadyImage(cached);
                    return cached;
                }

                cached.textStatus = NodeTextStatuses.Generating;
                cacheService.Put(cached);
            }

            var bypassPredictionLimiter = priority >= 90 || reason.StartsWith("choice_submit", StringComparison.OrdinalIgnoreCase);
            if (!bypassPredictionLimiter)
            {
                await textPredictionLimiter.WaitAsync(cancellationToken);
            }

            try
            {
                if (cacheService.TryGet(cacheKey, out cached) && (!allowOverwriteDraft || !IsDraftEntry(cached)))
                {
                    AttachReadyImage(cached);
                    return cached;
                }

                var aiResult = textService is IAiStreamingTextService streamingTextService
                    ? await streamingTextService.GenerateNextNodeStreamingAsync(
                        sourceNode,
                        choice,
                        statsSnapshot.Clone(),
                        text => PublishStreamingStoryText(cacheKey, text),
                        cancellationToken)
                    : await textService.GenerateNextNodeAsync(sourceNode, choice, statsSnapshot.Clone(), cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                if (IsProviderFallbackTextResult(aiResult))
                {
                    Debug.LogWarning("AI Builder text service reached mock fallback; keeping the draft placeholder instead of caching mock text as generated AI content.");
                    return CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
                }

                var branch = CreateBranchNode(sourceNode, aiResult, choice, naturalNext);
                var entry = CreateCacheEntry(sourceNode, choice, aiResult, branch, cacheKey);
                AttachReadyImage(entry);
                cacheService.Put(entry);
                if (currentNode != null && sourceNode != null && string.Equals(currentNode.id, sourceNode.id, StringComparison.OrdinalIgnoreCase))
                {
                    ScheduleStatJudgementPrediction(sourceNode, choice, statsSnapshot.Clone(), choicesSinceStatImpact, nextStatImpactInterval, recentStatEvents.ToArray(), reason + "_text_ready", true);
                }

                var imagePriority = HasGeneratedPortraitFallback(entry) ? Mathf.Max(10, priority - 25) : priority;
                var forceImage = priority >= 90 || reason.StartsWith("choice_submit", StringComparison.OrdinalIgnoreCase);
                QueueImageForEntry(entry, imagePriority, reason + "_text_ready", forceImage);
                QueuePanoramaForEntry(entry, Mathf.Max(10, priority - 8), reason + "_panorama_ready", false);
                return entry;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder branch generation used draft fallback: {ex.Message}");
                return CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
            }
            finally
            {
                if (!bypassPredictionLimiter)
                {
                    textPredictionLimiter.Release();
                }
            }
        }

        private bool IsDraftEntry(NodeCacheEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            return string.Equals(entry.textStatus, NodeTextStatuses.Draft, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(entry.textStatus, NodeTextStatuses.Generating, StringComparison.OrdinalIgnoreCase)
                   || IsProviderFallbackCacheEntry(entry);
        }

        private bool IsProviderFallbackTextResult(AiTextResult result)
        {
            return providerSettings != null
                   && providerSettings.CanUseText
                   && IsMockTextResult(result);
        }

        private bool IsProviderFallbackCacheEntry(NodeCacheEntry entry)
        {
            return providerSettings != null
                   && providerSettings.CanUseText
                   && entry?.resultNode != null
                   && !string.IsNullOrWhiteSpace(entry.resultNode.body)
                   && entry.resultNode.body.TrimStart().StartsWith("Mock branch after", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMockTextResult(AiTextResult result)
        {
            return result != null
                   && ((result.summaryTags != null
                        && result.summaryTags.Any(tag => string.Equals(tag, "mock", StringComparison.OrdinalIgnoreCase)))
                       || (!string.IsNullOrWhiteSpace(result.storyText)
                           && result.storyText.TrimStart().StartsWith("Mock branch after", StringComparison.OrdinalIgnoreCase)));
        }

        private StoryNode CreateDraftBranchNode(StoryNode sourceNode, ChoiceOption sourceChoice, StoryNode nextMainline, string cacheKey)
        {
            var nextId = nextMainline == null ? "" : nextMainline.id;
            var title = string.IsNullOrWhiteSpace(sourceChoice?.label) ? "临时分支" : sourceChoice.label;
            var body = $"你立刻执行「{title}」。局势偏离主线，但记录员已经留出回旋余地，后续细节会在暗处补全。";

            return new StoryNode
            {
                id = $"branch_{sourceNode.id}_{sourceChoice.id}_{ImageGenerationPolicy.StableBucket(cacheKey):0000}",
                chapterId = sourceNode.chapterId,
                title = title,
                body = body,
                imageRef = "branch",
                nodeKind = nextMainline == null ? StoryNodeKind.Ending : StoryNodeKind.GeneratedBranch,
                mainlineIndex = nextMainline == null ? sourceNode.mainlineIndex + 1 : nextMainline.mainlineIndex - 1,
                leftChoice = new ChoiceOption
                {
                    id = "branch_left",
                    label = "继续追问",
                    intent = "继续探索分支后回到主线。",
                    direction = "left",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(0, 0, 0, 0)
                },
                rightChoice = new ChoiceOption
                {
                    id = "branch_right",
                    label = "回到主线",
                    intent = "收束分支后回到主线。",
                    direction = "right",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(0, 0, 0, 0)
                }
            };
        }

        private NodeCacheEntry CreateDraftCacheEntry(StoryNode sourceNode, ChoiceOption choice, StoryNode naturalNext, string cacheKey)
        {
            var branch = CreateDraftBranchNode(sourceNode, choice, naturalNext, cacheKey);
            var locationTag = FirstNonEmpty(sourceNode.chapterId, sourceNode.title);
            var moodTag = "draft";
            var majorEventTag = FirstNonEmpty(choice.id, choice.label);
            var panoramaCacheKey = NodeCacheService.CreatePanoramaCacheKey(repository.StoryId, branch.chapterId, locationTag, moodTag, majorEventTag);
            return new NodeCacheEntry
            {
                storyId = repository.StoryId,
                cacheKey = cacheKey,
                sourceNodeId = sourceNode.id,
                choiceId = choice.id,
                resultNode = branch,
                statDelta = PlayerStats.ClampDelta(choice.statHint, ImmediateStatDeltaLimit),
                textStatus = NodeTextStatuses.Draft,
                imagePrompt = "",
                imageCacheKey = NodeCacheService.CreateImageCacheKey(repository.StoryId, branch.chapterId, locationTag, moodTag, majorEventTag),
                locationTag = locationTag,
                moodTag = moodTag,
                majorEventTag = majorEventTag,
                imagePath = "",
                imageStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable,
                imageError = "",
                panoramaPrompt = "",
                panoramaCacheKey = panoramaCacheKey,
                panoramaPath = "",
                panoramaStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable,
                panoramaError = "",
                createdAt = DateTime.UtcNow.ToString("O"),
                status = NodeCacheStatuses.PendingReview
            };
        }

        private NodeCacheEntry CreateCacheEntry(StoryNode sourceNode, ChoiceOption choice, AiTextResult aiResult, StoryNode branch, string cacheKey)
        {
            var locationTag = FirstNonEmpty(aiResult.locationTag, FindSummaryTag(aiResult.summaryTags, "location"), sourceNode.chapterId);
            var moodTag = FirstNonEmpty(aiResult.moodTag, FindSummaryTag(aiResult.summaryTags, "mood"), "branch");
            var majorEventTag = FirstNonEmpty(aiResult.majorEventTag, FindSummaryTag(aiResult.summaryTags, "event"), choice.id, choice.label);
            var imageCacheKey = NodeCacheService.CreateImageCacheKey(repository.StoryId, branch.chapterId, locationTag, moodTag, majorEventTag);
            var panoramaCacheKey = NodeCacheService.CreatePanoramaCacheKey(repository.StoryId, branch.chapterId, locationTag, moodTag, majorEventTag);

            return new NodeCacheEntry
            {
                storyId = repository.StoryId,
                cacheKey = cacheKey,
                sourceNodeId = sourceNode.id,
                choiceId = choice.id,
                resultNode = branch,
                statDelta = PlayerStats.ClampDelta(aiResult.statDelta, BranchStatDeltaLimit),
                textStatus = NodeTextStatuses.Generated,
                imagePrompt = aiResult.imagePrompt,
                imageCacheKey = imageCacheKey,
                locationTag = locationTag,
                moodTag = moodTag,
                majorEventTag = majorEventTag,
                imagePath = "",
                imageStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable,
                imageError = "",
                panoramaPrompt = aiResult.panoramaPrompt,
                panoramaCacheKey = panoramaCacheKey,
                panoramaPath = "",
                panoramaStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable,
                panoramaError = "",
                createdAt = DateTime.UtcNow.ToString("O"),
                status = NodeCacheStatuses.PendingReview
            };
        }

        private void SchedulePredictionsFromNode(StoryNode node, PlayerStats predictedStats, int depth, int priority, string reason)
        {
            if (node == null || depth < 0 || node.nodeKind == StoryNodeKind.Ending)
            {
                return;
            }

            ScheduleChoicePrediction(node, node.leftChoice, predictedStats, priority, reason + "_left");
            ScheduleChoicePrediction(node, node.rightChoice, predictedStats, priority, reason + "_right");

            if (depth == 0)
            {
                return;
            }

            foreach (var choice in new[] { node.leftChoice, node.rightChoice })
            {
                if (choice == null || IsBranchChoice(node, choice))
                {
                    continue;
                }

                var nextNode = repository.GetById(choice.nextMainlineNodeId);
                if (nextNode == null)
                {
                    continue;
                }

                var nextStats = predictedStats.Clone();
                nextStats.Apply(choice.statHint);
                SchedulePredictionsFromNode(nextNode, nextStats, depth - 1, Mathf.Max(10, priority - 15), reason + "_lookahead");
            }
        }

        private void ScheduleChoicePrediction(StoryNode sourceNode, ChoiceOption choice, PlayerStats statsSnapshot, int priority, string reason)
        {
            if (!IsBranchChoice(sourceNode, choice))
            {
                return;
            }

            var cacheKey = NodeCacheService.CreateCacheKey(repository.StoryId, sourceNode, choice, statsSnapshot);
            var naturalNext = repository.NextMainlineAfter(sourceNode);
            EnsureBranchTextPrediction(sourceNode, choice, statsSnapshot, naturalNext, cacheKey, priority, reason, backgroundCancellation.Token);
        }

        private bool IsBranchChoice(StoryNode sourceNode, ChoiceOption choice)
        {
            return sourceNode != null
                   && sourceNode.nodeKind != StoryNodeKind.Ending
                   && choice != null
                   && string.IsNullOrWhiteSpace(choice.nextMainlineNodeId);
        }

        private void QueueImageForEntry(NodeCacheEntry entry, int priority, string reason, bool force)
        {
            if (entry == null || entry.resultNode == null || IsDraftEntry(entry))
            {
                return;
            }

            if (!providerSettings.CanUseImage || !providerSettings.enableRuntimeImages)
            {
                entry.imageStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable;
                cacheService.Put(entry);
                return;
            }

            EnsureImageCacheKey(entry);
            if (AttachReadyImage(entry))
            {
                return;
            }

            var normalizedStatus = string.IsNullOrWhiteSpace(entry.imageStatus) ? NodeImageStatuses.SkippedByPolicy : entry.imageStatus;
            if (string.Equals(normalizedStatus, NodeImageStatuses.Generated, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedStatus, NodeImageStatuses.Reused, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedStatus, NodeImageStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!force && !ImageGenerationPolicy.ShouldGenerate(providerSettings, CurrentStoryCacheEntries(), entry.imageCacheKey))
            {
                entry.imageStatus = NodeImageStatuses.SkippedByPolicy;
                cacheService.Put(entry);
                return;
            }

            if (queuedImageKeys.Contains(entry.imageCacheKey) || runningImageKeys.Contains(entry.imageCacheKey))
            {
                return;
            }

            entry.imageStatus = NodeImageStatuses.Queued;
            entry.imageError = "";
            cacheService.Put(entry);
            queuedImageKeys.Add(entry.imageCacheKey);
            imageJobs.Add(new ImageGenerationJob
            {
                entry = entry,
                imageCacheKey = entry.imageCacheKey,
                purpose = AiImagePurpose.Card,
                priority = Mathf.Clamp(priority, 0, 100),
                sequence = imageJobSequence++,
                reason = reason
            });
            StartImagePump();
        }

        private void QueuePanoramaForEntry(NodeCacheEntry entry, int priority, string reason, bool force)
        {
            if (entry == null || entry.resultNode == null || IsDraftEntry(entry))
            {
                return;
            }

            if (!providerSettings.CanUseImage || !providerSettings.enableRuntimePanoramas)
            {
                entry.panoramaStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable;
                cacheService.Put(entry);
                return;
            }

            EnsurePanoramaCacheKey(entry);
            if (AttachReadyPanorama(entry))
            {
                return;
            }

            var normalizedStatus = string.IsNullOrWhiteSpace(entry.panoramaStatus) ? NodeImageStatuses.SkippedByPolicy : entry.panoramaStatus;
            if (string.Equals(normalizedStatus, NodeImageStatuses.Generated, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedStatus, NodeImageStatuses.Reused, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedStatus, NodeImageStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var isImportantMoment = IsImportantPanoramaMoment(entry, reason);
            var isDistantMoment = IsDistantPanoramaMoment(reason);
            if (!force && !PanoramaGenerationPolicy.ShouldGenerate(providerSettings, CurrentStoryCacheEntries(), entry.panoramaCacheKey, isImportantMoment, isDistantMoment))
            {
                entry.panoramaStatus = NodeImageStatuses.SkippedByPolicy;
                cacheService.Put(entry);
                return;
            }

            if (queuedImageKeys.Contains(entry.panoramaCacheKey) || runningImageKeys.Contains(entry.panoramaCacheKey))
            {
                return;
            }

            entry.panoramaStatus = NodeImageStatuses.Queued;
            entry.panoramaError = "";
            cacheService.Put(entry);
            queuedImageKeys.Add(entry.panoramaCacheKey);
            imageJobs.Add(new ImageGenerationJob
            {
                entry = entry,
                imageCacheKey = entry.panoramaCacheKey,
                purpose = AiImagePurpose.Panorama,
                priority = Mathf.Clamp(priority + (isImportantMoment ? 8 : 0), 0, 100),
                sequence = imageJobSequence++,
                reason = reason
            });
            StartImagePump();
        }

        private static bool IsImportantPanoramaMoment(NodeCacheEntry entry, string reason)
        {
            if (entry?.resultNode == null)
            {
                return false;
            }

            if (entry.resultNode.nodeKind == StoryNodeKind.Ending)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(reason)
                && (reason.StartsWith("choice_submit", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("drag_near_branch", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("cache_hit", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var eventTag = (entry.majorEventTag ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(eventTag)
                && eventTag != "branch"
                && eventTag != "choice"
                && eventTag != "mock"
                && eventTag != (entry.choiceId ?? "").Trim().ToLowerInvariant())
            {
                return true;
            }

            var delta = entry.statDelta ?? new PlayerStats(0, 0, 0, 0);
            return Mathf.Abs(delta.life) + Mathf.Abs(delta.force) + Mathf.Abs(delta.wealth) + Mathf.Abs(delta.faith) >= 18;
        }

        private static bool IsDistantPanoramaMoment(string reason)
        {
            return !string.IsNullOrWhiteSpace(reason)
                   && (reason.Contains("lookahead", StringComparison.OrdinalIgnoreCase)
                       || reason.Contains("idle", StringComparison.OrdinalIgnoreCase)
                       || reason.Contains("node_shown", StringComparison.OrdinalIgnoreCase));
        }

        private void StartImagePump()
        {
            statusText.text = BuildProviderStatus();
            if (imagePumpRunning)
            {
                return;
            }

            imagePumpRunning = true;
            _ = PumpImageQueueAsync(backgroundCancellation.Token);
        }

        private async Task PumpImageQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    while (runningImageJobs < RuntimeImageConcurrency && imageJobs.Count > 0)
                    {
                        var job = DequeueNextImageJob();
                        if (job == null)
                        {
                            break;
                        }

                        queuedImageKeys.Remove(job.imageCacheKey);
                        runningImageKeys.Add(job.imageCacheKey);
                        runningImageJobs++;
                        _ = RunImageJobAsync(job, cancellationToken);
                    }

                    statusText.text = BuildProviderStatus();
                    if (runningImageJobs == 0 && imageJobs.Count == 0)
                    {
                        break;
                    }

                    await Task.Delay(250, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                imagePumpRunning = false;
                if (!cancellationToken.IsCancellationRequested && imageJobs.Count > 0)
                {
                    StartImagePump();
                }
            }
        }

        private ImageGenerationJob DequeueNextImageJob()
        {
            if (imageJobs.Count == 0)
            {
                return null;
            }

            var bestIndex = 0;
            for (var i = 1; i < imageJobs.Count; i++)
            {
                var candidate = imageJobs[i];
                var best = imageJobs[bestIndex];
                if (candidate.priority > best.priority
                    || (candidate.priority == best.priority && candidate.sequence < best.sequence))
                {
                    bestIndex = i;
                }
            }

            var job = imageJobs[bestIndex];
            imageJobs.RemoveAt(bestIndex);
            return job;
        }

        private async Task RunImageJobAsync(ImageGenerationJob job, CancellationToken cancellationToken)
        {
            try
            {
                if (job?.entry == null)
                {
                    return;
                }

                var isPanorama = job.purpose == AiImagePurpose.Panorama;
                if (isPanorama ? AttachReadyPanorama(job.entry) : AttachReadyImage(job.entry))
                {
                    if (isPanorama && IsCurrentEntry(job.entry))
                    {
                        ApplyPanoramaBackground(job.entry);
                    }

                    return;
                }

                if (isPanorama)
                {
                    job.entry.panoramaStatus = NodeImageStatuses.Generating;
                    job.entry.panoramaError = "";
                }
                else
                {
                    job.entry.imageStatus = NodeImageStatuses.Generating;
                    job.entry.imageError = "";
                }

                cacheService.Put(job.entry);

                var imageResult = await imageService.GenerateImageAsync(
                    isPanorama ? ResolvePanoramaPrompt(job.entry) : ResolveImagePrompt(job.entry),
                    cancellationToken,
                    job.purpose);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var imagePath = cacheService.SaveImage(job.imageCacheKey, imageResult?.bytes);
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    if (isPanorama)
                    {
                        job.entry.panoramaPath = imagePath;
                        job.entry.panoramaStatus = NodeImageStatuses.Generated;
                        job.entry.panoramaError = "";
                    }
                    else
                    {
                        job.entry.imagePath = imagePath;
                        job.entry.imageStatus = NodeImageStatuses.Generated;
                        job.entry.imageError = "";
                        job.entry.resultNode.imageRef = imagePath;
                    }

                    cacheService.Put(job.entry);
                    if (isPanorama)
                    {
                        cacheService.ApplyGeneratedPanoramaToPanoramaKey(repository.StoryId, job.imageCacheKey, imagePath, NodeImageStatuses.Generated);
                        if (currentNodeCacheEntry != null
                            && string.Equals(currentNodeCacheEntry.cacheKey, job.entry.cacheKey, StringComparison.OrdinalIgnoreCase))
                        {
                            ApplyPanoramaBackground(job.entry);
                        }
                    }
                    else
                    {
                        cacheService.ApplyGeneratedImageToImageKey(repository.StoryId, job.imageCacheKey, imagePath, NodeImageStatuses.Generated);
                    }

                    return;
                }

                if (isPanorama)
                {
                    job.entry.panoramaStatus = NodeImageStatuses.Failed;
                    job.entry.panoramaError = CompactStatus(imageResult?.error ?? "Panorama generation returned no image bytes.");
                }
                else
                {
                    job.entry.imageStatus = NodeImageStatuses.Failed;
                    job.entry.imageError = CompactStatus(imageResult?.error ?? "Image generation returned no image bytes.");
                }

                cacheService.Put(job.entry);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (job?.entry != null)
                {
                    if (job.purpose == AiImagePurpose.Panorama)
                    {
                        job.entry.panoramaStatus = NodeImageStatuses.Failed;
                        job.entry.panoramaError = CompactStatus(ex.Message);
                    }
                    else
                    {
                        job.entry.imageStatus = NodeImageStatuses.Failed;
                        job.entry.imageError = CompactStatus(ex.Message);
                    }

                    cacheService.Put(job.entry);
                }
            }
            finally
            {
                if (job != null)
                {
                    runningImageKeys.Remove(job.imageCacheKey);
                }

                runningImageJobs = Mathf.Max(0, runningImageJobs - 1);
                statusText.text = BuildProviderStatus();
                if (!cancellationToken.IsCancellationRequested)
                {
                    ScheduleIdleImageBackfill();
                }
            }
        }

        private void ScheduleIdleImageBackfill()
        {
            if (!providerSettings.CanUseImage || (!providerSettings.enableRuntimeImages && !providerSettings.enableRuntimePanoramas))
            {
                return;
            }

            foreach (var entry in CurrentStoryCacheEntries()
                          .Where(entry => entry?.resultNode != null && entry.resultNode.nodeKind == StoryNodeKind.GeneratedBranch && !IsDraftEntry(entry))
                         .OrderByDescending(entry => string.Equals(NodeCacheStatuses.Normalize(entry.status), NodeCacheStatuses.Approved, StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(entry => HasGeneratedPortraitFallback(entry))
                         .Take(PanoramaBackfillWindow))
            {
                if (providerSettings.enableRuntimeImages)
                {
                    QueueImageForEntry(entry, HasGeneratedPortraitFallback(entry) ? 20 : 30, "idle_backfill", true);
                }

                QueuePanoramaForEntry(entry, 22, "idle_panorama_backfill", false);
            }
        }

        private bool AttachReadyImage(NodeCacheEntry entry)
        {
            if (entry == null || IsDraftEntry(entry))
            {
                return false;
            }

            EnsureImageCacheKey(entry);
            if (!string.IsNullOrWhiteSpace(entry.imagePath) && File.Exists(entry.imagePath))
            {
                if (entry.resultNode != null)
                {
                    entry.resultNode.imageRef = entry.imagePath;
                }

                return true;
            }

            if (!cacheService.TryFindGeneratedImagePath(repository.StoryId, entry.imageCacheKey, out var imagePath))
            {
                return false;
            }

            entry.imagePath = imagePath;
            entry.imageStatus = NodeImageStatuses.Reused;
            entry.imageError = "";
            if (entry.resultNode != null)
            {
                entry.resultNode.imageRef = imagePath;
            }

            cacheService.Put(entry);
            return true;
        }

        private bool AttachReadyPanorama(NodeCacheEntry entry)
        {
            if (entry == null || IsDraftEntry(entry))
            {
                return false;
            }

            EnsurePanoramaCacheKey(entry);
            if (!string.IsNullOrWhiteSpace(entry.panoramaPath) && File.Exists(entry.panoramaPath))
            {
                return true;
            }

            if (!cacheService.TryFindGeneratedPanoramaPath(repository.StoryId, entry.panoramaCacheKey, out var panoramaPath))
            {
                return false;
            }

            entry.panoramaPath = panoramaPath;
            entry.panoramaStatus = NodeImageStatuses.Reused;
            entry.panoramaError = "";
            cacheService.Put(entry);
            return true;
        }

        private void ApplyPanoramaBackground(NodeCacheEntry entry)
        {
            if (backgroundImage == null)
            {
                return;
            }

            if (entry != null)
            {
                if (AttachReadyPanorama(entry))
                {
                    backgroundImage.sprite = ResolveSprite(entry.panoramaPath);
                    backgroundImage.color = Color.white;
                    return;
                }
            }

            backgroundImage.sprite = defaultBackgroundSprite ?? MakeBackgroundSprite(1920, 1080);
            backgroundImage.color = Color.white;
        }

        private Sprite ResolveDefaultBackgroundSprite()
        {
            var graph = repository?.Graph;
            if (graph != null
                && graph.enableDefaultPanorama
                && !string.IsNullOrWhiteSpace(graph.defaultPanoramaPath)
                && File.Exists(graph.defaultPanoramaPath))
            {
                return ResolveSprite(graph.defaultPanoramaPath);
            }

            return MakeBackgroundSprite(1920, 1080);
        }

        private bool IsCurrentEntry(NodeCacheEntry entry)
        {
            return entry != null
                   && currentNodeCacheEntry != null
                   && string.Equals(currentNodeCacheEntry.cacheKey, entry.cacheKey, StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureImageCacheKey(NodeCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            entry.locationTag = FirstNonEmpty(entry.locationTag, entry.resultNode?.chapterId, entry.sourceNodeId);
            entry.moodTag = FirstNonEmpty(entry.moodTag, "branch");
            entry.majorEventTag = FirstNonEmpty(entry.majorEventTag, entry.choiceId, entry.resultNode?.title);
            var expectedKey = NodeCacheService.CreateImageCacheKey(repository.StoryId, entry.resultNode?.chapterId, entry.locationTag, entry.moodTag, entry.majorEventTag);
            if (string.Equals(entry.imageCacheKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previousPath = entry.imagePath;
            entry.imageCacheKey = expectedKey;
            entry.imagePath = "";
            entry.imageStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable;
            entry.imageError = "";
            if (entry.resultNode != null
                && !string.IsNullOrWhiteSpace(previousPath)
                && string.Equals(entry.resultNode.imageRef, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                entry.resultNode.imageRef = "branch";
            }
        }

        private void EnsurePanoramaCacheKey(NodeCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            entry.locationTag = FirstNonEmpty(entry.locationTag, entry.resultNode?.chapterId, entry.sourceNodeId);
            entry.moodTag = FirstNonEmpty(entry.moodTag, "branch");
            entry.majorEventTag = FirstNonEmpty(entry.majorEventTag, entry.choiceId, entry.resultNode?.title);
            var expectedKey = NodeCacheService.CreatePanoramaCacheKey(repository.StoryId, entry.resultNode?.chapterId, entry.locationTag, entry.moodTag, entry.majorEventTag);
            if (string.Equals(entry.panoramaCacheKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            entry.panoramaCacheKey = expectedKey;
            entry.panoramaPath = "";
            entry.panoramaStatus = providerSettings.CanUseImage ? NodeImageStatuses.SkippedByPolicy : NodeImageStatuses.SkippedUnavailable;
            entry.panoramaError = "";
        }

        private bool HasGeneratedPortraitFallback(NodeCacheEntry entry)
        {
            if (entry?.resultNode == null)
            {
                return false;
            }

            var portraitRef = portraitService?.ResolveImageRef(entry.resultNode);
            return !string.IsNullOrWhiteSpace(portraitRef) && File.Exists(portraitRef);
        }

        private async Task<NodeCacheEntry> AwaitBranchEntryOrDraftAsync(
            Task<NodeCacheEntry> task,
            StoryNode sourceNode,
            ChoiceOption choice,
            StoryNode naturalNext,
            string cacheKey,
            Task firstStoryTextTask,
            CancellationToken cancellationToken)
        {
            if (task == null)
            {
                return CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
            }

            try
            {
                if (task.IsCompleted)
                {
                    return await ResolveBranchTaskOrDraftAsync(task, sourceNode, choice, naturalNext, cacheKey, cancellationToken);
                }

                if (firstStoryTextTask != null)
                {
                    var firstTextWindow = Task.Delay(BranchStreamingFirstTextWindowMs, cancellationToken);
                    var completed = await Task.WhenAny(task, firstStoryTextTask, firstTextWindow);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (completed == task)
                    {
                        return await ResolveBranchTaskOrDraftAsync(task, sourceNode, choice, naturalNext, cacheKey, cancellationToken);
                    }

                    if (completed == firstStoryTextTask)
                    {
                        Debug.Log("AI Builder branch text started streaming; waiting for generated AI content instead of showing a mock draft.");
                        return await ResolveBranchTaskOrDraftAsync(task, sourceNode, choice, naturalNext, cacheKey, cancellationToken);
                    }
                }

                Debug.Log("AI Builder branch text has not started streaming yet; showing a draft branch now and caching the generated result when it finishes.");
                return CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder branch generation failed before display; using draft fallback: {ex.Message}");
                return CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
            }
        }

        private async Task<NodeCacheEntry> ResolveBranchTaskOrDraftAsync(
            Task<NodeCacheEntry> task,
            StoryNode sourceNode,
            ChoiceOption choice,
            StoryNode naturalNext,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            try
            {
                var entry = await task;
                return entry ?? CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder branch task failed; using draft fallback: {ex.Message}");
                return CreateAndCacheDraftEntry(sourceNode, choice, naturalNext, cacheKey);
            }
        }

        private NodeCacheEntry CreateAndCacheDraftEntry(StoryNode sourceNode, ChoiceOption choice, StoryNode naturalNext, string cacheKey)
        {
            if (sourceNode == null || choice == null || string.IsNullOrWhiteSpace(cacheKey))
            {
                return null;
            }

            if (cacheService.TryGet(cacheKey, out var cached) && !IsDraftEntry(cached))
            {
                return cached;
            }

            var entry = CreateDraftCacheEntry(sourceNode, choice, naturalNext, cacheKey);
            cacheService.Put(entry);
            return entry;
        }

        private IReadOnlyList<NodeCacheEntry> CurrentStoryCacheEntries()
        {
            return cacheService?.EntriesForStory(repository?.StoryId) ?? Array.Empty<NodeCacheEntry>();
        }

        private string ShortStoryId()
        {
            var storyId = repository?.StoryId ?? "story";
            return storyId.Length <= 18 ? storyId : storyId.Substring(0, 18);
        }

        private static string ResolveImagePrompt(NodeCacheEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.imagePrompt))
            {
                return entry.imagePrompt;
            }

            var node = entry?.resultNode;
            return node == null
                ? "generated medieval branch card, symbolic court omen"
                : $"symbolic medieval branch card, {node.title}, {node.body}";
        }

        private static string ResolvePanoramaPrompt(NodeCacheEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.panoramaPrompt))
            {
                return entry.panoramaPrompt;
            }

            var node = entry?.resultNode;
            if (node == null)
            {
                return "wide medieval kingdom panorama, distant harbor or court, moody horizon";
            }

            return $"wide establishing background panorama, location {entry.locationTag}, mood {entry.moodTag}, event {entry.majorEventTag}, {node.title}, {node.body}, distant horizon, layered low-poly depth, no close-up characters";
        }

        private static string FindSummaryTag(IEnumerable<string> tags, string prefix)
        {
            if (tags == null)
            {
                return "";
            }

            var marker = prefix + ":";
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var trimmed = tag.Trim();
                if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(marker.Length).Trim();
                }
            }

            return "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }

        private static string CompactStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return compact.Length <= 80 ? compact : compact.Substring(0, 80) + "...";
        }

        private StoryNode CreateBranchNode(StoryNode sourceNode, AiTextResult aiResult, ChoiceOption sourceChoice, StoryNode nextMainline)
        {
            var nextId = nextMainline == null ? "" : nextMainline.id;
            var safeLeft = string.IsNullOrWhiteSpace(aiResult.leftChoice) ? "追问真相" : aiResult.leftChoice;
            var safeRight = string.IsNullOrWhiteSpace(aiResult.rightChoice) ? "回到王座" : aiResult.rightChoice;

            return new StoryNode
            {
                id = $"branch_{sourceNode.id}_{sourceChoice.id}_{Mathf.Abs(DateTime.UtcNow.GetHashCode())}",
                chapterId = sourceNode.chapterId,
                title = sourceChoice.label,
                body = string.IsNullOrWhiteSpace(aiResult.storyText) ? "宫廷记录员沉默片刻，将这一页留给后来者补写。" : aiResult.storyText,
                imageRef = "branch",
                nodeKind = nextMainline == null ? StoryNodeKind.Ending : StoryNodeKind.GeneratedBranch,
                mainlineIndex = nextMainline == null ? sourceNode.mainlineIndex + 1 : nextMainline.mainlineIndex - 1,
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

        private StoryNode CreateDraftBranchNodeLegacy(StoryNode sourceNode, ChoiceOption sourceChoice, StoryNode nextMainline, string cacheKey)
        {
            var nextId = nextMainline == null ? "" : nextMainline.id;
            var title = string.IsNullOrWhiteSpace(sourceChoice?.label) ? "临时分支" : sourceChoice.label;
            var body = $"你立刻执行「{title}」。局势偏离主线，但记录员已经留出回旋余地，后续细节会在暗处补全。";

            return new StoryNode
            {
                id = $"branch_{sourceNode.id}_{sourceChoice.id}_{ImageGenerationPolicy.StableBucket(cacheKey):0000}",
                chapterId = sourceNode.chapterId,
                title = title,
                body = body,
                imageRef = "branch",
                nodeKind = nextMainline == null ? StoryNodeKind.Ending : StoryNodeKind.GeneratedBranch,
                mainlineIndex = nextMainline == null ? sourceNode.mainlineIndex + 1 : nextMainline.mainlineIndex - 1,
                leftChoice = new ChoiceOption
                {
                    id = "branch_left",
                    label = "继续追问",
                    intent = "继续探索分支后回到主线。",
                    direction = "left",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(0, 0, 0, 0)
                },
                rightChoice = new ChoiceOption
                {
                    id = "branch_right",
                    label = "回到主线",
                    intent = "收束分支后回到主线。",
                    direction = "right",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(0, 0, 0, 0)
                }
            };
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

        private void ShowChapterComplete(ChoiceOption choice, string feedback)
        {
            ShowOverlay("第一章完成", $"你选择了「{choice.label}」。灰冠初夜结束，新的主干章节将从这里展开。");
            cacheText.text = BuildChoiceStatus("Chapter Complete", feedback);
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

        private Sprite ResolveNodeSprite(StoryNode node)
        {
            if (node == null)
            {
                return ResolveSprite("");
            }

            if (!string.IsNullOrWhiteSpace(node.imageRef) && File.Exists(node.imageRef))
            {
                return ResolveSprite(node.imageRef);
            }

            var portraitRef = portraitService?.ResolveImageRef(node);
            if (!string.IsNullOrWhiteSpace(portraitRef))
            {
                return ResolveSprite(portraitRef);
            }

            return ResolveSprite(node.imageRef);
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

        private static void ApplyMaterial(Image image, Material material)
        {
            if (image != null && material != null)
            {
                image.material = material;
            }
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

        private static void RemoveTypographyEffects(Text text)
        {
            if (text == null)
            {
                return;
            }

            foreach (var effect in text.GetComponents<Shadow>())
            {
                if (Application.isPlaying)
                {
                    Destroy(effect);
                }
                else
                {
                    DestroyImmediate(effect);
                }
            }
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

        private static Sprite MakeStatIconSprite(string icon)
        {
            var resourceName = icon == LifeIcon
                ? "Icons/Lucide/heart"
                : icon == ForceIcon
                    ? "Icons/Lucide/swords"
                    : icon == WealthIcon
                        ? "Icons/Lucide/coins"
                        : "Icons/Lucide/cross";
            var texture = Resources.Load<Texture2D>(resourceName);
            if (texture != null)
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            }

            return MakeSolidSprite(96, 96, new Color32(0, 0, 0, 0));
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

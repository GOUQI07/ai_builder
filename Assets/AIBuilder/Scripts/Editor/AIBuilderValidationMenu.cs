#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public static class AIBuilderValidationMenu
    {
        [MenuItem("AI Builder/Run Local Validation")]
        public static void Run()
        {
            var graph = StoryRepository.LoadGraph();
            Require(graph.nodes.Count >= 3, "Mainline graph has at least 3 nodes.");
            Require(graph.nodes[0].leftChoice != null && graph.nodes[0].rightChoice != null, "First node has two choices.");

            var stats = new PlayerStats();
            stats.Apply(new PlayerStats(99, -99, 99, -99));
            Require(stats.life == 85 && stats.force == 35 && stats.wealth == 65 && stats.faith == 35, "Stat deltas clamp to +/-15.");
            var aiJudgementDelta = PlayerStats.ClampDelta(new PlayerStats(-150, 70, 8, -100), 100);
            Require(aiJudgementDelta.life == -100 && aiJudgementDelta.force == 70 && aiJudgementDelta.wealth == 8 && aiJudgementDelta.faith == -100, "AI stat judgement can express severe but bounded consequences.");
            var catastrophicStats = new PlayerStats(100, 50, 50, 50);
            catastrophicStats.Apply(new PlayerStats(-100, 0, 0, 0), 100);
            Require(catastrophicStats.life == 0 && catastrophicStats.IsGameOver(), "AI stat judgement can apply catastrophic consequences when justified.");
            var localFallbackDelta = PlayerStats.ClampDelta(new PlayerStats(99, -99, 8, -8), 3);
            Require(localFallbackDelta.life == 3 && localFallbackDelta.force == -3 && localFallbackDelta.wealth == 3 && localFallbackDelta.faith == -3, "Local fallback stat judgement remains conservative.");
            Require(new PlayerStats(0, 50, 50, 50).IsGameOver()
                    && new PlayerStats(50, 0, 50, 50).IsGameOver() == false
                    && new PlayerStats(50, 50, 0, 50).IsGameOver()
                    && new PlayerStats(50, 50, 50, 0).IsGameOver(),
                "Life, wealth, and faith can end the game; force alone cannot.");

            var keyA = NodeCacheService.CreateCacheKey("story_a", graph.nodes[0], graph.nodes[0].rightChoice, new PlayerStats());
            var keyB = NodeCacheService.CreateCacheKey("story_a", graph.nodes[0], graph.nodes[0].rightChoice, new PlayerStats());
            Require(keyA == keyB, "Cache key is stable for identical state.");
            var otherStoryKey = NodeCacheService.CreateCacheKey("story_b", graph.nodes[0], graph.nodes[0].rightChoice, new PlayerStats());
            Require(keyA != otherStoryKey, "Cache key is isolated per story id.");
            Require(NodeCacheStatuses.Normalize("") == NodeCacheStatuses.PendingReview, "Blank cache status normalizes to pending review.");
            Require(NodeCacheStatuses.IsRejected(NodeCacheStatuses.Rejected), "Rejected cache entries are marked as non-reusable.");
            Require(NodeImageStatuses.Generating == "Generating", "Generated branches can record image generation in progress.");
            Require(NodeImageStatuses.Queued == "Queued" && NodeImageStatuses.Reused == "Reused", "Runtime image jobs can queue and reuse coarse cached images.");
            Require(NodeTextStatuses.Draft == "Draft" && NodeTextStatuses.Generating == "Generating" && NodeTextStatuses.Generated == "Generated", "Runtime can display draft text while generated text continues into cache.");
            Require(new NodeCacheEntry { storyId = "story_a" }.storyId == "story_a", "Cache entries record their story id.");
            Require(new NodeCacheEntry { imagePrompt = "prompt" }.imagePrompt == "prompt", "Cache entries can keep image prompts for later backfill.");
            Require(new NodeCacheEntry { panoramaPrompt = "wide prompt" }.panoramaPrompt == "wide prompt", "Cache entries can keep panorama prompts for immersive backfill.");
            var imageKeyA = NodeCacheService.CreateImageCacheKey("story_a", "ch001", "harbor", "tense", "trial");
            var imageKeyB = NodeCacheService.CreateImageCacheKey("story_a", "ch001", "harbor", "tense", "trial");
            var imageKeyC = NodeCacheService.CreateImageCacheKey("story_a", "ch001", "harbor", "calm", "trial");
            Require(imageKeyA == imageKeyB && imageKeyA != imageKeyC, "Coarse image cache key is stable and uses semantic tags.");
            var panoramaKeyA = NodeCacheService.CreatePanoramaCacheKey("story_a", "ch001", "harbor", "tense", "trial");
            var panoramaKeyB = NodeCacheService.CreatePanoramaCacheKey("story_a", "ch001", "harbor", "tense", "trial");
            Require(panoramaKeyA == panoramaKeyB && panoramaKeyA != imageKeyA, "Panorama cache key is stable and separate from card images.");
            Require(new NodeCacheEntry { imageError = "timeout" }.imageError == "timeout", "Cache entries can keep image failure diagnostics.");
            Require(new NodeCacheEntry { panoramaError = "timeout" }.panoramaError == "timeout", "Cache entries can keep panorama failure diagnostics.");
            Require(AiImageResult.Failed("api error").error == "api error", "Image service failures carry a readable reason.");
            Require(AiProviderFactory.CreateTextService(new AiProviderSettings { providerType = AiProviderTypes.OpenAiCompatible }) is OpenAiCompatibleTextService, "Provider factory creates OpenAI-compatible text service.");
            Require(AiProviderFactory.CreateTextService(new AiProviderSettings { providerType = AiProviderTypes.Mock }) is MockAiTextService, "Provider factory creates mock text service.");
            Require(AiProviderFactory.CreateTextService(new AiProviderSettings { providerType = AiProviderTypes.Mock }) is IAiStatJudgementService, "Text providers can supply periodic stat judgement.");
            Require(AiProviderFactory.CreateImageService(new AiProviderSettings { providerType = "unknown_provider" }) is MockAiImageService, "Unknown provider falls back to mock image service.");
            Require(AiModelNameHints.IsLikelyImageOnlyModel("gpt-image-2"), "Image-only model names are not treated as text-ready.");
            Require(Mathf.Approximately(ImageGenerationPolicy.ClampRatio(-1f), 0f) && Mathf.Approximately(ImageGenerationPolicy.ClampRatio(2f), 1f), "Image ratio clamps to 0..1.");
            var timeoutSettings = new AiProviderSettings { timeoutSeconds = 999, imageTimeoutSeconds = 1 };
            timeoutSettings.NormalizeInPlace();
            Require(timeoutSettings.timeoutSeconds == 120 && timeoutSettings.imageTimeoutSeconds == 8, "Timeouts clamp to runtime-safe ranges.");
            var imageEndpointSettings = new AiProviderSettings { imageBaseUrl = " https://image.example/v1/ ", imageEndpoint = "/images/generations/" };
            imageEndpointSettings.NormalizeInPlace();
            Require(imageEndpointSettings.imageBaseUrl == "https://image.example/v1/" && imageEndpointSettings.imageEndpoint == "images/generations", "Image endpoint settings normalize independently from text base URL.");
            var fastImageSettings = new AiProviderSettings { imageSize = "", imageResolution = "", imageQuality = "", imageOutputFormat = "", panoramaImageSize = "1:1", imageCount = 99, imagePollIntervalSeconds = 1 };
            fastImageSettings.NormalizeInPlace();
            Require(fastImageSettings.imageSize == "1:1"
                    && fastImageSettings.panoramaImageSize == "16:9"
                    && fastImageSettings.imageResolution == "1k"
                    && fastImageSettings.imageQuality == "low"
                    && fastImageSettings.imageOutputFormat == "png"
                    && fastImageSettings.imageCount == 4
                    && fastImageSettings.imagePollIntervalSeconds == 2,
                "Fast image defaults and bounds normalize safely.");
            Require(AiProviderFactory.CreateImageService(new AiProviderSettings
            {
                providerType = AiProviderTypes.OpenAiCompatible,
                imageEndpointUrl = "https://image.example/v1/images/generations"
            }) is OpenAiCompatibleImageService, "OpenAI-compatible image service accepts dedicated image endpoint config.");
            Require(ImageGenerationPolicy.ShouldGenerate(true, true, true, 0f, new List<NodeCacheEntry>(), keyA), "Guaranteed first generated image policy hits before ratio.");
            var imageCache = new List<NodeCacheEntry> { new NodeCacheEntry { imagePath = "generated.png" } };
            var stableA = ImageGenerationPolicy.ShouldGenerate(true, true, false, 0.3f, imageCache, keyA);
            var stableB = ImageGenerationPolicy.ShouldGenerate(true, true, false, 0.3f, imageCache, keyA);
            Require(stableA == stableB, "Image ratio policy is deterministic for a fixed cache key.");
            var panoramaStableA = PanoramaGenerationPolicy.ShouldGenerate(true, true, false, 0.15f, imageCache, panoramaKeyA, true, false);
            var panoramaStableB = PanoramaGenerationPolicy.ShouldGenerate(true, true, false, 0.15f, imageCache, panoramaKeyA, true, false);
            Require(panoramaStableA == panoramaStableB, "Panorama ratio policy is deterministic for a fixed cache key.");
            var storyGraphA = new StoryGraph
            {
                chapterId = "story_project",
                chapterTitle = "Validation",
                nodes = new List<StoryNode> { new StoryNode { id = "n001", title = "Same Id", body = "First story body.", mainlineIndex = 1 } }
            };
            var storyGraphB = new StoryGraph
            {
                chapterId = "story_project",
                chapterTitle = "Validation",
                nodes = new List<StoryNode> { new StoryNode { id = "n001", title = "Same Id", body = "Second story body.", mainlineIndex = 1 } }
            };
            Require(StoryRepository.CreateStoryCacheId(storyGraphA) != StoryRepository.CreateStoryCacheId(storyGraphB), "Story cache id changes when exported story content changes.");
            var authoringProject = StoryAuthoringUtility.CreateDefaultProject();
            StoryAuthoringUtility.ImportSourceText(authoringProject, new string('A', 2500), "validation.txt", 1000);
            Require(authoringProject.sourceChunks.Count >= 2, "Story authoring import splits long source text into stable chunks.");
            Require(authoringProject.projectId.StartsWith("validation_txt_", System.StringComparison.OrdinalIgnoreCase), "Story authoring import assigns a source-derived story id.");
            authoringProject.targetChapterCount = 999;
            authoringProject.minAnchorsPerChapter = -1;
            authoringProject.maxAnchorsPerChapter = 99;
            StoryAuthoringUtility.NormalizeProject(authoringProject);
            Require(authoringProject.targetChapterCount == StoryAuthoringUtility.MaxChapterCount, "Story authoring target chapters clamp to safe range.");
            Require(authoringProject.minAnchorsPerChapter == StoryAuthoringUtility.MinAnchorCount && authoringProject.maxAnchorsPerChapter == StoryAuthoringUtility.MaxAnchorCount, "Story authoring anchor counts clamp to safe range.");
            authoringProject.minAnchorsPerChapter = 2;
            authoringProject.maxAnchorsPerChapter = 3;
            authoringProject.chapters = new List<StoryChapter>
            {
                new StoryChapter
                {
                    chapterId = "ch001",
                    chapterIndex = 1,
                    chapterTitle = "Validation Chapter",
                    summary = "Validation summary.",
                    status = StoryAuthoringStatuses.Approved,
                    anchors = new List<StoryAnchorNode>
                    {
                        new StoryAnchorNode
                        {
                            nodeId = "ch001_anchor001",
                            anchorIndex = 1,
                            title = "Anchor One",
                            body = "Anchor one body.",
                            status = StoryAuthoringStatuses.Approved,
                            mainlineChoice = "left",
                            leftChoice = StoryAuthoringUtility.NewChoice("left"),
                            rightChoice = StoryAuthoringUtility.NewChoice("right")
                        },
                        new StoryAnchorNode
                        {
                            nodeId = "ch001_anchor002",
                            anchorIndex = 2,
                            title = "Anchor Two",
                            body = "Anchor two body.",
                            status = StoryAuthoringStatuses.Approved,
                            mainlineChoice = "left",
                            leftChoice = StoryAuthoringUtility.NewChoice("left"),
                            rightChoice = StoryAuthoringUtility.NewChoice("right")
                        }
                    }
                }
            };
            StoryAuthoringUtility.NormalizeProject(authoringProject);
            var authoringValidation = StoryAuthoringUtility.ValidateProject(authoringProject);
            Require(authoringValidation.IsValid, "Story authoring project validates with two approved anchors.");
            var exportedGraph = StoryAuthoringUtility.ExportToStoryGraph(authoringProject);
            var exportedJson = JsonConvert.SerializeObject(exportedGraph);
            var roundTrippedGraph = JsonConvert.DeserializeObject<StoryGraph>(exportedJson);
            Require(roundTrippedGraph != null && roundTrippedGraph.nodes.Count == 2, "Story authoring export creates readable runtime StoryGraph nodes.");
            Require(roundTrippedGraph.nodes[0].leftChoice != null && roundTrippedGraph.nodes[0].leftChoice.nextMainlineNodeId == "ch001_anchor002", "Story authoring export links the required mainline path.");
            var portraitProject = StoryAuthoringUtility.CreateDefaultProject();
            portraitProject.summaries.Add(new SourceChunkSummary
            {
                chunkId = "chunk_001",
                chunkIndex = 1,
                characters = new List<string>
                {
                    "Hero/Alias: A decisive lead with a visible scar.",
                    "Bishop: A faith leader who blocks forbidden magic."
                }
            });
            var portraitDatabase = AIBuilderPortraitPresetMenu.BuildPresetDatabaseFromStoryProject(portraitProject, new CharacterPortraitPresetDatabase());
            Require(portraitDatabase.presets.Count == 2, "Story importer portrait builder creates presets from summarized characters.");
            Require(portraitDatabase.presets.Any(preset => preset.aliases.Contains("Alias")), "Story importer portrait builder preserves character aliases.");
            var existingPortraitDatabase = new CharacterPortraitPresetDatabase
            {
                presets = new List<CharacterPortraitPreset>
                {
                    new CharacterPortraitPreset
                    {
                        id = "bishop",
                        displayName = "Bishop",
                        aliases = new List<string> { "Bishop" },
                        imageRef = "existing.png",
                        imagePrompt = "existing bishop portrait prompt",
                        spriteKey = "oracle",
                        priority = 40
                    }
                }
            };
            var mergedPortraitDatabase = AIBuilderPortraitPresetMenu.BuildPresetDatabaseFromStoryProject(portraitProject, existingPortraitDatabase);
            Require(mergedPortraitDatabase.presets.Any(preset => preset.id == "bishop" && preset.imagePrompt == "existing bishop portrait prompt"), "Story importer portrait builder preserves existing preset ids and prompt data.");
            if (File.Exists(AIBuilderPortraitPresetMenu.PresetsAssetPath))
            {
                var installedPortraits = JsonConvert.DeserializeObject<CharacterPortraitPresetDatabase>(File.ReadAllText(AIBuilderPortraitPresetMenu.PresetsAssetPath)) ?? new CharacterPortraitPresetDatabase();
                var samplePreset = installedPortraits.presets.FirstOrDefault(preset => preset?.aliases != null && preset.aliases.Any(alias => !string.IsNullOrWhiteSpace(alias)));
                if (samplePreset != null)
                {
                    var sampleAlias = samplePreset.aliases.First(alias => !string.IsNullOrWhiteSpace(alias));
                    var portraitService = new CharacterPortraitService();
                    Require(portraitService.Match(new StoryNode { title = sampleAlias, body = $"Current story mentions {sampleAlias}." })?.id == samplePreset.id, "Portrait presets match current story aliases from node text.");
                }
            }

            var exampleConfig = "Assets/AIBuilder/Data/ai_provider.example.json";
            Require(File.Exists(exampleConfig), "Provider example config exists.");
            Require(!File.ReadAllText(exampleConfig).Contains("s" + "k-"), "Provider example config contains no API key.");

            Debug.Log("<b>AI Builder</b>: Local validation completed.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                Debug.LogError($"AI Builder validation failed: {message}");
                return;
            }

            Debug.Log($"AI Builder validation passed: {message}");
        }
    }
}
#endif

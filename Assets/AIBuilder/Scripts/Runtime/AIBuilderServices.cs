using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AIBuilder
{
    public interface IAiTextService
    {
        Task<AiTextResult> GenerateNextNodeAsync(StoryNode context, ChoiceOption choice, PlayerStats stats, CancellationToken cancellationToken);
    }

    public interface IAiStreamingTextService : IAiTextService
    {
        Task<AiTextResult> GenerateNextNodeStreamingAsync(
            StoryNode context,
            ChoiceOption choice,
            PlayerStats stats,
            Action<string> onStoryText,
            CancellationToken cancellationToken);
    }

    public interface IAiImageService
    {
        Task<AiImageResult> GenerateImageAsync(string prompt, CancellationToken cancellationToken, AiImagePurpose purpose = AiImagePurpose.Card);
    }

    public interface IAiStatJudgementService
    {
        Task<StatJudgementResult> JudgeStatsAsync(
            StoryNode context,
            ChoiceOption choice,
            PlayerStats stats,
            IReadOnlyList<string> recentEvents,
            CancellationToken cancellationToken);
    }

    public static class AiProviderFactory
    {
        private sealed class ProviderRegistration
        {
            public ProviderRegistration(Func<AiProviderSettings, IAiTextService> textFactory, Func<AiProviderSettings, IAiImageService> imageFactory)
            {
                TextFactory = textFactory;
                ImageFactory = imageFactory;
            }

            public Func<AiProviderSettings, IAiTextService> TextFactory { get; }
            public Func<AiProviderSettings, IAiImageService> ImageFactory { get; }
        }

        private static readonly Dictionary<string, ProviderRegistration> Providers =
            new Dictionary<string, ProviderRegistration>(StringComparer.OrdinalIgnoreCase)
            {
                [AiProviderTypes.OpenAiCompatible] = new ProviderRegistration(
                    settings => new OpenAiCompatibleTextService(settings),
                    settings => new OpenAiCompatibleImageService(settings)),
                [AiProviderTypes.Mock] = new ProviderRegistration(
                    _ => new MockAiTextService(),
                    _ => new MockAiImageService())
            };

        public static IAiTextService CreateTextService(AiProviderSettings settings)
        {
            settings ??= new AiProviderSettings();
            return Resolve(settings, "text").TextFactory(settings);
        }

        public static IAiImageService CreateImageService(AiProviderSettings settings)
        {
            settings ??= new AiProviderSettings();
            return Resolve(settings, "image").ImageFactory(settings);
        }

        private static ProviderRegistration Resolve(AiProviderSettings settings, string serviceKind)
        {
            if (!AiProviderTypes.IsKnown(settings.providerType))
            {
                Debug.LogWarning($"AI Builder unknown provider '{settings.providerType}' falls back to mock {serviceKind}.");
                return Providers[AiProviderTypes.Mock];
            }

            var providerType = settings.NormalizedProviderType;
            if (Providers.TryGetValue(providerType, out var provider))
            {
                return provider;
            }

            Debug.LogWarning($"AI Builder provider '{providerType}' is not registered; falling back to mock {serviceKind}.");
            return Providers[AiProviderTypes.Mock];
        }
    }

    public static class ImageGenerationPolicy
    {
        private const int HashBucketCount = 10000;

        public static bool ShouldGenerate(AiProviderSettings settings, IReadOnlyList<NodeCacheEntry> cacheEntries, string cacheKey)
        {
            if (settings == null)
            {
                return false;
            }

            return ShouldGenerate(
                settings.enableRuntimeImages,
                settings.CanUseImage,
                settings.guaranteeFirstGeneratedImage,
                settings.imageGenerationRatio,
                cacheEntries,
                cacheKey);
        }

        public static bool ShouldGenerate(
            bool enableRuntimeImages,
            bool canUseImageProvider,
            bool guaranteeFirstGeneratedImage,
            float imageGenerationRatio,
            IReadOnlyList<NodeCacheEntry> cacheEntries,
            string cacheKey)
        {
            if (!enableRuntimeImages || !canUseImageProvider)
            {
                return false;
            }

            if (guaranteeFirstGeneratedImage && !HasSuccessfulGeneratedImage(cacheEntries))
            {
                return true;
            }

            var ratio = ClampRatio(imageGenerationRatio);
            if (ratio <= 0f)
            {
                return false;
            }

            if (ratio >= 1f)
            {
                return true;
            }

            return StableUnitInterval(cacheKey) < ratio;
        }

        public static float ClampRatio(float ratio)
        {
            return Mathf.Clamp01(float.IsNaN(ratio) ? 0f : ratio);
        }

        public static bool HasSuccessfulGeneratedImage(IReadOnlyList<NodeCacheEntry> cacheEntries)
        {
            return cacheEntries != null && cacheEntries.Any(entry =>
                entry != null
                && !NodeCacheStatuses.IsRejected(entry.status)
                && !string.IsNullOrWhiteSpace(entry.imagePath)
                && (string.IsNullOrWhiteSpace(entry.imageStatus)
                    || string.Equals(entry.imageStatus, NodeImageStatuses.Generated, StringComparison.OrdinalIgnoreCase)));
        }

        public static float StableUnitInterval(string cacheKey)
        {
            return StableBucket(cacheKey) / (float)HashBucketCount;
        }

        public static int StableBucket(string cacheKey)
        {
            unchecked
            {
                var hash = 2166136261u;
                var value = string.IsNullOrWhiteSpace(cacheKey) ? "none" : cacheKey;
                for (var i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619u;
                }

                return (int)(hash % HashBucketCount);
            }
        }
    }

    public static class PanoramaGenerationPolicy
    {
        public static bool ShouldGenerate(
            AiProviderSettings settings,
            IReadOnlyList<NodeCacheEntry> cacheEntries,
            string cacheKey,
            bool isImportantMoment,
            bool isDistantMoment)
        {
            if (settings == null)
            {
                return false;
            }

            return ShouldGenerate(
                settings.enableRuntimePanoramas,
                settings.CanUseImage,
                settings.guaranteeFirstGeneratedPanorama,
                settings.panoramaGenerationRatio,
                cacheEntries,
                cacheKey,
                isImportantMoment,
                isDistantMoment);
        }

        public static bool ShouldGenerate(
            bool enableRuntimePanoramas,
            bool canUseImageProvider,
            bool guaranteeFirstGeneratedPanorama,
            float panoramaGenerationRatio,
            IReadOnlyList<NodeCacheEntry> cacheEntries,
            string cacheKey,
            bool isImportantMoment,
            bool isDistantMoment)
        {
            if (!enableRuntimePanoramas || !canUseImageProvider)
            {
                return false;
            }

            if (guaranteeFirstGeneratedPanorama && !HasSuccessfulGeneratedPanorama(cacheEntries))
            {
                return true;
            }

            var ratio = ImageGenerationPolicy.ClampRatio(panoramaGenerationRatio);
            if (ratio <= 0f)
            {
                return false;
            }

            if (isImportantMoment)
            {
                ratio = Mathf.Clamp01(ratio + 0.32f);
            }
            else if (isDistantMoment)
            {
                ratio = Mathf.Clamp01(ratio + 0.18f);
            }

            return ImageGenerationPolicy.StableUnitInterval(cacheKey) < ratio;
        }

        public static bool HasSuccessfulGeneratedPanorama(IReadOnlyList<NodeCacheEntry> cacheEntries)
        {
            return cacheEntries != null && cacheEntries.Any(entry =>
                entry != null
                && !NodeCacheStatuses.IsRejected(entry.status)
                && !string.IsNullOrWhiteSpace(entry.panoramaPath)
                && (string.IsNullOrWhiteSpace(entry.panoramaStatus)
                    || string.Equals(entry.panoramaStatus, NodeImageStatuses.Generated, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public static class AiBuilderImagePromptStyle
    {
        private const string SharedUiStyle =
            "Designed to blend into a royal decision-card game UI: flat medieval silhouettes, faceted low-poly geometry, limited muted palette of parchment, oxblood red, dull gold, charcoal, and desaturated teal, matte paper texture, hard readable shapes, restrained contrast.";

        private const string SharedAvoid =
            "No photorealism, no glossy 3D render, no anime, no modern concept-art polish, no cinematic lens flare, no soft bokeh, no painterly brushwork, no hyper-detailed AI look, no text, no letters, no captions, no UI, no logo.";

        public static string BuildCardPrompt(string details)
        {
            return "Create one square low-poly card illustration. Compact symbolic composition, centered readable subject, simple faceted planes, strong silhouettes, sparse background, board-game card asset. "
                   + SharedUiStyle + " " + Clean(details) + " " + SharedAvoid;
        }

        public static string BuildPanoramaPrompt(string details)
        {
            return "Create one wide 16:9 refined low-poly establishing background panorama. Broad horizontal composition, layered depth, distant horizon, faceted terrain and architecture, atmospheric but subdued so foreground UI remains readable. "
                   + SharedUiStyle + " " + Clean(details) + " " + SharedAvoid;
        }

        public static string BuildPortraitPrompt(string details)
        {
            return "Create one square low-poly character portrait for the same royal decision-card UI. Waist-up bust, centered face and shoulders, icon-like silhouette, faceted planes, minimal background, no realistic skin rendering. "
                   + SharedUiStyle + " " + Clean(details) + " " + SharedAvoid;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "dark medieval court scene";
            }

            var cleaned = value.Trim();
            foreach (var bannedPhrase in new[]
                     {
                         "cinematic noir lighting",
                         "cinematic dark fantasy",
                         "cinematic political fantasy",
                         "cinematic visual novel style",
                         "cinematic lighting",
                         "dramatic cinematic composition",
                         "dramatic lighting",
                         "muted cinematic lighting",
                         "visual novel art",
                         "fantasy realism",
                         "photorealistic",
                         "realistic"
                     })
            {
                cleaned = cleaned.Replace(bannedPhrase, "", StringComparison.OrdinalIgnoreCase);
            }

            return string.IsNullOrWhiteSpace(cleaned) ? "dark medieval court scene" : cleaned.Trim();
        }
    }

    public sealed class StoryRepository
    {
        private readonly Dictionary<string, StoryNode> nodesById = new Dictionary<string, StoryNode>();

        public StoryGraph Graph { get; private set; }
        public string StoryId { get; private set; }

        public StoryRepository()
        {
            Graph = LoadGraph();
            StoryId = CreateStoryCacheId(Graph);
            nodesById.Clear();
            foreach (var node in Graph.nodes.Where(node => node != null && !string.IsNullOrWhiteSpace(node.id)))
            {
                nodesById[node.id] = node;
            }
        }

        public StoryNode FirstNode()
        {
            return Graph.nodes.OrderBy(node => node.mainlineIndex).FirstOrDefault(node => node.nodeKind == StoryNodeKind.Mainline);
        }

        public StoryNode GetById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            nodesById.TryGetValue(id, out var node);
            return node;
        }

        public StoryNode NextMainlineAfter(StoryNode source)
        {
            if (source == null)
            {
                return FirstNode();
            }

            return Graph.nodes
                .Where(node => node.nodeKind == StoryNodeKind.Mainline && node.mainlineIndex > source.mainlineIndex)
                .OrderBy(node => node.mainlineIndex)
                .FirstOrDefault();
        }

        public static StoryGraph LoadGraph()
        {
            var textAsset = Resources.Load<TextAsset>("mainline_nodes");
            if (textAsset != null)
            {
                try
                {
                    var graph = JsonConvert.DeserializeObject<StoryGraph>(textAsset.text);
                    if (graph != null && graph.nodes != null && graph.nodes.Count > 0)
                    {
                        return graph;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"AI Builder mainline graph ignored: {ex.Message}");
                }
            }

            return CreateDefaultGraph();
        }

        public static string CreateStoryCacheId(StoryGraph graph)
        {
            if (graph == null)
            {
                return "story_none";
            }

            var baseId = SanitizeCacheSegment(string.IsNullOrWhiteSpace(graph.chapterId) ? graph.chapterTitle : graph.chapterId);
            var signature = new StringBuilder();
            signature.Append(graph.chapterId).Append('|').Append(graph.chapterTitle);
            foreach (var node in graph.nodes.Where(node => node != null).OrderBy(node => node.mainlineIndex).ThenBy(node => node.id))
            {
                signature.Append('|')
                    .Append(node.id).Append(':')
                    .Append(node.chapterId).Append(':')
                    .Append(node.title).Append(':')
                    .Append(node.body).Append(':')
                    .Append(node.leftChoice?.id).Append(':')
                    .Append(node.leftChoice?.label).Append(':')
                    .Append(node.leftChoice?.intent).Append(':')
                    .Append(node.rightChoice?.id).Append(':')
                    .Append(node.rightChoice?.label).Append(':')
                    .Append(node.rightChoice?.intent);
            }

            return $"{baseId}_{StableHashHex(signature.ToString())}";
        }

        public static StoryGraph CreateDefaultGraph()
        {
            return new StoryGraph
            {
                chapterId = "chapter_001",
                chapterTitle = "Fallback Mainline",
                enableDefaultPanorama = true,
                defaultPanoramaPrompt = "fallback medieval court realm panorama, distant crown hall, sealed relic chamber, broken gate at dawn",
                defaultPanoramaPath = "",
                defaultPanoramaStatus = "",
                nodes = new List<StoryNode>
                {
                    CreateDefaultNode("main_001", 1, "The Crown Arrives", "The old ruler is gone, and a cold crown is placed in your hands.", "queen", "Aid the crowd", "Call the guards", "main_002"),
                    CreateDefaultNode("main_002", 2, "The Green Reliquary", "A sealed relic whispers your name from the midnight chamber.", "oracle", "Open the relic", "Seal it away", "main_003"),
                    CreateDefaultNode("main_003", 3, "Dawn at the Gate", "An envoy waits outside the broken gate with grain, soldiers, and a demand.", "gate", "Sign the pact", "Refuse the envoy", "")
                }
            };
        }

        private static string SanitizeCacheSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "story";
            }

            var chars = value.Trim().ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray();
            var compact = new string(chars);
            while (compact.Contains("__", StringComparison.Ordinal))
            {
                compact = compact.Replace("__", "_");
            }

            return string.IsNullOrWhiteSpace(compact.Trim('_')) ? "story" : compact.Trim('_');
        }

        private static string StableHashHex(string value)
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

        private static StoryNode CreateDefaultNode(string id, int index, string title, string body, string imageRef, string leftLabel, string rightLabel, string nextId)
        {
            return new StoryNode
            {
                id = id,
                chapterId = "chapter_001",
                title = title,
                body = body,
                imageRef = imageRef,
                nodeKind = StoryNodeKind.Mainline,
                mainlineIndex = index,
                leftChoice = new ChoiceOption
                {
                    id = "mainline_left",
                    label = leftLabel,
                    intent = "Stay on the stable mainline.",
                    direction = "left",
                    nextMainlineNodeId = nextId,
                    statHint = new PlayerStats(1, 0, -1, 1)
                },
                rightChoice = new ChoiceOption
                {
                    id = "branch_right",
                    label = rightLabel,
                    intent = "Depart from the stable mainline.",
                    direction = "right",
                    nextMainlineNodeId = "",
                    statHint = new PlayerStats(-1, 2, 0, -1)
                }
            };
        }
    }

    public sealed class NodeCacheService
    {
        private readonly INodeCacheStore store;

        public static string CacheDirectory => Path.Combine(Application.persistentDataPath, "AIBuilder");
        public static string CacheFilePath => Path.Combine(CacheDirectory, "node_cache.json");

        public IReadOnlyList<NodeCacheEntry> Entries => store.Entries;

        public NodeCacheService()
            : this(new LocalNodeCacheStore())
        {
        }

        public NodeCacheService(INodeCacheStore store)
        {
            this.store = store ?? new LocalNodeCacheStore();
        }

        public static string CreateCacheKey(string storyId, StoryNode source, ChoiceOption choice, PlayerStats stats)
        {
            var normalizedStoryId = NormalizeCachePart(storyId, "story");
            var sourceId = source == null ? "none" : source.id;
            var choiceId = choice == null ? "none" : choice.id;
            var band = stats == null ? "0-0-0-0" : stats.BandKey();
            return $"{normalizedStoryId}|{sourceId}|{choiceId}|{band}".ToLowerInvariant();
        }

        public static string CreateCacheKey(StoryNode source, ChoiceOption choice, PlayerStats stats)
        {
            return CreateCacheKey(source?.chapterId, source, choice, stats);
        }

        public static string CreateImageCacheKey(string storyId, string chapterId, string locationTag, string moodTag, string majorEventTag)
        {
            var normalizedStoryId = NormalizeCachePart(storyId, "story");
            var chapter = NormalizeSemanticTag(chapterId, "chapter");
            var location = NormalizeSemanticTag(locationTag, "location");
            var mood = NormalizeSemanticTag(moodTag, "mood");
            var majorEvent = NormalizeSemanticTag(majorEventTag, "event");
            return $"{normalizedStoryId}|img_style_v2|{chapter}|{location}|{mood}|{majorEvent}";
        }

        public static string CreatePanoramaCacheKey(string storyId, string chapterId, string locationTag, string moodTag, string majorEventTag)
        {
            var normalizedStoryId = NormalizeCachePart(storyId, "story");
            var chapter = NormalizeSemanticTag(chapterId, "chapter");
            var location = NormalizeSemanticTag(locationTag, "location");
            var mood = NormalizeSemanticTag(moodTag, "mood");
            var majorEvent = NormalizeSemanticTag(majorEventTag, "event");
            return $"{normalizedStoryId}|pano_style_v2|{chapter}|{location}|{mood}|{majorEvent}";
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForStory(string storyId)
        {
            return store.EntriesForStory(storyId);
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForImageKey(string storyId, string imageCacheKey)
        {
            return store.EntriesForImageKey(storyId, imageCacheKey);
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForPanoramaKey(string storyId, string panoramaCacheKey)
        {
            return store.EntriesForPanoramaKey(storyId, panoramaCacheKey);
        }

        public bool TryFindGeneratedImagePath(string storyId, string imageCacheKey, out string imagePath)
        {
            return store.TryFindGeneratedImagePath(storyId, imageCacheKey, out imagePath);
        }

        public bool TryFindGeneratedPanoramaPath(string storyId, string panoramaCacheKey, out string panoramaPath)
        {
            return store.TryFindGeneratedPanoramaPath(storyId, panoramaCacheKey, out panoramaPath);
        }

        public bool TryGet(string key, out NodeCacheEntry entry)
        {
            return store.TryGet(key, out entry);
        }

        public void Put(NodeCacheEntry entry)
        {
            store.Put(entry);
        }

        public void Clear()
        {
            store.Clear();
        }

        public bool SetStatus(string cacheKey, string status)
        {
            return store.SetStatus(cacheKey, status);
        }

        public string SaveImage(string cacheKey, byte[] bytes)
        {
            return store.SaveImage(cacheKey, bytes);
        }

        public void ApplyGeneratedImageToImageKey(string storyId, string imageCacheKey, string imagePath, string imageStatus)
        {
            store.ApplyGeneratedImageToImageKey(storyId, imageCacheKey, imagePath, imageStatus);
        }

        public void ApplyGeneratedPanoramaToPanoramaKey(string storyId, string panoramaCacheKey, string panoramaPath, string panoramaStatus)
        {
            store.ApplyGeneratedPanoramaToPanoramaKey(storyId, panoramaCacheKey, panoramaPath, panoramaStatus);
        }

        private static string NormalizeCachePart(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback ?? "";
            }

            return value.Trim().ToLowerInvariant();
        }

        private static string NormalizeSemanticTag(string value, string fallback)
        {
            var normalized = NormalizeCachePart(value, fallback)
                .Replace("|", "_")
                .Replace("\r", " ")
                .Replace("\n", " ");
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ");
            }

            normalized = normalized.Trim();
            if (normalized.Length <= 48)
            {
                return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
            }

            return $"{normalized.Substring(0, 40).Trim()}_{StableHashHex(normalized)}";
        }

        private static string StableHashHex(string value)
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
    }

    public sealed class OpenAiCompatibleTextService : IAiStreamingTextService, IAiStatJudgementService
    {
        private readonly AiProviderSettings settings;
        private readonly MockAiTextService fallback = new MockAiTextService();

        public OpenAiCompatibleTextService(AiProviderSettings settings)
        {
            this.settings = settings;
        }

        public async Task<AiTextResult> GenerateNextNodeAsync(StoryNode context, ChoiceOption choice, PlayerStats stats, CancellationToken cancellationToken)
        {
            if (!settings.CanUseText)
            {
                return await fallback.GenerateNextNodeAsync(context, choice, stats, cancellationToken);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Mathf.Max(5, settings.timeoutSeconds)));

                var payload = new JObject
                {
                    ["model"] = settings.textModel,
                    ["input"] = BuildPrompt(context, choice, stats),
                    ["store"] = !settings.disableResponseStorage
                };

                var json = await PostJsonAsync(BuildEndpointUrl(settings), payload, timeout.Token, Mathf.Max(5, settings.timeoutSeconds));
                var text = ExtractOutputText(JToken.Parse(json));
                if (TryParseTextResult(text, out var result))
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder text generation fell back to mock: {ex.Message}");
            }

            return await fallback.GenerateNextNodeAsync(context, choice, stats, cancellationToken);
        }

        public async Task<AiTextResult> GenerateNextNodeStreamingAsync(
            StoryNode context,
            ChoiceOption choice,
            PlayerStats stats,
            Action<string> onStoryText,
            CancellationToken cancellationToken)
        {
            if (!settings.CanUseText)
            {
                return await fallback.GenerateNextNodeAsync(context, choice, stats, cancellationToken);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Mathf.Max(5, settings.timeoutSeconds)));

                var payload = new JObject
                {
                    ["model"] = settings.textModel,
                    ["input"] = BuildPrompt(context, choice, stats),
                    ["store"] = !settings.disableResponseStorage,
                    ["stream"] = true
                };

                var text = await PostJsonStreamingAsync(
                    BuildEndpointUrl(settings),
                    payload,
                    onStoryText,
                    timeout.Token,
                    Mathf.Max(5, settings.timeoutSeconds));
                if (TryParseTextResult(text, out var result))
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder streaming text generation fell back to non-streaming text: {ex.Message}");
            }

            var fallbackResult = await GenerateNextNodeAsync(context, choice, stats, cancellationToken);
            onStoryText?.Invoke(fallbackResult.storyText);
            return fallbackResult;
        }

        public async Task<StatJudgementResult> JudgeStatsAsync(
            StoryNode context,
            ChoiceOption choice,
            PlayerStats stats,
            IReadOnlyList<string> recentEvents,
            CancellationToken cancellationToken)
        {
            if (!settings.CanUseText)
            {
                return await fallback.JudgeStatsAsync(context, choice, stats, recentEvents, cancellationToken);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Mathf.Clamp(settings.timeoutSeconds, 5, 8)));

                var payload = new JObject
                {
                    ["model"] = settings.textModel,
                    ["input"] = BuildStatJudgementPrompt(context, choice, stats, recentEvents),
                    ["store"] = !settings.disableResponseStorage
                };

                var json = await PostJsonAsync(BuildEndpointUrl(settings), payload, timeout.Token, Mathf.Clamp(settings.timeoutSeconds, 5, 8));
                var text = ExtractOutputText(JToken.Parse(json));
                if (TryParseStatJudgementResult(text, out var result))
                {
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder stat judgement fell back to local rules: {ex.Message}");
            }

            return await fallback.JudgeStatsAsync(context, choice, stats, recentEvents, cancellationToken);
        }

        private static string BuildPrompt(StoryNode context, ChoiceOption choice, PlayerStats stats)
        {
            return "You are the branch writer for a dark fairytale visual novel. "
                   + "Generate one short branch node after the player's deviation. Return JSON only. "
                   + "Return keys in this exact order: "
                   + "storyText, leftChoice, rightChoice, statDelta{life,force,wealth,faith}, imagePrompt, panoramaPrompt, locationTag, moodTag, majorEventTag, summaryTags. "
                   + "storyText, leftChoice, and rightChoice must be Simplified Chinese. storyText <= 80 Chinese characters. "
                   + "imagePrompt must be concise English visual details for a square low-poly royal decision card: flat silhouettes, muted red/gold/parchment palette, no realism. "
                   + "panoramaPrompt must be concise English visual details for a wide 16:9 refined low-poly background panorama: layered horizon, same muted UI palette, no close-up characters. "
                   + "locationTag, moodTag, and majorEventTag must be short reusable Chinese or English labels for coarse image caching. "
                   + "statDelta values must be -5..5. Prefer -2..2, and usually change at most one stat. Use 0 when the consequence is not direct and obvious.\n"
                   + $"currentTitle: {context?.title}\ncurrentBody: {context?.body}\nchoice: {choice?.label} / {choice?.intent}"
                   + $"\nstats: life={stats?.life}, force={stats?.force}, wealth={stats?.wealth}, faith={stats?.faith}";
        }

        private static string BuildStatJudgementPrompt(StoryNode context, ChoiceOption choice, PlayerStats stats, IReadOnlyList<string> recentEvents)
        {
            var eventsText = recentEvents == null || recentEvents.Count == 0
                ? "(none)"
                : string.Join("\n", recentEvents.Skip(Mathf.Max(0, recentEvents.Count - 8)));
            return "You are a causality-based game-stat judge for a dark fairytale visual novel. "
                   + "The game applies stat changes only every 2-3 choices, so judge the accumulated consequence of the recent events with a scale that matches the fiction. "
                   + "Return JSON only: {\"statDelta\":{\"life\":0,\"force\":0,\"wealth\":0,\"faith\":0},\"reason\":\"简短中文理由\"}. "
                   + "Each statDelta value must be an integer from -100 to 100 and means change from currentStats, not a final value. "
                   + "Use 0 when there is no clear causal consequence. For ordinary tension, use 1-5; meaningful costs or gains, 6-15; major injuries/losses/crises, 16-40; catastrophic events, 41-100. "
                   + "A stat may fall to 0 only when the fiction clearly justifies it: fatal damage for life, total ruin for wealth, complete spiritual collapse for faith, or total loss of combat capability for force. "
                   + "Example: if life is 100 and the character is directly hit by a nuclear strike, life may be -100. "
                   + "Never change stats just to add drama; every non-zero value must be caused by a concrete recent event. "
                   + "Usually change one stat; change multiple stats only when the same event directly affects them. "
                   + "force can reach 0, but force alone is not game over. "
                   + "reason must be Simplified Chinese, <= 30 Chinese characters, and name the causal event.\n"
                   + $"currentTitle: {context?.title}\ncurrentBody: {context?.body}\nchoice: {choice?.label} / {choice?.intent}\n"
                   + $"currentStats: life={stats?.life}, force={stats?.force}, wealth={stats?.wealth}, faith={stats?.faith}\n"
                   + $"recentEvents:\n{eventsText}";
        }

        private async Task<string> PostJsonAsync(string url, JObject payload, CancellationToken cancellationToken, int timeoutSeconds)
        {
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            using var abortOnCancel = cancellationToken.Register(request.Abort);
            var body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
            request.timeout = timeoutSeconds;

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException($"{request.responseCode}: {request.error} {AiBuilderServiceLog.PreviewForLog(request.downloadHandler.text)}");
            }

            return request.downloadHandler.text;
        }

        private static string BuildEndpointUrl(AiProviderSettings settings)
        {
            var endpoint = string.IsNullOrWhiteSpace(settings.wireApi) ? "responses" : settings.wireApi.Trim().Trim('/');
            return $"{settings.baseUrl.TrimEnd('/')}/{endpoint}";
        }

        private static bool TryParseTextResult(string text, out AiTextResult result)
        {
            result = null;
            var token = ExtractJsonToken(text);
            if (token == null)
            {
                return false;
            }

            try
            {
                result = token.ToObject<AiTextResult>();
                if (result == null || string.IsNullOrWhiteSpace(result.storyText))
                {
                    return false;
                }

                result.leftChoice = string.IsNullOrWhiteSpace(result.leftChoice) ? "继续追问" : result.leftChoice;
                result.rightChoice = string.IsNullOrWhiteSpace(result.rightChoice) ? "回到主线" : result.rightChoice;
                result.statDelta ??= new PlayerStats(0, 0, 0, 0);
                result.summaryTags ??= new List<string>();
                result.imagePrompt = string.IsNullOrWhiteSpace(result.imagePrompt) ? "" : result.imagePrompt.Trim();
                result.panoramaPrompt = string.IsNullOrWhiteSpace(result.panoramaPrompt) ? "" : result.panoramaPrompt.Trim();
                result.locationTag = string.IsNullOrWhiteSpace(result.locationTag) ? FirstMatchingTag(result.summaryTags, "location") : result.locationTag.Trim();
                result.moodTag = string.IsNullOrWhiteSpace(result.moodTag) ? FirstMatchingTag(result.summaryTags, "mood") : result.moodTag.Trim();
                result.majorEventTag = string.IsNullOrWhiteSpace(result.majorEventTag) ? FirstMatchingTag(result.summaryTags, "event") : result.majorEventTag.Trim();
                SanitizeTextResult(result);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseStatJudgementResult(string text, out StatJudgementResult result)
        {
            result = null;
            var token = ExtractJsonToken(text);
            if (token == null)
            {
                return false;
            }

            try
            {
                result = token.ToObject<StatJudgementResult>();
                if (result == null)
                {
                    return false;
                }

                result.statDelta ??= new PlayerStats(0, 0, 0, 0);
                result.reason = string.IsNullOrWhiteSpace(result.reason) ? "局势暂稳" : result.reason.Trim();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FirstMatchingTag(IEnumerable<string> tags, string prefix)
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

        private static void SanitizeTextResult(AiTextResult result)
        {
            if (result == null)
            {
                return;
            }

            result.storyText = CompactGeneratedText(result.storyText, 160);
            result.leftChoice = CompactGeneratedText(result.leftChoice, 18);
            result.rightChoice = CompactGeneratedText(result.rightChoice, 18);
            result.statDelta = PlayerStats.ClampDelta(result.statDelta, 5);
            result.summaryTags = (result.summaryTags ?? new List<string>())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => CompactGeneratedText(tag, 64))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Take(8)
                .ToList();
            result.imagePrompt = CompactGeneratedText(result.imagePrompt, 500);
            result.panoramaPrompt = CompactGeneratedText(result.panoramaPrompt, 500);
            result.locationTag = CompactGeneratedText(result.locationTag, 64);
            result.moodTag = CompactGeneratedText(result.moodTag, 64);
            result.majorEventTag = CompactGeneratedText(result.majorEventTag, 64);
        }

        private static string CompactGeneratedText(string value, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            while (compact.Contains("  ", StringComparison.Ordinal))
            {
                compact = compact.Replace("  ", " ");
            }

            if (maxCharacters <= 0 || compact.Length <= maxCharacters)
            {
                return compact;
            }

            return compact.Substring(0, maxCharacters).Trim();
        }

        private static string ExtractOutputText(JToken root)
        {
            var direct = root.SelectToken("output_text")?.Value<string>();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            foreach (var token in WalkTokens(root))
            {
                if (token is JProperty property && (property.Name == "text" || property.Name == "content"))
                {
                    var value = property.Value.Type == JTokenType.String ? property.Value.Value<string>() : "";
                    if (!string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        return value;
                    }
                }
            }

            return root.ToString(Formatting.None);
        }

        private async Task<string> PostJsonStreamingAsync(string url, JObject payload, Action<string> onStoryText, CancellationToken cancellationToken, int timeoutSeconds)
        {
            var parser = new ResponseStreamParser(onStoryText);
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            using var abortOnCancel = cancellationToken.Register(request.Abort);
            var body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new StreamingDownloadHandler(parser.PushBytes);
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "text/event-stream");
            request.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
            request.timeout = timeoutSeconds;

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellationToken);
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException($"{request.responseCode}: {request.error} {AiBuilderServiceLog.PreviewForLog(request.downloadHandler.text)}");
            }

            return parser.Complete();
        }

        private sealed class StreamingDownloadHandler : DownloadHandlerScript
        {
            private readonly Action<byte[], int> onData;

            public StreamingDownloadHandler(Action<byte[], int> onData)
            {
                this.onData = onData;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0)
                {
                    return true;
                }

                onData?.Invoke(data, dataLength);
                return true;
            }
        }

        private sealed class ResponseStreamParser
        {
            private readonly Action<string> onStoryText;
            private readonly StringBuilder lineBuffer = new StringBuilder();
            private readonly StringBuilder outputText = new StringBuilder();
            private string lastStoryText = "";

            public ResponseStreamParser(Action<string> onStoryText)
            {
                this.onStoryText = onStoryText;
            }

            public void PushBytes(byte[] data, int dataLength)
            {
                PushText(Encoding.UTF8.GetString(data, 0, dataLength));
            }

            public string Complete()
            {
                if (lineBuffer.Length > 0)
                {
                    ProcessLine(lineBuffer.ToString());
                    lineBuffer.Length = 0;
                }

                return outputText.ToString();
            }

            private void PushText(string chunk)
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    return;
                }

                foreach (var character in chunk)
                {
                    if (character == '\n')
                    {
                        ProcessLine(lineBuffer.ToString().TrimEnd('\r'));
                        lineBuffer.Length = 0;
                    }
                    else
                    {
                        lineBuffer.Append(character);
                    }
                }
            }

            private void ProcessLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var data = line.Substring(5).Trim();
                if (data == "[DONE]")
                {
                    return;
                }

                try
                {
                    var root = JToken.Parse(data);
                    var delta = ExtractStreamDelta(root);
                    if (string.IsNullOrEmpty(delta))
                    {
                        return;
                    }

                    outputText.Append(delta);
                    PublishStoryText();
                }
                catch
                {
                    // Ignore individual malformed stream lines; the final parse will decide success.
                }
            }

            private void PublishStoryText()
            {
                var current = ExtractPartialJsonStringField(outputText.ToString(), "storyText");
                if (string.IsNullOrEmpty(current) || current == lastStoryText)
                {
                    return;
                }

                lastStoryText = current;
                onStoryText?.Invoke(current);
            }

            private static string ExtractStreamDelta(JToken root)
            {
                if (root == null)
                {
                    return "";
                }

                var type = root["type"]?.Value<string>() ?? "";
                if (string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
                {
                    return root["delta"]?.Value<string>() ?? "";
                }

                var directDelta = root["delta"]?.Value<string>();
                if (!string.IsNullOrEmpty(directDelta))
                {
                    return directDelta;
                }

                var outputTextDelta = root.SelectToken("response.output_text.delta")?.Value<string>()
                                      ?? root.SelectToken("choices[0].delta.content")?.Value<string>()
                                      ?? root.SelectToken("choices[0].text")?.Value<string>();
                return outputTextDelta ?? "";
            }

            private static string ExtractPartialJsonStringField(string json, string fieldName)
            {
                if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName))
                {
                    return "";
                }

                var marker = $"\"{fieldName}\"";
                var markerIndex = json.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex < 0)
                {
                    return "";
                }

                var colonIndex = json.IndexOf(':', markerIndex + marker.Length);
                if (colonIndex < 0)
                {
                    return "";
                }

                var quoteIndex = json.IndexOf('"', colonIndex + 1);
                if (quoteIndex < 0)
                {
                    return "";
                }

                var builder = new StringBuilder();
                var escaping = false;
                for (var i = quoteIndex + 1; i < json.Length; i++)
                {
                    var character = json[i];
                    if (escaping)
                    {
                        switch (character)
                        {
                            case '"':
                            case '\\':
                            case '/':
                                builder.Append(character);
                                break;
                            case 'n':
                                builder.Append('\n');
                                break;
                            case 'r':
                                builder.Append('\r');
                                break;
                            case 't':
                                builder.Append('\t');
                                break;
                            case 'u':
                                if (i + 4 < json.Length
                                    && int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var codepoint))
                                {
                                    builder.Append((char)codepoint);
                                    i += 4;
                                }
                                break;
                            default:
                                builder.Append(character);
                                break;
                        }

                        escaping = false;
                        continue;
                    }

                    if (character == '\\')
                    {
                        escaping = true;
                        continue;
                    }

                    if (character == '"')
                    {
                        break;
                    }

                    builder.Append(character);
                }

                return builder.ToString();
            }
        }

        private static JToken ExtractJsonToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start < 0 || end <= start ? null : JToken.Parse(text.Substring(start, end - start + 1));
        }

        private static IEnumerable<JToken> WalkTokens(JToken token)
        {
            if (token == null)
            {
                yield break;
            }

            yield return token;
            foreach (var child in token.Children())
            {
                foreach (var nested in WalkTokens(child))
                {
                    yield return nested;
                }
            }
        }
    }

    public sealed class OpenAiCompatibleImageService : IAiImageService
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly AiProviderSettings settings;

        static OpenAiCompatibleImageService()
        {
            HttpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public OpenAiCompatibleImageService(AiProviderSettings settings)
        {
            this.settings = settings;
        }

        public async Task<AiImageResult> GenerateImageAsync(string prompt, CancellationToken cancellationToken, AiImagePurpose purpose = AiImagePurpose.Card)
        {
            if (!settings.CanUseImage)
            {
                return AiImageResult.Failed(BuildImageUnavailableReason(settings));
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return AiImageResult.Failed("Image prompt is empty.");
            }

            var timeoutSeconds = Mathf.Max(8, settings.imageTimeoutSeconds);
            var useImagesEndpoint = !string.IsNullOrWhiteSpace(settings.imageEndpointUrl)
                                    || !string.IsNullOrWhiteSpace(settings.imageBaseUrl)
                                    || AiModelNameHints.IsLikelyImageOnlyModel(settings.imageModel);
            var endpointUrl = useImagesEndpoint ? BuildImagesEndpointUrl(settings) : BuildResponsesEndpointUrl(settings);
            var endpointKind = useImagesEndpoint ? "images/generations" : "responses";

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                Debug.Log($"AI Builder image request: purpose={purpose}, provider={settings.NormalizedProviderType}, model={settings.imageModel}, endpoint={endpointKind}, timeout={timeoutSeconds}s.");

                var json = useImagesEndpoint
                    ? await PostJsonAsync(endpointUrl, BuildImagesPayload(prompt, purpose), timeout.Token, timeoutSeconds)
                    : await PostJsonAsync(endpointUrl, BuildResponsesImagePayload(prompt, purpose), timeout.Token, timeoutSeconds);
                var root = JToken.Parse(json);
                var immediateResult = await TryExtractImageResultAsync(root, timeout.Token, timeoutSeconds);
                if (immediateResult != null)
                {
                    return immediateResult;
                }

                var taskId = FindTaskId(root);
                if (!string.IsNullOrWhiteSpace(taskId))
                {
                    return await PollImageTaskAsync(endpointUrl, taskId, timeout.Token, timeoutSeconds);
                }

                var noImageReason = $"No image bytes or image URL found in API response. Preview: {AiBuilderServiceLog.PreviewForLog(json)}";
                Debug.LogWarning($"AI Builder image generation skipped: {noImageReason}");
                return AiImageResult.Failed(noImageReason);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var reason = $"Image request timed out after {timeoutSeconds}s using {endpointKind}.";
                Debug.LogWarning($"AI Builder image generation skipped: {reason}");
                return AiImageResult.Failed(reason);
            }
            catch (Exception ex)
            {
                var reason = $"{endpointKind} image request failed: {ex.Message}";
                Debug.LogWarning($"AI Builder image generation skipped: {reason}");
                return AiImageResult.Failed(reason);
            }
        }

        private JObject BuildImagesPayload(string prompt, AiImagePurpose purpose)
        {
            return new JObject
            {
                ["model"] = settings.imageModel,
                ["prompt"] = BuildPromptForPurpose(prompt, purpose),
                ["size"] = ImageSizeForPurpose(purpose),
                ["resolution"] = settings.imageResolution,
                ["quality"] = settings.imageQuality,
                ["output_format"] = settings.imageOutputFormat,
                ["n"] = settings.imageCount
            };
        }

        private JObject BuildResponsesImagePayload(string prompt, AiImagePurpose purpose)
        {
            return new JObject
            {
                ["model"] = settings.imageModel,
                ["input"] = BuildPromptForPurpose(prompt, purpose),
                ["tools"] = new JArray(new JObject
                {
                    ["type"] = "image_generation",
                    ["size"] = ResponsesToolSizeForPurpose(purpose),
                    ["quality"] = settings.imageQuality
                }),
                ["tool_choice"] = new JObject { ["type"] = "image_generation" },
                ["store"] = !settings.disableResponseStorage
            };
        }

        private string ImageSizeForPurpose(AiImagePurpose purpose)
        {
            return purpose == AiImagePurpose.Panorama && !string.IsNullOrWhiteSpace(settings.panoramaImageSize)
                ? settings.panoramaImageSize
                : settings.imageSize;
        }

        private static string ResponsesToolSizeForPurpose(AiImagePurpose purpose)
        {
            return purpose == AiImagePurpose.Panorama ? "1536x1024" : "1024x1024";
        }

        private static string BuildPromptForPurpose(string prompt, AiImagePurpose purpose)
        {
            return purpose == AiImagePurpose.Panorama
                ? AiBuilderImagePromptStyle.BuildPanoramaPrompt(prompt)
                : AiBuilderImagePromptStyle.BuildCardPrompt(prompt);
        }

        private async Task<string> PostJsonAsync(string url, JObject payload, CancellationToken cancellationToken, int timeoutSeconds)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ImageApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using var response = await AwaitHttpAsync(
                HttpClient.SendAsync(request, cancellationToken),
                cancellationToken,
                timeoutSeconds,
                "image POST");
            var text = await AwaitHttpAsync(
                response.Content.ReadAsStringAsync(),
                cancellationToken,
                timeoutSeconds,
                "image response read");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{(int)response.StatusCode}: {response.ReasonPhrase} {AiBuilderServiceLog.PreviewForLog(text)}");
            }

            return text;
        }

        private async Task<string> GetJsonAsync(string url, CancellationToken cancellationToken, int timeoutSeconds)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ImageApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await AwaitHttpAsync(
                HttpClient.SendAsync(request, cancellationToken),
                cancellationToken,
                timeoutSeconds,
                "image status GET");
            var text = await AwaitHttpAsync(
                response.Content.ReadAsStringAsync(),
                cancellationToken,
                timeoutSeconds,
                "image status response read");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{(int)response.StatusCode}: {response.ReasonPhrase} {AiBuilderServiceLog.PreviewForLog(text)}");
            }

            return text;
        }

        private async Task<AiImageResult> TryExtractImageResultAsync(JToken root, CancellationToken cancellationToken, int timeoutSeconds)
        {
            var base64 = FindBase64Image(root);
            if (!string.IsNullOrWhiteSpace(base64))
            {
                return AiImageResult.Success(Convert.FromBase64String(NormalizeBase64(StripDataUrl(base64))));
            }

            var url = FindImageUrl(root);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var bytes = await GetBytesAsync(url, cancellationToken, timeoutSeconds);
                return bytes == null || bytes.Length == 0
                    ? AiImageResult.Failed("Image URL returned empty bytes.")
                    : AiImageResult.Success(bytes);
            }

            return null;
        }

        private async Task<AiImageResult> PollImageTaskAsync(string imageEndpointUrl, string taskId, CancellationToken cancellationToken, int timeoutSeconds)
        {
            var statusUrl = BuildTaskStatusUrl(imageEndpointUrl, taskId);
            var intervalMs = Mathf.Clamp(settings.imagePollIntervalSeconds, 2, 10) * 1000;
            Debug.Log($"AI Builder image task polling started: task={taskId}, interval={settings.imagePollIntervalSeconds}s.");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(intervalMs, cancellationToken);
                var json = await GetJsonAsync(statusUrl, cancellationToken, timeoutSeconds);
                var root = JToken.Parse(json);
                var image = await TryExtractImageResultAsync(root, cancellationToken, timeoutSeconds);
                if (image != null && image.Succeeded)
                {
                    return image;
                }

                var status = FindTaskStatus(root);
                if (IsFailedTaskStatus(status))
                {
                    return AiImageResult.Failed($"Image task {taskId} failed: {AiBuilderServiceLog.PreviewForLog(json)}");
                }

                if (IsCompletedTaskStatus(status))
                {
                    return AiImageResult.Failed($"Image task {taskId} completed without a readable image URL. Preview: {AiBuilderServiceLog.PreviewForLog(json)}");
                }
            }
        }

        private async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken, int timeoutSeconds)
        {
            using var response = await AwaitHttpAsync(
                HttpClient.GetAsync(url, cancellationToken),
                cancellationToken,
                timeoutSeconds,
                "image bytes GET");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            return await AwaitHttpAsync(
                response.Content.ReadAsByteArrayAsync(),
                cancellationToken,
                timeoutSeconds,
                "image bytes read");
        }

        private static async Task<T> AwaitHttpAsync<T>(Task<T> task, CancellationToken cancellationToken, int timeoutSeconds, string operation)
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(Mathf.Max(1, timeoutSeconds)), cancellationToken);
            var completed = await Task.WhenAny(task, delay);
            if (ReferenceEquals(completed, task))
            {
                return await task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{operation} timed out after {timeoutSeconds}s.");
        }

        private static string BuildResponsesEndpointUrl(AiProviderSettings settings)
        {
            var endpoint = string.IsNullOrWhiteSpace(settings.wireApi) ? "responses" : settings.wireApi.Trim().Trim('/');
            return $"{settings.baseUrl.TrimEnd('/')}/{endpoint}";
        }

        private static string BuildImagesEndpointUrl(AiProviderSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.imageEndpointUrl))
            {
                return settings.imageEndpointUrl.Trim();
            }

            var baseUrl = string.IsNullOrWhiteSpace(settings.imageBaseUrl)
                ? settings.baseUrl
                : settings.imageBaseUrl;
            var endpoint = string.IsNullOrWhiteSpace(settings.imageEndpoint)
                ? "images/generations"
                : settings.imageEndpoint.Trim().Trim('/');
            return $"{baseUrl.TrimEnd('/')}/{endpoint}";
        }

        private static string BuildImageUnavailableReason(AiProviderSettings settings)
        {
            if (settings == null)
            {
                return "Image provider settings are missing.";
            }

            if (settings.IsMockProvider)
            {
                return "Image provider is mock.";
            }

            if (!settings.ImageApiKeyPresent)
            {
                return "Image API key is missing.";
            }

            if (string.IsNullOrWhiteSpace(settings.imageModel))
            {
                return "Image model is missing.";
            }

            return "Image provider is unavailable.";
        }

        private static string FindTaskId(JToken root)
        {
            foreach (var property in WalkTokens(root).OfType<JProperty>())
            {
                var name = property.Name.ToLowerInvariant();
                if ((name == "task_id" || name == "taskid") && property.Value.Type == JTokenType.String)
                {
                    return property.Value.Value<string>();
                }
            }

            return "";
        }

        private static string FindTaskStatus(JToken root)
        {
            foreach (var property in WalkTokens(root).OfType<JProperty>())
            {
                if (string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase)
                    && property.Value.Type == JTokenType.String)
                {
                    return property.Value.Value<string>();
                }
            }

            return "";
        }

        private static bool IsFailedTaskStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var normalized = status.Trim().ToLowerInvariant();
            return normalized == "failed"
                   || normalized == "error"
                   || normalized == "cancelled"
                   || normalized == "canceled";
        }

        private static bool IsCompletedTaskStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var normalized = status.Trim().ToLowerInvariant();
            return normalized == "completed"
                   || normalized == "complete"
                   || normalized == "succeeded"
                   || normalized == "success";
        }

        private static string BuildTaskStatusUrl(string imageEndpointUrl, string taskId)
        {
            var escapedTaskId = Uri.EscapeDataString(taskId);
            var endpoint = imageEndpointUrl.TrimEnd('/');
            var marker = "/images/";
            var markerIndex = endpoint.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                return $"{endpoint.Substring(0, markerIndex)}/tasks/{escapedTaskId}?language=zh";
            }

            return $"{endpoint}/../tasks/{escapedTaskId}?language=zh";
        }

        private static string FindBase64Image(JToken root)
        {
            foreach (var item in WalkTokens(root).OfType<JObject>())
            {
                if (string.Equals(item["type"]?.Value<string>(), "image_generation_call", StringComparison.OrdinalIgnoreCase))
                {
                    var result = item["result"]?.Value<string>();
                    if (LooksLikeBase64Image(result))
                    {
                        return result;
                    }
                }
            }

            foreach (var property in WalkTokens(root).OfType<JProperty>())
            {
                var name = property.Name.ToLowerInvariant();
                if ((name.Contains("base64") || name == "b64_json" || name == "image_base64")
                    && property.Value.Type == JTokenType.String)
                {
                    var value = property.Value.Value<string>();
                    if (LooksLikeBase64Image(value))
                    {
                        return value;
                    }
                }
            }

            return "";
        }

        private static string FindImageUrl(JToken root)
        {
            foreach (var property in WalkTokens(root).OfType<JProperty>())
            {
                var name = property.Name.ToLowerInvariant();
                if (name != "url" && !name.Contains("image_url") && !name.Contains("download_url"))
                {
                    continue;
                }

                if (property.Value.Type == JTokenType.String)
                {
                    var value = property.Value.Value<string>();
                    if (IsHttpUrl(value))
                    {
                        return value;
                    }
                }

                if (property.Value is JArray array)
                {
                    foreach (var value in array.DescendantsAndSelf().OfType<JValue>())
                    {
                        if (value.Type == JTokenType.String)
                        {
                            var url = value.Value<string>();
                            if (IsHttpUrl(url))
                            {
                                return url;
                            }
                        }
                    }
                }
            }

            return "";
        }

        private static bool IsHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static IEnumerable<JToken> WalkTokens(JToken token)
        {
            if (token == null)
            {
                yield break;
            }

            yield return token;
            foreach (var child in token.Children())
            {
                foreach (var nested in WalkTokens(child))
                {
                    yield return nested;
                }
            }
        }

        private static string StripDataUrl(string value)
        {
            var comma = value.IndexOf(',');
            return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
                ? value.Substring(comma + 1)
                : value;
        }

        private static string NormalizeBase64(string value)
        {
            var compact = new string((value ?? "").Where(character => !char.IsWhiteSpace(character)).ToArray())
                .Replace('-', '+')
                .Replace('_', '/');
            var padding = compact.Length % 4;
            return padding == 0 ? compact : compact.PadRight(compact.Length + (4 - padding), '=');
        }

        private static bool LooksLikeBase64Image(string value)
        {
            var compact = NormalizeBase64(StripDataUrl(value ?? ""));
            return compact.Length > 200
                   && compact.All(character =>
                       char.IsLetterOrDigit(character)
                       || character == '+'
                       || character == '/'
                       || character == '=');
        }
    }

    public sealed class MockAiTextService : IAiTextService, IAiStatJudgementService
    {
        public Task<AiTextResult> GenerateNextNodeAsync(StoryNode context, ChoiceOption choice, PlayerStats stats, CancellationToken cancellationToken)
        {
            var seed = Mathf.Abs(NodeCacheService.CreateCacheKey(context, choice, stats).GetHashCode());
            var result = new AiTextResult
            {
                storyText = $"Mock branch after {choice?.label}: the court hesitates, then records a new uncertain path.",
                leftChoice = "追问真相",
                rightChoice = "回到主线",
                statDelta = new PlayerStats(
                    (seed % 9) - 4,
                    ((seed / 10) % 11) - 5,
                    ((seed / 100) % 13) - 6,
                    ((seed / 1000) % 9) - 4),
                imagePrompt = $"symbolic medieval branch card, {context?.title}, {choice?.label}, flat shapes, limited palette",
                panoramaPrompt = $"wide medieval harbor or court panorama, {context?.chapterId}, {context?.title}, {choice?.label}, distant horizon, layered low-poly depth",
                locationTag = context?.chapterId ?? "mock_location",
                moodTag = "uncertain",
                majorEventTag = choice?.id ?? "choice",
                summaryTags = new List<string> { "mock", "branch", choice?.id ?? "choice" }
            };

            return Task.FromResult(result);
        }

        public Task<StatJudgementResult> JudgeStatsAsync(
            StoryNode context,
            ChoiceOption choice,
            PlayerStats stats,
            IReadOnlyList<string> recentEvents,
            CancellationToken cancellationToken)
        {
            var text = $"{context?.title} {context?.body} {choice?.label} {choice?.intent} {string.Join(" ", recentEvents ?? Array.Empty<string>())}".ToLowerInvariant();
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

            return Task.FromResult(new StatJudgementResult
            {
                statDelta = delta,
                reason = PlayerStats.IsZeroDelta(delta) ? "局势暂稳" : "近期选择产生连锁影响"
            });
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            return needles.Any(needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    public sealed class MockAiImageService : IAiImageService
    {
        public Task<AiImageResult> GenerateImageAsync(string prompt, CancellationToken cancellationToken, AiImagePurpose purpose = AiImagePurpose.Card)
        {
            return Task.FromResult(AiImageResult.Failed("Mock provider does not generate runtime images."));
        }
    }

    internal static class AiBuilderServiceLog
    {
        public static string PreviewForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return compact.Length <= 500 ? compact : compact.Substring(0, 500) + "...";
        }
    }
}

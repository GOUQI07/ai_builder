using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AIBuilder
{
    public interface IAiAdaptationService
    {
        Task<SourceChunkSummary> SummarizeChunkAsync(SourceChunk chunk, CancellationToken cancellationToken = default);
        Task<List<StoryChapter>> BuildChapterOutlineAsync(List<SourceChunkSummary> summaries, int targetChapterCount, CancellationToken cancellationToken = default);
        Task<List<StoryAnchorNode>> BuildAnchorNodesAsync(StoryChapter chapter, int minAnchors, int maxAnchors, CancellationToken cancellationToken = default);
    }

    public static class AiAdaptationServiceFactory
    {
        public static IAiAdaptationService Create(AiProviderSettings settings)
        {
            settings ??= new AiProviderSettings();
            return settings.CanUseText && settings.NormalizedProviderType == AiProviderTypes.OpenAiCompatible
                ? new OpenAiCompatibleAdaptationService(settings)
                : new MockAiAdaptationService();
        }
    }

    public sealed class OpenAiCompatibleAdaptationService : IAiAdaptationService
    {
        private readonly AiProviderSettings settings;
        private readonly MockAiAdaptationService fallback = new MockAiAdaptationService();

        public OpenAiCompatibleAdaptationService(AiProviderSettings settings)
        {
            this.settings = settings;
        }

        public async Task<SourceChunkSummary> SummarizeChunkAsync(SourceChunk chunk, CancellationToken cancellationToken = default)
        {
            if (!settings.CanUseText || chunk == null)
            {
                return await fallback.SummarizeChunkAsync(chunk, cancellationToken);
            }

            try
            {
                var text = await GenerateTextAsync(BuildChunkSummaryPrompt(chunk), cancellationToken);
                if (TryParseObject(text, out SourceChunkSummary summary))
                {
                    summary.chunkId = string.IsNullOrWhiteSpace(summary.chunkId) ? chunk.id : summary.chunkId;
                    summary.chunkIndex = summary.chunkIndex <= 0 ? chunk.index : summary.chunkIndex;
                    summary.status = StoryAuthoringStatuses.Draft;
                    return summary;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder adaptation chunk summary fell back to mock: {ex.Message}");
            }

            return await fallback.SummarizeChunkAsync(chunk, cancellationToken);
        }

        public async Task<List<StoryChapter>> BuildChapterOutlineAsync(List<SourceChunkSummary> summaries, int targetChapterCount, CancellationToken cancellationToken = default)
        {
            if (!settings.CanUseText || summaries == null || summaries.Count == 0)
            {
                return await fallback.BuildChapterOutlineAsync(summaries, targetChapterCount, cancellationToken);
            }

            try
            {
                var count = Mathf.Clamp(targetChapterCount, StoryAuthoringUtility.MinChapterCount, StoryAuthoringUtility.MaxChapterCount);
                var text = await GenerateTextAsync(BuildChapterOutlinePrompt(summaries, count), cancellationToken, true);
                if (TryParseList(text, "chapters", out List<StoryChapter> chapters) && chapters.Count > 0)
                {
                    for (var i = 0; i < chapters.Count; i++)
                    {
                        chapters[i].chapterIndex = i + 1;
                        chapters[i].chapterId = string.IsNullOrWhiteSpace(chapters[i].chapterId) ? $"ch{i + 1:000}" : chapters[i].chapterId;
                        chapters[i].status = StoryAuthoringStatuses.Draft;
                    }

                    return chapters;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder adaptation outline fell back to mock: {ex.Message}");
            }

            return await fallback.BuildChapterOutlineAsync(summaries, targetChapterCount, cancellationToken);
        }

        public async Task<List<StoryAnchorNode>> BuildAnchorNodesAsync(StoryChapter chapter, int minAnchors, int maxAnchors, CancellationToken cancellationToken = default)
        {
            if (!settings.CanUseText || chapter == null)
            {
                return await fallback.BuildAnchorNodesAsync(chapter, minAnchors, maxAnchors, cancellationToken);
            }

            try
            {
                var text = await GenerateTextAsync(BuildAnchorPrompt(chapter, minAnchors, maxAnchors), cancellationToken);
                if (TryParseList(text, "anchors", out List<StoryAnchorNode> anchors) && anchors.Count > 0)
                {
                    for (var i = 0; i < anchors.Count; i++)
                    {
                        anchors[i].anchorIndex = i + 1;
                        anchors[i].nodeId = string.IsNullOrWhiteSpace(anchors[i].nodeId)
                            ? $"{chapter.chapterId}_anchor{i + 1:000}"
                            : anchors[i].nodeId;
                        anchors[i].status = StoryAuthoringStatuses.Draft;
                        StoryAuthoringUtility.NormalizeAnchor(chapter, anchors[i], i + 1);
                    }

                    return anchors;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder adaptation anchors fell back to mock: {ex.Message}");
            }

            return await fallback.BuildAnchorNodesAsync(chapter, minAnchors, maxAnchors, cancellationToken);
        }

        private async Task<string> GenerateTextAsync(string prompt, CancellationToken cancellationToken, bool longRunning = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutSeconds = longRunning
                ? Mathf.Clamp(settings.timeoutSeconds * 3, 30, 180)
                : Mathf.Clamp(settings.timeoutSeconds * 2, 20, 120);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var payload = new JObject
            {
                ["model"] = settings.textModel,
                ["input"] = prompt,
                ["store"] = !settings.disableResponseStorage
            };

            if (!string.IsNullOrWhiteSpace(settings.reasoningEffort))
            {
                payload["reasoning"] = new JObject { ["effort"] = settings.reasoningEffort };
            }

            var json = await PostJsonAsync(BuildEndpointUrl(settings), payload, timeout.Token, timeoutSeconds);
            return ExtractOutputText(JToken.Parse(json));
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
                throw new InvalidOperationException($"{request.responseCode}: {request.error} {PreviewForLog(request.downloadHandler.text)}");
            }

            return request.downloadHandler.text;
        }

        private static string BuildEndpointUrl(AiProviderSettings settings)
        {
            var endpoint = string.IsNullOrWhiteSpace(settings.wireApi) ? "responses" : settings.wireApi.Trim().Trim('/');
            return $"{settings.baseUrl.TrimEnd('/')}/{endpoint}";
        }

        private static string BuildChunkSummaryPrompt(SourceChunk chunk)
        {
            return "You are adapting authorized source material into a branching visual novel. "
                   + "Summarize this source chunk for later transformation. Do not copy long source passages. "
                   + "Return JSON only with fields: chunkId, chunkIndex, title, summary, characters[], locations[], conflicts[], timelineNotes[]. "
                   + "Each characters[] item must use this shape: \"display name/important aliases: concise role, visual identity, and story function\". "
                   + "summary must be Simplified Chinese, concise, and transformed into planning notes.\n"
                   + $"chunkId: {chunk.id}\nchunkIndex: {chunk.index}\ntitleGuess: {chunk.titleGuess}\nsource:\n{TrimForPrompt(chunk.text, 12000)}";
        }

        private static string BuildChapterOutlinePrompt(List<SourceChunkSummary> summaries, int targetChapterCount)
        {
            var compactSummaries = summaries
                .OrderBy(item => item.chunkIndex)
                .Select(item => $"{item.chunkId}: {TrimForPrompt(item.summary, 700)}")
                .ToArray();
            return "You are adapting source summaries into a stable 100-chapter style mainline for a branching game. "
                   + "Transform, compress, reorder, and re-theme the material. Do not reproduce long source passages. "
                   + $"Create exactly {targetChapterCount} chapters when possible. "
                   + "Return JSON only: {\"chapters\":[{\"chapterId\":\"ch001\",\"chapterIndex\":1,\"chapterTitle\":\"...\",\"summary\":\"...\",\"sourceChunkRefs\":[\"chunk_001\"],\"toneTags\":[\"...\"]}]}. "
                   + "chapterTitle and summary must be Simplified Chinese.\n"
                   + string.Join("\n", compactSummaries);
        }

        private static string BuildAnchorPrompt(StoryChapter chapter, int minAnchors, int maxAnchors)
        {
            var clampedMin = Mathf.Clamp(minAnchors, StoryAuthoringUtility.MinAnchorCount, StoryAuthoringUtility.MaxAnchorCount);
            var clampedMax = Mathf.Clamp(maxAnchors, clampedMin, StoryAuthoringUtility.MaxAnchorCount);
            return "You are creating required mainline anchor nodes for a branching visual novel chapter. "
                   + $"Create {clampedMin}-{clampedMax} anchors. Each anchor must be stable, short, and playable. "
                   + "At least one choice must be marked as mainlineChoice left or right; the other can invite branch generation. "
                   + "Return JSON only: {\"anchors\":[{\"nodeId\":\"\",\"anchorIndex\":1,\"title\":\"...\",\"body\":\"80-120 Chinese chars\",\"imagePrompt\":\"English image prompt\",\"stabilityNote\":\"...\",\"mainlineChoice\":\"left\",\"leftChoice\":{\"id\":\"continue_mainline\",\"label\":\"...\",\"intent\":\"...\",\"direction\":\"left\",\"statHint\":{\"life\":0,\"force\":0,\"wealth\":0,\"faith\":0}},\"rightChoice\":{\"id\":\"deviate_branch\",\"label\":\"...\",\"intent\":\"...\",\"direction\":\"right\",\"statHint\":{\"life\":0,\"force\":0,\"wealth\":0,\"faith\":0}}}]}. "
                   + "imagePrompt must describe a square low-poly royal decision card: flat medieval silhouettes, limited muted red/gold/parchment palette, graphic UI-friendly composition, no realism. "
                   + "Use Simplified Chinese for title, body, labels, intents, and stabilityNote. Keep statHint values in -15..15.\n"
                   + $"chapterId: {chapter.chapterId}\nchapterTitle: {chapter.chapterTitle}\nsummary: {chapter.summary}\ntoneTags: {string.Join(", ", chapter.toneTags ?? new List<string>())}";
        }

        private static bool TryParseObject<T>(string text, out T result)
        {
            result = default;
            var token = ExtractJsonToken(text);
            if (token == null)
            {
                return false;
            }

            try
            {
                result = token.ToObject<T>();
                return result != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseList<T>(string text, string listProperty, out List<T> result)
        {
            result = null;
            var token = ExtractJsonToken(text);
            if (token == null)
            {
                return false;
            }

            try
            {
                var listToken = token.Type == JTokenType.Array ? token : token[listProperty];
                result = listToken?.ToObject<List<T>>();
                return result != null;
            }
            catch
            {
                return false;
            }
        }

        private static JToken ExtractJsonToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var trimmed = text.Trim();
            var startObject = trimmed.IndexOf('{');
            var startArray = trimmed.IndexOf('[');
            var startsWithArray = startArray >= 0 && (startObject < 0 || startArray < startObject);
            var start = startsWithArray ? startArray : startObject;
            var end = startsWithArray ? trimmed.LastIndexOf(']') : trimmed.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return JToken.Parse(trimmed.Substring(start, end - start + 1));
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
                    if (!string.IsNullOrWhiteSpace(value) && (value.TrimStart().StartsWith("{", StringComparison.Ordinal) || value.TrimStart().StartsWith("[", StringComparison.Ordinal)))
                    {
                        return value;
                    }
                }
            }

            return root.ToString(Formatting.None);
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

        private static string TrimForPrompt(string value, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var compact = value.Trim();
            return compact.Length <= maxCharacters ? compact : compact.Substring(0, maxCharacters);
        }

        private static string PreviewForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return compact.Length <= 500 ? compact : compact.Substring(0, 500) + "...";
        }
    }

    public sealed class MockAiAdaptationService : IAiAdaptationService
    {
        public Task<SourceChunkSummary> SummarizeChunkAsync(SourceChunk chunk, CancellationToken cancellationToken = default)
        {
            chunk ??= new SourceChunk { id = "chunk_001", index = 1, titleGuess = "Mock Source", text = "" };
            var summary = new SourceChunkSummary
            {
                chunkId = chunk.id,
                chunkIndex = chunk.index,
                title = string.IsNullOrWhiteSpace(chunk.titleGuess) ? $"Chunk {chunk.index:000}" : chunk.titleGuess,
                summary = $"Mock summary for {chunk.titleGuess}: {Trim(chunk.text, 120)}",
                characters = new List<string> { "Protagonist", "Rival" },
                locations = new List<string> { "Court" },
                conflicts = new List<string> { "Power and trust" },
                timelineNotes = new List<string> { $"Source chunk {chunk.index}" },
                status = StoryAuthoringStatuses.Draft
            };
            return Task.FromResult(summary);
        }

        public Task<List<StoryChapter>> BuildChapterOutlineAsync(List<SourceChunkSummary> summaries, int targetChapterCount, CancellationToken cancellationToken = default)
        {
            var count = Mathf.Clamp(targetChapterCount, StoryAuthoringUtility.MinChapterCount, StoryAuthoringUtility.MaxChapterCount);
            var source = summaries == null || summaries.Count == 0
                ? new List<SourceChunkSummary> { new SourceChunkSummary { chunkId = "chunk_001", summary = "Mock source summary." } }
                : summaries;
            var chapters = new List<StoryChapter>();
            for (var i = 0; i < count; i++)
            {
                var summary = source[i % source.Count];
                chapters.Add(new StoryChapter
                {
                    chapterId = $"ch{i + 1:000}",
                    chapterIndex = i + 1,
                    chapterTitle = $"Mock Chapter {i + 1:000}",
                    summary = $"Adapted outline from {summary.chunkId}: {Trim(summary.summary, 100)}",
                    sourceChunkRefs = new List<string> { summary.chunkId },
                    toneTags = new List<string> { "dark-fairytale", "branching" },
                    status = StoryAuthoringStatuses.Draft
                });
            }

            return Task.FromResult(chapters);
        }

        public Task<List<StoryAnchorNode>> BuildAnchorNodesAsync(StoryChapter chapter, int minAnchors, int maxAnchors, CancellationToken cancellationToken = default)
        {
            chapter ??= new StoryChapter { chapterId = "ch001", chapterTitle = "Mock Chapter", summary = "Mock chapter summary." };
            var count = Mathf.Clamp(minAnchors, StoryAuthoringUtility.MinAnchorCount, StoryAuthoringUtility.MaxAnchorCount);
            count = Mathf.Clamp(count, 1, Mathf.Clamp(maxAnchors, count, StoryAuthoringUtility.MaxAnchorCount));
            var anchors = new List<StoryAnchorNode>();
            for (var i = 0; i < count; i++)
            {
                var anchor = new StoryAnchorNode
                {
                    nodeId = $"{chapter.chapterId}_anchor{i + 1:000}",
                    anchorIndex = i + 1,
                    title = $"{chapter.chapterTitle} Anchor {i + 1}",
                    body = $"A stable adapted beat for {chapter.chapterTitle}. The player faces pressure, cost, and a clear route back to the mainline.",
                    imagePrompt = $"symbolic medieval low-poly decision card, {chapter.chapterTitle}, anchor {i + 1}, flat silhouettes, muted red gold parchment palette",
                    stabilityNote = "Mock anchor keeps the mainline recoverable.",
                    mainlineChoice = "left",
                    leftChoice = new ChoiceOption
                    {
                        id = "continue_mainline",
                        label = "Continue",
                        intent = "Follow the required mainline anchor.",
                        direction = "left",
                        statHint = new PlayerStats(2, 0, -1, 1)
                    },
                    rightChoice = new ChoiceOption
                    {
                        id = "deviate_branch",
                        label = "Deviate",
                        intent = "Let AI generate a side branch before returning.",
                        direction = "right",
                        statHint = new PlayerStats(-2, 2, 0, -1)
                    },
                    status = StoryAuthoringStatuses.Draft
                };
                StoryAuthoringUtility.NormalizeAnchor(chapter, anchor, i + 1);
                anchors.Add(anchor);
            }

            return Task.FromResult(anchors);
        }

        private static string Trim(string value, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxCharacters ? trimmed : trimmed.Substring(0, maxCharacters);
        }
    }
}

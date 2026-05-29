using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBuilder
{
    [Serializable]
    public class StoryProject
    {
        public string projectId = "story_project";
        public string title = "AI Builder Story Project";
        public string sourceName = "";
        public int targetChapterCount = 100;
        public int minAnchorsPerChapter = 2;
        public int maxAnchorsPerChapter = 3;
        public int chunkCharacterLimit = 6000;
        public bool enableDefaultPanorama = true;
        [TextArea(2, 5)] public string defaultPanoramaPrompt = "";
        public string defaultPanoramaPath = "";
        public string defaultPanoramaStatus = "";
        public string defaultPanoramaError = "";
        public string createdAt = DateTime.UtcNow.ToString("O");
        public string updatedAt = DateTime.UtcNow.ToString("O");
        public List<SourceChunk> sourceChunks = new List<SourceChunk>();
        public List<SourceChunkSummary> summaries = new List<SourceChunkSummary>();
        public List<StoryChapter> chapters = new List<StoryChapter>();
        public AdaptationJobState job = new AdaptationJobState();
    }

    [Serializable]
    public class SourceChunk
    {
        public string id;
        public int index;
        public string titleGuess;
        [TextArea(6, 16)] public string text;
        [TextArea(2, 5)] public string summary;
        public string status = StoryAuthoringStatuses.Draft;
        public string error;
    }

    [Serializable]
    public class SourceChunkSummary
    {
        public string chunkId;
        public int chunkIndex;
        public string title;
        [TextArea(3, 8)] public string summary;
        public List<string> characters = new List<string>();
        public List<string> locations = new List<string>();
        public List<string> conflicts = new List<string>();
        public List<string> timelineNotes = new List<string>();
        public string status = StoryAuthoringStatuses.Draft;
    }

    [Serializable]
    public class StoryChapter
    {
        public string chapterId;
        public int chapterIndex;
        public string chapterTitle;
        [TextArea(3, 8)] public string summary;
        public List<string> sourceChunkRefs = new List<string>();
        public List<string> toneTags = new List<string>();
        public string status = StoryAuthoringStatuses.Draft;
        public List<StoryAnchorNode> anchors = new List<StoryAnchorNode>();
    }

    [Serializable]
    public class StoryAnchorNode
    {
        public string nodeId;
        public int anchorIndex;
        public string title;
        [TextArea(3, 8)] public string body;
        public string imageRef = "";
        public string imagePrompt;
        public string stabilityNote;
        public string mainlineChoice = "left";
        public string status = StoryAuthoringStatuses.Draft;
        public ChoiceOption leftChoice = StoryAuthoringUtility.NewChoice("left");
        public ChoiceOption rightChoice = StoryAuthoringUtility.NewChoice("right");
    }

    [Serializable]
    public class AdaptationJobState
    {
        public bool isRunning;
        public string stage = "Idle";
        public string message = "";
        public int completed;
        public int total;
        public string lastError = "";
        public string updatedAt = DateTime.UtcNow.ToString("O");
    }

    [Serializable]
    public class StoryAuthoringValidationResult
    {
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();

        [JsonIgnore] public bool IsValid => errors.Count == 0;

        public void Error(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                errors.Add(message);
            }
        }

        public void Warning(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                warnings.Add(message);
            }
        }

        public string Summary()
        {
            return IsValid
                ? $"Valid with {warnings.Count} warning(s)."
                : $"Invalid with {errors.Count} error(s), {warnings.Count} warning(s).";
        }
    }

    public static class StoryAuthoringPaths
    {
        public const string StoryProjectAssetPath = "Assets/AIBuilder/Data/story_project.json";
        public const string MainlineAssetPath = "Assets/AIBuilder/Resources/mainline_nodes.json";
    }

    public static class StoryAuthoringStatuses
    {
        public const string Draft = "Draft";
        public const string Summarized = "Summarized";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Failed = "Failed";

        public static readonly string[] ReviewStatuses = { Draft, Approved, Rejected, Failed };

        public static bool IsRejected(string status)
        {
            return string.Equals(status, Rejected, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeReviewStatus(string status)
        {
            return ReviewStatuses.Any(item => string.Equals(item, status, StringComparison.OrdinalIgnoreCase))
                ? ReviewStatuses.First(item => string.Equals(item, status, StringComparison.OrdinalIgnoreCase))
                : Draft;
        }
    }

    public static class StoryAuthoringUtility
    {
        public const int MinChapterCount = 1;
        public const int MaxChapterCount = 300;
        public const int MinAnchorCount = 1;
        public const int MaxAnchorCount = 5;
        public const int MinChunkCharacterLimit = 1000;
        public const int MaxChunkCharacterLimit = 20000;

        public static StoryProject CreateDefaultProject()
        {
            var project = new StoryProject();
            NormalizeProject(project);
            return project;
        }

        public static ChoiceOption NewChoice(string direction)
        {
            var isLeft = string.Equals(direction, "left", StringComparison.OrdinalIgnoreCase);
            return new ChoiceOption
            {
                id = isLeft ? "continue_mainline" : "deviate_branch",
                label = isLeft ? "Continue" : "Deviate",
                intent = isLeft ? "Follow the stable mainline." : "Let the player depart from the adapted source.",
                direction = isLeft ? "left" : "right",
                nextMainlineNodeId = "",
                statHint = new PlayerStats(0, 0, 0, 0)
            };
        }

        public static void NormalizeProject(StoryProject project)
        {
            if (project == null)
            {
                return;
            }

            project.projectId = string.IsNullOrWhiteSpace(project.projectId) ? "story_project" : SanitizeId(project.projectId);
            project.title = string.IsNullOrWhiteSpace(project.title) ? "AI Builder Story Project" : project.title.Trim();
            project.targetChapterCount = Mathf.Clamp(project.targetChapterCount, MinChapterCount, MaxChapterCount);
            project.chunkCharacterLimit = Mathf.Clamp(project.chunkCharacterLimit, MinChunkCharacterLimit, MaxChunkCharacterLimit);
            project.minAnchorsPerChapter = Mathf.Clamp(project.minAnchorsPerChapter, MinAnchorCount, MaxAnchorCount);
            project.maxAnchorsPerChapter = Mathf.Clamp(project.maxAnchorsPerChapter, project.minAnchorsPerChapter, MaxAnchorCount);
            project.sourceChunks ??= new List<SourceChunk>();
            project.summaries ??= new List<SourceChunkSummary>();
            project.chapters ??= new List<StoryChapter>();
            project.job ??= new AdaptationJobState();
            project.defaultPanoramaPrompt = string.IsNullOrWhiteSpace(project.defaultPanoramaPrompt) && project.sourceChunks.Count > 0
                ? BuildDefaultPanoramaPrompt(project)
                : (project.defaultPanoramaPrompt ?? "").Trim();
            project.defaultPanoramaPath = (project.defaultPanoramaPath ?? "").Trim();
            project.defaultPanoramaStatus = (project.defaultPanoramaStatus ?? "").Trim();
            project.defaultPanoramaError = (project.defaultPanoramaError ?? "").Trim();
            project.updatedAt = DateTime.UtcNow.ToString("O");

            for (var i = 0; i < project.sourceChunks.Count; i++)
            {
                var chunk = project.sourceChunks[i] ?? new SourceChunk();
                chunk.index = i + 1;
                chunk.id = string.IsNullOrWhiteSpace(chunk.id) ? $"chunk_{chunk.index:000}" : SanitizeId(chunk.id);
                chunk.titleGuess = string.IsNullOrWhiteSpace(chunk.titleGuess) ? $"Chunk {chunk.index:000}" : chunk.titleGuess.Trim();
                chunk.status = string.IsNullOrWhiteSpace(chunk.status) ? StoryAuthoringStatuses.Draft : chunk.status.Trim();
                project.sourceChunks[i] = chunk;
            }

            foreach (var summary in project.summaries.Where(item => item != null))
            {
                summary.chunkId = string.IsNullOrWhiteSpace(summary.chunkId) ? $"chunk_{Mathf.Max(1, summary.chunkIndex):000}" : SanitizeId(summary.chunkId);
                summary.status = string.IsNullOrWhiteSpace(summary.status) ? StoryAuthoringStatuses.Draft : summary.status.Trim();
                summary.characters ??= new List<string>();
                summary.locations ??= new List<string>();
                summary.conflicts ??= new List<string>();
                summary.timelineNotes ??= new List<string>();
            }

            var orderedChapters = project.chapters
                .Where(item => item != null)
                .OrderBy(item => item.chapterIndex <= 0 ? int.MaxValue : item.chapterIndex)
                .ToList();
            for (var chapterOffset = 0; chapterOffset < orderedChapters.Count; chapterOffset++)
            {
                NormalizeChapter(orderedChapters[chapterOffset], chapterOffset + 1);
            }

            project.chapters = orderedChapters;
        }

        public static List<SourceChunk> ImportSourceText(StoryProject project, string sourceText, string sourceName, int chunkCharacterLimit = 0)
        {
            project ??= CreateDefaultProject();
            var limit = Mathf.Clamp(chunkCharacterLimit > 0 ? chunkCharacterLimit : project.chunkCharacterLimit, MinChunkCharacterLimit, MaxChunkCharacterLimit);
            var chunks = SplitText(sourceText ?? "", limit)
                .Select((text, index) => new SourceChunk
                {
                    id = $"chunk_{index + 1:000}",
                    index = index + 1,
                    titleGuess = GuessTitle(text, index + 1),
                    text = text,
                    summary = "",
                    status = StoryAuthoringStatuses.Draft,
                    error = ""
                })
                .ToList();

            project.sourceName = sourceName ?? "";
            project.projectId = BuildImportedStoryId(sourceName, sourceText);
            project.chunkCharacterLimit = limit;
            project.sourceChunks = chunks;
            project.summaries ??= new List<SourceChunkSummary>();
            project.chapters ??= new List<StoryChapter>();
            project.summaries.Clear();
            project.chapters.Clear();
            project.defaultPanoramaPrompt = BuildDefaultPanoramaPrompt(project);
            project.defaultPanoramaPath = "";
            project.defaultPanoramaStatus = project.enableDefaultPanorama ? NodeImageStatuses.Queued : "";
            project.defaultPanoramaError = "";
            NormalizeProject(project);
            return project.sourceChunks;
        }

        public static string BuildDefaultPanoramaPrompt(StoryProject project)
        {
            if (project == null)
            {
                return "story-wide medieval realm establishing panorama, distant seat of power, roads, horizon, subdued royal decision-card mood";
            }

            var locations = (project.summaries ?? new List<SourceChunkSummary>())
                .Where(item => item != null)
                .SelectMany(item => item.locations ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => TrimPromptPart(item, 72))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var tones = new List<string>();
            tones.AddRange((project.chapters ?? new List<StoryChapter>())
                .Where(item => item != null)
                .SelectMany(item => item.toneTags ?? new List<string>()));
            tones.AddRange((project.summaries ?? new List<SourceChunkSummary>())
                .Where(item => item != null)
                .SelectMany(item => item.conflicts ?? new List<string>()));
            tones = tones
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => TrimPromptPart(item, 72))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var sceneHints = RepresentativeChunks(project.sourceChunks)
                .SelectMany(chunk => new[]
                {
                    chunk.titleGuess,
                    FirstMeaningfulLine(chunk.text)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => TrimPromptPart(item, 96))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            if (locations.Count == 0)
            {
                locations.AddRange(sceneHints.Take(2));
            }

            var builder = new StringBuilder();
            builder.Append("Story-wide establishing background panorama for a royal decision-card UI. ");
            builder.Append("Default far-view world before any specific branch: distant seat of power, roads, horizon, and environmental symbols implied by the story; no close-up character focus. ");
            AppendPromptList(builder, "Story identity", new[] { project.title, project.sourceName });
            AppendPromptList(builder, "Primary locations", locations);
            AppendPromptList(builder, "Mood and conflicts", tones);
            AppendPromptList(builder, "Source scene hints", sceneHints);
            builder.Append("Use a wide layered horizon and enough calm negative space behind the central card UI.");
            return TrimPromptPart(builder.ToString(), 1000);
        }

        public static void NormalizeChapter(StoryChapter chapter, int fallbackIndex)
        {
            if (chapter == null)
            {
                return;
            }

            chapter.chapterIndex = chapter.chapterIndex <= 0 ? fallbackIndex : chapter.chapterIndex;
            chapter.chapterId = string.IsNullOrWhiteSpace(chapter.chapterId) ? $"ch{chapter.chapterIndex:000}" : SanitizeId(chapter.chapterId);
            chapter.chapterTitle = string.IsNullOrWhiteSpace(chapter.chapterTitle) ? $"Chapter {chapter.chapterIndex:000}" : chapter.chapterTitle.Trim();
            chapter.status = StoryAuthoringStatuses.NormalizeReviewStatus(chapter.status);
            chapter.sourceChunkRefs ??= new List<string>();
            chapter.toneTags ??= new List<string>();
            chapter.anchors ??= new List<StoryAnchorNode>();

            var orderedAnchors = chapter.anchors
                .Where(item => item != null)
                .OrderBy(item => item.anchorIndex <= 0 ? int.MaxValue : item.anchorIndex)
                .ToList();
            for (var anchorOffset = 0; anchorOffset < orderedAnchors.Count; anchorOffset++)
            {
                NormalizeAnchor(chapter, orderedAnchors[anchorOffset], anchorOffset + 1);
            }

            chapter.anchors = orderedAnchors;
        }

        public static void NormalizeAnchor(StoryChapter chapter, StoryAnchorNode anchor, int fallbackIndex)
        {
            if (chapter == null || anchor == null)
            {
                return;
            }

            anchor.anchorIndex = anchor.anchorIndex <= 0 ? fallbackIndex : anchor.anchorIndex;
            anchor.nodeId = string.IsNullOrWhiteSpace(anchor.nodeId)
                ? $"{chapter.chapterId}_anchor{anchor.anchorIndex:000}"
                : SanitizeId(anchor.nodeId);
            anchor.title = string.IsNullOrWhiteSpace(anchor.title) ? $"{chapter.chapterTitle} / Anchor {anchor.anchorIndex}" : anchor.title.Trim();
            anchor.body = string.IsNullOrWhiteSpace(anchor.body) ? chapter.summary : anchor.body.Trim();
            anchor.imagePrompt = string.IsNullOrWhiteSpace(anchor.imagePrompt)
                ? $"symbolic medieval low-poly decision card, {anchor.title}, {chapter.chapterTitle}, flat silhouettes, muted red gold parchment palette"
                : anchor.imagePrompt.Trim();
            anchor.mainlineChoice = string.Equals(anchor.mainlineChoice, "right", StringComparison.OrdinalIgnoreCase) ? "right" : "left";
            anchor.status = StoryAuthoringStatuses.NormalizeReviewStatus(anchor.status);
            anchor.leftChoice ??= NewChoice("left");
            anchor.rightChoice ??= NewChoice("right");
            NormalizeChoice(anchor.leftChoice, "left");
            NormalizeChoice(anchor.rightChoice, "right");
        }

        public static StoryAuthoringValidationResult ValidateProject(StoryProject project)
        {
            var result = new StoryAuthoringValidationResult();
            if (project == null)
            {
                result.Error("Story project is missing.");
                return result;
            }

            NormalizeProject(project);
            if (project.sourceChunks.Count == 0)
            {
                result.Warning("No source chunks imported yet.");
            }

            var activeChapters = project.chapters.Where(chapter => !StoryAuthoringStatuses.IsRejected(chapter.status)).ToList();
            if (activeChapters.Count == 0)
            {
                result.Error("No active chapters are available for export.");
                return result;
            }

            foreach (var chapter in activeChapters)
            {
                if (string.IsNullOrWhiteSpace(chapter.chapterTitle))
                {
                    result.Error($"{chapter.chapterId} has no title.");
                }

                if (!string.Equals(chapter.status, StoryAuthoringStatuses.Approved, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warning($"{chapter.chapterId} is not approved.");
                }

                var activeAnchors = chapter.anchors.Where(anchor => !StoryAuthoringStatuses.IsRejected(anchor.status)).ToList();
                if (activeAnchors.Count < project.minAnchorsPerChapter)
                {
                    result.Error($"{chapter.chapterId} has {activeAnchors.Count} active anchor(s), expected at least {project.minAnchorsPerChapter}.");
                }

                foreach (var anchor in activeAnchors)
                {
                    ValidateAnchor(project, chapter, anchor, result);
                }
            }

            return result;
        }

        public static StoryGraph ExportToStoryGraph(StoryProject project)
        {
            NormalizeProject(project);
            var exported = new List<(StoryChapter chapter, StoryAnchorNode anchor)>();
            foreach (var chapter in project.chapters.Where(item => !StoryAuthoringStatuses.IsRejected(item.status)))
            {
                foreach (var anchor in chapter.anchors.Where(item => !StoryAuthoringStatuses.IsRejected(item.status)))
                {
                    exported.Add((chapter, anchor));
                }
            }

            var graph = new StoryGraph
            {
                chapterId = string.IsNullOrWhiteSpace(project.projectId) ? "story_project" : project.projectId,
                chapterTitle = project.title,
                enableDefaultPanorama = project.enableDefaultPanorama,
                defaultPanoramaPrompt = project.defaultPanoramaPrompt ?? "",
                defaultPanoramaPath = project.defaultPanoramaPath ?? "",
                defaultPanoramaStatus = project.defaultPanoramaStatus ?? "",
                nodes = new List<StoryNode>()
            };

            for (var i = 0; i < exported.Count; i++)
            {
                var chapter = exported[i].chapter;
                var anchor = exported[i].anchor;
                var nextId = i + 1 < exported.Count ? exported[i + 1].anchor.nodeId : "";
                var left = CloneChoice(anchor.leftChoice, "left");
                var right = CloneChoice(anchor.rightChoice, "right");
                if (string.Equals(anchor.mainlineChoice, "right", StringComparison.OrdinalIgnoreCase))
                {
                    right.nextMainlineNodeId = nextId;
                }
                else
                {
                    left.nextMainlineNodeId = nextId;
                }

                graph.nodes.Add(new StoryNode
                {
                    id = anchor.nodeId,
                    chapterId = chapter.chapterId,
                    title = anchor.title,
                    body = anchor.body,
                    imageRef = string.IsNullOrWhiteSpace(anchor.imageRef) ? DefaultImageRef(i) : anchor.imageRef,
                    nodeKind = StoryNodeKind.Mainline,
                    leftChoice = left,
                    rightChoice = right,
                    mainlineIndex = i + 1
                });
            }

            return graph;
        }

        public static string SanitizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "id";
            }

            var chars = value.Trim().ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray();
            var compact = new string(chars);
            while (compact.Contains("__", StringComparison.Ordinal))
            {
                compact = compact.Replace("__", "_");
            }

            return compact.Trim('_');
        }

        private static void ValidateAnchor(StoryProject project, StoryChapter chapter, StoryAnchorNode anchor, StoryAuthoringValidationResult result)
        {
            if (!string.Equals(anchor.status, StoryAuthoringStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                result.Warning($"{anchor.nodeId} is not approved.");
            }

            if (string.IsNullOrWhiteSpace(anchor.title))
            {
                result.Error($"{chapter.chapterId}/{anchor.nodeId} has no title.");
            }

            if (string.IsNullOrWhiteSpace(anchor.body))
            {
                result.Error($"{chapter.chapterId}/{anchor.nodeId} has no body.");
            }

            if (anchor.leftChoice == null || anchor.rightChoice == null)
            {
                result.Error($"{chapter.chapterId}/{anchor.nodeId} must have left and right choices.");
                return;
            }

            if (string.IsNullOrWhiteSpace(anchor.leftChoice.label) || string.IsNullOrWhiteSpace(anchor.rightChoice.label))
            {
                result.Error($"{chapter.chapterId}/{anchor.nodeId} has an empty choice label.");
            }

            if (!string.Equals(anchor.mainlineChoice, "left", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(anchor.mainlineChoice, "right", StringComparison.OrdinalIgnoreCase))
            {
                result.Error($"{chapter.chapterId}/{anchor.nodeId} has invalid mainlineChoice.");
            }

            if (anchor.body != null && anchor.body.Length > 180)
            {
                result.Warning($"{chapter.chapterId}/{anchor.nodeId} body is longer than the 80-120 character target.");
            }
        }

        private static void NormalizeChoice(ChoiceOption choice, string direction)
        {
            choice.direction = string.Equals(direction, "right", StringComparison.OrdinalIgnoreCase) ? "right" : "left";
            choice.id = string.IsNullOrWhiteSpace(choice.id) ? (choice.direction == "left" ? "continue_mainline" : "deviate_branch") : SanitizeId(choice.id);
            choice.label = string.IsNullOrWhiteSpace(choice.label) ? (choice.direction == "left" ? "Continue" : "Deviate") : choice.label.Trim();
            choice.intent = string.IsNullOrWhiteSpace(choice.intent) ? "Player-facing branch choice." : choice.intent.Trim();
            choice.nextMainlineNodeId ??= "";
            choice.statHint ??= new PlayerStats(0, 0, 0, 0);
        }

        private static ChoiceOption CloneChoice(ChoiceOption choice, string direction)
        {
            choice ??= NewChoice(direction);
            return new ChoiceOption
            {
                id = string.IsNullOrWhiteSpace(choice.id) ? (direction == "left" ? "continue_mainline" : "deviate_branch") : choice.id,
                label = choice.label ?? "",
                intent = choice.intent ?? "",
                direction = string.IsNullOrWhiteSpace(choice.direction) ? direction : choice.direction,
                nextMainlineNodeId = choice.nextMainlineNodeId ?? "",
                statHint = choice.statHint == null ? new PlayerStats(0, 0, 0, 0) : choice.statHint.Clone()
            };
        }

        private static List<string> SplitText(string sourceText, int chunkCharacterLimit)
        {
            var chunks = new List<string>();
            var normalized = (sourceText ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return chunks;
            }

            var start = 0;
            while (start < normalized.Length)
            {
                var windowLength = Mathf.Min(chunkCharacterLimit, normalized.Length - start);
                var end = start + windowLength;
                if (end < normalized.Length)
                {
                    var window = normalized.Substring(start, windowLength);
                    var breakAt = window.LastIndexOf("\n\n", StringComparison.Ordinal);
                    if (breakAt < chunkCharacterLimit / 2)
                    {
                        breakAt = window.LastIndexOf('\n');
                    }

                    if (breakAt > chunkCharacterLimit / 3)
                    {
                        end = start + breakAt + 1;
                    }
                }

                var chunk = normalized.Substring(start, end - start).Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    chunks.Add(chunk);
                }

                start = end;
                while (start < normalized.Length && char.IsWhiteSpace(normalized[start]))
                {
                    start++;
                }
            }

            return chunks;
        }

        private static IEnumerable<SourceChunk> RepresentativeChunks(List<SourceChunk> chunks)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return Enumerable.Empty<SourceChunk>();
            }

            return new[] { 0, chunks.Count / 2, chunks.Count - 1 }
                .Distinct()
                .Where(index => index >= 0 && index < chunks.Count)
                .Select(index => chunks[index])
                .Where(chunk => chunk != null);
        }

        private static string FirstMeaningfulLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            return (text ?? "")
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim().TrimStart('#').Trim())
                .FirstOrDefault(item => item.Length >= 4) ?? "";
        }

        private static void AppendPromptList(StringBuilder builder, string label, IEnumerable<string> values)
        {
            var compact = (values ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => TrimPromptPart(item, 96))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            if (compact.Count == 0)
            {
                return;
            }

            builder.Append(label).Append(": ").Append(string.Join(", ", compact)).Append(". ");
        }

        private static string TrimPromptPart(string value, int maxLength)
        {
            var compact = string.Join(" ", (value ?? "")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (compact.Length <= maxLength)
            {
                return compact.Trim();
            }

            return compact.Substring(0, Mathf.Max(0, maxLength - 3)).Trim() + "...";
        }

        private static string GuessTitle(string text, int index)
        {
            var line = (text ?? "")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim().TrimStart('#').Trim())
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            if (string.IsNullOrWhiteSpace(line))
            {
                return $"Chunk {index:000}";
            }

            return line.Length <= 48 ? line : line.Substring(0, 48);
        }

        private static string BuildImportedStoryId(string sourceName, string sourceText)
        {
            var baseName = SanitizeId(string.IsNullOrWhiteSpace(sourceName) ? "story" : sourceName);
            return $"{baseName}_{StableHashHex(sourceText)}";
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

        private static string DefaultImageRef(int index)
        {
            return index % 3 == 0 ? "queen" : index % 3 == 1 ? "oracle" : "gate";
        }
    }
}

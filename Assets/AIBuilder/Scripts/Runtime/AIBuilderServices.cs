using System;
using System.Collections.Generic;
using System.IO;
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
    public interface IAiTextService
    {
        Task<AiTextResult> GenerateNextNodeAsync(StoryNode context, ChoiceOption choice, PlayerStats stats, CancellationToken cancellationToken);
    }

    public interface IAiImageService
    {
        Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken);
    }

    public sealed class StoryRepository
    {
        private readonly Dictionary<string, StoryNode> nodesById = new Dictionary<string, StoryNode>();

        public StoryGraph Graph { get; private set; }

        public StoryRepository()
        {
            Graph = LoadGraph();
            nodesById = Graph.nodes.ToDictionary(node => node.id, node => node);
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
                    Debug.LogWarning($"AI Builder mainline JSON ignored: {ex.Message}");
                }
            }

            return CreateDefaultGraph();
        }

        public static StoryGraph CreateDefaultGraph()
        {
            return new StoryGraph
            {
                chapterId = "chapter_001",
                chapterTitle = "灰冠初夜",
                nodes = new List<StoryNode>
                {
                    new StoryNode
                    {
                        id = "main_001",
                        chapterId = "chapter_001",
                        title = "苔阶上的王冠",
                        body = "旧王在钟声里离世，灰冠被送到你的掌心。主教低声说，城外的饥民正在等待新王的第一句话。",
                        imageRef = "queen",
                        nodeKind = StoryNodeKind.Mainline,
                        mainlineIndex = 1,
                        leftChoice = new ChoiceOption
                        {
                            id = "bless_crowd",
                            label = "向饥民开仓",
                            intent = "安抚民心，牺牲财富换取信仰。",
                            direction = "left",
                            nextMainlineNodeId = "main_002",
                            statHint = new PlayerStats(4, 0, -9, 8)
                        },
                        rightChoice = new ChoiceOption
                        {
                            id = "call_guards",
                            label = "召集卫兵",
                            intent = "以武力维持秩序，触发一个偏离主干的分支。",
                            direction = "right",
                            statHint = new PlayerStats(-3, 8, 0, -4)
                        }
                    },
                    new StoryNode
                    {
                        id = "main_002",
                        chapterId = "chapter_001",
                        title = "密室里的绿光",
                        body = "夜半，宫廷术士献上一枚发绿的玻璃匣。里面传来与你同名的声音，问你是否相信命运已经写好。",
                        imageRef = "oracle",
                        nodeKind = StoryNodeKind.Mainline,
                        mainlineIndex = 2,
                        leftChoice = new ChoiceOption
                        {
                            id = "open_relic",
                            label = "打开匣子",
                            intent = "接受神秘力量，触发一个偏离主干的分支。",
                            direction = "left",
                            statHint = new PlayerStats(-6, 4, 0, 5)
                        },
                        rightChoice = new ChoiceOption
                        {
                            id = "seal_relic",
                            label = "封存匣子",
                            intent = "拒绝诱惑，稳定地进入下一个主干节点。",
                            direction = "right",
                            nextMainlineNodeId = "main_003",
                            statHint = new PlayerStats(3, -2, 2, 4)
                        }
                    },
                    new StoryNode
                    {
                        id = "main_003",
                        chapterId = "chapter_001",
                        title = "城门前的黎明",
                        body = "天亮时，邻国使者抵达破旧城门。他的车队带着粮食，也带着一份要求你让出边境矿山的盟约。",
                        imageRef = "gate",
                        nodeKind = StoryNodeKind.Mainline,
                        mainlineIndex = 3,
                        leftChoice = new ChoiceOption
                        {
                            id = "sign_treaty",
                            label = "签下盟约",
                            intent = "用财富和和平换取生命。",
                            direction = "left",
                            statHint = new PlayerStats(8, -4, -8, 2)
                        },
                        rightChoice = new ChoiceOption
                        {
                            id = "reject_treaty",
                            label = "拒绝使者",
                            intent = "保住矿山，但战争阴影逼近。",
                            direction = "right",
                            statHint = new PlayerStats(-6, 8, 7, -6)
                        }
                    }
                }
            };
        }
    }

    public sealed class NodeCacheService
    {
        private NodeCacheDatabase database = new NodeCacheDatabase();

        public static string CacheDirectory => Path.Combine(Application.persistentDataPath, "AIBuilder");
        public static string CacheFilePath => Path.Combine(CacheDirectory, "node_cache.json");

        public IReadOnlyList<NodeCacheEntry> Entries => database.entries;

        public NodeCacheService()
        {
            Load();
        }

        public static string CreateCacheKey(StoryNode source, ChoiceOption choice, PlayerStats stats)
        {
            var sourceId = source == null ? "none" : source.id;
            var choiceId = choice == null ? "none" : choice.id;
            var band = stats == null ? "0-0-0-0" : stats.BandKey();
            return $"{sourceId}|{choiceId}|{band}".ToLowerInvariant();
        }

        public bool TryGet(string key, out NodeCacheEntry entry)
        {
            entry = database.entries.FirstOrDefault(item => item.cacheKey == key && item.status != "Rejected");
            return entry != null;
        }

        public void Put(NodeCacheEntry entry)
        {
            var index = database.entries.FindIndex(item => item.cacheKey == entry.cacheKey);
            if (index >= 0)
            {
                database.entries[index] = entry;
            }
            else
            {
                database.entries.Add(entry);
            }

            Save();
        }

        public void Clear()
        {
            database.entries.Clear();
            Save();
        }

        public string SaveImage(string cacheKey, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return "";
            }

            Directory.CreateDirectory(CacheDirectory);
            var safeName = Convert.ToBase64String(Encoding.UTF8.GetBytes(cacheKey))
                .Replace("/", "_")
                .Replace("+", "-")
                .TrimEnd('=');
            var path = Path.Combine(CacheDirectory, $"{safeName}.png");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private void Load()
        {
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    var json = File.ReadAllText(CacheFilePath);
                    database = JsonConvert.DeserializeObject<NodeCacheDatabase>(json) ?? new NodeCacheDatabase();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder cache ignored: {ex.Message}");
                database = new NodeCacheDatabase();
            }
        }

        private void Save()
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(CacheFilePath, JsonConvert.SerializeObject(database, Formatting.Indented));
        }
    }

    public sealed class OpenAiCompatibleTextService : IAiTextService
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

                if (!string.IsNullOrWhiteSpace(settings.reasoningEffort))
                {
                    payload["reasoning"] = new JObject { ["effort"] = settings.reasoningEffort };
                }

                var json = await PostJsonAsync($"{settings.baseUrl.TrimEnd('/')}/responses", payload, timeout.Token);
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

        private static string BuildPrompt(StoryNode context, ChoiceOption choice, PlayerStats stats)
        {
            return "你是互动视觉小说《灰冠初夜》的剧情生成器。"
                   + "请根据当前节点、玩家选择和数值生成一个短分支节点。"
                   + "只输出 JSON，不要 Markdown。JSON 字段必须是："
                   + "storyText, leftChoice, rightChoice, statDelta{life,force,wealth,faith}, imagePrompt, summaryTags。"
                   + "statDelta 每项必须在 -15 到 15。故事中文，80字以内，低多边形黑暗童话风格。"
                   + $"\n当前节点：{context?.title}\n正文：{context?.body}\n选择：{choice?.label} / {choice?.intent}"
                   + $"\n数值：生命{stats?.life} 武力{stats?.force} 财富{stats?.wealth} 信仰{stats?.faith}";
        }

        private async Task<string> PostJsonAsync(string url, JObject payload, CancellationToken cancellationToken)
        {
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            var body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
            request.timeout = Mathf.Max(5, settings.timeoutSeconds);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException($"{request.responseCode}: {request.error} {request.downloadHandler.text}");
            }

            return request.downloadHandler.text;
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

        private static bool TryParseTextResult(string text, out AiTextResult result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            var json = text.Substring(start, end - start + 1);
            try
            {
                result = JsonConvert.DeserializeObject<AiTextResult>(json);
                return result != null
                       && !string.IsNullOrWhiteSpace(result.storyText)
                       && !string.IsNullOrWhiteSpace(result.leftChoice)
                       && !string.IsNullOrWhiteSpace(result.rightChoice);
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class OpenAiCompatibleImageService : IAiImageService
    {
        private readonly AiProviderSettings settings;

        public OpenAiCompatibleImageService(AiProviderSettings settings)
        {
            this.settings = settings;
        }

        public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken)
        {
            if (!settings.CanUseImage || string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Mathf.Max(8, settings.timeoutSeconds)));

                var payload = new JObject
                {
                    ["model"] = settings.imageModel,
                    ["input"] = "Create one square low-poly Reigns-style visual novel card image. " + prompt,
                    ["tools"] = new JArray(new JObject { ["type"] = "image_generation" }),
                    ["store"] = !settings.disableResponseStorage
                };

                var json = await PostJsonAsync($"{settings.baseUrl.TrimEnd('/')}/responses", payload, timeout.Token);
                var root = JToken.Parse(json);
                var base64 = FindBase64Image(root);
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    return Convert.FromBase64String(StripDataUrl(base64));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder image generation skipped: {ex.Message}");
            }

            return null;
        }

        private async Task<string> PostJsonAsync(string url, JObject payload, CancellationToken cancellationToken)
        {
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            var body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
            request.timeout = Mathf.Max(8, settings.timeoutSeconds);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException($"{request.responseCode}: {request.error} {request.downloadHandler.text}");
            }

            return request.downloadHandler.text;
        }

        private static string FindBase64Image(JToken root)
        {
            foreach (var property in WalkTokens(root).OfType<JProperty>())
            {
                var name = property.Name.ToLowerInvariant();
                if ((name.Contains("base64") || name == "b64_json" || name == "image_base64")
                    && property.Value.Type == JTokenType.String)
                {
                    var value = property.Value.Value<string>();
                    if (!string.IsNullOrWhiteSpace(value) && value.Length > 200)
                    {
                        return value;
                    }
                }
            }

            return "";
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
    }

    public sealed class MockAiTextService : IAiTextService
    {
        public Task<AiTextResult> GenerateNextNodeAsync(StoryNode context, ChoiceOption choice, PlayerStats stats, CancellationToken cancellationToken)
        {
            var seed = Mathf.Abs(NodeCacheService.CreateCacheKey(context, choice, stats).GetHashCode());
            var omens = new[] { "钟声忽然倒转", "宫灯一盏盏熄灭", "远处传来铁靴声", "旧王画像渗出金粉" };
            var outcomes = new[] { "人群暂时退去", "贵族们开始低语", "守卫跪下等待命令", "术士记录下你的名字" };

            var result = new AiTextResult
            {
                storyText = $"{choice?.label}之后，{omens[seed % omens.Length]}。{outcomes[(seed / 3) % outcomes.Length]}，灰冠的重量变得更真实。",
                leftChoice = "追问真相",
                rightChoice = "回到王座",
                statDelta = new PlayerStats(
                    (seed % 9) - 4,
                    ((seed / 10) % 11) - 5,
                    ((seed / 100) % 13) - 6,
                    ((seed / 1000) % 9) - 4),
                imagePrompt = $"low-poly dark fairytale ruler card, {context?.title}, {choice?.label}, flat shapes, limited palette",
                summaryTags = new List<string> { "mock", "branch", choice?.id ?? "choice" }
            };

            return Task.FromResult(result);
        }
    }
}

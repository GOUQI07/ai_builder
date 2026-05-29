using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBuilder
{
    public enum StoryNodeKind
    {
        Mainline,
        GeneratedBranch,
        Ending
    }

    [Serializable]
    public class ChoiceOption
    {
        public string id;
        public string label;
        public string intent;
        public string direction;
        public string nextMainlineNodeId;
        public PlayerStats statHint = new PlayerStats(0, 0, 0, 0);
    }

    [Serializable]
    public class StoryNode
    {
        public string id;
        public string chapterId;
        public string title;
        [TextArea(3, 8)] public string body;
        public string imageRef;
        public StoryNodeKind nodeKind;
        public ChoiceOption leftChoice;
        public ChoiceOption rightChoice;
        public int mainlineIndex;
    }

    [Serializable]
    public class StoryGraph
    {
        public string chapterId;
        public string chapterTitle;
        public List<StoryNode> nodes = new List<StoryNode>();
    }

    [Serializable]
    public class PlayerStats
    {
        public int life;
        public int force;
        public int wealth;
        public int faith;

        public PlayerStats()
        {
            life = 70;
            force = 50;
            wealth = 50;
            faith = 50;
        }

        public PlayerStats(int life, int force, int wealth, int faith)
        {
            this.life = life;
            this.force = force;
            this.wealth = wealth;
            this.faith = faith;
        }

        public PlayerStats Clone()
        {
            return new PlayerStats(life, force, wealth, faith);
        }

        public void Apply(PlayerStats delta)
        {
            if (delta == null)
            {
                return;
            }

            life = Mathf.Clamp(life + Mathf.Clamp(delta.life, -15, 15), 0, 100);
            force = Mathf.Clamp(force + Mathf.Clamp(delta.force, -15, 15), 0, 100);
            wealth = Mathf.Clamp(wealth + Mathf.Clamp(delta.wealth, -15, 15), 0, 100);
            faith = Mathf.Clamp(faith + Mathf.Clamp(delta.faith, -15, 15), 0, 100);
        }

        public bool IsGameOver()
        {
            return life <= 0 || wealth <= 0 || faith <= 0;
        }

        public string BandKey()
        {
            return $"{life / 10}-{force / 10}-{wealth / 10}-{faith / 10}";
        }
    }

    [Serializable]
    public class AiTextResult
    {
        public string storyText;
        public string leftChoice;
        public string rightChoice;
        public PlayerStats statDelta = new PlayerStats(0, 0, 0, 0);
        public string imagePrompt;
        public List<string> summaryTags = new List<string>();
    }

    [Serializable]
    public class NodeCacheEntry
    {
        public string cacheKey;
        public string sourceNodeId;
        public string choiceId;
        public StoryNode resultNode;
        public PlayerStats statDelta = new PlayerStats(0, 0, 0, 0);
        public string imagePath;
        public string createdAt;
        public string status;
    }

    [Serializable]
    public class NodeCacheDatabase
    {
        public List<NodeCacheEntry> entries = new List<NodeCacheEntry>();
    }

    [Serializable]
    public class AiProviderSettings
    {
        public string baseUrl = "https://api.asxs.top/v1";
        public string wireApi = "responses";
        public string textModel = "";
        public string imageModel = "gpt-image-2";
        public string apiKeyEnvName = "OPENAI_API_KEY";
        public bool disableResponseStorage = true;
        public string reasoningEffort = "xhigh";
        public int timeoutSeconds = 25;

        [JsonIgnore] public string ApiKey { get; private set; }

        public bool CanUseText => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(textModel);
        public bool CanUseImage => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(imageModel);

        public static string LocalConfigPath =>
            Path.Combine(Application.persistentDataPath, "AIBuilder", "ai_provider.json");

        public static AiProviderSettings Load()
        {
            var settings = new AiProviderSettings();
            var dotenv = LoadDotEnv();

            try
            {
                if (File.Exists(LocalConfigPath))
                {
                    var json = File.ReadAllText(LocalConfigPath);
                    var loaded = JsonConvert.DeserializeObject<AiProviderSettings>(json);
                    if (loaded != null)
                    {
                        settings = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder provider config ignored: {ex.Message}");
            }

            var envTextModel = GetConfigValue(dotenv, "AI_BUILDER_TEXT_MODEL", "MODEL");
            if (!string.IsNullOrWhiteSpace(envTextModel))
            {
                settings.textModel = envTextModel;
            }

            var envImageModel = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_MODEL", "REVIEW_MODEL");
            if (!string.IsNullOrWhiteSpace(envImageModel))
            {
                settings.imageModel = envImageModel;
            }

            var envBaseUrl = GetConfigValue(dotenv, "AI_BUILDER_BASE_URL", "OPENAI_BASE_URL", "BASE_URL");
            if (!string.IsNullOrWhiteSpace(envBaseUrl))
            {
                settings.baseUrl = envBaseUrl;
            }

            var envWireApi = GetConfigValue(dotenv, "AI_BUILDER_WIRE_API", "WIRE_API");
            if (!string.IsNullOrWhiteSpace(envWireApi))
            {
                settings.wireApi = envWireApi;
            }

            var envReasoning = GetConfigValue(dotenv, "AI_BUILDER_REASONING_EFFORT", "MODEL_REASONING_EFFORT");
            if (!string.IsNullOrWhiteSpace(envReasoning))
            {
                settings.reasoningEffort = envReasoning;
            }

            var disableStorage = GetConfigValue(dotenv, "AI_BUILDER_DISABLE_RESPONSE_STORAGE", "DISABLE_RESPONSE_STORAGE");
            if (bool.TryParse(disableStorage, out var parsedDisableStorage))
            {
                settings.disableResponseStorage = parsedDisableStorage;
            }

            var keyName = string.IsNullOrWhiteSpace(settings.apiKeyEnvName) ? "OPENAI_API_KEY" : settings.apiKeyEnvName;
            settings.ApiKey = GetConfigValue(dotenv, keyName);
            return settings;
        }

        private static string GetConfigValue(IReadOnlyDictionary<string, string> dotenv, params string[] keys)
        {
            foreach (var key in keys)
            {
                var environmentValue = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(environmentValue))
                {
                    return environmentValue;
                }

                if (dotenv != null && dotenv.TryGetValue(key, out var dotenvValue) && !string.IsNullOrWhiteSpace(dotenvValue))
                {
                    return dotenvValue;
                }
            }

            return "";
        }

        private static Dictionary<string, string> LoadDotEnv()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in GetDotEnvPaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    foreach (var rawLine in File.ReadAllLines(path))
                    {
                        var line = rawLine.Trim();
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var separator = line.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        var key = line.Substring(0, separator).Trim();
                        var value = line.Substring(separator + 1).Trim().Trim('"', '\'');
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            values[key] = value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"AI Builder .env ignored: {ex.Message}");
                }
            }

            return values;
        }

        private static IEnumerable<string> GetDotEnvPaths()
        {
            yield return Path.Combine(Application.dataPath, "..", ".env");
            yield return Path.Combine(Directory.GetCurrentDirectory(), ".env");
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
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
        public bool enableDefaultPanorama = true;
        public string defaultPanoramaPrompt;
        public string defaultPanoramaPath;
        public string defaultPanoramaStatus;
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

        public static PlayerStats ClampDelta(PlayerStats delta, int maxAbs)
        {
            if (delta == null)
            {
                return new PlayerStats(0, 0, 0, 0);
            }

            var limit = Mathf.Clamp(Mathf.Abs(maxAbs), 0, 100);
            return new PlayerStats(
                Mathf.Clamp(delta.life, -limit, limit),
                Mathf.Clamp(delta.force, -limit, limit),
                Mathf.Clamp(delta.wealth, -limit, limit),
                Mathf.Clamp(delta.faith, -limit, limit));
        }

        public static bool IsZeroDelta(PlayerStats delta)
        {
            return delta == null || (delta.life == 0 && delta.force == 0 && delta.wealth == 0 && delta.faith == 0);
        }

        public static int TotalAbs(PlayerStats delta)
        {
            return delta == null
                ? 0
                : Mathf.Abs(delta.life) + Mathf.Abs(delta.force) + Mathf.Abs(delta.wealth) + Mathf.Abs(delta.faith);
        }

        public void Apply(PlayerStats delta)
        {
            Apply(delta, 15);
        }

        public void Apply(PlayerStats delta, int maxAbs)
        {
            if (delta == null)
            {
                return;
            }

            var limit = Mathf.Clamp(Mathf.Abs(maxAbs), 0, 100);
            life = Mathf.Clamp(life + Mathf.Clamp(delta.life, -limit, limit), 0, 100);
            force = Mathf.Clamp(force + Mathf.Clamp(delta.force, -limit, limit), 0, 100);
            wealth = Mathf.Clamp(wealth + Mathf.Clamp(delta.wealth, -limit, limit), 0, 100);
            faith = Mathf.Clamp(faith + Mathf.Clamp(delta.faith, -limit, limit), 0, 100);
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
    public class StatJudgementResult
    {
        public PlayerStats statDelta = new PlayerStats(0, 0, 0, 0);
        public string reason;
    }

    [Serializable]
    public class AiTextResult
    {
        public string storyText;
        public string leftChoice;
        public string rightChoice;
        public PlayerStats statDelta = new PlayerStats(0, 0, 0, 0);
        public string imagePrompt;
        public string panoramaPrompt;
        public string locationTag;
        public string moodTag;
        public string majorEventTag;
        public List<string> summaryTags = new List<string>();
    }

    public enum AiImagePurpose
    {
        Card,
        Panorama
    }

    public class AiImageResult
    {
        public byte[] bytes;
        public string error;

        [JsonIgnore] public bool Succeeded => bytes != null && bytes.Length > 0;

        public static AiImageResult Success(byte[] imageBytes)
        {
            return new AiImageResult { bytes = imageBytes };
        }

        public static AiImageResult Failed(string reason)
        {
            return new AiImageResult { error = string.IsNullOrWhiteSpace(reason) ? "Image generation returned no image bytes." : reason };
        }
    }

    [Serializable]
    public class NodeCacheEntry
    {
        public string storyId;
        public string cacheKey;
        public string sourceNodeId;
        public string choiceId;
        public StoryNode resultNode;
        public PlayerStats statDelta = new PlayerStats(0, 0, 0, 0);
        public string textStatus;
        public string imagePrompt;
        public string imageCacheKey;
        public string locationTag;
        public string moodTag;
        public string majorEventTag;
        public string imagePath;
        public string imageUrl;
        public string imageStatus;
        public string imageError;
        public string panoramaPrompt;
        public string panoramaCacheKey;
        public string panoramaPath;
        public string panoramaUrl;
        public string panoramaStatus;
        public string panoramaError;
        public string createdAt;
        public string status;
    }

    public static class NodeCacheStatuses
    {
        public const string PendingReview = "PendingReview";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";

        public static readonly string[] All = { PendingReview, Approved, Rejected };

        public static string Normalize(string status)
        {
            return string.IsNullOrWhiteSpace(status) ? PendingReview : status;
        }

        public static bool IsRejected(string status)
        {
            return string.Equals(Normalize(status), Rejected, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class NodeImageStatuses
    {
        public const string Queued = "Queued";
        public const string Generating = "Generating";
        public const string Generated = "Generated";
        public const string Reused = "Reused";
        public const string Failed = "Failed";
        public const string SkippedByPolicy = "SkippedByPolicy";
        public const string SkippedUnavailable = "SkippedUnavailable";
    }

    public static class NodeTextStatuses
    {
        public const string Draft = "Draft";
        public const string Generating = "Generating";
        public const string Generated = "Generated";
    }

    [Serializable]
    public class NodeCacheDatabase
    {
        public List<NodeCacheEntry> entries = new List<NodeCacheEntry>();
    }

    public static class AiProviderTypes
    {
        public const string OpenAiCompatible = "openai_compatible";
        public const string Mock = "mock";

        public static readonly string[] All = { OpenAiCompatible, Mock };

        public static string Normalize(string providerType)
        {
            if (string.IsNullOrWhiteSpace(providerType))
            {
                return OpenAiCompatible;
            }

            var normalized = providerType.Trim().ToLowerInvariant().Replace("-", "_");
            if (normalized == OpenAiCompatible || normalized == "openai")
            {
                return OpenAiCompatible;
            }

            return Mock;
        }

        public static bool IsKnown(string providerType)
        {
            var normalized = (providerType ?? "").Trim().ToLowerInvariant().Replace("-", "_");
            return normalized == OpenAiCompatible || normalized == "openai" || normalized == Mock;
        }
    }

    public static class AiModelNameHints
    {
        public static bool IsLikelyImageOnlyModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            var normalized = model.Trim().ToLowerInvariant();
            return normalized.StartsWith("gpt-image", StringComparison.Ordinal)
                   || normalized.StartsWith("dall-e", StringComparison.Ordinal)
                   || normalized.StartsWith("imagen", StringComparison.Ordinal);
        }
    }

    [Serializable]
    public class AiProviderSettings
    {
        public string providerType = AiProviderTypes.OpenAiCompatible;
        public string baseUrl = "https://api.asxs.top/v1";
        public string wireApi = "responses";
        public string textModel = "";
        public string imageModel = "gpt-image-2";
        public string imageBaseUrl = "";
        public string imageEndpoint = "images/generations";
        public string imageEndpointUrl = "";
        public string apiKeyEnvName = "OPENAI_API_KEY";
        public string imageApiKeyEnvName = "";
        public string imageSize = "1:1";
        public string imageResolution = "1k";
        public string imageQuality = "low";
        public string imageOutputFormat = "png";
        public int imageCount = 1;
        public int imagePollIntervalSeconds = 3;
        public bool disableResponseStorage = true;
        public string reasoningEffort = "";
        public int timeoutSeconds = 25;
        public int imageTimeoutSeconds = 60;
        public bool enableRuntimeImages = true;
        public float imageGenerationRatio = 0.30f;
        public bool guaranteeFirstGeneratedImage = true;
        public bool enableRuntimePanoramas = true;
        public float panoramaGenerationRatio = 0.30f;
        public bool guaranteeFirstGeneratedPanorama = true;
        public string panoramaImageSize = "16:9";
        public bool enableRemoteNodeCache = false;
        public string remoteCacheBaseUrl = "";
        public string remoteCacheApiKeyEnvName = "";
        public int remoteCacheTimeoutSeconds = 8;

        [JsonIgnore] public string ApiKey { get; private set; }
        [JsonIgnore] public string ImageApiKey { get; private set; }
        [JsonIgnore] public string RemoteCacheApiKey { get; private set; }

        [JsonIgnore] public string NormalizedProviderType => AiProviderTypes.Normalize(providerType);
        [JsonIgnore] public bool IsMockProvider => NormalizedProviderType == AiProviderTypes.Mock;
        [JsonIgnore] public bool ApiKeyPresent => !string.IsNullOrWhiteSpace(ApiKey);
        [JsonIgnore] public bool ImageApiKeyPresent => !string.IsNullOrWhiteSpace(ImageApiKey);
        [JsonIgnore] public bool RemoteCacheApiKeyPresent => !string.IsNullOrWhiteSpace(RemoteCacheApiKey);
        [JsonIgnore] public bool TextModelLooksImageOnly => AiModelNameHints.IsLikelyImageOnlyModel(textModel);
        [JsonIgnore] public bool CanUseText => !IsMockProvider && ApiKeyPresent && !string.IsNullOrWhiteSpace(textModel) && !TextModelLooksImageOnly;
        [JsonIgnore] public bool CanUseImage => !IsMockProvider && ImageApiKeyPresent && !string.IsNullOrWhiteSpace(imageModel);
        [JsonIgnore] public bool CanUseRemoteNodeCache => enableRemoteNodeCache && !string.IsNullOrWhiteSpace(remoteCacheBaseUrl);

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

            var envProviderType = GetConfigValue(dotenv, "AI_BUILDER_PROVIDER_TYPE", "AI_BUILDER_MODEL_PROVIDER");
            if (!string.IsNullOrWhiteSpace(envProviderType))
            {
                settings.providerType = envProviderType;
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

            var envImageBaseUrl = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_BASE_URL", "IMAGE_BASE_URL");
            if (!string.IsNullOrWhiteSpace(envImageBaseUrl))
            {
                settings.imageBaseUrl = envImageBaseUrl;
            }

            var envImageEndpoint = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_ENDPOINT", "IMAGE_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(envImageEndpoint))
            {
                settings.imageEndpoint = envImageEndpoint;
            }

            var envImageEndpointUrl = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_ENDPOINT_URL", "IMAGE_ENDPOINT_URL");
            if (!string.IsNullOrWhiteSpace(envImageEndpointUrl))
            {
                settings.imageEndpointUrl = envImageEndpointUrl;
            }

            var envImageApiKeyEnvName = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_API_KEY_ENV_NAME", "IMAGE_API_KEY_ENV_NAME");
            if (!string.IsNullOrWhiteSpace(envImageApiKeyEnvName))
            {
                settings.imageApiKeyEnvName = envImageApiKeyEnvName;
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

            var envTimeoutSeconds = GetConfigValue(dotenv, "AI_BUILDER_TIMEOUT_SECONDS", "TIMEOUT_SECONDS");
            if (int.TryParse(envTimeoutSeconds, out var parsedTimeoutSeconds))
            {
                settings.timeoutSeconds = parsedTimeoutSeconds;
            }

            var envImageTimeoutSeconds = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_TIMEOUT_SECONDS", "IMAGE_TIMEOUT_SECONDS");
            if (int.TryParse(envImageTimeoutSeconds, out var parsedImageTimeoutSeconds))
            {
                settings.imageTimeoutSeconds = parsedImageTimeoutSeconds;
            }

            var envImageSize = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_SIZE", "IMAGE_SIZE");
            if (!string.IsNullOrWhiteSpace(envImageSize))
            {
                settings.imageSize = envImageSize;
            }

            var envImageResolution = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_RESOLUTION", "IMAGE_RESOLUTION");
            if (!string.IsNullOrWhiteSpace(envImageResolution))
            {
                settings.imageResolution = envImageResolution;
            }

            var envImageQuality = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_QUALITY", "IMAGE_QUALITY");
            if (!string.IsNullOrWhiteSpace(envImageQuality))
            {
                settings.imageQuality = envImageQuality;
            }

            var envImageOutputFormat = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_OUTPUT_FORMAT", "IMAGE_OUTPUT_FORMAT");
            if (!string.IsNullOrWhiteSpace(envImageOutputFormat))
            {
                settings.imageOutputFormat = envImageOutputFormat;
            }

            var envImageCount = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_COUNT", "IMAGE_COUNT");
            if (int.TryParse(envImageCount, out var parsedImageCount))
            {
                settings.imageCount = parsedImageCount;
            }

            var envImagePollInterval = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_POLL_INTERVAL_SECONDS", "IMAGE_POLL_INTERVAL_SECONDS");
            if (int.TryParse(envImagePollInterval, out var parsedImagePollInterval))
            {
                settings.imagePollIntervalSeconds = parsedImagePollInterval;
            }

            var disableStorage = GetConfigValue(dotenv, "AI_BUILDER_DISABLE_RESPONSE_STORAGE", "DISABLE_RESPONSE_STORAGE");
            if (bool.TryParse(disableStorage, out var parsedDisableStorage))
            {
                settings.disableResponseStorage = parsedDisableStorage;
            }

            var enableImages = GetConfigValue(dotenv, "AI_BUILDER_ENABLE_RUNTIME_IMAGES", "AI_BUILDER_RUNTIME_IMAGES");
            if (bool.TryParse(enableImages, out var parsedEnableImages))
            {
                settings.enableRuntimeImages = parsedEnableImages;
            }

            var imageRatio = GetConfigValue(dotenv, "AI_BUILDER_IMAGE_GENERATION_RATIO", "AI_BUILDER_IMAGE_RATIO");
            if (TryParseFloat(imageRatio, out var parsedImageRatio))
            {
                settings.imageGenerationRatio = parsedImageRatio;
            }

            var guaranteeFirstImage = GetConfigValue(dotenv, "AI_BUILDER_GUARANTEE_FIRST_GENERATED_IMAGE", "AI_BUILDER_FIRST_IMAGE_GUARANTEED");
            if (bool.TryParse(guaranteeFirstImage, out var parsedGuaranteeFirstImage))
            {
                settings.guaranteeFirstGeneratedImage = parsedGuaranteeFirstImage;
            }

            var enablePanoramas = GetConfigValue(dotenv, "AI_BUILDER_ENABLE_RUNTIME_PANORAMAS", "AI_BUILDER_RUNTIME_PANORAMAS");
            if (bool.TryParse(enablePanoramas, out var parsedEnablePanoramas))
            {
                settings.enableRuntimePanoramas = parsedEnablePanoramas;
            }

            var panoramaRatio = GetConfigValue(dotenv, "AI_BUILDER_PANORAMA_GENERATION_RATIO", "AI_BUILDER_PANORAMA_RATIO");
            if (TryParseFloat(panoramaRatio, out var parsedPanoramaRatio))
            {
                settings.panoramaGenerationRatio = parsedPanoramaRatio;
            }

            var guaranteeFirstPanorama = GetConfigValue(dotenv, "AI_BUILDER_GUARANTEE_FIRST_GENERATED_PANORAMA", "AI_BUILDER_FIRST_PANORAMA_GUARANTEED");
            if (bool.TryParse(guaranteeFirstPanorama, out var parsedGuaranteeFirstPanorama))
            {
                settings.guaranteeFirstGeneratedPanorama = parsedGuaranteeFirstPanorama;
            }

            var envPanoramaImageSize = GetConfigValue(dotenv, "AI_BUILDER_PANORAMA_IMAGE_SIZE", "PANORAMA_IMAGE_SIZE");
            if (!string.IsNullOrWhiteSpace(envPanoramaImageSize))
            {
                settings.panoramaImageSize = envPanoramaImageSize;
            }

            var enableRemoteNodeCache = GetConfigValue(dotenv, "AI_BUILDER_ENABLE_REMOTE_NODE_CACHE", "AI_BUILDER_REMOTE_CACHE");
            if (bool.TryParse(enableRemoteNodeCache, out var parsedEnableRemoteNodeCache))
            {
                settings.enableRemoteNodeCache = parsedEnableRemoteNodeCache;
            }

            var remoteCacheBaseUrl = GetConfigValue(dotenv, "AI_BUILDER_REMOTE_CACHE_BASE_URL", "AI_BUILDER_NODE_CACHE_BASE_URL");
            if (!string.IsNullOrWhiteSpace(remoteCacheBaseUrl))
            {
                settings.remoteCacheBaseUrl = remoteCacheBaseUrl;
            }

            var remoteCacheApiKeyEnvName = GetConfigValue(dotenv, "AI_BUILDER_REMOTE_CACHE_API_KEY_ENV_NAME", "AI_BUILDER_NODE_CACHE_API_KEY_ENV_NAME");
            if (!string.IsNullOrWhiteSpace(remoteCacheApiKeyEnvName))
            {
                settings.remoteCacheApiKeyEnvName = remoteCacheApiKeyEnvName;
            }

            var remoteCacheTimeoutSeconds = GetConfigValue(dotenv, "AI_BUILDER_REMOTE_CACHE_TIMEOUT_SECONDS", "AI_BUILDER_NODE_CACHE_TIMEOUT_SECONDS");
            if (int.TryParse(remoteCacheTimeoutSeconds, out var parsedRemoteCacheTimeoutSeconds))
            {
                settings.remoteCacheTimeoutSeconds = parsedRemoteCacheTimeoutSeconds;
            }

            var keyName = string.IsNullOrWhiteSpace(settings.apiKeyEnvName) ? "OPENAI_API_KEY" : settings.apiKeyEnvName;
            settings.ApiKey = GetConfigValue(dotenv, keyName);
            var imageKeyName = string.IsNullOrWhiteSpace(settings.imageApiKeyEnvName) ? keyName : settings.imageApiKeyEnvName;
            settings.ImageApiKey = GetConfigValue(dotenv, imageKeyName);
            var remoteCacheKeyName = settings.remoteCacheApiKeyEnvName;
            settings.RemoteCacheApiKey = string.IsNullOrWhiteSpace(remoteCacheKeyName)
                ? ""
                : GetConfigValue(dotenv, remoteCacheKeyName);
            settings.NormalizeInPlace();
            return settings;
        }

        public void NormalizeInPlace()
        {
            if (!string.IsNullOrWhiteSpace(providerType) && !AiProviderTypes.IsKnown(providerType))
            {
                Debug.LogWarning($"AI Builder unknown provider '{providerType}' falls back to mock.");
            }

            providerType = AiProviderTypes.Normalize(providerType);
            baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.asxs.top/v1" : baseUrl.Trim();
            wireApi = string.IsNullOrWhiteSpace(wireApi) ? "responses" : wireApi.Trim();
            imageBaseUrl = string.IsNullOrWhiteSpace(imageBaseUrl) ? "" : imageBaseUrl.Trim();
            imageEndpoint = string.IsNullOrWhiteSpace(imageEndpoint) ? "images/generations" : imageEndpoint.Trim().Trim('/');
            imageEndpointUrl = string.IsNullOrWhiteSpace(imageEndpointUrl) ? "" : imageEndpointUrl.Trim();
            apiKeyEnvName = string.IsNullOrWhiteSpace(apiKeyEnvName) ? "OPENAI_API_KEY" : apiKeyEnvName.Trim();
            imageApiKeyEnvName = string.IsNullOrWhiteSpace(imageApiKeyEnvName) ? "" : imageApiKeyEnvName.Trim();
            imageSize = string.IsNullOrWhiteSpace(imageSize) ? "1:1" : imageSize.Trim();
            imageResolution = string.IsNullOrWhiteSpace(imageResolution) ? "1k" : imageResolution.Trim().ToLowerInvariant();
            imageQuality = string.IsNullOrWhiteSpace(imageQuality) ? "low" : imageQuality.Trim().ToLowerInvariant();
            imageOutputFormat = string.IsNullOrWhiteSpace(imageOutputFormat) ? "png" : imageOutputFormat.Trim().ToLowerInvariant();
            panoramaImageSize = NormalizePanoramaImageSize(panoramaImageSize);
            imageCount = Mathf.Clamp(imageCount, 1, 4);
            imagePollIntervalSeconds = Mathf.Clamp(imagePollIntervalSeconds, 2, 10);
            reasoningEffort = NormalizeReasoningEffort(reasoningEffort);
            imageGenerationRatio = Mathf.Clamp01(float.IsNaN(imageGenerationRatio) ? 0f : imageGenerationRatio);
            panoramaGenerationRatio = Mathf.Clamp01(float.IsNaN(panoramaGenerationRatio) ? 0f : panoramaGenerationRatio);
            timeoutSeconds = Mathf.Clamp(timeoutSeconds, 5, 120);
            imageTimeoutSeconds = Mathf.Clamp(imageTimeoutSeconds, 8, 240);
            remoteCacheBaseUrl = string.IsNullOrWhiteSpace(remoteCacheBaseUrl) ? "" : remoteCacheBaseUrl.Trim().TrimEnd('/');
            remoteCacheApiKeyEnvName = string.IsNullOrWhiteSpace(remoteCacheApiKeyEnvName) ? "" : remoteCacheApiKeyEnvName.Trim();
            remoteCacheTimeoutSeconds = Mathf.Clamp(remoteCacheTimeoutSeconds, 2, 60);
        }

        private static string NormalizePanoramaImageSize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "16:9";
            }

            var normalized = value.Trim().ToLowerInvariant().Replace(" ", "");
            return normalized == "1:1" || normalized == "1024x1024" || normalized == "square"
                ? "16:9"
                : value.Trim();
        }

        private static string NormalizeReasoningEffort(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized == "none"
                   || normalized == "off"
                   || normalized == "false"
                   || normalized == "0"
                ? ""
                : value.Trim();
        }

        public void RefreshApiKeyStatus()
        {
            var keyName = string.IsNullOrWhiteSpace(apiKeyEnvName) ? "OPENAI_API_KEY" : apiKeyEnvName;
            ApiKey = GetConfigValue(LoadDotEnv(), keyName);
            var imageKeyName = string.IsNullOrWhiteSpace(imageApiKeyEnvName) ? keyName : imageApiKeyEnvName;
            ImageApiKey = GetConfigValue(LoadDotEnv(), imageKeyName);
            RemoteCacheApiKey = string.IsNullOrWhiteSpace(remoteCacheApiKeyEnvName)
                ? ""
                : GetConfigValue(LoadDotEnv(), remoteCacheApiKeyEnvName);
        }

        public void SaveLocalConfig()
        {
            NormalizeInPlace();
            Directory.CreateDirectory(Path.GetDirectoryName(LocalConfigPath));
            File.WriteAllText(LocalConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
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

        private static bool TryParseFloat(string value, out float result)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                   || float.TryParse(value, out result);
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

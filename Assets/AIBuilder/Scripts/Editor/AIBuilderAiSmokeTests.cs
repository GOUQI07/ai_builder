#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public static class AIBuilderAiSmokeTests
    {
        [MenuItem("AI Builder/AI Smoke Test/Text")]
        public static async void RunTextSmokeTest()
        {
            var settings = AiProviderSettings.Load();
            if (!settings.CanUseText)
            {
                Debug.LogWarning(BuildTextConfigWarning(settings));
                return;
            }

            var graph = StoryRepository.LoadGraph();
            var node = graph.nodes.OrderBy(item => item.mainlineIndex).FirstOrDefault(item => item.nodeKind == StoryNodeKind.Mainline);
            var choice = node?.rightChoice ?? node?.leftChoice;
            if (node == null || choice == null)
            {
                Debug.LogError("AI Builder text smoke failed: no playable node or choice found.");
                return;
            }

            var timeoutSeconds = Mathf.Max(5, settings.timeoutSeconds);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var service = AiProviderFactory.CreateTextService(settings);
            try
            {
                Debug.Log($"AI Builder text smoke started: provider={settings.NormalizedProviderType}, model={settings.textModel}, timeout={timeoutSeconds}s.");
                var result = await WaitForSmokeResult(
                    service.GenerateNextNodeAsync(node, choice, new PlayerStats(), timeout.Token),
                    timeout,
                    timeoutSeconds,
                    "text smoke");
                if (result == null
                    || string.IsNullOrWhiteSpace(result.storyText)
                    || string.IsNullOrWhiteSpace(result.leftChoice)
                    || string.IsNullOrWhiteSpace(result.rightChoice))
                {
                    Debug.LogError("AI Builder text smoke failed: response did not contain a valid branch JSON result.");
                    return;
                }

                if (HasMockTag(result))
                {
                    Debug.LogWarning("AI Builder text smoke reached the mock fallback. Check provider config, network, and prior API warnings.");
                    return;
                }

                Debug.Log($"AI Builder text smoke passed: {Preview(result.storyText)}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder text smoke failed safely: {ex.Message}");
            }
        }

        [MenuItem("AI Builder/AI Smoke Test/Image")]
        public static async void RunImageSmokeTest()
        {
            var settings = AiProviderSettings.Load();
            if (!settings.CanUseImage)
            {
                Debug.LogWarning(BuildImageConfigWarning(settings));
                return;
            }

            var timeoutSeconds = Mathf.Max(8, settings.imageTimeoutSeconds);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var service = AiProviderFactory.CreateImageService(settings);
            try
            {
                Debug.Log($"AI Builder image smoke started: provider={settings.NormalizedProviderType}, model={settings.imageModel}, timeout={timeoutSeconds}s.");
                var result = await WaitForSmokeResult(
                    service.GenerateImageAsync(
                        "symbolic golden crown on empty stone steps, ruined castle silhouettes, ominous shadows, muted gold and charcoal palette, flat medieval decision-card composition",
                        timeout.Token),
                    timeout,
                    timeoutSeconds,
                    "image smoke");
                if (result == null || !result.Succeeded)
                {
                    Debug.LogWarning($"AI Builder image smoke failed safely: {result?.error ?? "no image bytes returned"}");
                    return;
                }

                Directory.CreateDirectory(NodeCacheService.CacheDirectory);
                var path = Path.Combine(NodeCacheService.CacheDirectory, $"ai_builder_image_smoke_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                File.WriteAllBytes(path, result.bytes);
                Debug.Log($"AI Builder image smoke passed: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder image smoke failed safely: {ex.Message}");
            }
        }

        private static string BuildTextConfigWarning(AiProviderSettings settings)
        {
            var modelStatus = string.IsNullOrWhiteSpace(settings.textModel)
                ? "missing"
                : settings.TextModelLooksImageOnly
                    ? $"{settings.textModel} (image-only)"
                    : settings.textModel;
            return $"AI Builder text smoke skipped: provider={settings.NormalizedProviderType}, apiKey={(settings.ApiKeyPresent ? "found" : "missing")}, textModel={modelStatus}.";
        }

        private static string BuildImageConfigWarning(AiProviderSettings settings)
        {
            return $"AI Builder image smoke skipped: provider={settings.NormalizedProviderType}, imageApiKey={(settings.ImageApiKeyPresent ? "found" : "missing")}, imageModel={(string.IsNullOrWhiteSpace(settings.imageModel) ? "missing" : settings.imageModel)}.";
        }

        private static bool HasMockTag(AiTextResult result)
        {
            return result.summaryTags != null && result.summaryTags.Any(tag => string.Equals(tag, "mock", StringComparison.OrdinalIgnoreCase));
        }

        private static string Preview(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            return value.Length <= 80 ? value : value.Substring(0, 80) + "...";
        }

        private static async Task<T> WaitForSmokeResult<T>(Task<T> task, CancellationTokenSource timeout, int timeoutSeconds, string label)
        {
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (completed != task)
            {
                timeout.Cancel();
                throw new TimeoutException($"AI Builder {label} timed out after {timeoutSeconds} seconds.");
            }

            return await task;
        }
    }
}
#endif

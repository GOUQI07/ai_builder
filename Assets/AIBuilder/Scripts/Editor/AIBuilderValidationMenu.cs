#if UNITY_EDITOR
using System.IO;
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

            var keyA = NodeCacheService.CreateCacheKey(graph.nodes[0], graph.nodes[0].rightChoice, new PlayerStats());
            var keyB = NodeCacheService.CreateCacheKey(graph.nodes[0], graph.nodes[0].rightChoice, new PlayerStats());
            Require(keyA == keyB, "Cache key is stable for identical state.");

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

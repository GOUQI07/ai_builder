using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBuilder
{
    [Serializable]
    public class CharacterPortraitPreset
    {
        public string id;
        public string displayName;
        public List<string> aliases = new List<string>();
        [TextArea(2, 5)] public string description;
        [TextArea(2, 6)] public string imagePrompt;
        public string imageRef;
        public string spriteKey = "branch";
        public int priority = 10;
    }

    [Serializable]
    public class CharacterPortraitPresetDatabase
    {
        public List<CharacterPortraitPreset> presets = new List<CharacterPortraitPreset>();
    }

    public sealed class CharacterPortraitService
    {
        private const string ResourceName = "character_portrait_presets";
        private const string PortraitStyleVersion = "style_v2";
        private readonly CharacterPortraitPresetDatabase database;

        public CharacterPortraitService()
        {
            database = LoadDatabase();
        }

        public static string PortraitDirectory =>
            Path.Combine(Application.persistentDataPath, "AIBuilder", "PortraitPresets");

        public static string PresetImagePath(string id)
        {
            return Path.Combine(PortraitDirectory, PortraitStyleVersion, $"{SanitizeId(id)}.png");
        }

        public static bool IsCurrentPortraitImagePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                   && path.Replace('\\', '/').IndexOf($"/{PortraitStyleVersion}/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public CharacterPortraitPreset Match(StoryNode node)
        {
            if (node == null || database?.presets == null || database.presets.Count == 0)
            {
                return null;
            }

            var text = BuildSearchText(node);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return database.presets
                .Select(preset => new { preset, score = ScorePreset(preset, text) })
                .Where(item => item.score > 0)
                .OrderByDescending(item => item.score)
                .ThenByDescending(item => item.preset.priority)
                .Select(item => item.preset)
                .FirstOrDefault();
        }

        public string ResolveImageRef(StoryNode node)
        {
            var preset = Match(node);
            if (preset == null)
            {
                return "";
            }

            var localPath = PresetImagePath(preset.id);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            if (IsCurrentPortraitImagePath(preset.imageRef) && File.Exists(preset.imageRef))
            {
                return preset.imageRef;
            }

            return string.IsNullOrWhiteSpace(preset.spriteKey) ? "branch" : preset.spriteKey;
        }

        private static CharacterPortraitPresetDatabase LoadDatabase()
        {
            try
            {
                var asset = Resources.Load<TextAsset>(ResourceName);
                if (asset == null || string.IsNullOrWhiteSpace(asset.text))
                {
                    return new CharacterPortraitPresetDatabase();
                }

                var loaded = JsonConvert.DeserializeObject<CharacterPortraitPresetDatabase>(asset.text);
                loaded ??= new CharacterPortraitPresetDatabase();
                loaded.presets ??= new List<CharacterPortraitPreset>();
                foreach (var preset in loaded.presets)
                {
                    NormalizePreset(preset);
                }

                return loaded;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder portrait presets ignored: {ex.Message}");
                return new CharacterPortraitPresetDatabase();
            }
        }

        private static void NormalizePreset(CharacterPortraitPreset preset)
        {
            if (preset == null)
            {
                return;
            }

            preset.id = SanitizeId(string.IsNullOrWhiteSpace(preset.id) ? preset.displayName : preset.id);
            preset.displayName = string.IsNullOrWhiteSpace(preset.displayName) ? preset.id : preset.displayName.Trim();
            preset.aliases ??= new List<string>();
            if (!preset.aliases.Any(alias => string.Equals(alias, preset.displayName, StringComparison.OrdinalIgnoreCase)))
            {
                preset.aliases.Insert(0, preset.displayName);
            }

            preset.aliases = preset.aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            preset.spriteKey = string.IsNullOrWhiteSpace(preset.spriteKey) ? "branch" : preset.spriteKey.Trim();
            preset.priority = Mathf.Clamp(preset.priority, 0, 100);
        }

        private static int ScorePreset(CharacterPortraitPreset preset, string text)
        {
            if (preset?.aliases == null)
            {
                return 0;
            }

            var score = 0;
            foreach (var alias in preset.aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                var index = text.IndexOf(alias, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    score += Mathf.Max(1, alias.Length) * 10 + preset.priority;
                }
            }

            return score;
        }

        private static string BuildSearchText(StoryNode node)
        {
            return string.Join("\n", new[]
            {
                node.title,
                node.body,
                node.leftChoice?.label,
                node.leftChoice?.intent,
                node.rightChoice?.label,
                node.rightChoice?.intent
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string SanitizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "portrait";
            }

            var chars = value.Trim().ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray();
            var id = new string(chars);
            while (id.Contains("__", StringComparison.Ordinal))
            {
                id = id.Replace("__", "_");
            }

            return string.IsNullOrWhiteSpace(id.Trim('_')) ? "portrait" : id.Trim('_');
        }
    }
}

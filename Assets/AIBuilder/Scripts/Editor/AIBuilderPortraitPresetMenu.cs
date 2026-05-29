#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public class PortraitPresetStats
    {
        public int detectedCharacterCount;
        public int presetCount;
        public int existingImageCount;
        public int missingImageCount;

        public string Summary()
        {
            return $"detected={detectedCharacterCount}, presets={presetCount}, images={existingImageCount}, missing={missingImageCount}";
        }
    }

    public sealed class PortraitPresetBuildReport : PortraitPresetStats
    {
        public int newPresetCount;
        public int reusedPresetCount;
        public CharacterPortraitPresetDatabase database;
    }

    public sealed class PortraitPresetGenerationReport : PortraitPresetStats
    {
        public int generatedCount;
        public int failedCount;
        public int skippedCount;
    }

    public static class AIBuilderPortraitPresetMenu
    {
        public const string PresetsAssetPath = "Assets/AIBuilder/Resources/character_portrait_presets.json";
        private const int DefaultMaxGenerationConcurrency = 2;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        [MenuItem("AI Builder/Portrait Presets/Rebuild From Story Project")]
        public static void RebuildFromStoryProjectMenu()
        {
            var report = RebuildFromStoryProject(LoadStoryProjectOrDefault(), true, true, true);
            Debug.Log($"AI Builder portrait presets rebuilt from story import: {report.Summary()}, new={report.newPresetCount}, reused={report.reusedPresetCount}.");
        }

        [MenuItem("AI Builder/Portrait Presets/Generate Missing Portrait Images")]
        public static async void GenerateMissingPortraitImagesMenu()
        {
            await GenerateMissingPortraitImagesAsync(LoadStoryProjectOrDefault(), DefaultMaxGenerationConcurrency);
        }

        [MenuItem("AI Builder/Portrait Presets/Validate")]
        public static void Validate()
        {
            var stats = GetStats(LoadStoryProjectOrDefault());
            Debug.Log($"AI Builder portrait presets: {stats.Summary()}.");
        }

        public static PortraitPresetStats GetStats(StoryProject storyProject = null)
        {
            var existing = LoadPresetsFileOnly();
            ApplyLocalImageRefs(existing);
            var detected = BuildPresetDatabaseFromStoryProject(storyProject ?? LoadStoryProjectOrDefault(), existing);
            return BuildStats(detected.presets.Count, existing);
        }

        public static PortraitPresetBuildReport RebuildFromStoryProject(StoryProject storyProject, bool preserveExisting = true, bool save = true, bool refreshAssetDatabase = true)
        {
            storyProject ??= LoadStoryProjectOrDefault();
            StoryAuthoringUtility.NormalizeProject(storyProject);

            var existing = preserveExisting ? LoadPresetsFileOnly() : new CharacterPortraitPresetDatabase();
            var detected = BuildPresetDatabaseFromStoryProject(storyProject, existing);
            var database = detected.presets.Count > 0 || existing.presets.Count == 0
                ? detected
                : existing;

            ApplyLocalImageRefs(database);
            var existingIds = new HashSet<string>(
                existing.presets.Select(preset => NormalizeKey(preset.id)).Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            var stats = BuildStats(detected.presets.Count, database);
            var report = new PortraitPresetBuildReport
            {
                detectedCharacterCount = stats.detectedCharacterCount,
                presetCount = stats.presetCount,
                existingImageCount = stats.existingImageCount,
                missingImageCount = stats.missingImageCount,
                database = database,
                newPresetCount = database.presets.Count(preset => !existingIds.Contains(NormalizeKey(preset.id))),
                reusedPresetCount = database.presets.Count(preset => existingIds.Contains(NormalizeKey(preset.id)))
            };

            if (save)
            {
                SavePresets(database, refreshAssetDatabase);
            }

            return report;
        }

        public static CharacterPortraitPresetDatabase LoadOrBuildPresets(StoryProject storyProject = null)
        {
            var loaded = LoadPresetsFileOnly();
            if (loaded.presets.Count > 0)
            {
                ApplyLocalImageRefs(loaded);
                return loaded;
            }

            var report = RebuildFromStoryProject(storyProject ?? LoadStoryProjectOrDefault(), true, true, true);
            return report.database ?? new CharacterPortraitPresetDatabase();
        }

        public static CharacterPortraitPresetDatabase BuildPresetDatabaseFromStoryProject(StoryProject storyProject, CharacterPortraitPresetDatabase existingPresets = null)
        {
            var database = new CharacterPortraitPresetDatabase();
            if (storyProject == null)
            {
                return database;
            }

            StoryAuthoringUtility.NormalizeProject(storyProject);
            existingPresets ??= new CharacterPortraitPresetDatabase();
            NormalizeDatabase(existingPresets);

            var mentions = ExtractCharacterMentions(storyProject, existingPresets);
            var presetsById = new Dictionary<string, CharacterPortraitPreset>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < mentions.Count; i++)
            {
                var draft = CreatePresetFromMention(mentions[i], i);
                if (draft == null)
                {
                    continue;
                }

                var existing = FindMatchingExistingPreset(draft, existingPresets.presets);
                var id = existing != null ? existing.id : draft.id;
                if (!presetsById.TryGetValue(id, out var merged))
                {
                    merged = existing != null ? ClonePreset(existing) : draft;
                    MergePresetStoryData(merged, draft);
                    presetsById[id] = merged;
                }
                else
                {
                    MergePresetStoryData(merged, draft);
                }
            }

            database.presets = presetsById.Values
                .Select(preset =>
                {
                    NormalizePreset(preset);
                    return preset;
                })
                .OrderByDescending(preset => preset.priority)
                .ThenBy(preset => preset.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyLocalImageRefs(database);
            return database;
        }

        public static async Task<PortraitPresetGenerationReport> GenerateMissingPortraitImagesAsync(StoryProject storyProject = null, int maxConcurrency = DefaultMaxGenerationConcurrency)
        {
            var buildReport = RebuildFromStoryProject(storyProject ?? LoadStoryProjectOrDefault(), true, true, false);
            var database = buildReport.database ?? LoadOrBuildPresets(storyProject);
            var report = new PortraitPresetGenerationReport
            {
                detectedCharacterCount = buildReport.detectedCharacterCount,
                presetCount = database.presets.Count
            };

            var settings = AiProviderSettings.Load();
            if (!settings.CanUseImage)
            {
                report.skippedCount = database.presets.Count;
                Debug.LogWarning($"AI Builder portrait generation skipped: imageApiKey={(settings.ImageApiKeyPresent ? "found" : "missing")}, imageModel={(string.IsNullOrWhiteSpace(settings.imageModel) ? "missing" : settings.imageModel)}.");
                return report;
            }

            Directory.CreateDirectory(CharacterPortraitService.PortraitDirectory);
            ApplyLocalImageRefs(database);
            var jobs = database.presets
                .Where(IsMissingImage)
                .Select(preset => new PortraitGenerationJob(preset, CharacterPortraitService.PresetImagePath(preset.id)))
                .ToList();

            report.existingImageCount = database.presets.Count - jobs.Count;
            report.missingImageCount = jobs.Count;
            if (jobs.Count == 0)
            {
                SavePresets(database, true);
                Debug.Log($"AI Builder portrait generation skipped: all {database.presets.Count} preset image(s) already exist.");
                return report;
            }

            var service = AiProviderFactory.CreateImageService(settings);
            maxConcurrency = Mathf.Clamp(maxConcurrency, 1, 4);
            var timeoutSeconds = Mathf.Max(
                settings.imageTimeoutSeconds + 30,
                settings.imageTimeoutSeconds * Mathf.CeilToInt(jobs.Count / (float)maxConcurrency) + 30);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var sync = new object();

            Debug.Log($"AI Builder portrait generation started: {jobs.Count} missing image(s), concurrency={maxConcurrency}.");
            var tasks = jobs.Select(async job =>
            {
                var acquired = false;
                try
                {
                    await semaphore.WaitAsync(timeout.Token);
                    acquired = true;

                    if (File.Exists(job.targetPath))
                    {
                        lock (sync)
                        {
                            job.preset.imageRef = job.targetPath;
                            report.skippedCount++;
                        }

                        return;
                    }

                    Debug.Log($"AI Builder portrait generation requested: {job.preset.displayName}");
                    var result = await service.GenerateImageAsync(BuildPortraitPrompt(job.preset), timeout.Token);
                    if (result == null || !result.Succeeded)
                    {
                        lock (sync)
                        {
                            report.failedCount++;
                        }

                        Debug.LogWarning($"AI Builder portrait generation failed safely for {job.preset.displayName}: {result?.error ?? "no image bytes"}");
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(job.targetPath));
                    File.WriteAllBytes(job.targetPath, result.bytes);
                    lock (sync)
                    {
                        job.preset.imageRef = job.targetPath;
                        report.generatedCount++;
                        SavePresets(database, false);
                    }

                    Debug.Log($"AI Builder portrait generated: {job.preset.displayName} -> {job.targetPath}");
                }
                catch (OperationCanceledException)
                {
                    lock (sync)
                    {
                        report.failedCount++;
                    }

                    Debug.LogWarning($"AI Builder portrait generation cancelled or timed out for {job.preset.displayName}.");
                }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        report.failedCount++;
                    }

                    Debug.LogWarning($"AI Builder portrait generation failed safely for {job.preset.displayName}: {ex.Message}");
                }
                finally
                {
                    if (acquired)
                    {
                        semaphore.Release();
                    }
                }
            });

            await Task.WhenAll(tasks);
            ApplyLocalImageRefs(database);
            var finalStats = BuildStats(buildReport.detectedCharacterCount, database);
            report.existingImageCount = finalStats.existingImageCount;
            report.missingImageCount = finalStats.missingImageCount;
            SavePresets(database, true);
            Debug.Log($"AI Builder portrait generation finished: generated={report.generatedCount}, failed={report.failedCount}, skipped={report.skippedCount}, {finalStats.Summary()}.");
            return report;
        }

        public static StoryProject LoadStoryProjectOrDefault()
        {
            try
            {
                if (File.Exists(StoryAuthoringPaths.StoryProjectAssetPath))
                {
                    var loaded = JsonConvert.DeserializeObject<StoryProject>(File.ReadAllText(StoryAuthoringPaths.StoryProjectAssetPath, Utf8NoBom));
                    if (loaded != null)
                    {
                        StoryAuthoringUtility.NormalizeProject(loaded);
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder story project ignored while building portraits: {ex.Message}");
            }

            return StoryAuthoringUtility.CreateDefaultProject();
        }

        public static void SavePresets(CharacterPortraitPresetDatabase database, bool refreshAssetDatabase = true)
        {
            NormalizeDatabase(database);
            Directory.CreateDirectory(Path.GetDirectoryName(PresetsAssetPath));
            File.WriteAllText(PresetsAssetPath, JsonConvert.SerializeObject(database, Formatting.Indented), Utf8NoBom);
            if (refreshAssetDatabase)
            {
                AssetDatabase.Refresh();
            }
        }

        private static CharacterPortraitPresetDatabase LoadPresetsFileOnly()
        {
            if (!File.Exists(PresetsAssetPath))
            {
                return new CharacterPortraitPresetDatabase();
            }

            try
            {
                var loaded = JsonConvert.DeserializeObject<CharacterPortraitPresetDatabase>(File.ReadAllText(PresetsAssetPath, Utf8NoBom));
                loaded ??= new CharacterPortraitPresetDatabase();
                NormalizeDatabase(loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder portrait preset file ignored: {ex.Message}");
                return new CharacterPortraitPresetDatabase();
            }
        }

        private static List<CharacterMention> ExtractCharacterMentions(StoryProject storyProject, CharacterPortraitPresetDatabase existingPresets)
        {
            var mentions = new List<CharacterMention>();
            foreach (var summary in storyProject.summaries.OrderBy(item => item.chunkIndex))
            {
                if (summary?.characters == null)
                {
                    continue;
                }

                foreach (var character in summary.characters)
                {
                    var mention = ParseCharacterMention(character, summary.chunkId, 100 - mentions.Count);
                    if (mention != null)
                    {
                        mentions.Add(mention);
                    }
                }
            }

            var projectText = BuildProjectSearchText(storyProject);
            if (existingPresets?.presets != null && !string.IsNullOrWhiteSpace(projectText))
            {
                foreach (var preset in existingPresets.presets)
                {
                    NormalizePreset(preset);
                    if (preset.aliases.Any(alias => AliasAppearsInText(alias, projectText)))
                    {
                        mentions.Add(new CharacterMention
                        {
                            displayName = preset.displayName,
                            aliases = new List<string>(preset.aliases),
                            description = preset.description,
                            imagePrompt = preset.imagePrompt,
                            source = "existing-preset-match",
                            priority = Mathf.Max(10, preset.priority)
                        });
                    }
                }
            }

            return mentions;
        }

        private static CharacterMention ParseCharacterMention(string rawValue, string source, int priority)
        {
            var value = CleanEntry(rawValue);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var separator = FirstIndexOfAny(value, ':', '\uFF1A', '-', '\u2014');
            var namePart = separator > 0 ? value.Substring(0, separator).Trim() : value.Trim();
            var description = separator > 0 ? value.Substring(separator + 1).Trim() : value.Trim();
            if (namePart.Length > 48 && separator < 0)
            {
                return null;
            }

            var aliases = SplitAliases(namePart).ToList();
            var displayName = aliases.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            return new CharacterMention
            {
                displayName = displayName,
                aliases = aliases,
                description = string.IsNullOrWhiteSpace(description) ? displayName : description,
                source = source,
                priority = priority
            };
        }

        private static CharacterPortraitPreset CreatePresetFromMention(CharacterMention mention, int rank)
        {
            if (mention == null || string.IsNullOrWhiteSpace(mention.displayName))
            {
                return null;
            }

            var aliases = mention.aliases == null || mention.aliases.Count == 0
                ? new List<string> { mention.displayName }
                : mention.aliases.ToList();
            if (!aliases.Any(alias => string.Equals(alias, mention.displayName, StringComparison.OrdinalIgnoreCase)))
            {
                aliases.Insert(0, mention.displayName);
            }

            var preset = new CharacterPortraitPreset
            {
                id = BuildStablePresetId(mention.displayName, aliases),
                displayName = mention.displayName.Trim(),
                aliases = aliases.SelectMany(AliasVariants)
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => alias.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                description = CompactWhitespace(mention.description),
                imagePrompt = string.IsNullOrWhiteSpace(mention.imagePrompt)
                    ? BuildDefaultImagePrompt(mention.displayName, mention.description)
                    : mention.imagePrompt,
                imageRef = "",
                spriteKey = GuessSpriteKey(mention),
                priority = Mathf.Clamp(mention.priority - rank, 1, 100)
            };
            NormalizePreset(preset);
            return preset;
        }

        private static CharacterPortraitPreset FindMatchingExistingPreset(CharacterPortraitPreset candidate, List<CharacterPortraitPreset> existingPresets)
        {
            if (candidate == null || existingPresets == null || existingPresets.Count == 0)
            {
                return null;
            }

            return existingPresets
                .Where(preset => preset != null)
                .Select(preset => new { preset, score = PresetMatchScore(candidate, preset) })
                .Where(item => item.score > 0)
                .OrderByDescending(item => item.score)
                .ThenByDescending(item => item.preset.priority)
                .Select(item => item.preset)
                .FirstOrDefault();
        }

        private static int PresetMatchScore(CharacterPortraitPreset candidate, CharacterPortraitPreset existing)
        {
            NormalizePreset(candidate);
            NormalizePreset(existing);
            if (!string.IsNullOrWhiteSpace(candidate.id)
                && string.Equals(candidate.id, existing.id, StringComparison.OrdinalIgnoreCase))
            {
                return 1000;
            }

            var candidateAliases = AliasSet(candidate).ToList();
            var existingAliases = AliasSet(existing).ToList();
            var best = 0;
            foreach (var candidateAlias in candidateAliases)
            {
                foreach (var existingAlias in existingAliases)
                {
                    if (string.Equals(candidateAlias, existingAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        best = Mathf.Max(best, 900 + Mathf.Min(candidateAlias.Length, existingAlias.Length));
                    }
                    else if (candidateAlias.Length >= 2 && existingAlias.Length >= 2
                             && (candidateAlias.IndexOf(existingAlias, StringComparison.OrdinalIgnoreCase) >= 0
                                 || existingAlias.IndexOf(candidateAlias, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        best = Mathf.Max(best, 500 + Mathf.Min(candidateAlias.Length, existingAlias.Length));
                    }
                }
            }

            return best;
        }

        private static void MergePresetStoryData(CharacterPortraitPreset target, CharacterPortraitPreset storyPreset)
        {
            NormalizePreset(target);
            NormalizePreset(storyPreset);
            target.aliases = target.aliases
                .Concat(storyPreset.aliases)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (string.IsNullOrWhiteSpace(target.description))
            {
                target.description = storyPreset.description;
            }

            if (string.IsNullOrWhiteSpace(target.imagePrompt))
            {
                target.imagePrompt = storyPreset.imagePrompt;
            }

            if (string.IsNullOrWhiteSpace(target.spriteKey))
            {
                target.spriteKey = storyPreset.spriteKey;
            }

            target.priority = Mathf.Clamp(Mathf.Max(target.priority, storyPreset.priority), 0, 100);
        }

        private static void ApplyLocalImageRefs(CharacterPortraitPresetDatabase database)
        {
            if (database?.presets == null)
            {
                return;
            }

            foreach (var preset in database.presets)
            {
                NormalizePreset(preset);
                var localPath = CharacterPortraitService.PresetImagePath(preset.id);
                if (File.Exists(localPath))
                {
                    preset.imageRef = localPath;
                }
                else if (!CharacterPortraitService.IsCurrentPortraitImagePath(preset.imageRef) || !File.Exists(preset.imageRef))
                {
                    preset.imageRef = "";
                }
            }
        }

        private static bool IsMissingImage(CharacterPortraitPreset preset)
        {
            if (preset == null)
            {
                return true;
            }

            NormalizePreset(preset);
            if (CharacterPortraitService.IsCurrentPortraitImagePath(preset.imageRef) && File.Exists(preset.imageRef))
            {
                return false;
            }

            return !File.Exists(CharacterPortraitService.PresetImagePath(preset.id));
        }

        private static PortraitPresetStats BuildStats(int detectedCharacterCount, CharacterPortraitPresetDatabase database)
        {
            database ??= new CharacterPortraitPresetDatabase();
            NormalizeDatabase(database);
            ApplyLocalImageRefs(database);
            var missing = database.presets.Count(IsMissingImage);
            return new PortraitPresetStats
            {
                detectedCharacterCount = detectedCharacterCount,
                presetCount = database.presets.Count,
                existingImageCount = database.presets.Count - missing,
                missingImageCount = missing
            };
        }

        private static void NormalizeDatabase(CharacterPortraitPresetDatabase database)
        {
            if (database == null)
            {
                return;
            }

            database.presets ??= new List<CharacterPortraitPreset>();
            foreach (var preset in database.presets.Where(preset => preset != null))
            {
                NormalizePreset(preset);
            }

            database.presets = database.presets
                .Where(preset => preset != null && !string.IsNullOrWhiteSpace(preset.id))
                .GroupBy(preset => preset.id, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    foreach (var duplicate in group.Skip(1))
                    {
                        MergePresetStoryData(first, duplicate);
                    }

                    return first;
                })
                .ToList();
        }

        private static void NormalizePreset(CharacterPortraitPreset preset)
        {
            if (preset == null)
            {
                return;
            }

            preset.displayName = string.IsNullOrWhiteSpace(preset.displayName) ? "Character" : preset.displayName.Trim();
            preset.aliases ??= new List<string>();
            if (!preset.aliases.Any(alias => string.Equals(alias, preset.displayName, StringComparison.OrdinalIgnoreCase)))
            {
                preset.aliases.Insert(0, preset.displayName);
            }

            preset.aliases = preset.aliases
                .SelectMany(AliasVariants)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            preset.id = SanitizeAsciiId(preset.id, BuildStablePresetId(preset.displayName, preset.aliases));
            preset.description = string.IsNullOrWhiteSpace(preset.description) ? preset.displayName : CompactWhitespace(preset.description);
            preset.imagePrompt = string.IsNullOrWhiteSpace(preset.imagePrompt)
                ? BuildDefaultImagePrompt(preset.displayName, preset.description)
                : CompactWhitespace(preset.imagePrompt);
            preset.imageRef ??= "";
            preset.spriteKey = string.IsNullOrWhiteSpace(preset.spriteKey) ? "branch" : preset.spriteKey.Trim();
            preset.priority = Mathf.Clamp(preset.priority, 0, 100);
        }

        private static string BuildPortraitPrompt(CharacterPortraitPreset preset)
        {
            NormalizePreset(preset);
            return AiBuilderImagePromptStyle.BuildPortraitPrompt(ExtractPortraitDetails(preset.imagePrompt));
        }

        private static string BuildDefaultImagePrompt(string displayName, string description)
        {
            var notes = Truncate(CompactWhitespace($"{displayName}. {description}"), 500);
            return "Character notes: " + notes;
        }

        private static string ExtractPortraitDetails(string prompt)
        {
            const string legacyPrefix = "Waist-up character portrait for a branching visual novel, single subject, centered face and shoulders, dark fantasy, low-poly stylized, muted cinematic lighting. Character notes:";
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return "medieval court character";
            }

            var compact = CompactWhitespace(prompt);
            return compact.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase)
                ? "Character notes: " + compact.Substring(legacyPrefix.Length).Trim()
                : compact;
        }

        private static string GuessSpriteKey(CharacterMention mention)
        {
            var text = NormalizeKey($"{mention.displayName} {mention.description}");
            if (ContainsAny(text, "bishop", "faith", "church", "oracle", "sorcerer", "magic", "relic"))
            {
                return "oracle";
            }

            if (ContainsAny(text, "guard", "captain", "soldier", "envoy", "knight", "gate"))
            {
                return "gate";
            }

            if (ContainsAny(text, "king", "queen", "ruler", "crown", "noble", "court"))
            {
                return "queen";
            }

            return "branch";
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            return values.Any(value => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static CharacterPortraitPreset ClonePreset(CharacterPortraitPreset preset)
        {
            return new CharacterPortraitPreset
            {
                id = preset.id,
                displayName = preset.displayName,
                aliases = preset.aliases == null ? new List<string>() : new List<string>(preset.aliases),
                description = preset.description,
                imagePrompt = preset.imagePrompt,
                imageRef = preset.imageRef,
                spriteKey = preset.spriteKey,
                priority = preset.priority
            };
        }

        private static IEnumerable<string> SplitAliases(string namePart)
        {
            if (string.IsNullOrWhiteSpace(namePart))
            {
                yield break;
            }

            var separators = new[] { '/', '\\', '|', ',', ';', '\uFF0F', '\u3001', '\uFF0C', '\uFF1B' };
            foreach (var item in namePart.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var alias = item.Trim();
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    yield return alias;
                }
            }
        }

        private static IEnumerable<string> AliasVariants(string alias)
        {
            alias = alias?.Trim();
            if (string.IsNullOrWhiteSpace(alias))
            {
                yield break;
            }

            yield return alias;
            const string pluralSuffix = "\u4EEC";
            if (alias.EndsWith(pluralSuffix, StringComparison.Ordinal) && alias.Length > pluralSuffix.Length)
            {
                yield return alias.Substring(0, alias.Length - pluralSuffix.Length);
            }
        }

        private static IEnumerable<string> AliasSet(CharacterPortraitPreset preset)
        {
            if (preset == null)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(preset.displayName))
            {
                yield return preset.displayName.Trim();
            }

            if (preset.aliases == null)
            {
                yield break;
            }

            foreach (var alias in preset.aliases.SelectMany(AliasVariants))
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    yield return alias.Trim();
                }
            }
        }

        private static bool AliasAppearsInText(string alias, string text)
        {
            alias = alias?.Trim();
            if (string.IsNullOrWhiteSpace(alias) || alias.Length < 2 || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildProjectSearchText(StoryProject project)
        {
            if (project == null)
            {
                return "";
            }

            var values = new List<string>();
            values.AddRange(project.sourceChunks.SelectMany(chunk => new[] { chunk?.titleGuess, chunk?.summary }));
            values.AddRange(project.summaries.SelectMany(summary => new[] { summary?.title, summary?.summary }));
            values.AddRange(project.summaries.SelectMany(summary => summary?.characters ?? new List<string>()));
            values.AddRange(project.chapters.SelectMany(chapter => new[] { chapter?.chapterTitle, chapter?.summary }));
            values.AddRange(project.chapters.SelectMany(chapter => chapter?.toneTags ?? new List<string>()));
            foreach (var anchor in project.chapters.SelectMany(chapter => chapter?.anchors ?? new List<StoryAnchorNode>()))
            {
                values.Add(anchor.title);
                values.Add(anchor.body);
                values.Add(anchor.imagePrompt);
                values.Add(anchor.stabilityNote);
                values.Add(anchor.leftChoice?.label);
                values.Add(anchor.leftChoice?.intent);
                values.Add(anchor.rightChoice?.label);
                values.Add(anchor.rightChoice?.intent);
            }

            return string.Join("\n", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static int FirstIndexOfAny(string value, params char[] candidates)
        {
            var best = -1;
            foreach (var candidate in candidates)
            {
                var index = value.IndexOf(candidate);
                if (index >= 0 && (best < 0 || index < best))
                {
                    best = index;
                }
            }

            return best;
        }

        private static string CleanEntry(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var cleaned = value.Trim();
            while (cleaned.Length > 0 && (cleaned[0] == '-' || cleaned[0] == '*' || cleaned[0] == '\t' || cleaned[0] == ' '))
            {
                cleaned = cleaned.Substring(1).TrimStart();
            }

            return cleaned;
        }

        private static string SanitizeAsciiId(string value, string fallback)
        {
            var raw = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw)
            {
                builder.Append(character < 128 && char.IsLetterOrDigit(character) ? character : '_');
            }

            var compact = builder.ToString();
            while (compact.Contains("__", StringComparison.Ordinal))
            {
                compact = compact.Replace("__", "_");
            }

            compact = compact.Trim('_');
            return string.IsNullOrWhiteSpace(compact) ? fallback : compact;
        }

        private static string BuildStablePresetId(string displayName, IEnumerable<string> aliases)
        {
            var asciiName = SanitizeAsciiId(displayName, "");
            if (!string.IsNullOrWhiteSpace(asciiName))
            {
                return asciiName;
            }

            var seed = $"{displayName}|{string.Join("|", aliases ?? Array.Empty<string>())}";
            return "char_" + StableHash(seed).ToString("x8");
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var character in value ?? "")
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                return hash;
            }
        }

        private static string NormalizeKey(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        }

        private static string CompactWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var builder = new StringBuilder(value.Length);
            var previousWasWhiteSpace = false;
            foreach (var character in value.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasWhiteSpace)
                    {
                        builder.Append(' ');
                    }

                    previousWasWhiteSpace = true;
                }
                else
                {
                    builder.Append(character);
                    previousWasWhiteSpace = false;
                }
            }

            return builder.ToString();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            return value.Substring(0, maxLength);
        }

        private sealed class CharacterMention
        {
            public string displayName;
            public List<string> aliases = new List<string>();
            public string description;
            public string imagePrompt;
            public string source;
            public int priority;
        }

        private sealed class PortraitGenerationJob
        {
            public PortraitGenerationJob(CharacterPortraitPreset preset, string targetPath)
            {
                this.preset = preset;
                this.targetPath = targetPath;
            }

            public CharacterPortraitPreset preset { get; }
            public string targetPath { get; }
        }
    }
}
#endif

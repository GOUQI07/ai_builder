#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public sealed class AIBuilderStoryImporterWindow : EditorWindow
    {
        private const float MinimumWindowWidth = 1000f;
        private const float MinimumWindowHeight = 920f;
        private const float ChunkColumnWidth = 260f;
        private const float ChapterColumnWidth = 300f;
        private const float DetailsMinWidth = 360f;
        private const char EditorSoftBreak = '\u200B';

        private StoryProject project;
        private IAiAdaptationService adaptationService;
        private CancellationTokenSource runCancellation;
        private Vector2 sourceScroll;
        private Vector2 chunkScroll;
        private Vector2 chapterScroll;
        private Vector2 detailScroll;
        private string pastedSource = "";
        private int selectedChunkIndex;
        private int selectedChapterIndex;
        private bool showSourceText;
        private bool running;
        private GUIStyle wrappingTextAreaStyle;
        private GUIStyle wrappingListButtonStyle;
        private float detailsContentWidth = DetailsMinWidth;

        [MenuItem("AI Builder/Story Importer/Open Window", false, 0)]
        public static void Open()
        {
            var window = GetWindow<AIBuilderStoryImporterWindow>("Story Importer");
            window.EnsureDefaultWindowSize();
            window.Show();
        }

        [MenuItem("AI Builder/Story Importer/Validate Project")]
        public static void ValidateProjectMenu()
        {
            var loaded = LoadProjectOrDefault();
            var result = StoryAuthoringUtility.ValidateProject(loaded);
            LogValidation(result);
        }

        [MenuItem("AI Builder/Story Importer/Export Mainline")]
        public static void ExportMainlineMenu()
        {
            var loaded = LoadProjectOrDefault();
            var result = StoryAuthoringUtility.ValidateProject(loaded);
            LogValidation(result);
            if (!result.IsValid)
            {
                return;
            }

            SaveMainline(loaded);
        }

        private void OnEnable()
        {
            minSize = MinimumWindowSize;
            EditorApplication.delayCall += EnsureDefaultWindowSizeDelayed;
            project = LoadProjectOrDefault();
            RefreshService();
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= EnsureDefaultWindowSizeDelayed;
            runCancellation?.Cancel();
            runCancellation?.Dispose();
            runCancellation = null;
        }

        private static Vector2 MinimumWindowSize => new Vector2(MinimumWindowWidth, MinimumWindowHeight);

        private float SourceTextAreaWidth => Mathf.Max(260f, position.width - 44f);

        private float DetailsColumnWidth => Mathf.Max(DetailsMinWidth, position.width - ChunkColumnWidth - ChapterColumnWidth - 34f);

        private void EnsureDefaultWindowSizeDelayed()
        {
            if (this == null)
            {
                return;
            }

            EnsureDefaultWindowSize();
        }

        private void EnsureDefaultWindowSize()
        {
            minSize = MinimumWindowSize;
            if (position.width >= MinimumWindowWidth && position.height >= MinimumWindowHeight)
            {
                return;
            }

            var rect = position;
            rect.width = Mathf.Max(rect.width, MinimumWindowWidth);
            rect.height = Mathf.Max(rect.height, MinimumWindowHeight);
            position = rect;
        }

        private void OnGUI()
        {
            project ??= StoryAuthoringUtility.CreateDefaultProject();
            StoryAuthoringUtility.NormalizeProject(project);

            DrawToolbar();
            EditorGUILayout.Space(6);
            DrawSettings();
            EditorGUILayout.Space(6);
            DrawImportPanel();
            EditorGUILayout.Space(6);
            DrawDefaultPanoramaPanel();
            EditorGUILayout.Space(6);
            DrawPortraitPresetPanel();
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawChunks();
                DrawChapters();
                DrawDetails();
            }

            DrawJobState();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(running))
                {
                    if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    {
                        project = LoadProjectOrDefault();
                    }

                    if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(58)))
                    {
                        SaveProject(project);
                    }

                    if (GUILayout.Button("Validate", EditorStyles.toolbarButton, GUILayout.Width(78)))
                    {
                        LogValidation(StoryAuthoringUtility.ValidateProject(project));
                    }

                    if (GUILayout.Button("Export Mainline", EditorStyles.toolbarButton, GUILayout.Width(118)))
                    {
                        var result = StoryAuthoringUtility.ValidateProject(project);
                        LogValidation(result);
                        if (result.IsValid)
                        {
                            SaveMainline(project);
                        }
                    }
                }

                if (running && GUILayout.Button("Cancel", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    runCancellation?.Cancel();
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label(StoryAuthoringPaths.StoryProjectAssetPath, EditorStyles.miniLabel);
            }
        }

        private void DrawSettings()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("Project", EditorStyles.boldLabel);
                project.title = EditorGUILayout.TextField("Title", project.title);
                project.projectId = EditorGUILayout.TextField("Project Id", project.projectId);
                project.sourceName = EditorGUILayout.TextField("Source Name", project.sourceName);

                using (new EditorGUILayout.HorizontalScope())
                {
                    project.targetChapterCount = EditorGUILayout.IntField("Target Chapters", project.targetChapterCount);
                    project.minAnchorsPerChapter = EditorGUILayout.IntField("Min Anchors", project.minAnchorsPerChapter);
                    project.maxAnchorsPerChapter = EditorGUILayout.IntField("Max Anchors", project.maxAnchorsPerChapter);
                    project.chunkCharacterLimit = EditorGUILayout.IntField("Chunk Characters", project.chunkCharacterLimit);
                }

                StoryAuthoringUtility.NormalizeProject(project);
            }
        }

        private void DrawImportPanel()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Source Import", EditorStyles.boldLabel);
                    showSourceText = EditorGUILayout.ToggleLeft("Show Text", showSourceText, GUILayout.Width(90));
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(running))
                    {
                        if (GUILayout.Button("Import .txt/.md", GUILayout.Width(120)))
                        {
                            ImportSourceFile();
                        }

                        if (GUILayout.Button("Import Pasted Text", GUILayout.Width(140)))
                        {
                            ImportSourceText(pastedSource, "Pasted Source");
                        }
                    }
                }

                if (showSourceText)
                {
                    sourceScroll = EditorGUILayout.BeginScrollView(sourceScroll, false, true, GUILayout.Height(160));
                    pastedSource = DrawWrappingTextArea(pastedSource, 150f, 260f, 18, SourceTextAreaWidth);
                    EditorGUILayout.EndScrollView();
                }

                EditorGUILayout.LabelField($"Chunks: {project.sourceChunks.Count}  Summaries: {project.summaries.Count}  Chapters: {project.chapters.Count}");
            }
        }

        private void DrawDefaultPanoramaPanel()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Default Background Panorama", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    project.enableDefaultPanorama = EditorGUILayout.ToggleLeft("Auto on Import", project.enableDefaultPanorama, GUILayout.Width(120));
                }

                project.defaultPanoramaPrompt = DrawWrappingTextArea(project.defaultPanoramaPrompt, 58f, 130f, 24, SourceTextAreaWidth);

                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(running || project.sourceChunks.Count == 0))
                {
                    if (GUILayout.Button("Rebuild Prompt", GUILayout.Width(130)))
                    {
                        project.defaultPanoramaPrompt = StoryAuthoringUtility.BuildDefaultPanoramaPrompt(project);
                        project.defaultPanoramaStatus = "";
                        project.defaultPanoramaError = "";
                        SaveProject(project);
                    }

                    if (GUILayout.Button("Generate Panorama", GUILayout.Width(160)))
                    {
                        QueueDefaultPanoramaGeneration();
                    }

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"Status: {DefaultPanoramaStatusLabel(project)}", EditorStyles.miniLabel, GUILayout.Width(220));
                }

                if (!string.IsNullOrWhiteSpace(project.defaultPanoramaPath))
                {
                    EditorGUILayout.SelectableLabel(project.defaultPanoramaPath, EditorStyles.miniLabel, GUILayout.Height(18));
                }

                if (!string.IsNullOrWhiteSpace(project.defaultPanoramaError))
                {
                    EditorGUILayout.HelpBox(project.defaultPanoramaError, MessageType.Warning);
                }
            }
        }

        private void DrawPortraitPresetPanel()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Portrait Presets", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField("Rebuilt from summarized story characters; image generation is manual.", EditorStyles.miniLabel);
                }

                var stats = AIBuilderPortraitPresetMenu.GetStats(project);
                EditorGUILayout.LabelField($"Detected Characters: {stats.detectedCharacterCount}  Presets: {stats.presetCount}  Images: {stats.existingImageCount}  Missing Images: {stats.missingImageCount}");

                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(running))
                {
                    if (GUILayout.Button("Rebuild Portrait Presets", GUILayout.Width(180)))
                    {
                        RebuildPortraitPresets(true, true);
                    }

                    if (GUILayout.Button("Generate Missing Portraits", GUILayout.Width(190)))
                    {
                        _ = AIBuilderPortraitPresetMenu.GenerateMissingPortraitImagesAsync(project);
                    }

                    if (GUILayout.Button("Validate Portrait Presets", GUILayout.Width(180)))
                    {
                        AIBuilderPortraitPresetMenu.Validate();
                    }
                }
            }
        }

        private void DrawChunks()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ChunkColumnWidth)))
            {
                EditorGUILayout.LabelField("Chunks", EditorStyles.boldLabel);
                chunkScroll = EditorGUILayout.BeginScrollView(chunkScroll, GUI.skin.box, GUILayout.Height(270));
                for (var i = 0; i < project.sourceChunks.Count; i++)
                {
                    var chunk = project.sourceChunks[i];
                    var label = $"{chunk.index:000} {chunk.status} | {chunk.titleGuess}";
                    var displayLabel = AddEditorSoftBreaks(label, 10);
                    if (GUILayout.Toggle(selectedChunkIndex == i, new GUIContent(displayLabel), WrappingListButtonStyle, GUILayout.Height(ListRowHeight(displayLabel, ChunkColumnWidth))))
                    {
                        selectedChunkIndex = i;
                    }
                }
                EditorGUILayout.EndScrollView();

                using (new EditorGUI.DisabledScope(running || project.sourceChunks.Count == 0))
                {
                    if (GUILayout.Button("Summarize Selected Chunk"))
                    {
                        SummarizeSelectedChunk();
                    }

                    if (GUILayout.Button("Summarize Next Pending Chunk"))
                    {
                        SummarizeNextPendingChunk();
                    }

                    if (GUILayout.Button("Summarize All Pending Chunks"))
                    {
                        SummarizeAllPendingChunks();
                    }
                }
            }
        }

        private void DrawChapters()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ChapterColumnWidth)))
            {
                EditorGUILayout.LabelField("Chapters", EditorStyles.boldLabel);
                chapterScroll = EditorGUILayout.BeginScrollView(chapterScroll, GUI.skin.box, GUILayout.Height(270));
                for (var i = 0; i < project.chapters.Count; i++)
                {
                    var chapter = project.chapters[i];
                    var label = $"{chapter.chapterIndex:000} {chapter.status} | {chapter.chapterTitle} ({chapter.anchors.Count})";
                    var displayLabel = AddEditorSoftBreaks(label, 10);
                    if (GUILayout.Toggle(selectedChapterIndex == i, new GUIContent(displayLabel), WrappingListButtonStyle, GUILayout.Height(ListRowHeight(displayLabel, ChapterColumnWidth))))
                    {
                        selectedChapterIndex = i;
                    }
                }
                EditorGUILayout.EndScrollView();

                using (new EditorGUI.DisabledScope(running))
                {
                    if (GUILayout.Button("Build Chapter Outline"))
                    {
                        BuildChapterOutline();
                    }

                    using (new EditorGUI.DisabledScope(project.chapters.Count == 0))
                    {
                        if (GUILayout.Button("Generate Anchors For Selected"))
                        {
                            GenerateAnchorsForSelectedChapter();
                        }

                        if (GUILayout.Button("Generate Missing Anchors"))
                        {
                            GenerateMissingAnchors();
                        }

                        if (GUILayout.Button("Generate All"))
                        {
                            GenerateAnchorsForAllChapters();
                        }
                    }
                }
            }
        }

        private void DrawDetails()
        {
            var detailsColumnWidth = DetailsColumnWidth;
            detailsContentWidth = Mathf.Max(240f, detailsColumnWidth - 28f);
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(detailsColumnWidth), GUILayout.ExpandWidth(false)))
            {
                EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
                detailScroll = EditorGUILayout.BeginScrollView(detailScroll, false, true, GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar, GUI.skin.box, GUILayout.Height(430), GUILayout.Width(detailsColumnWidth));
                DrawChunkDetail();
                EditorGUILayout.Space(8);
                DrawChapterDetail();
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawChunkDetail()
        {
            if (project.sourceChunks.Count == 0)
            {
                EditorGUILayout.HelpBox("Import source text to create chunks.", MessageType.Info);
                return;
            }

            selectedChunkIndex = Mathf.Clamp(selectedChunkIndex, 0, project.sourceChunks.Count - 1);
            var chunk = project.sourceChunks[selectedChunkIndex];
            EditorGUILayout.LabelField("Selected Chunk", EditorStyles.boldLabel);
            chunk.titleGuess = EditorGUILayout.TextField("Title Guess", chunk.titleGuess);
            chunk.status = EditorGUILayout.TextField("Status", chunk.status);
            chunk.error = EditorGUILayout.TextField("Error", chunk.error ?? "");
            EditorGUILayout.LabelField("Summary");
            chunk.summary = DrawWrappingTextArea(chunk.summary, 64f, 160f, 16, detailsContentWidth);
        }

        private void DrawChapterDetail()
        {
            if (project.chapters.Count == 0)
            {
                EditorGUILayout.HelpBox("Build a chapter outline after summarizing chunks.", MessageType.Info);
                return;
            }

            selectedChapterIndex = Mathf.Clamp(selectedChapterIndex, 0, project.chapters.Count - 1);
            var chapter = project.chapters[selectedChapterIndex];
            EditorGUILayout.LabelField("Selected Chapter", EditorStyles.boldLabel);
            chapter.chapterId = EditorGUILayout.TextField("Id", chapter.chapterId);
            chapter.chapterIndex = EditorGUILayout.IntField("Index", chapter.chapterIndex);
            chapter.chapterTitle = EditorGUILayout.TextField("Title", chapter.chapterTitle);
            chapter.status = StatusPopup("Status", chapter.status);
            chapter.sourceChunkRefs = EditCsvList("Source Chunks", chapter.sourceChunkRefs);
            chapter.toneTags = EditCsvList("Tone Tags", chapter.toneTags);
            EditorGUILayout.LabelField("Summary");
            chapter.summary = DrawWrappingTextArea(chapter.summary, 92f, 220f, 16, detailsContentWidth);

            using (new EditorGUILayout.HorizontalScope())
            {
                var buttonWidth = CompactReviewButtonWidth(3);
                if (GUILayout.Button("Approve Chapter", GUILayout.Width(buttonWidth)))
                {
                    chapter.status = StoryAuthoringStatuses.Approved;
                    SaveProject(project);
                }

                GUILayout.Space(6f);
                if (GUILayout.Button("Reject Chapter", GUILayout.Width(buttonWidth)))
                {
                    chapter.status = StoryAuthoringStatuses.Rejected;
                    SaveProject(project);
                }

                GUILayout.Space(6f);
                if (GUILayout.Button("Approve All", GUILayout.Width(buttonWidth)))
                {
                    ApproveAllReviewItems();
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Anchors ({chapter.anchors.Count})", EditorStyles.boldLabel);
            for (var i = 0; i < chapter.anchors.Count; i++)
            {
                DrawAnchor(chapter, chapter.anchors[i], i);
            }
        }

        private void DrawAnchor(StoryChapter chapter, StoryAnchorNode anchor, int index)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField($"{index + 1:000} {anchor.nodeId}", EditorStyles.boldLabel);
                anchor.nodeId = EditorGUILayout.TextField("Node Id", anchor.nodeId);
                anchor.anchorIndex = EditorGUILayout.IntField("Anchor Index", anchor.anchorIndex);
                anchor.title = EditorGUILayout.TextField("Title", anchor.title);
                anchor.status = StatusPopup("Status", anchor.status);
                anchor.mainlineChoice = EditorGUILayout.Popup("Mainline Choice", string.Equals(anchor.mainlineChoice, "right", StringComparison.OrdinalIgnoreCase) ? 1 : 0, new[] { "left", "right" }) == 1 ? "right" : "left";
                anchor.imageRef = EditorGUILayout.TextField("Image Ref", anchor.imageRef ?? "");
                EditorGUILayout.LabelField("Image Prompt");
                anchor.imagePrompt = DrawWrappingTextArea(anchor.imagePrompt, 42f, 110f, 24, Mathf.Max(220f, detailsContentWidth - 18f));
                EditorGUILayout.LabelField("Stability Note");
                anchor.stabilityNote = DrawWrappingTextArea(anchor.stabilityNote, 42f, 110f, 18, Mathf.Max(220f, detailsContentWidth - 18f));
                EditorGUILayout.LabelField("Body");
                anchor.body = DrawWrappingTextArea(anchor.body, 78f, 180f, 16, Mathf.Max(220f, detailsContentWidth - 18f));

                DrawChoice("Left Choice", anchor.leftChoice ??= StoryAuthoringUtility.NewChoice("left"));
                DrawChoice("Right Choice", anchor.rightChoice ??= StoryAuthoringUtility.NewChoice("right"));

                using (new EditorGUILayout.HorizontalScope())
                {
                    var buttonWidth = CompactReviewButtonWidth(3);
                    if (GUILayout.Button("Approve Anchor", GUILayout.Width(buttonWidth)))
                    {
                        anchor.status = StoryAuthoringStatuses.Approved;
                        SaveProject(project);
                    }

                    GUILayout.Space(6f);
                    if (GUILayout.Button("Reject Anchor", GUILayout.Width(buttonWidth)))
                    {
                        anchor.status = StoryAuthoringStatuses.Rejected;
                        SaveProject(project);
                    }

                    GUILayout.Space(6f);
                    if (GUILayout.Button("Normalize", GUILayout.Width(buttonWidth)))
                    {
                        StoryAuthoringUtility.NormalizeAnchor(chapter, anchor, index + 1);
                        SaveProject(project);
                    }
                }
            }
        }

        private void DrawChoice(string title, ChoiceOption choice)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            choice.id = EditorGUILayout.TextField("Id", choice.id);
            EditorGUILayout.LabelField("Label");
            choice.label = DrawWrappingTextArea(choice.label, 34f, 82f, 16, Mathf.Max(220f, detailsContentWidth - 18f));
            EditorGUILayout.LabelField("Intent");
            choice.intent = DrawWrappingTextArea(choice.intent, 42f, 110f, 16, Mathf.Max(220f, detailsContentWidth - 18f));
            choice.direction = EditorGUILayout.TextField("Direction", choice.direction);
            choice.nextMainlineNodeId = EditorGUILayout.TextField("Next Mainline", choice.nextMainlineNodeId ?? "");
            choice.statHint ??= new PlayerStats(0, 0, 0, 0);
            using (new EditorGUILayout.HorizontalScope())
            {
                choice.statHint.life = EditorGUILayout.IntField("Life", choice.statHint.life);
                choice.statHint.force = EditorGUILayout.IntField("Force", choice.statHint.force);
                choice.statHint.wealth = EditorGUILayout.IntField("Wealth", choice.statHint.wealth);
                choice.statHint.faith = EditorGUILayout.IntField("Faith", choice.statHint.faith);
            }
        }

        private void DrawJobState()
        {
            if (project?.job == null)
            {
                return;
            }

            EditorGUILayout.Space(4);
            var progress = project.job.total <= 0 ? 0f : project.job.completed / (float)project.job.total;
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20f), progress, $"{project.job.stage}: {project.job.message} ({project.job.completed}/{project.job.total})");
            if (!string.IsNullOrWhiteSpace(project.job.lastError))
            {
                EditorGUILayout.HelpBox(project.job.lastError, MessageType.Warning);
            }
        }

        private void ImportSourceFile()
        {
            var path = EditorUtility.OpenFilePanel("Import story source", "", "txt,md");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            ImportSourceText(File.ReadAllText(path), Path.GetFileName(path));
        }

        private void ImportSourceText(string sourceText, string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                Debug.LogWarning("AI Builder Story Importer ignored empty source text.");
                return;
            }

            StoryAuthoringUtility.ImportSourceText(project, sourceText, sourceName, project.chunkCharacterLimit);
            selectedChunkIndex = 0;
            selectedChapterIndex = 0;
            SaveProject(project);
            Debug.Log($"AI Builder Story Importer imported {project.sourceChunks.Count} chunk(s) from {sourceName}.");
            if (project.enableDefaultPanorama)
            {
                QueueDefaultPanoramaGeneration();
            }
        }

        private void QueueDefaultPanoramaGeneration()
        {
            if (project == null || project.sourceChunks.Count == 0)
            {
                Debug.LogWarning("AI Builder Story Importer needs source text before generating a default panorama.");
                return;
            }

            if (string.IsNullOrWhiteSpace(project.defaultPanoramaPrompt))
            {
                project.defaultPanoramaPrompt = StoryAuthoringUtility.BuildDefaultPanoramaPrompt(project);
            }

            project.defaultPanoramaStatus = NodeImageStatuses.Queued;
            project.defaultPanoramaError = "";
            SaveProject(project, false);
            _ = RunAsync("Generate Default Panorama", 1, GenerateDefaultPanorama);
        }

        private async Task GenerateDefaultPanorama(CancellationToken cancellationToken)
        {
            StoryAuthoringUtility.NormalizeProject(project);
            if (!project.enableDefaultPanorama)
            {
                project.defaultPanoramaStatus = NodeImageStatuses.SkippedByPolicy;
                project.defaultPanoramaError = "";
                return;
            }

            if (string.IsNullOrWhiteSpace(project.defaultPanoramaPrompt))
            {
                project.defaultPanoramaPrompt = StoryAuthoringUtility.BuildDefaultPanoramaPrompt(project);
            }

            var settings = AiProviderSettings.Load();
            if (!settings.CanUseImage)
            {
                project.defaultPanoramaStatus = NodeImageStatuses.SkippedUnavailable;
                project.defaultPanoramaError = "Image provider is not configured; prompt was saved for later generation.";
                project.job.completed = 1;
                SaveProject(project, false);
                return;
            }

            project.defaultPanoramaStatus = NodeImageStatuses.Generating;
            project.defaultPanoramaError = "";
            project.job.message = "story default background";
            SaveProject(project, false);
            Repaint();

            var imageService = AiProviderFactory.CreateImageService(settings);
            var result = await imageService.GenerateImageAsync(project.defaultPanoramaPrompt, cancellationToken, AiImagePurpose.Panorama);
            if (result != null && result.Succeeded)
            {
                var path = SaveDefaultPanoramaImage(project, result.bytes);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    project.defaultPanoramaPath = path;
                    project.defaultPanoramaStatus = NodeImageStatuses.Generated;
                    project.defaultPanoramaError = "";
                    Debug.Log($"AI Builder Story Importer generated default panorama: {path}");
                    SaveMainlineIfValid(project);
                }
                else
                {
                    project.defaultPanoramaStatus = NodeImageStatuses.Failed;
                    project.defaultPanoramaError = "Default panorama image bytes were returned but could not be saved.";
                }
            }
            else
            {
                project.defaultPanoramaStatus = NodeImageStatuses.Failed;
                project.defaultPanoramaError = result?.error ?? "Default panorama generation returned no image bytes.";
            }

            project.job.completed = 1;
            SaveProject(project, false);
        }

        private void SummarizeSelectedChunk()
        {
            if (project.sourceChunks.Count == 0)
            {
                return;
            }

            selectedChunkIndex = Mathf.Clamp(selectedChunkIndex, 0, project.sourceChunks.Count - 1);
            _ = RunAsync("Summarize Chunk", 1, async token => await SummarizeChunk(project.sourceChunks[selectedChunkIndex], token));
        }

        private void SummarizeNextPendingChunk()
        {
            var chunk = project.sourceChunks.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.summary) || string.Equals(item.status, StoryAuthoringStatuses.Failed, StringComparison.OrdinalIgnoreCase));
            if (chunk == null)
            {
                Debug.Log("AI Builder Story Importer: no pending chunks.");
                return;
            }

            _ = RunAsync("Summarize Next Chunk", 1, async token => await SummarizeChunk(chunk, token));
        }

        private void SummarizeAllPendingChunks()
        {
            var chunks = project.sourceChunks
                .Where(item => string.IsNullOrWhiteSpace(item.summary) || string.Equals(item.status, StoryAuthoringStatuses.Failed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _ = RunAsync("Summarize All Chunks", chunks.Count, async token =>
            {
                foreach (var chunk in chunks)
                {
                    token.ThrowIfCancellationRequested();
                    await SummarizeChunk(chunk, token);
                    project.job.completed++;
                    SaveProject(project, false);
                    Repaint();
                }
            });
        }

        private void BuildChapterOutline()
        {
            _ = RunAsync("Build Chapter Outline", 1, async token =>
            {
                var summaries = project.summaries.Count > 0
                    ? project.summaries
                    : project.sourceChunks.Select(item => new SourceChunkSummary
                    {
                        chunkId = item.id,
                        chunkIndex = item.index,
                        title = item.titleGuess,
                        summary = string.IsNullOrWhiteSpace(item.summary) ? item.text : item.summary
                    }).ToList();
                var chapters = await adaptationService.BuildChapterOutlineAsync(summaries, project.targetChapterCount, token);
                project.chapters = chapters ?? new List<StoryChapter>();
                StoryAuthoringUtility.NormalizeProject(project);
                RebuildPortraitPresets(false, false);
                project.job.completed = 1;
                SaveProject(project);
            });
        }

        private void GenerateAnchorsForSelectedChapter()
        {
            if (project.chapters.Count == 0)
            {
                return;
            }

            selectedChapterIndex = Mathf.Clamp(selectedChapterIndex, 0, project.chapters.Count - 1);
            _ = RunAsync("Generate Anchors", 1, async token => await GenerateAnchors(project.chapters[selectedChapterIndex], token));
        }

        private void GenerateMissingAnchors()
        {
            var chapters = project.chapters
                .Where(chapter => !StoryAuthoringStatuses.IsRejected(chapter.status)
                                  && chapter.anchors.Count(anchor => !StoryAuthoringStatuses.IsRejected(anchor.status)) < project.minAnchorsPerChapter)
                .ToList();
            _ = RunAsync("Generate Missing Anchors", chapters.Count, async token =>
            {
                foreach (var chapter in chapters)
                {
                    token.ThrowIfCancellationRequested();
                    await GenerateAnchors(chapter, token);
                    project.job.completed++;
                    SaveProject(project, false);
                    Repaint();
                }
            });
        }

        private void GenerateAnchorsForAllChapters()
        {
            var chapters = project.chapters
                .Where(chapter => !StoryAuthoringStatuses.IsRejected(chapter.status))
                .ToList();
            _ = RunAsync("Generate All Anchors", chapters.Count, async token =>
            {
                foreach (var chapter in chapters)
                {
                    token.ThrowIfCancellationRequested();
                    await GenerateAnchors(chapter, token);
                    project.job.completed++;
                    SaveProject(project, false);
                    Repaint();
                }
            });
        }

        private void ApproveAllReviewItems()
        {
            foreach (var chapter in project.chapters.Where(chapter => !StoryAuthoringStatuses.IsRejected(chapter.status)))
            {
                chapter.status = StoryAuthoringStatuses.Approved;
                foreach (var anchor in chapter.anchors.Where(anchor => !StoryAuthoringStatuses.IsRejected(anchor.status)))
                {
                    anchor.status = StoryAuthoringStatuses.Approved;
                }
            }

            SaveProject(project);
            Debug.Log("AI Builder Story Importer approved all non-rejected chapters and anchors.");
        }

        private async Task SummarizeChunk(SourceChunk chunk, CancellationToken cancellationToken)
        {
            chunk.status = "Summarizing";
            chunk.error = "";
            project.job.message = chunk.id;
            var summary = await adaptationService.SummarizeChunkAsync(chunk, cancellationToken);
            chunk.summary = summary.summary;
            chunk.status = StoryAuthoringStatuses.Summarized;
            UpsertSummary(summary);
            RebuildPortraitPresets(false, false);
            SaveProject(project, false);
        }

        private async Task GenerateAnchors(StoryChapter chapter, CancellationToken cancellationToken)
        {
            project.job.message = chapter.chapterId;
            var anchors = await adaptationService.BuildAnchorNodesAsync(chapter, project.minAnchorsPerChapter, project.maxAnchorsPerChapter, cancellationToken);
            chapter.anchors = anchors ?? new List<StoryAnchorNode>();
            StoryAuthoringUtility.NormalizeChapter(chapter, chapter.chapterIndex);
            RebuildPortraitPresets(false, false);
            SaveProject(project, false);
        }

        private void RebuildPortraitPresets(bool refreshAssetDatabase, bool log)
        {
            var report = AIBuilderPortraitPresetMenu.RebuildFromStoryProject(project, true, true, refreshAssetDatabase);
            if (log)
            {
                Debug.Log($"AI Builder Story Importer refreshed portrait presets: {report.Summary()}, new={report.newPresetCount}, reused={report.reusedPresetCount}.");
            }
        }

        private async Task RunAsync(string stage, int total, Func<CancellationToken, Task> action)
        {
            if (running)
            {
                return;
            }

            RefreshService();
            running = true;
            runCancellation?.Cancel();
            runCancellation?.Dispose();
            runCancellation = new CancellationTokenSource();
            project.job.isRunning = true;
            project.job.stage = stage;
            project.job.message = "";
            project.job.completed = 0;
            project.job.total = Mathf.Max(1, total);
            project.job.lastError = "";
            project.job.updatedAt = DateTime.UtcNow.ToString("O");
            Repaint();

            try
            {
                await action(runCancellation.Token);
                if (project.job.completed <= 0)
                {
                    project.job.completed = project.job.total;
                }

                project.job.lastError = "";
            }
            catch (OperationCanceledException)
            {
                project.job.lastError = "Cancelled.";
            }
            catch (Exception ex)
            {
                project.job.lastError = ex.Message;
                Debug.LogWarning($"AI Builder Story Importer {stage} failed safely: {ex.Message}");
            }
            finally
            {
                running = false;
                project.job.isRunning = false;
                project.job.stage = "Idle";
                project.job.updatedAt = DateTime.UtcNow.ToString("O");
                StoryAuthoringUtility.NormalizeProject(project);
                SaveProject(project);
                Repaint();
            }
        }

        private void UpsertSummary(SourceChunkSummary summary)
        {
            if (summary == null)
            {
                return;
            }

            var index = project.summaries.FindIndex(item => item.chunkId == summary.chunkId);
            if (index >= 0)
            {
                project.summaries[index] = summary;
            }
            else
            {
                project.summaries.Add(summary);
            }
        }

        private void RefreshService()
        {
            adaptationService = AiAdaptationServiceFactory.Create(AiProviderSettings.Load());
        }

        private GUIStyle WrappingTextAreaStyle
        {
            get
            {
                wrappingTextAreaStyle ??= new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = true,
                    stretchWidth = true
                };
                return wrappingTextAreaStyle;
            }
        }

        private GUIStyle WrappingListButtonStyle
        {
            get
            {
                wrappingListButtonStyle ??= new GUIStyle(GUI.skin.button)
                {
                    wordWrap = true,
                    alignment = TextAnchor.MiddleLeft,
                    stretchWidth = true,
                    padding = new RectOffset(8, 8, 4, 4)
                };
                return wrappingListButtonStyle;
            }
        }

        private float ListRowHeight(string label, float columnWidth)
        {
            var content = new GUIContent(label ?? "");
            var calculated = WrappingListButtonStyle.CalcHeight(content, Mathf.Max(80f, columnWidth - 12f));
            return Mathf.Clamp(calculated + 4f, 22f, 64f);
        }

        private float CompactReviewButtonWidth(int buttonCount)
        {
            var safeCount = Mathf.Max(1, buttonCount);
            var available = Mathf.Max(220f, detailsContentWidth - 18f);
            var gapWidth = 6f * (safeCount - 1);
            return Mathf.Clamp((available - gapWidth) / safeCount, 96f, 160f);
        }

        private string DrawWrappingTextArea(string value, float minHeight, float maxHeight, int softBreakInterval, float fixedWidth = 0f)
        {
            var displayValue = AddEditorSoftBreaks(value ?? "", softBreakInterval);
            var width = Mathf.Max(180f, fixedWidth > 0f ? fixedWidth : EditorGUIUtility.currentViewWidth - 44f);
            var height = Mathf.Clamp(WrappingTextAreaStyle.CalcHeight(new GUIContent(displayValue), width) + 8f, minHeight, maxHeight);
            EditorGUI.BeginChangeCheck();
            var rect = EditorGUILayout.GetControlRect(false, height, WrappingTextAreaStyle, GUILayout.Width(width), GUILayout.Height(height));
            var edited = EditorGUI.TextArea(rect, displayValue, WrappingTextAreaStyle);
            return EditorGUI.EndChangeCheck() ? RemoveEditorSoftBreaks(edited) : value ?? "";
        }

        private static string AddEditorSoftBreaks(string value, int interval)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var safeInterval = Mathf.Max(4, interval);
            var result = new System.Text.StringBuilder(value.Length + value.Length / safeInterval);
            var runLength = 0;
            foreach (var character in value)
            {
                if (character == EditorSoftBreak)
                {
                    continue;
                }

                result.Append(character);
                if (char.IsWhiteSpace(character) || character < 128)
                {
                    runLength = 0;
                    continue;
                }

                runLength++;
                if (runLength >= safeInterval)
                {
                    result.Append(EditorSoftBreak);
                    runLength = 0;
                }
            }

            return result.ToString();
        }

        private static string RemoveEditorSoftBreaks(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace(EditorSoftBreak.ToString(), "");
        }

        private static string StatusPopup(string label, string status)
        {
            var normalized = StoryAuthoringStatuses.NormalizeReviewStatus(status);
            var index = Mathf.Max(0, Array.IndexOf(StoryAuthoringStatuses.ReviewStatuses, normalized));
            return StoryAuthoringStatuses.ReviewStatuses[EditorGUILayout.Popup(label, index, StoryAuthoringStatuses.ReviewStatuses)];
        }

        private static List<string> EditCsvList(string label, List<string> values)
        {
            values ??= new List<string>();
            var text = EditorGUILayout.TextField(label, string.Join(", ", values));
            return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        private static StoryProject LoadProjectOrDefault()
        {
            try
            {
                if (File.Exists(StoryAuthoringPaths.StoryProjectAssetPath))
                {
                    var loaded = JsonConvert.DeserializeObject<StoryProject>(File.ReadAllText(StoryAuthoringPaths.StoryProjectAssetPath));
                    if (loaded != null)
                    {
                        StoryAuthoringUtility.NormalizeProject(loaded);
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder Story Importer project ignored: {ex.Message}");
            }

            return StoryAuthoringUtility.CreateDefaultProject();
        }

        private static void SaveProject(StoryProject storyProject, bool refreshAssetDatabase = true)
        {
            StoryAuthoringUtility.NormalizeProject(storyProject);
            Directory.CreateDirectory(Path.GetDirectoryName(StoryAuthoringPaths.StoryProjectAssetPath));
            File.WriteAllText(StoryAuthoringPaths.StoryProjectAssetPath, JsonConvert.SerializeObject(storyProject, Formatting.Indented));
            if (refreshAssetDatabase)
            {
                AssetDatabase.Refresh();
            }
        }

        private static void SaveMainline(StoryProject storyProject)
        {
            var graph = StoryAuthoringUtility.ExportToStoryGraph(storyProject);
            Directory.CreateDirectory(Path.GetDirectoryName(StoryAuthoringPaths.MainlineAssetPath));
            File.WriteAllText(StoryAuthoringPaths.MainlineAssetPath, JsonConvert.SerializeObject(graph, Formatting.Indented));
            AssetDatabase.Refresh();
            Debug.Log($"AI Builder Story Importer exported {graph.nodes.Count} mainline node(s) to {StoryAuthoringPaths.MainlineAssetPath}.");
        }

        private static void SaveMainlineIfValid(StoryProject storyProject)
        {
            var result = StoryAuthoringUtility.ValidateProject(storyProject);
            if (result.IsValid)
            {
                SaveMainline(storyProject);
            }
        }

        private static string SaveDefaultPanoramaImage(StoryProject storyProject, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return "";
            }

            try
            {
                var directory = Path.Combine(NodeCacheService.CacheDirectory, "DefaultPanoramas");
                Directory.CreateDirectory(directory);
                var projectId = StoryAuthoringUtility.SanitizeId(storyProject?.projectId ?? "story_project");
                var path = Path.Combine(directory, $"{projectId}_default_pano_style_v2.png");
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder Story Importer could not save default panorama: {ex.Message}");
                return "";
            }
        }

        private static string DefaultPanoramaStatusLabel(StoryProject storyProject)
        {
            if (storyProject == null)
            {
                return "(none)";
            }

            return string.IsNullOrWhiteSpace(storyProject.defaultPanoramaStatus)
                ? "(prompt only)"
                : storyProject.defaultPanoramaStatus;
        }

        private static void LogValidation(StoryAuthoringValidationResult result)
        {
            if (result == null)
            {
                Debug.LogError("AI Builder Story Importer validation failed: no result.");
                return;
            }

            foreach (var warning in result.warnings)
            {
                Debug.LogWarning($"AI Builder Story Importer validation warning: {warning}");
            }

            foreach (var error in result.errors)
            {
                Debug.LogError($"AI Builder Story Importer validation error: {error}");
            }

            if (result.IsValid)
            {
                Debug.Log($"AI Builder Story Importer validation passed: {result.Summary()}");
            }
        }
    }
}
#endif

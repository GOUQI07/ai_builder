#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public sealed class AIBuilderNodeEditorWindow : EditorWindow
    {
        private const string MainlineAssetPath = "Assets/AIBuilder/Resources/mainline_nodes.json";

        private StoryGraph graph;
        private int selectedIndex;
        private Vector2 leftScroll;
        private Vector2 rightScroll;
        private Vector2 cacheScroll;

        [MenuItem("AI Builder/Node Editor")]
        public static void Open()
        {
            GetWindow<AIBuilderNodeEditorWindow>("AI Builder Nodes");
        }

        private void OnEnable()
        {
            LoadGraph();
        }

        private void OnGUI()
        {
            if (graph == null)
            {
                LoadGraph();
            }

            DrawToolbar();
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawNodeList();
                DrawNodeDetail();
            }

            EditorGUILayout.Space(8);
            DrawGraphPreview();
            EditorGUILayout.Space(8);
            DrawCachePreview();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    LoadGraph();
                }

                if (GUILayout.Button("Save Mainline", EditorStyles.toolbarButton, GUILayout.Width(112)))
                {
                    SaveGraph();
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"Runtime config: {AiProviderSettings.LocalConfigPath}", EditorStyles.miniLabel);
            }
        }

        private void DrawNodeList()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(240)))
            {
                EditorGUILayout.LabelField("主干节点", EditorStyles.boldLabel);
                leftScroll = EditorGUILayout.BeginScrollView(leftScroll, GUI.skin.box, GUILayout.Height(290));
                for (var i = 0; i < graph.nodes.Count; i++)
                {
                    var node = graph.nodes[i];
                    var label = $"{node.mainlineIndex}. {node.title}";
                    if (GUILayout.Toggle(selectedIndex == i, label, "Button"))
                    {
                        selectedIndex = i;
                    }
                }
                EditorGUILayout.EndScrollView();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add"))
                    {
                        graph.nodes.Add(new StoryNode
                        {
                            id = $"main_{graph.nodes.Count + 1:000}",
                            chapterId = graph.chapterId,
                            title = "新节点",
                            body = "填写剧情文本。",
                            imageRef = "queen",
                            nodeKind = StoryNodeKind.Mainline,
                            mainlineIndex = graph.nodes.Count + 1,
                            leftChoice = NewChoice("left"),
                            rightChoice = NewChoice("right")
                        });
                        selectedIndex = graph.nodes.Count - 1;
                    }

                    using (new EditorGUI.DisabledScope(graph.nodes.Count <= 1))
                    {
                        if (GUILayout.Button("Remove"))
                        {
                            graph.nodes.RemoveAt(Mathf.Clamp(selectedIndex, 0, graph.nodes.Count - 1));
                            selectedIndex = Mathf.Clamp(selectedIndex, 0, graph.nodes.Count - 1);
                        }
                    }
                }
            }
        }

        private void DrawNodeDetail()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField("节点详情", EditorStyles.boldLabel);
                rightScroll = EditorGUILayout.BeginScrollView(rightScroll, GUI.skin.box, GUILayout.Height(290));

                if (graph.nodes.Count == 0)
                {
                    EditorGUILayout.HelpBox("没有节点。", MessageType.Info);
                    EditorGUILayout.EndScrollView();
                    return;
                }

                selectedIndex = Mathf.Clamp(selectedIndex, 0, graph.nodes.Count - 1);
                var node = graph.nodes[selectedIndex];
                node.id = EditorGUILayout.TextField("Id", node.id);
                node.chapterId = EditorGUILayout.TextField("Chapter", node.chapterId);
                node.title = EditorGUILayout.TextField("Title", node.title);
                node.mainlineIndex = EditorGUILayout.IntField("Mainline Index", node.mainlineIndex);
                node.nodeKind = (StoryNodeKind)EditorGUILayout.EnumPopup("Kind", node.nodeKind);
                node.imageRef = EditorGUILayout.TextField("Image Ref", node.imageRef);
                EditorGUILayout.LabelField("Body");
                node.body = EditorGUILayout.TextArea(node.body, GUILayout.MinHeight(70));

                EditorGUILayout.Space(8);
                DrawChoice("Left Choice", node.leftChoice ??= NewChoice("left"));
                EditorGUILayout.Space(6);
                DrawChoice("Right Choice", node.rightChoice ??= NewChoice("right"));
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawChoice(string title, ChoiceOption choice)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            choice.id = EditorGUILayout.TextField("Id", choice.id);
            choice.label = EditorGUILayout.TextField("Label", choice.label);
            choice.intent = EditorGUILayout.TextField("Intent", choice.intent);
            choice.direction = EditorGUILayout.TextField("Direction", choice.direction);
            choice.nextMainlineNodeId = EditorGUILayout.TextField("Next Mainline", choice.nextMainlineNodeId);
            choice.statHint ??= new PlayerStats(0, 0, 0, 0);
            using (new EditorGUILayout.HorizontalScope())
            {
                choice.statHint.life = EditorGUILayout.IntField("Life", choice.statHint.life);
                choice.statHint.force = EditorGUILayout.IntField("Force", choice.statHint.force);
                choice.statHint.wealth = EditorGUILayout.IntField("Wealth", choice.statHint.wealth);
                choice.statHint.faith = EditorGUILayout.IntField("Faith", choice.statHint.faith);
            }
        }

        private void DrawGraphPreview()
        {
            EditorGUILayout.LabelField("主干与缓存网络预览", EditorStyles.boldLabel);
            var rect = GUILayoutUtility.GetRect(position.width - 24f, 150f, GUI.skin.box);
            GUI.Box(rect, GUIContent.none);

            if (graph.nodes.Count == 0)
            {
                return;
            }

            Handles.BeginGUI();
            var ordered = graph.nodes.OrderBy(node => node.mainlineIndex).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var x = Mathf.Lerp(rect.x + 70f, rect.xMax - 70f, ordered.Count == 1 ? 0.5f : i / (float)(ordered.Count - 1));
                var y = rect.y + 60f;
                if (i < ordered.Count - 1)
                {
                    var nextX = Mathf.Lerp(rect.x + 70f, rect.xMax - 70f, (i + 1) / (float)(ordered.Count - 1));
                    Handles.color = new Color(0.8f, 0.68f, 0.36f, 1f);
                    Handles.DrawLine(new Vector3(x, y), new Vector3(nextX, y));
                }

                Handles.color = i == selectedIndex ? new Color(1f, 0.84f, 0.28f, 1f) : new Color(0.38f, 0.28f, 0.14f, 1f);
                Handles.DrawSolidDisc(new Vector3(x, y), Vector3.forward, 18f);
                GUI.Label(new Rect(x - 42f, y + 24f, 84f, 42f), ordered[i].title, EditorStyles.centeredGreyMiniLabel);
            }

            var cache = LoadCache();
            for (var i = 0; i < cache.entries.Count; i++)
            {
                var sourceIndex = Mathf.Max(0, ordered.FindIndex(node => node.id == cache.entries[i].sourceNodeId));
                var sourceX = Mathf.Lerp(rect.x + 70f, rect.xMax - 70f, ordered.Count == 1 ? 0.5f : sourceIndex / (float)(ordered.Count - 1));
                var branchX = sourceX + ((i % 2 == 0) ? -44f : 44f);
                var branchY = rect.y + 112f;
                Handles.color = new Color(0.28f, 0.72f, 0.52f, 0.88f);
                Handles.DrawLine(new Vector3(sourceX, rect.y + 78f), new Vector3(branchX, branchY));
                Handles.DrawSolidDisc(new Vector3(branchX, branchY), Vector3.forward, 8f);
            }
            Handles.EndGUI();
        }

        private void DrawCachePreview()
        {
            EditorGUILayout.LabelField("玩家分支缓存", EditorStyles.boldLabel);
            var cache = LoadCache();
            cacheScroll = EditorGUILayout.BeginScrollView(cacheScroll, GUI.skin.box, GUILayout.Height(130));
            if (cache.entries.Count == 0)
            {
                EditorGUILayout.HelpBox("还没有缓存。运行 Demo 后选择偏离主干的选项即可生成。", MessageType.Info);
            }
            else
            {
                foreach (var entry in cache.entries)
                {
                    EditorGUILayout.LabelField($"{entry.status} | {entry.sourceNodeId} -> {entry.choiceId} | {entry.resultNode?.title}");
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void LoadGraph()
        {
            try
            {
                if (File.Exists(MainlineAssetPath))
                {
                    graph = JsonConvert.DeserializeObject<StoryGraph>(File.ReadAllText(MainlineAssetPath));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder editor could not load graph: {ex.Message}");
            }

            graph ??= StoryRepository.CreateDefaultGraph();
            selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, graph.nodes.Count - 1));
        }

        private void SaveGraph()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MainlineAssetPath));
            File.WriteAllText(MainlineAssetPath, JsonConvert.SerializeObject(graph, Formatting.Indented));
            AssetDatabase.ImportAsset(MainlineAssetPath);
            AssetDatabase.Refresh();
        }

        private static ChoiceOption NewChoice(string direction)
        {
            return new ChoiceOption
            {
                id = $"{direction}_choice",
                label = direction == "left" ? "左侧选择" : "右侧选择",
                intent = "填写策划意图。",
                direction = direction,
                statHint = new PlayerStats(0, 0, 0, 0)
            };
        }

        private static NodeCacheDatabase LoadCache()
        {
            try
            {
                if (File.Exists(NodeCacheService.CacheFilePath))
                {
                    return JsonConvert.DeserializeObject<NodeCacheDatabase>(File.ReadAllText(NodeCacheService.CacheFilePath)) ?? new NodeCacheDatabase();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder editor could not load cache: {ex.Message}");
            }

            return new NodeCacheDatabase();
        }
    }
}
#endif

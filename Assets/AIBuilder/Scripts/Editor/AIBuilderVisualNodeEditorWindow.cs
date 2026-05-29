#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AIBuilder.EditorTools
{
    public sealed class AIBuilderVisualNodeEditorWindow : EditorWindow
    {
        private const string MainlineAssetPath = "Assets/AIBuilder/Resources/mainline_nodes.json";
        private const float LeftWidth = 280f;
        private const float RightWidth = 380f;
        private const float MainlineXSpacing = 230f;
        private const float ExpandedMainlineXSpacing = 330f;
        private const float MainlineYSpacing = 62f;
        private const float BranchYSpacing = 88f;
        private const float ExpandedBranchYSpacing = 170f;

        private StoryGraph graph;
        private NodeCacheDatabase cache;
        private StoryNode selectedNode;
        private NodeCacheEntry selectedBranch;
        private StoryGraphView graphView;
        private VisualElement listPanel;
        private VisualElement detailPanel;
        private Label summaryLabel;
        private ToolbarButton graphDetailToggleButton;
        private ToolbarSearchField searchField;
        private PopupField<string> branchFilter;
        private readonly Dictionary<string, Rect> graphNodePositions = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> expandedGraphDetailKeys = new HashSet<string>();
        private string searchText = "";

        [MenuItem("AI Builder/Visual Node Editor", false, 15)]
        public static void Open()
        {
            var window = GetWindow<AIBuilderVisualNodeEditorWindow>("AI Builder Visual Nodes");
            window.minSize = new Vector2(1280f, 780f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadData();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.backgroundColor = new Color(0.055f, 0.055f, 0.058f);

            BuildToolbar();
            BuildLayout();
            RefreshAll();
        }

        private void BuildToolbar()
        {
            var toolbar = new Toolbar();
            toolbar.style.height = 32f;
            toolbar.style.backgroundColor = new Color(0.08f, 0.08f, 0.085f);

            toolbar.Add(new ToolbarButton(() =>
            {
                LoadData();
                RefreshAll();
            })
            {
                text = "重新加载"
            });

            toolbar.Add(new ToolbarButton(() =>
            {
                SaveMainline();
                RefreshAll();
            })
            {
                text = "保存主干"
            });

            toolbar.Add(new ToolbarButton(() =>
            {
                SaveCache();
                RefreshAll();
            })
            {
                text = "保存审核"
            });

            toolbar.Add(new ToolbarButton(() =>
            {
                graphView?.FrameAll();
            })
            {
                text = "适配视图"
            });

            toolbar.Add(new ToolbarButton(() =>
            {
                graphView?.ArrangeLayered();
                CaptureGraphLayout();
            })
            {
                text = "整理布局"
            });

            toolbar.Add(new ToolbarButton(ResetGraphLayout)
            {
                text = "重置布局"
            });

            graphDetailToggleButton = new ToolbarButton(ToggleAllGraphDetails)
            {
                text = "展开全部详情"
            };
            toolbar.Add(graphDetailToggleButton);

            summaryLabel = new Label();
            summaryLabel.style.flexGrow = 1f;
            summaryLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            summaryLabel.style.marginLeft = 12f;
            summaryLabel.style.color = new Color(0.74f, 0.74f, 0.68f);
            toolbar.Add(summaryLabel);
            rootVisualElement.Add(toolbar);
        }

        private void BuildLayout()
        {
            var body = new VisualElement();
            body.style.flexGrow = 1f;
            body.style.flexDirection = FlexDirection.Row;
            rootVisualElement.Add(body);

            body.Add(BuildLeftPanel());

            graphView = new StoryGraphView();
            graphView.OnNodeSelected = node =>
            {
                selectedNode = node;
                selectedBranch = null;
                RefreshLists();
                RefreshDetail();
            };
            graphView.OnBranchSelected = branch =>
            {
                selectedBranch = branch;
                selectedNode = null;
                RefreshLists();
                RefreshDetail();
            };
            graphView.OnDetailToggle = ToggleGraphDetail;
            graphView.OnNodeMoved = StoreGraphNodePosition;
            graphView.style.flexGrow = 1f;
            body.Add(graphView);

            body.Add(BuildRightPanel());
        }

        private VisualElement BuildLeftPanel()
        {
            var panel = PanelShell(LeftWidth);
            panel.style.borderRightWidth = 1f;
            panel.style.borderRightColor = new Color(0.18f, 0.18f, 0.19f);

            searchField = new ToolbarSearchField();
            searchField.style.marginBottom = 6f;
            searchField.RegisterValueChangedCallback(evt =>
            {
                searchText = evt.newValue ?? "";
                RefreshLists();
                RefreshGraph();
            });
            panel.Add(searchField);

            branchFilter = new PopupField<string>(
                new List<string> { "全部分支", NodeCacheStatuses.PendingReview, NodeCacheStatuses.Approved, NodeCacheStatuses.Rejected },
                0);
            branchFilter.style.marginBottom = 8f;
            branchFilter.RegisterValueChangedCallback(_ =>
            {
                RefreshLists();
                RefreshGraph();
            });
            panel.Add(branchFilter);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            listPanel = new VisualElement();
            scroll.Add(listPanel);
            panel.Add(scroll);
            return panel;
        }

        private VisualElement BuildRightPanel()
        {
            var panel = PanelShell(RightWidth);
            panel.style.borderLeftWidth = 1f;
            panel.style.borderLeftColor = new Color(0.18f, 0.18f, 0.19f);

            var title = new Label("属性与审核");
            title.style.fontSize = 16f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new Color(0.92f, 0.89f, 0.78f);
            title.style.marginBottom = 8f;
            panel.Add(title);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            detailPanel = new VisualElement();
            scroll.Add(detailPanel);
            panel.Add(scroll);
            return panel;
        }

        private static VisualElement PanelShell(float width)
        {
            var panel = new VisualElement();
            panel.style.width = width;
            panel.style.minWidth = width;
            panel.style.paddingLeft = 10f;
            panel.style.paddingRight = 10f;
            panel.style.paddingTop = 10f;
            panel.style.paddingBottom = 10f;
            panel.style.backgroundColor = new Color(0.075f, 0.075f, 0.08f);
            return panel;
        }

        private void RefreshAll()
        {
            RefreshSummary();
            RefreshLists();
            RefreshGraph();
            RefreshDetail();
        }

        private void RefreshSummary()
        {
            var nodes = graph?.nodes?.Count ?? 0;
            var branches = cache?.entries?.Count ?? 0;
            var pending = cache?.entries?.Count(entry => string.Equals(NodeCacheStatuses.Normalize(entry.status), NodeCacheStatuses.PendingReview, StringComparison.OrdinalIgnoreCase)) ?? 0;
            var approved = cache?.entries?.Count(entry => string.Equals(NodeCacheStatuses.Normalize(entry.status), NodeCacheStatuses.Approved, StringComparison.OrdinalIgnoreCase)) ?? 0;
            var rejected = cache?.entries?.Count(entry => NodeCacheStatuses.IsRejected(entry.status)) ?? 0;
            summaryLabel.text = $"主干 {nodes}    分支 {branches}    待审 {pending}    通过 {approved}    驳回 {rejected}";
            if (graphDetailToggleButton != null)
            {
                graphDetailToggleButton.text = AreAllVisibleGraphDetailsExpanded() ? "收起全部详情" : "展开全部详情";
            }
        }

        private void RefreshLists()
        {
            listPanel.Clear();
            listPanel.Add(SectionTitle("主干节点"));
            foreach (var node in FilteredMainlineNodes())
            {
                var button = SidebarButton($"{node.mainlineIndex:000}  {node.title}", selectedNode == node, new Color(0.42f, 0.34f, 0.18f));
                button.clicked += () =>
                {
                    selectedNode = node;
                    selectedBranch = null;
                    graphView?.PingMainline(node.id);
                    RefreshAll();
                };
                listPanel.Add(button);
            }

            listPanel.Add(SectionTitle("玩家分支"));
            foreach (var branch in FilteredBranches())
            {
                var title = branch.resultNode?.title ?? "(missing branch)";
                var button = SidebarButton($"{StatusLabel(branch.status)}  {branch.sourceNodeId}/{branch.choiceId}\n{title}", selectedBranch == branch, StatusColor(branch.status));
                button.style.height = 46f;
                button.clicked += () =>
                {
                    selectedBranch = branch;
                    selectedNode = null;
                    graphView?.PingBranch(branch.cacheKey);
                    RefreshAll();
                };
                listPanel.Add(button);
            }
        }

        private void RefreshGraph()
        {
            CaptureGraphLayout();
            graphView?.SetData(
                FilteredMainlineNodes().ToList(),
                FilteredBranches().ToList(),
                selectedNode?.id,
                selectedBranch?.cacheKey,
                expandedGraphDetailKeys,
                graphNodePositions);
        }

        private void ToggleGraphDetail(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!expandedGraphDetailKeys.Add(key))
            {
                expandedGraphDetailKeys.Remove(key);
            }

            RefreshGraph();
            RefreshSummary();
        }

        private void ToggleAllGraphDetails()
        {
            var keys = VisibleGraphDetailKeys().ToList();
            if (keys.Count == 0)
            {
                return;
            }

            if (keys.All(key => expandedGraphDetailKeys.Contains(key)))
            {
                foreach (var key in keys)
                {
                    expandedGraphDetailKeys.Remove(key);
                }
            }
            else
            {
                foreach (var key in keys)
                {
                    expandedGraphDetailKeys.Add(key);
                }
            }

            RefreshGraph();
            RefreshSummary();
        }

        private bool AreAllVisibleGraphDetailsExpanded()
        {
            var keys = VisibleGraphDetailKeys().ToList();
            return keys.Count > 0 && keys.All(key => expandedGraphDetailKeys.Contains(key));
        }

        private IEnumerable<string> VisibleGraphDetailKeys()
        {
            foreach (var node in FilteredMainlineNodes())
            {
                yield return GraphDetailKey(true, node.id);
            }

            foreach (var branch in FilteredBranches())
            {
                yield return GraphDetailKey(false, branch.cacheKey);
            }
        }

        private void RefreshDetail()
        {
            detailPanel.Clear();
            if (selectedBranch != null)
            {
                DrawBranchDetail(selectedBranch);
                return;
            }

            selectedNode ??= graph?.nodes?.OrderBy(node => node.mainlineIndex).FirstOrDefault();
            if (selectedNode != null)
            {
                DrawMainlineDetail(selectedNode);
                return;
            }

            detailPanel.Add(new HelpBox("当前没有节点。", HelpBoxMessageType.Info));
        }

        private void DrawMainlineDetail(StoryNode node)
        {
            detailPanel.Add(SectionTitle("主干节点"));
            AddTextField("Id", node.id, value => node.id = value);
            AddTextField("Chapter", node.chapterId, value => node.chapterId = value);
            AddTextField("Title", node.title, value => node.title = value);
            AddIntegerField("Mainline Index", node.mainlineIndex, value => node.mainlineIndex = value);
            AddEnumField("Kind", node.nodeKind, value => node.nodeKind = (StoryNodeKind)value);
            AddTextField("Image Ref", node.imageRef, value => node.imageRef = value);
            AddTextArea("Body", node.body, value => node.body = value);

            DrawChoice("左侧选择", node.leftChoice ??= NewChoice("left"));
            DrawChoice("右侧选择", node.rightChoice ??= NewChoice("right"));

            var row = ButtonRow();
            row.Add(ActionButton("保存主干", () =>
            {
                SaveMainline();
                RefreshAll();
            }));
            row.Add(ActionButton("新增节点", AddMainlineNode));
            detailPanel.Add(row);
        }

        private void DrawChoice(string title, ChoiceOption choice)
        {
            detailPanel.Add(SectionTitle(title));
            AddTextField("Id", choice.id, value => choice.id = value);
            AddTextField("Label", choice.label, value => choice.label = value);
            AddTextField("Intent", choice.intent, value => choice.intent = value);
            AddTextField("Direction", choice.direction, value => choice.direction = value);
            AddTextField("Next Mainline", choice.nextMainlineNodeId, value => choice.nextMainlineNodeId = value);
            choice.statHint ??= new PlayerStats(0, 0, 0, 0);
            AddStats(choice.statHint);
        }

        private void DrawBranchDetail(NodeCacheEntry branch)
        {
            detailPanel.Add(SectionTitle("分支审核"));
            var status = new PopupField<string>("Status", NodeCacheStatuses.All.ToList(), StatusIndex(branch.status));
            status.RegisterValueChangedCallback(evt => branch.status = evt.newValue);
            detailPanel.Add(status);

            AddReadonly("Cache Key", branch.cacheKey);
            AddReadonly("Source / Choice", $"{branch.sourceNodeId} / {branch.choiceId}");
            AddReadonly("Created", branch.createdAt ?? "");
            AddReadonly("Image Status", string.IsNullOrWhiteSpace(branch.imageStatus) ? "(none)" : branch.imageStatus);
            AddReadonly("Image Key", string.IsNullOrWhiteSpace(branch.imageCacheKey) ? "(none)" : branch.imageCacheKey);
            if (!string.IsNullOrWhiteSpace(branch.imageError))
            {
                AddReadonly("Image Error", branch.imageError);
            }
            AddReadonly("Panorama Status", string.IsNullOrWhiteSpace(branch.panoramaStatus) ? "(none)" : branch.panoramaStatus);
            AddReadonly("Panorama Key", string.IsNullOrWhiteSpace(branch.panoramaCacheKey) ? "(none)" : branch.panoramaCacheKey);
            if (!string.IsNullOrWhiteSpace(branch.panoramaError))
            {
                AddReadonly("Panorama Error", branch.panoramaError);
            }

            branch.statDelta ??= new PlayerStats(0, 0, 0, 0);
            detailPanel.Add(SectionTitle("数值变化"));
            AddStats(branch.statDelta);

            if (branch.resultNode != null)
            {
                detailPanel.Add(SectionTitle("分支内容"));
                AddTextField("Title", branch.resultNode.title, value => branch.resultNode.title = value);
                AddTextArea("Body", branch.resultNode.body, value => branch.resultNode.body = value);
                if (branch.resultNode.leftChoice != null)
                {
                    AddTextField("Left Choice", branch.resultNode.leftChoice.label, value => branch.resultNode.leftChoice.label = value);
                }
                if (branch.resultNode.rightChoice != null)
                {
                    AddTextField("Right Choice", branch.resultNode.rightChoice.label, value => branch.resultNode.rightChoice.label = value);
                }
            }

            var row = ButtonRow();
            row.Add(ActionButton("待审", () => SetBranchStatus(branch, NodeCacheStatuses.PendingReview)));
            row.Add(ActionButton("通过", () => SetBranchStatus(branch, NodeCacheStatuses.Approved)));
            row.Add(ActionButton("驳回", () => SetBranchStatus(branch, NodeCacheStatuses.Rejected)));
            row.Add(ActionButton("保存", () =>
            {
                SaveCache();
                RefreshAll();
            }));
            detailPanel.Add(row);
        }

        private IEnumerable<StoryNode> FilteredMainlineNodes()
        {
            return (graph?.nodes ?? new List<StoryNode>())
                .Where(node => node != null)
                .Where(node => string.IsNullOrWhiteSpace(searchText)
                               || Contains(node.id, searchText)
                               || Contains(node.chapterId, searchText)
                               || Contains(node.title, searchText)
                               || Contains(node.body, searchText))
                .OrderBy(node => node.mainlineIndex)
                .ThenBy(node => node.id);
        }

        private IEnumerable<NodeCacheEntry> FilteredBranches()
        {
            var filter = branchFilter?.value ?? "全部分支";
            return (cache?.entries ?? new List<NodeCacheEntry>())
                .Where(entry => entry != null)
                .Where(entry => filter == "全部分支" || string.Equals(NodeCacheStatuses.Normalize(entry.status), filter, StringComparison.OrdinalIgnoreCase))
                .Where(entry => string.IsNullOrWhiteSpace(searchText)
                               || Contains(entry.cacheKey, searchText)
                               || Contains(entry.sourceNodeId, searchText)
                               || Contains(entry.choiceId, searchText)
                               || Contains(entry.resultNode?.title, searchText)
                               || Contains(entry.resultNode?.body, searchText))
                .OrderByDescending(entry => StatusWeight(entry.status))
                .ThenByDescending(entry => entry.createdAt);
        }

        private void AddMainlineNode()
        {
            graph.nodes ??= new List<StoryNode>();
            var nextIndex = graph.nodes.Count == 0 ? 1 : graph.nodes.Max(node => node.mainlineIndex) + 1;
            var node = new StoryNode
            {
                id = $"main_{nextIndex:000}",
                chapterId = string.IsNullOrWhiteSpace(graph.chapterId) ? $"ch{Mathf.CeilToInt(nextIndex / 3f):000}" : graph.chapterId,
                title = "新主干节点",
                body = "填写剧情正文。",
                imageRef = "branch",
                nodeKind = StoryNodeKind.Mainline,
                mainlineIndex = nextIndex,
                leftChoice = NewChoice("left"),
                rightChoice = NewChoice("right")
            };
            graph.nodes.Add(node);
            selectedNode = node;
            selectedBranch = null;
            RefreshAll();
        }

        private void LoadData()
        {
            graph = LoadGraph();
            cache = LoadCache();
            cache.entries ??= new List<NodeCacheEntry>();
            LoadGraphLayout();
            selectedNode = graph.nodes.OrderBy(node => node.mainlineIndex).FirstOrDefault();
            selectedBranch = null;
        }

        private static StoryGraph LoadGraph()
        {
            try
            {
                if (File.Exists(MainlineAssetPath))
                {
                    var loaded = JsonConvert.DeserializeObject<StoryGraph>(File.ReadAllText(MainlineAssetPath));
                    if (loaded?.nodes != null)
                    {
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder visual node editor could not load mainline: {ex.Message}");
            }

            return StoryRepository.CreateDefaultGraph();
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
                Debug.LogWarning($"AI Builder visual node editor could not load cache: {ex.Message}");
            }

            return new NodeCacheDatabase();
        }

        private void SaveMainline()
        {
            CaptureGraphLayout();
            Directory.CreateDirectory(Path.GetDirectoryName(MainlineAssetPath));
            File.WriteAllText(MainlineAssetPath, JsonConvert.SerializeObject(graph, Formatting.Indented));
            AssetDatabase.ImportAsset(MainlineAssetPath);
            Debug.Log($"AI Builder visual node editor saved mainline: {MainlineAssetPath}");
        }

        private void SaveCache()
        {
            CaptureGraphLayout();
            Directory.CreateDirectory(Path.GetDirectoryName(NodeCacheService.CacheFilePath));
            File.WriteAllText(NodeCacheService.CacheFilePath, JsonConvert.SerializeObject(cache, Formatting.Indented));
            Debug.Log($"AI Builder visual node editor saved branch review: {NodeCacheService.CacheFilePath}");
        }

        private void SetBranchStatus(NodeCacheEntry branch, string status)
        {
            CaptureGraphLayout();
            branch.status = status;
            SaveCache();
            RefreshAll();
        }

        private void StoreGraphNodePosition(string key, Rect rect)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            graphNodePositions[key] = rect;
            SaveGraphLayout();
        }

        private void CaptureGraphLayout()
        {
            if (graphView == null)
            {
                return;
            }

            if (graphView.CaptureLayout(graphNodePositions))
            {
                SaveGraphLayout();
            }
        }

        private void ResetGraphLayout()
        {
            graphNodePositions.Clear();
            EditorPrefs.DeleteKey(CurrentGraphLayoutKey());
            graphView?.ArrangeLayered();
            CaptureGraphLayout();
        }

        private void LoadGraphLayout()
        {
            graphNodePositions.Clear();
            var json = EditorPrefs.GetString(CurrentGraphLayoutKey(), "");
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                var database = JsonConvert.DeserializeObject<GraphNodeLayoutDatabase>(json);
                foreach (var record in database?.records ?? new List<GraphNodeLayoutRecord>())
                {
                    if (!string.IsNullOrWhiteSpace(record.key))
                    {
                        graphNodePositions[record.key] = record.ToRect();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder visual node layout ignored: {ex.Message}");
            }
        }

        private void SaveGraphLayout()
        {
            var database = new GraphNodeLayoutDatabase
            {
                records = graphNodePositions
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                    .Select(pair => GraphNodeLayoutRecord.From(pair.Key, pair.Value))
                    .ToList()
            };
            EditorPrefs.SetString(CurrentGraphLayoutKey(), JsonConvert.SerializeObject(database, Formatting.None));
        }

        private string CurrentGraphLayoutKey()
        {
            var projectKey = StableHash(Application.dataPath);
            var storyKey = StableHash(FirstNonEmpty(graph?.chapterId, graph?.chapterTitle, MainlineAssetPath));
            return $"AIBuilder.VisualNodeEditor.Layout.{projectKey}.{storyKey}";
        }

        private static string StableHash(string value)
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

        private void AddTextField(string label, string value, Action<string> setter)
        {
            var field = new TextField(label) { value = value ?? "" };
            field.RegisterValueChangedCallback(evt => setter(evt.newValue));
            field.style.marginBottom = 4f;
            detailPanel.Add(field);
        }

        private void AddTextArea(string label, string value, Action<string> setter)
        {
            var field = new TextField(label) { value = value ?? "", multiline = true };
            field.RegisterValueChangedCallback(evt => setter(evt.newValue));
            field.style.height = 118f;
            field.style.marginBottom = 6f;
            detailPanel.Add(field);
        }

        private void AddIntegerField(string label, int value, Action<int> setter)
        {
            var field = new IntegerField(label) { value = value };
            field.RegisterValueChangedCallback(evt => setter(evt.newValue));
            field.style.marginBottom = 4f;
            detailPanel.Add(field);
        }

        private void AddEnumField(string label, Enum value, Action<Enum> setter)
        {
            var field = new EnumField(label, value);
            field.RegisterValueChangedCallback(evt => setter(evt.newValue));
            field.style.marginBottom = 4f;
            detailPanel.Add(field);
        }

        private void AddReadonly(string label, string value)
        {
            var field = new TextField(label) { value = value ?? "", isReadOnly = true };
            field.style.marginBottom = 4f;
            detailPanel.Add(field);
        }

        private void AddStats(PlayerStats stats)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 6f;
            row.Add(CompactInt("Life", stats.life, value => stats.life = value));
            row.Add(CompactInt("Force", stats.force, value => stats.force = value));
            row.Add(CompactInt("Wealth", stats.wealth, value => stats.wealth = value));
            row.Add(CompactInt("Faith", stats.faith, value => stats.faith = value));
            detailPanel.Add(row);
        }

        private static IntegerField CompactInt(string label, int value, Action<int> setter)
        {
            var field = new IntegerField(label) { value = value };
            field.style.flexGrow = 1f;
            field.style.marginRight = 4f;
            field.RegisterValueChangedCallback(evt => setter(evt.newValue));
            return field;
        }

        private static Label SectionTitle(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 13f;
            label.style.color = new Color(0.88f, 0.82f, 0.64f);
            label.style.marginTop = 8f;
            label.style.marginBottom = 5f;
            return label;
        }

        private static Button SidebarButton(string text, bool selected, Color color)
        {
            var button = new Button { text = text };
            button.style.height = 30f;
            button.style.marginBottom = 4f;
            button.style.whiteSpace = WhiteSpace.Normal;
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.backgroundColor = selected ? new Color(0.78f, 0.62f, 0.28f) : new Color(color.r * 0.46f, color.g * 0.46f, color.b * 0.46f);
            button.style.color = selected ? Color.black : new Color(0.9f, 0.88f, 0.78f);
            button.style.borderLeftWidth = 4f;
            button.style.borderLeftColor = color;
            return button;
        }

        private static VisualElement ButtonRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 8f;
            row.style.marginBottom = 8f;
            return row;
        }

        private static Button ActionButton(string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.style.flexGrow = 1f;
            button.style.marginRight = 4f;
            return button;
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

        private static bool Contains(string source, string query)
        {
            return !string.IsNullOrWhiteSpace(source)
                   && !string.IsNullOrWhiteSpace(query)
                   && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int StatusIndex(string status)
        {
            var index = Array.IndexOf(NodeCacheStatuses.All, NodeCacheStatuses.Normalize(status));
            return Mathf.Max(0, index);
        }

        private static int StatusWeight(string status)
        {
            var normalized = NodeCacheStatuses.Normalize(status);
            if (string.Equals(normalized, NodeCacheStatuses.PendingReview, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            return string.Equals(normalized, NodeCacheStatuses.Approved, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        private static string StatusLabel(string status)
        {
            var normalized = NodeCacheStatuses.Normalize(status);
            if (string.Equals(normalized, NodeCacheStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                return "通过";
            }

            return string.Equals(normalized, NodeCacheStatuses.Rejected, StringComparison.OrdinalIgnoreCase) ? "驳回" : "待审";
        }

        private static Color StatusColor(string status)
        {
            var normalized = NodeCacheStatuses.Normalize(status);
            if (string.Equals(normalized, NodeCacheStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.28f, 0.72f, 0.48f);
            }

            return string.Equals(normalized, NodeCacheStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
                ? new Color(0.78f, 0.24f, 0.22f)
                : new Color(0.92f, 0.67f, 0.22f);
        }

        private static string GraphDetailKey(bool isMainline, string id)
        {
            return $"{(isMainline ? "mainline" : "branch")}:{id ?? ""}";
        }

        private sealed class GraphNodeLayoutDatabase
        {
            public List<GraphNodeLayoutRecord> records = new List<GraphNodeLayoutRecord>();
        }

        private sealed class GraphNodeLayoutRecord
        {
            public string key;
            public float x;
            public float y;
            public float width;
            public float height;

            public static GraphNodeLayoutRecord From(string key, Rect rect)
            {
                return new GraphNodeLayoutRecord
                {
                    key = key,
                    x = rect.x,
                    y = rect.y,
                    width = rect.width,
                    height = rect.height
                };
            }

            public Rect ToRect()
            {
                return new Rect(x, y, Mathf.Max(40f, width), Mathf.Max(32f, height));
            }
        }

        private sealed class StoryGraphView : GraphView
        {
            private readonly Dictionary<string, StoryGraphNode> mainlineNodes = new Dictionary<string, StoryGraphNode>();
            private readonly Dictionary<string, StoryGraphNode> branchNodes = new Dictionary<string, StoryGraphNode>();
            private readonly Dictionary<string, StoryNode> sourceMainline = new Dictionary<string, StoryNode>();
            private readonly Dictionary<string, NodeCacheEntry> sourceBranches = new Dictionary<string, NodeCacheEntry>();
            private readonly Dictionary<string, Rect> storedNodePositions = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> expandedDetailKeys = new HashSet<string>();
            private string selectedMainlineId = "";
            private string selectedCacheKey = "";
            private bool applyingLayout;

            public Action<StoryNode> OnNodeSelected;
            public Action<NodeCacheEntry> OnBranchSelected;
            public Action<string> OnDetailToggle;
            public Action<string, Rect> OnNodeMoved;

            public StoryGraphView()
            {
                style.flexGrow = 1f;
                style.backgroundColor = new Color(0.035f, 0.035f, 0.038f);
                Insert(0, new GridBackground());
                SetupZoom(ContentZoomer.DefaultMinScale, 1.35f);
                this.AddManipulator(new ContentDragger());
                this.AddManipulator(new SelectionDragger());
                this.AddManipulator(new RectangleSelector());
                graphViewChanged = HandleGraphViewChanged;
            }

            public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
            {
                return ports.ToList();
            }

            private GraphViewChange HandleGraphViewChanged(GraphViewChange change)
            {
                if (!applyingLayout && change.movedElements != null)
                {
                    foreach (var node in change.movedElements.OfType<StoryGraphNode>())
                    {
                        OnNodeMoved?.Invoke(node.LayoutKey, node.GetPosition());
                    }
                }

                return change;
            }

            public bool CaptureLayout(Dictionary<string, Rect> target)
            {
                if (target == null)
                {
                    return false;
                }

                var changed = false;
                foreach (var node in mainlineNodes.Values.Concat(branchNodes.Values))
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.LayoutKey))
                    {
                        continue;
                    }

                    var rect = node.GetPosition();
                    if (!target.TryGetValue(node.LayoutKey, out var previous) || !SameRect(previous, rect))
                    {
                        target[node.LayoutKey] = rect;
                        changed = true;
                    }
                }

                return changed;
            }

            public void SetData(
                List<StoryNode> nodes,
                List<NodeCacheEntry> branches,
                string mainlineId,
                string cacheKey,
                IEnumerable<string> expandedKeys,
                IReadOnlyDictionary<string, Rect> savedPositions)
            {
                selectedMainlineId = mainlineId ?? "";
                selectedCacheKey = cacheKey ?? "";
                storedNodePositions.Clear();
                if (savedPositions != null)
                {
                    foreach (var pair in savedPositions)
                    {
                        storedNodePositions[pair.Key] = pair.Value;
                    }
                }

                expandedDetailKeys.Clear();
                if (expandedKeys != null)
                {
                    foreach (var key in expandedKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
                    {
                        expandedDetailKeys.Add(key);
                    }
                }

                Build(nodes ?? new List<StoryNode>(), branches ?? new List<NodeCacheEntry>());
            }

            public void PingMainline(string id)
            {
                if (mainlineNodes.TryGetValue(id ?? "", out var node))
                {
                    ClearSelection();
                    AddToSelection(node);
                    FrameSelection();
                }
            }

            public void PingBranch(string key)
            {
                if (branchNodes.TryGetValue(key ?? "", out var node))
                {
                    ClearSelection();
                    AddToSelection(node);
                    FrameSelection();
                }
            }

            public void ArrangeLayered(bool frameAfter = true)
            {
                ApplyLayeredLayout(false);
                if (frameAfter)
                {
                    schedule.Execute(_ => FrameAll()).ExecuteLater(60);
                }
            }

            private void Build(List<StoryNode> nodes, List<NodeCacheEntry> branches)
            {
                DeleteElements(graphElements.ToList());
                mainlineNodes.Clear();
                branchNodes.Clear();
                sourceMainline.Clear();
                sourceBranches.Clear();

                var ordered = nodes.OrderBy(node => node.mainlineIndex).ThenBy(node => node.id).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var storyNode = ordered[i];
                    var detailKey = GraphDetailKey(true, storyNode.id);
                    var graphNode = new StoryGraphNode(storyNode, null, true, selectedMainlineId == storyNode.id, expandedDetailKeys.Contains(detailKey), detailKey, OnDetailToggle);
                    graphNode.RegisterCallback<MouseDownEvent>(_ => OnNodeSelected?.Invoke(storyNode));
                    AddElement(graphNode);
                    mainlineNodes[storyNode.id] = graphNode;
                    sourceMainline[storyNode.id] = storyNode;
                }

                for (var i = 0; i < ordered.Count - 1; i++)
                {
                    Connect(mainlineNodes[ordered[i].id].Output, mainlineNodes[ordered[i + 1].id].Input, new Color(0.78f, 0.68f, 0.45f), "Default next");
                }

                foreach (var branch in branches)
                {
                    var sourceId = branch.sourceNodeId ?? "";
                    var detailKey = GraphDetailKey(false, branch.cacheKey);
                    var graphNode = new StoryGraphNode(null, branch, false, selectedCacheKey == branch.cacheKey, expandedDetailKeys.Contains(detailKey), detailKey, OnDetailToggle);
                    graphNode.RegisterCallback<MouseDownEvent>(_ => OnBranchSelected?.Invoke(branch));
                    AddElement(graphNode);
                    branchNodes[branch.cacheKey] = graphNode;
                    sourceBranches[branch.cacheKey] = branch;

                    if (mainlineNodes.TryGetValue(sourceId, out var sourceGraphNode))
                    {
                        Connect(sourceGraphNode.Output, graphNode.Input, StatusColor(branch.status), branch.choiceId ?? "branch");
                    }

                    var targetId = FirstNonEmpty(
                        branch.resultNode?.leftChoice?.nextMainlineNodeId,
                        branch.resultNode?.rightChoice?.nextMainlineNodeId);
                    if (!string.IsNullOrWhiteSpace(targetId) && mainlineNodes.TryGetValue(targetId, out var targetGraphNode))
                    {
                        Connect(graphNode.Output, targetGraphNode.Input, new Color(0.52f, 0.68f, 0.86f), "return");
                    }
                }

                ApplyLayeredLayout(true);
            }

            private void ApplyLayeredLayout(bool preserveSavedPositions)
            {
                var ordered = sourceMainline.Values
                    .Where(node => node != null)
                    .OrderBy(node => node.mainlineIndex)
                    .ThenBy(node => node.id)
                    .ToList();
                var indexById = ordered
                    .Select((node, index) => new { node.id, index })
                    .Where(item => !string.IsNullOrWhiteSpace(item.id))
                    .GroupBy(item => item.id)
                    .ToDictionary(group => group.Key, group => group.First().index);

                var hasExpandedDetails = expandedDetailKeys.Count > 0;
                var mainlineXSpacing = hasExpandedDetails ? ExpandedMainlineXSpacing : 250f;
                var branchLaneSpacing = hasExpandedDetails ? ExpandedBranchYSpacing : 104f;
                var originX = 160f;
                var mainlineY = hasExpandedDetails ? 540f : 420f;

                applyingLayout = true;
                try
                {
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var storyNode = ordered[i];
                        if (!mainlineNodes.TryGetValue(storyNode.id ?? "", out var graphNode))
                        {
                            continue;
                        }

                        var key = GraphDetailKey(true, storyNode.id);
                        var detailsExpanded = expandedDetailKeys.Contains(key);
                        var width = detailsExpanded ? 240f : 150f;
                        var height = detailsExpanded ? 132f : 54f;
                        var fallback = new Rect(originX + i * mainlineXSpacing - width * 0.5f, mainlineY, width, height);
                        SetNodePosition(graphNode, key, fallback, preserveSavedPositions);
                    }

                    foreach (var group in sourceBranches.Values
                                 .Where(branch => branch != null)
                                 .GroupBy(branch => branch.sourceNodeId ?? "")
                                 .OrderBy(group => SourceIndex(indexById, group.Key)))
                    {
                        var sourceIndex = SourceIndex(indexById, group.Key);
                        var sourceX = originX + sourceIndex * mainlineXSpacing;
                        var localBranches = group
                            .OrderByDescending(branch => StatusWeight(branch.status))
                            .ThenByDescending(branch => branch.createdAt)
                            .ThenBy(branch => branch.cacheKey)
                            .ToList();
                        var aboveLane = 0;
                        var belowLane = 0;

                        for (var localIndex = 0; localIndex < localBranches.Count; localIndex++)
                        {
                            var branch = localBranches[localIndex];
                            if (!branchNodes.TryGetValue(branch.cacheKey ?? "", out var graphNode))
                            {
                                continue;
                            }

                            var key = GraphDetailKey(false, branch.cacheKey);
                            var detailsExpanded = expandedDetailKeys.Contains(key);
                            var width = detailsExpanded ? 240f : 154f;
                            var height = detailsExpanded ? 118f : 54f;
                            var placeAbove = localIndex % 2 == 0;
                            var lane = placeAbove ? aboveLane++ : belowLane++;
                            var laneOffset = lane % 2 == 0 ? 0f : (placeAbove ? -18f : 18f);
                            var x = sourceX - width * 0.5f + laneOffset;
                            var y = placeAbove
                                ? mainlineY - 112f - height - lane * branchLaneSpacing
                                : mainlineY + 116f + lane * branchLaneSpacing;
                            var fallback = new Rect(x, y, width, height);
                            SetNodePosition(graphNode, key, fallback, preserveSavedPositions);
                        }
                    }
                }
                finally
                {
                    applyingLayout = false;
                }
            }

            private void SetNodePosition(StoryGraphNode node, string key, Rect fallback, bool preserveSavedPosition)
            {
                if (node == null)
                {
                    return;
                }

                var rect = fallback;
                if (preserveSavedPosition
                    && !string.IsNullOrWhiteSpace(key)
                    && storedNodePositions.TryGetValue(key, out var saved))
                {
                    rect = new Rect(saved.x, saved.y, fallback.width, fallback.height);
                }

                node.SetPosition(rect);
            }

            private static bool SameRect(Rect left, Rect right)
            {
                return Mathf.Abs(left.x - right.x) < 0.1f
                       && Mathf.Abs(left.y - right.y) < 0.1f
                       && Mathf.Abs(left.width - right.width) < 0.1f
                       && Mathf.Abs(left.height - right.height) < 0.1f;
            }

            private static int SourceIndex(Dictionary<string, int> indexById, string nodeId)
            {
                return indexById.TryGetValue(nodeId ?? "", out var index) ? index : 0;
            }

            private void Connect(Port output, Port input, Color color, string label)
            {
                if (output == null || input == null)
                {
                    return;
                }

                var edge = new Edge
                {
                    output = output,
                    input = input,
                    edgeControl =
                    {
                        inputColor = color,
                        outputColor = color
                    }
                };
                edge.AddToClassList("ai-builder-edge");
                output.Connect(edge);
                input.Connect(edge);
                AddElement(edge);

                if (!string.IsNullOrWhiteSpace(label))
                {
                    edge.tooltip = label;
                }
            }
        }

        private sealed class StoryGraphNode : Node
        {
            public Port Input { get; }
            public Port Output { get; }
            public string LayoutKey { get; }

            public StoryGraphNode(
                StoryNode mainline,
                NodeCacheEntry branch,
                bool isMainline,
                bool selected,
                bool detailsExpanded,
                string detailKey,
                Action<string> onDetailToggle)
            {
                var rawTitle = isMainline ? mainline?.title ?? "Mainline" : branch?.resultNode?.title ?? "Branch";
                LayoutKey = detailKey ?? "";
                title = CompactTitle(rawTitle, 18);
                tooltip = rawTitle;
                viewDataKey = LayoutKey;
                capabilities |= Capabilities.Movable | Capabilities.Selectable;
                capabilities &= ~Capabilities.Deletable;

                Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
                Input.portName = "";
                inputContainer.Add(Input);

                Output = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
                Output.portName = "";
                outputContainer.Add(Output);

                var compactRow = new VisualElement();
                compactRow.style.flexDirection = FlexDirection.Row;
                compactRow.style.alignItems = Align.Center;
                compactRow.style.marginTop = 1f;
                compactRow.style.marginBottom = detailsExpanded ? 5f : 0f;
                mainContainer.Insert(1, compactRow);

                var badge = new Label(isMainline ? "主干" : StatusLabel(branch?.status));
                badge.style.alignSelf = Align.FlexStart;
                badge.style.paddingLeft = 7f;
                badge.style.paddingRight = 7f;
                badge.style.paddingTop = 2f;
                badge.style.paddingBottom = 2f;
                badge.style.marginRight = 6f;
                badge.style.borderTopLeftRadius = 8f;
                badge.style.borderTopRightRadius = 8f;
                badge.style.borderBottomLeftRadius = 8f;
                badge.style.borderBottomRightRadius = 8f;
                badge.style.backgroundColor = isMainline ? new Color(0.28f, 0.24f, 0.16f) : StatusColor(branch?.status);
                badge.style.color = isMainline ? new Color(0.9f, 0.84f, 0.66f) : Color.black;
                compactRow.Add(badge);

                var toggleButton = new Button(() => onDetailToggle?.Invoke(detailKey))
                {
                    text = detailsExpanded ? "收起" : "详情"
                };
                toggleButton.style.height = 20f;
                toggleButton.style.minWidth = 44f;
                toggleButton.style.paddingLeft = 4f;
                toggleButton.style.paddingRight = 4f;
                toggleButton.style.fontSize = 10f;
                toggleButton.tooltip = detailsExpanded ? "收起这个节点的详情" : "展开这个节点的正文、来源和图片状态";
                compactRow.Add(toggleButton);

                if (detailsExpanded)
                {
                    var meta = new Label(isMainline
                        ? $"{mainline?.chapterId}   #{mainline?.mainlineIndex}"
                        : $"{branch?.sourceNodeId} / {branch?.choiceId}");
                    meta.style.fontSize = 11f;
                    meta.style.color = new Color(0.68f, 0.68f, 0.64f);
                    mainContainer.Add(meta);

                    var body = new Label(isMainline ? mainline?.body : branch?.resultNode?.body);
                    body.style.whiteSpace = WhiteSpace.Normal;
                    body.style.maxHeight = 48f;
                    body.style.fontSize = 12f;
                    body.style.color = new Color(0.84f, 0.82f, 0.74f);
                    body.style.marginTop = 5f;
                    mainContainer.Add(body);

                    var imageState = branch == null
                        ? ""
                        : string.IsNullOrWhiteSpace(branch.imagePath)
                            ? $"image: {branch.imageStatus ?? "none"}"
                            : "image: ready";
                    var panoramaState = branch == null
                        ? ""
                        : string.IsNullOrWhiteSpace(branch.panoramaPath)
                            ? $"panorama: {branch.panoramaStatus ?? "none"}"
                            : "panorama: ready";
                    if (!string.IsNullOrWhiteSpace(imageState))
                    {
                        var imageLabel = new Label(imageState);
                        imageLabel.style.fontSize = 10f;
                        imageLabel.style.color = new Color(0.58f, 0.68f, 0.8f);
                        imageLabel.style.marginTop = 4f;
                        mainContainer.Add(imageLabel);
                    }

                    if (!string.IsNullOrWhiteSpace(panoramaState))
                    {
                        var panoramaLabel = new Label(panoramaState);
                        panoramaLabel.style.fontSize = 10f;
                        panoramaLabel.style.color = new Color(0.52f, 0.76f, 0.66f);
                        panoramaLabel.style.marginTop = 2f;
                        mainContainer.Add(panoramaLabel);
                    }
                }

                ApplyNodeStyle(isMainline, selected, branch?.status);
                RefreshExpandedState();
                RefreshPorts();
            }

            private void ApplyNodeStyle(bool isMainline, bool selected, string status)
            {
                var accent = isMainline ? new Color(0.78f, 0.62f, 0.28f) : StatusColor(status);
                mainContainer.style.backgroundColor = selected ? new Color(0.18f, 0.17f, 0.14f) : new Color(0.105f, 0.105f, 0.105f);
                mainContainer.style.borderTopWidth = 1f;
                mainContainer.style.borderBottomWidth = 1f;
                mainContainer.style.borderLeftWidth = 1f;
                mainContainer.style.borderRightWidth = 1f;
                mainContainer.style.borderTopColor = selected ? new Color(1f, 0.86f, 0.46f) : new Color(0.28f, 0.28f, 0.27f);
                mainContainer.style.borderBottomColor = selected ? new Color(1f, 0.86f, 0.46f) : new Color(0.28f, 0.28f, 0.27f);
                mainContainer.style.borderLeftColor = accent;
                mainContainer.style.borderRightColor = selected ? new Color(1f, 0.86f, 0.46f) : new Color(0.28f, 0.28f, 0.27f);
                mainContainer.style.borderTopLeftRadius = 8f;
                mainContainer.style.borderTopRightRadius = 8f;
                mainContainer.style.borderBottomLeftRadius = 8f;
                mainContainer.style.borderBottomRightRadius = 8f;
            }

            private static string CompactTitle(string value, int maxLength)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
                {
                    return value ?? "";
                }

                return value.Substring(0, maxLength - 1) + "...";
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return "";
        }
    }
}
#endif

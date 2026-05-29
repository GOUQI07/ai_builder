#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public static class AIBuilderDemoBuildMenu
    {
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        private const string BuildRoot = "Builds";
        private const string ProductName = "AI Builder Demo";

        [MenuItem("AI Builder/Build Demo/Windows", false, 200)]
        public static void BuildWindowsDemo()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                scenes = new[] { SampleScenePath };
            }

            foreach (var scene in scenes)
            {
                if (!File.Exists(scene))
                {
                    throw new FileNotFoundException($"Build scene not found: {scene}");
                }
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputDirectory = Path.Combine(BuildRoot, $"AIBuilderDemo_Windows_{timestamp}");
            var outputPath = Path.Combine(outputDirectory, "AIBuilderDemo.exe");
            Directory.CreateDirectory(outputDirectory);

            var previousProductName = PlayerSettings.productName;
            try
            {
                PlayerSettings.productName = ProductName;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                });

                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException($"AI Builder demo build failed: {summary.result}, errors={summary.totalErrors}, warnings={summary.totalWarnings}");
                }

                WriteBuildReadme(outputDirectory, scenes, summary);
                WriteAiConfigNotice(outputDirectory);
                WriteLatestPointer(outputDirectory, outputPath);
                Debug.Log($"AI Builder demo build succeeded: {Path.GetFullPath(outputPath)} ({summary.totalSize / (1024f * 1024f):0.0} MB)");
            }
            finally
            {
                PlayerSettings.productName = previousProductName;
            }
        }

        private static void WriteBuildReadme(string outputDirectory, string[] scenes, BuildSummary summary)
        {
            var readmePath = Path.Combine(outputDirectory, "README_AI_BUILDER_DEMO.txt");
            var persistentConfigHint = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                PlayerSettings.companyName,
                ProductName,
                "AIBuilder",
                "ai_provider.json");

            var builder = new StringBuilder();
            builder.AppendLine("AI Builder Demo");
            builder.AppendLine();
            builder.AppendLine("运行方式:");
            builder.AppendLine("1. 双击 AIBuilderDemo.exe。");
            builder.AppendLine("2. 没有真实 AI 配置时，Demo 会使用 mock/本地兜底逻辑，仍可运行。");
            builder.AppendLine("3. 需要真实文本/图片联调时，把 ai_provider.json 放到下面路径，或使用系统环境变量/.env 提供 key:");
            builder.AppendLine($"   {persistentConfigHint}");
            builder.AppendLine();
            builder.AppendLine("构建信息:");
            builder.AppendLine($"- Unity: {Application.unityVersion}");
            builder.AppendLine($"- Target: Windows x64");
            builder.AppendLine($"- Size: {summary.totalSize / (1024f * 1024f):0.0} MB");
            builder.AppendLine("- Scenes:");
            foreach (var scene in scenes)
            {
                builder.AppendLine($"  - {scene}");
            }

            File.WriteAllText(readmePath, builder.ToString(), new UTF8Encoding(false));
        }

        private static void WriteAiConfigNotice(string outputDirectory)
        {
            var noticePath = Path.Combine(outputDirectory, "重要_AI配置说明.txt");
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                PlayerSettings.companyName,
                ProductName,
                "AIBuilder",
                "ai_provider.json");

            var builder = new StringBuilder();
            builder.AppendLine("重要：AI 生成功能需要本机配置");
            builder.AppendLine();
            builder.AppendLine("这个 Demo 包不会内置任何真实 API Key。");
            builder.AppendLine("如果没有配置，游戏仍能运行，但会走 mock / 本地兜底逻辑，看起来就像“没有 AI 生成”。");
            builder.AppendLine();
            builder.AppendLine("配置方法 A：推荐");
            builder.AppendLine("1. 第一次运行 AIBuilderDemo.exe，让 Unity 创建 LocalLow 数据目录。");
            builder.AppendLine("2. 创建或复制配置文件到：");
            builder.AppendLine($"   {configPath}");
            builder.AppendLine("3. 在系统环境变量中设置文本和图片 API Key，例如 OPENAI_API_KEY / IMAGE_API_KEY。");
            builder.AppendLine();
            builder.AppendLine("ai_provider.json 示例，注意不要把真实 key 写进这个文件：");
            builder.AppendLine("{");
            builder.AppendLine("  \"providerType\": \"openai_compatible\",");
            builder.AppendLine("  \"baseUrl\": \"https://api.asxs.top/v1\",");
            builder.AppendLine("  \"wireApi\": \"responses\",");
            builder.AppendLine("  \"textModel\": \"填写你的快速文本模型\",");
            builder.AppendLine("  \"imageModel\": \"gpt-image-2-official\",");
            builder.AppendLine("  \"imageEndpointUrl\": \"填写你的图片 generations 完整 POST URL\",");
            builder.AppendLine("  \"apiKeyEnvName\": \"OPENAI_API_KEY\",");
            builder.AppendLine("  \"imageApiKeyEnvName\": \"IMAGE_API_KEY\",");
            builder.AppendLine("  \"imageSize\": \"1:1\",");
            builder.AppendLine("  \"imageResolution\": \"1k\",");
            builder.AppendLine("  \"imageQuality\": \"low\",");
            builder.AppendLine("  \"imageOutputFormat\": \"png\",");
            builder.AppendLine("  \"imageCount\": 1,");
            builder.AppendLine("  \"imagePollIntervalSeconds\": 2,");
            builder.AppendLine("  \"disableResponseStorage\": true,");
            builder.AppendLine("  \"timeoutSeconds\": 25,");
            builder.AppendLine("  \"imageTimeoutSeconds\": 240,");
            builder.AppendLine("  \"enableRuntimeImages\": true,");
            builder.AppendLine("  \"imageGenerationRatio\": 0.3,");
            builder.AppendLine("  \"guaranteeFirstGeneratedImage\": true");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("配置方法 B：把 .env 放在 AIBuilderDemo.exe 同级目录");
            builder.AppendLine("OPENAI_API_KEY=你的文本模型Key");
            builder.AppendLine("IMAGE_API_KEY=你的图片模型Key");
            builder.AppendLine("AI_BUILDER_TEXT_MODEL=你的快速文本模型");
            builder.AppendLine("AI_BUILDER_IMAGE_MODEL=gpt-image-2-official");
            builder.AppendLine("AI_BUILDER_IMAGE_ENDPOINT_URL=你的图片 generations 完整 POST URL");
            builder.AppendLine();
            builder.AppendLine("判断是否配置成功：");
            builder.AppendLine("进入游戏后看左下角状态栏，应该显示 T:real 和 I:real。");
            builder.AppendLine("如果显示 T:mock 或 I:mock/off，就说明对应的模型名、Endpoint 或 Key 没有被读取到。");

            File.WriteAllText(noticePath, builder.ToString(), new UTF8Encoding(false));
        }

        private static void WriteLatestPointer(string outputDirectory, string outputPath)
        {
            Directory.CreateDirectory(BuildRoot);
            var pointerPath = Path.Combine(BuildRoot, "AIBuilderDemo_Windows_Latest.txt");
            File.WriteAllText(
                pointerPath,
                $"{Path.GetFullPath(outputPath)}{Environment.NewLine}{Path.GetFullPath(outputDirectory)}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
    }
}
#endif

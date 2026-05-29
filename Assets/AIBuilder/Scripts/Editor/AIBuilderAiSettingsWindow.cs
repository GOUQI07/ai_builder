#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AIBuilder.EditorTools
{
    public sealed class AIBuilderAiSettingsWindow : EditorWindow
    {
        private AiProviderSettings settings;
        private Vector2 scroll;

        [MenuItem("AI Builder/AI Settings")]
        public static void Open()
        {
            GetWindow<AIBuilderAiSettingsWindow>("AI Settings");
        }

        private void OnEnable()
        {
            Reload();
        }

        private void OnGUI()
        {
            if (settings == null)
            {
                Reload();
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Provider", EditorStyles.boldLabel);
            DrawProviderFields();
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Runtime Image Policy", EditorStyles.boldLabel);
            DrawImagePolicyFields();
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Remote Node Cache", EditorStyles.boldLabel);
            DrawRemoteCacheFields();
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Local Config", EditorStyles.boldLabel);
            DrawConfigActions();
            EditorGUILayout.EndScrollView();
        }

        private void DrawProviderFields()
        {
            var providerIndex = Mathf.Max(0, Array.IndexOf(AiProviderTypes.All, settings.NormalizedProviderType));
            var nextProviderIndex = EditorGUILayout.Popup("Provider Type", providerIndex, AiProviderTypes.All);
            settings.providerType = AiProviderTypes.All[nextProviderIndex];

            settings.baseUrl = EditorGUILayout.TextField("Base URL", settings.baseUrl);
            settings.wireApi = EditorGUILayout.TextField("Wire API", settings.wireApi);
            settings.textModel = EditorGUILayout.TextField("Text Model", settings.textModel);
            settings.imageModel = EditorGUILayout.TextField("Image Model", settings.imageModel);
            settings.imageBaseUrl = EditorGUILayout.TextField("Image Base URL", settings.imageBaseUrl);
            settings.imageEndpoint = EditorGUILayout.TextField("Image Endpoint", settings.imageEndpoint);
            settings.imageEndpointUrl = EditorGUILayout.TextField("Image Endpoint URL", settings.imageEndpointUrl);
            EditorGUI.BeginChangeCheck();
            settings.apiKeyEnvName = EditorGUILayout.TextField("API Key Env Name", settings.apiKeyEnvName);
            settings.imageApiKeyEnvName = EditorGUILayout.TextField("Image API Key Env Name", settings.imageApiKeyEnvName);
            if (EditorGUI.EndChangeCheck())
            {
                settings.RefreshApiKeyStatus();
            }

            settings.reasoningEffort = EditorGUILayout.TextField("Reasoning Effort", settings.reasoningEffort);
            settings.timeoutSeconds = EditorGUILayout.IntSlider("Timeout Seconds", settings.timeoutSeconds, 5, 120);
            settings.imageTimeoutSeconds = EditorGUILayout.IntSlider("Image Timeout Seconds", settings.imageTimeoutSeconds, 8, 240);
            settings.imageSize = EditorGUILayout.TextField("Image Size", settings.imageSize);
            settings.imageResolution = EditorGUILayout.TextField("Image Resolution", settings.imageResolution);
            settings.imageQuality = EditorGUILayout.TextField("Image Quality", settings.imageQuality);
            settings.imageOutputFormat = EditorGUILayout.TextField("Image Output Format", settings.imageOutputFormat);
            settings.panoramaImageSize = EditorGUILayout.TextField("Panorama Image Size", settings.panoramaImageSize);
            settings.imageCount = EditorGUILayout.IntSlider("Image Count", settings.imageCount, 1, 4);
            settings.imagePollIntervalSeconds = EditorGUILayout.IntSlider("Image Poll Seconds", settings.imagePollIntervalSeconds, 2, 10);
            settings.disableResponseStorage = EditorGUILayout.Toggle("Disable Response Storage", settings.disableResponseStorage);

            var keyStatus = settings.ApiKeyPresent ? "Found" : "Missing";
            EditorGUILayout.LabelField("API Key", $"{keyStatus} ({settings.apiKeyEnvName})");
            var imageKeyName = string.IsNullOrWhiteSpace(settings.imageApiKeyEnvName) ? settings.apiKeyEnvName : settings.imageApiKeyEnvName;
            var imageKeyStatus = settings.ImageApiKeyPresent ? "Found" : "Missing";
            EditorGUILayout.LabelField("Image API Key", $"{imageKeyStatus} ({imageKeyName})");
        }

        private void DrawImagePolicyFields()
        {
            settings.enableRuntimeImages = EditorGUILayout.Toggle("Enable Runtime Images", settings.enableRuntimeImages);
            settings.imageGenerationRatio = EditorGUILayout.Slider("Image Generation Ratio", settings.imageGenerationRatio, 0f, 1f);
            settings.guaranteeFirstGeneratedImage = EditorGUILayout.Toggle("Guarantee First Image", settings.guaranteeFirstGeneratedImage);
            settings.enableRuntimePanoramas = EditorGUILayout.Toggle("Enable Runtime Panoramas", settings.enableRuntimePanoramas);
            settings.panoramaGenerationRatio = EditorGUILayout.Slider("Panorama Generation Ratio", settings.panoramaGenerationRatio, 0f, 1f);
            settings.guaranteeFirstGeneratedPanorama = EditorGUILayout.Toggle("Guarantee First Panorama", settings.guaranteeFirstGeneratedPanorama);
        }

        private void DrawRemoteCacheFields()
        {
            settings.enableRemoteNodeCache = EditorGUILayout.Toggle("Enable Remote Cache", settings.enableRemoteNodeCache);
            settings.remoteCacheBaseUrl = EditorGUILayout.TextField("Cache Base URL", settings.remoteCacheBaseUrl);
            EditorGUI.BeginChangeCheck();
            settings.remoteCacheApiKeyEnvName = EditorGUILayout.TextField("Cache API Key Env", settings.remoteCacheApiKeyEnvName);
            if (EditorGUI.EndChangeCheck())
            {
                settings.RefreshApiKeyStatus();
            }

            settings.remoteCacheTimeoutSeconds = EditorGUILayout.IntSlider("Cache Timeout Seconds", settings.remoteCacheTimeoutSeconds, 2, 60);
            var cacheStatus = settings.CanUseRemoteNodeCache ? "Configured" : "Local Only";
            var keyStatus = string.IsNullOrWhiteSpace(settings.remoteCacheApiKeyEnvName)
                ? "No key env"
                : settings.RemoteCacheApiKeyPresent ? "Key found" : "Key missing";
            EditorGUILayout.LabelField("Remote Cache", $"{cacheStatus} / {keyStatus}");
        }

        private void DrawConfigActions()
        {
            EditorGUILayout.SelectableLabel(AiProviderSettings.LocalConfigPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload"))
                {
                    Reload();
                }

                if (GUILayout.Button("Save Local Config"))
                {
                    settings.SaveLocalConfig();
                    Debug.Log($"AI Builder provider config saved: {AiProviderSettings.LocalConfigPath}");
                }

                if (GUILayout.Button("Copy Path"))
                {
                    EditorGUIUtility.systemCopyBuffer = AiProviderSettings.LocalConfigPath;
                }
            }
        }

        private void Reload()
        {
            settings = AiProviderSettings.Load();
        }
    }
}
#endif

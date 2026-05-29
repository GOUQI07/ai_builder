using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace AIBuilder
{
    public interface INodeCacheStore
    {
        IReadOnlyList<NodeCacheEntry> Entries { get; }
        IReadOnlyList<NodeCacheEntry> EntriesForStory(string storyId);
        IReadOnlyList<NodeCacheEntry> EntriesForImageKey(string storyId, string imageCacheKey);
        IReadOnlyList<NodeCacheEntry> EntriesForPanoramaKey(string storyId, string panoramaCacheKey);
        bool TryFindGeneratedImagePath(string storyId, string imageCacheKey, out string imagePath);
        bool TryFindGeneratedPanoramaPath(string storyId, string panoramaCacheKey, out string panoramaPath);
        bool TryGet(string key, out NodeCacheEntry entry);
        void Put(NodeCacheEntry entry);
        void Clear();
        bool SetStatus(string cacheKey, string status);
        string SaveImage(string cacheKey, byte[] bytes);
        void ApplyGeneratedImageToImageKey(string storyId, string imageCacheKey, string imagePath, string imageStatus);
        void ApplyGeneratedPanoramaToPanoramaKey(string storyId, string panoramaCacheKey, string panoramaPath, string panoramaStatus);
    }

    public static class NodeCacheStoreFactory
    {
        public static INodeCacheStore Create(AiProviderSettings settings)
        {
            if (settings != null && settings.CanUseRemoteNodeCache)
            {
                return new RemoteNodeCacheStore(
                    settings.remoteCacheBaseUrl,
                    settings.RemoteCacheApiKey,
                    settings.remoteCacheTimeoutSeconds,
                    new LocalNodeCacheStore());
            }

            return new LocalNodeCacheStore();
        }
    }

    public static class RemoteNodeCacheApi
    {
        public const string BranchCachePath = "branch-cache";
        public const string AssetPath = "assets";

        public static string BranchCacheEndpoint(string baseUrl, string cacheKey)
        {
            return $"{NormalizeBaseUrl(baseUrl)}/{BranchCachePath}/{Uri.EscapeDataString(cacheKey ?? "")}";
        }

        public static string AssetUploadEndpoint(string baseUrl)
        {
            return $"{NormalizeBaseUrl(baseUrl)}/{AssetPath}";
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? "").Trim().TrimEnd('/');
        }
    }

    [Serializable]
    public sealed class RemoteNodeCacheEntryRequest
    {
        public string cacheKey;
        public NodeCacheEntry entry;
    }

    [Serializable]
    public sealed class RemoteNodeCacheEntryResponse
    {
        public bool hit;
        public string cacheKey;
        public NodeCacheEntry entry;
        public string status;
        public string error;
    }

    [Serializable]
    public sealed class RemoteAssetUploadRequest
    {
        public string assetKey;
        public string contentType = "image/png";
        public string bytesBase64;
    }

    [Serializable]
    public sealed class RemoteAssetUploadResponse
    {
        public string assetKey;
        public string assetUrl;
        public string error;
    }

    public sealed class LocalNodeCacheStore : INodeCacheStore
    {
        private readonly string cacheDirectory;
        private readonly string cacheFilePath;
        private NodeCacheDatabase database = new NodeCacheDatabase();

        public LocalNodeCacheStore()
            : this(NodeCacheService.CacheDirectory, NodeCacheService.CacheFilePath)
        {
        }

        public LocalNodeCacheStore(string cacheDirectory, string cacheFilePath)
        {
            this.cacheDirectory = cacheDirectory;
            this.cacheFilePath = cacheFilePath;
            Load();
        }

        public IReadOnlyList<NodeCacheEntry> Entries => database.entries;

        public IReadOnlyList<NodeCacheEntry> EntriesForStory(string storyId)
        {
            var normalizedStoryId = NormalizeCachePart(storyId, "story");
            var prefix = normalizedStoryId + "|";
            return database.entries
                .Where(entry => entry != null
                                && (string.Equals(NormalizeCachePart(entry.storyId, ""), normalizedStoryId, StringComparison.OrdinalIgnoreCase)
                                    || (!string.IsNullOrWhiteSpace(entry.cacheKey) && entry.cacheKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForImageKey(string storyId, string imageCacheKey)
        {
            if (string.IsNullOrWhiteSpace(imageCacheKey))
            {
                return Array.Empty<NodeCacheEntry>();
            }

            var normalizedStoryId = NormalizeCachePart(storyId, "story");
            return EntriesForStory(normalizedStoryId)
                .Where(entry => string.Equals(entry.imageCacheKey, imageCacheKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForPanoramaKey(string storyId, string panoramaCacheKey)
        {
            if (string.IsNullOrWhiteSpace(panoramaCacheKey))
            {
                return Array.Empty<NodeCacheEntry>();
            }

            var normalizedStoryId = NormalizeCachePart(storyId, "story");
            return EntriesForStory(normalizedStoryId)
                .Where(entry => string.Equals(entry.panoramaCacheKey, panoramaCacheKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public bool TryFindGeneratedImagePath(string storyId, string imageCacheKey, out string imagePath)
        {
            imagePath = "";
            foreach (var entry in EntriesForImageKey(storyId, imageCacheKey))
            {
                if (entry == null
                    || NodeCacheStatuses.IsRejected(entry.status)
                    || string.IsNullOrWhiteSpace(entry.imagePath)
                    || !File.Exists(entry.imagePath))
                {
                    continue;
                }

                var normalizedImageStatus = string.IsNullOrWhiteSpace(entry.imageStatus) ? NodeImageStatuses.Generated : entry.imageStatus;
                if (string.Equals(normalizedImageStatus, NodeImageStatuses.Generated, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedImageStatus, NodeImageStatuses.Reused, StringComparison.OrdinalIgnoreCase))
                {
                    imagePath = entry.imagePath;
                    return true;
                }
            }

            return false;
        }

        public bool TryFindGeneratedPanoramaPath(string storyId, string panoramaCacheKey, out string panoramaPath)
        {
            panoramaPath = "";
            foreach (var entry in EntriesForPanoramaKey(storyId, panoramaCacheKey))
            {
                if (entry == null
                    || NodeCacheStatuses.IsRejected(entry.status)
                    || string.IsNullOrWhiteSpace(entry.panoramaPath)
                    || !File.Exists(entry.panoramaPath))
                {
                    continue;
                }

                var normalizedImageStatus = string.IsNullOrWhiteSpace(entry.panoramaStatus) ? NodeImageStatuses.Generated : entry.panoramaStatus;
                if (string.Equals(normalizedImageStatus, NodeImageStatuses.Generated, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedImageStatus, NodeImageStatuses.Reused, StringComparison.OrdinalIgnoreCase))
                {
                    panoramaPath = entry.panoramaPath;
                    return true;
                }
            }

            return false;
        }

        public bool TryGet(string key, out NodeCacheEntry entry)
        {
            entry = database.entries.FirstOrDefault(item => item.cacheKey == key && !NodeCacheStatuses.IsRejected(item.status));
            return entry != null;
        }

        public void Put(NodeCacheEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.cacheKey))
            {
                return;
            }

            var index = database.entries.FindIndex(item => item.cacheKey == entry.cacheKey);
            if (index >= 0)
            {
                database.entries[index] = entry;
            }
            else
            {
                database.entries.Add(entry);
            }

            Save();
        }

        public void Clear()
        {
            database.entries.Clear();
            Save();
        }

        public bool SetStatus(string cacheKey, string status)
        {
            var entry = database.entries.FirstOrDefault(item => item.cacheKey == cacheKey);
            if (entry == null)
            {
                return false;
            }

            entry.status = NodeCacheStatuses.Normalize(status);
            Save();
            return true;
        }

        public string SaveImage(string cacheKey, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return "";
            }

            try
            {
                Directory.CreateDirectory(cacheDirectory);
                var safeName = Convert.ToBase64String(Encoding.UTF8.GetBytes(cacheKey))
                    .Replace("/", "_")
                    .Replace("+", "-")
                    .TrimEnd('=');
                var path = Path.Combine(cacheDirectory, $"{safeName}.png");
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder generated image could not be saved: {ex.Message}");
                return "";
            }
        }

        public void ApplyGeneratedImageToImageKey(string storyId, string imageCacheKey, string imagePath, string imageStatus)
        {
            if (string.IsNullOrWhiteSpace(imageCacheKey) || string.IsNullOrWhiteSpace(imagePath))
            {
                return;
            }

            var changed = false;
            foreach (var entry in EntriesForImageKey(storyId, imageCacheKey))
            {
                if (entry == null || NodeCacheStatuses.IsRejected(entry.status))
                {
                    continue;
                }

                entry.imagePath = imagePath;
                entry.imageStatus = string.IsNullOrWhiteSpace(imageStatus) ? NodeImageStatuses.Generated : imageStatus;
                entry.imageError = "";
                if (entry.resultNode != null)
                {
                    entry.resultNode.imageRef = imagePath;
                }

                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }

        public void ApplyGeneratedPanoramaToPanoramaKey(string storyId, string panoramaCacheKey, string panoramaPath, string panoramaStatus)
        {
            if (string.IsNullOrWhiteSpace(panoramaCacheKey) || string.IsNullOrWhiteSpace(panoramaPath))
            {
                return;
            }

            var changed = false;
            foreach (var entry in EntriesForPanoramaKey(storyId, panoramaCacheKey))
            {
                if (entry == null || NodeCacheStatuses.IsRejected(entry.status))
                {
                    continue;
                }

                entry.panoramaPath = panoramaPath;
                entry.panoramaStatus = string.IsNullOrWhiteSpace(panoramaStatus) ? NodeImageStatuses.Generated : panoramaStatus;
                entry.panoramaError = "";
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(cacheFilePath))
                {
                    var json = File.ReadAllText(cacheFilePath);
                    database = JsonConvert.DeserializeObject<NodeCacheDatabase>(json) ?? new NodeCacheDatabase();
                    database.entries ??= new List<NodeCacheEntry>();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder cache ignored: {ex.Message}");
                database = new NodeCacheDatabase();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                var tempPath = cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(database, Formatting.Indented));
                File.Copy(tempPath, cacheFilePath, true);
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder cache could not be saved: {ex.Message}");
            }
        }

        private static string NormalizeCachePart(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback ?? "";
            }

            return value.Trim().ToLowerInvariant();
        }
    }

    public sealed class RemoteNodeCacheStore : INodeCacheStore
    {
        private readonly LocalNodeCacheStore localStore;
        private readonly RemoteNodeCacheClient client;

        public RemoteNodeCacheStore(
            string baseUrl,
            string apiKey,
            int timeoutSeconds,
            LocalNodeCacheStore localStore = null)
        {
            this.localStore = localStore ?? new LocalNodeCacheStore();
            client = new RemoteNodeCacheClient(baseUrl, apiKey, timeoutSeconds);
        }

        public IReadOnlyList<NodeCacheEntry> Entries => localStore.Entries;

        public IReadOnlyList<NodeCacheEntry> EntriesForStory(string storyId)
        {
            return localStore.EntriesForStory(storyId);
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForImageKey(string storyId, string imageCacheKey)
        {
            return localStore.EntriesForImageKey(storyId, imageCacheKey);
        }

        public IReadOnlyList<NodeCacheEntry> EntriesForPanoramaKey(string storyId, string panoramaCacheKey)
        {
            return localStore.EntriesForPanoramaKey(storyId, panoramaCacheKey);
        }

        public bool TryFindGeneratedImagePath(string storyId, string imageCacheKey, out string imagePath)
        {
            return localStore.TryFindGeneratedImagePath(storyId, imageCacheKey, out imagePath);
        }

        public bool TryFindGeneratedPanoramaPath(string storyId, string panoramaCacheKey, out string panoramaPath)
        {
            return localStore.TryFindGeneratedPanoramaPath(storyId, panoramaCacheKey, out panoramaPath);
        }

        public bool TryGet(string key, out NodeCacheEntry entry)
        {
            if (localStore.TryGet(key, out entry))
            {
                return true;
            }

            if (!client.TryGetEntry(key, out entry))
            {
                return false;
            }

            entry = CacheRemoteAssetsLocally(entry);
            if (entry != null)
            {
                localStore.Put(entry);
            }

            return entry != null;
        }

        public void Put(NodeCacheEntry entry)
        {
            UploadEntryAssets(entry);
            localStore.Put(entry);
            client.TryPutEntry(entry);
        }

        public void Clear()
        {
            localStore.Clear();
        }

        public bool SetStatus(string cacheKey, string status)
        {
            var changed = localStore.SetStatus(cacheKey, status);
            if (localStore.TryGet(cacheKey, out var entry))
            {
                client.TryPutEntry(entry);
            }

            return changed;
        }

        public string SaveImage(string cacheKey, byte[] bytes)
        {
            var localPath = localStore.SaveImage(cacheKey, bytes);
            client.TryUploadAsset(cacheKey, bytes, out _);
            return localPath;
        }

        public void ApplyGeneratedImageToImageKey(string storyId, string imageCacheKey, string imagePath, string imageStatus)
        {
            localStore.ApplyGeneratedImageToImageKey(storyId, imageCacheKey, imagePath, imageStatus);
            foreach (var entry in localStore.EntriesForImageKey(storyId, imageCacheKey))
            {
                UploadEntryAssets(entry);
                localStore.Put(entry);
                client.TryPutEntry(entry);
            }
        }

        public void ApplyGeneratedPanoramaToPanoramaKey(string storyId, string panoramaCacheKey, string panoramaPath, string panoramaStatus)
        {
            localStore.ApplyGeneratedPanoramaToPanoramaKey(storyId, panoramaCacheKey, panoramaPath, panoramaStatus);
            foreach (var entry in localStore.EntriesForPanoramaKey(storyId, panoramaCacheKey))
            {
                UploadEntryAssets(entry);
                localStore.Put(entry);
                client.TryPutEntry(entry);
            }
        }

        private NodeCacheEntry CacheRemoteAssetsLocally(NodeCacheEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(entry.imageUrl) && !File.Exists(entry.imagePath))
            {
                var assetKey = string.IsNullOrWhiteSpace(entry.imageCacheKey) ? entry.cacheKey + "|image" : entry.imageCacheKey;
                if (client.TryDownloadAsset(entry.imageUrl, out var imageBytes))
                {
                    var localPath = localStore.SaveImage(assetKey, imageBytes);
                    if (!string.IsNullOrWhiteSpace(localPath))
                    {
                        entry.imagePath = localPath;
                        if (entry.resultNode != null)
                        {
                            entry.resultNode.imageRef = localPath;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.panoramaUrl) && !File.Exists(entry.panoramaPath))
            {
                var assetKey = string.IsNullOrWhiteSpace(entry.panoramaCacheKey) ? entry.cacheKey + "|panorama" : entry.panoramaCacheKey;
                if (client.TryDownloadAsset(entry.panoramaUrl, out var panoramaBytes))
                {
                    var localPath = localStore.SaveImage(assetKey, panoramaBytes);
                    if (!string.IsNullOrWhiteSpace(localPath))
                    {
                        entry.panoramaPath = localPath;
                    }
                }
            }

            return entry;
        }

        private void UploadEntryAssets(NodeCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.imageUrl)
                && !string.IsNullOrWhiteSpace(entry.imagePath)
                && File.Exists(entry.imagePath))
            {
                var assetKey = string.IsNullOrWhiteSpace(entry.imageCacheKey) ? entry.cacheKey + "|image" : entry.imageCacheKey;
                if (TryReadAllBytes(entry.imagePath, out var imageBytes) && client.TryUploadAsset(assetKey, imageBytes, out var imageAssetUrl))
                {
                    entry.imageUrl = imageAssetUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(entry.panoramaUrl)
                && !string.IsNullOrWhiteSpace(entry.panoramaPath)
                && File.Exists(entry.panoramaPath))
            {
                var assetKey = string.IsNullOrWhiteSpace(entry.panoramaCacheKey) ? entry.cacheKey + "|panorama" : entry.panoramaCacheKey;
                if (TryReadAllBytes(entry.panoramaPath, out var panoramaBytes) && client.TryUploadAsset(assetKey, panoramaBytes, out var panoramaAssetUrl))
                {
                    entry.panoramaUrl = panoramaAssetUrl;
                }
            }
        }

        private static bool TryReadAllBytes(string path, out byte[] bytes)
        {
            bytes = null;
            try
            {
                bytes = File.ReadAllBytes(path);
                return bytes != null && bytes.Length > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder cache asset could not be read for upload: {ex.Message}");
                return false;
            }
        }
    }

    internal sealed class RemoteNodeCacheClient
    {
        private readonly string baseUrl;
        private readonly int timeoutSeconds;
        private readonly HttpClient httpClient;

        public RemoteNodeCacheClient(string baseUrl, string apiKey, int timeoutSeconds)
        {
            this.baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            this.timeoutSeconds = Mathf.Clamp(timeoutSeconds, 2, 60);
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(this.timeoutSeconds) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        public bool TryGetEntry(string cacheKey, out NodeCacheEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(cacheKey))
            {
                return false;
            }

            try
            {
                var response = SendJsonRequest<RemoteNodeCacheEntryResponse>(() =>
                    httpClient.GetAsync(RemoteNodeCacheApi.BranchCacheEndpoint(baseUrl, cacheKey)));
                if (response == null || !response.hit || response.entry == null || NodeCacheStatuses.IsRejected(response.entry.status))
                {
                    return false;
                }

                entry = response.entry;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder remote cache read skipped: {ex.Message}");
                return false;
            }
        }

        public bool TryPutEntry(NodeCacheEntry entry)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || entry == null || string.IsNullOrWhiteSpace(entry.cacheKey))
            {
                return false;
            }

            try
            {
                var request = new RemoteNodeCacheEntryRequest
                {
                    cacheKey = entry.cacheKey,
                    entry = entry
                };
                var json = JsonConvert.SerializeObject(request, Formatting.None);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = Await(httpClient.PutAsync(RemoteNodeCacheApi.BranchCacheEndpoint(baseUrl, entry.cacheKey), content));
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder remote cache write skipped: {ex.Message}");
                return false;
            }
        }

        public bool TryUploadAsset(string assetKey, byte[] bytes, out string assetUrl)
        {
            assetUrl = "";
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(assetKey) || bytes == null || bytes.Length == 0)
            {
                return false;
            }

            try
            {
                var request = new RemoteAssetUploadRequest
                {
                    assetKey = assetKey,
                    bytesBase64 = Convert.ToBase64String(bytes)
                };
                var json = JsonConvert.SerializeObject(request, Formatting.None);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = SendJsonRequest<RemoteAssetUploadResponse>(() =>
                    httpClient.PostAsync(RemoteNodeCacheApi.AssetUploadEndpoint(baseUrl), content));
                assetUrl = response?.assetUrl ?? "";
                return !string.IsNullOrWhiteSpace(assetUrl);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder remote asset upload skipped: {ex.Message}");
                return false;
            }
        }

        public bool TryDownloadAsset(string assetUrl, out byte[] bytes)
        {
            bytes = null;
            if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            try
            {
                bytes = Await(httpClient.GetByteArrayAsync(uri));
                return bytes != null && bytes.Length > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AI Builder remote asset download skipped: {ex.Message}");
                return false;
            }
        }

        private T SendJsonRequest<T>(Func<System.Threading.Tasks.Task<HttpResponseMessage>> requestFactory)
        {
            using var response = Await(requestFactory());
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            var json = Await(response.Content.ReadAsStringAsync());
            return string.IsNullOrWhiteSpace(json)
                ? default
                : JsonConvert.DeserializeObject<T>(json);
        }

        private T Await<T>(System.Threading.Tasks.Task<T> task)
        {
            var completed = System.Threading.Tasks.Task.WhenAny(task, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).GetAwaiter().GetResult();
            if (!ReferenceEquals(completed, task))
            {
                throw new TimeoutException($"Remote node cache request timed out after {timeoutSeconds}s.");
            }

            return task.GetAwaiter().GetResult();
        }
    }
}

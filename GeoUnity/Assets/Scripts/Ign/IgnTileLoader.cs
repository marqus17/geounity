using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoUnity.Ign
{
    public enum IgnMapLayer
    {
        PlanIgnV2,
        OrthoPhotos
    }

    /// <summary>
    /// Downloads and caches IGN Geoplateforme WMTS tiles (PM / Web Mercator) in memory and on disk.
    /// </summary>
    public class IgnTileLoader : MonoBehaviour
    {
        private const string WmtsBaseUrl = "https://data.geopf.fr/wmts";
        private const int MaxConcurrentRequests = 6;
        private const int MaxMemoryTiles = 256;

        private readonly Dictionary<string, Texture2D> _memoryCache = new Dictionary<string, Texture2D>();
        private readonly LinkedList<string> _lruOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new Dictionary<string, LinkedListNode<string>>();

        private readonly Queue<TileRequest> _pendingQueue = new Queue<TileRequest>();
        private readonly HashSet<string> _queuedOrActiveKeys = new HashSet<string>();
        private readonly List<ActiveRequest> _activeRequests = new List<ActiveRequest>();

        private string _diskCacheRoot;

        private struct TileRequest
        {
            public string Key;
            public IgnMapLayer Layer;
            public int Z;
            public int X;
            public int Y;
            public Action<string, Texture2D> OnLoaded;
        }

        private class ActiveRequest
        {
            public TileRequest Request;
            public UnityWebRequest WebRequest;
            public UnityWebRequestAsyncOperation Operation;
            public string DiskPath;
        }

        private void Awake()
        {
            _diskCacheRoot = Path.Combine(Application.persistentDataPath, "IgnTileCache");
        }

        private void OnDestroy()
        {
            CancelAllPending();
            foreach (var tex in _memoryCache.Values)
            {
                if (tex != null) Destroy(tex);
            }
            _memoryCache.Clear();
            _lruOrder.Clear();
            _lruNodes.Clear();
        }

        private void Update()
        {
            // Poll active requests
            for (int i = _activeRequests.Count - 1; i >= 0; i--)
            {
                var active = _activeRequests[i];
                if (!active.Operation.isDone) continue;

                _activeRequests.RemoveAt(i);
                _queuedOrActiveKeys.Remove(active.Request.Key);

                Texture2D texture = null;
                if (active.WebRequest.result == UnityWebRequest.Result.Success)
                {
                    byte[] data = active.WebRequest.downloadHandler.data;
                    if (data != null && data.Length > 0)
                    {
                        texture = CreateTileTexture(data);
                        if (texture != null)
                        {
                            PutInMemoryCache(active.Request.Key, texture);
                            SaveToDiskSafe(active.DiskPath, data);
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"[IgnTileLoader] Failed {active.Request.Key}: {active.WebRequest.error}");
                }

                active.WebRequest.Dispose();
                active.Request.OnLoaded?.Invoke(active.Request.Key, texture);
            }

            // Start new requests up to concurrency limit
            while (_activeRequests.Count < MaxConcurrentRequests && _pendingQueue.Count > 0)
            {
                var req = _pendingQueue.Dequeue();
                if (_memoryCache.TryGetValue(req.Key, out var cached) && cached != null)
                {
                    _queuedOrActiveKeys.Remove(req.Key);
                    TouchLru(req.Key);
                    req.OnLoaded?.Invoke(req.Key, cached);
                    continue;
                }

                string url = BuildWmtsTileUrl(req.Layer, req.Z, req.X, req.Y);
                string diskPath = GetDiskPath(req.Layer, req.Z, req.X, req.Y);

                var webReq = UnityWebRequest.Get(url);
                webReq.timeout = 15;
                var op = webReq.SendWebRequest();

                _activeRequests.Add(new ActiveRequest
                {
                    Request = req,
                    WebRequest = webReq,
                    Operation = op,
                    DiskPath = diskPath
                });
            }
        }

        public static string MakeKey(IgnMapLayer layer, int z, int x, int y)
        {
            return $"{(int)layer}_{z}_{x}_{y}";
        }

        public static string BuildWmtsTileUrl(IgnMapLayer layer, int z, int x, int y)
        {
            string layerName = layer == IgnMapLayer.PlanIgnV2
                ? "GEOGRAPHICALGRIDSYSTEMS.PLANIGNV2"
                : "ORTHOIMAGERY.ORTHOPHOTOS";
            string format = layer == IgnMapLayer.PlanIgnV2 ? "image/png" : "image/jpeg";

            return $"{WmtsBaseUrl}?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                   $"&LAYER={layerName}&STYLE=normal&TILEMATRIXSET=PM" +
                   $"&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&FORMAT={format}";
        }

        public bool TryGetCached(string key, out Texture2D texture)
        {
            if (_memoryCache.TryGetValue(key, out texture) && texture != null)
            {
                TouchLru(key);
                return true;
            }
            texture = null;
            return false;
        }

        public void RequestTile(IgnMapLayer layer, int z, int x, int y, Action<string, Texture2D> onLoaded)
        {
            string key = MakeKey(layer, z, x, y);

            if (TryGetCached(key, out var cached))
            {
                onLoaded?.Invoke(key, cached);
                return;
            }

            // Try disk cache synchronously (small 15-40 KB tile files)
            string diskPath = GetDiskPath(layer, z, x, y);
            if (File.Exists(diskPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(diskPath);
                    var tex = CreateTileTexture(bytes);
                    if (tex != null)
                    {
                        PutInMemoryCache(key, tex);
                        onLoaded?.Invoke(key, tex);
                        return;
                    }
                }
                catch
                {
                    // Fall through to network download
                }
            }

            if (_queuedOrActiveKeys.Contains(key)) return;

            _queuedOrActiveKeys.Add(key);
            _pendingQueue.Enqueue(new TileRequest
            {
                Key = key,
                Layer = layer,
                Z = z,
                X = x,
                Y = y,
                OnLoaded = onLoaded
            });
        }

        public void CancelAllPending()
        {
            _pendingQueue.Clear();
            foreach (var active in _activeRequests)
            {
                active.WebRequest.Abort();
                active.WebRequest.Dispose();
            }
            _activeRequests.Clear();
            _queuedOrActiveKeys.Clear();
        }

        private Texture2D CreateTileTexture(byte[] data)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            if (!tex.LoadImage(data, true))
            {
                Destroy(tex);
                return null;
            }
            return tex;
        }

        private void PutInMemoryCache(string key, Texture2D tex)
        {
            if (_memoryCache.ContainsKey(key))
            {
                TouchLru(key);
                return;
            }

            _memoryCache[key] = tex;
            var node = _lruOrder.AddFirst(key);
            _lruNodes[key] = node;

            while (_memoryCache.Count > MaxMemoryTiles && _lruOrder.Last != null)
            {
                string oldest = _lruOrder.Last.Value;
                _lruOrder.RemoveLast();
                _lruNodes.Remove(oldest);
                if (_memoryCache.TryGetValue(oldest, out var oldTex) && oldTex != null)
                {
                    Destroy(oldTex);
                }
                _memoryCache.Remove(oldest);
            }
        }

        private void TouchLru(string key)
        {
            if (_lruNodes.TryGetValue(key, out var node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
            }
        }

        private string GetDiskPath(IgnMapLayer layer, int z, int x, int y)
        {
            string ext = layer == IgnMapLayer.PlanIgnV2 ? "png" : "jpg";
            string layerDir = layer == IgnMapLayer.PlanIgnV2 ? "planignv2" : "ortho";
            return Path.Combine(_diskCacheRoot, layerDir, z.ToString(), $"{x}_{y}.{ext}");
        }

        private static void SaveToDiskSafe(string path, byte[] data)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, data);
            }
            catch
            {
                // Non-fatal cache write failure
            }
        }
    }
}

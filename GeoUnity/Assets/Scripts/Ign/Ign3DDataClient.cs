using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using GeoUnity.Geo;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoUnity.Ign
{
    public class ElevationGrid
    {
        public int Width;
        public int Height;
        public double MinLambertX;
        public double MaxLambertX;
        public double MinLambertY;
        public double MaxLambertY;
        public float[] Heights; // Row 0 = North (MaxLambertY), Row Height-1 = South (MinLambertY)
        public float MinElevation;
        public float MaxElevation;

        public float SampleHeight(double lambertX, double lambertY)
        {
            if (Heights == null || Width < 2 || Height < 2) return MinElevation;

            double u = (lambertX - MinLambertX) / (MaxLambertX - MinLambertX);
            double v = (MaxLambertY - lambertY) / (MaxLambertY - MinLambertY); // 0 = North, 1 = South

            u = Math.Clamp(u, 0.0, 1.0);
            v = Math.Clamp(v, 0.0, 1.0);

            double fx = u * (Width - 1);
            double fy = v * (Height - 1);

            int x0 = Math.Min((int)fx, Width - 2);
            int y0 = Math.Min((int)fy, Height - 2);
            float tx = (float)(fx - x0);
            float ty = (float)(fy - y0);

            float h00 = Heights[y0 * Width + x0];
            float h10 = Heights[y0 * Width + (x0 + 1)];
            float h01 = Heights[(y0 + 1) * Width + x0];
            float h11 = Heights[(y0 + 1) * Width + (x0 + 1)];

            float h0 = Mathf.Lerp(h00, h10, tx);
            float h1 = Mathf.Lerp(h01, h11, tx);
            return Mathf.Lerp(h0, h1, ty);
        }
    }

    public class BuildingData
    {
        public List<Vector2> OuterRingLocal; // Local Unity (X, Z) meters relative to sector center
        public float HeightMeters;
        public float RoofAltitude;
        public bool HasRoofAltitude;
        public string Nature;
        public string WallMaterial;
        public string RoofMaterial;
        public string Usage;
        public int HousingCount = -1;
    }

    public class BridgeData
    {
        public List<Vector2> CenterLineLocal; // Local Unity (X, Z) polyline relative to sector center
        public List<float> Altitudes;         // Optional IGN Z altitudes (NaN if absent)
        public float WidthMeters;
        public string Nature;
        public string DetailedNature;
        public string Name;
    }

    public enum LandmarkKind
    {
        Statue,
        Monument,
        Calvaire,
        Fountain,
        WaterTower,
        TowerOrPylon
    }

    public struct LandmarkData
    {
        public Vector2 LocalPos;
        public LandmarkKind Kind;
        public string Label;
    }

    public class VegetationZoneData
    {
        public List<Vector2> OuterRingLocal; // Local Unity (X, Z) meters relative to sector center
        public string Nature;
    }

    public class WaterSurfaceData
    {
        public List<Vector2> OuterRingLocal; // Local Unity (X, Z) meters relative to sector center
        public string Nature;
    }

    public class CropParcelData
    {
        public List<Vector2> OuterRingLocal;
        public string CropCode;
    }

    public enum RoadSignKind
    {
        TrafficSignal,
        Stop,
        GiveWay
    }

    public struct RoadSignData
    {
        public Vector2 LocalPos;
        public RoadSignKind Kind;
    }

    /// <summary>
    /// Downloads IGN Geoplateforme 3D data in Lambert-93 (EPSG:2154) for a GeoSector:
    /// - RGE ALTI elevation grid (WMS-R BIL 32-bit float)
    /// - Orthophoto texture (WMS-R JPEG)
    /// - BD TOPO V3 buildings (WFS 2.0.0 GeoJSON with pagination)
    /// </summary>
    public class Ign3DDataClient
    {
        private const string WmsRBaseUrl = "https://data.geopf.fr/wms-r";
        private const string WfsBaseUrl = "https://data.geopf.fr/wfs/ows";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly string _cacheRoot;

        public Ign3DDataClient()
        {
            _cacheRoot = Path.Combine(Application.persistentDataPath, "Ign3DCache");
        }

        private string GetSectorCacheDir(GeoSector sector)
        {
            string key = $"L93_{Math.Round(sector.CenterLambertX)}_{Math.Round(sector.CenterLambertY)}_{Math.Round(sector.SizeMeters)}";
            return Path.Combine(_cacheRoot, key);
        }

        public async Task<ElevationGrid> DownloadElevationGridAsync(GeoSector sector, int resolution = 257)
        {
            string cachePath = Path.Combine(GetSectorCacheDir(sector), $"dem_{resolution}.bil");
            byte[] raw = TryReadCache(cachePath);

            if (raw == null || raw.Length != resolution * resolution * 4)
            {
                string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
                string url = $"{WmsRBaseUrl}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
                             $"&LAYERS=ELEVATION.ELEVATIONGRIDCOVERAGE.HIGHRES&STYLES=&CRS=EPSG:2154" +
                             $"&BBOX={bbox}&WIDTH={resolution}&HEIGHT={resolution}&FORMAT=image/x-bil;bits=32";

                raw = await DownloadBytesAsync(url, 30);
                if (raw == null || raw.Length < resolution * resolution * 4)
                {
                    Debug.LogWarning($"[Ign3DDataClient] DEM download failed or incomplete ({raw?.Length ?? 0} bytes), falling back to flat ground.");
                    return CreateFlatGrid(sector, resolution, 0f);
                }
                WriteCacheSafe(cachePath, raw);
            }

            int count = resolution * resolution;
            float[] heights = new float[count];
            float minH = float.MaxValue;
            float maxH = float.MinValue;
            float sumValid = 0f;
            int validCount = 0;

            for (int i = 0; i < count; i++)
            {
                float h = BitConverter.ToSingle(raw, i * 4);
                if (float.IsNaN(h) || float.IsInfinity(h) || h < -500f || h > 5500f)
                {
                    heights[i] = float.NaN;
                }
                else
                {
                    heights[i] = h;
                    if (h < minH) minH = h;
                    if (h > maxH) maxH = h;
                    sumValid += h;
                    validCount++;
                }
            }

            float fallback = validCount > 0 ? sumValid / validCount : 0f;
            if (validCount == 0) { minH = 0f; maxH = 0f; }

            for (int i = 0; i < count; i++)
            {
                if (float.IsNaN(heights[i]))
                    heights[i] = fallback;
            }

            return new ElevationGrid
            {
                Width = resolution,
                Height = resolution,
                MinLambertX = sector.MinLambertX,
                MaxLambertX = sector.MaxLambertX,
                MinLambertY = sector.MinLambertY,
                MaxLambertY = sector.MaxLambertY,
                Heights = heights,
                MinElevation = minH,
                MaxElevation = maxH
            };
        }

        public async Task<Texture2D> DownloadOrthophotoAsync(GeoSector sector, int size = 2048)
        {
            string cachePath = Path.Combine(GetSectorCacheDir(sector), $"ortho_{size}.jpg");
            byte[] raw = TryReadCache(cachePath);

            if (raw == null || raw.Length == 0)
            {
                string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
                string url = $"{WmsRBaseUrl}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
                             $"&LAYERS=ORTHOIMAGERY.ORTHOPHOTOS&STYLES=&CRS=EPSG:2154" +
                             $"&BBOX={bbox}&WIDTH={size}&HEIGHT={size}&FORMAT=image/jpeg";

                raw = await DownloadBytesAsync(url, 30);
                if (raw == null || raw.Length == 0)
                {
                    Debug.LogWarning("[Ign3DDataClient] Orthophoto download failed.");
                    return null;
                }
                WriteCacheSafe(cachePath, raw);
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            if (!tex.LoadImage(raw, true))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return tex;
        }

        public async Task<List<BuildingData>> DownloadBuildingsAsync(GeoSector sector, int maxFeatures = 4000, Action<int> onBatchLoaded = null)
        {
            const int pageSize = 1000;
            var allBuildings = new List<BuildingData>(1024);
            string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
            string cacheDir = GetSectorCacheDir(sector);

            for (int startIndex = 0; startIndex < maxFeatures; startIndex += pageSize)
            {
                string cachePath = Path.Combine(cacheDir, $"buildings_v2_{startIndex}.json");
                string json = TryReadCacheText(cachePath);

                if (string.IsNullOrEmpty(json))
                {
                    string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                                 $"&TYPENAMES=BDTOPO_V3:batiment&SRSNAME=EPSG:2154" +
                                 $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                                 $"&PROPERTYNAME=geometrie,nature,hauteur,altitude_maximale_toit,materiaux_des_murs,materiaux_de_la_toiture,usage_1,nombre_de_logements" +
                                 $"&COUNT={pageSize}&STARTINDEX={startIndex}";

                    byte[] bytes = await DownloadBytesAsync(url, 35);
                    if (bytes == null || bytes.Length == 0) break;
                    json = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(cachePath, bytes);
                }

                int parsedFeatures = ParseGeoJsonBuildings(json, sector.CenterLambertX, sector.CenterLambertY, allBuildings);
                onBatchLoaded?.Invoke(allBuildings.Count);

                if (parsedFeatures < pageSize)
                    break;
            }

            return allBuildings;
        }

        public async Task<List<VegetationZoneData>> DownloadVegetationAsync(GeoSector sector, int maxFeatures = 2000)
        {
            const int pageSize = 1000;
            var allZones = new List<VegetationZoneData>(256);
            string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
            string cacheDir = GetSectorCacheDir(sector);

            for (int startIndex = 0; startIndex < maxFeatures; startIndex += pageSize)
            {
                string cachePath = Path.Combine(cacheDir, $"vegetation_{startIndex}.json");
                string json = TryReadCacheText(cachePath);

                if (string.IsNullOrEmpty(json))
                {
                    string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                                 $"&TYPENAMES=BDTOPO_V3:zone_de_vegetation&SRSNAME=EPSG:2154" +
                                 $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                                 $"&PROPERTYNAME=geometrie,nature" +
                                 $"&COUNT={pageSize}&STARTINDEX={startIndex}";

                    byte[] bytes = await DownloadBytesAsync(url, 30);
                    if (bytes == null || bytes.Length == 0) break;
                    json = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(cachePath, bytes);
                }

                int parsed = ParseGeoJsonVegetation(json, sector.CenterLambertX, sector.CenterLambertY, allZones);
                if (parsed < pageSize)
                    break;
            }

            return allZones;
        }

        public async Task<List<WaterSurfaceData>> DownloadWaterAsync(GeoSector sector, int maxFeatures = 1000)
        {
            const int pageSize = 1000;
            var allWater = new List<WaterSurfaceData>(64);
            string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
            string cacheDir = GetSectorCacheDir(sector);

            for (int startIndex = 0; startIndex < maxFeatures; startIndex += pageSize)
            {
                string cachePath = Path.Combine(cacheDir, $"water_{startIndex}.json");
                string json = TryReadCacheText(cachePath);

                if (string.IsNullOrEmpty(json))
                {
                    string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                                 $"&TYPENAMES=BDTOPO_V3:surface_hydrographique&SRSNAME=EPSG:2154" +
                                 $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                                 $"&PROPERTYNAME=geometrie,nature" +
                                 $"&COUNT={pageSize}&STARTINDEX={startIndex}";

                    byte[] bytes = await DownloadBytesAsync(url, 30);
                    if (bytes == null || bytes.Length == 0) break;
                    json = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(cachePath, bytes);
                }

                // Reuse vegetation polygon+nature parser and wrap as WaterSurfaceData
                var temp = new List<VegetationZoneData>(64);
                int parsed = ParseGeoJsonVegetation(json, sector.CenterLambertX, sector.CenterLambertY, temp);
                for (int i = 0; i < temp.Count; i++)
                {
                    allWater.Add(new WaterSurfaceData
                    {
                        OuterRingLocal = temp[i].OuterRingLocal,
                        Nature = temp[i].Nature
                    });
                }

                if (parsed < pageSize)
                    break;
            }

            return allWater;
        }

        public async Task<List<CropParcelData>> DownloadCropParcelsAsync(GeoSector sector, int maxFeatures = 1000)
        {
            const int pageSize = 500;
            var allParcels = new List<CropParcelData>(128);
            string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
            string cacheDir = GetSectorCacheDir(sector);

            for (int startIndex = 0; startIndex < maxFeatures; startIndex += pageSize)
            {
                string cachePath = Path.Combine(cacheDir, $"rpg2024_{startIndex}.json");
                string json = TryReadCacheText(cachePath);

                if (string.IsNullOrEmpty(json))
                {
                    string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                                 $"&TYPENAMES=IGNF_RPG_PARCELLES-AGRICOLES-CATEGORISEES_2024:parcelles_agricole_categorisees_2024" +
                                 $"&SRSNAME=EPSG:2154&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                                 $"&PROPERTYNAME=geom,code_cultu&COUNT={pageSize}&STARTINDEX={startIndex}";

                    byte[] bytes = await DownloadBytesAsync(url, 25);
                    if (bytes == null || bytes.Length == 0) break;
                    json = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(cachePath, bytes);
                }

                int parsed = ParseGeoJsonCrops(json, sector.CenterLambertX, sector.CenterLambertY, allParcels);
                if (parsed < pageSize)
                    break;
            }

            return allParcels;
        }

        public async Task<List<RoadSignData>> DownloadRoadSignsAsync(GeoSector sector)
        {
            var signs = new List<RoadSignData>(128);
            string cachePath = Path.Combine(GetSectorCacheDir(sector), "osm_signs.json");
            string json = TryReadCacheText(cachePath);

            if (string.IsNullOrEmpty(json))
            {
                string query = string.Format(Inv,
                    "[out:json][timeout:10];node[\"highway\"~\"^(traffic_signals|stop|give_way)$\"]({0:F5},{1:F5},{2:F5},{3:F5});out body;",
                    sector.MinLat, sector.MinLon, sector.MaxLat, sector.MaxLon);
                string encoded = UnityWebRequest.EscapeURL(query);

                // shortcut: OSM France Overpass primary, public mirrors as fallback
                string[] endpoints =
                {
                    "https://overpass.openstreetmap.fr/api/interpreter?data=",
                    "https://overpass-api.de/api/interpreter?data=",
                    "https://overpass.private.coffee/api/interpreter?data="
                };

                byte[] bytes = null;
                foreach (var ep in endpoints)
                {
                    bytes = await DownloadBytesAsync(ep + encoded, 12, "GeoUnity/1.0");
                    if (bytes != null && bytes.Length > 0 && bytes[0] == (byte)'{')
                        break;
                    bytes = null;
                }

                if (bytes == null)
                    return signs;

                json = System.Text.Encoding.UTF8.GetString(bytes);
                WriteCacheSafe(cachePath, bytes);
            }

            ParseOverpassSigns(json, sector.CenterLambertX, sector.CenterLambertY, signs);
            return signs;
        }

        public async Task<List<BridgeData>> DownloadBridgesAsync(GeoSector sector, int maxFeatures = 500)
        {
            var bridges = new List<BridgeData>(64);
            string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
            string cacheDir = GetSectorCacheDir(sector);

            // 1. Wide bridges from BDTOPO_V3:construction_surfacique (gives exact width + orientation)
            string surfCachePath = Path.Combine(cacheDir, "const_surfacique_0.json");
            string surfJson = TryReadCacheText(surfCachePath);
            if (string.IsNullOrEmpty(surfJson))
            {
                string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                             $"&TYPENAMES=BDTOPO_V3:construction_surfacique&SRSNAME=EPSG:2154" +
                             $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                             $"&PROPERTYNAME=geometrie,nature,nature_detaillee,toponyme&COUNT={maxFeatures}&STARTINDEX=0";
                byte[] bytes = await DownloadBytesAsync(url, 25);
                if (bytes != null && bytes.Length > 0)
                {
                    surfJson = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(surfCachePath, bytes);
                }
            }
            if (!string.IsNullOrEmpty(surfJson))
                ParseGeoJsonSurfaceBridges(surfJson, sector.CenterLambertX, sector.CenterLambertY, bridges);

            // 2. Linear bridges / viaducts / footbridges from BDTOPO_V3:construction_lineaire
            string linCachePath = Path.Combine(cacheDir, "const_lineaire_0.json");
            string linJson = TryReadCacheText(linCachePath);
            if (string.IsNullOrEmpty(linJson))
            {
                string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                             $"&TYPENAMES=BDTOPO_V3:construction_lineaire&SRSNAME=EPSG:2154" +
                             $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                             $"&PROPERTYNAME=geometrie,nature,nature_detaillee,toponyme&COUNT={maxFeatures}&STARTINDEX=0";
                byte[] bytes = await DownloadBytesAsync(url, 25);
                if (bytes != null && bytes.Length > 0)
                {
                    linJson = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(linCachePath, bytes);
                }
            }
            if (!string.IsNullOrEmpty(linJson))
                ParseGeoJsonLinearBridges(linJson, sector.CenterLambertX, sector.CenterLambertY, bridges);

            return bridges;
        }

        public async Task<List<LandmarkData>> DownloadLandmarksAsync(GeoSector sector, int maxFeatures = 500)
        {
            var landmarks = new List<LandmarkData>(64);
            string bbox = FormatBbox(sector.MinLambertX, sector.MinLambertY, sector.MaxLambertX, sector.MaxLambertY);
            string cacheDir = GetSectorCacheDir(sector);

            // 1. IGN BDTOPO_V3:construction_ponctuelle (calvaires, statues, monuments, phares, antennes, etc.)
            string ignCachePath = Path.Combine(cacheDir, "const_ponctuelle_0.json");
            string ignJson = TryReadCacheText(ignCachePath);
            if (string.IsNullOrEmpty(ignJson))
            {
                string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                             $"&TYPENAMES=BDTOPO_V3:construction_ponctuelle&SRSNAME=EPSG:2154" +
                             $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                             $"&PROPERTYNAME=geometrie,nature,nature_detaillee,toponyme&COUNT={maxFeatures}&STARTINDEX=0";
                byte[] bytes = await DownloadBytesAsync(url, 25);
                if (bytes != null && bytes.Length > 0)
                {
                    ignJson = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(ignCachePath, bytes);
                }
            }
            if (!string.IsNullOrEmpty(ignJson))
                ParseGeoJsonPointLandmarks(ignJson, sector.CenterLambertX, sector.CenterLambertY, landmarks);

            // 2. OSM Overpass for urban statues, memorials, monuments, fountains, water towers
            string osmCachePath = Path.Combine(cacheDir, "osm_landmarks.json");
            string osmJson = TryReadCacheText(osmCachePath);
            if (string.IsNullOrEmpty(osmJson))
            {
                string b = string.Format(Inv, "{0:F5},{1:F5},{2:F5},{3:F5}",
                    sector.MinLat, sector.MinLon, sector.MaxLat, sector.MaxLon);
                string query =
                    $"[out:json][timeout:12];(" +
                    $"node[\"historic\"~\"^(monument|memorial|wayside_cross|wayside_shrine)$\"]({b});" +
                    $"node[\"artwork_type\"=\"statue\"]({b});" +
                    $"node[\"amenity\"=\"fountain\"]({b});" +
                    $"node[\"man_made\"=\"water_tower\"]({b});" +
                    $");out body;";
                string encoded = UnityWebRequest.EscapeURL(query);

                string[] endpoints =
                {
                    "https://overpass.openstreetmap.fr/api/interpreter?data=",
                    "https://overpass-api.de/api/interpreter?data=",
                    "https://overpass.private.coffee/api/interpreter?data="
                };

                byte[] bytes = null;
                foreach (var ep in endpoints)
                {
                    bytes = await DownloadBytesAsync(ep + encoded, 12, "GeoUnity/1.0");
                    if (bytes != null && bytes.Length > 0 && bytes[0] == (byte)'{')
                        break;
                    bytes = null;
                }

                if (bytes != null)
                {
                    osmJson = System.Text.Encoding.UTF8.GetString(bytes);
                    WriteCacheSafe(osmCachePath, bytes);
                }
            }
            if (!string.IsNullOrEmpty(osmJson))
                ParseOverpassLandmarks(osmJson, sector.CenterLambertX, sector.CenterLambertY, landmarks);

            return landmarks;
        }

        private static string FormatBbox(double minX, double minY, double maxX, double maxY)
        {
            return string.Format(Inv, "{0:F2},{1:F2},{2:F2},{3:F2}", minX, minY, maxX, maxY);
        }

        private static ElevationGrid CreateFlatGrid(GeoSector sector, int resolution, float height)
        {
            float[] heights = new float[resolution * resolution];
            if (height != 0f) Array.Fill(heights, height);
            return new ElevationGrid
            {
                Width = resolution,
                Height = resolution,
                MinLambertX = sector.MinLambertX,
                MaxLambertX = sector.MaxLambertX,
                MinLambertY = sector.MinLambertY,
                MaxLambertY = sector.MaxLambertY,
                Heights = heights,
                MinElevation = height,
                MaxElevation = height
            };
        }

        private static async Task<byte[]> DownloadBytesAsync(string url, int timeoutSec, string userAgent = null)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
            if (!string.IsNullOrEmpty(userAgent))
            {
                req.SetRequestHeader("User-Agent", userAgent);
                req.SetRequestHeader("Accept", "application/json");
            }
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                await Task.Yield();
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Ign3DDataClient] HTTP error ({req.responseCode}): {req.error} -> {url}");
                return null;
            }
            return req.downloadHandler.data;
        }

        private static byte[] TryReadCache(string path)
        {
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch { return null; }
        }

        private static string TryReadCacheText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static void WriteCacheSafe(string path, byte[] data)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, data);
            }
            catch { }
        }

        // --- Fast zero-dependency GeoJSON parser for IGN BDTOPO_V3:batiment ---
        private static int ParseGeoJsonBuildings(string json, double centerX, double centerY, List<BuildingData> output)
        {
            if (string.IsNullOrEmpty(json)) return 0;

            int featureCount = 0;
            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return 0;
            idx++;

            int len = json.Length;
            while (idx < len)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= len || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                featureCount++;
                ParseSingleFeature(json, objStart, objEnd, centerX, centerY, output);
                idx = objEnd + 1;
            }

            return featureCount;
        }

        private static void ParseSingleFeature(string json, int start, int end, double centerX, double centerY, List<BuildingData> output)
        {
            // Extract properties
            float hauteur = 0f;
            float roofAlt = 0f;
            bool hasRoofAlt = false;
            string nature = null;
            string wallMat = null;
            string roofMat = null;
            string usage = null;
            int housingCount = -1;

            int propsIdx = json.IndexOf("\"properties\"", start, end - start, StringComparison.Ordinal);
            if (propsIdx >= 0)
            {
                int propsStart = json.IndexOf('{', propsIdx, end - propsIdx);
                if (propsStart >= 0)
                {
                    int propsEnd = FindMatchingBrace(json, propsStart);
                    if (propsEnd > propsStart && propsEnd <= end)
                    {
                        TryReadFloatProperty(json, propsStart, propsEnd, "\"hauteur\"", out hauteur);
                        hasRoofAlt = TryReadFloatProperty(json, propsStart, propsEnd, "\"altitude_maximale_toit\"", out roofAlt);
                        nature = ReadStringProperty(json, propsStart, propsEnd, "\"nature\"");
                        wallMat = ReadStringProperty(json, propsStart, propsEnd, "\"materiaux_des_murs\"");
                        roofMat = ReadStringProperty(json, propsStart, propsEnd, "\"materiaux_de_la_toiture\"");
                        usage = ReadStringProperty(json, propsStart, propsEnd, "\"usage_1\"");
                        if (TryReadFloatProperty(json, propsStart, propsEnd, "\"nombre_de_logements\"", out float housingF))
                            housingCount = Mathf.RoundToInt(housingF);
                    }
                }
            }

            // Extract geometry coordinates (supports MultiPolygon and Polygon)
            int geomIdx = json.IndexOf("\"geometry\"", start, end - start, StringComparison.Ordinal);
            if (geomIdx < 0) return;
            int coordsIdx = json.IndexOf("\"coordinates\"", geomIdx, end - geomIdx, StringComparison.Ordinal);
            if (coordsIdx < 0) return;

            bool isMultiPolygon = json.IndexOf("\"MultiPolygon\"", geomIdx, coordsIdx - geomIdx, StringComparison.Ordinal) >= 0;
            int arrayStart = json.IndexOf('[', coordsIdx, end - coordsIdx);
            if (arrayStart < 0) return;

            var rings = new List<List<Vector2>>();
            if (isMultiPolygon)
            {
                int p = arrayStart + 1;
                while (p < end)
                {
                    SkipWhitespace(json, ref p);
                    if (p >= end || json[p] == ']') break;
                    if (json[p] == ',') { p++; continue; }
                    if (json[p] == '[')
                    {
                        int polyEnd = FindMatchingBracket(json, p, end);
                        if (polyEnd < 0) break;
                        int ringStart = json.IndexOf('[', p + 1, polyEnd - p - 1);
                        if (ringStart >= 0)
                        {
                            int ringEnd = FindMatchingBracket(json, ringStart, polyEnd);
                            if (ringEnd > ringStart)
                            {
                                var ring = ParseCoordinateRing(json, ringStart, ringEnd, centerX, centerY);
                                if (ring.Count >= 3) rings.Add(ring);
                            }
                        }
                        p = polyEnd + 1;
                    }
                    else break;
                }
            }
            else
            {
                int ringStart = json.IndexOf('[', arrayStart + 1, end - arrayStart - 1);
                if (ringStart >= 0)
                {
                    int ringEnd = FindMatchingBracket(json, ringStart, end);
                    if (ringEnd > ringStart)
                    {
                        var ring = ParseCoordinateRing(json, ringStart, ringEnd, centerX, centerY);
                        if (ring.Count >= 3) rings.Add(ring);
                    }
                }
            }

            float finalHeight = hauteur > 0.5f ? hauteur : 6.0f;
            foreach (var ring in rings)
            {
                output.Add(new BuildingData
                {
                    OuterRingLocal = ring,
                    HeightMeters = finalHeight,
                    RoofAltitude = roofAlt,
                    HasRoofAltitude = hasRoofAlt,
                    Nature = nature,
                    WallMaterial = wallMat,
                    RoofMaterial = roofMat,
                    Usage = usage,
                    HousingCount = housingCount
                });
            }
        }

        private static List<Vector2> ParseCoordinateRing(string json, int start, int end, double centerX, double centerY)
        {
            var pts = new List<Vector2>(16);
            int p = start + 1;
            while (p < end)
            {
                SkipWhitespace(json, ref p);
                if (p >= end || json[p] == ']') break;
                if (json[p] == ',') { p++; continue; }
                if (json[p] != '[') { p++; continue; }

                int ptEnd = json.IndexOf(']', p + 1, end - p - 1);
                if (ptEnd < 0) break;

                int c1 = json.IndexOf(',', p + 1, ptEnd - p - 1);
                if (c1 > p)
                {
                    int c2 = json.IndexOf(',', c1 + 1, ptEnd - c1 - 1);
                    int yEnd = c2 > c1 ? c2 : ptEnd;

                    if (double.TryParse(json.AsSpan(p + 1, c1 - p - 1), NumberStyles.Float, Inv, out double lx) &&
                        double.TryParse(json.AsSpan(c1 + 1, yEnd - c1 - 1), NumberStyles.Float, Inv, out double ly))
                    {
                        pts.Add(new Vector2((float)(lx - centerX), (float)(ly - centerY)));
                    }
                }
                p = ptEnd + 1;
            }
            return pts;
        }

        private static int ParseGeoJsonVegetation(string json, double centerX, double centerY, List<VegetationZoneData> output)
        {
            if (string.IsNullOrEmpty(json)) return 0;

            int featureCount = 0;
            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return 0;
            idx++;

            int len = json.Length;
            while (idx < len)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= len || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                featureCount++;
                ParseSingleVegetationFeature(json, objStart, objEnd, centerX, centerY, output);
                idx = objEnd + 1;
            }

            return featureCount;
        }

        private static void ParseSingleVegetationFeature(string json, int start, int end, double centerX, double centerY, List<VegetationZoneData> output)
        {
            string nature = null;
            int propsIdx = json.IndexOf("\"properties\"", start, end - start, StringComparison.Ordinal);
            if (propsIdx >= 0)
            {
                int propsStart = json.IndexOf('{', propsIdx, end - propsIdx);
                if (propsStart >= 0)
                {
                    int propsEnd = FindMatchingBrace(json, propsStart);
                    if (propsEnd > propsStart && propsEnd <= end)
                    {
                        nature = ReadStringProperty(json, propsStart, propsEnd, "\"nature\"");
                    }
                }
            }

            int geomIdx = json.IndexOf("\"geometry\"", start, end - start, StringComparison.Ordinal);
            if (geomIdx < 0) return;
            int coordsIdx = json.IndexOf("\"coordinates\"", geomIdx, end - geomIdx, StringComparison.Ordinal);
            if (coordsIdx < 0) return;

            bool isMultiPolygon = json.IndexOf("\"MultiPolygon\"", geomIdx, coordsIdx - geomIdx, StringComparison.Ordinal) >= 0;
            int arrayStart = json.IndexOf('[', coordsIdx, end - coordsIdx);
            if (arrayStart < 0) return;

            if (isMultiPolygon)
            {
                int p = arrayStart + 1;
                while (p < end)
                {
                    SkipWhitespace(json, ref p);
                    if (p >= end || json[p] == ']') break;
                    if (json[p] == ',') { p++; continue; }
                    if (json[p] == '[')
                    {
                        int polyEnd = FindMatchingBracket(json, p, end);
                        if (polyEnd < 0) break;
                        int ringStart = json.IndexOf('[', p + 1, polyEnd - p - 1);
                        if (ringStart >= 0)
                        {
                            int ringEnd = FindMatchingBracket(json, ringStart, polyEnd);
                            if (ringEnd > ringStart)
                            {
                                var ring = ParseCoordinateRing(json, ringStart, ringEnd, centerX, centerY);
                                if (ring.Count >= 3)
                                    output.Add(new VegetationZoneData { OuterRingLocal = ring, Nature = nature });
                            }
                        }
                        p = polyEnd + 1;
                    }
                    else break;
                }
            }
            else
            {
                int ringStart = json.IndexOf('[', arrayStart + 1, end - arrayStart - 1);
                if (ringStart >= 0)
                {
                    int ringEnd = FindMatchingBracket(json, ringStart, end);
                    if (ringEnd > ringStart)
                    {
                        var ring = ParseCoordinateRing(json, ringStart, ringEnd, centerX, centerY);
                        if (ring.Count >= 3)
                            output.Add(new VegetationZoneData { OuterRingLocal = ring, Nature = nature });
                    }
                }
            }
        }

        private static int ParseGeoJsonCrops(string json, double centerX, double centerY, List<CropParcelData> output)
        {
            if (string.IsNullOrEmpty(json)) return 0;

            int featureCount = 0;
            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return 0;
            idx++;

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                featureCount++;
                string code = ReadStringProperty(json, objStart, objEnd, "\"code_cultu\"");
                int geomIdx = json.IndexOf("\"geometry\"", objStart, objEnd - objStart, StringComparison.Ordinal);
                if (geomIdx >= 0)
                {
                    int coordsIdx = json.IndexOf("\"coordinates\"", geomIdx, objEnd - geomIdx, StringComparison.Ordinal);
                    if (coordsIdx >= 0)
                    {
                        bool isMulti = json.IndexOf("\"MultiPolygon\"", geomIdx, coordsIdx - geomIdx, StringComparison.Ordinal) >= 0;
                        int arrStart = json.IndexOf('[', coordsIdx, objEnd - coordsIdx);
                        if (arrStart >= 0)
                        {
                            if (isMulti)
                            {
                                int p = arrStart + 1;
                                while (p < objEnd)
                                {
                                    SkipWhitespace(json, ref p);
                                    if (p >= objEnd || json[p] == ']') break;
                                    if (json[p] == ',') { p++; continue; }
                                    if (json[p] == '[')
                                    {
                                        int polyEnd = FindMatchingBracket(json, p, objEnd);
                                        if (polyEnd < 0) break;
                                        int ringStart = json.IndexOf('[', p + 1, polyEnd - p - 1);
                                        if (ringStart >= 0)
                                        {
                                            int ringEnd = FindMatchingBracket(json, ringStart, polyEnd);
                                            if (ringEnd > ringStart)
                                            {
                                                var ring = ParseCoordinateRing(json, ringStart, ringEnd, centerX, centerY);
                                                if (ring.Count >= 3)
                                                    output.Add(new CropParcelData { OuterRingLocal = ring, CropCode = code });
                                            }
                                        }
                                        p = polyEnd + 1;
                                    }
                                    else break;
                                }
                            }
                            else
                            {
                                int ringStart = json.IndexOf('[', arrStart + 1, objEnd - arrStart - 1);
                                if (ringStart >= 0)
                                {
                                    int ringEnd = FindMatchingBracket(json, ringStart, objEnd);
                                    if (ringEnd > ringStart)
                                    {
                                        var ring = ParseCoordinateRing(json, ringStart, ringEnd, centerX, centerY);
                                        if (ring.Count >= 3)
                                            output.Add(new CropParcelData { OuterRingLocal = ring, CropCode = code });
                                    }
                                }
                            }
                        }
                    }
                }
                idx = objEnd + 1;
            }
            return featureCount;
        }

        private static void ParseOverpassSigns(string json, double centerX, double centerY, List<RoadSignData> output)
        {
            if (string.IsNullOrEmpty(json)) return;
            int idx = json.IndexOf("\"elements\"", StringComparison.Ordinal);
            if (idx < 0) return;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return;
            idx++;

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                if (TryReadDoubleProperty(json, objStart, objEnd, "\"lat\"", out double lat) &&
                    TryReadDoubleProperty(json, objStart, objEnd, "\"lon\"", out double lon))
                {
                    string hw = ReadStringProperty(json, objStart, objEnd, "\"highway\"");
                    RoadSignKind? kind = hw switch
                    {
                        "traffic_signals" => RoadSignKind.TrafficSignal,
                        "stop" => RoadSignKind.Stop,
                        "give_way" => RoadSignKind.GiveWay,
                        _ => null
                    };

                    if (kind.HasValue)
                    {
                        GeoProjection.Wgs84ToLambert93(lat, lon, out double lx, out double ly);
                        output.Add(new RoadSignData
                        {
                            LocalPos = new Vector2((float)(lx - centerX), (float)(ly - centerY)),
                            Kind = kind.Value
                        });
                    }
                }
                idx = objEnd + 1;
            }
        }

        private static bool IsBridgeFeature(string nature, string detailed)
        {
            if (!string.IsNullOrEmpty(nature) &&
                (nature.Contains("Pont", StringComparison.OrdinalIgnoreCase) ||
                 nature.Contains("Viaduc", StringComparison.OrdinalIgnoreCase) ||
                 nature.Contains("Passerelle", StringComparison.OrdinalIgnoreCase) ||
                 nature.Contains("Aqueduc", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrEmpty(detailed) &&
                (detailed.Contains("Pont", StringComparison.OrdinalIgnoreCase) ||
                 detailed.Contains("Viaduc", StringComparison.OrdinalIgnoreCase) ||
                 detailed.Contains("Passerelle", StringComparison.OrdinalIgnoreCase) ||
                 detailed.Contains("Aqueduc", StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        private static void ParseGeoJsonLinearBridges(string json, double centerX, double centerY, List<BridgeData> output)
        {
            if (string.IsNullOrEmpty(json)) return;
            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return;
            idx++;

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                string nature = ReadStringProperty(json, objStart, objEnd, "\"nature\"");
                string detailed = ReadStringProperty(json, objStart, objEnd, "\"nature_detaillee\"");
                if (IsBridgeFeature(nature, detailed))
                {
                    string name = ReadStringProperty(json, objStart, objEnd, "\"toponyme\"");
                    int geomIdx = json.IndexOf("\"geometry\"", objStart, objEnd - objStart, StringComparison.Ordinal);
                    if (geomIdx >= 0)
                    {
                        int coordsIdx = json.IndexOf("\"coordinates\"", geomIdx, objEnd - geomIdx, StringComparison.Ordinal);
                        if (coordsIdx >= 0)
                        {
                            bool isMulti = json.IndexOf("\"MultiLineString\"", geomIdx, coordsIdx - geomIdx, StringComparison.Ordinal) >= 0;
                            int arrStart = json.IndexOf('[', coordsIdx, objEnd - coordsIdx);
                            if (arrStart >= 0)
                            {
                                if (isMulti)
                                {
                                    int p = arrStart + 1;
                                    while (p < objEnd)
                                    {
                                        SkipWhitespace(json, ref p);
                                        if (p >= objEnd || json[p] == ']') break;
                                        if (json[p] == ',') { p++; continue; }
                                        if (json[p] == '[')
                                        {
                                            int lineEnd = FindMatchingBracket(json, p, objEnd);
                                            if (lineEnd < 0) break;
                                            AddLinearBridgeIfUnique(json, p, lineEnd, centerX, centerY, nature, detailed, name, output);
                                            p = lineEnd + 1;
                                        }
                                        else break;
                                    }
                                }
                                else
                                {
                                    int lineEnd = FindMatchingBracket(json, arrStart, objEnd);
                                    if (lineEnd > arrStart)
                                        AddLinearBridgeIfUnique(json, arrStart, lineEnd, centerX, centerY, nature, detailed, name, output);
                                }
                            }
                        }
                    }
                }
                idx = objEnd + 1;
            }
        }

        private static void AddLinearBridgeIfUnique(
            string json, int start, int end, double centerX, double centerY,
            string nature, string detailed, string name, List<BridgeData> output)
        {
            ParseCoordinateLine3D(json, start, end, centerX, centerY, out var pts, out var alts);
            if (pts.Count < 2) return;

            float totalLen = 0f;
            for (int i = 0; i < pts.Count - 1; i++)
                totalLen += Vector2.Distance(pts[i], pts[i + 1]);
            if (totalLen < 4f) return;

            Vector2 mid = pts[pts.Count / 2];
            for (int i = 0; i < output.Count; i++)
            {
                var ex = output[i].CenterLineLocal;
                if (ex != null && ex.Count > 0 && Vector2.SqrMagnitude(ex[ex.Count / 2] - mid) < 64f)
                    return; // Already covered by surface bridge or nearby duplicate
            }

            float defaultWidth = 9.0f;
            if (!string.IsNullOrEmpty(detailed) && detailed.Contains("Passerelle", StringComparison.OrdinalIgnoreCase))
                defaultWidth = 3.2f;
            else if (!string.IsNullOrEmpty(detailed) && detailed.Contains("Aqueduc", StringComparison.OrdinalIgnoreCase))
                defaultWidth = 4.5f;
            else if (totalLen > 120f)
                defaultWidth = 13.5f;

            output.Add(new BridgeData
            {
                CenterLineLocal = pts,
                Altitudes = alts,
                WidthMeters = defaultWidth,
                Nature = nature,
                DetailedNature = detailed,
                Name = name
            });
        }

        private static void ParseGeoJsonSurfaceBridges(string json, double centerX, double centerY, List<BridgeData> output)
        {
            if (string.IsNullOrEmpty(json)) return;
            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return;
            idx++;

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                string nature = ReadStringProperty(json, objStart, objEnd, "\"nature\"");
                string detailed = ReadStringProperty(json, objStart, objEnd, "\"nature_detaillee\"");
                if (IsBridgeFeature(nature, detailed))
                {
                    string name = ReadStringProperty(json, objStart, objEnd, "\"toponyme\"");
                    int geomIdx = json.IndexOf("\"geometry\"", objStart, objEnd - objStart, StringComparison.Ordinal);
                    if (geomIdx >= 0)
                    {
                        int coordsIdx = json.IndexOf("\"coordinates\"", geomIdx, objEnd - geomIdx, StringComparison.Ordinal);
                        if (coordsIdx >= 0)
                        {
                            bool isMulti = json.IndexOf("\"MultiPolygon\"", geomIdx, coordsIdx - geomIdx, StringComparison.Ordinal) >= 0;
                            int arrStart = json.IndexOf('[', coordsIdx, objEnd - coordsIdx);
                            if (arrStart >= 0)
                            {
                                int ringStart = -1;
                                if (isMulti)
                                {
                                    int polyStart = json.IndexOf('[', arrStart + 1, objEnd - arrStart - 1);
                                    if (polyStart >= 0 && polyStart + 1 < objEnd)
                                        ringStart = json.IndexOf('[', polyStart + 1, objEnd - polyStart - 1);
                                }
                                else
                                {
                                    ringStart = json.IndexOf('[', arrStart + 1, objEnd - arrStart - 1);
                                }
                                if (ringStart >= 0)
                                {
                                    int ringEnd = FindMatchingBracket(json, ringStart, objEnd);
                                    if (ringEnd > ringStart)
                                    {
                                        ParseCoordinateLine3D(json, ringStart, ringEnd, centerX, centerY, out var ring, out var alts);
                                        if (ring.Count >= 3 && TryPolygonToBridgeAxis(ring, alts, out var a, out var b, out float width, out float avgAlt))
                                        {
                                            output.Add(new BridgeData
                                            {
                                                CenterLineLocal = new List<Vector2> { a, b },
                                                Altitudes = new List<float> { avgAlt, avgAlt },
                                                WidthMeters = Mathf.Clamp(width, 3.5f, 34f),
                                                Nature = nature,
                                                DetailedNature = detailed,
                                                Name = name
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                idx = objEnd + 1;
            }
        }

        private static bool TryPolygonToBridgeAxis(
            List<Vector2> ring, List<float> alts,
            out Vector2 start, out Vector2 end, out float width, out float avgAlt)
        {
            start = Vector2.zero;
            end = Vector2.zero;
            width = 10f;
            avgAlt = float.NaN;

            // Compute centroid and farthest vertex pair to find bridge principal axis
            Vector2 centroid = Vector2.zero;
            float altSum = 0f;
            int altCount = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                centroid += ring[i];
                if (i < alts.Count && !float.IsNaN(alts[i])) { altSum += alts[i]; altCount++; }
            }
            centroid /= ring.Count;
            if (altCount > 0) avgAlt = altSum / altCount;

            // Principal axis via 2D covariance eigenvector
            float cxx = 0f, cxy = 0f, cyy = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 d = ring[i] - centroid;
                cxx += d.x * d.x;
                cxy += d.x * d.y;
                cyy += d.y * d.y;
            }
            float angle = 0.5f * Mathf.Atan2(2f * cxy, cxx - cyy);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 perp = new Vector2(-dir.y, dir.x);

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 d = ring[i] - centroid;
                float u = Vector2.Dot(d, dir);
                float v = Vector2.Dot(d, perp);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            float length = maxU - minU;
            width = maxV - minV;
            if (length < 5f) return false;

            start = centroid + dir * minU;
            end = centroid + dir * maxU;
            return true;
        }

        private static void ParseCoordinateLine3D(
            string json, int start, int end, double centerX, double centerY,
            out List<Vector2> pts, out List<float> alts)
        {
            pts = new List<Vector2>(16);
            alts = new List<float>(16);
            int p = start + 1;
            while (p < end)
            {
                SkipWhitespace(json, ref p);
                if (p >= end || json[p] == ']') break;
                if (json[p] == ',') { p++; continue; }
                if (json[p] != '[') { p++; continue; }

                int ptEnd = json.IndexOf(']', p + 1, end - p - 1);
                if (ptEnd < 0) break;

                int c1 = json.IndexOf(',', p + 1, ptEnd - p - 1);
                if (c1 > p)
                {
                    int c2 = json.IndexOf(',', c1 + 1, ptEnd - c1 - 1);
                    int yEnd = c2 > c1 ? c2 : ptEnd;

                    if (double.TryParse(json.AsSpan(p + 1, c1 - p - 1), NumberStyles.Float, Inv, out double lx) &&
                        double.TryParse(json.AsSpan(c1 + 1, yEnd - c1 - 1), NumberStyles.Float, Inv, out double ly))
                    {
                        pts.Add(new Vector2((float)(lx - centerX), (float)(ly - centerY)));
                        float alt = float.NaN;
                        if (c2 > c1 && float.TryParse(json.AsSpan(c2 + 1, ptEnd - c2 - 1), NumberStyles.Float, Inv, out float z) && z > -200f && z < 5500f)
                            alt = z;
                        alts.Add(alt);
                    }
                }
                p = ptEnd + 1;
            }
        }

        private static void ParseGeoJsonPointLandmarks(string json, double centerX, double centerY, List<LandmarkData> output)
        {
            if (string.IsNullOrEmpty(json)) return;
            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return;
            idx++;

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                string nature = ReadStringProperty(json, objStart, objEnd, "\"nature\"");
                string detailed = ReadStringProperty(json, objStart, objEnd, "\"nature_detaillee\"");
                string name = ReadStringProperty(json, objStart, objEnd, "\"toponyme\"");

                LandmarkKind? kind = ClassifyIgnLandmark(nature, detailed);
                if (kind.HasValue)
                {
                    int geomIdx = json.IndexOf("\"geometry\"", objStart, objEnd - objStart, StringComparison.Ordinal);
                    if (geomIdx >= 0)
                    {
                        int coordsIdx = json.IndexOf("\"coordinates\"", geomIdx, objEnd - geomIdx, StringComparison.Ordinal);
                        if (coordsIdx >= 0)
                        {
                            int ptStart = json.IndexOf('[', coordsIdx, objEnd - coordsIdx);
                            int ptEnd = ptStart >= 0 ? json.IndexOf(']', ptStart + 1, objEnd - ptStart - 1) : -1;
                            if (ptStart >= 0 && ptEnd > ptStart)
                            {
                                int c1 = json.IndexOf(',', ptStart + 1, ptEnd - ptStart - 1);
                                if (c1 > ptStart)
                                {
                                    int c2 = json.IndexOf(',', c1 + 1, ptEnd - c1 - 1);
                                    int yEnd = c2 > c1 ? c2 : ptEnd;
                                    if (double.TryParse(json.AsSpan(ptStart + 1, c1 - ptStart - 1), NumberStyles.Float, Inv, out double lx) &&
                                        double.TryParse(json.AsSpan(c1 + 1, yEnd - c1 - 1), NumberStyles.Float, Inv, out double ly))
                                    {
                                        AddLandmarkIfUnique(output, new Vector2((float)(lx - centerX), (float)(ly - centerY)), kind.Value, name ?? detailed ?? nature);
                                    }
                                }
                            }
                        }
                    }
                }
                idx = objEnd + 1;
            }
        }

        private static LandmarkKind? ClassifyIgnLandmark(string nature, string detailed)
        {
            string combined = $"{nature} {detailed}";
            if (combined.Contains("Statue", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Vierge", StringComparison.OrdinalIgnoreCase))
                return LandmarkKind.Statue;
            if (combined.Contains("Monument", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Obélisque", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return LandmarkKind.Monument;
            if (combined.Contains("Croix", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Calvaire", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Oratoire", StringComparison.OrdinalIgnoreCase))
                return LandmarkKind.Calvaire;
            if (combined.Contains("Fontaine", StringComparison.OrdinalIgnoreCase))
                return LandmarkKind.Fountain;
            if (combined.Contains("Château d'eau", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Chateau d'eau", StringComparison.OrdinalIgnoreCase))
                return LandmarkKind.WaterTower;
            if (combined.Contains("Phare", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Antenne", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Cheminée", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Eolienne", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Éolienne", StringComparison.OrdinalIgnoreCase))
                return LandmarkKind.TowerOrPylon;
            return null;
        }

        private static void ParseOverpassLandmarks(string json, double centerX, double centerY, List<LandmarkData> output)
        {
            if (string.IsNullOrEmpty(json)) return;
            int idx = json.IndexOf("\"elements\"", StringComparison.Ordinal);
            if (idx < 0) return;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return;
            idx++;

            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                if (TryReadDoubleProperty(json, objStart, objEnd, "\"lat\"", out double lat) &&
                    TryReadDoubleProperty(json, objStart, objEnd, "\"lon\"", out double lon))
                {
                    string historic = ReadStringProperty(json, objStart, objEnd, "\"historic\"");
                    string artwork = ReadStringProperty(json, objStart, objEnd, "\"artwork_type\"");
                    string amenity = ReadStringProperty(json, objStart, objEnd, "\"amenity\"");
                    string manMade = ReadStringProperty(json, objStart, objEnd, "\"man_made\"");
                    string name = ReadStringProperty(json, objStart, objEnd, "\"name\"");

                    LandmarkKind? kind = null;
                    if (artwork == "statue") kind = LandmarkKind.Statue;
                    else if (historic == "monument" || historic == "memorial") kind = LandmarkKind.Monument;
                    else if (historic == "wayside_cross" || historic == "wayside_shrine") kind = LandmarkKind.Calvaire;
                    else if (amenity == "fountain") kind = LandmarkKind.Fountain;
                    else if (manMade == "water_tower") kind = LandmarkKind.WaterTower;

                    if (kind.HasValue)
                    {
                        GeoProjection.Wgs84ToLambert93(lat, lon, out double lx, out double ly);
                        AddLandmarkIfUnique(output, new Vector2((float)(lx - centerX), (float)(ly - centerY)), kind.Value, name);
                    }
                }
                idx = objEnd + 1;
            }
        }

        private static void AddLandmarkIfUnique(List<LandmarkData> list, Vector2 pos, LandmarkKind kind, string label)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Vector2.SqrMagnitude(list[i].LocalPos - pos) < 64f) // within 8m
                    return;
            }
            list.Add(new LandmarkData { LocalPos = pos, Kind = kind, Label = label });
        }

        private static bool TryReadDoubleProperty(string json, int start, int end, string key, out double value)
        {
            value = 0.0;
            int k = json.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (k < 0) return false;
            int colon = json.IndexOf(':', k + key.Length, end - (k + key.Length));
            if (colon < 0) return false;
            int p = colon + 1;
            SkipWhitespace(json, ref p);
            if (p >= end || json[p] == 'n') return false;
            int valEnd = p;
            while (valEnd < end && (char.IsDigit(json[valEnd]) || json[valEnd] == '.' || json[valEnd] == '-' || json[valEnd] == 'e' || json[valEnd] == 'E' || json[valEnd] == '+'))
                valEnd++;
            return double.TryParse(json.AsSpan(p, valEnd - p), NumberStyles.Float, Inv, out value);
        }

        private static bool TryReadFloatProperty(string json, int start, int end, string key, out float value)
        {
            value = 0f;
            int k = json.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (k < 0) return false;
            int colon = json.IndexOf(':', k + key.Length, end - (k + key.Length));
            if (colon < 0) return false;
            int p = colon + 1;
            SkipWhitespace(json, ref p);
            if (p >= end || json[p] == 'n') return false; // null
            int valEnd = p;
            while (valEnd < end && (char.IsDigit(json[valEnd]) || json[valEnd] == '.' || json[valEnd] == '-' || json[valEnd] == 'e' || json[valEnd] == 'E' || json[valEnd] == '+'))
                valEnd++;
            return float.TryParse(json.AsSpan(p, valEnd - p), NumberStyles.Float, Inv, out value);
        }

        private static string ReadStringProperty(string json, int start, int end, string key)
        {
            int k = json.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k + key.Length, end - (k + key.Length));
            if (colon < 0) return null;
            int p = colon + 1;
            SkipWhitespace(json, ref p);
            if (p >= end || json[p] != '"') return null;
            int qEnd = json.IndexOf('"', p + 1, end - p - 1);
            if (qEnd < 0) return null;
            return json.Substring(p + 1, qEnd - p - 1);
        }

        private static void SkipWhitespace(string s, ref int idx)
        {
            while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
        }

        private static int FindMatchingBrace(string s, int openIdx)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = openIdx; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindMatchingBracket(string s, int openIdx, int limit)
        {
            int depth = 0;
            for (int i = openIdx; i <= limit && i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }
}

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
        public string WallMaterial;
        public string RoofMaterial;
        public string Usage;
    }

    public class VegetationZoneData
    {
        public List<Vector2> OuterRingLocal; // Local Unity (X, Z) meters relative to sector center
        public string Nature;
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
                string cachePath = Path.Combine(cacheDir, $"buildings_{startIndex}.json");
                string json = TryReadCacheText(cachePath);

                if (string.IsNullOrEmpty(json))
                {
                    string url = $"{WfsBaseUrl}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature" +
                                 $"&TYPENAMES=BDTOPO_V3:batiment&SRSNAME=EPSG:2154" +
                                 $"&BBOX={bbox},EPSG:2154&OUTPUTFORMAT=application/json" +
                                 $"&PROPERTYNAME=geometrie,hauteur,altitude_maximale_toit,materiaux_des_murs,materiaux_de_la_toiture,usage_1" +
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

        private static async Task<byte[]> DownloadBytesAsync(string url, int timeoutSec)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
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
            string wallMat = null;
            string roofMat = null;
            string usage = null;

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
                        wallMat = ReadStringProperty(json, propsStart, propsEnd, "\"materiaux_des_murs\"");
                        roofMat = ReadStringProperty(json, propsStart, propsEnd, "\"materiaux_de_la_toiture\"");
                        usage = ReadStringProperty(json, propsStart, propsEnd, "\"usage_1\"");
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
                    WallMaterial = wallMat,
                    RoofMaterial = roofMat,
                    Usage = usage
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

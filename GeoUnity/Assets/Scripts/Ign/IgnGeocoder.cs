using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using GeoUnity.Geo;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoUnity.Ign
{
    public struct GeocodeResult
    {
        public string Label;
        public string Context;
        public double Latitude;
        public double Longitude;
    }

    /// <summary>
    /// Searches French cities and addresses using the official IGN Geoplateforme Geocoding API (BAN).
    /// Filters results to Metropolitan France.
    /// </summary>
    public static class IgnGeocoder
    {
        private const string SearchUrl = "https://data.geopf.fr/geocodage/search";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static async Task<List<GeocodeResult>> SearchAsync(string query, int limit = 5)
        {
            var results = new List<GeocodeResult>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            string encoded = UnityWebRequest.EscapeURL(query.Trim());
            string url = $"{SearchUrl}?q={encoded}&limit={limit}";

            using var req = UnityWebRequest.Get(url);
            req.timeout = 10;
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[IgnGeocoder] Search failed: {req.error}");
                return results;
            }

            string json = req.downloadHandler.text;
            ParseResults(json, results);
            return results;
        }

        private static void ParseResults(string json, List<GeocodeResult> output)
        {
            if (string.IsNullOrEmpty(json)) return;

            int idx = json.IndexOf("\"features\"", StringComparison.Ordinal);
            if (idx < 0) return;
            idx = json.IndexOf('[', idx);
            if (idx < 0) return;
            idx++;

            while (idx < json.Length)
            {
                while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
                if (idx >= json.Length || json[idx] == ']') break;
                if (json[idx] == ',') { idx++; continue; }
                if (json[idx] != '{') break;

                int objStart = idx;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                if (TryParseFeature(json, objStart, objEnd, out var res))
                {
                    // Strict Metropolitan France filter
                    if (res.Latitude >= GeoProjection.FranceMinLat && res.Latitude <= GeoProjection.FranceMaxLat &&
                        res.Longitude >= GeoProjection.FranceMinLon && res.Longitude <= GeoProjection.FranceMaxLon)
                    {
                        output.Add(res);
                    }
                }

                idx = objEnd + 1;
            }
        }

        private static bool TryParseFeature(string json, int start, int end, out GeocodeResult result)
        {
            result = default;

            // Parse Point coordinates: [lon, lat]
            int coordsIdx = json.IndexOf("\"coordinates\"", start, end - start, StringComparison.Ordinal);
            if (coordsIdx < 0) return false;
            int arrStart = json.IndexOf('[', coordsIdx, end - coordsIdx);
            if (arrStart < 0) return false;
            int arrEnd = json.IndexOf(']', arrStart + 1, end - arrStart - 1);
            if (arrEnd < 0) return false;

            int comma = json.IndexOf(',', arrStart + 1, arrEnd - arrStart - 1);
            if (comma < 0) return false;

            if (!double.TryParse(json.AsSpan(arrStart + 1, comma - arrStart - 1), NumberStyles.Float, Inv, out double lon) ||
                !double.TryParse(json.AsSpan(comma + 1, arrEnd - comma - 1), NumberStyles.Float, Inv, out double lat))
                return false;

            string label = ReadStringProp(json, start, end, "\"label\"");
            string context = ReadStringProp(json, start, end, "\"context\"");
            if (string.IsNullOrEmpty(label)) return false;

            result = new GeocodeResult
            {
                Label = UnescapeJson(label),
                Context = string.IsNullOrEmpty(context) ? "" : UnescapeJson(context),
                Latitude = lat,
                Longitude = lon
            };
            return true;
        }

        private static string ReadStringProp(string json, int start, int end, string key)
        {
            int k = json.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k + key.Length, end - (k + key.Length));
            if (colon < 0) return null;
            int p = colon + 1;
            while (p < end && char.IsWhiteSpace(json[p])) p++;
            if (p >= end || json[p] != '"') return null;

            int qEnd = p + 1;
            while (qEnd < end)
            {
                if (json[qEnd] == '"' && json[qEnd - 1] != '\\') break;
                qEnd++;
            }
            if (qEnd >= end) return null;
            return json.Substring(p + 1, qEnd - p - 1);
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
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
    }
}

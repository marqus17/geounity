using System;
using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Renders IGN RPG 2024 agricultural parcels as low, terrain-conforming translucent 3D volumes
    /// whose height, color, and furrow pattern match the cultivated crop (code_cultu).
    /// Subdivides long edges and interior triangles so large parcels hug RGE ALTI hills without clipping.
    /// </summary>
    public static class CropFieldBuilder
    {
        private enum CropCategory
        {
            Wheat,
            YellowBloom, // Rapeseed / Sunflower
            Corn,
            Vineyard,
            Orchard,
            Meadow
        }

        private class CropBucket
        {
            public readonly string Name;
            public readonly float HeightMeters;
            public readonly Color Color;
            public readonly float StripeScale;
            public readonly float StripeAmp;
            public readonly List<Vector3> Verts = new List<Vector3>(8192);
            public readonly List<Vector3> Normals = new List<Vector3>(8192);
            public readonly List<int> Tris = new List<int>(16384);

            public CropBucket(string name, float heightMeters, Color color, float stripeScale, float stripeAmp)
            {
                Name = name;
                HeightMeters = heightMeters;
                Color = color;
                StripeScale = stripeScale;
                StripeAmp = stripeAmp;
            }
        }

        private const float MaxBoundaryEdgeMeters = 18f;
        private const float MaxInteriorEdgeMeters = 24f;
        private const int MaxSubdivDepth = 4;

        public static GameObject Build(GeoSector sector, ElevationGrid grid, List<CropParcelData> parcels, Transform parent, out int builtParcelCount)
        {
            builtParcelCount = 0;
            var root = new GameObject($"Crops_RPG2024 ({parcels?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (parcels == null || parcels.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 1f;
            float baseElev = grid.MinElevation;

            var buckets = new Dictionary<CropCategory, CropBucket>
            {
                { CropCategory.Wheat,       new CropBucket("RPG_Ble_Cereales",   0.95f, new Color(0.86f, 0.72f, 0.28f, 0.50f), 0.35f, 0.14f) },
                { CropCategory.YellowBloom, new CropBucket("RPG_Colza_Tournesol",1.35f, new Color(0.95f, 0.80f, 0.14f, 0.54f), 0.30f, 0.16f) },
                { CropCategory.Corn,        new CropBucket("RPG_Mais",           2.10f, new Color(0.32f, 0.58f, 0.18f, 0.52f), 0.45f, 0.18f) },
                { CropCategory.Vineyard,    new CropBucket("RPG_Vignes",         1.60f, new Color(0.22f, 0.48f, 0.18f, 0.56f), 0.65f, 0.24f) },
                { CropCategory.Orchard,     new CropBucket("RPG_Vergers",        3.00f, new Color(0.24f, 0.50f, 0.22f, 0.52f), 0.22f, 0.14f) },
                { CropCategory.Meadow,      new CropBucket("RPG_Prairies",       0.40f, new Color(0.40f, 0.62f, 0.26f, 0.34f), 0.18f, 0.08f) }
            };

            for (int pIdx = 0; pIdx < parcels.Count; pIdx++)
            {
                var parcel = parcels[pIdx];
                var rawRing = parcel.OuterRingLocal;
                if (rawRing == null || rawRing.Count < 3) continue;

                // Clean duplicate closing points
                var cleaned = new List<Vector2>(rawRing.Count);
                for (int i = 0; i < rawRing.Count; i++)
                {
                    Vector2 p = rawRing[i];
                    if (cleaned.Count > 0 && (p - cleaned[cleaned.Count - 1]).sqrMagnitude < 0.09f) continue;
                    cleaned.Add(p);
                }
                if (cleaned.Count > 1 && (cleaned[0] - cleaned[cleaned.Count - 1]).sqrMagnitude < 0.09f)
                    cleaned.RemoveAt(cleaned.Count - 1);
                if (cleaned.Count < 3) continue;

                // Clip strictly to the 2 km x 2 km diorama bounds
                var clipped = ClipToSquare(cleaned, -half, half, -half, half);
                if (clipped.Count < 3) continue;

                if (EarCut2D.SignedArea(clipped) < 0f)
                    clipped.Reverse();

                // Subdivide long perimeter edges so the border follows the RGE ALTI terrain smoothly
                var denseRing = SubdivideBoundary(clipped, MaxBoundaryEdgeMeters);
                var roofTris = EarCut2D.Triangulate(denseRing);
                if (roofTris.Count < 3) continue;

                CropCategory cat = ClassifyCrop(parcel.CropCode);
                CropBucket bucket = buckets[cat];
                float h = bucket.HeightMeters;

                // 1. Subdivided terrain-conforming top surface
                for (int i = 0; i < roofTris.Count; i += 3)
                {
                    Vector2 a = denseRing[roofTris[i]];
                    Vector2 b = denseRing[roofTris[i + 2]]; // CW front-face up (+Y)
                    Vector2 c = denseRing[roofTris[i + 1]];
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, h, a, b, c, 0);
                }

                // 2. Perimeter low skirt wall from terrainY - 0.3m up to terrainY + h
                int n = denseRing.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = denseRing[i];
                    Vector2 b = denseRing[(i + 1) % n];
                    Vector2 edge = b - a;
                    float len = edge.magnitude;
                    if (len < 0.05f) continue;

                    Vector3 normal = new Vector3(edge.y, 0f, -edge.x) / len;
                    float gA = grid.SampleHeight(sector.CenterLambertX + a.x, sector.CenterLambertY + a.y) - baseElev;
                    float gB = grid.SampleHeight(sector.CenterLambertX + b.x, sector.CenterLambertY + b.y) - baseElev;

                    int vStart = bucket.Verts.Count;
                    bucket.Verts.Add(new Vector3(a.x, gA - 0.3f, a.y));
                    bucket.Verts.Add(new Vector3(b.x, gB - 0.3f, b.y));
                    bucket.Verts.Add(new Vector3(b.x, gB + h, b.y));
                    bucket.Verts.Add(new Vector3(a.x, gA + h, a.y));

                    bucket.Normals.Add(normal);
                    bucket.Normals.Add(normal);
                    bucket.Normals.Add(normal);
                    bucket.Normals.Add(normal);

                    bucket.Tris.Add(vStart); bucket.Tris.Add(vStart + 3); bucket.Tris.Add(vStart + 2);
                    bucket.Tris.Add(vStart); bucket.Tris.Add(vStart + 2); bucket.Tris.Add(vStart + 1);
                }

                builtParcelCount++;
            }

            var cropShader = Shader.Find("GeoUnity/CropField")
                             ?? Shader.Find("Universal Render Pipeline/Lit")
                             ?? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Tris.Count == 0) continue;

                var go = new GameObject(bucket.Name);
                go.transform.SetParent(root.transform, false);

                var mesh = new Mesh { name = bucket.Name, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(bucket.Verts);
                mesh.SetNormals(bucket.Normals);
                mesh.SetTriangles(bucket.Tris, 0);
                mesh.RecalculateBounds();

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;

                var mat = new Material(cropShader);
                mat.name = $"{bucket.Name}_Mat";
                mat.SetColor("_BaseColor", bucket.Color);
                if (cropShader.name == "GeoUnity/CropField")
                {
                    mat.SetFloat("_StripeScale", bucket.StripeScale);
                    mat.SetFloat("_StripeAmp", bucket.StripeAmp);
                }
                mr.sharedMaterial = mat;
            }

            return root;
        }

        private static void EmitSubdividedTopTriangle(
            CropBucket bucket, GeoSector sector, ElevationGrid grid, float baseElev, float cropHeight,
            Vector2 a, Vector2 b, Vector2 c, int depth)
        {
            float ab = (b - a).sqrMagnitude;
            float bc = (c - b).sqrMagnitude;
            float ca = (a - c).sqrMagnitude;
            float maxSq = MaxInteriorEdgeMeters * MaxInteriorEdgeMeters;

            if (depth < MaxSubdivDepth && (ab > maxSq || bc > maxSq || ca > maxSq))
            {
                // Split along the longest edge to conform cleanly to terrain curvature
                if (ab >= bc && ab >= ca)
                {
                    Vector2 m = (a + b) * 0.5f;
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, cropHeight, a, m, c, depth + 1);
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, cropHeight, m, b, c, depth + 1);
                }
                else if (bc >= ab && bc >= ca)
                {
                    Vector2 m = (b + c) * 0.5f;
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, cropHeight, a, b, m, depth + 1);
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, cropHeight, a, m, c, depth + 1);
                }
                else
                {
                    Vector2 m = (c + a) * 0.5f;
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, cropHeight, a, b, m, depth + 1);
                    EmitSubdividedTopTriangle(bucket, sector, grid, baseElev, cropHeight, m, b, c, depth + 1);
                }
                return;
            }

            Vector3 va = new Vector3(a.x, grid.SampleHeight(sector.CenterLambertX + a.x, sector.CenterLambertY + a.y) - baseElev + cropHeight, a.y);
            Vector3 vb = new Vector3(b.x, grid.SampleHeight(sector.CenterLambertX + b.x, sector.CenterLambertY + b.y) - baseElev + cropHeight, b.y);
            Vector3 vc = new Vector3(c.x, grid.SampleHeight(sector.CenterLambertX + c.x, sector.CenterLambertY + c.y) - baseElev + cropHeight, c.y);

            Vector3 n = Vector3.Cross(vb - va, vc - va).normalized;
            if (n.y < 0f) n = -n;

            int idx = bucket.Verts.Count;
            bucket.Verts.Add(va); bucket.Verts.Add(vb); bucket.Verts.Add(vc);
            bucket.Normals.Add(n); bucket.Normals.Add(n); bucket.Normals.Add(n);
            bucket.Tris.Add(idx); bucket.Tris.Add(idx + 1); bucket.Tris.Add(idx + 2);
        }

        private static List<Vector2> SubdivideBoundary(List<Vector2> ring, float maxLen)
        {
            var result = new List<Vector2>(ring.Count * 2);
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % n];
                float len = (b - a).magnitude;
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / maxLen));
                for (int s = 0; s < steps; s++)
                {
                    result.Add(Vector2.Lerp(a, b, s / (float)steps));
                }
            }
            return result;
        }

        private static CropCategory ClassifyCrop(string code)
        {
            if (string.IsNullOrEmpty(code)) return CropCategory.Meadow;
            string c = code.ToUpperInvariant();
            if (c.StartsWith("VR") && c != "VRG") return CropCategory.Vineyard;
            if (c == "VRG" || c == "OLI" || c == "NOX" || c == "CTG") return CropCategory.Orchard;
            if (c.StartsWith("MI")) return CropCategory.Corn;
            if (c.StartsWith("CZ") || c == "TRN" || c == "MOUT") return CropCategory.YellowBloom;
            if (c.StartsWith("BT") || c.StartsWith("BD") || c.StartsWith("OR") || c.StartsWith("AV") || c.StartsWith("SG") || c.StartsWith("TT"))
                return CropCategory.Wheat;
            return CropCategory.Meadow;
        }

        private static List<Vector2> ClipToSquare(List<Vector2> input, float minX, float maxX, float minY, float maxY)
        {
            var poly = ClipEdge(input, p => p.x >= minX, (a, b) => IntersectX(a, b, minX));
            poly = ClipEdge(poly, p => p.x <= maxX, (a, b) => IntersectX(a, b, maxX));
            poly = ClipEdge(poly, p => p.y >= minY, (a, b) => IntersectY(a, b, minY));
            poly = ClipEdge(poly, p => p.y <= maxY, (a, b) => IntersectY(a, b, maxY));
            return poly;
        }

        private static List<Vector2> ClipEdge(List<Vector2> input, Func<Vector2, bool> inside, Func<Vector2, Vector2, Vector2> intersect)
        {
            var output = new List<Vector2>(input.Count);
            if (input.Count == 0) return output;
            Vector2 prev = input[input.Count - 1];
            bool prevIn = inside(prev);
            for (int i = 0; i < input.Count; i++)
            {
                Vector2 curr = input[i];
                bool currIn = inside(curr);
                if (currIn)
                {
                    if (!prevIn) output.Add(intersect(prev, curr));
                    output.Add(curr);
                }
                else if (prevIn)
                {
                    output.Add(intersect(prev, curr));
                }
                prev = curr;
                prevIn = currIn;
            }
            return output;
        }

        private static Vector2 IntersectX(Vector2 a, Vector2 b, float x)
        {
            float dx = b.x - a.x;
            float t = Mathf.Abs(dx) > 1e-6f ? (x - a.x) / dx : 0f;
            return new Vector2(x, a.y + t * (b.y - a.y));
        }

        private static Vector2 IntersectY(Vector2 a, Vector2 b, float y)
        {
            float dy = b.y - a.y;
            float t = Mathf.Abs(dy) > 1e-6f ? (y - a.y) / dy : 0f;
            return new Vector2(a.x + t * (b.x - a.x), y);
        }
    }
}

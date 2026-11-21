using System;
using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Renders IGN RPG 2024 agricultural parcels as 3D crops (vineyard rows, orchards, corn, wheat, rapeseed/sunflower)
    /// conformed to the RGE ALTI terrain, batched by crop type for 60 FPS.
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

        private class MeshAcc
        {
            public readonly List<Vector3> Verts = new List<Vector3>(8192);
            public readonly List<Vector3> Normals = new List<Vector3>(8192);
            public readonly List<int> Tris = new List<int>(16384);
        }

        public static GameObject Build(GeoSector sector, ElevationGrid grid, List<CropParcelData> parcels, Transform parent, out int cropElementCount)
        {
            cropElementCount = 0;
            var root = new GameObject($"Crops_RPG2024 ({parcels?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (parcels == null || parcels.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 4f;
            float baseElev = grid.MinElevation;

            var wheatAcc = new MeshAcc();
            var yellowAcc = new MeshAcc();
            var cornAcc = new MeshAcc();
            var vineAcc = new MeshAcc();
            var orchardTrunkAcc = new MeshAcc();
            var orchardCrownAcc = new MeshAcc();

            const int maxElements = 5000;

            for (int pIdx = 0; pIdx < parcels.Count && cropElementCount < maxElements; pIdx++)
            {
                var parcel = parcels[pIdx];
                var ring = parcel.OuterRingLocal;
                if (ring == null || ring.Count < 3) continue;

                CropCategory cat = ClassifyCrop(parcel.CropCode);
                if (cat == CropCategory.Meadow) continue; // Keep natural orthophoto clean for plain grass/meadow

                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector2 p = ring[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y < minZ) minZ = p.y;
                    if (p.y > maxZ) maxZ = p.y;
                }

                minX = Mathf.Max(minX, -half);
                maxX = Mathf.Min(maxX, half);
                minZ = Mathf.Max(minZ, -half);
                maxZ = Mathf.Min(maxZ, half);
                if (minX >= maxX || minZ >= maxZ) continue;

                float stepX = cat == CropCategory.Vineyard ? 7f : (cat == CropCategory.Orchard ? 12f : 14f);
                float stepZ = cat == CropCategory.Vineyard ? 16f : (cat == CropCategory.Orchard ? 12f : 14f);

                int gxMin = Mathf.FloorToInt(minX / stepX);
                int gxMax = Mathf.CeilToInt(maxX / stepX);
                int gzMin = Mathf.FloorToInt(minZ / stepZ);
                int gzMax = Mathf.CeilToInt(maxZ / stepZ);

                for (int gz = gzMin; gz <= gzMax && cropElementCount < maxElements; gz++)
                {
                    for (int gx = gxMin; gx <= gxMax && cropElementCount < maxElements; gx++)
                    {
                        float px = (gx + 0.5f) * stepX;
                        float pz = (gz + 0.5f) * stepZ;
                        if (cat != CropCategory.Vineyard && cat != CropCategory.Orchard)
                        {
                            px += (Hash01(gx, gz, 19) - 0.5f) * stepX * 0.6f;
                            pz += (Hash01(gx, gz, 43) - 0.5f) * stepZ * 0.6f;
                        }

                        if (px < -half || px > half || pz < -half || pz > half) continue;
                        if (!PointInPolygon(ring, px, pz)) continue;

                        float y = grid.SampleHeight(sector.CenterLambertX + px, sector.CenterLambertY + pz) - baseElev;
                        Vector3 pos = new Vector3(px, y, pz);

                        switch (cat)
                        {
                            case CropCategory.Vineyard:
                                // Parallel grapevine hedgerow segment (1.6m tall, 12m long)
                                AddBox(vineAcc, pos + new Vector3(0f, 0.75f, 0f), new Vector3(1.1f, 1.5f, 12.5f));
                                break;
                            case CropCategory.Orchard:
                                AddBox(orchardTrunkAcc, pos + new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 1.8f, 0.45f));
                                AddPyramidCrown(orchardCrownAcc, pos + new Vector3(0f, 2.7f, 0f), 2.4f, 1.8f);
                                break;
                            case CropCategory.Corn:
                                AddCrossTuft(cornAcc, pos, 5.5f, 2.2f);
                                break;
                            case CropCategory.YellowBloom:
                                AddCrossTuft(yellowAcc, pos, 6.0f, 1.35f);
                                break;
                            case CropCategory.Wheat:
                                AddCrossTuft(wheatAcc, pos, 6.2f, 1.05f);
                                break;
                        }

                        cropElementCount++;
                    }
                }
            }

            var urpShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpShader == null)
                urpShader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            BuildBucket("Crops_Wheat", wheatAcc, new Color(0.84f, 0.70f, 0.30f), 0.08f, urpShader, root.transform);
            BuildBucket("Crops_SunflowerRapeseed", yellowAcc, new Color(0.94f, 0.80f, 0.14f), 0.12f, urpShader, root.transform);
            BuildBucket("Crops_Corn", cornAcc, new Color(0.36f, 0.56f, 0.18f), 0.10f, urpShader, root.transform);
            BuildBucket("Crops_Vineyards", vineAcc, new Color(0.24f, 0.46f, 0.16f), 0.10f, urpShader, root.transform);
            BuildBucket("Crops_OrchardTrunks", orchardTrunkAcc, new Color(0.36f, 0.25f, 0.16f), 0.06f, urpShader, root.transform);
            BuildBucket("Crops_OrchardCrowns", orchardCrownAcc, new Color(0.28f, 0.52f, 0.22f), 0.10f, urpShader, root.transform);

            return root;
        }

        private static CropCategory ClassifyCrop(string code)
        {
            if (string.IsNullOrEmpty(code)) return CropCategory.Meadow;
            string c = code.ToUpperInvariant();
            if (c.StartsWith("VR") && c != "VRG") return CropCategory.Vineyard; // VRC, VRT
            if (c == "VRG" || c == "OLI" || c == "NOX" || c == "CTG") return CropCategory.Orchard;
            if (c.StartsWith("MI")) return CropCategory.Corn; // MIS, MIE, MID
            if (c.StartsWith("CZ") || c == "TRN" || c == "MOUT") return CropCategory.YellowBloom; // Colza, Tournesol
            if (c.StartsWith("BT") || c.StartsWith("BD") || c.StartsWith("OR") || c.StartsWith("AV") || c.StartsWith("SG") || c.StartsWith("TT"))
                return CropCategory.Wheat;
            return CropCategory.Meadow;
        }

        private static void AddCrossTuft(MeshAcc acc, Vector3 basePos, float width, float height)
        {
            float hw = width * 0.5f;
            AddDoubleQuad(acc, basePos + new Vector3(-hw, 0f, 0f), basePos + new Vector3(hw, 0f, 0f), height);
            AddDoubleQuad(acc, basePos + new Vector3(0f, 0f, -hw), basePos + new Vector3(0f, 0f, hw), height);
        }

        private static void AddDoubleQuad(MeshAcc acc, Vector3 a, Vector3 b, float h)
        {
            Vector3 c = b + new Vector3(0f, h, 0f);
            Vector3 d = a + new Vector3(0f, h, 0f);
            Vector3 n = Vector3.up;
            int i = acc.Verts.Count;
            acc.Verts.Add(a); acc.Verts.Add(b); acc.Verts.Add(c); acc.Verts.Add(d);
            acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n);
            acc.Tris.Add(i); acc.Tris.Add(i + 2); acc.Tris.Add(i + 1);
            acc.Tris.Add(i); acc.Tris.Add(i + 3); acc.Tris.Add(i + 2);
            acc.Tris.Add(i); acc.Tris.Add(i + 1); acc.Tris.Add(i + 2);
            acc.Tris.Add(i); acc.Tris.Add(i + 2); acc.Tris.Add(i + 3);
        }

        private static void AddBox(MeshAcc acc, Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3[] v =
            {
                center + new Vector3(-h.x, -h.y, -h.z), center + new Vector3(h.x, -h.y, -h.z),
                center + new Vector3(h.x, h.y, -h.z),   center + new Vector3(-h.x, h.y, -h.z),
                center + new Vector3(-h.x, -h.y, h.z),  center + new Vector3(h.x, -h.y, h.z),
                center + new Vector3(h.x, h.y, h.z),    center + new Vector3(-h.x, h.y, h.z)
            };
            AddFace(acc, v[3], v[2], v[6], v[7], Vector3.up);
            AddFace(acc, v[0], v[3], v[2], v[1], Vector3.back);
            AddFace(acc, v[5], v[6], v[7], v[4], Vector3.forward);
            AddFace(acc, v[4], v[7], v[3], v[0], Vector3.left);
            AddFace(acc, v[1], v[2], v[6], v[5], Vector3.right);
        }

        private static void AddPyramidCrown(MeshAcc acc, Vector3 center, float r, float h)
        {
            Vector3 top = center + new Vector3(0f, h, 0f);
            Vector3[] ring =
            {
                center + new Vector3(r, 0f, 0f), center + new Vector3(0f, 0f, r),
                center + new Vector3(-r, 0f, 0f), center + new Vector3(0f, 0f, -r)
            };
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = ring[i], b = ring[(i + 1) % 4];
                Vector3 n = Vector3.Cross(top - a, b - a).normalized;
                int idx = acc.Verts.Count;
                acc.Verts.Add(a); acc.Verts.Add(top); acc.Verts.Add(b);
                acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n);
                acc.Tris.Add(idx); acc.Tris.Add(idx + 1); acc.Tris.Add(idx + 2);
            }
        }

        private static void AddFace(MeshAcc acc, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            int idx = acc.Verts.Count;
            acc.Verts.Add(a); acc.Verts.Add(b); acc.Verts.Add(c); acc.Verts.Add(d);
            acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 1); acc.Tris.Add(idx + 2);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 2); acc.Tris.Add(idx + 3);
        }

        private static void BuildBucket(string name, MeshAcc acc, Color color, float smoothness, Shader shader, Transform parent)
        {
            if (acc.Tris.Count == 0) return;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(acc.Verts);
            mesh.SetNormals(acc.Normals);
            mesh.SetTriangles(acc.Tris, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static bool PointInPolygon(List<Vector2> poly, float px, float pz)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 pi = poly[i], pj = poly[j];
                if (((pi.y > pz) != (pj.y > pz)) && (px < (pj.x - pi.x) * (pz - pi.y) / (pj.y - pi.y + 1e-9f) + pi.x))
                    inside = !inside;
            }
            return inside;
        }

        private static float Hash01(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFu) / 65535f;
        }
    }
}

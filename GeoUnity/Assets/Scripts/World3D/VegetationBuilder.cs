using System;
using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Scatters and batches 3D procedural trees & shrubs inside IGN BD TOPO vegetation polygons
    /// (conifers, broadleaf forests, mixed woods, parks, hedges/shrubs), conformed to RGE ALTI relief.
    /// </summary>
    public static class VegetationBuilder
    {
        private enum VegKind
        {
            Broadleaf,
            Conifer,
            Mixed,
            Shrub
        }

        private class MeshAcc
        {
            public readonly List<Vector3> Verts = new List<Vector3>(16384);
            public readonly List<Vector3> Normals = new List<Vector3>(16384);
            public readonly List<int> Tris = new List<int>(32768);
        }

        public static GameObject Build(
            GeoSector sector,
            ElevationGrid grid,
            List<VegetationZoneData> zones,
            Transform parent,
            out int placedTreeCount)
        {
            placedTreeCount = 0;
            var root = new GameObject("Vegetation_BDTOPO");
            root.transform.SetParent(parent, false);

            if (zones == null || zones.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 6f;
            float baseElev = grid.MinElevation;

            // Estimate total forest area inside sector to auto-adapt tree spacing for smooth 60 FPS
            double approxTotalArea = 0.0;
            for (int i = 0; i < zones.Count; i++)
            {
                var ring = zones[i].OuterRingLocal;
                if (ring == null || ring.Count < 3) continue;
                approxTotalArea += Math.Abs(EarCut2D.SignedArea(ring));
            }

            const int targetMaxTrees = 5500;
            float baseSpacing = 15f;
            if (approxTotalArea > 0.0)
            {
                float neededSpacing = Mathf.Sqrt((float)(approxTotalArea / targetMaxTrees));
                baseSpacing = Mathf.Clamp(neededSpacing, 14f, 30f);
            }

            var trunks = new MeshAcc();
            var coniferCanopy = new MeshAcc();
            var broadleafCanopy = new MeshAcc();
            var shrubCanopy = new MeshAcc();

            int maxTreesHardCap = 6500;

            for (int zIdx = 0; zIdx < zones.Count && placedTreeCount < maxTreesHardCap; zIdx++)
            {
                var zone = zones[zIdx];
                var ring = zone.OuterRingLocal;
                if (ring == null || ring.Count < 3) continue;

                VegKind kind = ClassifyNature(zone.Nature);
                float spacing = kind == VegKind.Shrub ? Mathf.Max(11f, baseSpacing * 0.8f) : baseSpacing;

                // Compute clipped polygon bounds within sector
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

                int gxMin = Mathf.FloorToInt(minX / spacing);
                int gxMax = Mathf.CeilToInt(maxX / spacing);
                int gzMin = Mathf.FloorToInt(minZ / spacing);
                int gzMax = Mathf.CeilToInt(maxZ / spacing);

                for (int gz = gzMin; gz <= gzMax && placedTreeCount < maxTreesHardCap; gz++)
                {
                    for (int gx = gxMin; gx <= gxMax && placedTreeCount < maxTreesHardCap; gx++)
                    {
                        float jx = (Hash01(gx, gz, 13) - 0.5f) * spacing * 0.78f;
                        float jz = (Hash01(gx, gz, 37) - 0.5f) * spacing * 0.78f;
                        float px = (gx + 0.5f) * spacing + jx;
                        float pz = (gz + 0.5f) * spacing + jz;

                        if (px < -half || px > half || pz < -half || pz > half) continue;
                        if (!PointInPolygon(ring, px, pz)) continue;

                        float groundY = grid.SampleHeight(sector.CenterLambertX + px, sector.CenterLambertY + pz) - baseElev;
                        float scale = Mathf.Lerp(0.75f, 1.35f, Hash01(gx, gz, 73));
                        float yawRad = Hash01(gx, gz, 97) * Mathf.PI * 2f;

                        VegKind treeKind = kind;
                        if (kind == VegKind.Mixed)
                            treeKind = Hash01(gx, gz, 131) < 0.5f ? VegKind.Conifer : VegKind.Broadleaf;

                        Vector3 pos = new Vector3(px, groundY - 0.4f, pz);

                        if (treeKind == VegKind.Conifer)
                        {
                            AddConiferTree(trunks, coniferCanopy, pos, scale, yawRad);
                        }
                        else if (treeKind == VegKind.Broadleaf)
                        {
                            AddBroadleafTree(trunks, broadleafCanopy, pos, scale, yawRad);
                        }
                        else
                        {
                            AddShrub(shrubCanopy, pos, scale, yawRad);
                        }

                        placedTreeCount++;
                    }
                }
            }

            root.name = $"Vegetation_BDTOPO ({placedTreeCount} trees)";

            var urpShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpShader == null)
                urpShader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            CreateMeshObject("TreeTrunks", trunks, new Color(0.32f, 0.23f, 0.15f, 1f), 0.05f, urpShader, root.transform);
            CreateMeshObject("ConiferFoliage", coniferCanopy, new Color(0.14f, 0.29f, 0.16f, 1f), 0.08f, urpShader, root.transform);
            CreateMeshObject("BroadleafFoliage", broadleafCanopy, new Color(0.24f, 0.42f, 0.19f, 1f), 0.10f, urpShader, root.transform);
            CreateMeshObject("ShrubFoliage", shrubCanopy, new Color(0.32f, 0.45f, 0.21f, 1f), 0.08f, urpShader, root.transform);

            return root;
        }

        private static VegKind ClassifyNature(string nature)
        {
            if (string.IsNullOrEmpty(nature)) return VegKind.Broadleaf;
            if (nature.Contains("conif", StringComparison.OrdinalIgnoreCase)) return VegKind.Conifer;
            if (nature.Contains("mixte", StringComparison.OrdinalIgnoreCase)) return VegKind.Mixed;
            if (nature.Contains("Lande", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Vigne", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Haie", StringComparison.OrdinalIgnoreCase))
                return VegKind.Shrub;
            return VegKind.Broadleaf;
        }

        private static void AddConiferTree(MeshAcc trunks, MeshAcc foliage, Vector3 pos, float scale, float yaw)
        {
            float trunkH = 3.5f * scale;
            AddPrism(trunks, pos, 0.45f * scale, 0.25f * scale, trunkH, 5, yaw);

            // 3 stacked pine cones
            AddCone(foliage, pos + new Vector3(0f, 2.2f * scale, 0f), 3.6f * scale, 5.2f * scale, 6, yaw);
            AddCone(foliage, pos + new Vector3(0f, 5.0f * scale, 0f), 2.8f * scale, 4.6f * scale, 6, yaw + 0.5f);
            AddCone(foliage, pos + new Vector3(0f, 7.6f * scale, 0f), 1.9f * scale, 3.8f * scale, 6, yaw + 1.0f);
        }

        private static void AddBroadleafTree(MeshAcc trunks, MeshAcc foliage, Vector3 pos, float scale, float yaw)
        {
            float trunkH = 4.2f * scale;
            AddPrism(trunks, pos, 0.50f * scale, 0.30f * scale, trunkH, 5, yaw);

            // Organic 2-lobe low-poly crown
            AddOctaCrown(foliage, pos + new Vector3(0f, 6.2f * scale, 0f), 4.2f * scale, 3.6f * scale, yaw);
            Vector3 sideOffset = new Vector3(Mathf.Cos(yaw) * 1.4f * scale, 5.4f * scale, Mathf.Sin(yaw) * 1.4f * scale);
            AddOctaCrown(foliage, pos + sideOffset, 3.0f * scale, 2.6f * scale, yaw + 0.8f);
        }

        private static void AddShrub(MeshAcc foliage, Vector3 pos, float scale, float yaw)
        {
            AddOctaCrown(foliage, pos + new Vector3(0f, 1.4f * scale, 0f), 2.4f * scale, 1.6f * scale, yaw);
        }

        private static void AddPrism(MeshAcc acc, Vector3 basePos, float rBot, float rTop, float height, int sides, float yaw)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = yaw + (i / (float)sides) * Mathf.PI * 2f;
                float a1 = yaw + ((i + 1) / (float)sides) * Mathf.PI * 2f;

                Vector3 b0 = basePos + new Vector3(Mathf.Cos(a0) * rBot, 0f, Mathf.Sin(a0) * rBot);
                Vector3 b1 = basePos + new Vector3(Mathf.Cos(a1) * rBot, 0f, Mathf.Sin(a1) * rBot);
                Vector3 t0 = basePos + new Vector3(Mathf.Cos(a0) * rTop, height, Mathf.Sin(a0) * rTop);
                Vector3 t1 = basePos + new Vector3(Mathf.Cos(a1) * rTop, height, Mathf.Sin(a1) * rTop);

                AddQuad(acc, b0, b1, t1, t0);
            }
        }

        private static void AddCone(MeshAcc acc, Vector3 basePos, float radius, float height, int sides, float yaw)
        {
            Vector3 apex = basePos + new Vector3(0f, height, 0f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = yaw + (i / (float)sides) * Mathf.PI * 2f;
                float a1 = yaw + ((i + 1) / (float)sides) * Mathf.PI * 2f;

                Vector3 p0 = basePos + new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                Vector3 p1 = basePos + new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);

                AddTriangle(acc, p0, apex, p1);
            }
        }

        private static void AddOctaCrown(MeshAcc acc, Vector3 center, float radiusXZ, float radiusY, float yaw)
        {
            const int sides = 6;
            Vector3 top = center + new Vector3(0f, radiusY, 0f);
            Vector3 bot = center - new Vector3(0f, radiusY * 0.7f, 0f);

            for (int i = 0; i < sides; i++)
            {
                float a0 = yaw + (i / (float)sides) * Mathf.PI * 2f;
                float a1 = yaw + ((i + 1) / (float)sides) * Mathf.PI * 2f;

                Vector3 p0 = center + new Vector3(Mathf.Cos(a0) * radiusXZ, 0f, Mathf.Sin(a0) * radiusXZ);
                Vector3 p1 = center + new Vector3(Mathf.Cos(a1) * radiusXZ, 0f, Mathf.Sin(a1) * radiusXZ);

                AddTriangle(acc, p0, top, p1);
                AddTriangle(acc, p1, bot, p0);
            }
        }

        private static void AddTriangle(MeshAcc acc, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            int idx = acc.Verts.Count;
            acc.Verts.Add(a); acc.Verts.Add(b); acc.Verts.Add(c);
            acc.Normals.Add(normal); acc.Normals.Add(normal); acc.Normals.Add(normal);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 1); acc.Tris.Add(idx + 2);
        }

        private static void AddQuad(MeshAcc acc, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 normal = Vector3.Cross(d - a, b - a).normalized;
            int idx = acc.Verts.Count;
            acc.Verts.Add(a); acc.Verts.Add(b); acc.Verts.Add(c); acc.Verts.Add(d);
            acc.Normals.Add(normal); acc.Normals.Add(normal); acc.Normals.Add(normal); acc.Normals.Add(normal);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 3); acc.Tris.Add(idx + 2);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 2); acc.Tris.Add(idx + 1);
        }

        private static void CreateMeshObject(string name, MeshAcc acc, Color color, float smoothness, Shader urpShader, Transform parent)
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
            var mr = go.AddComponent<MeshRenderer>();
            var mat = new Material(urpShader);
            mat.name = $"{name}_Mat";
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            mr.sharedMaterial = mat;
        }

        private static bool PointInPolygon(List<Vector2> poly, float px, float pz)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 pi = poly[i];
                Vector2 pj = poly[j];
                bool intersect = ((pi.y > pz) != (pj.y > pz)) &&
                                 (px < (pj.x - pi.x) * (pz - pi.y) / (pj.y - pi.y + 1e-9f) + pi.x);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static float Hash01(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFu) / 65535f;
        }
    }
}

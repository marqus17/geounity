using System;
using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Extrudes IGN BD TOPO building footprints into batched URP 3D meshes grouped by architectural material buckets.
    /// Automatically conforms building foundations to the RGE ALTI elevation grid with a 3m skirt so nothing floats on slopes.
    /// </summary>
    public static class BuildingMeshBuilder
    {
        private class MeshBucket
        {
            public readonly string Name;
            public readonly Color Color;
            public readonly float Smoothness;
            public readonly Texture2D BaseTexture;
            public readonly List<Vector3> Vertices = new List<Vector3>(8192);
            public readonly List<Vector3> Normals = new List<Vector3>(8192);
            public readonly List<Vector2> Uvs = new List<Vector2>(8192);
            public readonly List<int> Triangles = new List<int>(16384);

            public MeshBucket(string name, Color color, float smoothness, Texture2D baseTexture)
            {
                Name = name;
                Color = color;
                Smoothness = smoothness;
                BaseTexture = baseTexture;
            }
        }

        public static GameObject Build(GeoSector sector, ElevationGrid grid, List<BuildingData> buildings, Transform parent)
        {
            var root = new GameObject($"Buildings_BDTOPO ({buildings.Count})");
            root.transform.SetParent(parent, false);

            if (buildings == null || buildings.Count == 0)
                return root;

            // Textured architectural material buckets (6 draw calls total for thousands of buildings)
            var wallLimestone = new MeshBucket("Walls_Limestone", Color.white, 0.14f, ArchitecturalTextureGenerator.GetLimestoneWall());
            var wallWarm = new MeshBucket("Walls_Warm", Color.white, 0.12f, ArchitecturalTextureGenerator.GetWarmWall());
            var wallModern = new MeshBucket("Walls_Modern", Color.white, 0.30f, ArchitecturalTextureGenerator.GetModernWall());

            var roofSlate = new MeshBucket("Roofs_Slate", Color.white, 0.22f, ArchitecturalTextureGenerator.GetSlateRoof());
            var roofTerracotta = new MeshBucket("Roofs_Terracotta", Color.white, 0.15f, ArchitecturalTextureGenerator.GetTerracottaRoof());
            var roofFlat = new MeshBucket("Roofs_Flat", Color.white, 0.10f, ArchitecturalTextureGenerator.GetFlatRoof());

            float baseElev = grid.MinElevation;
            float half = (float)(sector.SizeMeters * 0.5);
            var cleanedRing = new List<Vector2>(32);

            foreach (var b in buildings)
            {
                if (b.OuterRingLocal == null || b.OuterRingLocal.Count < 3) continue;

                // Clean duplicate/closing vertices
                cleanedRing.Clear();
                for (int i = 0; i < b.OuterRingLocal.Count; i++)
                {
                    Vector2 p = b.OuterRingLocal[i];
                    if (cleanedRing.Count > 0 && (p - cleanedRing[cleanedRing.Count - 1]).sqrMagnitude < 0.04f)
                        continue;
                    cleanedRing.Add(p);
                }
                if (cleanedRing.Count > 1 && (cleanedRing[0] - cleanedRing[cleanedRing.Count - 1]).sqrMagnitude < 0.04f)
                    cleanedRing.RemoveAt(cleanedRing.Count - 1);

                if (cleanedRing.Count < 3) continue;

                // Ensure counter-clockwise winding in XZ (positive signed area)
                if (EarCut2D.SignedArea(cleanedRing) < 0f)
                    cleanedRing.Reverse();

                // Sample terrain height under footprint
                float minGround = float.MaxValue;
                float sumGround = 0f;
                int insideCount = 0;
                for (int i = 0; i < cleanedRing.Count; i++)
                {
                    Vector2 p = cleanedRing[i];
                    if (Mathf.Abs(p.x) <= half + 20f && Mathf.Abs(p.y) <= half + 20f)
                        insideCount++;

                    float g = grid.SampleHeight(sector.CenterLambertX + p.x, sector.CenterLambertY + p.y) - baseElev;
                    if (g < minGround) minGround = g;
                    sumGround += g;
                }
                if (insideCount == 0) continue;

                float avgGround = sumGround / cleanedRing.Count;
                float bottomY = minGround - 3.0f; // Foundation skirt into terrain
                float height = Mathf.Clamp(b.HeightMeters, 2.5f, 350f);
                float topY = avgGround + height;

                if (b.HasRoofAltitude)
                {
                    float altTop = b.RoofAltitude - baseElev;
                    if (altTop > avgGround + 2.0f && altTop < avgGround + 350f)
                        topY = altTop;
                }

                // Pick wall & roof buckets from BD TOPO attributes
                MeshBucket wallBucket = PickWallBucket(b, wallLimestone, wallWarm, wallModern);
                MeshBucket roofBucket = PickRoofBucket(b, roofSlate, roofTerracotta, roofFlat);

                // 1. Extrude Walls (flat-shaded per edge with outward normals & metric facade UVs)
                int count = cleanedRing.Count;
                float wallHeight = Mathf.Max(1f, topY - bottomY);
                float vRepeat = wallHeight / 6.0f; // 1 texture tile = 2 floors (~6m)
                for (int i = 0; i < count; i++)
                {
                    Vector2 a = cleanedRing[i];
                    Vector2 c = cleanedRing[(i + 1) % count];
                    Vector2 edge = c - a;
                    float edgeLen = edge.magnitude;
                    if (edgeLen < 0.01f) continue;

                    // For CCW ring in (X, Z), outward normal is (edge.y, 0, -edge.x)
                    Vector3 normal = new Vector3(edge.y, 0f, -edge.x) / edgeLen;
                    float uRepeat = edgeLen / 6.0f; // 1 texture tile = 2 window bays (~6m)

                    int vStart = wallBucket.Vertices.Count;
                    wallBucket.Vertices.Add(new Vector3(a.x, bottomY, a.y));
                    wallBucket.Vertices.Add(new Vector3(c.x, bottomY, c.y));
                    wallBucket.Vertices.Add(new Vector3(c.x, topY, c.y));
                    wallBucket.Vertices.Add(new Vector3(a.x, topY, a.y));

                    wallBucket.Normals.Add(normal);
                    wallBucket.Normals.Add(normal);
                    wallBucket.Normals.Add(normal);
                    wallBucket.Normals.Add(normal);

                    wallBucket.Uvs.Add(new Vector2(0f, 0f));
                    wallBucket.Uvs.Add(new Vector2(uRepeat, 0f));
                    wallBucket.Uvs.Add(new Vector2(uRepeat, vRepeat));
                    wallBucket.Uvs.Add(new Vector2(0f, vRepeat));

                    // Outward-facing CW winding when looking at wall from outside
                    wallBucket.Triangles.Add(vStart);
                    wallBucket.Triangles.Add(vStart + 3);
                    wallBucket.Triangles.Add(vStart + 2);

                    wallBucket.Triangles.Add(vStart);
                    wallBucket.Triangles.Add(vStart + 2);
                    wallBucket.Triangles.Add(vStart + 1);
                }

                // 2. Triangulate Roof (upward normal +Y & metric world-space UVs)
                var roofTris = EarCut2D.Triangulate(cleanedRing);
                if (roofTris.Count >= 3)
                {
                    int rStart = roofBucket.Vertices.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Vector2 p = cleanedRing[i];
                        roofBucket.Vertices.Add(new Vector3(p.x, topY, p.y));
                        roofBucket.Normals.Add(Vector3.up);
                        roofBucket.Uvs.Add(new Vector2(p.x / 8.0f, p.y / 8.0f));
                    }

                    // EarCut returns CCW in (X, Z) -> reverse to (a, c, b) for Unity CW front-face up (+Y)
                    for (int i = 0; i < roofTris.Count; i += 3)
                    {
                        roofBucket.Triangles.Add(rStart + roofTris[i]);
                        roofBucket.Triangles.Add(rStart + roofTris[i + 2]);
                        roofBucket.Triangles.Add(rStart + roofTris[i + 1]);
                    }
                }
            }

            var urpShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpShader == null)
                urpShader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            var buckets = new[] { wallLimestone, wallWarm, wallModern, roofSlate, roofTerracotta, roofFlat };
            foreach (var bucket in buckets)
            {
                if (bucket.Triangles.Count == 0) continue;

                var go = new GameObject(bucket.Name);
                go.transform.SetParent(root.transform, false);

                var mesh = new Mesh
                {
                    name = bucket.Name,
                    indexFormat = IndexFormat.UInt32
                };
                mesh.SetVertices(bucket.Vertices);
                mesh.SetNormals(bucket.Normals);
                mesh.SetUVs(0, bucket.Uvs);
                mesh.SetTriangles(bucket.Triangles, 0);
                mesh.RecalculateBounds();

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(urpShader);
                mat.name = $"{bucket.Name}_Mat";
                if (bucket.BaseTexture != null)
                    mat.SetTexture("_BaseMap", bucket.BaseTexture);
                mat.SetColor("_BaseColor", bucket.Color);
                mat.SetFloat("_Smoothness", bucket.Smoothness);
                mr.sharedMaterial = mat;
            }

            return root;
        }

        private static MeshBucket PickWallBucket(BuildingData b, MeshBucket limestone, MeshBucket warm, MeshBucket modern)
        {
            if (!string.IsNullOrEmpty(b.WallMaterial))
            {
                if (b.WallMaterial.Contains("Brique", StringComparison.OrdinalIgnoreCase) ||
                    b.WallMaterial.Contains("Bois", StringComparison.OrdinalIgnoreCase))
                    return warm;
                if (b.WallMaterial.Contains("Béton", StringComparison.OrdinalIgnoreCase) ||
                    b.WallMaterial.Contains("Beton", StringComparison.OrdinalIgnoreCase) ||
                    b.WallMaterial.Contains("Verre", StringComparison.OrdinalIgnoreCase) ||
                    b.WallMaterial.Contains("Métal", StringComparison.OrdinalIgnoreCase))
                    return modern;
            }
            if (!string.IsNullOrEmpty(b.Usage) &&
                (b.Usage.Contains("Industriel", StringComparison.OrdinalIgnoreCase) ||
                 b.Usage.Contains("Commercial", StringComparison.OrdinalIgnoreCase)))
                return modern;

            return limestone;
        }

        private static MeshBucket PickRoofBucket(BuildingData b, MeshBucket slate, MeshBucket terracotta, MeshBucket flat)
        {
            if (!string.IsNullOrEmpty(b.RoofMaterial))
            {
                if (b.RoofMaterial.Contains("Tuile", StringComparison.OrdinalIgnoreCase))
                    return terracotta;
                if (b.RoofMaterial.Contains("Ardoise", StringComparison.OrdinalIgnoreCase) ||
                    b.RoofMaterial.Contains("Zinc", StringComparison.OrdinalIgnoreCase))
                    return slate;
                if (b.RoofMaterial.Contains("Béton", StringComparison.OrdinalIgnoreCase) ||
                    b.RoofMaterial.Contains("Beton", StringComparison.OrdinalIgnoreCase) ||
                    b.RoofMaterial.Contains("Tôle", StringComparison.OrdinalIgnoreCase))
                    return flat;
            }
            return slate;
        }
    }
}

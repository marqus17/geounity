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
            public readonly List<Vector3> Vertices = new List<Vector3>(8192);
            public readonly List<Vector3> Normals = new List<Vector3>(8192);
            public readonly List<int> Triangles = new List<int>(16384);

            public MeshBucket(string name, Color color, float smoothness)
            {
                Name = name;
                Color = color;
                Smoothness = smoothness;
            }
        }

        public static GameObject Build(GeoSector sector, ElevationGrid grid, List<BuildingData> buildings, Transform parent)
        {
            var root = new GameObject($"Buildings_BDTOPO ({buildings.Count})");
            root.transform.SetParent(parent, false);

            if (buildings == null || buildings.Count == 0)
                return root;

            // Architectural maquette material buckets (5 draw calls total for thousands of buildings)
            var wallLimestone = new MeshBucket("Walls_Limestone", new Color(0.86f, 0.83f, 0.76f, 1f), 0.12f);
            var wallWarm = new MeshBucket("Walls_Warm", new Color(0.80f, 0.72f, 0.64f, 1f), 0.12f);
            var wallModern = new MeshBucket("Walls_Modern", new Color(0.76f, 0.79f, 0.82f, 1f), 0.25f);

            var roofSlate = new MeshBucket("Roofs_Slate", new Color(0.32f, 0.36f, 0.42f, 1f), 0.22f);
            var roofTerracotta = new MeshBucket("Roofs_Terracotta", new Color(0.64f, 0.36f, 0.27f, 1f), 0.15f);
            var roofFlat = new MeshBucket("Roofs_Flat", new Color(0.55f, 0.56f, 0.58f, 1f), 0.10f);

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

                // 1. Extrude Walls (flat-shaded per edge with outward normals)
                int count = cleanedRing.Count;
                for (int i = 0; i < count; i++)
                {
                    Vector2 a = cleanedRing[i];
                    Vector2 c = cleanedRing[(i + 1) % count];
                    Vector2 edge = c - a;
                    if (edge.sqrMagnitude < 1e-4f) continue;

                    // For CCW ring in (X, Z), outward normal is (edge.y, 0, -edge.x)
                    Vector3 normal = new Vector3(edge.y, 0f, -edge.x).normalized;

                    int vStart = wallBucket.Vertices.Count;
                    wallBucket.Vertices.Add(new Vector3(a.x, bottomY, a.y));
                    wallBucket.Vertices.Add(new Vector3(c.x, bottomY, c.y));
                    wallBucket.Vertices.Add(new Vector3(c.x, topY, c.y));
                    wallBucket.Vertices.Add(new Vector3(a.x, topY, a.y));

                    wallBucket.Normals.Add(normal);
                    wallBucket.Normals.Add(normal);
                    wallBucket.Normals.Add(normal);
                    wallBucket.Normals.Add(normal);

                    // Outward-facing CW winding when looking at wall from outside
                    wallBucket.Triangles.Add(vStart);
                    wallBucket.Triangles.Add(vStart + 3);
                    wallBucket.Triangles.Add(vStart + 2);

                    wallBucket.Triangles.Add(vStart);
                    wallBucket.Triangles.Add(vStart + 2);
                    wallBucket.Triangles.Add(vStart + 1);
                }

                // 2. Triangulate Roof (upward normal +Y)
                var roofTris = EarCut2D.Triangulate(cleanedRing);
                if (roofTris.Count >= 3)
                {
                    int rStart = roofBucket.Vertices.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Vector2 p = cleanedRing[i];
                        roofBucket.Vertices.Add(new Vector3(p.x, topY, p.y));
                        roofBucket.Normals.Add(Vector3.up);
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
                mesh.SetTriangles(bucket.Triangles, 0);
                mesh.RecalculateBounds();

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(urpShader);
                mat.name = $"{bucket.Name}_Mat";
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

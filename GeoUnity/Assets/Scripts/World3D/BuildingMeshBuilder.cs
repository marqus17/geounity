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
    /// Adapts wall textures by building type:
    /// - Detached houses: 2 plain white crépi gable sides, 1 entry door + window side, 1 porte-fenêtre patio side
    /// - Churches / castles / historic monuments: grey dressed stone masonry with arched lancet windows
    /// - Industrial / agricultural hangars: concrete plinth + corrugated cladding
    /// - Urban apartments / modern commercial: limestone, warm brick, or modern glass
    /// </summary>
    public static class BuildingMeshBuilder
    {
        private enum BuildingArchetype
        {
            DetachedHouse,
            SmallAnnex,
            HistoricStone,
            Industrial,
            ModernCommercial,
            UrbanWarm,
            UrbanLimestone
        }

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

            // Textured architectural material buckets (11 draw calls total for thousands of buildings)
            var wallHousePlain = new MeshBucket("Walls_HousePlain", Color.white, 0.08f, ArchitecturalTextureGenerator.GetHouseStuccoPlain());
            var wallHouseEntry = new MeshBucket("Walls_HouseEntry", Color.white, 0.10f, ArchitecturalTextureGenerator.GetHouseStuccoEntry());
            var wallHousePatio = new MeshBucket("Walls_HousePatio", Color.white, 0.14f, ArchitecturalTextureGenerator.GetHouseStuccoPatio());
            var wallHistoric = new MeshBucket("Walls_HistoricStone", Color.white, 0.10f, ArchitecturalTextureGenerator.GetHistoricStoneWall());
            var wallIndustrial = new MeshBucket("Walls_Industrial", Color.white, 0.22f, ArchitecturalTextureGenerator.GetIndustrialCladdingWall());
            var wallLimestone = new MeshBucket("Walls_Limestone", Color.white, 0.14f, ArchitecturalTextureGenerator.GetLimestoneWall());
            var wallWarm = new MeshBucket("Walls_Warm", Color.white, 0.12f, ArchitecturalTextureGenerator.GetWarmWall());
            var wallModern = new MeshBucket("Walls_Modern", Color.white, 0.34f, ArchitecturalTextureGenerator.GetModernWall());

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
                float signedArea = EarCut2D.SignedArea(cleanedRing);
                if (signedArea < 0f)
                {
                    cleanedRing.Reverse();
                    signedArea = -signedArea;
                }

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

                float visibleHeight = Mathf.Max(2.0f, topY - avgGround);
                BuildingArchetype archetype = ClassifyBuilding(b, visibleHeight, signedArea);
                MeshBucket roofBucket = PickRoofBucket(b, archetype, cleanedRing[0], roofSlate, roofTerracotta, roofFlat);

                // For detached houses, pick which edge gets the front door+window and which opposite edge gets the porte-fenêtre
                int entryEdgeIdx = -1;
                int patioEdgeIdx = -1;
                if (archetype == BuildingArchetype.DetachedHouse)
                {
                    SelectHousePrincipalEdges(cleanedRing, out entryEdgeIdx, out patioEdgeIdx);
                }

                // 1. Extrude Walls (flat-shaded per edge with outward normals & type-adapted UVs)
                int count = cleanedRing.Count;
                float wallHeight = Mathf.Max(1f, topY - bottomY);

                for (int i = 0; i < count; i++)
                {
                    Vector2 a = cleanedRing[i];
                    Vector2 c = cleanedRing[(i + 1) % count];
                    Vector2 edge = c - a;
                    float edgeLen = edge.magnitude;
                    if (edgeLen < 0.01f) continue;

                    // For CCW ring in (X, Z), outward normal is (edge.y, 0, -edge.x)
                    Vector3 normal = new Vector3(edge.y, 0f, -edge.x) / edgeLen;

                    MeshBucket edgeBucket;
                    float uMax, vMin, vMax;

                    if (archetype == BuildingArchetype.DetachedHouse)
                    {
                        if (i == entryEdgeIdx)
                        {
                            edgeBucket = wallHouseEntry;
                            uMax = 1.0f; // Single front door + shuttered window across the facade
                            float topV = visibleHeight <= 5.2f ? 0.56f : 1.0f;
                            vMin = -(avgGround - bottomY) / Mathf.Max(1f, visibleHeight) * topV;
                            vMax = topV;
                        }
                        else if (i == patioEdgeIdx)
                        {
                            edgeBucket = wallHousePatio;
                            uMax = 1.0f; // Single wide porte-fenêtre on garden facade
                            float topV = visibleHeight <= 5.2f ? 0.56f : 1.0f;
                            vMin = -(avgGround - bottomY) / Mathf.Max(1f, visibleHeight) * topV;
                            vMax = topV;
                        }
                        else
                        {
                            edgeBucket = wallHousePlain; // Blind white crépi side / gable walls
                            uMax = edgeLen / 6.0f;
                            vMin = 0f;
                            vMax = wallHeight / 6.0f;
                        }
                    }
                    else if (archetype == BuildingArchetype.SmallAnnex)
                    {
                        edgeBucket = wallHousePlain;
                        uMax = edgeLen / 6.0f;
                        vMin = 0f;
                        vMax = wallHeight / 6.0f;
                    }
                    else if (archetype == BuildingArchetype.HistoricStone)
                    {
                        edgeBucket = wallHistoric;
                        uMax = edgeLen / 8.0f;
                        vMin = 0f;
                        vMax = wallHeight / 9.0f;
                    }
                    else if (archetype == BuildingArchetype.Industrial)
                    {
                        edgeBucket = wallIndustrial;
                        uMax = edgeLen / 8.0f;
                        vMin = -(avgGround - bottomY) / Mathf.Max(2f, visibleHeight);
                        vMax = 1.0f;
                    }
                    else
                    {
                        edgeBucket = archetype switch
                        {
                            BuildingArchetype.ModernCommercial => wallModern,
                            BuildingArchetype.UrbanWarm => wallWarm,
                            _ => wallLimestone
                        };
                        uMax = edgeLen / 6.0f;
                        vMin = 0f;
                        vMax = wallHeight / 6.0f;
                    }

                    int vStart = edgeBucket.Vertices.Count;
                    edgeBucket.Vertices.Add(new Vector3(a.x, bottomY, a.y));
                    edgeBucket.Vertices.Add(new Vector3(c.x, bottomY, c.y));
                    edgeBucket.Vertices.Add(new Vector3(c.x, topY, c.y));
                    edgeBucket.Vertices.Add(new Vector3(a.x, topY, a.y));

                    edgeBucket.Normals.Add(normal);
                    edgeBucket.Normals.Add(normal);
                    edgeBucket.Normals.Add(normal);
                    edgeBucket.Normals.Add(normal);

                    edgeBucket.Uvs.Add(new Vector2(0f, vMin));
                    edgeBucket.Uvs.Add(new Vector2(uMax, vMin));
                    edgeBucket.Uvs.Add(new Vector2(uMax, vMax));
                    edgeBucket.Uvs.Add(new Vector2(0f, vMax));

                    // Outward-facing CW winding when looking at wall from outside
                    edgeBucket.Triangles.Add(vStart);
                    edgeBucket.Triangles.Add(vStart + 3);
                    edgeBucket.Triangles.Add(vStart + 2);

                    edgeBucket.Triangles.Add(vStart);
                    edgeBucket.Triangles.Add(vStart + 2);
                    edgeBucket.Triangles.Add(vStart + 1);
                }

                // 2. Build Roof: sloped hipped roof for houses/historic/compact buildings, flat roof otherwise
                bool allowSloped = roofBucket != roofFlat && visibleHeight <= 32f && count <= 10;
                if (!allowSloped || !TryAddSlopedRoof(roofBucket, cleanedRing, topY, archetype == BuildingArchetype.HistoricStone))
                {
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
            }

            var urpShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpShader == null)
                urpShader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            var buckets = new[]
            {
                wallHousePlain, wallHouseEntry, wallHousePatio,
                wallHistoric, wallIndustrial,
                wallLimestone, wallWarm, wallModern,
                roofSlate, roofTerracotta, roofFlat
            };

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
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
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

        private static BuildingArchetype ClassifyBuilding(BuildingData b, float height, float area)
        {
            string nature = b.Nature ?? string.Empty;
            string usage = b.Usage ?? string.Empty;
            string wallMat = b.WallMaterial ?? string.Empty;

            // 1. Churches, chapels, castles, towers, forts, historic monuments -> grey dressed stone
            if (usage.Contains("Religieux", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Eglise", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Église", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Chapelle", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Château", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Chateau", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Monument", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Tour", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Fort", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Arène", StringComparison.OrdinalIgnoreCase))
            {
                return BuildingArchetype.HistoricStone;
            }

            // 2. Industrial / Agricultural / Sports halls / Silos -> ribbed cladding + concrete plinth
            if (usage.Contains("Industriel", StringComparison.OrdinalIgnoreCase) ||
                usage.Contains("Agricole", StringComparison.OrdinalIgnoreCase) ||
                usage.Contains("Sportif", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Industriel", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Silo", StringComparison.OrdinalIgnoreCase) ||
                nature.Contains("Serre", StringComparison.OrdinalIgnoreCase))
            {
                return BuildingArchetype.Industrial;
            }

            // 3. Modern glass/metal offices & tall commercial centers
            if (wallMat.Contains("Verre", StringComparison.OrdinalIgnoreCase) ||
                (usage.Contains("Commercial", StringComparison.OrdinalIgnoreCase) && height > 11f))
            {
                return BuildingArchetype.ModernCommercial;
            }
            if (usage.Contains("Commercial", StringComparison.OrdinalIgnoreCase) && area > 450f)
            {
                return BuildingArchetype.Industrial; // Big-box retail / supermarket
            }

            // 4. Small sheds / garages / annexes
            if (usage.Contains("Annexe", StringComparison.OrdinalIgnoreCase) || (area < 36f && height <= 4.6f))
            {
                return BuildingArchetype.SmallAnnex;
            }

            // 5. Detached single-family / semi-detached houses -> white crépi with per-face door + patio
            bool looksLikeHouse =
                height <= 10.5f &&
                area <= 260f &&
                (b.HousingCount < 0 || b.HousingCount <= 2) &&
                !wallMat.Contains("Verre", StringComparison.OrdinalIgnoreCase) &&
                !wallMat.Contains("Métal", StringComparison.OrdinalIgnoreCase);

            if (looksLikeHouse)
            {
                return BuildingArchetype.DetachedHouse;
            }

            // 6. Larger urban apartments / collective housing
            if (wallMat.Contains("Brique", StringComparison.OrdinalIgnoreCase) ||
                wallMat.Contains("Bois", StringComparison.OrdinalIgnoreCase))
                return BuildingArchetype.UrbanWarm;

            if (wallMat.Contains("Métal", StringComparison.OrdinalIgnoreCase) ||
                (wallMat.Contains("Béton", StringComparison.OrdinalIgnoreCase) && height > 18f))
                return BuildingArchetype.ModernCommercial;

            return BuildingArchetype.UrbanLimestone;
        }

        /// <summary>
        /// Picks the primary front edge (entry door + window) and the opposite rear edge (porte-fenêtre)
        /// so all other edges (typically the 2 gable sides) remain plain white crépi.
        /// </summary>
        private static void SelectHousePrincipalEdges(List<Vector2> ring, out int entryIdx, out int patioIdx)
        {
            int n = ring.Count;
            entryIdx = 0;
            patioIdx = -1;

            float bestLen = -1f;
            for (int i = 0; i < n; i++)
            {
                float len = (ring[(i + 1) % n] - ring[i]).magnitude;
                if (len > bestLen)
                {
                    bestLen = len;
                    entryIdx = i;
                }
            }

            Vector2 entryDir = (ring[(entryIdx + 1) % n] - ring[entryIdx]).normalized;
            Vector2 entryNormal = new Vector2(entryDir.y, -entryDir.x);

            // Pick the longest edge facing roughly opposite to entryEdge
            float bestOppScore = -1f;
            for (int i = 0; i < n; i++)
            {
                if (i == entryIdx) continue;
                Vector2 e = ring[(i + 1) % n] - ring[i];
                float len = e.magnitude;
                if (len < 2.2f) continue;

                Vector2 nrm = new Vector2(e.y, -e.x) / len;
                float dot = Vector2.Dot(entryNormal, nrm);
                if (dot < -0.45f)
                {
                    float score = len * (-dot);
                    if (score > bestOppScore)
                    {
                        bestOppScore = score;
                        patioIdx = i;
                    }
                }
            }
        }

        private static bool TryAddSlopedRoof(MeshBucket bucket, List<Vector2> ring, float eaveY, bool steepHistoric)
        {
            float area = Mathf.Abs(EarCut2D.SignedArea(ring));
            if (area < 16f || area > 1800f) return false;

            float slopeFactor = steepHistoric ? 0.42f : 0.28f;
            float maxRidgeH = steepHistoric ? 7.5f : 4.4f;

            // Case A: 4-vertex buildings -> true French 4-pan hipped roof with ridge along the longer axis
            if (ring.Count == 4)
            {
                Vector2 p0 = ring[0], p1 = ring[1], p2 = ring[2], p3 = ring[3];
                float len01 = (p1 - p0).magnitude;
                float len12 = (p2 - p1).magnitude;
                float len23 = (p3 - p2).magnitude;
                float len30 = (p0 - p3).magnitude;

                float avgA = (len01 + len23) * 0.5f;
                float avgB = (len12 + len30) * 0.5f;

                Vector2 midStart, midEnd;
                float shortSpan;
                int i0, i1, i2, i3;

                if (avgA >= avgB)
                {
                    i0 = 0; i1 = 1; i2 = 2; i3 = 3;
                    midStart = (p0 + p3) * 0.5f;
                    midEnd = (p1 + p2) * 0.5f;
                    shortSpan = avgB;
                }
                else
                {
                    i0 = 1; i1 = 2; i2 = 3; i3 = 0;
                    midStart = (p1 + p0) * 0.5f;
                    midEnd = (p2 + p3) * 0.5f;
                    shortSpan = avgA;
                }

                if (shortSpan < 3.2f || shortSpan > 28f) return false;

                float ridgeH = Mathf.Clamp(shortSpan * slopeFactor, 1.4f, maxRidgeH);
                Vector2 rA2D = Vector2.Lerp(midStart, midEnd, 0.22f);
                Vector2 rB2D = Vector2.Lerp(midStart, midEnd, 0.78f);

                Vector3 c0 = new Vector3(ring[i0].x, eaveY, ring[i0].y);
                Vector3 c1 = new Vector3(ring[i1].x, eaveY, ring[i1].y);
                Vector3 c2 = new Vector3(ring[i2].x, eaveY, ring[i2].y);
                Vector3 c3 = new Vector3(ring[i3].x, eaveY, ring[i3].y);
                Vector3 rA = new Vector3(rA2D.x, eaveY + ridgeH, rA2D.y);
                Vector3 rB = new Vector3(rB2D.x, eaveY + ridgeH, rB2D.y);

                AddRoofQuad(bucket, c0, rA, rB, c1);
                AddRoofQuad(bucket, c2, rB, rA, c3);
                AddRoofTri(bucket, c1, rB, c2);
                AddRoofTri(bucket, c3, rA, c0);
                return true;
            }

            // Case B: compact 3..10-vertex footprints where centroid lies cleanly inside -> hipped pyramid
            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < ring.Count; i++) centroid += ring[i];
            centroid /= ring.Count;

            if (!PointInPolygon(ring, centroid)) return false;

            float maxRad = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                float d = (ring[i] - centroid).magnitude;
                if (d > maxRad) maxRad = d;
            }
            if (maxRad < 2.5f || maxRad > 20f) return false;

            float apexH = Mathf.Clamp(maxRad * slopeFactor, 1.4f, maxRidgeH);
            Vector3 apex = new Vector3(centroid.x, eaveY + apexH, centroid.y);

            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a2 = ring[i];
                Vector2 b2 = ring[(i + 1) % ring.Count];
                Vector3 a = new Vector3(a2.x, eaveY, a2.y);
                Vector3 b = new Vector3(b2.x, eaveY, b2.y);
                AddRoofTri(bucket, a, apex, b);
            }
            return true;
        }

        private static void AddRoofTri(MeshBucket bucket, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            if (n.y < 0f) { (b, c) = (c, b); n = -n; }

            int idx = bucket.Vertices.Count;
            bucket.Vertices.Add(a); bucket.Vertices.Add(b); bucket.Vertices.Add(c);
            bucket.Normals.Add(n); bucket.Normals.Add(n); bucket.Normals.Add(n);
            bucket.Uvs.Add(new Vector2(a.x / 8f, a.z / 8f));
            bucket.Uvs.Add(new Vector2(b.x / 8f, b.z / 8f));
            bucket.Uvs.Add(new Vector2(c.x / 8f, c.z / 8f));
            bucket.Triangles.Add(idx); bucket.Triangles.Add(idx + 1); bucket.Triangles.Add(idx + 2);
        }

        private static void AddRoofQuad(MeshBucket bucket, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            AddRoofTri(bucket, a, b, c);
            AddRoofTri(bucket, a, c, d);
        }

        private static bool PointInPolygon(List<Vector2> poly, Vector2 pt)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 pi = poly[i];
                Vector2 pj = poly[j];
                bool intersect = ((pi.y > pt.y) != (pj.y > pt.y)) &&
                                 (pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y + 1e-9f) + pi.x);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static MeshBucket PickRoofBucket(
            BuildingData b, BuildingArchetype archetype, Vector2 refPos,
            MeshBucket slate, MeshBucket terracotta, MeshBucket flat)
        {
            if (archetype == BuildingArchetype.Industrial || archetype == BuildingArchetype.ModernCommercial)
                return flat;

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

            if (archetype == BuildingArchetype.HistoricStone)
                return slate;

            // Default houses without explicit roof material: terracotta tiles dominate in France (with slate variety)
            if (archetype == BuildingArchetype.DetachedHouse || archetype == BuildingArchetype.SmallAnnex)
            {
                int h = Mathf.Abs(Mathf.RoundToInt(refPos.x * 13f + refPos.y * 31f)) % 10;
                return h < 7 ? terracotta : slate;
            }

            return slate;
        }
    }
}

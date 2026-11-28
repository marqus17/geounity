using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Places batched 3D procedural base models for French landmarks and point structures
    /// (IGN BD TOPO construction_ponctuelle + OSM Overpass):
    /// - Statues (3 variants: standing figure on pedestal, equestrian statue, classical column statue)
    /// - Monuments (2 variants: obelisk / war memorial, triumphal arch)
    /// - Calvaires / wayside crosses
    /// - Fountains (octagonal basin + tiered bowl + water)
    /// - Water towers (châteaux d'eau)
    /// - Towers / Lighthouses / Pylons
    /// </summary>
    public static class LandmarkBuilder
    {
        private class MeshAcc
        {
            public readonly List<Vector3> Verts = new List<Vector3>(2048);
            public readonly List<Vector3> Normals = new List<Vector3>(2048);
            public readonly List<int> Tris = new List<int>(4096);
        }

        public static GameObject Build(
            GeoSector sector, ElevationGrid grid, List<LandmarkData> landmarks, Transform parent)
        {
            var root = new GameObject($"Landmarks_IGN_OSM ({landmarks?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (landmarks == null || landmarks.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 2f;
            float baseElev = grid.MinElevation;

            var stone = new MeshAcc();
            var marble = new MeshAcc();
            var bronze = new MeshAcc();
            var metal = new MeshAcc();
            var water = new MeshAcc();

            for (int i = 0; i < landmarks.Count; i++)
            {
                var lm = landmarks[i];
                if (Mathf.Abs(lm.LocalPos.x) > half || Mathf.Abs(lm.LocalPos.y) > half)
                    continue;

                float y = grid.SampleHeight(sector.CenterLambertX + lm.LocalPos.x, sector.CenterLambertY + lm.LocalPos.y) - baseElev;
                Vector3 pos = new Vector3(lm.LocalPos.x, y, lm.LocalPos.y);
                int variant = Mathf.Abs(Mathf.RoundToInt(lm.LocalPos.x * 13f + lm.LocalPos.y * 23f));
                float yaw = (variant % 8) * 45f * Mathf.Deg2Rad;
                Vector3 fwd = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

                switch (lm.Kind)
                {
                    case LandmarkKind.Statue:
                        BuildStatue(pos, fwd, right, variant % 3, stone, marble, bronze);
                        break;
                    case LandmarkKind.Monument:
                        BuildMonument(pos, fwd, right, variant % 2, stone, marble, bronze);
                        break;
                    case LandmarkKind.Calvaire:
                        BuildCalvaire(pos, fwd, right, stone);
                        break;
                    case LandmarkKind.Fountain:
                        BuildFountain(pos, stone, marble, water);
                        break;
                    case LandmarkKind.WaterTower:
                        BuildWaterTower(pos, stone, marble);
                        break;
                    case LandmarkKind.TowerOrPylon:
                        BuildTowerOrPylon(pos, stone, metal);
                        break;
                }
            }

            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            BuildMesh("Landmark_Stone", stone, new Color(0.68f, 0.67f, 0.64f), lit, 0.0f, 0.12f, root.transform);
            BuildMesh("Landmark_Marble", marble, new Color(0.88f, 0.87f, 0.83f), lit, 0.0f, 0.35f, root.transform);
            BuildMesh("Landmark_Bronze", bronze, new Color(0.24f, 0.34f, 0.30f), lit, 0.72f, 0.48f, root.transform);
            BuildMesh("Landmark_Metal", metal, new Color(0.72f, 0.75f, 0.78f), lit, 0.78f, 0.62f, root.transform);
            BuildMesh("Landmark_Water", water, new Color(0.18f, 0.56f, 0.74f), lit, 0.05f, 0.85f, root.transform);

            return root;
        }

        // --- Statues (3 distinct procedural archetypes) ---
        private static void BuildStatue(
            Vector3 p, Vector3 fwd, Vector3 right, int variant,
            MeshAcc stone, MeshAcc marble, MeshAcc bronze)
        {
            if (variant == 0)
            {
                // Variant A: Classical standing bronze statue on stepped stone pedestal
                AddBox(stone, p + Vector3.up * 0.25f, fwd, right, 3.2f, 0.7f, 3.2f);
                AddBox(stone, p + Vector3.up * 1.45f, fwd, right, 1.9f, 1.9f, 1.9f);
                AddBox(marble, p + Vector3.up * 2.55f, fwd, right, 2.15f, 0.30f, 2.15f);

                // Standing figure (legs/drapery, torso, shoulders, raised arm, head)
                AddCylinder(bronze, p + Vector3.up * 2.70f, 0.42f, 0.32f, 1.45f, 8);
                AddCylinder(bronze, p + Vector3.up * 4.15f, 0.36f, 0.42f, 0.95f, 8);
                AddCylinder(bronze, p + Vector3.up * 5.10f, 0.24f, 0.20f, 0.48f, 8);
                AddBox(bronze, p + Vector3.up * 4.95f + right * 0.48f, fwd, right, 0.22f, 0.85f, 0.22f);
            }
            else if (variant == 1)
            {
                // Variant B: Equestrian statue on monumental plinth
                AddBox(stone, p + Vector3.up * 0.30f, fwd, right, 4.6f, 0.8f, 3.2f);
                AddBox(stone, p + Vector3.up * 1.75f, fwd, right, 3.6f, 2.2f, 2.1f);
                AddBox(marble, p + Vector3.up * 2.95f, fwd, right, 3.85f, 0.30f, 2.35f);

                // Horse body + 4 legs + neck/head + rider
                AddBox(bronze, p + Vector3.up * 4.25f, fwd, right, 2.3f, 0.85f, 0.78f);
                AddBox(bronze, p + Vector3.up * 3.45f + fwd * 0.80f + right * 0.24f, fwd, right, 0.22f, 0.95f, 0.22f);
                AddBox(bronze, p + Vector3.up * 3.45f + fwd * 0.80f - right * 0.24f, fwd, right, 0.22f, 0.95f, 0.22f);
                AddBox(bronze, p + Vector3.up * 3.45f - fwd * 0.80f + right * 0.24f, fwd, right, 0.22f, 0.95f, 0.22f);
                AddBox(bronze, p + Vector3.up * 3.45f - fwd * 0.80f - right * 0.24f, fwd, right, 0.22f, 0.95f, 0.22f);
                AddBox(bronze, p + Vector3.up * 5.05f + fwd * 1.15f, fwd, right, 0.55f, 1.05f, 0.42f);
                AddCylinder(bronze, p + Vector3.up * 4.65f, 0.34f, 0.30f, 1.15f, 8);
                AddCylinder(bronze, p + Vector3.up * 5.80f, 0.22f, 0.18f, 0.42f, 8);
            }
            else
            {
                // Variant C: Classical fluted marble column topped with bronze figure
                AddBox(stone, p + Vector3.up * 0.35f, fwd, right, 3.0f, 0.9f, 3.0f);
                AddCylinder(marble, p + Vector3.up * 0.80f, 0.65f, 0.52f, 4.6f, 10);
                AddBox(marble, p + Vector3.up * 5.55f, fwd, right, 1.55f, 0.35f, 1.55f);
                AddCylinder(bronze, p + Vector3.up * 5.72f, 0.36f, 0.26f, 1.85f, 8);
            }
        }

        // --- Monuments (2 archetypes: Obelisk / War Memorial & Triumphal Arch) ---
        private static void BuildMonument(
            Vector3 p, Vector3 fwd, Vector3 right, int variant,
            MeshAcc stone, MeshAcc marble, MeshAcc bronze)
        {
            if (variant == 0)
            {
                // Variant A: Obelisk / Monument aux Morts
                AddBox(stone, p + Vector3.up * 0.25f, fwd, right, 4.4f, 0.7f, 4.4f);
                AddBox(stone, p + Vector3.up * 0.90f, fwd, right, 3.4f, 0.7f, 3.4f);
                AddCylinder(marble, p + Vector3.up * 1.25f, 1.05f, 0.52f, 6.8f, 4);
                AddCylinder(bronze, p + Vector3.up * 8.05f, 0.52f, 0.04f, 1.3f, 4);
                AddBox(bronze, p + Vector3.up * 2.4f + fwd * 0.95f, fwd, right, 0.14f, 1.1f, 0.85f);
            }
            else
            {
                // Variant B: Memorial / Triumphal Arch
                AddBox(stone, p + Vector3.up * 0.20f, fwd, right, 2.8f, 0.6f, 7.6f);
                AddBox(stone, p + Vector3.up * 3.2f + right * 2.4f, fwd, right, 2.3f, 5.8f, 2.2f);
                AddBox(stone, p + Vector3.up * 3.2f - right * 2.4f, fwd, right, 2.3f, 5.8f, 2.2f);
                AddBox(marble, p + Vector3.up * 6.8f, fwd, right, 2.5f, 1.8f, 7.2f);
                AddBox(stone, p + Vector3.up * 7.85f, fwd, right, 2.8f, 0.45f, 7.6f);
            }
        }

        // --- Calvaire / Wayside Cross ---
        private static void BuildCalvaire(Vector3 p, Vector3 fwd, Vector3 right, MeshAcc stone)
        {
            AddBox(stone, p + Vector3.up * 0.22f, fwd, right, 2.2f, 0.65f, 2.2f);
            AddBox(stone, p + Vector3.up * 0.85f, fwd, right, 1.3f, 0.75f, 1.3f);
            AddCylinder(stone, p + Vector3.up * 1.20f, 0.24f, 0.16f, 3.6f, 6);
            AddBox(stone, p + Vector3.up * 4.15f, fwd, right, 0.24f, 0.28f, 1.65f);
        }

        // --- Fountain ---
        private static void BuildFountain(Vector3 p, MeshAcc stone, MeshAcc marble, MeshAcc water)
        {
            AddCylinder(stone, p + Vector3.down * 0.2f, 3.4f, 3.4f, 1.1f, 8);
            AddCylinder(water, p + Vector3.up * 0.78f, 2.95f, 2.95f, 0.15f, 8);
            AddCylinder(marble, p + Vector3.up * 0.85f, 0.65f, 0.42f, 2.0f, 8);
            AddCylinder(marble, p + Vector3.up * 2.65f, 1.35f, 1.55f, 0.40f, 8);
            AddCylinder(marble, p + Vector3.up * 3.05f, 0.32f, 0.18f, 0.95f, 8);
        }

        // --- French Water Tower (Château d'eau) ---
        private static void BuildWaterTower(Vector3 p, MeshAcc stone, MeshAcc marble)
        {
            AddCylinder(stone, p + Vector3.down * 1.5f, 3.6f, 3.1f, 15.5f, 12);
            AddCylinder(marble, p + Vector3.up * 14.0f, 3.1f, 6.4f, 4.2f, 14);
            AddCylinder(marble, p + Vector3.up * 18.2f, 6.4f, 6.4f, 5.0f, 14);
            AddCylinder(stone, p + Vector3.up * 23.2f, 6.5f, 1.8f, 2.2f, 14);
        }

        // --- Tower / Lighthouse / Antenna Pylon ---
        private static void BuildTowerOrPylon(Vector3 p, MeshAcc stone, MeshAcc metal)
        {
            AddCylinder(stone, p + Vector3.down * 1.0f, 2.2f, 1.5f, 5.0f, 8);
            AddCylinder(metal, p + Vector3.up * 4.0f, 0.95f, 0.22f, 18.0f, 8);
            AddCylinder(metal, p + Vector3.up * 22.0f, 1.2f, 1.2f, 0.35f, 8);
        }

        private static void AddBox(
            MeshAcc acc, Vector3 center, Vector3 fwd, Vector3 right,
            float sizeF, float sizeU, float sizeR)
        {
            Vector3 hf = fwd * (sizeF * 0.5f);
            Vector3 hu = Vector3.up * (sizeU * 0.5f);
            Vector3 hr = right * (sizeR * 0.5f);

            Vector3 p000 = center - hf - hu - hr;
            Vector3 p100 = center + hf - hu - hr;
            Vector3 p110 = center + hf + hu - hr;
            Vector3 p010 = center - hf + hu - hr;
            Vector3 p001 = center - hf - hu + hr;
            Vector3 p101 = center + hf - hu + hr;
            Vector3 p111 = center + hf + hu + hr;
            Vector3 p011 = center - hf + hu + hr;

            AddQuad(acc, p010, p110, p111, p011, Vector3.up);
            AddQuad(acc, p001, p101, p100, p000, Vector3.down);
            AddQuad(acc, p100, p110, p111, p101, fwd);
            AddQuad(acc, p001, p011, p010, p000, -fwd);
            AddQuad(acc, p001, p101, p111, p011, right);
            AddQuad(acc, p100, p000, p010, p110, -right);
        }

        private static void AddCylinder(
            MeshAcc acc, Vector3 baseCenter, float radiusBot, float radiusTop, float height, int sides)
        {
            Vector3 topCenter = baseCenter + Vector3.up * height;
            float step = Mathf.PI * 2f / sides;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                Vector3 d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));

                Vector3 b0 = baseCenter + d0 * radiusBot;
                Vector3 b1 = baseCenter + d1 * radiusBot;
                Vector3 t0 = topCenter + d0 * radiusTop;
                Vector3 t1 = topCenter + d1 * radiusTop;

                Vector3 sideN = (d0 + d1).normalized;
                AddQuad(acc, b0, t0, t1, b1, sideN);

                // Top cap triangle
                if (radiusTop > 0.05f)
                {
                    int idx = acc.Verts.Count;
                    acc.Verts.Add(topCenter); acc.Verts.Add(t1); acc.Verts.Add(t0);
                    acc.Normals.Add(Vector3.up); acc.Normals.Add(Vector3.up); acc.Normals.Add(Vector3.up);
                    acc.Tris.Add(idx); acc.Tris.Add(idx + 1); acc.Tris.Add(idx + 2);
                }
            }
        }

        private static void AddQuad(MeshAcc acc, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            Vector3 computed = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(computed, n) < 0f)
                (b, d) = (d, b);

            int idx = acc.Verts.Count;
            acc.Verts.Add(a); acc.Verts.Add(b); acc.Verts.Add(c); acc.Verts.Add(d);
            acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 1); acc.Tris.Add(idx + 2);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 2); acc.Tris.Add(idx + 3);
        }

        private static void BuildMesh(
            string name, MeshAcc acc, Color color, Shader shader,
            float metallic, float smoothness, Transform parent)
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
            mat.name = $"{name}_Mat";
            mat.SetColor("_BaseColor", color);
            if (metallic > 0f) mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }
    }
}

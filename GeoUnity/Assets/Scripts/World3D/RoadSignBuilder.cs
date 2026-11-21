using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Places batched 3D traffic signals, Stop signs, and Give Way signs from OpenStreetMap Overpass data.
    /// </summary>
    public static class RoadSignBuilder
    {
        private class MeshAcc
        {
            public readonly List<Vector3> Verts = new List<Vector3>(2048);
            public readonly List<Vector3> Normals = new List<Vector3>(2048);
            public readonly List<int> Tris = new List<int>(4096);
        }

        public static GameObject Build(GeoSector sector, ElevationGrid grid, List<RoadSignData> signs, Transform parent)
        {
            var root = new GameObject($"RoadSigns_OSM ({signs?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (signs == null || signs.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 2f;
            float baseElev = grid.MinElevation;

            var poles = new MeshAcc();
            var housings = new MeshAcc();
            var redLights = new MeshAcc();
            var amberLights = new MeshAcc();
            var greenLights = new MeshAcc();
            var whiteSigns = new MeshAcc();

            for (int i = 0; i < signs.Count; i++)
            {
                var s = signs[i];
                if (Mathf.Abs(s.LocalPos.x) > half || Mathf.Abs(s.LocalPos.y) > half) continue;

                float y = grid.SampleHeight(sector.CenterLambertX + s.LocalPos.x, sector.CenterLambertY + s.LocalPos.y) - baseElev;
                Vector3 basePos = new Vector3(s.LocalPos.x, y, s.LocalPos.y);

                switch (s.Kind)
                {
                    case RoadSignKind.TrafficSignal:
                        AddBox(poles, basePos + new Vector3(0f, 2.3f, 0f), new Vector3(0.22f, 4.6f, 0.22f));
                        AddBox(housings, basePos + new Vector3(0f, 4.2f, 0f), new Vector3(0.55f, 1.45f, 0.45f));
                        AddBox(redLights, basePos + new Vector3(0f, 4.62f, 0f), new Vector3(0.32f, 0.28f, 0.50f));
                        AddBox(amberLights, basePos + new Vector3(0f, 4.20f, 0f), new Vector3(0.32f, 0.28f, 0.50f));
                        AddBox(greenLights, basePos + new Vector3(0f, 3.78f, 0f), new Vector3(0.32f, 0.28f, 0.50f));
                        break;

                    case RoadSignKind.Stop:
                        AddBox(poles, basePos + new Vector3(0f, 1.35f, 0f), new Vector3(0.14f, 2.7f, 0.14f));
                        AddBox(redLights, basePos + new Vector3(0f, 2.85f, 0f), new Vector3(0.95f, 0.95f, 0.12f));
                        break;

                    case RoadSignKind.GiveWay:
                        AddBox(poles, basePos + new Vector3(0f, 1.35f, 0f), new Vector3(0.14f, 2.7f, 0.14f));
                        AddBox(whiteSigns, basePos + new Vector3(0f, 2.80f, 0f), new Vector3(0.90f, 0.82f, 0.12f));
                        AddBox(redLights, basePos + new Vector3(0f, 3.22f, 0f), new Vector3(0.98f, 0.14f, 0.14f));
                        break;
                }
            }

            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;
            var unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? lit;

            BuildMesh("SignPoles", poles, new Color(0.68f, 0.72f, 0.76f), lit, 0.75f, 0.7f, root.transform);
            BuildMesh("SignalHousings", housings, new Color(0.14f, 0.15f, 0.17f), lit, 0.2f, 0.35f, root.transform);
            BuildMesh("SignRed", redLights, new Color(1.4f, 0.12f, 0.10f), unlit, 0f, 0f, root.transform);
            BuildMesh("SignAmber", amberLights, new Color(1.5f, 0.85f, 0.08f), unlit, 0f, 0f, root.transform);
            BuildMesh("SignGreen", greenLights, new Color(0.10f, 1.35f, 0.35f), unlit, 0f, 0f, root.transform);
            BuildMesh("SignWhite", whiteSigns, new Color(0.96f, 0.96f, 0.98f), lit, 0.05f, 0.3f, root.transform);

            return root;
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

        private static void AddFace(MeshAcc acc, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            int idx = acc.Verts.Count;
            acc.Verts.Add(a); acc.Verts.Add(b); acc.Verts.Add(c); acc.Verts.Add(d);
            acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n); acc.Normals.Add(n);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 1); acc.Tris.Add(idx + 2);
            acc.Tris.Add(idx); acc.Tris.Add(idx + 2); acc.Tris.Add(idx + 3);
        }

        private static void BuildMesh(string name, MeshAcc acc, Color color, Shader shader, float metallic, float smoothness, Transform parent)
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
            if (metallic > 0f) mat.SetFloat("_Metallic", metallic);
            if (smoothness > 0f) mat.SetFloat("_Smoothness", smoothness);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }
    }
}

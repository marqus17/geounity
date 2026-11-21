using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Builds clipped, terrain-conforming 3D water surfaces from IGN BD TOPO surface_hydrographique polygons.
    /// Uses Sutherland-Hodgman clipping so rivers and lakes stay strictly inside the 2 km x 2 km diorama.
    /// </summary>
    public static class WaterMeshBuilder
    {
        public static GameObject Build(GeoSector sector, ElevationGrid grid, List<WaterSurfaceData> surfaces, Transform parent)
        {
            var root = new GameObject($"Water_BDTOPO ({surfaces?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (surfaces == null || surfaces.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 0.5f;
            float baseElev = grid.MinElevation;

            var verts = new List<Vector3>(4096);
            var normals = new List<Vector3>(4096);
            var uvs = new List<Vector2>(4096);
            var tris = new List<int>(8192);

            for (int s = 0; s < surfaces.Count; s++)
            {
                var rawRing = surfaces[s].OuterRingLocal;
                if (rawRing == null || rawRing.Count < 3) continue;

                // Clean duplicate closing vertex
                var ring = new List<Vector2>(rawRing.Count);
                for (int i = 0; i < rawRing.Count; i++)
                {
                    Vector2 p = rawRing[i];
                    if (ring.Count > 0 && (p - ring[ring.Count - 1]).sqrMagnitude < 0.04f) continue;
                    ring.Add(p);
                }
                if (ring.Count > 1 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 0.04f)
                    ring.RemoveAt(ring.Count - 1);
                if (ring.Count < 3) continue;

                // Clip polygon strictly to [-half, +half] x [-half, +half]
                var clipped = ClipToSquare(ring, -half, half, -half, half);
                if (clipped.Count < 3) continue;

                if (EarCut2D.SignedArea(clipped) < 0f)
                    clipped.Reverse();

                var polyTris = EarCut2D.Triangulate(clipped);
                if (polyTris.Count < 3) continue;

                int vStart = verts.Count;
                for (int i = 0; i < clipped.Count; i++)
                {
                    Vector2 p = clipped[i];
                    float y = grid.SampleHeight(sector.CenterLambertX + p.x, sector.CenterLambertY + p.y) - baseElev + 0.45f;
                    verts.Add(new Vector3(p.x, y, p.y));
                    normals.Add(Vector3.up);
                    uvs.Add(new Vector2(p.x / 40f, p.y / 40f));
                }

                for (int i = 0; i < polyTris.Count; i += 3)
                {
                    tris.Add(vStart + polyTris[i]);
                    tris.Add(vStart + polyTris[i + 2]);
                    tris.Add(vStart + polyTris[i + 1]);
                }
            }

            if (tris.Count == 0)
                return root;

            var mesh = new Mesh
            {
                name = "WaterSurfacesMesh",
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshCollider>().sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;

            var waterShader = Shader.Find("GeoUnity/Water");
            if (waterShader == null)
                waterShader = Shader.Find("Universal Render Pipeline/Lit");
            if (waterShader == null)
                waterShader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            var mat = new Material(waterShader);
            mat.name = "WaterSurfaceMaterial";
            if (waterShader.name == "GeoUnity/Water")
            {
                mat.SetColor("_ShallowColor", new Color(0.09f, 0.44f, 0.54f, 0.60f));
                mat.SetColor("_DeepColor", new Color(0.04f, 0.21f, 0.36f, 0.84f));
                mat.SetColor("_SkyColor", new Color(0.58f, 0.78f, 0.96f, 1f));
                mat.SetFloat("_WaveSpeed", 0.50f);
                mat.SetFloat("_WaveScale", 0.14f);
                mat.SetFloat("_WaveStrength", 0.09f);
                mat.SetFloat("_SpecularPower", 96f);
            }
            else
            {
                mat.SetColor("_BaseColor", new Color(0.07f, 0.27f, 0.40f, 1f));
                mat.SetFloat("_Smoothness", 0.92f);
                mat.SetFloat("_Metallic", 0.18f);
            }
            mr.sharedMaterial = mat;

            return root;
        }

        // Sutherland-Hodgman polygon clipping against an axis-aligned rectangle
        private static List<Vector2> ClipToSquare(List<Vector2> input, float minX, float maxX, float minY, float maxY)
        {
            var poly = ClipEdge(input, p => p.x >= minX, (a, b) => IntersectX(a, b, minX));
            poly = ClipEdge(poly, p => p.x <= maxX, (a, b) => IntersectX(a, b, maxX));
            poly = ClipEdge(poly, p => p.y >= minY, (a, b) => IntersectY(a, b, minY));
            poly = ClipEdge(poly, p => p.y <= maxY, (a, b) => IntersectY(a, b, maxY));
            return poly;
        }

        private static List<Vector2> ClipEdge(
            List<Vector2> input,
            System.Func<Vector2, bool> isInside,
            System.Func<Vector2, Vector2, Vector2> intersect)
        {
            var output = new List<Vector2>(input.Count);
            if (input.Count == 0) return output;

            Vector2 prev = input[input.Count - 1];
            bool prevIn = isInside(prev);

            for (int i = 0; i < input.Count; i++)
            {
                Vector2 curr = input[i];
                bool currIn = isInside(curr);

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

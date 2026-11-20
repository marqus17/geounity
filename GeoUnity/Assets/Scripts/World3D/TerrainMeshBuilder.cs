using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Builds a centered 3D terrain mesh from an IGN RGE ALTI ElevationGrid and drapes the Orthophoto texture.
    /// Includes a dark diorama skirt around the 4 borders.
    /// </summary>
    public static class TerrainMeshBuilder
    {
        public static GameObject Build(GeoSector sector, ElevationGrid grid, Texture2D orthophoto, Transform parent)
        {
            var root = new GameObject("Terrain_RGE_ALTI");
            root.transform.SetParent(parent, false);

            int w = grid.Width;
            int h = grid.Height;
            float size = (float)sector.SizeMeters;
            float half = size * 0.5f;
            float baseElev = grid.MinElevation;

            // --- 1. Terrain Surface Mesh ---
            var verts = new Vector3[w * h];
            var uvs = new Vector2[w * h];
            var tris = new int[(w - 1) * (h - 1) * 6];

            for (int row = 0; row < h; row++)
            {
                float v = 1f - (float)row / (h - 1); // row 0 = North (+Z, UV v=1), row h-1 = South (-Z, UV v=0)
                float z = Mathf.Lerp(-half, half, v);

                for (int col = 0; col < w; col++)
                {
                    float u = (float)col / (w - 1); // col 0 = West (-X, UV u=0), col w-1 = East (+X, UV u=1)
                    float x = Mathf.Lerp(-half, half, u);
                    float y = grid.Heights[row * w + col] - baseElev;

                    int idx = row * w + col;
                    verts[idx] = new Vector3(x, y, z);
                    uvs[idx] = new Vector2(u, v);
                }
            }

            int t = 0;
            for (int row = 0; row < h - 1; row++)
            {
                for (int col = 0; col < w - 1; col++)
                {
                    int i00 = row * w + col;
                    int i10 = i00 + 1;
                    int i01 = i00 + w;
                    int i11 = i01 + 1;

                    // Clockwise winding when viewed from +Y (Unity front-face up)
                    tris[t++] = i00;
                    tris[t++] = i10;
                    tris[t++] = i01;

                    tris[t++] = i10;
                    tris[t++] = i11;
                    tris[t++] = i01;
                }
            }

            var mesh = new Mesh
            {
                name = "TerrainSurfaceMesh",
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var mf = root.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>();

            var urpShader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(urpShader != null ? urpShader : GraphicsSettings.currentRenderPipeline.defaultMaterial.shader);
            mat.name = "TerrainOrthoMaterial";
            if (orthophoto != null)
            {
                mat.SetTexture("_BaseMap", orthophoto);
                mat.SetColor("_BaseColor", Color.white);
            }
            else
            {
                mat.SetColor("_BaseColor", new Color(0.38f, 0.44f, 0.34f, 1f));
            }
            mat.SetFloat("_Smoothness", 0.05f);
            mr.sharedMaterial = mat;

            // --- 2. Diorama Skirt ---
            BuildSkirt(verts, w, h, -40f, root.transform, urpShader);

            return root;
        }

        private static void BuildSkirt(Vector3[] surfaceVerts, int w, int h, float bottomY, Transform parent, Shader urpShader)
        {
            var skirtGo = new GameObject("TerrainSkirt");
            skirtGo.transform.SetParent(parent, false);

            var vList = new System.Collections.Generic.List<Vector3>();
            var tList = new System.Collections.Generic.List<int>();

            void AddEdgeStrip(int[] edgeIndices, bool flip)
            {
                int start = vList.Count;
                for (int i = 0; i < edgeIndices.Length; i++)
                {
                    Vector3 top = surfaceVerts[edgeIndices[i]];
                    vList.Add(top);
                    vList.Add(new Vector3(top.x, bottomY, top.z));
                }

                for (int i = 0; i < edgeIndices.Length - 1; i++)
                {
                    int a = start + i * 2;
                    int b = a + 1;
                    int c = a + 2;
                    int d = a + 3;

                    if (!flip)
                    {
                        tList.Add(a); tList.Add(b); tList.Add(c);
                        tList.Add(c); tList.Add(b); tList.Add(d);
                    }
                    else
                    {
                        tList.Add(a); tList.Add(c); tList.Add(b);
                        tList.Add(c); tList.Add(d); tList.Add(b);
                    }
                }
            }

            // North edge (row 0, col 0..w-1)
            var north = new int[w];
            var south = new int[w];
            for (int col = 0; col < w; col++)
            {
                north[col] = col;
                south[col] = (h - 1) * w + col;
            }
            var west = new int[h];
            var east = new int[h];
            for (int row = 0; row < h; row++)
            {
                west[row] = row * w;
                east[row] = row * w + (w - 1);
            }

            AddEdgeStrip(north, true);
            AddEdgeStrip(south, false);
            AddEdgeStrip(west, false);
            AddEdgeStrip(east, true);

            var mesh = new Mesh { name = "TerrainSkirtMesh", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vList);
            mesh.SetTriangles(tList, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            skirtGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = skirtGo.AddComponent<MeshRenderer>();
            var mat = new Material(urpShader != null ? urpShader : GraphicsSettings.currentRenderPipeline.defaultMaterial.shader);
            mat.name = "DioramaSkirtMaterial";
            mat.SetColor("_BaseColor", new Color(0.14f, 0.16f, 0.19f, 1f));
            mat.SetFloat("_Smoothness", 0.1f);
            mr.sharedMaterial = mat;
        }
    }
}

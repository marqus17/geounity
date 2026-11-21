using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Fast in-memory road network query for Car mode (spawn + nearest drivable road point).
    /// </summary>
    public class RoadNetwork
    {
        public struct Segment
        {
            public Vector2 A;
            public Vector2 B;
            public float HalfWidth;
        }

        private readonly List<Segment> _segments = new List<Segment>(2048);
        public int SegmentCount => _segments.Count;

        public void AddPolyline(List<Vector2> pts, float widthMeters, float clipHalf)
        {
            if (pts == null || pts.Count < 2) return;
            float hw = Mathf.Max(2.5f, widthMeters * 0.5f);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[i + 1];
                if ((b - a).sqrMagnitude < 0.25f) continue;

                // Keep segments that touch the playable sector
                if (Mathf.Max(Mathf.Abs(a.x), Mathf.Abs(a.y)) > clipHalf + 20f &&
                    Mathf.Max(Mathf.Abs(b.x), Mathf.Abs(b.y)) > clipHalf + 20f)
                    continue;

                _segments.Add(new Segment { A = a, B = b, HalfWidth = hw });
            }
        }

        public bool TryFindNearest(Vector2 pos, out Vector2 closest, out Vector2 tangent, out float distance, out float allowedHalfWidth)
        {
            closest = Vector2.zero;
            tangent = Vector2.up;
            distance = float.MaxValue;
            allowedHalfWidth = 4f;

            if (_segments.Count == 0) return false;

            float bestDistSq = float.MaxValue;
            int bestIdx = -1;
            Vector2 bestPt = Vector2.zero;

            // shortcut: linear scan over <= ~6000 segments (<0.1ms/frame); upgrade to 2D grid if sector size grows
            for (int i = 0; i < _segments.Count; i++)
            {
                Segment s = _segments[i];
                Vector2 ab = s.B - s.A;
                float lenSq = ab.sqrMagnitude;
                float t = lenSq > 1e-5f ? Mathf.Clamp01(Vector2.Dot(pos - s.A, ab) / lenSq) : 0f;
                Vector2 pt = s.A + ab * t;
                float dSq = (pos - pt).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                    bestPt = pt;
                }
            }

            if (bestIdx < 0) return false;

            Segment best = _segments[bestIdx];
            closest = bestPt;
            tangent = (best.B - best.A).normalized;
            distance = Mathf.Sqrt(bestDistSq);
            allowedHalfWidth = best.HalfWidth;
            return true;
        }
    }

    /// <summary>
    /// Builds a semi-transparent draped ribbon mesh for IGN BD TOPO roads and populates a RoadNetwork.
    /// </summary>
    public static class RoadOverlayBuilder
    {
        public static GameObject Build(
            GeoSector sector,
            ElevationGrid grid,
            List<RoadSegmentData> roads,
            Transform parent,
            out RoadNetwork network)
        {
            network = new RoadNetwork();
            var root = new GameObject($"Roads_BDTOPO ({roads?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (roads == null || roads.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 2f;
            float baseElev = grid.MinElevation;

            var verts = new List<Vector3>(16384);
            var uvs = new List<Vector2>(16384);
            var tris = new List<int>(32768);

            for (int r = 0; r < roads.Count; r++)
            {
                var road = roads[r];
                var pts = road.PointsLocal;
                if (pts == null || pts.Count < 2) continue;

                network.AddPolyline(pts, road.WidthMeters, half);

                float hw = Mathf.Clamp(road.WidthMeters * 0.5f, 2.2f, 12f);
                float distAlong = 0f;

                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = pts[i];
                    Vector2 b = pts[i + 1];

                    // Skip segments completely outside the 2 km x 2 km square
                    if ((a.x < -half && b.x < -half) || (a.x > half && b.x > half) ||
                        (a.y < -half && b.y < -half) || (a.y > half && b.y > half))
                        continue;

                    a.x = Mathf.Clamp(a.x, -half, half);
                    a.y = Mathf.Clamp(a.y, -half, half);
                    b.x = Mathf.Clamp(b.x, -half, half);
                    b.y = Mathf.Clamp(b.y, -half, half);

                    Vector2 seg = b - a;
                    float segLen = seg.magnitude;
                    if (segLen < 0.3f) continue;

                    Vector2 perp = new Vector2(-seg.y, seg.x) / segLen * hw;
                    Vector2 aL = a + perp, aR = a - perp;
                    Vector2 bL = b + perp, bR = b - perp;

                    float yAL = grid.SampleHeight(sector.CenterLambertX + aL.x, sector.CenterLambertY + aL.y) - baseElev + 0.35f;
                    float yAR = grid.SampleHeight(sector.CenterLambertX + aR.x, sector.CenterLambertY + aR.y) - baseElev + 0.35f;
                    float yBL = grid.SampleHeight(sector.CenterLambertX + bL.x, sector.CenterLambertY + bL.y) - baseElev + 0.35f;
                    float yBR = grid.SampleHeight(sector.CenterLambertX + bR.x, sector.CenterLambertY + bR.y) - baseElev + 0.35f;

                    int vStart = verts.Count;
                    verts.Add(new Vector3(aL.x, yAL, aL.y));
                    verts.Add(new Vector3(aR.x, yAR, aR.y));
                    verts.Add(new Vector3(bR.x, yBR, bR.y));
                    verts.Add(new Vector3(bL.x, yBL, bL.y));

                    uvs.Add(new Vector2(0f, distAlong));
                    uvs.Add(new Vector2(1f, distAlong));
                    uvs.Add(new Vector2(1f, distAlong + segLen));
                    uvs.Add(new Vector2(0f, distAlong + segLen));

                    tris.Add(vStart); tris.Add(vStart + 2); tris.Add(vStart + 1);
                    tris.Add(vStart); tris.Add(vStart + 3); tris.Add(vStart + 2);

                    distAlong += segLen;
                }
            }

            if (tris.Count == 0)
                return root;

            var mesh = new Mesh { name = "RoadOverlayMesh", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;

            var shader = Shader.Find("GeoUnity/RoadOverlay");
            if (shader == null)
                shader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            var mat = new Material(shader);
            mat.name = "RoadOverlayMaterial";
            mat.SetColor("_BaseColor", new Color(0.16f, 0.76f, 0.96f, 0.34f));
            mr.sharedMaterial = mat;

            return root;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Robust 2D ear-clipping triangulator for concave building footprints.
    /// Returns triangle index triples (a, b, c) in counter-clockwise order (XZ plane -> upward Y normal).
    /// </summary>
    public static class EarCut2D
    {
        public static List<int> Triangulate(List<Vector2> polygon)
        {
            var indices = new List<int>();
            int n = polygon.Count;
            if (n < 3) return indices;

            // Build working index list in counter-clockwise order
            var V = new List<int>(n);
            if (SignedArea(polygon) > 0f)
            {
                for (int i = 0; i < n; i++) V.Add(i);
            }
            else
            {
                for (int i = 0; i < n; i++) V.Add(n - 1 - i);
            }

            int nv = n;
            int guard = 2 * nv;

            for (int v = nv - 1; nv > 2;)
            {
                if (guard-- <= 0)
                    break; // Degenerate/self-intersecting polygon safety exit

                int u = v;
                if (nv <= u) u = 0;
                v = u + 1;
                if (nv <= v) v = 0;
                int w = v + 1;
                if (nv <= w) w = 0;

                if (Snip(polygon, u, v, w, nv, V))
                {
                    int a = V[u];
                    int b = V[v];
                    int c = V[w];
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);

                    V.RemoveAt(v);
                    nv--;
                    guard = 2 * nv;
                }
            }

            return indices;
        }

        public static float SignedArea(List<Vector2> poly)
        {
            float area = 0f;
            int n = poly.Count;
            for (int p = n - 1, q = 0; q < n; p = q++)
            {
                area += poly[p].x * poly[q].y - poly[q].x * poly[p].y;
            }
            return area * 0.5f;
        }

        private static bool Snip(List<Vector2> poly, int u, int v, int w, int n, List<int> V)
        {
            Vector2 A = poly[V[u]];
            Vector2 B = poly[V[v]];
            Vector2 C = poly[V[w]];

            if (((B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x)) < 1e-5f)
                return false;

            for (int p = 0; p < n; p++)
            {
                if (p == u || p == v || p == w) continue;
                Vector2 P = poly[V[p]];
                if (PointInTriangle(A, B, C, P))
                    return false;
            }
            return true;
        }

        private static bool PointInTriangle(Vector2 A, Vector2 B, Vector2 C, Vector2 P)
        {
            float ax = C.x - B.x, ay = C.y - B.y;
            float bx = A.x - C.x, by = A.y - C.y;
            float cx = B.x - A.x, cy = B.y - A.y;

            float apx = P.x - A.x, apy = P.y - A.y;
            float bpx = P.x - B.x, bpy = P.y - B.y;
            float cpx = P.x - C.x, cpy = P.y - C.y;

            float aCrossBP = ax * bpy - ay * bpx;
            float cCrossAP = cx * apy - cy * apx;
            float bCrossCP = bx * cpy - by * cpx;

            return aCrossBP >= 0f && bCrossCP >= 0f && cCrossAP >= 0f;
        }
    }
}

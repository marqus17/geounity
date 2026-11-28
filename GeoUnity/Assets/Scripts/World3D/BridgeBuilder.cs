using System;
using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Generates batched 3D civil-engineering bridge structures from IGN BD TOPO
    /// (construction_lineaire & construction_surfacique) using 4 procedural base models:
    /// 1. StoneArch (pont en pierre à arches)
    /// 2. ConcreteGirder (pont / viaduc en béton à piles)
    /// 3. SteelTruss (pont métallique à treillis)
    /// 4. Footbridge (passerelle piétonne en arc léger)
    /// </summary>
    public static class BridgeBuilder
    {
        private enum BridgeStyle
        {
            StoneArch,
            ConcreteGirder,
            SteelTruss,
            Footbridge
        }

        private class MeshAcc
        {
            public readonly List<Vector3> Verts = new List<Vector3>(4096);
            public readonly List<Vector3> Normals = new List<Vector3>(4096);
            public readonly List<int> Tris = new List<int>(8192);
        }

        public static GameObject Build(
            GeoSector sector, ElevationGrid grid, List<BridgeData> bridges, Transform parent)
        {
            var root = new GameObject($"Bridges_BDTOPO ({bridges?.Count ?? 0})");
            root.transform.SetParent(parent, false);

            if (bridges == null || bridges.Count == 0)
                return root;

            float half = (float)(sector.SizeMeters * 0.5) - 1f;
            float baseElev = grid.MinElevation;

            var asphalt = new MeshAcc();
            var stone = new MeshAcc();
            var concrete = new MeshAcc();
            var steel = new MeshAcc();
            var wood = new MeshAcc();

            for (int i = 0; i < bridges.Count; i++)
            {
                var b = bridges[i];
                if (b.CenterLineLocal == null || b.CenterLineLocal.Count < 2) continue;

                // Use start and end of polyline (or segment-by-segment for curved bridges)
                Vector2 pStart = b.CenterLineLocal[0];
                Vector2 pEnd = b.CenterLineLocal[b.CenterLineLocal.Count - 1];
                Vector2 mid = (pStart + pEnd) * 0.5f;

                if (Mathf.Abs(mid.x) > half + 40f || Mathf.Abs(mid.y) > half + 40f)
                    continue;

                float totalLength = Vector2.Distance(pStart, pEnd);
                if (totalLength < 4f || totalLength > 900f) continue;

                Vector2 dir2 = (pEnd - pStart) / totalLength;
                Vector2 perp2 = new Vector2(-dir2.y, dir2.x);
                Vector3 fwd = new Vector3(dir2.x, 0f, dir2.y);
                Vector3 right = new Vector3(perp2.x, 0f, perp2.y);

                float width = Mathf.Clamp(b.WidthMeters, 2.8f, 34f);

                // Sample ground at endpoints and midpoint
                float yStartG = grid.SampleHeight(sector.CenterLambertX + pStart.x, sector.CenterLambertY + pStart.y) - baseElev;
                float yEndG = grid.SampleHeight(sector.CenterLambertX + pEnd.x, sector.CenterLambertY + pEnd.y) - baseElev;
                float yMidG = grid.SampleHeight(sector.CenterLambertX + mid.x, sector.CenterLambertY + mid.y) - baseElev;

                // Determine deck height (honor IGN 3D altitude when sensible, else lift cleanly over terrain/water)
                float deckStartY = yStartG + 0.65f;
                float deckEndY = yEndG + 0.65f;
                if (b.Altitudes != null && b.Altitudes.Count >= 2)
                {
                    float a0 = b.Altitudes[0];
                    float a1 = b.Altitudes[b.Altitudes.Count - 1];
                    if (!float.IsNaN(a0))
                    {
                        float rel = a0 - baseElev;
                        if (rel >= yStartG - 1f && rel <= yStartG + 60f) deckStartY = Mathf.Max(deckStartY, rel);
                    }
                    if (!float.IsNaN(a1))
                    {
                        float rel = a1 - baseElev;
                        if (rel >= yEndG - 1f && rel <= yEndG + 60f) deckEndY = Mathf.Max(deckEndY, rel);
                    }
                }

                float minMidClearance = Mathf.Clamp(totalLength * 0.045f, 1.4f, 6.5f);
                float baseMidDeck = Mathf.Max((deckStartY + deckEndY) * 0.5f, yMidG + minMidClearance);
                float camber = Mathf.Max(0f, baseMidDeck - (deckStartY + deckEndY) * 0.5f);

                BridgeStyle style = PickBridgeStyle(b, totalLength, width, mid);

                switch (style)
                {
                    case BridgeStyle.Footbridge:
                        BuildFootbridge(pStart, fwd, right, totalLength, width, deckStartY, deckEndY, camber, yMidG, wood, steel, concrete);
                        break;
                    case BridgeStyle.StoneArch:
                        BuildStoneArchBridge(pStart, fwd, right, totalLength, width, deckStartY, deckEndY, camber, yMidG, asphalt, stone);
                        break;
                    case BridgeStyle.SteelTruss:
                        BuildSteelTrussBridge(pStart, fwd, right, totalLength, width, deckStartY, deckEndY, camber, yMidG, asphalt, steel, concrete);
                        break;
                    default:
                        BuildConcreteGirderBridge(pStart, fwd, right, totalLength, width, deckStartY, deckEndY, camber, yMidG, asphalt, concrete, steel);
                        break;
                }
            }

            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            BuildMesh("Bridge_Asphalt", asphalt, new Color(0.24f, 0.25f, 0.27f), lit, 0.0f, 0.14f, root.transform);
            BuildMesh("Bridge_Stone", stone, new Color(0.76f, 0.72f, 0.64f), lit, 0.0f, 0.12f, root.transform);
            BuildMesh("Bridge_Concrete", concrete, new Color(0.74f, 0.75f, 0.76f), lit, 0.0f, 0.18f, root.transform);
            BuildMesh("Bridge_Steel", steel, new Color(0.32f, 0.38f, 0.44f), lit, 0.65f, 0.55f, root.transform);
            BuildMesh("Bridge_Wood", wood, new Color(0.54f, 0.39f, 0.26f), lit, 0.0f, 0.15f, root.transform);

            return root;
        }

        private static BridgeStyle PickBridgeStyle(BridgeData b, float length, float width, Vector2 mid)
        {
            string info = $"{b.Nature} {b.DetailedNature} {b.Name}";

            if (width <= 4.5f || info.Contains("Passerelle", StringComparison.OrdinalIgnoreCase))
                return BridgeStyle.Footbridge;

            if (info.Contains("Aqueduc", StringComparison.OrdinalIgnoreCase) ||
                info.Contains("Vieux", StringComparison.OrdinalIgnoreCase) ||
                info.Contains("Neuf", StringComparison.OrdinalIgnoreCase))
                return BridgeStyle.StoneArch;

            if (info.Contains("Viaduc", StringComparison.OrdinalIgnoreCase))
                return length > 140f ? BridgeStyle.ConcreteGirder : BridgeStyle.StoneArch;

            if (info.Contains("Ferroviaire", StringComparison.OrdinalIgnoreCase) ||
                info.Contains("Métallique", StringComparison.OrdinalIgnoreCase))
                return BridgeStyle.SteelTruss;

            // Deterministic variety by world position & span length
            int hash = Mathf.Abs(Mathf.RoundToInt(mid.x * 17f + mid.y * 29f)) % 10;
            if (length < 28f)
                return hash < 6 ? BridgeStyle.StoneArch : BridgeStyle.ConcreteGirder;
            if (length < 95f)
                return hash < 4 ? BridgeStyle.StoneArch : (hash < 7 ? BridgeStyle.ConcreteGirder : BridgeStyle.SteelTruss);

            return hash < 6 ? BridgeStyle.ConcreteGirder : BridgeStyle.SteelTruss;
        }

        private static float DeckY(float y0, float y1, float camber, float t)
        {
            return Mathf.Lerp(y0, y1, t) + Mathf.Sin(t * Mathf.PI) * camber;
        }

        // --- Model 1: Classic French Stone Arch Bridge ---
        private static void BuildStoneArchBridge(
            Vector2 p0, Vector3 fwd, Vector3 right, float len, float width,
            float y0, float y1, float camber, float minGroundY,
            MeshAcc asphalt, MeshAcc stone)
        {
            int spans = Mathf.Clamp(Mathf.RoundToInt(len / 22f), 1, 8);
            int deckSegs = spans * 4;
            float hw = width * 0.5f;
            float parapetW = 0.55f;
            float pierBottomY = minGroundY - 6.0f;

            // Road deck + stone parapets along arched profile
            for (int s = 0; s < deckSegs; s++)
            {
                float tA = (float)s / deckSegs;
                float tB = (float)(s + 1) / deckSegs;
                float tM = (tA + tB) * 0.5f;
                float segLen = len / deckSegs + 0.08f;

                float yM = DeckY(y0, y1, camber, tM);
                Vector3 c = new Vector3(p0.x, yM, p0.y) + fwd * (tM * len);

                // Roadway surface
                AddOrientedBox(asphalt, c + Vector3.down * 0.25f, fwd, right, segLen, 0.5f, width - parapetW * 2f);
                // Stone structural deck slab
                AddOrientedBox(stone, c + Vector3.down * 0.85f, fwd, right, segLen, 0.9f, width);
                // Left & right stone parapets
                AddOrientedBox(stone, c + right * (hw - parapetW * 0.5f) + Vector3.up * 0.55f, fwd, right, segLen, 1.15f, parapetW);
                AddOrientedBox(stone, c - right * (hw - parapetW * 0.5f) + Vector3.up * 0.55f, fwd, right, segLen, 1.15f, parapetW);
            }

            // Piers & stone arch ribs between spans
            for (int p = 0; p <= spans; p++)
            {
                float t = (float)p / spans;
                float yTop = DeckY(y0, y1, camber, t) - 0.8f;
                float pierH = Mathf.Max(2.5f, yTop - pierBottomY);
                float pierLen = (p == 0 || p == spans) ? 3.2f : 2.4f;
                Vector3 pierCenter = new Vector3(p0.x, pierBottomY + pierH * 0.5f, p0.y) + fwd * (t * len);
                AddOrientedBox(stone, pierCenter, fwd, right, pierLen, pierH, width + 1.0f);

                // Arch spandrel blocks arching up from each pier toward mid-span
                if (p < spans)
                {
                    float spanLen = len / spans;
                    int archSteps = 6;
                    for (int a = 0; a < archSteps; a++)
                    {
                        float u = (a + 0.5f) / archSteps;
                        float tWorld = (p + u) / spans;
                        float yDeck = DeckY(y0, y1, camber, tWorld) - 1.1f;
                        float archDrop = Mathf.Sin(u * Mathf.PI) * Mathf.Min(spanLen * 0.22f, 3.8f);
                        float spandrelH = Mathf.Max(0.7f, (Mathf.Min(spanLen * 0.22f, 3.8f) + 0.9f) - archDrop);
                        Vector3 ac = new Vector3(p0.x, yDeck - spandrelH * 0.5f, p0.y) + fwd * (tWorld * len);
                        AddOrientedBox(stone, ac, fwd, right, spanLen / archSteps + 0.15f, spandrelH, width * 0.94f);
                    }
                }
            }
        }

        // --- Model 2: Modern Concrete Girder / Viaduct Bridge ---
        private static void BuildConcreteGirderBridge(
            Vector2 p0, Vector3 fwd, Vector3 right, float len, float width,
            float y0, float y1, float camber, float minGroundY,
            MeshAcc asphalt, MeshAcc concrete, MeshAcc steel)
        {
            int spans = Mathf.Clamp(Mathf.RoundToInt(len / 28f), 1, 10);
            int deckSegs = Mathf.Max(spans * 2, 4);
            float hw = width * 0.5f;
            float pierBottomY = minGroundY - 6.0f;

            for (int s = 0; s < deckSegs; s++)
            {
                float tM = (s + 0.5f) / deckSegs;
                float segLen = len / deckSegs + 0.08f;
                float yM = DeckY(y0, y1, camber, tM);
                Vector3 c = new Vector3(p0.x, yM, p0.y) + fwd * (tM * len);

                // Asphalt deck
                AddOrientedBox(asphalt, c + Vector3.down * 0.15f, fwd, right, segLen, 0.35f, width - 1.2f);
                // Concrete deck + box girder belly
                AddOrientedBox(concrete, c + Vector3.down * 0.65f, fwd, right, segLen, 0.7f, width);
                AddOrientedBox(concrete, c + Vector3.down * 1.45f, fwd, right, segLen, 1.0f, width * 0.68f);
                // Sidewalk curbs + steel guardrails
                AddOrientedBox(concrete, c + right * (hw - 0.35f) + Vector3.up * 0.22f, fwd, right, segLen, 0.45f, 0.65f);
                AddOrientedBox(concrete, c - right * (hw - 0.35f) + Vector3.up * 0.22f, fwd, right, segLen, 0.45f, 0.65f);
                AddOrientedBox(steel, c + right * (hw - 0.12f) + Vector3.up * 0.95f, fwd, right, segLen, 0.16f, 0.12f);
                AddOrientedBox(steel, c - right * (hw - 0.12f) + Vector3.up * 0.95f, fwd, right, segLen, 0.16f, 0.12f);
            }

            // Twin concrete piers per support bent
            for (int p = 0; p <= spans; p++)
            {
                float t = (float)p / spans;
                float yTop = DeckY(y0, y1, camber, t) - 1.5f;
                float pierH = Mathf.Max(2.5f, yTop - pierBottomY);
                Vector3 baseC = new Vector3(p0.x, pierBottomY + pierH * 0.5f, p0.y) + fwd * (t * len);

                if (p == 0 || p == spans)
                {
                    AddOrientedBox(concrete, baseC, fwd, right, 2.8f, pierH, width + 0.4f);
                }
                else
                {
                    // Cap beam + twin columns
                    AddOrientedBox(concrete, new Vector3(baseC.x, yTop - 0.5f, baseC.z), fwd, right, 1.8f, 1.1f, width * 0.88f);
                    float colOff = width * 0.30f;
                    AddOrientedBox(concrete, baseC + right * colOff, fwd, right, 1.4f, pierH, 1.4f);
                    AddOrientedBox(concrete, baseC - right * colOff, fwd, right, 1.4f, pierH, 1.4f);
                }
            }
        }

        // --- Model 3: Steel Truss Bridge ---
        private static void BuildSteelTrussBridge(
            Vector2 p0, Vector3 fwd, Vector3 right, float len, float width,
            float y0, float y1, float camber, float minGroundY,
            MeshAcc asphalt, MeshAcc steel, MeshAcc concrete)
        {
            float hw = width * 0.5f;
            float trussH = Mathf.Clamp(len * 0.085f, 4.2f, 9.0f);
            int bays = Mathf.Clamp(Mathf.RoundToInt(len / 8.5f), 3, 18);
            float bayLen = len / bays;
            float pierBottomY = minGroundY - 6.0f;

            // End abutments + mid piers if long
            int pierCount = len > 75f ? Mathf.Clamp(Mathf.RoundToInt(len / 45f), 2, 5) : 1;
            for (int p = 0; p <= pierCount; p++)
            {
                float t = (float)p / pierCount;
                float yTop = DeckY(y0, y1, camber, t) - 0.8f;
                float pierH = Mathf.Max(2.5f, yTop - pierBottomY);
                Vector3 pc = new Vector3(p0.x, pierBottomY + pierH * 0.5f, p0.y) + fwd * (t * len);
                AddOrientedBox(concrete, pc, fwd, right, 2.6f, pierH, width + 0.8f);
            }

            for (int b = 0; b < bays; b++)
            {
                float tA = (float)b / bays;
                float tB = (float)(b + 1) / bays;
                float tM = (tA + tB) * 0.5f;

                float yA = DeckY(y0, y1, camber, tA);
                float yB = DeckY(y0, y1, camber, tB);
                float yM = (yA + yB) * 0.5f;

                Vector3 cM = new Vector3(p0.x, yM, p0.y) + fwd * (tM * len);
                Vector3 cA = new Vector3(p0.x, yA, p0.y) + fwd * (tA * len);
                Vector3 cB = new Vector3(p0.x, yB, p0.y) + fwd * (tB * len);

                // Roadway deck + lower steel beam
                AddOrientedBox(asphalt, cM + Vector3.down * 0.2f, fwd, right, bayLen + 0.06f, 0.4f, width - 0.8f);
                AddOrientedBox(steel, cM + Vector3.down * 0.75f, fwd, right, bayLen + 0.06f, 0.75f, width);

                // Left & right truss chords + vertical posts + diagonal bracing
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 sOff = right * (side * (hw - 0.25f));
                    // Top chord
                    AddOrientedBox(steel, cM + sOff + Vector3.up * trussH, fwd, right, bayLen + 0.1f, 0.45f, 0.45f);
                    // Vertical post at bay start
                    AddOrientedBox(steel, cA + sOff + Vector3.up * (trussH * 0.5f), fwd, right, 0.42f, trussH, 0.42f);
                    // Diagonal strut across bay
                    Vector3 diagStart = (b % 2 == 0) ? (cA + sOff) : (cA + sOff + Vector3.up * trussH);
                    Vector3 diagEnd = (b % 2 == 0) ? (cB + sOff + Vector3.up * trussH) : (cB + sOff);
                    AddStrut(steel, diagStart, diagEnd, 0.30f);
                }

                // Overhead portal cross-beam
                AddOrientedBox(steel, cA + Vector3.up * trussH, fwd, right, 0.40f, 0.40f, width);
            }

            // Final end vertical posts + portal
            Vector3 endC = new Vector3(p0.x, y1, p0.y) + fwd * len;
            AddOrientedBox(steel, endC + right * (hw - 0.25f) + Vector3.up * (trussH * 0.5f), fwd, right, 0.42f, trussH, 0.42f);
            AddOrientedBox(steel, endC - right * (hw - 0.25f) + Vector3.up * (trussH * 0.5f), fwd, right, 0.42f, trussH, 0.42f);
            AddOrientedBox(steel, endC + Vector3.up * trussH, fwd, right, 0.40f, 0.40f, width);
        }

        // --- Model 4: Sleek Arch Pedestrian Footbridge ---
        private static void BuildFootbridge(
            Vector2 p0, Vector3 fwd, Vector3 right, float len, float width,
            float y0, float y1, float camber, float minGroundY,
            MeshAcc wood, MeshAcc steel, MeshAcc concrete)
        {
            int segs = Mathf.Clamp(Mathf.RoundToInt(len / 4f), 4, 24);
            float hw = width * 0.5f;
            float archHeight = Mathf.Clamp(len * 0.14f, 2.5f, 8.5f);
            float pierBottomY = minGroundY - 5.0f;

            // End concrete abutments
            for (int e = 0; e <= 1; e++)
            {
                float yTop = (e == 0 ? y0 : y1) - 0.3f;
                float h = Mathf.Max(2f, yTop - pierBottomY);
                Vector3 pc = new Vector3(p0.x, pierBottomY + h * 0.5f, p0.y) + fwd * (e * len);
                AddOrientedBox(concrete, pc, fwd, right, 1.8f, h, width + 0.6f);
            }

            for (int s = 0; s < segs; s++)
            {
                float tA = (float)s / segs;
                float tB = (float)(s + 1) / segs;
                float tM = (tA + tB) * 0.5f;
                float segLen = len / segs + 0.05f;

                float yM = DeckY(y0, y1, camber, tM);
                Vector3 cM = new Vector3(p0.x, yM, p0.y) + fwd * (tM * len);

                // Timber walkway deck + slim steel edge beams + handrails
                AddOrientedBox(wood, cM, fwd, right, segLen, 0.28f, width - 0.3f);
                AddOrientedBox(steel, cM + Vector3.down * 0.25f, fwd, right, segLen, 0.26f, width);
                AddOrientedBox(steel, cM + right * (hw - 0.08f) + Vector3.up * 1.0f, fwd, right, segLen, 0.10f, 0.10f);
                AddOrientedBox(steel, cM - right * (hw - 0.08f) + Vector3.up * 1.0f, fwd, right, segLen, 0.10f, 0.10f);

                // Twin curved steel arches + vertical hanger cables
                float archYA = DeckY(y0, y1, camber, tA) + Mathf.Sin(tA * Mathf.PI) * archHeight;
                float archYB = DeckY(y0, y1, camber, tB) + Mathf.Sin(tB * Mathf.PI) * archHeight;
                Vector3 aPos = new Vector3(p0.x, archYA, p0.y) + fwd * (tA * len);
                Vector3 bPos = new Vector3(p0.x, archYB, p0.y) + fwd * (tB * len);

                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 sOff = right * (side * hw);
                    AddStrut(steel, aPos + sOff, bPos + sOff, 0.26f);

                    if (s > 0 && s % 2 == 0)
                    {
                        Vector3 deckPt = new Vector3(p0.x, DeckY(y0, y1, camber, tA), p0.y) + fwd * (tA * len) + sOff;
                        AddStrut(steel, deckPt, aPos + sOff, 0.09f);
                    }
                }
            }
        }

        private static void AddStrut(MeshAcc acc, Vector3 a, Vector3 b, float thickness)
        {
            Vector3 diff = b - a;
            float len = diff.magnitude;
            if (len < 0.05f) return;
            Vector3 dir = diff / len;
            Vector3 upRef = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
            Vector3 right = Vector3.Cross(upRef, dir).normalized;
            Vector3 up = Vector3.Cross(dir, right).normalized;
            AddOrientedBoxFull(acc, (a + b) * 0.5f, dir, up, right, len, thickness, thickness);
        }

        private static void AddOrientedBox(
            MeshAcc acc, Vector3 center, Vector3 fwd, Vector3 right,
            float lenFwd, float heightY, float widthRight)
        {
            AddOrientedBoxFull(acc, center, fwd, Vector3.up, right, lenFwd, heightY, widthRight);
        }

        private static void AddOrientedBoxFull(
            MeshAcc acc, Vector3 center, Vector3 axisF, Vector3 axisU, Vector3 axisR,
            float sizeF, float sizeU, float sizeR)
        {
            Vector3 hf = axisF * (sizeF * 0.5f);
            Vector3 hu = axisU * (sizeU * 0.5f);
            Vector3 hr = axisR * (sizeR * 0.5f);

            Vector3 p000 = center - hf - hu - hr;
            Vector3 p100 = center + hf - hu - hr;
            Vector3 p110 = center + hf + hu - hr;
            Vector3 p010 = center - hf + hu - hr;
            Vector3 p001 = center - hf - hu + hr;
            Vector3 p101 = center + hf - hu + hr;
            Vector3 p111 = center + hf + hu + hr;
            Vector3 p011 = center - hf + hu + hr;

            AddQuad(acc, p010, p110, p111, p011, axisU);   // Top
            AddQuad(acc, p001, p101, p100, p000, -axisU);  // Bottom
            AddQuad(acc, p100, p110, p111, p101, axisF);   // Front
            AddQuad(acc, p001, p011, p010, p000, -axisF);  // Back
            AddQuad(acc, p001, p101, p111, p011, axisR);   // Right
            AddQuad(acc, p100, p000, p010, p110, -axisR);  // Left
        }

        private static void AddQuad(MeshAcc acc, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
        {
            // Ensure winding matches desired outward normal
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

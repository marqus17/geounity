using UnityEngine;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Generates and caches crisp tileable procedural architectural textures (512x512, mipmapped)
    /// for French building facades (limestone, brick/warm, modern glass) and roofs (slate, terracotta, flat).
    /// </summary>
    public static class ArchitecturalTextureGenerator
    {
        private static Texture2D _limestoneWall;
        private static Texture2D _warmWall;
        private static Texture2D _modernWall;
        private static Texture2D _slateRoof;
        private static Texture2D _terracottaRoof;
        private static Texture2D _flatRoof;

        public static Texture2D GetLimestoneWall()
        {
            if (_limestoneWall == null)
                _limestoneWall = BuildFacadeTexture(
                    baseColor: new Color(0.84f, 0.80f, 0.72f),
                    mortarColor: new Color(0.73f, 0.69f, 0.61f),
                    windowColor: new Color(0.16f, 0.22f, 0.29f),
                    windowHighlight: new Color(0.34f, 0.44f, 0.54f),
                    frameColor: new Color(0.92f, 0.90f, 0.85f),
                    corniceColor: new Color(0.76f, 0.72f, 0.64f),
                    cols: 2, rows: 2, modern: false, seed: 11);
            return _limestoneWall;
        }

        public static Texture2D GetWarmWall()
        {
            if (_warmWall == null)
                _warmWall = BuildFacadeTexture(
                    baseColor: new Color(0.76f, 0.56f, 0.44f),
                    mortarColor: new Color(0.66f, 0.50f, 0.40f),
                    windowColor: new Color(0.18f, 0.20f, 0.24f),
                    windowHighlight: new Color(0.32f, 0.36f, 0.42f),
                    frameColor: new Color(0.86f, 0.80f, 0.72f),
                    corniceColor: new Color(0.68f, 0.52f, 0.42f),
                    cols: 2, rows: 2, modern: false, seed: 29);
            return _warmWall;
        }

        public static Texture2D GetModernWall()
        {
            if (_modernWall == null)
                _modernWall = BuildFacadeTexture(
                    baseColor: new Color(0.70f, 0.74f, 0.78f),
                    mortarColor: new Color(0.54f, 0.58f, 0.63f),
                    windowColor: new Color(0.20f, 0.30f, 0.39f),
                    windowHighlight: new Color(0.42f, 0.55f, 0.66f),
                    frameColor: new Color(0.45f, 0.49f, 0.54f),
                    corniceColor: new Color(0.62f, 0.66f, 0.71f),
                    cols: 3, rows: 2, modern: true, seed: 47);
            return _modernWall;
        }

        public static Texture2D GetSlateRoof()
        {
            if (_slateRoof == null)
                _slateRoof = BuildRoofTileTexture(
                    baseA: new Color(0.27f, 0.31f, 0.37f),
                    baseB: new Color(0.34f, 0.38f, 0.45f),
                    seamColor: new Color(0.18f, 0.21f, 0.26f),
                    rows: 16, cols: 12, stagger: true, seed: 71);
            return _slateRoof;
        }

        public static Texture2D GetTerracottaRoof()
        {
            if (_terracottaRoof == null)
                _terracottaRoof = BuildRoofTileTexture(
                    baseA: new Color(0.64f, 0.33f, 0.22f),
                    baseB: new Color(0.74f, 0.42f, 0.28f),
                    seamColor: new Color(0.42f, 0.20f, 0.13f),
                    rows: 14, cols: 16, stagger: false, seed: 89);
            return _terracottaRoof;
        }

        public static Texture2D GetFlatRoof()
        {
            if (_flatRoof == null)
                _flatRoof = BuildRoofTileTexture(
                    baseA: new Color(0.50f, 0.52f, 0.54f),
                    baseB: new Color(0.57f, 0.58f, 0.60f),
                    seamColor: new Color(0.40f, 0.42f, 0.44f),
                    rows: 6, cols: 6, stagger: false, seed: 103);
            return _flatRoof;
        }

        private static Texture2D BuildFacadeTexture(
            Color baseColor, Color mortarColor,
            Color windowColor, Color windowHighlight,
            Color frameColor, Color corniceColor,
            int cols, int rows, bool modern, int seed)
        {
            const int size = 512;
            var pixels = new Color32[size * size];

            int cellW = size / cols;
            int cellH = size / rows;

            for (int y = 0; y < size; y++)
            {
                int row = y / cellH;
                int ly = y % cellH;
                float fy = (float)ly / cellH;

                bool isStoneCourse = !modern && (y % 32 == 0 || y % 32 == 1);
                bool isCornice = fy > 0.90f || fy < 0.05f;

                for (int x = 0; x < size; x++)
                {
                    int col = x / cellW;
                    int lx = x % cellW;
                    float fx = (float)lx / cellW;

                    float grain = (Hash01(x / 2, y / 2, seed) - 0.5f) * 0.05f;
                    Color c = baseColor;

                    if (isCornice)
                    {
                        c = corniceColor;
                        if (fy > 0.96f) c *= 0.88f; // subtle shadow under ledge
                    }
                    else if (isStoneCourse)
                    {
                        c = mortarColor;
                    }
                    else if (!modern && ((x + (y / 32) * 24) % 64 == 0))
                    {
                        c = Color.Lerp(baseColor, mortarColor, 0.55f);
                    }

                    // Window bounds inside each bay
                    float wMinX = modern ? 0.08f : 0.24f;
                    float wMaxX = modern ? 0.92f : 0.76f;
                    float wMinY = modern ? 0.16f : 0.18f;
                    float wMaxY = modern ? 0.84f : 0.80f;

                    float framePad = modern ? 0.03f : 0.045f;

                    if (fx >= wMinX - framePad && fx <= wMaxX + framePad &&
                        fy >= wMinY - framePad && fy <= wMaxY + framePad)
                    {
                        if (fx >= wMinX && fx <= wMaxX && fy >= wMinY && fy <= wMaxY)
                        {
                            // Glass interior with subtle vertical sky gradient + mullion cross
                            float glassT = (fy - wMinY) / (wMaxY - wMinY);
                            float bayVar = Hash01(col, row, seed + 5) * 0.14f;
                            c = Color.Lerp(windowColor, windowHighlight, glassT * 0.75f + bayVar);

                            // Subtle window mullion bars
                            float midX = (wMinX + wMaxX) * 0.5f;
                            float midY = (wMinY + wMaxY) * 0.55f;
                            if (Mathf.Abs(fx - midX) < 0.015f || (!modern && Mathf.Abs(fy - midY) < 0.014f))
                            {
                                c = frameColor * 0.85f;
                            }
                            else if (fy > wMaxY - 0.04f)
                            {
                                c *= 0.72f; // top lintel shadow inside recess
                            }
                        }
                        else
                        {
                            // Window frame / sill
                            c = fy < wMinY ? frameColor * 1.04f : frameColor * 0.92f;
                        }
                    }

                    c.r = Mathf.Clamp01(c.r + grain);
                    c.g = Mathf.Clamp01(c.g + grain);
                    c.b = Mathf.Clamp01(c.b + grain);
                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }

            return FinalizeTexture(pixels, size, "FacadeTex");
        }

        private static Texture2D BuildRoofTileTexture(
            Color baseA, Color baseB, Color seamColor,
            int rows, int cols, bool stagger, int seed)
        {
            const int size = 512;
            var pixels = new Color32[size * size];

            float rowH = (float)size / rows;
            float colW = (float)size / cols;

            for (int y = 0; y < size; y++)
            {
                int r = Mathf.FloorToInt(y / rowH);
                float ry = (y - r * rowH) / rowH;
                float xOffset = (stagger && (r % 2 == 1)) ? colW * 0.5f : 0f;

                for (int x = 0; x < size; x++)
                {
                    float sx = (x + xOffset) % size;
                    int cIdx = Mathf.FloorToInt(sx / colW);
                    float rx = (sx - cIdx * colW) / colW;

                    float tileVar = Hash01(cIdx, r, seed);
                    Color c = Color.Lerp(baseA, baseB, tileVar);

                    // Subtle shading across each shingle / tile
                    c *= Mathf.Lerp(0.88f, 1.06f, ry);

                    // Horizontal overlap & vertical seam
                    if (ry < 0.09f || rx < 0.06f)
                        c = Color.Lerp(c, seamColor, 0.72f);

                    float grain = (Hash01(x / 2, y / 2, seed + 17) - 0.5f) * 0.04f;
                    c.r = Mathf.Clamp01(c.r + grain);
                    c.g = Mathf.Clamp01(c.g + grain);
                    c.b = Mathf.Clamp01(c.b + grain);
                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }

            return FinalizeTexture(pixels, size, "RoofTex");
        }

        private static Texture2D FinalizeTexture(Color32[] pixels, int size, string name)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true);
            tex.name = name;
            tex.SetPixels32(pixels);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return tex;
        }

        private static float Hash01(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFu) / 65535f;
        }
    }
}

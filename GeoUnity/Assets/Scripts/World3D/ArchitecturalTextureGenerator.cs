using UnityEngine;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Generates and caches crisp procedural architectural textures (512x512, mipmapped)
    /// adapted to French building types:
    /// - Houses: white crépi stucco (plain gable, entry door + shuttered window, patio porte-fenêtre)
    /// - Historic / Religious (churches, castles, monuments): grey dressed ashlar stone + arched lancet windows
    /// - Industrial / Agricultural: concrete plinth + corrugated cladding + high clerestory strip
    /// - Urban apartments & modern offices: limestone, warm brick, modern glass
    /// - Roofs: slate, terracotta tiles, flat concrete
    /// </summary>
    public static class ArchitecturalTextureGenerator
    {
        private static Texture2D _limestoneWall;
        private static Texture2D _warmWall;
        private static Texture2D _modernWall;
        private static Texture2D _houseStuccoPlain;
        private static Texture2D _houseStuccoEntry;
        private static Texture2D _houseStuccoPatio;
        private static Texture2D _historicStoneWall;
        private static Texture2D _industrialCladdingWall;

        private static Texture2D _slateRoof;
        private static Texture2D _terracottaRoof;
        private static Texture2D _flatRoof;

        public static Texture2D GetHouseStuccoPlain()
        {
            if (_houseStuccoPlain == null)
                _houseStuccoPlain = BuildHouseStuccoTexture(HouseFaceKind.Plain, seed: 131);
            return _houseStuccoPlain;
        }

        public static Texture2D GetHouseStuccoEntry()
        {
            if (_houseStuccoEntry == null)
                _houseStuccoEntry = BuildHouseStuccoTexture(HouseFaceKind.EntryAndWindow, seed: 149);
            return _houseStuccoEntry;
        }

        public static Texture2D GetHouseStuccoPatio()
        {
            if (_houseStuccoPatio == null)
                _houseStuccoPatio = BuildHouseStuccoTexture(HouseFaceKind.PatioDoor, seed: 173);
            return _houseStuccoPatio;
        }

        public static Texture2D GetHistoricStoneWall()
        {
            if (_historicStoneWall == null)
                _historicStoneWall = BuildHistoricStoneTexture(seed: 197);
            return _historicStoneWall;
        }

        public static Texture2D GetIndustrialCladdingWall()
        {
            if (_industrialCladdingWall == null)
                _industrialCladdingWall = BuildIndustrialCladdingTexture(seed: 223);
            return _industrialCladdingWall;
        }

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

        private enum HouseFaceKind
        {
            Plain,
            EntryAndWindow,
            PatioDoor
        }

        private static Texture2D BuildHouseStuccoTexture(HouseFaceKind kind, int seed)
        {
            const int size = 512;
            var pixels = new Color32[size * size];

            Color stuccoBase = new Color(0.92f, 0.90f, 0.86f);
            Color plinthCol = new Color(0.74f, 0.72f, 0.68f);
            Color eaveShadow = new Color(0.80f, 0.78f, 0.74f);
            Color frameWhite = new Color(0.95f, 0.95f, 0.94f);
            Color shutterCol = new Color(0.36f, 0.46f, 0.52f); // classic French slate-blue shutters
            Color doorWood = new Color(0.42f, 0.27f, 0.18f);
            Color doorTrim = new Color(0.28f, 0.18f, 0.12f);
            Color glassDark = new Color(0.15f, 0.21f, 0.27f);
            Color glassLight = new Color(0.38f, 0.50f, 0.60f);

            for (int y = 0; y < size; y++)
            {
                float v = (float)y / size;
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;

                    // Pebbled French crépi texture (fine + coarse grain)
                    float fine = (Hash01(x, y, seed) - 0.5f) * 0.045f;
                    float coarse = (Hash01(x / 3, y / 3, seed + 19) - 0.5f) * 0.035f;
                    Color c = stuccoBase;

                    // Ground plinth & subtle top eave shadow
                    if (v < 0.055f)
                        c = plinthCol;
                    else if (v > 0.955f)
                        c = eaveShadow;

                    if (kind == HouseFaceKind.EntryAndWindow)
                    {
                        // Ground floor: Front entry door (left) + shuttered window (right)
                        c = DrawEntryDoor(c, u, v, 0.18f, 0.36f, 0.055f, 0.49f, doorWood, doorTrim, frameWhite, glassDark, glassLight);
                        c = DrawShutteredWindow(c, u, v, 0.56f, 0.78f, 0.19f, 0.47f, frameWhite, shutterCol, glassDark, glassLight);

                        // Upper floor (visible on 2-story houses when v goes up to 1.0)
                        c = DrawShutteredWindow(c, u, v, 0.17f, 0.37f, 0.64f, 0.88f, frameWhite, shutterCol, glassDark, glassLight);
                        c = DrawShutteredWindow(c, u, v, 0.56f, 0.78f, 0.64f, 0.88f, frameWhite, shutterCol, glassDark, glassLight);
                    }
                    else if (kind == HouseFaceKind.PatioDoor)
                    {
                        // Ground floor: wide French porte-fenêtre (double glazed sliding/hinged patio door)
                        c = DrawPatioDoor(c, u, v, 0.24f, 0.76f, 0.055f, 0.50f, frameWhite, shutterCol, glassDark, glassLight);

                        // Upper floor centered shuttered window (visible on 2-story houses)
                        c = DrawShutteredWindow(c, u, v, 0.37f, 0.63f, 0.64f, 0.88f, frameWhite, shutterCol, glassDark, glassLight);
                    }

                    float g = fine + coarse;
                    c.r = Mathf.Clamp01(c.r + g);
                    c.g = Mathf.Clamp01(c.g + g);
                    c.b = Mathf.Clamp01(c.b + g);
                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }

            var wrap = kind == HouseFaceKind.Plain ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            return FinalizeTexture(pixels, size, $"HouseStucco_{kind}", wrap);
        }

        private static Color DrawEntryDoor(
            Color bg, float u, float v,
            float x0, float x1, float y0, float y1,
            Color wood, Color trim, Color frame, Color glassDark, Color glassLight)
        {
            const float pad = 0.016f;
            if (u < x0 - pad || u > x1 + pad || v < y0 || v > y1 + pad)
                return bg;

            // Stone threshold step
            if (v < y0 + 0.014f)
                return new Color(0.68f, 0.67f, 0.65f);

            // Outer frame / lintel
            if (u < x0 || u > x1 || v > y1)
                return v > y1 ? frame * 0.90f : frame;

            float lu = (u - x0) / (x1 - x0);
            float lv = (v - y0) / (y1 - y0);

            Color c = wood;
            // Subtle vertical wood panels
            if (lu < 0.07f || lu > 0.93f || lv < 0.04f || lv > 0.95f)
                c = trim;
            // Small upper glass transom pane
            else if (lu > 0.22f && lu < 0.78f && lv > 0.60f && lv < 0.86f)
                c = Color.Lerp(glassDark, glassLight, (lv - 0.60f) / 0.26f);
            // Lower recessed panel
            else if (lu > 0.20f && lu < 0.80f && lv > 0.14f && lv < 0.46f)
                c = wood * 0.88f;

            // Brass handle
            if (lu > 0.78f && lu < 0.88f && lv > 0.46f && lv < 0.51f)
                c = new Color(0.84f, 0.72f, 0.36f);

            return c;
        }

        private static Color DrawShutteredWindow(
            Color bg, float u, float v,
            float x0, float x1, float y0, float y1,
            Color frame, Color shutter, Color glassDark, Color glassLight)
        {
            float w = x1 - x0;
            float shutterW = w * 0.34f;
            const float sillPad = 0.016f;

            // Left and right wooden shutters
            bool inLeftShutter = u >= x0 - shutterW - 0.008f && u <= x0 - 0.008f && v >= y0 && v <= y1;
            bool inRightShutter = u >= x1 + 0.008f && u <= x1 + shutterW + 0.008f && v >= y0 && v <= y1;
            if (inLeftShutter || inRightShutter)
            {
                // Horizontal louver slats
                float slat = Mathf.Sin(v * 220f) * 0.05f;
                return new Color(shutter.r + slat, shutter.g + slat, shutter.b + slat);
            }

            if (u < x0 - sillPad || u > x1 + sillPad || v < y0 - sillPad || v > y1 + sillPad)
                return bg;

            // Sill & frame
            if (u < x0 || u > x1 || v < y0 || v > y1)
                return v < y0 ? frame * 0.98f : frame * 0.88f;

            float lu = (u - x0) / w;
            float lv = (v - y0) / (y1 - y0);

            // Mullion cross
            if (Mathf.Abs(lu - 0.5f) < 0.035f || Mathf.Abs(lv - 0.52f) < 0.03f)
                return frame;

            Color g = Color.Lerp(glassDark, glassLight, lv * 0.85f);
            if (lv > 0.90f) g *= 0.72f; // lintel shadow
            return g;
        }

        private static Color DrawPatioDoor(
            Color bg, float u, float v,
            float x0, float x1, float y0, float y1,
            Color frame, Color shutter, Color glassDark, Color glassLight)
        {
            float w = x1 - x0;
            float shutterW = w * 0.16f;
            const float pad = 0.016f;

            // Side shutters / roller-shutter box above
            if ((u >= x0 - shutterW - 0.008f && u <= x0 - 0.008f && v >= y0 && v <= y1) ||
                (u >= x1 + 0.008f && u <= x1 + shutterW + 0.008f && v >= y0 && v <= y1))
            {
                float slat = Mathf.Sin(v * 220f) * 0.05f;
                return new Color(shutter.r + slat, shutter.g + slat, shutter.b + slat);
            }

            if (u < x0 - pad || u > x1 + pad || v < y0 || v > y1 + pad)
                return bg;

            // Stone terrace threshold
            if (v < y0 + 0.015f)
                return new Color(0.68f, 0.67f, 0.65f);

            // Outer frame
            if (u < x0 || u > x1 || v > y1)
                return frame * 0.92f;

            float lu = (u - x0) / w;
            float lv = (v - y0) / (y1 - y0);

            // Double sliding patio door frames (center divider + outer stiles + kickplate)
            if (lv < 0.08f || lu < 0.04f || lu > 0.96f || Mathf.Abs(lu - 0.5f) < 0.025f)
                return frame;

            // Subtle reflection diagonal + vertical sky gradient
            float diag = Mathf.Clamp01(1f - Mathf.Abs((lu * 1.4f - lv * 0.9f) - 0.25f) * 4.0f) * 0.18f;
            Color g = Color.Lerp(glassDark, glassLight, Mathf.Clamp01(lv * 0.8f + diag));
            if (lv > 0.92f) g *= 0.72f;
            return g;
        }

        private static Texture2D BuildHistoricStoneTexture(int seed)
        {
            const int size = 512;
            var pixels = new Color32[size * size];

            Color stoneLight = new Color(0.64f, 0.64f, 0.62f);
            Color stoneDark = new Color(0.50f, 0.51f, 0.50f);
            Color mortar = new Color(0.38f, 0.38f, 0.37f);
            Color stainedDark = new Color(0.14f, 0.16f, 0.20f);
            Color stainedGlow = new Color(0.24f, 0.29f, 0.38f);

            const int courseH = 24;
            const int blockW = 56;

            for (int y = 0; y < size; y++)
            {
                int row = y / courseH;
                int ly = y % courseH;
                int xOff = (row % 2) * (blockW / 2);
                float v = (float)y / size;

                for (int x = 0; x < size; x++)
                {
                    int sx = (x + xOff) % size;
                    int col = sx / blockW;
                    int lx = sx % blockW;
                    float u = (float)x / size;

                    float blockTint = Hash01(col, row, seed);
                    Color c = Color.Lerp(stoneDark, stoneLight, blockTint);

                    // Dressed ashlar mortar seams
                    if (ly <= 1 || lx <= 1)
                        c = mortar;
                    else if (ly == 2)
                        c *= 1.05f; // subtle top bevel highlight

                    // Narrow Gothic/Romanesque arched lancet windows (2 per tile, spaced wide apart)
                    float bayU = (u * 2f) % 1f;
                    float bayV = (v * 1f) % 1f;
                    float cx = 0.5f;
                    float halfW = 0.11f;
                    float winBot = 0.22f;
                    float archBase = 0.68f;
                    float archTop = 0.82f;

                    float dx = Mathf.Abs(bayU - cx);
                    bool inArch = false;
                    bool inVoussoir = false;

                    if (bayV >= winBot && bayV <= archTop)
                    {
                        float maxDx = halfW;
                        if (bayV > archBase)
                        {
                            float t = (bayV - archBase) / (archTop - archBase);
                            maxDx = halfW * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                        }
                        if (dx <= maxDx)
                            inArch = true;
                        else if (dx <= maxDx + 0.045f && bayV >= winBot - 0.02f)
                            inVoussoir = true;
                    }

                    if (inArch)
                    {
                        float gt = (bayV - winBot) / (archTop - winBot);
                        c = Color.Lerp(stainedDark, stainedGlow, gt * 0.7f);
                        // Stone tracery mullion
                        if (dx < 0.012f || Mathf.Abs(bayV - 0.45f) < 0.01f || Mathf.Abs(bayV - 0.66f) < 0.01f)
                            c = mortar * 1.15f;
                    }
                    else if (inVoussoir)
                    {
                        c = stoneLight * 0.92f;
                    }

                    // Weathering & stone grain
                    float grain = (Hash01(x / 2, y / 2, seed + 31) - 0.5f) * 0.06f;
                    c.r = Mathf.Clamp01(c.r + grain);
                    c.g = Mathf.Clamp01(c.g + grain);
                    c.b = Mathf.Clamp01(c.b + grain);
                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }

            return FinalizeTexture(pixels, size, "HistoricStoneTex", TextureWrapMode.Repeat);
        }

        private static Texture2D BuildIndustrialCladdingTexture(int seed)
        {
            const int size = 512;
            var pixels = new Color32[size * size];

            Color concrete = new Color(0.68f, 0.69f, 0.67f);
            Color claddingA = new Color(0.76f, 0.78f, 0.80f);
            Color claddingB = new Color(0.67f, 0.70f, 0.73f);
            Color accentBand = new Color(0.32f, 0.40f, 0.48f);
            Color glassDark = new Color(0.18f, 0.24f, 0.30f);

            for (int y = 0; y < size; y++)
            {
                float v = (float)y / size;
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;
                    Color c;

                    if (v < 0.28f)
                    {
                        // Precast concrete lower wall
                        c = concrete;
                        if (x % 128 < 2 || y % 64 < 2) c *= 0.86f;
                    }
                    else if (v >= 0.76f && v <= 0.88f)
                    {
                        // High clerestory strip window (narrow industrial band)
                        bool mullion = (x % 32) < 3;
                        c = mullion ? accentBand : Color.Lerp(glassDark, claddingA, 0.25f);
                    }
                    else if (v > 0.93f)
                    {
                        // Roof fascia trim
                        c = accentBand;
                    }
                    else
                    {
                        // Corrugated vertical metal cladding ribs
                        float rib = Mathf.Sin(u * Mathf.PI * 64f) * 0.5f + 0.5f;
                        c = Color.Lerp(claddingB, claddingA, rib);
                    }

                    float grain = (Hash01(x / 2, y / 2, seed) - 0.5f) * 0.03f;
                    c.r = Mathf.Clamp01(c.r + grain);
                    c.g = Mathf.Clamp01(c.g + grain);
                    c.b = Mathf.Clamp01(c.b + grain);
                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }

            return FinalizeTexture(pixels, size, "IndustrialCladdingTex", TextureWrapMode.Repeat);
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

            return FinalizeTexture(pixels, size, "FacadeTex", TextureWrapMode.Repeat);
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

            return FinalizeTexture(pixels, size, "RoofTex", TextureWrapMode.Repeat);
        }

        private static Texture2D FinalizeTexture(Color32[] pixels, int size, string name, TextureWrapMode wrapMode)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true);
            tex.name = name;
            tex.SetPixels32(pixels);
            tex.wrapMode = wrapMode;
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

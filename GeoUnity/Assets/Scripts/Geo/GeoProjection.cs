using System;

namespace GeoUnity.Geo
{
    /// <summary>
    /// Represents a validated geographic sector in Metropolitan France (default 2 km x 2 km).
    /// </summary>
    [Serializable]
    public struct GeoSector
    {
        public double CenterLat;
        public double CenterLon;
        public double CenterLambertX;
        public double CenterLambertY;
        public double SizeMeters;

        public double MinLambertX => CenterLambertX - SizeMeters * 0.5;
        public double MaxLambertX => CenterLambertX + SizeMeters * 0.5;
        public double MinLambertY => CenterLambertY - SizeMeters * 0.5;
        public double MaxLambertY => CenterLambertY + SizeMeters * 0.5;

        public double MinLat;
        public double MaxLat;
        public double MinLon;
        public double MaxLon;

        public static GeoSector FromCenterWgs84(double lat, double lon, double sizeMeters = 2000.0)
        {
            GeoProjection.ClampToMetropolitanFrance(ref lat, ref lon);
            GeoProjection.Wgs84ToLambert93(lat, lon, out double lx, out double ly);

            double half = sizeMeters * 0.5;
            GeoProjection.Lambert93ToWgs84(lx - half, ly - half, out double swLat, out double swLon);
            GeoProjection.Lambert93ToWgs84(lx + half, ly + half, out double neLat, out double neLon);

            return new GeoSector
            {
                CenterLat = lat,
                CenterLon = lon,
                CenterLambertX = lx,
                CenterLambertY = ly,
                SizeMeters = sizeMeters,
                MinLat = Math.Min(swLat, neLat),
                MaxLat = Math.Max(swLat, neLat),
                MinLon = Math.Min(swLon, neLon),
                MaxLon = Math.Max(swLon, neLon)
            };
        }

        public override string ToString()
        {
            return $"GeoSector({SizeMeters:F0}m x {SizeMeters:F0}m | WGS84: {CenterLat:F5}, {CenterLon:F5} | L93: {CenterLambertX:F1}, {CenterLambertY:F1})";
        }
    }

    /// <summary>
    /// Geodetic projection utilities for Metropolitan France:
    /// WGS84 (EPSG:4326) <-> Web Mercator (EPSG:3857 / WMTS PM) <-> Lambert-93 (EPSG:2154).
    /// </summary>
    public static class GeoProjection
    {
        // Web Mercator constants (EPSG:3857)
        public const double WebMercatorRadius = 6378137.0;
        public const double WebMercatorCircumference = 2.0 * Math.PI * WebMercatorRadius;
        public const double TileSizePx = 256.0;

        // Metropolitan France geographic bounding box
        public const double FranceMinLat = 41.2;
        public const double FranceMaxLat = 51.2;
        public const double FranceMinLon = -5.5;
        public const double FranceMaxLon = 9.8;

        // RGF93 / Lambert-93 constants (IGN official GRS80 parameters)
        private const double Grs80A = 6378137.0;
        private const double Grs80E = 0.0818191910428158; // sqrt(2f - f^2)
        private const double L93Lon0Rad = 3.0 * Math.PI / 180.0;
        private const double L93N = 0.7256077650532670;
        private const double L93C = 11754255.426096;
        private const double L93Xs = 700000.0;
        private const double L93Ys = 12655612.049876;

        public static void ClampToMetropolitanFrance(ref double lat, ref double lon)
        {
            if (lat < FranceMinLat) lat = FranceMinLat;
            else if (lat > FranceMaxLat) lat = FranceMaxLat;

            if (lon < FranceMinLon) lon = FranceMinLon;
            else if (lon > FranceMaxLon) lon = FranceMaxLon;
        }

        public static void Wgs84ToTileCoords(double lat, double lon, int zoom, out double tileX, out double tileY)
        {
            double n = Math.Pow(2.0, zoom);
            double latRad = lat * Math.PI / 180.0;
            tileX = (lon + 180.0) / 360.0 * n;
            tileY = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) * 0.5 * n;
        }

        public static void TileCoordsToWgs84(double tileX, double tileY, int zoom, out double lat, out double lon)
        {
            double n = Math.Pow(2.0, zoom);
            lon = tileX / n * 360.0 - 180.0;
            double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * tileY / n)));
            lat = latRad * 180.0 / Math.PI;
        }

        /// <summary>
        /// Real ground meters per pixel on a Web Mercator map at the given latitude and continuous zoom level.
        /// Accounts for the Web Mercator cos(lat) distortion.
        /// </summary>
        public static double GroundMetersPerPixel(double lat, double zoom)
        {
            double latRad = lat * Math.PI / 180.0;
            return (WebMercatorCircumference * Math.Cos(latRad)) / (TileSizePx * Math.Pow(2.0, zoom));
        }

        /// <summary>
        /// Converts WGS84 (degrees) to RGF93 / Lambert-93 (meters).
        /// </summary>
        public static void Wgs84ToLambert93(double latDeg, double lonDeg, out double x, out double y)
        {
            double phi = latDeg * Math.PI / 180.0;
            double lambda = lonDeg * Math.PI / 180.0;

            double isoLat = IsometricLatitude(phi, Grs80E);
            double r = L93C * Math.Exp(-L93N * isoLat);
            double gamma = L93N * (lambda - L93Lon0Rad);

            x = L93Xs + r * Math.Sin(gamma);
            y = L93Ys - r * Math.Cos(gamma);
        }

        /// <summary>
        /// Converts RGF93 / Lambert-93 (meters) to WGS84 (degrees).
        /// </summary>
        public static void Lambert93ToWgs84(double x, double y, out double latDeg, out double lonDeg)
        {
            double dx = x - L93Xs;
            double dy = L93Ys - y;
            double r = Math.Sqrt(dx * dx + dy * dy);
            double gamma = Math.Atan2(dx, dy);

            double lambda = L93Lon0Rad + gamma / L93N;
            double isoLat = -Math.Log(r / L93C) / L93N;
            double phi = InverseIsometricLatitude(isoLat, Grs80E);

            latDeg = phi * 180.0 / Math.PI;
            lonDeg = lambda * 180.0 / Math.PI;
        }

        private static double IsometricLatitude(double phi, double e)
        {
            double sinPhi = Math.Sin(phi);
            double term1 = Math.Tan(Math.PI * 0.25 + phi * 0.5);
            double term2 = Math.Pow((1.0 - e * sinPhi) / (1.0 + e * sinPhi), e * 0.5);
            return Math.Log(term1 * term2);
        }

        private static double InverseIsometricLatitude(double isoLat, double e)
        {
            double phi = 2.0 * Math.Atan(Math.Exp(isoLat)) - Math.PI * 0.5;
            for (int i = 0; i < 8; i++)
            {
                double sinPhi = Math.Sin(phi);
                double factor = Math.Pow((1.0 + e * sinPhi) / (1.0 - e * sinPhi), e * 0.5);
                double nextPhi = 2.0 * Math.Atan(Math.Exp(isoLat) * factor) - Math.PI * 0.5;
                if (Math.Abs(nextPhi - phi) < 1e-11)
                {
                    phi = nextPhi;
                    break;
                }
                phi = nextPhi;
            }
            return phi;
        }
    }
}

using System;
using System.Collections.Generic;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GeoUnity.MapSelector
{
    /// <summary>
    /// Full-screen 2D IGN WMTS slippy map for selecting a fixed 2 km x 2 km sector in Metropolitan France.
    /// Supports pan (left-drag), zoom (scroll wheel), click-to-place sector, layer toggle (Plan IGN / Ortho),
    /// and city quick-jump presets.
    /// </summary>
    public class MapSelectorController : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler, IScrollHandler
    {
        public const double SectorSizeMeters = 2000.0;
        public const int MinZoom = 6;
        public const int MaxZoom = 17;

        public event Action<GeoSector> OnSectorConfirmed;

        [Header("Core References")]
        [SerializeField] private IgnTileLoader tileLoader;
        [SerializeField] private RectTransform mapViewport;
        [SerializeField] private RectTransform tilesContainer;
        [SerializeField] private RectTransform selectionBox;
        [SerializeField] private UnityEngine.UI.Text selectionBoxLabel;

        [Header("HUD References")]
        [SerializeField] private UnityEngine.UI.Text coordsText;
        [SerializeField] private UnityEngine.UI.Text lambertText;
        [SerializeField] private UnityEngine.UI.Text statusText;
        [SerializeField] private UnityEngine.UI.Text layerButtonLabel;
        [SerializeField] private UnityEngine.UI.Button layerToggleButton;
        [SerializeField] private UnityEngine.UI.Button confirmButton;

        [Header("Initial View (Paris)")]
        [SerializeField] private double centerLat = 48.8566;
        [SerializeField] private double centerLon = 2.3522;
        [SerializeField] private double zoom = 13.0;

        private double _selectedLat;
        private double _selectedLon;
        private IgnMapLayer _activeLayer = IgnMapLayer.PlanIgnV2;

        private readonly Dictionary<string, UnityEngine.UI.RawImage> _activeTiles = new Dictionary<string, UnityEngine.UI.RawImage>();
        private readonly Stack<UnityEngine.UI.RawImage> _tilePool = new Stack<UnityEngine.UI.RawImage>();
        private readonly List<string> _keysToRemove = new List<string>();
        private readonly HashSet<string> _visibleKeys = new HashSet<string>();

        private Vector2 _pointerDownPos;
        private bool _isDragging;
        private const float DragThresholdPx = 6f;

        private Vector2 _lastViewportSize;
        private bool _dirty = true;

        public GeoSector CurrentSector => GeoSector.FromCenterWgs84(_selectedLat, _selectedLon, SectorSizeMeters);

        public void Configure(
            IgnTileLoader loader,
            RectTransform viewport,
            RectTransform tilesRoot,
            RectTransform box,
            UnityEngine.UI.Text boxLabel,
            UnityEngine.UI.Text coords,
            UnityEngine.UI.Text lambert,
            UnityEngine.UI.Text status,
            UnityEngine.UI.Text layerLabel,
            UnityEngine.UI.Button toggleLayerBtn,
            UnityEngine.UI.Button confirmBtn)
        {
            tileLoader = loader;
            mapViewport = viewport;
            tilesContainer = tilesRoot;
            selectionBox = box;
            selectionBoxLabel = boxLabel;
            coordsText = coords;
            lambertText = lambert;
            statusText = status;
            layerButtonLabel = layerLabel;
            layerToggleButton = toggleLayerBtn;
            confirmButton = confirmBtn;
        }

        private void Awake()
        {
            _selectedLat = centerLat;
            _selectedLon = centerLon;
        }

        private void Start()
        {
            if (layerToggleButton != null)
                layerToggleButton.onClick.AddListener(ToggleLayer);

            if (confirmButton != null)
                confirmButton.onClick.AddListener(ConfirmSelection);

            UpdateLayerButtonLabel();
            UpdateHud();
            _dirty = true;
        }

        private void Update()
        {
            if (mapViewport == null) return;

            Vector2 size = mapViewport.rect.size;
            if (size != _lastViewportSize)
            {
                _lastViewportSize = size;
                _dirty = true;
            }

            if (_dirty)
            {
                _dirty = false;
                RefreshTiles();
                RefreshSelectionBox();
            }
        }

        public void JumpTo(double lat, double lon, double targetZoom = 13.0)
        {
            GeoProjection.ClampToMetropolitanFrance(ref lat, ref lon);
            centerLat = lat;
            centerLon = lon;
            _selectedLat = lat;
            _selectedLon = lon;
            zoom = Math.Clamp(targetZoom, MinZoom, MaxZoom);
            UpdateHud();
            _dirty = true;
        }

        public void ToggleLayer()
        {
            _activeLayer = _activeLayer == IgnMapLayer.PlanIgnV2
                ? IgnMapLayer.OrthoPhotos
                : IgnMapLayer.PlanIgnV2;

            if (tileLoader != null)
                tileLoader.CancelAllPending();

            ReleaseAllActiveTiles();
            UpdateLayerButtonLabel();
            _dirty = true;
        }

        public void ConfirmSelection()
        {
            GeoSector sector = CurrentSector;
            Debug.Log($"[GeoUnity] Sector confirmed: {sector}");
            if (statusText != null)
            {
                statusText.text = $"Secteur validé ({sector.SizeMeters:F0}m × {sector.SizeMeters:F0}m) — prêt pour la génération 3D !";
                statusText.color = new Color(0.45f, 0.95f, 0.65f, 1f);
            }
            OnSectorConfirmed?.Invoke(sector);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            _pointerDownPos = eventData.position;
            _isDragging = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;

            if (!_isDragging && Vector2.Distance(eventData.position, _pointerDownPos) > DragThresholdPx)
            {
                _isDragging = true;
            }

            if (!_isDragging || mapViewport == null) return;

            // Convert screen delta to local viewport pixel delta
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                mapViewport, eventData.position, eventData.pressEventCamera, out Vector2 currLocal);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                mapViewport, eventData.position - eventData.delta, eventData.pressEventCamera, out Vector2 prevLocal);
            Vector2 localDelta = currLocal - prevLocal;

            int tileZ = GetTileZoom();
            double scale = Math.Pow(2.0, zoom - tileZ);
            double tilePx = GeoProjection.TileSizePx * scale;

            GeoProjection.Wgs84ToTileCoords(centerLat, centerLon, tileZ, out double cx, out double cy);
            cx -= localDelta.x / tilePx;
            cy += localDelta.y / tilePx; // UI Y is up, tile Y is down

            GeoProjection.TileCoordsToWgs84(cx, cy, tileZ, out double newLat, out double newLon);
            GeoProjection.ClampToMetropolitanFrance(ref newLat, ref newLon);
            centerLat = newLat;
            centerLon = newLon;

            _dirty = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;

            // Click (without drag) places the fixed 2 km x 2 km sector at the clicked location
            if (!_isDragging && mapViewport != null)
            {
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    mapViewport, eventData.position, eventData.pressEventCamera, out Vector2 localPos))
                {
                    LocalPointToWgs84(localPos, out double clickedLat, out double clickedLon);
                    GeoProjection.ClampToMetropolitanFrance(ref clickedLat, ref clickedLon);
                    _selectedLat = clickedLat;
                    _selectedLon = clickedLon;
                    UpdateHud();
                    RefreshSelectionBox();
                }
            }

            _isDragging = false;
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (mapViewport == null) return;

            float scrollY = eventData.scrollDelta.y;
            if (Mathf.Approximately(scrollY, 0f)) return;

            // Zoom centered on cursor position
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                mapViewport, eventData.position, eventData.pressEventCamera, out Vector2 cursorLocal);
            LocalPointToWgs84(cursorLocal, out double anchorLat, out double anchorLon);

            double newZoom = Math.Clamp(zoom + Math.Sign(scrollY) * 0.5, MinZoom, MaxZoom);
            if (Math.Abs(newZoom - zoom) < 1e-4) return;

            zoom = newZoom;

            // Recompute map center so the geographic point under the cursor stays fixed
            int tileZ = GetTileZoom();
            double scale = Math.Pow(2.0, zoom - tileZ);
            double tilePx = GeoProjection.TileSizePx * scale;

            GeoProjection.Wgs84ToTileCoords(anchorLat, anchorLon, tileZ, out double ax, out double ay);
            double newCx = ax - cursorLocal.x / tilePx;
            double newCy = ay + cursorLocal.y / tilePx;

            GeoProjection.TileCoordsToWgs84(newCx, newCy, tileZ, out double newCenterLat, out double newCenterLon);
            GeoProjection.ClampToMetropolitanFrance(ref newCenterLat, ref newCenterLon);
            centerLat = newCenterLat;
            centerLon = newCenterLon;

            UpdateHud();
            _dirty = true;
        }

        private int GetTileZoom()
        {
            return Math.Clamp((int)Math.Round(zoom), MinZoom, MaxZoom);
        }

        private void LocalPointToWgs84(Vector2 localPos, out double lat, out double lon)
        {
            int tileZ = GetTileZoom();
            double scale = Math.Pow(2.0, zoom - tileZ);
            double tilePx = GeoProjection.TileSizePx * scale;

            GeoProjection.Wgs84ToTileCoords(centerLat, centerLon, tileZ, out double cx, out double cy);
            double tx = cx + localPos.x / tilePx;
            double ty = cy - localPos.y / tilePx;
            GeoProjection.TileCoordsToWgs84(tx, ty, tileZ, out lat, out lon);
        }

        private Vector2 Wgs84ToLocalPoint(double lat, double lon)
        {
            int tileZ = GetTileZoom();
            double scale = Math.Pow(2.0, zoom - tileZ);
            double tilePx = GeoProjection.TileSizePx * scale;

            GeoProjection.Wgs84ToTileCoords(centerLat, centerLon, tileZ, out double cx, out double cy);
            GeoProjection.Wgs84ToTileCoords(lat, lon, tileZ, out double tx, out double ty);

            float x = (float)((tx - cx) * tilePx);
            float y = (float)((cy - ty) * tilePx);
            return new Vector2(x, y);
        }

        private void RefreshTiles()
        {
            if (tilesContainer == null || mapViewport == null) return;

            Vector2 vpSize = mapViewport.rect.size;
            if (vpSize.x <= 1f || vpSize.y <= 1f) return;

            int tileZ = GetTileZoom();
            int maxTileIndex = (1 << tileZ) - 1;
            double scale = Math.Pow(2.0, zoom - tileZ);
            double tilePx = GeoProjection.TileSizePx * scale;

            GeoProjection.Wgs84ToTileCoords(centerLat, centerLon, tileZ, out double centerTx, out double centerTy);

            int halfCols = Mathf.CeilToInt((vpSize.x * 0.5f) / (float)tilePx) + 1;
            int halfRows = Mathf.CeilToInt((vpSize.y * 0.5f) / (float)tilePx) + 1;

            int centerCol = (int)Math.Floor(centerTx);
            int centerRow = (int)Math.Floor(centerTy);

            int minCol = Math.Max(0, centerCol - halfCols);
            int maxCol = Math.Min(maxTileIndex, centerCol + halfCols);
            int minRow = Math.Max(0, centerRow - halfRows);
            int maxRow = Math.Min(maxTileIndex, centerRow + halfRows);

            _visibleKeys.Clear();

            for (int ty = minRow; ty <= maxRow; ty++)
            {
                for (int tx = minCol; tx <= maxCol; tx++)
                {
                    string key = IgnTileLoader.MakeKey(_activeLayer, tileZ, tx, ty);
                    _visibleKeys.Add(key);

                    if (!_activeTiles.TryGetValue(key, out var rawImg))
                    {
                        rawImg = AcquireTileImage();
                        rawImg.name = $"Tile_{key}";
                        _activeTiles[key] = rawImg;

                        if (tileLoader != null && tileLoader.TryGetCached(key, out var cachedTex))
                        {
                            rawImg.texture = cachedTex;
                            rawImg.color = Color.white;
                        }
                        else
                        {
                            rawImg.texture = null;
                            rawImg.color = new Color(0.11f, 0.14f, 0.18f, 1f);
                            if (tileLoader != null)
                            {
                                string capturedKey = key;
                                tileLoader.RequestTile(_activeLayer, tileZ, tx, ty, (loadedKey, tex) =>
                                {
                                    if (tex != null && _activeTiles.TryGetValue(capturedKey, out var img) && img != null)
                                    {
                                        img.texture = tex;
                                        img.color = Color.white;
                                    }
                                });
                            }
                        }
                    }

                    // Position tile center relative to viewport center
                    float posX = (float)((tx + 0.5 - centerTx) * tilePx);
                    float posY = (float)((centerTy - (ty + 0.5)) * tilePx);
                    var rt = rawImg.rectTransform;
                    rt.anchoredPosition = new Vector2(posX, posY);
                    rt.sizeDelta = new Vector2((float)tilePx + 0.5f, (float)tilePx + 0.5f); // +0.5px avoids subpixel seams
                }
            }

            // Recycle off-screen tiles
            _keysToRemove.Clear();
            foreach (var kvp in _activeTiles)
            {
                if (!_visibleKeys.Contains(kvp.Key))
                    _keysToRemove.Add(kvp.Key);
            }
            foreach (var key in _keysToRemove)
            {
                var img = _activeTiles[key];
                _activeTiles.Remove(key);
                ReleaseTileImage(img);
            }
        }

        private void RefreshSelectionBox()
        {
            if (selectionBox == null) return;

            Vector2 localPos = Wgs84ToLocalPoint(_selectedLat, _selectedLon);
            double metersPerPx = GeoProjection.GroundMetersPerPixel(_selectedLat, zoom);
            float boxPx = (float)(SectorSizeMeters / metersPerPx);

            selectionBox.anchoredPosition = localPos;
            selectionBox.sizeDelta = new Vector2(Mathf.Max(boxPx, 12f), Mathf.Max(boxPx, 12f));

            if (selectionBoxLabel != null)
            {
                selectionBoxLabel.text = boxPx >= 56f ? "2 km × 2 km" : "2 km";
            }
        }

        private void UpdateHud()
        {
            GeoSector sector = CurrentSector;
            if (coordsText != null)
            {
                coordsText.text = $"Centre WGS84 : {sector.CenterLat:F5}° N, {sector.CenterLon:F5}° E  |  Zoom {zoom:F1}";
            }
            if (lambertText != null)
            {
                lambertText.text = $"Lambert-93 : X {sector.CenterLambertX:F0} m, Y {sector.CenterLambertY:F0} m  (2000 × 2000 m)";
            }
            if (statusText != null)
            {
                statusText.text = "Cliquer sur la carte pour placer le secteur · Glisser pour déplacer · Molette pour zoomer";
                statusText.color = new Color(0.75f, 0.82f, 0.9f, 1f);
            }
        }

        private void UpdateLayerButtonLabel()
        {
            if (layerButtonLabel != null)
            {
                layerButtonLabel.text = _activeLayer == IgnMapLayer.PlanIgnV2
                    ? "Fond : Plan IGN v2"
                    : "Fond : Orthophoto IGN";
            }
        }

        private UnityEngine.UI.RawImage AcquireTileImage()
        {
            if (_tilePool.Count > 0)
            {
                var recycled = _tilePool.Pop();
                recycled.gameObject.SetActive(true);
                return recycled;
            }

            var go = new GameObject("Tile", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
            go.transform.SetParent(tilesContainer, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            var img = go.GetComponent<UnityEngine.UI.RawImage>();
            img.raycastTarget = false;
            return img;
        }

        private void ReleaseTileImage(UnityEngine.UI.RawImage img)
        {
            if (img == null) return;
            img.texture = null;
            img.gameObject.SetActive(false);
            _tilePool.Push(img);
        }

        private void ReleaseAllActiveTiles()
        {
            foreach (var img in _activeTiles.Values)
            {
                ReleaseTileImage(img);
            }
            _activeTiles.Clear();
        }
    }
}

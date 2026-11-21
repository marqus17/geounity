using System;
using System.Threading.Tasks;
using GeoUnity.Geo;
using GeoUnity.Ign;
using GeoUnity.MapSelector;
using UnityEngine;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Orchestrates the transition between the 2D IGN sector selector and the 3D reconstructed world
    /// (RGE ALTI terrain + Orthophoto texture + BD TOPO extruded buildings).
    /// </summary>
    public class GeoWorldManager : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private MapSelectorController mapSelector;
        [SerializeField] private GameObject mapSelectorCanvas;
        [SerializeField] private GameObject loadingOverlay;
        [SerializeField] private UnityEngine.UI.Text loadingStatusText;
        [SerializeField] private GameObject worldHudCanvas;
        [SerializeField] private UnityEngine.UI.Text worldInfoText;
        [SerializeField] private UnityEngine.UI.Button backToMapButton;
        [SerializeField] private UnityEngine.UI.Button resetCameraButton;
        [SerializeField] private OrbitFlyCameraController cameraController;

        private Ign3DDataClient _dataClient;
        private GameObject _worldRoot;
        private bool _isLoading;

        public void Configure(
            MapSelectorController selector,
            GameObject mapCanvas,
            GameObject loadingPanel,
            UnityEngine.UI.Text loadingText,
            GameObject hud3DCanvas,
            UnityEngine.UI.Text info3DText,
            UnityEngine.UI.Button backBtn,
            UnityEngine.UI.Button resetCamBtn,
            OrbitFlyCameraController camCtrl)
        {
            mapSelector = selector;
            mapSelectorCanvas = mapCanvas;
            loadingOverlay = loadingPanel;
            loadingStatusText = loadingText;
            worldHudCanvas = hud3DCanvas;
            worldInfoText = info3DText;
            backToMapButton = backBtn;
            resetCameraButton = resetCamBtn;
            cameraController = camCtrl;
        }

        private void Awake()
        {
            _dataClient = new Ign3DDataClient();
        }

        private void Start()
        {
            if (mapSelector != null)
                mapSelector.OnSectorConfirmed += HandleSectorConfirmed;

            if (backToMapButton != null)
                backToMapButton.onClick.AddListener(ReturnToMapSelector);

            if (resetCameraButton != null)
                resetCameraButton.onClick.AddListener(() =>
                {
                    if (cameraController != null) cameraController.ResetView(false);
                });

            ShowMapSelectorState();
        }

        private void OnDestroy()
        {
            if (mapSelector != null)
                mapSelector.OnSectorConfirmed -= HandleSectorConfirmed;
        }

        private void HandleSectorConfirmed(GeoSector sector)
        {
            if (_isLoading) return;
            _ = LoadAndBuildSectorAsync(sector);
        }

        public async Task LoadAndBuildSectorAsync(GeoSector sector)
        {
            _isLoading = true;
            SetLoading(true, "Téléchargement des données IGN (RGE ALTI, Orthophoto, BD TOPO)...");

            try
            {
                // Clear previous 3D world if any
                if (_worldRoot != null)
                {
                    Destroy(_worldRoot);
                    _worldRoot = null;
                }

                // Download DEM, Orthophoto, Buildings, Vegetation, Water, RPG Crops, and OSM Road Signs in parallel
                var demTask = _dataClient.DownloadElevationGridAsync(sector, resolution: 257);
                var orthoTask = _dataClient.DownloadOrthophotoAsync(sector, size: 2048);
                var buildingsTask = _dataClient.DownloadBuildingsAsync(sector, maxFeatures: 12000, count =>
                {
                    SetLoading(true, $"Téléchargement BD TOPO... ({count} bâtiments reçus)");
                });
                var vegetationTask = _dataClient.DownloadVegetationAsync(sector, maxFeatures: 2000);
                var waterTask = _dataClient.DownloadWaterAsync(sector, maxFeatures: 1000);
                var cropsTask = _dataClient.DownloadCropParcelsAsync(sector, maxFeatures: 1000);
                var signsTask = _dataClient.DownloadRoadSignsAsync(sector);

                await Task.WhenAll(demTask, orthoTask, buildingsTask, vegetationTask, waterTask, cropsTask, signsTask);

                var grid = demTask.Result;
                var ortho = orthoTask.Result;
                var buildings = buildingsTask.Result;
                var vegetation = vegetationTask.Result;
                var water = waterTask.Result;
                var crops = cropsTask.Result;
                var signs = signsTask.Result;

                SetLoading(true, $"Génération 3D ({buildings.Count} bâtiments, {crops.Count} parcelles RPG, {signs.Count} panneaux/feux)...");
                await Task.Yield();

                _worldRoot = new GameObject($"GeoWorld_{Math.Round(sector.CenterLambertX)}_{Math.Round(sector.CenterLambertY)}");
                TerrainMeshBuilder.Build(sector, grid, ortho, _worldRoot.transform);
                WaterMeshBuilder.Build(sector, grid, water, _worldRoot.transform);
                BuildingMeshBuilder.Build(sector, grid, buildings, _worldRoot.transform);
                VegetationBuilder.Build(sector, grid, vegetation, _worldRoot.transform, out int treeCount);
                CropFieldBuilder.Build(sector, grid, crops, _worldRoot.transform, out int cropCount);
                RoadSignBuilder.Build(sector, grid, signs, _worldRoot.transform);

                float centerHeight = grid.SampleHeight(sector.CenterLambertX, sector.CenterLambertY) - grid.MinElevation;

                // Ensure far clip plane handles 2 km x 2 km comfortably
                if (Camera.main != null && Camera.main.farClipPlane < 10000f)
                    Camera.main.farClipPlane = 10000f;

                if (cameraController != null)
                {
                    cameraController.FocusOnSector(centerHeight, (float)sector.SizeMeters);
                    cameraController.SetControlsEnabled(true);
                }

                if (worldInfoText != null)
                {
                    worldInfoText.text =
                        $"Secteur 2 km × 2 km  |  {sector.CenterLat:F4}° N, {sector.CenterLon:F4}° E\n" +
                        $"Bâtiments : {buildings.Count}  |  Arbres : {treeCount}  |  RPG : {crops.Count} parcelles  |  Feux/Panneaux : {signs.Count}\n" +
                        $"Clic gauche / ZQSD : Déplacer  ·  Clic droit : Orbiter  ·  Molette : Zoom  ·  R : Recentrer";
                }

                // Switch UI to 3D View
                if (mapSelectorCanvas != null) mapSelectorCanvas.SetActive(false);
                if (worldHudCanvas != null) worldHudCanvas.SetActive(true);

                Debug.Log($"[GeoWorldManager] Built 3D sector: {buildings.Count} buildings, {treeCount} trees, {crops.Count} RPG parcels ({cropCount} elements), {signs.Count} road signs, {water.Count} water.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeoWorldManager] Failed to build 3D sector: {ex}");
            }
            finally
            {
                SetLoading(false, string.Empty);
                _isLoading = false;
            }
        }

        public void ReturnToMapSelector()
        {
            if (_isLoading) return;
            ShowMapSelectorState();
        }

        private void ShowMapSelectorState()
        {
            if (cameraController != null) cameraController.SetControlsEnabled(false);
            if (mapSelectorCanvas != null) mapSelectorCanvas.SetActive(true);
            if (worldHudCanvas != null) worldHudCanvas.SetActive(false);
            if (loadingOverlay != null) loadingOverlay.SetActive(false);
        }

        private void SetLoading(bool visible, string message)
        {
            if (loadingOverlay != null) loadingOverlay.SetActive(visible);
            if (loadingStatusText != null) loadingStatusText.text = message;
        }
    }
}

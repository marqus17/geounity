# GeoUnity

## Project Overview

- **Unity project**: `GeoUnity/` (Unity `6000.6.9f1`, URP `17.6.0` 3D, Desktop).
- **Key packages**: Input System (`1.20.1`), uGUI (`2.6.0`), Cinemachine (`3.1.7`).
- **Main scene**: `GeoUnity/Assets/default.unity` (note: `ProjectSettings/EditorBuildSettings.asset` still lists `Assets/Scenes/SampleScene.unity`).
- **Scope**: Real-time 3D geographic reconstruction of any **2 km × 2 km** sector in **Metropolitan France** (`lat ∈ [41.2, 51.2]`, `lon ∈ [-5.5, 9.8]`) at `1 Unity unit = 1 meter`, starting from a full-screen 2D IGN WMTS sector selector and transitioning into a procedural 3D world with an orbit/pan/zoom exploration camera.

## Architecture (`GeoUnity/Assets/`)

All C# scripts live under `GeoUnity/Assets/Scripts/` with namespaces matching their folder:

- `Scripts/Geo/` (`GeoUnity.Geo`)
  - `GeoProjection.cs`: `GeoSector` struct and conversions between `WGS84 (EPSG:4326)`, `Web Mercator (EPSG:3857)` (with `cos(lat)` ground scale correction), and `RGF93 / Lambert-93 (EPSG:2154)`.
- `Scripts/Ign/` (`GeoUnity.Ign`)
  - `IgnTileLoader.cs`: 2D IGN WMTS tile downloader (`PLANIGNV2`, `ORTHOIMAGERY.ORTHOPHOTOS`) with LRU memory cache (256 tiles) and disk cache (`Application.persistentDataPath/IgnTileCache`).
  - `IgnGeocoder.cs`: French city/address search via Géoplateforme BAN (`data.geopf.fr/geocodage/search`), filtered to Metropolitan France.
  - `Ign3DDataClient.cs`: Parallel downloader + disk cache (`Application.persistentDataPath/Ign3DCache/L93_<X>_<Y>_<Size>`) and lightweight custom JSON parsers (no Newtonsoft dependency) for:
    - **DEM**: WMS-R `ELEVATION.ELEVATIONGRIDCOVERAGE.HIGHRES` (RGE ALTI, `257×257` 32-bit float BIL).
    - **Orthophoto**: WMS-R `ORTHOIMAGERY.ORTHOPHOTOS` (`2048×2048` JPEG, `EPSG:2154`).
    - **Buildings**: WFS `BDTOPO_V3:batiment` (paginated GeoJSON, up to 12,000 features, with `nature`, `usage_1`, `nombre_de_logements`, wall/roof materials).
    - **Vegetation**: WFS `BDTOPO_V3:zone_de_vegetation`.
    - **Water**: WFS `BDTOPO_V3:surface_hydrographique`.
    - **Roads**: WFS `BDTOPO_V3:troncon_de_route`.
    - **Bridges**: WFS `BDTOPO_V3:construction_lineaire` & `BDTOPO_V3:construction_surfacique`.
    - **Landmarks / Statues**: WFS `BDTOPO_V3:construction_ponctuelle` + OSM Overpass (`monument`, `memorial`, `statue`, `fountain`, `water_tower`).
    - **Crops**: WFS `IGNF_RPG_PARCELLES-AGRICOLES-CATEGORISEES_2024:parcelles_agricole_categorisees_2024`.
    - **Road signage**: OSM Overpass (`overpass.openstreetmap.fr` primary, with `overpass-api.de` and `overpass.private.coffee` fallbacks) for traffic lights, Stop, and Give Way signs.
- `Scripts/MapSelector/` (`GeoUnity.MapSelector`)
  - `MapSelectorController.cs`, `MapSearchBarController.cs`, `CityPresetButton.cs`: Full-screen 2D slippy map, search bar, city presets, Plan/Ortho toggle, and fixed 2 km × 2 km selection box.
- `Scripts/World3D/` (`GeoUnity.World3D`)
  - `GeoWorldManager.cs`: Orchestrates 2D ↔ 3D transitions, parallel layer downloads, procedural world generation, HUD updates, and control mode switching.
  - **Procedural builders** (static `Build(...)` entry points, batched `UInt32` meshes, zero external 3D assets):
    - `TerrainMeshBuilder.cs`, `WaterMeshBuilder.cs`, `RoadOverlayBuilder.cs` (+ `RoadNetwork`), `BridgeBuilder.cs` (4 bridge archetypes), `BuildingMeshBuilder.cs`, `ArchitecturalTextureGenerator.cs` (building-type & per-face house textures), `LandmarkBuilder.cs` (statues, monuments, calvaires, fountains, water towers), `VegetationBuilder.cs`, `CropFieldBuilder.cs`, `RoadSignBuilder.cs`, `EarCut2D.cs`.
  - **Exploration, audio & HUD**:
    - `OrbitFlyCameraController.cs`: Smooth orbit/pan/zoom camera.
    - `VehicleModeController.cs`: `ControlMode` (`Camera`, `Ulm`, `Car`) — procedural 3D ULM (flight, crash/respawn, border auto-U-turn) and Car (restricted to `RoadNetwork` with auto-reset).
    - `VehicleAudioController.cs`: Seamless looping procedural `AudioClip` synthesis (ULM propeller/wind, Car engine/reverse whine/brake screech).
    - `CropLegendToggle.cs`: Collapsible RPG 2024 crop legend panel.
- `Shaders/` (custom URP shaders)
  - `GeoUnityWater.shader` (`GeoUnity/Water`)
  - `GeoUnityCropField.shader` (`GeoUnity/CropField`)
  - `GeoUnityRoadOverlay.shader` (`GeoUnity/RoadOverlay`)

## Conventions & Guidelines

- **Coordinate convention**: 3D world positions are local Unity `(X, Z)` meters centered on `(sector.CenterLambertX, sector.CenterLambertY)` (`[-1000, +1000]`), with `Y` normalized relative to `ElevationGrid.MinElevation`. Always use `CultureInfo.InvariantCulture` when parsing/formatting geographic numbers or URLs.
- **Procedural-first**: Prefer procedural meshes and textures in C# over adding binary asset packs; keep meshes batched (`IndexFormat.UInt32`) to minimize draw calls.
- **Input & UI**: Use the Unity Input System (`UnityEngine.InputSystem`: `Keyboard.current`, `Mouse.current`) and uGUI (`UnityEngine.UI`), checking `EventSystem.current.IsPointerOverGameObject()` before handling 3D mouse input. UI copy is in **French**; code, comments, and logs are in **English**.
- **Public APIs**: IGN Géoplateforme and OSM Overpass endpoints require no API keys. Always cache downloaded responses on disk and handle network failures gracefully with safe fallbacks.
- **Unity gameplay & craft rule**: When building gameplay, feel, audio, shell, or live features, load the `craft-game-floors` skill; if it isn't installed, ask the user whether to run `unity skill install <client>` (ask: never run it without a yes).

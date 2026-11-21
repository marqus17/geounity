# GeoUnity

Reconstruction 3D temps réel de n'importe quel secteur de **France métropolitaine** (2 km × 2 km) dans **Unity 6 (URP)** à partir des services SIG ouverts de la **Géoplateforme IGN**.

---

## Fonctionnalités

### 1. Sélecteur de secteur 2D au lancement
- Carte interactive plein écran consommant les tuiles **IGN WMTS** (`PLANIGNV2` et `ORTHOIMAGERY.ORTHOPHOTOS`).
- **Barre de recherche ville / adresse** branchée sur l'API officielle de géocodage **Géoplateforme / BAN** (`data.geopf.fr/geocodage/search`).
- Carré de sélection fixe **2 km × 2 km** avec correction géodésique `cos(lat)` en Web Mercator et affichage temps réel des coordonnées **WGS84 (`EPSG:4326`)** et **Lambert-93 (`EPSG:2154`)**.
- Raccourcis rapides (**Paris**, **Lyon**, **Chamonix**, **Marseille**) et cache disque local des tuiles.

### 2. Reconstruction 3D (1 unité Unity = 1 mètre)
Toutes les couches sont téléchargées en parallèle dans la projection métrique native **Lambert-93 (`EPSG:2154`)** et mises en cache localement :

| Couche | Service IGN Géoplateforme | Rendu 3D dans Unity |
| :--- | :--- | :--- |
| **Relief (MNT)** | `WMS-R` `ELEVATION.ELEVATIONGRIDCOVERAGE.HIGHRES` (RGE ALTI, format `BIL 32-bit float`) | Mesh terrain `257 × 257` (`UInt32`) + jupe diorama |
| **Texture sol** | `WMS-R` `ORTHOIMAGERY.ORTHOPHOTOS` (`2048 × 2048` en `EPSG:2154`) | Orthophoto haute résolution alignée au pixel près sur la BBox |
| **Bâtiments 3D** | `WFS 2.0.0` `BDTOPO_V3:batiment` (GeoJSON paginé jusqu'à 12 000 objets) | Extrusion procédurale, **toits en pente (4 pans / croupes)** ou terrasses, fondations calées sur le relief, **textures procédurales** de façades & toitures avec UVs métriques |
| **Végétation** | `WFS 2.0.0` `BDTOPO_V3:zone_de_vegetation` | Forêts 3D (**conifères**, **feuillus**, **mixte**, **arbustes/haies**) distribuées par *Point-in-Polygon* sur le relief |
| **Eau** | `WFS 2.0.0` `BDTOPO_V3:surface_hydrographique` | Surfaces découpées (Sutherland-Hodgman) avec **shader URP d'eau translucide**, vagues animées, reflets Fresnel et reflets solaires |
| **Cultures (RPG 2024)** | `WFS 2.0.0` `IGNF_RPG_PARCELLES-AGRICOLES-CATEGORISEES_2024` | Volumes bas translucides à sillons, subdivisés pour épouser le relief (`blé`, `colza/tournesol`, `maïs`, `vignes`, `vergers`, `prairies`) + **légende minimisable** |
| **Signalisation** | **OpenStreetMap Overpass France** (`overpass.openstreetmap.fr`) | **Feux tricolores**, panneaux **Stop** et **Cédez-le-passage** 3D batchés aux carrefours |

---

## Prérequis & lancement

- **Unity** : `6000.6.9f1` (URP 3D, Desktop)
- **Connexion Internet** : requise lors du premier chargement d'un secteur (aucune clé API IGN nécessaire pour ces flux publics ; les données sont ensuite mises en cache dans `Application.persistentDataPath`).

### Lancer le projet
1. Cloner le dépôt :
   ```bash
   git clone git@github.com:marqus17/geounity.git
   ```
2. Ouvrir le dossier `GeoUnity/` dans Unity (`6000.6.9f1`).
3. Ouvrir la scène `Assets/default.unity` et appuyer sur **Play**.

---

## Contrôles

### Carte 2D (sélection)
- **Clic gauche (glisser)** : déplacer la carte
- **Molette** : zoomer / dézoomer (centré sur le curseur)
- **Clic gauche (simple)** : placer le carré fixe **2 km × 2 km**
- **Barre de recherche** : taper une ville ou adresse puis **Entrée** ou **Chercher**
- **Fond : Plan IGN v2 / Orthophoto** : basculer le fond de carte
- **Valider ce secteur 3D** : lancer la reconstruction 3D

### Vue 3D (exploration)
- **Clic gauche (glisser)** ou **Z Q S D / Flèches** : déplacer la caméra sur le secteur
- **Clic droit (glisser)** ou **molette pressée** : orbiter autour du point cible
- **Molette** : zoomer / dézoomer
- **R** : recentrer la vue sur le secteur
- **← Changer de secteur 2D** : revenir à la carte 2D pour explorer un autre lieu

---

## Architecture du code (`GeoUnity/Assets/`)

```text
Assets/
├── Scripts/
│   ├── Geo/
│   │   └── GeoProjection.cs                # WGS84 <-> Web Mercator <-> Lambert-93 & GeoSector
│   ├── Ign/
│   │   ├── IgnTileLoader.cs                # Tuiles WMTS 2D + cache mémoire LRU / disque
│   │   ├── IgnGeocoder.cs                  # Recherche d'adresses / communes (Géoplateforme BAN)
│   │   └── Ign3DDataClient.cs              # Client WMS-R (RGE ALTI, Ortho) & WFS (BD TOPO)
│   ├── MapSelector/
│   │   ├── MapSelectorController.cs        # Carte 2D interactive & carré 2 km x 2 km
│   │   ├── MapSearchBarController.cs       # Barre de recherche et liste de résultats
│   │   └── CityPresetButton.cs             # Boutons de raccourcis villes
│   └── World3D/
│       ├── GeoWorldManager.cs              # Orchestration 2D <-> 3D & téléchargements parallèles
│       ├── TerrainMeshBuilder.cs           # Génération du mesh MNT + orthophoto + jupe diorama
│       ├── BuildingMeshBuilder.cs          # Extrusion bâtiments, toits en pente & UVs métriques
│       ├── ArchitecturalTextureGenerator.cs# Textures procédurales 512x512 (façades & toits)
│       ├── VegetationBuilder.cs            # Arbres & forêts 3D batchés depuis BD TOPO
│       ├── WaterMeshBuilder.cs             # Surfaces d'eau découpées & calées sur le MNT
│       ├── CropFieldBuilder.cs             # Volumes bas translucides RPG 2024 calés sur le relief
│       ├── RoadSignBuilder.cs              # Feux tricolores & panneaux Stop / Cédez-le-passage (OSM)
│       ├── CropLegendToggle.cs             # Panneau de légende des cultures minimisable
│       ├── EarCut2D.cs                     # Triangulateur 2D de polygones concaves
│       └── OrbitFlyCameraController.cs     # Caméra 3D orbit / pan / zoom
└── Shaders/
    ├── GeoUnityWater.shader                # Shader URP eau translucide, vagues animées & Fresnel
    └── GeoUnityCropField.shader            # Shader URP parcelles agricoles translucides à sillons
```

---

## Crédits & données

Données géographiques ouvertes fournies par l'**IGN — Géoplateforme** (Licence Ouverte / Open Licence Etalab 2.0) et **OpenStreetMap** (ODbL) :
- WMTS : `GEOGRAPHICALGRIDSYSTEMS.PLANIGNV2`, `ORTHOIMAGERY.ORTHOPHOTOS`
- WMS-R : `ELEVATION.ELEVATIONGRIDCOVERAGE.HIGHRES` (RGE ALTI), `ORTHOIMAGERY.ORTHOPHOTOS`
- WFS : `BDTOPO_V3:batiment`, `BDTOPO_V3:zone_de_vegetation`, `BDTOPO_V3:surface_hydrographique`, `IGNF_RPG_PARCELLES-AGRICOLES-CATEGORISEES_2024:parcelles_agricole_categorisees_2024`
- Géocodage : `data.geopf.fr/geocodage/search`
- Signalisation routière : **OpenStreetMap** via Overpass France (`overpass.openstreetmap.fr`)

using System;
using GeoUnity.Geo;
using GeoUnity.Ign;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace GeoUnity.World3D
{
    public enum ControlMode
    {
        Camera,
        Ulm,
        Car
    }

    /// <summary>
    /// Manages 3D exploration modes: Free Orbit Camera, 3rd-person ULM flight (with crash + border auto-U-turn),
    /// and 3rd-person Car driving restricted to IGN BD TOPO roads (with auto-reset on off-road / obstacle / water).
    /// </summary>
    public class VehicleModeController : MonoBehaviour
    {
        public event Action<ControlMode> OnModeChanged;

        [SerializeField] private OrbitFlyCameraController orbitCamera;
        [SerializeField] private UnityEngine.UI.Text statusToastText;

        private GeoSector _sector;
        private ElevationGrid _grid;
        private RoadNetwork _roads;
        private float _sectorHalfSize = 1000f;

        private ControlMode _mode = ControlMode.Camera;
        public ControlMode CurrentMode => _mode;

        // ULM state
        private GameObject _ulmRoot;
        private Transform _ulmPropeller;
        private Vector3 _ulmPos;
        private float _ulmYaw;
        private float _ulmPitch;
        private float _ulmRoll;
        private float _ulmSpeed = 48f;
        private bool _ulmCrashed;
        private float _ulmRespawnTimer;
        private bool _ulmAutoTurning;

        // Car state
        private GameObject _carRoot;
        private Transform[] _carWheels;
        private Transform[] _carFrontSteerPivots;
        private Material _carTailLightMat;
        private Vector3 _carPos;
        private float _carYaw;
        private float _carSpeed;
        private float _carSteerVisual;
        private Vector2 _lastSafeRoadPos;
        private float _lastSafeRoadYaw;
        private float _carResetCooldown;

        // Toast timer
        private float _toastTimer;
        private VehicleAudioController _audio;

        private readonly Collider[] _overlapBuffer = new Collider[8];

        private void Awake()
        {
            _audio = GetComponent<VehicleAudioController>();
            if (_audio == null)
                _audio = gameObject.AddComponent<VehicleAudioController>();
        }

        public void Configure(OrbitFlyCameraController camCtrl, UnityEngine.UI.Text toastText)
        {
            orbitCamera = camCtrl;
            statusToastText = toastText;
        }

        public void SetWorldData(GeoSector sector, ElevationGrid grid, RoadNetwork roads)
        {
            _sector = sector;
            _grid = grid;
            _roads = roads;
            _sectorHalfSize = (float)(sector.SizeMeters * 0.5);

            EnsureVehiclesCreated();
            SetMode(ControlMode.Camera);
        }

        public void SetMode(ControlMode newMode)
        {
            if (newMode == ControlMode.Car && (_roads == null || _roads.SegmentCount == 0))
            {
                ShowToast("Aucune route carrossable dans ce secteur !", new Color(1f, 0.55f, 0.35f, 1f), 2.5f);
                return;
            }

            _mode = newMode;

            if (orbitCamera != null)
                orbitCamera.SetControlsEnabled(_mode == ControlMode.Camera);

            if (_ulmRoot != null) _ulmRoot.SetActive(_mode == ControlMode.Ulm);
            if (_carRoot != null) _carRoot.SetActive(_mode == ControlMode.Car);

            if (_mode == ControlMode.Ulm)
            {
                SpawnUlm();
                ShowToast("Mode ULM : ZQSD/Flèches = Voler · Shift/Ctrl = Gaz · R = Respawn", new Color(0.55f, 0.90f, 1f, 1f), 3.5f);
            }
            else if (_mode == ControlMode.Car)
            {
                SpawnCarOnRoad();
                ShowToast("Mode Voiture : ZQSD/Flèches = Conduire sur route · R = Remise sur route", new Color(0.55f, 0.95f, 0.70f, 1f), 3.5f);
            }
            else
            {
                _audio?.MuteAll();
                HideToast();
            }

            OnModeChanged?.Invoke(_mode);
        }

        public void DisableAll()
        {
            _mode = ControlMode.Camera;
            _audio?.MuteAll();
            if (_ulmRoot != null) _ulmRoot.SetActive(false);
            if (_carRoot != null) _carRoot.SetActive(false);
            HideToast();
        }

        private void Update()
        {
            if (_toastTimer > 0f)
            {
                _toastTimer -= Time.deltaTime;
                if (_toastTimer <= 0f) HideToast();
            }

            if (_grid == null) return;

            if (_mode == ControlMode.Ulm)
                UpdateUlm(Time.deltaTime);
            else if (_mode == ControlMode.Car)
                UpdateCar(Time.deltaTime);
        }

        // ===================== ULM MODE =====================
        private void SpawnUlm()
        {
            float centerH = SampleTerrainY(0f, 0f);
            _ulmPos = new Vector3(0f, centerH + 110f, -220f);
            _ulmYaw = 0f;
            _ulmPitch = 0f;
            _ulmRoll = 0f;
            _ulmSpeed = 52f;
            _ulmCrashed = false;
            _ulmAutoTurning = false;

            ApplyUlmTransform();
            SnapChaseCamera(_ulmRoot.transform, 18f, 6.5f);
        }

        private void UpdateUlm(float dt)
        {
            var kb = Keyboard.current;
            if (kb != null && kb.rKey.wasPressedThisFrame)
            {
                SpawnUlm();
                return;
            }

            if (_ulmCrashed)
            {
                _audio?.MuteAll();
                _ulmRespawnTimer -= dt;
                if (_ulmRespawnTimer <= 0f)
                    SpawnUlm();
                return;
            }

            float pitchInput = 0f;
            float turnInput = 0f;
            float throttleInput = 0f;

            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) pitchInput -= 1f;    // nose down (standard flight)
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) pitchInput += 1f;  // nose up
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) turnInput += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) turnInput -= 1f;
                if (kb.leftShiftKey.isPressed || kb.spaceKey.isPressed) throttleInput += 1f;
                if (kb.leftCtrlKey.isPressed) throttleInput -= 1f;
            }

            // Border auto U-turn when approaching sector boundary
            float borderMargin = _sectorHalfSize - 65f;
            bool nearBorder = Mathf.Abs(_ulmPos.x) > borderMargin || Mathf.Abs(_ulmPos.z) > borderMargin;

            if (nearBorder)
            {
                Vector2 toCenter = new Vector2(-_ulmPos.x, -_ulmPos.z).normalized;
                float targetYaw = Mathf.Atan2(toCenter.x, toCenter.y) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(_ulmYaw, targetYaw);
                float turnStep = Mathf.Clamp(delta, -110f * dt, 110f * dt);
                _ulmYaw += turnStep;
                _ulmRoll = Mathf.Lerp(_ulmRoll, Mathf.Clamp(-delta * 0.7f, -45f, 45f), dt * 6f);
                _ulmPitch = Mathf.Lerp(_ulmPitch, -4f, dt * 4f);

                if (!_ulmAutoTurning)
                {
                    _ulmAutoTurning = true;
                    ShowToast("Limite de zone — demi-tour automatique !", new Color(1f, 0.80f, 0.25f, 1f), 2.0f);
                }
            }
            else
            {
                _ulmAutoTurning = false;
                _ulmYaw += turnInput * 70f * dt;
                _ulmPitch = Mathf.Clamp(_ulmPitch + pitchInput * 55f * dt, -42f, 42f);
                if (Mathf.Approximately(pitchInput, 0f))
                    _ulmPitch = Mathf.Lerp(_ulmPitch, 0f, dt * 1.4f);

                float targetRoll = -turnInput * 38f;
                _ulmRoll = Mathf.Lerp(_ulmRoll, targetRoll, dt * 5f);
            }

            _ulmSpeed = Mathf.Clamp(_ulmSpeed + throttleInput * 30f * dt, 24f, 115f);

            Quaternion rot = Quaternion.Euler(_ulmPitch, _ulmYaw, _ulmRoll);
            _ulmPos += (rot * Vector3.forward) * (_ulmSpeed * dt);

            // Soft ceiling
            float terrainY = SampleTerrainY(_ulmPos.x, _ulmPos.z);
            if (_ulmPos.y > terrainY + 450f)
                _ulmPos.y = terrainY + 450f;

            ApplyUlmTransform();
            _audio?.UpdateUlmAudio(_ulmSpeed, 24f, 115f, active: true);
            if (_ulmPropeller != null)
                _ulmPropeller.Rotate(0f, 0f, 1400f * dt, Space.Self);

            // Crash detection: terrain, buildings, or water
            bool hitGround = _ulmPos.y <= terrainY + 1.6f;
            bool hitObstacle = CheckWorldCollision(_ulmPos, 2.6f, out string obstacleReason);
            if (hitGround || hitObstacle)
            {
                _ulmCrashed = true;
                _ulmRespawnTimer = 1.6f;
                string cause = hitGround ? "impact au sol" : obstacleReason;
                ShowToast($"CRASH ULM ({cause}) ! Redécollage...", new Color(1f, 0.35f, 0.35f, 1f), 1.6f);
            }

            UpdateChaseCamera(_ulmRoot.transform, 18f, 6.5f, dt);
        }

        private void ApplyUlmTransform()
        {
            if (_ulmRoot == null) return;
            _ulmRoot.transform.position = _ulmPos;
            _ulmRoot.transform.rotation = Quaternion.Euler(_ulmPitch, _ulmYaw, _ulmRoll);
        }

        // ===================== CAR MODE =====================
        private void SpawnCarOnRoad()
        {
            if (_roads != null && _roads.TryFindNearest(Vector2.zero, out Vector2 pt, out Vector2 tangent, out _, out _))
            {
                _lastSafeRoadPos = pt;
                _lastSafeRoadYaw = Mathf.Atan2(tangent.x, tangent.y) * Mathf.Rad2Deg;
            }
            else
            {
                _lastSafeRoadPos = Vector2.zero;
                _lastSafeRoadYaw = 0f;
            }

            ResetCarToSafeRoad(null);
            SnapChaseCamera(_carRoot.transform, 13f, 5.2f);
        }

        private void ResetCarToSafeRoad(string reason)
        {
            float y = SampleTerrainY(_lastSafeRoadPos.x, _lastSafeRoadPos.y) + 0.55f;
            _carPos = new Vector3(_lastSafeRoadPos.x, y, _lastSafeRoadPos.y);
            _carYaw = _lastSafeRoadYaw;
            _carSpeed = 0f;
            _carResetCooldown = 0.6f;

            ApplyCarTransform();
            if (!string.IsNullOrEmpty(reason))
                ShowToast($"{reason} — remise sur la route", new Color(1f, 0.72f, 0.28f, 1f), 1.8f);
        }

        private void UpdateCar(float dt)
        {
            var kb = Keyboard.current;
            if (kb != null && kb.rKey.wasPressedThisFrame)
            {
                ResetCarToSafeRoad("Recentrage manuel");
                return;
            }

            if (_carResetCooldown > 0f)
                _carResetCooldown -= dt;

            float accel = 0f;
            float steer = 0f;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) accel += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) accel -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
            }

            if (Mathf.Abs(accel) > 0.01f)
                _carSpeed = Mathf.Clamp(_carSpeed + accel * 26f * dt, -14f, 38f);
            else
                _carSpeed = Mathf.MoveTowards(_carSpeed, 0f, 18f * dt);

            float steerFactor = Mathf.Clamp01(Mathf.Abs(_carSpeed) / 6f) * (_carSpeed >= 0f ? 1f : -1f);
            _carYaw += steer * 115f * steerFactor * dt;

            Vector3 forward = Quaternion.Euler(0f, _carYaw, 0f) * Vector3.forward;
            _carPos += forward * (_carSpeed * dt);
            _carPos.y = SampleTerrainY(_carPos.x, _carPos.z) + 0.55f;

            ApplyCarTransform();
            bool braking = accel < -0.1f && _carSpeed > 0.5f;
            AnimateCarDetails(steer, braking, dt);
            _audio?.UpdateCarAudio(_carSpeed, 38f, braking, active: true);

            // Check road adherence, border, building collision, and water collision
            if (_carResetCooldown <= 0f)
            {
                float maxBound = _sectorHalfSize - 12f;
                if (Mathf.Abs(_carPos.x) > maxBound || Mathf.Abs(_carPos.z) > maxBound)
                {
                    ResetCarToSafeRoad("Limite de zone atteinte");
                }
                else if (CheckWorldCollision(_carPos + Vector3.up * 0.9f, 1.8f, out string obstacle))
                {
                    ResetCarToSafeRoad($"Collision ({obstacle})");
                }
                else if (_roads != null && _roads.TryFindNearest(new Vector2(_carPos.x, _carPos.z), out Vector2 closest, out Vector2 tangent, out float dist, out float allowedHw))
                {
                    if (dist <= allowedHw + 1.5f)
                    {
                        _lastSafeRoadPos = closest;
                        // Preserve travel direction along the road tangent
                        Vector2 currFwd = new Vector2(forward.x, forward.z);
                        if (Vector2.Dot(currFwd, tangent) < 0f) tangent = -tangent;
                        _lastSafeRoadYaw = Mathf.Atan2(tangent.x, tangent.y) * Mathf.Rad2Deg;
                    }
                    else if (dist > allowedHw + 5.5f)
                    {
                        ResetCarToSafeRoad("Sortie de route");
                    }
                }
            }

            UpdateChaseCamera(_carRoot.transform, 13f, 5.2f, dt);
        }

        private void ApplyCarTransform()
        {
            if (_carRoot == null) return;
            _carRoot.transform.position = _carPos;

            // Align car pitch/roll smoothly with local terrain slope
            Vector3 normal = SampleTerrainNormal(_carPos.x, _carPos.z);
            Vector3 fwd = Quaternion.Euler(0f, _carYaw, 0f) * Vector3.forward;
            Vector3 slopeFwd = Vector3.ProjectOnPlane(fwd, normal).normalized;
            if (slopeFwd.sqrMagnitude > 0.01f)
                _carRoot.transform.rotation = Quaternion.LookRotation(slopeFwd, normal);
            else
                _carRoot.transform.rotation = Quaternion.Euler(0f, _carYaw, 0f);
        }

        // ===================== HELPERS =====================
        private bool CheckWorldCollision(Vector3 pos, float radius, out string reason)
        {
            reason = null;
            int hits = Physics.OverlapSphereNonAlloc(pos, radius, _overlapBuffer);
            for (int i = 0; i < hits; i++)
            {
                var col = _overlapBuffer[i];
                if (col == null) continue;
                if (col.name.StartsWith("Water", StringComparison.Ordinal))
                {
                    reason = "eau";
                    return true;
                }
                if (col.transform.parent != null && col.transform.parent.name.StartsWith("Buildings", StringComparison.Ordinal))
                {
                    reason = "bâtiment";
                    return true;
                }
            }
            return false;
        }

        private float SampleTerrainY(float localX, float localZ)
        {
            return _grid.SampleHeight(_sector.CenterLambertX + localX, _sector.CenterLambertY + localZ) - _grid.MinElevation;
        }

        private Vector3 SampleTerrainNormal(float localX, float localZ)
        {
            const float d = 2.0f;
            float hL = SampleTerrainY(localX - d, localZ);
            float hR = SampleTerrainY(localX + d, localZ);
            float hD = SampleTerrainY(localX, localZ - d);
            float hU = SampleTerrainY(localX, localZ + d);
            return new Vector3(hL - hR, 2f * d, hD - hU).normalized;
        }

        private void UpdateChaseCamera(Transform target, float backDist, float upHeight, float dt)
        {
            var cam = Camera.main;
            if (cam == null || target == null) return;

            Vector3 flatFwd = target.forward;
            flatFwd.y = Mathf.Clamp(flatFwd.y, -0.25f, 0.25f);
            if (flatFwd.sqrMagnitude < 0.01f) flatFwd = Vector3.forward;
            flatFwd.Normalize();

            Vector3 desiredPos = target.position - flatFwd * backDist + Vector3.up * upHeight;
            float minCamY = SampleTerrainY(desiredPos.x, desiredPos.z) + 2.5f;
            if (desiredPos.y < minCamY) desiredPos.y = minCamY;

            float t = 1f - Mathf.Exp(-8f * dt);
            cam.transform.position = Vector3.Lerp(cam.transform.position, desiredPos, t);

            Vector3 lookTarget = target.position + flatFwd * 12f + Vector3.up * 1.2f;
            Quaternion desiredRot = Quaternion.LookRotation((lookTarget - cam.transform.position).normalized, Vector3.up);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, desiredRot, t);
        }

        private void SnapChaseCamera(Transform target, float backDist, float upHeight)
        {
            var cam = Camera.main;
            if (cam == null || target == null) return;
            cam.transform.position = target.position - target.forward * backDist + Vector3.up * upHeight;
            cam.transform.LookAt(target.position + target.forward * 10f, Vector3.up);
        }

        private void ShowToast(string msg, Color color, float duration)
        {
            if (statusToastText == null) return;
            statusToastText.gameObject.SetActive(true);
            statusToastText.text = msg;
            statusToastText.color = color;
            _toastTimer = duration;
        }

        private void HideToast()
        {
            if (statusToastText != null)
                statusToastText.gameObject.SetActive(false);
        }

        private void AnimateCarDetails(float steerInput, bool braking, float dt)
        {
            _carSteerVisual = Mathf.Lerp(_carSteerVisual, steerInput * 28f, dt * 10f);
            if (_carFrontSteerPivots != null)
            {
                for (int i = 0; i < _carFrontSteerPivots.Length; i++)
                {
                    if (_carFrontSteerPivots[i] != null)
                        _carFrontSteerPivots[i].localRotation = Quaternion.Euler(0f, _carSteerVisual, 0f);
                }
            }

            if (_carWheels != null)
            {
                float spinDeg = (_carSpeed / 0.36f) * Mathf.Rad2Deg * dt;
                for (int i = 0; i < _carWheels.Length; i++)
                {
                    if (_carWheels[i] != null)
                        _carWheels[i].Rotate(spinDeg, 0f, 0f, Space.Self);
                }
            }

            if (_carTailLightMat != null)
            {
                Color tailGlow = braking ? new Color(2.4f, 0.12f, 0.08f, 1f) : new Color(1.05f, 0.08f, 0.06f, 1f);
                _carTailLightMat.SetColor("_BaseColor", tailGlow);
            }
        }

        // ===================== PROCEDURAL VEHICLE MESHES =====================
        private void EnsureVehiclesCreated()
        {
            var urpShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpShader == null)
                urpShader = GraphicsSettings.currentRenderPipeline.defaultMaterial.shader;

            if (_ulmRoot == null)
                BuildRealisticUlm(urpShader);

            if (_carRoot == null)
                BuildRealisticCar(urpShader);
        }

        private void BuildRealisticUlm(Shader urpShader)
        {
            _ulmRoot = new GameObject("Player_ULM");

            Material wingOrange = MakeMat(urpShader, new Color(0.92f, 0.30f, 0.12f), metallic: 0.35f, smoothness: 0.86f);
            Material whiteGloss = MakeMat(urpShader, new Color(0.95f, 0.96f, 0.98f), metallic: 0.25f, smoothness: 0.92f);
            Material chromeMat = MakeMat(urpShader, new Color(0.82f, 0.86f, 0.90f), metallic: 0.92f, smoothness: 0.94f);
            Material glassMat = MakeMat(urpShader, new Color(0.08f, 0.15f, 0.24f), metallic: 0.30f, smoothness: 0.97f);
            Material darkCarbon = MakeMat(urpShader, new Color(0.12f, 0.13f, 0.16f), metallic: 0.45f, smoothness: 0.65f);
            Material navRed = MakeEmissiveMat(urpShader, new Color(0.9f, 0.1f, 0.1f), new Color(2.5f, 0.15f, 0.1f));
            Material navGreen = MakeEmissiveMat(urpShader, new Color(0.1f, 0.9f, 0.2f), new Color(0.15f, 2.5f, 0.3f));

            // Swept delta wing (left & right swept panels + glossy white leading-edge center)
            CreatePart(_ulmRoot.transform, PrimitiveType.Cube, "WingLeft", new Vector3(-2.15f, 1.55f, -0.35f), new Vector3(4.6f, 0.10f, 1.85f), wingOrange, Quaternion.Euler(2f, 18f, -3f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cube, "WingRight", new Vector3(2.15f, 1.55f, -0.35f), new Vector3(4.6f, 0.10f, 1.85f), wingOrange, Quaternion.Euler(2f, -18f, 3f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cube, "WingCenter", new Vector3(0f, 1.62f, 0.25f), new Vector3(1.5f, 0.12f, 1.95f), whiteGloss);
            // Winglets + aviation nav lights (port=red, starboard=green)
            CreatePart(_ulmRoot.transform, PrimitiveType.Cube, "WingletL", new Vector3(-4.25f, 1.68f, -1.05f), new Vector3(0.08f, 0.55f, 0.95f), whiteGloss, Quaternion.Euler(0f, 18f, 15f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cube, "WingletR", new Vector3(4.25f, 1.68f, -1.05f), new Vector3(0.08f, 0.55f, 0.95f), whiteGloss, Quaternion.Euler(0f, -18f, -15f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Sphere, "NavLightL", new Vector3(-4.32f, 1.72f, -0.95f), Vector3.one * 0.18f, navRed);
            CreatePart(_ulmRoot.transform, PrimitiveType.Sphere, "NavLightR", new Vector3(4.32f, 1.72f, -0.95f), Vector3.one * 0.18f, navGreen);

            // Aerodynamic trike fuselage pod + reflective glass canopy
            CreatePart(_ulmRoot.transform, PrimitiveType.Capsule, "FuselagePod", new Vector3(0f, 0.42f, 0.20f), new Vector3(0.88f, 1.55f, 0.82f), whiteGloss, Quaternion.Euler(86f, 0f, 0f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Sphere, "CanopyGlass", new Vector3(0f, 0.72f, 0.55f), new Vector3(0.76f, 0.56f, 1.25f), glassMat, Quaternion.Euler(-12f, 0f, 0f));

            // Chrome A-frame struts & kingpost mast
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "Mast", new Vector3(0f, 1.15f, -0.05f), new Vector3(0.09f, 0.58f, 0.09f), chromeMat, Quaternion.Euler(-10f, 0f, 0f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "StrutL", new Vector3(-0.62f, 1.10f, 0.25f), new Vector3(0.05f, 0.72f, 0.05f), chromeMat, Quaternion.Euler(8f, 0f, -26f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "StrutR", new Vector3(0.62f, 1.10f, 0.25f), new Vector3(0.05f, 0.72f, 0.05f), chromeMat, Quaternion.Euler(8f, 0f, 26f));

            // Engine cowling & 3-blade spinning pusher propeller
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "EngineCowling", new Vector3(0f, 0.50f, -1.15f), new Vector3(0.55f, 0.35f, 0.55f), darkCarbon, Quaternion.Euler(90f, 0f, 0f));
            var propHub = new GameObject("PropellerHub");
            propHub.transform.SetParent(_ulmRoot.transform, false);
            propHub.transform.localPosition = new Vector3(0f, 0.50f, -1.52f);
            CreatePart(propHub.transform, PrimitiveType.Sphere, "Spinner", Vector3.zero, new Vector3(0.22f, 0.22f, 0.28f), chromeMat);
            for (int b = 0; b < 3; b++)
            {
                var blade = CreatePart(propHub.transform, PrimitiveType.Cube, $"Blade_{b}", Vector3.zero, new Vector3(0.11f, 0.82f, 0.03f), darkCarbon);
                float angle = b * 120f;
                blade.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
                blade.transform.localPosition = Quaternion.Euler(0f, 0f, angle) * new Vector3(0f, 0.42f, 0f);
            }
            _ulmPropeller = propHub.transform;

            // Landing gear (chrome legs + 3 wheels)
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "GearLegL", new Vector3(-0.65f, 0.05f, -0.45f), new Vector3(0.06f, 0.42f, 0.06f), chromeMat, Quaternion.Euler(0f, 0f, 28f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "GearLegR", new Vector3(0.65f, 0.05f, -0.45f), new Vector3(0.06f, 0.42f, 0.06f), chromeMat, Quaternion.Euler(0f, 0f, -28f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "WheelNose", new Vector3(0f, -0.18f, 1.15f), new Vector3(0.36f, 0.10f, 0.36f), darkCarbon, Quaternion.Euler(0f, 0f, 90f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "WheelRearL", new Vector3(-0.88f, -0.22f, -0.45f), new Vector3(0.42f, 0.12f, 0.42f), darkCarbon, Quaternion.Euler(0f, 0f, 90f));
            CreatePart(_ulmRoot.transform, PrimitiveType.Cylinder, "WheelRearR", new Vector3(0.88f, -0.22f, -0.45f), new Vector3(0.42f, 0.12f, 0.42f), darkCarbon, Quaternion.Euler(0f, 0f, 90f));

            _ulmRoot.SetActive(false);
        }

        private void BuildRealisticCar(Shader urpShader)
        {
            _carRoot = new GameObject("Player_Car");

            // High-gloss metallic sports coupe paint + tinted reflective glass + chrome rims
            Material carPaint = MakeMat(urpShader, new Color(0.78f, 0.10f, 0.14f), metallic: 0.68f, smoothness: 0.93f);
            Material darkTrim = MakeMat(urpShader, new Color(0.11f, 0.12f, 0.14f), metallic: 0.25f, smoothness: 0.45f);
            Material glassMat = MakeMat(urpShader, new Color(0.07f, 0.12f, 0.19f), metallic: 0.35f, smoothness: 0.98f);
            Material chromeMat = MakeMat(urpShader, new Color(0.86f, 0.89f, 0.93f), metallic: 0.95f, smoothness: 0.94f);
            Material tireMat = MakeMat(urpShader, new Color(0.09f, 0.09f, 0.10f), metallic: 0.05f, smoothness: 0.18f);
            Material headLightMat = MakeEmissiveMat(urpShader, new Color(1.0f, 0.97f, 0.88f), new Color(2.6f, 2.4f, 2.0f));
            _carTailLightMat = MakeEmissiveMat(urpShader, new Color(0.85f, 0.06f, 0.06f), new Color(1.1f, 0.06f, 0.05f));

            // Sculpted lower chassis + front/rear bumpers + side skirts
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "BodyMain", new Vector3(0f, 0.56f, 0f), new Vector3(2.04f, 0.54f, 4.50f), carPaint);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "FrontSplitter", new Vector3(0f, 0.28f, 2.18f), new Vector3(2.02f, 0.12f, 0.34f), darkTrim);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "RearDiffuser", new Vector3(0f, 0.28f, -2.18f), new Vector3(1.96f, 0.16f, 0.30f), darkTrim);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "FrontGrille", new Vector3(0f, 0.48f, 2.24f), new Vector3(1.22f, 0.24f, 0.06f), darkTrim);

            // Sloped hood + roof + angled reflective windshield & fastback rear glass
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "Hood", new Vector3(0f, 0.81f, 1.32f), new Vector3(1.88f, 0.14f, 1.48f), carPaint, Quaternion.Euler(5f, 0f, 0f));
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "Roof", new Vector3(0f, 1.32f, -0.28f), new Vector3(1.56f, 0.10f, 1.50f), carPaint);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "CabinGlass", new Vector3(0f, 1.06f, -0.25f), new Vector3(1.62f, 0.46f, 1.92f), glassMat);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "Windshield", new Vector3(0f, 1.06f, 0.68f), new Vector3(1.58f, 0.10f, 0.92f), glassMat, Quaternion.Euler(-34f, 0f, 0f));
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "RearGlass", new Vector3(0f, 1.08f, -1.20f), new Vector3(1.56f, 0.10f, 0.88f), glassMat, Quaternion.Euler(28f, 0f, 0f));

            // Side mirrors, rear spoiler & chrome twin exhausts
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "MirrorL", new Vector3(-1.12f, 0.92f, 0.52f), new Vector3(0.24f, 0.13f, 0.16f), carPaint);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "MirrorR", new Vector3(1.12f, 0.92f, 0.52f), new Vector3(0.24f, 0.13f, 0.16f), carPaint);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "Spoiler", new Vector3(0f, 0.96f, -2.08f), new Vector3(1.88f, 0.07f, 0.28f), darkTrim, Quaternion.Euler(-8f, 0f, 0f));
            CreatePart(_carRoot.transform, PrimitiveType.Cylinder, "ExhaustL", new Vector3(-0.56f, 0.28f, -2.30f), new Vector3(0.13f, 0.14f, 0.13f), chromeMat, Quaternion.Euler(90f, 0f, 0f));
            CreatePart(_carRoot.transform, PrimitiveType.Cylinder, "ExhaustR", new Vector3(0.56f, 0.28f, -2.30f), new Vector3(0.13f, 0.14f, 0.13f), chromeMat, Quaternion.Euler(90f, 0f, 0f));

            // Emissive LED headlights & taillights
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "HeadlightL", new Vector3(-0.74f, 0.60f, 2.24f), new Vector3(0.38f, 0.14f, 0.08f), headLightMat);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "HeadlightR", new Vector3(0.74f, 0.60f, 2.24f), new Vector3(0.38f, 0.14f, 0.08f), headLightMat);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "TaillightL", new Vector3(-0.70f, 0.64f, -2.24f), new Vector3(0.46f, 0.13f, 0.08f), _carTailLightMat);
            CreatePart(_carRoot.transform, PrimitiveType.Cube, "TaillightR", new Vector3(0.70f, 0.64f, -2.24f), new Vector3(0.46f, 0.13f, 0.08f), _carTailLightMat);

            // 4 articulated wheels with rubber tires + chrome alloy rims
            _carWheels = new Transform[4];
            _carFrontSteerPivots = new Transform[2];

            Vector3[] wheelOffsets =
            {
                new Vector3(-1.02f, 0.36f, 1.38f),
                new Vector3(1.02f, 0.36f, 1.38f),
                new Vector3(-1.02f, 0.36f, -1.36f),
                new Vector3(1.02f, 0.36f, -1.36f)
            };

            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject($"WheelPivot_{i}");
                pivot.transform.SetParent(_carRoot.transform, false);
                pivot.transform.localPosition = wheelOffsets[i];
                if (i < 2) _carFrontSteerPivots[i] = pivot.transform;

                var spinNode = new GameObject($"WheelSpin_{i}");
                spinNode.transform.SetParent(pivot.transform, false);
                _carWheels[i] = spinNode.transform;

                CreatePart(spinNode.transform, PrimitiveType.Cylinder, "Tire", Vector3.zero, new Vector3(0.72f, 0.15f, 0.72f), tireMat, Quaternion.Euler(0f, 0f, 90f));
                CreatePart(spinNode.transform, PrimitiveType.Cylinder, "Rim", Vector3.zero, new Vector3(0.46f, 0.165f, 0.46f), chromeMat, Quaternion.Euler(0f, 0f, 90f));
                CreatePart(spinNode.transform, PrimitiveType.Cube, "Spoke", Vector3.zero, new Vector3(0.35f, 0.40f, 0.07f), darkTrim);
            }

            _carRoot.SetActive(false);
        }

        private static Material MakeMat(Shader shader, Color color, float metallic, float smoothness)
        {
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            return mat;
        }

        private static Material MakeEmissiveMat(Shader fallbackShader, Color baseColor, Color emissionColor)
        {
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(unlit != null ? unlit : fallbackShader);
            mat.SetColor("_BaseColor", emissionColor);
            return mat;
        }

        private static GameObject CreatePart(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat, Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>()); // No self-collision
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            if (rot.HasValue) go.transform.localRotation = rot.Value;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }
    }
}

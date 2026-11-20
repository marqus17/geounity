using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Smooth architectural orbit + pan + zoom camera for exploring a 2 km x 2 km 3D sector.
    /// Left-drag / WASD: Pan | Right-drag / Middle-drag: Orbit | Scroll: Zoom | R: Reset view.
    /// </summary>
    public class OrbitFlyCameraController : MonoBehaviour
    {
        [SerializeField] private float minDistance = 40f;
        [SerializeField] private float maxDistance = 2600f;
        [SerializeField] private float minPitch = 12f;
        [SerializeField] private float maxPitch = 85f;
        [SerializeField] private float orbitSpeed = 0.25f;
        [SerializeField] private float zoomFactor = 0.14f;
        [SerializeField] private float keyboardPanSpeed = 350f;
        [SerializeField] private float smoothing = 12f;

        private Vector3 _targetPos = Vector3.zero;
        private Vector3 _desiredTargetPos = Vector3.zero;
        private float _yaw = 35f;
        private float _desiredYaw = 35f;
        private float _pitch = 42f;
        private float _desiredPitch = 42f;
        private float _distance = 950f;
        private float _desiredDistance = 950f;

        private float _defaultHeight = 20f;
        private float _sectorHalfSize = 1000f;
        private bool _controlsEnabled;

        public void SetControlsEnabled(bool enabled)
        {
            _controlsEnabled = enabled;
        }

        public void FocusOnSector(float centerHeight, float sectorSizeMeters = 2000f)
        {
            _defaultHeight = centerHeight;
            _sectorHalfSize = sectorSizeMeters * 0.5f;
            ResetView(instant: true);
        }

        public void ResetView(bool instant = false)
        {
            _desiredTargetPos = new Vector3(0f, _defaultHeight, 0f);
            _desiredYaw = 35f;
            _desiredPitch = 40f;
            _desiredDistance = _sectorHalfSize * 0.95f;

            if (instant)
            {
                _targetPos = _desiredTargetPos;
                _yaw = _desiredYaw;
                _pitch = _desiredPitch;
                _distance = _desiredDistance;
                ApplyTransform();
            }
        }

        private void Update()
        {
            if (!_controlsEnabled) return;

            bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            if (mouse != null && !pointerOverUi)
            {
                Vector2 delta = mouse.delta.ReadValue();

                // Orbit: Right-drag or Middle-drag
                if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
                {
                    _desiredYaw += delta.x * orbitSpeed;
                    _desiredPitch = Mathf.Clamp(_desiredPitch - delta.y * orbitSpeed, minPitch, maxPitch);
                }
                // Pan: Left-drag
                else if (mouse.leftButton.isPressed)
                {
                    float panScale = _desiredDistance * 0.0014f;
                    Quaternion yawRot = Quaternion.Euler(0f, _desiredYaw, 0f);
                    Vector3 right = yawRot * Vector3.right;
                    Vector3 forward = yawRot * Vector3.forward;
                    _desiredTargetPos -= right * (delta.x * panScale);
                    _desiredTargetPos -= forward * (delta.y * panScale);
                }

                // Zoom: Scroll wheel
                float scrollY = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scrollY) > 0.01f)
                {
                    float dir = Mathf.Sign(scrollY);
                    _desiredDistance = Mathf.Clamp(_desiredDistance * (1f - dir * zoomFactor), minDistance, maxDistance);
                }
            }

            if (kb != null)
            {
                Vector2 move = Vector2.zero;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1f;

                if (move.sqrMagnitude > 0.01f)
                {
                    float speed = keyboardPanSpeed * (_desiredDistance / 800f) * Time.deltaTime;
                    Quaternion yawRot = Quaternion.Euler(0f, _desiredYaw, 0f);
                    _desiredTargetPos += (yawRot * Vector3.right * move.x + yawRot * Vector3.forward * move.y) * speed;
                }

                if (kb.rKey.wasPressedThisFrame)
                    ResetView(instant: false);
            }

            // Clamp pan within sector bounds
            float bound = _sectorHalfSize * 1.05f;
            _desiredTargetPos.x = Mathf.Clamp(_desiredTargetPos.x, -bound, bound);
            _desiredTargetPos.z = Mathf.Clamp(_desiredTargetPos.z, -bound, bound);

            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            _targetPos = Vector3.Lerp(_targetPos, _desiredTargetPos, t);
            _yaw = Mathf.Lerp(_yaw, _desiredYaw, t);
            _pitch = Mathf.Lerp(_pitch, _desiredPitch, t);
            _distance = Mathf.Lerp(_distance, _desiredDistance, t);

            ApplyTransform();
        }

        private void ApplyTransform()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 offset = rot * new Vector3(0f, 0f, -_distance);
            transform.position = _targetPos + offset;
            transform.LookAt(_targetPos, Vector3.up);
        }
    }
}

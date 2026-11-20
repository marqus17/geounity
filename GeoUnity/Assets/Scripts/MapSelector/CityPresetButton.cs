using UnityEngine;

namespace GeoUnity.MapSelector
{
    [RequireComponent(typeof(UnityEngine.UI.Button))]
    public class CityPresetButton : MonoBehaviour
    {
        [SerializeField] private MapSelectorController controller;
        [SerializeField] private double latitude;
        [SerializeField] private double longitude;
        [SerializeField] private double zoom = 13.0;

        public void Configure(MapSelectorController target, double lat, double lon, double targetZoom = 13.0)
        {
            controller = target;
            latitude = lat;
            longitude = lon;
            zoom = targetZoom;
        }

        private void Awake()
        {
            var btn = GetComponent<UnityEngine.UI.Button>();
            btn.onClick.AddListener(() =>
            {
                if (controller != null)
                    controller.JumpTo(latitude, longitude, zoom);
            });
        }
    }
}

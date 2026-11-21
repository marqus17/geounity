using UnityEngine;

namespace GeoUnity.World3D
{
    /// <summary>
    /// Toggles visibility of the RPG crop legend body when the user clicks its header button.
    /// </summary>
    public class CropLegendToggle : MonoBehaviour
    {
        [SerializeField] private UnityEngine.UI.Button headerButton;
        [SerializeField] private UnityEngine.UI.Text headerLabel;
        [SerializeField] private GameObject bodyContainer;

        private bool _expanded = true;

        public void Configure(UnityEngine.UI.Button button, UnityEngine.UI.Text label, GameObject body)
        {
            headerButton = button;
            headerLabel = label;
            bodyContainer = body;
        }

        private void Awake()
        {
            if (headerButton != null)
                headerButton.onClick.AddListener(Toggle);
            ApplyState();
        }

        public void Toggle()
        {
            _expanded = !_expanded;
            ApplyState();
        }

        private void ApplyState()
        {
            if (bodyContainer != null)
                bodyContainer.SetActive(_expanded);
            if (headerLabel != null)
                headerLabel.text = _expanded ? "Légende cultures RPG   [−]" : "Légende cultures RPG   [+]";
        }
    }
}

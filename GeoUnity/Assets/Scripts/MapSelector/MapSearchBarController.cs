using System.Collections.Generic;
using System.Threading.Tasks;
using GeoUnity.Ign;
using UnityEngine;

namespace GeoUnity.MapSelector
{
    /// <summary>
    /// Search bar controller for the 2D IGN map selector.
    /// Queries the official IGN Geoplateforme Geocoding API (BAN) and displays clickable results.
    /// </summary>
    public class MapSearchBarController : MonoBehaviour
    {
        [SerializeField] private MapSelectorController mapSelector;
        [SerializeField] private UnityEngine.UI.InputField searchInput;
        [SerializeField] private UnityEngine.UI.Button searchButton;
        [SerializeField] private GameObject resultsPanel;
        [SerializeField] private UnityEngine.UI.Button[] resultButtons;
        [SerializeField] private UnityEngine.UI.Text[] resultLabels;
        [SerializeField] private UnityEngine.UI.Text feedbackText;

        private readonly List<GeocodeResult> _currentResults = new List<GeocodeResult>();
        private bool _isSearching;

        public void Configure(
            MapSelectorController selector,
            UnityEngine.UI.InputField input,
            UnityEngine.UI.Button submitBtn,
            GameObject dropdownPanel,
            UnityEngine.UI.Button[] buttons,
            UnityEngine.UI.Text[] labels,
            UnityEngine.UI.Text feedback)
        {
            mapSelector = selector;
            searchInput = input;
            searchButton = submitBtn;
            resultsPanel = dropdownPanel;
            resultButtons = buttons;
            resultLabels = labels;
            feedbackText = feedback;
        }

        private void Start()
        {
            if (searchButton != null)
                searchButton.onClick.AddListener(TriggerSearch);

            if (searchInput != null)
                searchInput.onSubmit.AddListener(_ => TriggerSearch());

            if (resultButtons != null)
            {
                for (int i = 0; i < resultButtons.Length; i++)
                {
                    int captured = i;
                    if (resultButtons[i] != null)
                        resultButtons[i].onClick.AddListener(() => SelectResult(captured));
                }
            }

            if (resultsPanel != null)
                resultsPanel.SetActive(false);
        }

        public void TriggerSearch()
        {
            if (_isSearching || searchInput == null) return;
            string query = searchInput.text;
            if (string.IsNullOrWhiteSpace(query)) return;
            _ = ExecuteSearchAsync(query);
        }

        public async Task ExecuteSearchAsync(string query)
        {
            _isSearching = true;
            SetFeedback("Recherche en cours...");

            try
            {
                var results = await IgnGeocoder.SearchAsync(query, resultButtons != null ? resultButtons.Length : 5);
                _currentResults.Clear();
                _currentResults.AddRange(results);

                if (_currentResults.Count == 0)
                {
                    if (resultsPanel != null) resultsPanel.SetActive(false);
                    SetFeedback("Aucun résultat en France métropolitaine.");
                    return;
                }

                if (resultsPanel != null) resultsPanel.SetActive(true);
                for (int i = 0; i < resultButtons.Length; i++)
                {
                    bool active = i < _currentResults.Count;
                    resultButtons[i].gameObject.SetActive(active);
                    if (active && i < resultLabels.Length && resultLabels[i] != null)
                    {
                        var r = _currentResults[i];
                        resultLabels[i].text = string.IsNullOrEmpty(r.Context)
                            ? r.Label
                            : $"{r.Label}  ({r.Context})";
                    }
                }
                SetFeedback($"{_currentResults.Count} résultat(s) trouvé(s)");
            }
            finally
            {
                _isSearching = false;
            }
        }

        private void SelectResult(int index)
        {
            if (index < 0 || index >= _currentResults.Count) return;
            var r = _currentResults[index];
            if (mapSelector != null)
                mapSelector.JumpTo(r.Latitude, r.Longitude, 13.5);

            if (resultsPanel != null)
                resultsPanel.SetActive(false);

            if (searchInput != null)
                searchInput.text = r.Label;

            SetFeedback($"Centré sur : {r.Label}");
        }

        private void SetFeedback(string msg)
        {
            if (feedbackText != null)
                feedbackText.text = msg;
        }
    }
}

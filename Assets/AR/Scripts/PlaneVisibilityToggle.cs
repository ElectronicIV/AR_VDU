using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace ARVDU
{
    /// <summary>
    /// Shows or hides the detected-plane overlay without disturbing detection.
    /// <para>
    /// Works by toggling each plane GameObject's active state. The two obvious alternatives are
    /// both wrong: disabling <see cref="ARPlaneManager"/> stops detection outright (and can make
    /// the provider drop the <c>ARPlane</c> objects that anchoring needs), while toggling the
    /// mesh and line renderers loses a race with <c>ARPlaneMeshVisualizer</c>, which owns those
    /// flags and rewrites them whenever a plane's boundary or tracking state changes.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(ARPlaneManager))]
    public class PlaneVisibilityToggle : MonoBehaviour
    {
        [SerializeField]
        bool m_PlanesVisible = true;

        [SerializeField]
        [Tooltip("Optional. Kept in sync with the current state so the button says what it does.")]
        TMP_Text m_ButtonLabel;

        [SerializeField]
        string m_HideText = "Hide Planes";

        [SerializeField]
        string m_ShowText = "Show Planes";

        ARPlaneManager m_PlaneManager;

        public bool planesVisible => m_PlanesVisible;

        void Awake() => m_PlaneManager = GetComponent<ARPlaneManager>();

        void OnEnable()
        {
            m_PlaneManager.trackablesChanged.AddListener(OnTrackablesChanged);
            Apply();
        }

        void OnDisable() => m_PlaneManager.trackablesChanged.RemoveListener(OnTrackablesChanged);

        /// <summary>
        /// Flips the overlay. Public, void and argument-free because the HUD button binds to it
        /// as a serialized persistent listener.
        /// </summary>
        public void Toggle() => SetPlanesVisible(!m_PlanesVisible);

        public void SetPlanesVisible(bool visible)
        {
            m_PlanesVisible = visible;
            Apply();
        }

        void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
        {
            // Planes found after the toggle are instantiated active, so without this the overlay
            // leaks back one plane at a time.
            foreach (var plane in changes.added)
            {
                if (plane != null)
                    plane.gameObject.SetActive(m_PlanesVisible);
            }
        }

        void Apply()
        {
            foreach (var plane in m_PlaneManager.trackables)
            {
                if (plane != null)
                    plane.gameObject.SetActive(m_PlanesVisible);
            }

            if (m_ButtonLabel != null)
                m_ButtonLabel.text = m_PlanesVisible ? m_HideText : m_ShowText;
        }
    }
}

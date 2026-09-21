using TMPro;
using UnityEngine;

namespace ARVDU
{
    /// <summary>
    /// Drives the bottom-centre label from both model sources.
    /// <para>
    /// The two have different shapes: image tracking reports continuous state (a marker is
    /// visible or it isn't, and it sends an empty string when tracking stops), while tap
    /// placement is a one-off event. Letting both write the same field means the tracker's
    /// empty string wipes a fresh placement a frame later, and a placement permanently masks a
    /// marker that's still on screen. So they're kept as two channels: placement is a short
    /// toast that outranks tracking while it lasts, tracking is the persistent baseline
    /// underneath.
    /// </para>
    /// </summary>
    public class ObjectNameHud : MonoBehaviour
    {
        [SerializeField]
        TrackedImageModelSpawner m_Spawner;

        [SerializeField]
        TapToPlaceSpawner m_PlacementSpawner;

        [SerializeField]
        TMP_Text m_Label;

        [SerializeField]
        [Tooltip("Shown/hidden as a whole -- normally the label's background panel, so the empty " +
                 "backing doesn't sit on screen while nothing is tracked. Defaults to the label's " +
                 "own object if left unset.")]
        GameObject m_Panel;

        [SerializeField]
        [Tooltip("How long a freshly placed model's name stays on the label before it falls back " +
                 "to whatever image tracking currently says.")]
        float m_PlacementLabelSeconds = 2f;

        string m_TrackedLabel = string.Empty;
        string m_ToastLabel = string.Empty;
        float m_ToastExpiry;

        void OnEnable()
        {
            if (m_Spawner != null)
                m_Spawner.activeLabelChanged += OnTrackedLabelChanged;

            if (m_PlacementSpawner != null)
                m_PlacementSpawner.modelPlaced += OnModelPlaced;

            m_TrackedLabel = string.Empty;
            m_ToastLabel = string.Empty;
            Apply();
        }

        void OnDisable()
        {
            if (m_Spawner != null)
                m_Spawner.activeLabelChanged -= OnTrackedLabelChanged;

            if (m_PlacementSpawner != null)
                m_PlacementSpawner.modelPlaced -= OnModelPlaced;
        }

        void Update()
        {
            // Costs nothing at rest: with no toast pending this is one string-length check.
            if (m_ToastLabel.Length == 0 || Time.time < m_ToastExpiry)
                return;

            m_ToastLabel = string.Empty;
            Apply();
        }

        void OnTrackedLabelChanged(string label)
        {
            m_TrackedLabel = label ?? string.Empty;
            Apply();
        }

        void OnModelPlaced(string label)
        {
            m_ToastLabel = label ?? string.Empty;
            m_ToastExpiry = Time.time + m_PlacementLabelSeconds;
            Apply();
        }

        void Apply()
        {
            if (m_Label == null)
                return;

            var text = m_ToastLabel.Length > 0 ? m_ToastLabel : m_TrackedLabel;
            m_Label.text = text;

            var target = m_Panel != null ? m_Panel : m_Label.gameObject;
            var visible = text.Length > 0;

            if (target.activeSelf != visible)
                target.SetActive(visible);
        }
    }
}

using TMPro;
using UnityEngine;

namespace ARVDU
{
    /// <summary>
    /// Mirrors <see cref="TrackedImageModelSpawner.activeLabelChanged"/> onto a UI label --
    /// the name of whichever model is currently tracked, hidden when nothing is.
    /// </summary>
    public class ObjectNameHud : MonoBehaviour
    {
        [SerializeField]
        TrackedImageModelSpawner m_Spawner;

        [SerializeField]
        TMP_Text m_Label;

        [SerializeField]
        [Tooltip("Shown/hidden as a whole -- normally the label's background panel, so the empty " +
                 "backing doesn't sit on screen while nothing is tracked. Defaults to the label's " +
                 "own object if left unset.")]
        GameObject m_Panel;

        void OnEnable()
        {
            if (m_Spawner != null)
                m_Spawner.activeLabelChanged += SetLabel;

            SetLabel(string.Empty);
        }

        void OnDisable()
        {
            if (m_Spawner != null)
                m_Spawner.activeLabelChanged -= SetLabel;
        }

        void SetLabel(string label)
        {
            if (m_Label == null)
                return;

            m_Label.text = label;
            var target = m_Panel != null ? m_Panel : m_Label.gameObject;
            target.SetActive(!string.IsNullOrEmpty(label));
        }
    }
}

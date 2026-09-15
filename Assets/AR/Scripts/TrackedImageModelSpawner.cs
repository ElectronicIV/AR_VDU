using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARVDU
{
    /// <summary>
    /// Binds one reference image in the <see cref="XRReferenceImageLibrary"/> to the prefab
    /// that should appear on top of it.
    /// </summary>
    [Serializable]
    public class TrackedImageBinding
    {
        [Tooltip("Must match the image name in the XRReferenceImageLibrary, exactly.")]
        public string referenceImageName;

        [Tooltip("Shown in the on-screen HUD while this image is tracked. Falls back to " +
                 "referenceImageName when left blank.")]
        public string displayName;

        [Tooltip("Spawned once, as a child of the tracked image, when that image is first detected.")]
        public GameObject prefab;

        [Tooltip("How much of the printed picture's width the model should span. " +
                 "1 = exactly as wide as the picture, 2 = twice as wide.")]
        [Range(0.1f, 5f)]
        public float widthRelativeToImage = 0.9f;

        [Tooltip("Offset from the centre of the picture, in multiples of the picture's width.")]
        public Vector3 offsetRelativeToImage = Vector3.zero;
    }

    /// <summary>
    /// Spawns a different 3D model over each tracked image.
    /// <para>
    /// <see cref="ARTrackedImageManager"/> only exposes a single prefab for every image it tracks,
    /// so instead of using that field we listen to its change events and instantiate the prefab
    /// bound to each image ourselves.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(ARTrackedImageManager))]
    public class TrackedImageModelSpawner : MonoBehaviour
    {
        [SerializeField]
        List<TrackedImageBinding> m_Bindings = new();

        [SerializeField]
        [Tooltip("Hide the model while the image is out of view instead of leaving it floating " +
                 "at its last known pose.")]
        bool m_HideWhenTrackingLost = true;

        ARTrackedImageManager m_Manager;

        readonly Dictionary<TrackableId, GameObject> m_Spawned = new();
        readonly Dictionary<string, TrackedImageBinding> m_BindingsByName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Which bound images are currently visible, keyed by trackable so a removal can
        /// look its label back up without touching the tracked image itself.</summary>
        readonly Dictionary<TrackableId, string> m_VisibleLabels = new();
        string m_CurrentLabel = string.Empty;

        /// <summary>
        /// Fired with the display name of the model that should be shown in the HUD -- the most
        /// recently tracked one, falling back to any other still-visible model, or an empty
        /// string once nothing is tracked.
        /// </summary>
        public event Action<string> activeLabelChanged;

        /// <summary>The image-to-prefab bindings, editable at runtime before the manager is enabled.</summary>
        public List<TrackedImageBinding> bindings => m_Bindings;

        void Awake()
        {
            m_Manager = GetComponent<ARTrackedImageManager>();
            RebuildLookup();
        }

        void OnEnable() => m_Manager.trackablesChanged.AddListener(OnTrackablesChanged);

        void OnDisable() => m_Manager.trackablesChanged.RemoveListener(OnTrackablesChanged);

        /// <summary>
        /// Refreshes the name lookup. Call this after changing <see cref="bindings"/> at runtime.
        /// </summary>
        public void RebuildLookup()
        {
            m_BindingsByName.Clear();
            foreach (var binding in m_Bindings)
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.referenceImageName))
                    continue;

                if (!m_BindingsByName.TryAdd(binding.referenceImageName.Trim(), binding))
                {
                    Debug.LogWarning(
                        $"[{nameof(TrackedImageModelSpawner)}] Duplicate binding for reference image " +
                        $"'{binding.referenceImageName}'. Only the first one is used.", this);
                }
            }
        }

        void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
        {
            foreach (var trackedImage in changes.added)
                Spawn(trackedImage);

            foreach (var trackedImage in changes.updated)
                UpdateSpawned(trackedImage);

            foreach (var removed in changes.removed)
            {
                if (m_Spawned.Remove(removed.Key, out var instance) && instance != null)
                    Destroy(instance);

                // The removed trackable still carries its last-known data, so its reference image
                // name is available to look the label back up even though it is gone from m_Spawned.
                if (m_BindingsByName.TryGetValue(removed.Value.referenceImage.name, out var binding))
                    SetLabelVisible(removed.Key, ResolveLabel(binding), false);
            }
        }

        void Spawn(ARTrackedImage trackedImage)
        {
            var imageName = trackedImage.referenceImage.name;
            if (!m_BindingsByName.TryGetValue(imageName, out var binding) || binding.prefab == null)
            {
                Debug.LogWarning(
                    $"[{nameof(TrackedImageModelSpawner)}] No prefab bound to reference image " +
                    $"'{imageName}'. Add a binding in the inspector.", this);
                return;
            }

            // Parenting to the tracked image means AR Foundation keeps the model glued to the
            // picture for us -- we never have to write the pose ourselves.
            var instance = Instantiate(binding.prefab, trackedImage.transform);
            instance.name = $"{binding.prefab.name} ({imageName})";
            m_Spawned[trackedImage.trackableId] = instance;

            UpdateSpawned(trackedImage);
        }

        void UpdateSpawned(ARTrackedImage trackedImage)
        {
            if (!m_Spawned.TryGetValue(trackedImage.trackableId, out var instance) || instance == null)
                return;

            var imageName = trackedImage.referenceImage.name;
            if (!m_BindingsByName.TryGetValue(imageName, out var binding))
                return;

            // trackedImage.size is the physical size of the picture in metres and can change as
            // the provider refines its estimate, so the fit is re-applied on every update.
            Fit(instance, binding, trackedImage.size);

            var visible = !m_HideWhenTrackingLost ||
                          trackedImage.trackingState == TrackingState.Tracking;

            if (instance.activeSelf != visible)
                instance.SetActive(visible);

            SetLabelVisible(trackedImage.trackableId, ResolveLabel(binding), visible);
        }

        static string ResolveLabel(TrackedImageBinding binding) =>
            string.IsNullOrWhiteSpace(binding.displayName)
                ? binding.referenceImageName
                : binding.displayName;

        /// <summary>
        /// Tracks which trackable currently owns the HUD label. The most recently shown model
        /// wins; if it is the one that just disappeared, any other still-visible model takes
        /// over, or the label clears once nothing is left.
        /// </summary>
        void SetLabelVisible(TrackableId id, string label, bool visible)
        {
            if (visible)
            {
                m_VisibleLabels[id] = label;
                SetCurrentLabel(label);
                return;
            }

            if (!m_VisibleLabels.Remove(id) || m_CurrentLabel != label)
                return;

            using var remaining = m_VisibleLabels.Values.GetEnumerator();
            SetCurrentLabel(remaining.MoveNext() ? remaining.Current : string.Empty);
        }

        void SetCurrentLabel(string label)
        {
            if (m_CurrentLabel == label)
                return;

            m_CurrentLabel = label;
            activeLabelChanged?.Invoke(label);
        }

        static void Fit(GameObject instance, TrackedImageBinding binding, Vector2 imageSize)
        {
            var width = imageSize.x;
            if (width <= 0f)
                return;

            // Prefabs are authored one unit wide, so the target width is the scale factor.
            var scale = width * binding.widthRelativeToImage;
            instance.transform.localScale = Vector3.one * scale;
            instance.transform.localPosition = binding.offsetRelativeToImage * width;
            instance.transform.localRotation = Quaternion.identity;
        }
    }
}

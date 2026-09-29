using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARVDU
{
    /// <summary>
    /// One entry in the pool a tap draws from. Deliberately shaped like
    /// <see cref="TrackedImageBinding"/> so the two spawners read as siblings.
    /// </summary>
    [Serializable]
    public class PlaceableModel
    {
        [Tooltip("Shown in the HUD when this model is placed. Falls back to the prefab name.")]
        public string displayName;

        public GameObject prefab;

        [Tooltip("Extra yaw applied after facing the camera, for prefabs whose visual front " +
                 "isn't +Z. The turbine's nacelle nose points -Z, so it wants 180.")]
        public float yawOffsetDegrees;
    }

    /// <summary>
    /// Drops a random model onto whatever AR plane the user taps, or in mid-air just in front of
    /// the camera via <see cref="SpawnAtCamera"/>.
    /// <para>
    /// Tapping is plane-only by design: no mid-air fallback. A tap that doesn't land on a
    /// detected surface does nothing, rather than guessing a depth to float the model at.
    /// Mid-air placement is a separate, explicit action (the HUD's Spawn button), at a fixed
    /// offset from the phone rather than a guessed surface.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(ARRaycastManager))]
    [RequireComponent(typeof(ARAnchorManager))]
    public class TapToPlaceSpawner : MonoBehaviour
    {
        [SerializeField]
        List<PlaceableModel> m_Placeables = new();

        [SerializeField]
        [Tooltip("Target width of a placed model, in metres. The prefabs are authored one unit " +
                 "wide, so this doubles as the uniform scale.")]
        float m_WidthMetres = 0.25f;

        [SerializeField]
        bool m_FaceCameraOnPlace = true;

        [SerializeField]
        [Tooltip("Never place the same model twice in a row. With only three in the pool, pure " +
                 "random repeats often enough to look broken.")]
        bool m_AvoidImmediateRepeat = true;

        [SerializeField]
        [Tooltip("Screen rects that swallow a tap instead of placing -- the HUD buttons. " +
                 "See IsOverBlockingUi for why this isn't EventSystem.IsPointerOverGameObject.")]
        List<RectTransform> m_BlockingRects = new();

        [SerializeField]
        Camera m_Camera;

        [SerializeField]
        [Tooltip("How far in front of the camera SpawnAtCamera places a model, in metres. Measured " +
                 "along the view direction, so the model lands where the phone is pointing.")]
        float m_AirSpawnDistanceMetres = 0.5f;

        [SerializeField]
        [Tooltip("How far below that point the model's base goes, in metres. The models are about " +
                 "30 cm tall with their pivot at the base, so 0.25 puts one just below the middle " +
                 "of the view.")]
        float m_AirSpawnDropMetres = 0.25f;

        [SerializeField]
        [Tooltip("Optional. When set, planes it considers redundant are skipped when choosing " +
                 "where a tap lands. Subsumed planes are skipped regardless.")]
        PlaneOverlapFilter m_OverlapFilter;

        ARRaycastManager m_RaycastManager;
        ARAnchorManager m_AnchorManager;

        readonly List<ARRaycastHit> m_Hits = new();
        readonly List<GameObject> m_Placed = new();
        readonly List<ARAnchor> m_Anchors = new();
        int m_LastIndex = -1;

        /// <summary>Display name of the model that was just placed.</summary>
        public event Action<string> modelPlaced;

        public int placedCount => m_Placed.Count;

        void Awake()
        {
            m_RaycastManager = GetComponent<ARRaycastManager>();
            m_AnchorManager = GetComponent<ARAnchorManager>();

            if (m_Camera == null)
            {
                var origin = GetComponent<XROrigin>();
                m_Camera = origin != null && origin.Camera != null ? origin.Camera : Camera.main;
            }
        }

        void Update()
        {
            if (!TryGetTap(out var screenPosition))
                return;

            if (IsOverBlockingUi(screenPosition))
                return;

            TryPlace(screenPosition);
        }

        /// <summary>
        /// Reads a press from the touchscreen, falling back to the mouse so the whole feature
        /// can be exercised in the Editor's Game view against XR Simulation.
        /// </summary>
        static bool TryGetTap(out Vector2 screenPosition)
        {
            screenPosition = default;

            var touchscreen = Touchscreen.current;
            if (touchscreen != null)
            {
                if (!touchscreen.primaryTouch.press.wasPressedThisFrame)
                    return false;

                screenPosition = touchscreen.primaryTouch.position.ReadValue();
                return true;
            }

            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return false;

            screenPosition = mouse.position.ReadValue();
            return true;
        }

        /// <summary>
        /// A plain rect test rather than <c>EventSystem.IsPointerOverGameObject</c>: that answer
        /// is whatever the UI module recorded the last time it processed input, so on the
        /// finger-down frame it can still report "not over UI" and let the tap fall through to
        /// placement. This is a pure function of the tap coordinate and survives execution-order
        /// differences between the Editor and the device.
        /// </summary>
        bool IsOverBlockingUi(Vector2 screenPosition)
        {
            foreach (var rect in m_BlockingRects)
            {
                if (rect == null || !rect.gameObject.activeInHierarchy)
                    continue;

                // Null camera is correct for a Screen Space - Overlay canvas, where a
                // RectTransform's world coordinates already are screen pixels.
                if (RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null))
                    return true;
            }

            return false;
        }

        bool TryPlace(Vector2 screenPosition)
        {
            // PlaneWithinPolygon, not PlaneWithinBounds: a plane's bounds are a rectangle that
            // overshoots the detected polygon, so bounds hits land off the real surface.
            if (!m_RaycastManager.Raycast(screenPosition, m_Hits, TrackableType.PlaneWithinPolygon))
                return false;

            if (!TryPickHit(out var hit, out var plane))
                return false;

            var model = PickRandom();
            if (model?.prefab == null)
                return false;

            var position = hit.pose.position;
            var pose = new Pose(position, ResolveYaw(m_Camera != null
                ? m_Camera.transform.position - position
                : Vector3.zero, model));

            // Anchoring is what makes a placed model stay on its real-world spot: the provider
            // keeps re-posing the anchor as its map of the room improves, and a model parented
            // under it rides along. AF's docs steer to this over ARAnchorManager.anchorPrefab.
            ARAnchor anchor = null;
            if (m_AnchorManager.enabled && m_AnchorManager.subsystem != null)
                anchor = m_AnchorManager.AttachAnchor(plane, pose);

            if (anchor == null)
            {
                Debug.LogWarning(
                    $"[{nameof(TapToPlaceSpawner)}] Could not anchor to plane {plane.trackableId}; " +
                    "placing unanchored -- it may drift.", this);
            }

            Spawn(model, pose, anchor);
            return true;
        }

        /// <summary>
        /// Places a random model in mid-air, a little in front of and below the camera. Public,
        /// void and argument-free because the HUD's Spawn button binds to it as a serialized
        /// persistent listener.
        /// <para>
        /// <c>async void</c> rather than a discarded <see cref="Awaitable"/>: this is an event
        /// handler with no caller to await it, and async void routes exceptions to Unity's
        /// synchronization context, so they land in the Console instead of vanishing.
        /// </para>
        /// </summary>
        public async void SpawnAtCamera()
        {
            if (m_Camera == null)
                return;

            var model = PickRandom();
            if (model?.prefab == null)
                return;

            // Captured now, not after the await: the phone keeps moving while the anchor is
            // being created, and the model belongs where it was when the button was pressed.
            // Forward along the view direction so it's on screen whatever the phone's tilt; the
            // drop is along world down, so "lower" means lower in the room, not the screen.
            var cameraTransform = m_Camera.transform;
            var cameraPosition = cameraTransform.position;
            var position = cameraPosition
                + cameraTransform.forward * m_AirSpawnDistanceMetres
                + Vector3.down * m_AirSpawnDropMetres;

            var pose = new Pose(position, ResolveYaw(cameraPosition - position, model));

            // There's no plane to attach to in mid-air, so this is a free-standing anchor. It
            // still matters: an unanchored object sits at fixed Unity coordinates, and those
            // drift against the real room as ARCore refines its map.
            ARAnchor anchor = null;
            if (m_AnchorManager.enabled && m_AnchorManager.subsystem != null)
            {
                var result = await m_AnchorManager.TryAddAnchorAsync(pose);
                if (result.status.IsSuccess())
                    anchor = result.value;
            }

            // The await can outlive this component (scene unload, app quit).
            if (this == null)
                return;

            if (anchor == null)
            {
                Debug.LogWarning(
                    $"[{nameof(TapToPlaceSpawner)}] Could not create an anchor at the camera; " +
                    "placing unanchored -- it may drift.", this);
            }

            Spawn(model, pose, anchor);
        }

        /// <summary>
        /// Instantiates <paramref name="model"/> at <paramref name="pose"/> -- under
        /// <paramref name="anchor"/> when there is one -- and records it so <see cref="ClearAll"/>
        /// can remove it. Shared by tap placement and <see cref="SpawnAtCamera"/>.
        /// </summary>
        void Spawn(PlaceableModel model, Pose pose, ARAnchor anchor)
        {
            GameObject instance;
            if (anchor != null)
            {
                // The anchor carries the pose, so the model sits at local identity under it.
                instance = Instantiate(model.prefab, anchor.transform);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                m_Anchors.Add(anchor);
            }
            else
            {
                instance = Instantiate(model.prefab, pose.position, pose.rotation);
            }

            // Prefabs are authored one unit wide, the same convention the image-tracking path
            // relies on, so the target width in metres is the uniform scale.
            instance.transform.localScale = Vector3.one * m_WidthMetres;
            instance.name = $"{model.prefab.name} (placed {m_Placed.Count + 1})";
            m_Placed.Add(instance);

            modelPlaced?.Invoke(ResolveLabel(model));
        }

        /// <summary>
        /// Picks the nearest hit that lands on a plane actually worth placing on.
        /// <para>
        /// Raycasting does not filter planes the way rendering does: a plane ARCore has already
        /// merged away is invisible but still reports hits, so taking the nearest hit blindly can
        /// drop a model onto a surface the user cannot see, at the wrong height. Redundant
        /// duplicates (see <see cref="PlaneOverlapFilter"/>) have the same problem, so both are
        /// skipped in favour of the plane that is actually on screen.
        /// </para>
        /// </summary>
        bool TryPickHit(out ARRaycastHit hit, out ARPlane plane)
        {
            // m_Hits is sorted nearest-first, so the first acceptable one is the right one.
            foreach (var candidate in m_Hits)
            {
                if (candidate.trackable is not ARPlane candidatePlane)
                    continue;

                if (candidatePlane.subsumedBy != null)
                    continue;

                if (candidatePlane.trackingState != TrackingState.Tracking)
                    continue;

                if (m_OverlapFilter != null && m_OverlapFilter.IsRedundant(candidatePlane))
                    continue;

                hit = candidate;
                plane = candidatePlane;
                return true;
            }

            hit = default;
            plane = null;
            return false;
        }

        static string ResolveLabel(PlaceableModel model) =>
            string.IsNullOrWhiteSpace(model.displayName) ? model.prefab.name : model.displayName;

        PlaceableModel PickRandom()
        {
            if (m_Placeables.Count == 0)
                return null;

            if (m_Placeables.Count == 1)
            {
                m_LastIndex = 0;
                return m_Placeables[0];
            }

            int index;
            do
            {
                // Fully qualified -- `using System;` above makes a bare `Random` ambiguous.
                index = UnityEngine.Random.Range(0, m_Placeables.Count);
            }
            while (m_AvoidImmediateRepeat && index == m_LastIndex);

            m_LastIndex = index;
            return m_Placeables[index];
        }

        /// <summary>
        /// Removes every placed model. Public, void and argument-free because the HUD button
        /// binds to it as a serialized persistent listener.
        /// </summary>
        public void ClearAll()
        {
            // Models first: they're children of the anchors, and removing an anchor only takes
            // its children with it whenever the provider gets round to it.
            foreach (var instance in m_Placed)
            {
                if (instance != null)
                    Destroy(instance);
            }

            m_Placed.Clear();

            foreach (var anchor in m_Anchors)
            {
                if (anchor == null)
                    continue;

                if (m_AnchorManager.enabled && m_AnchorManager.TryRemoveAnchor(anchor))
                    continue;

                Destroy(anchor.gameObject);
            }

            m_Anchors.Clear();
        }

        /// <summary>
        /// Upright, yaw-only rotation that turns the model's front toward <paramref name="facing"/>.
        /// <para>
        /// <see cref="ARRaycastHit.pose"/>'s own rotation is ignored on purpose: on a horizontal
        /// plane its up axis is the plane normal and its yaw is arbitrary, so models would land
        /// at random headings. Flattening Y is what keeps the model upright -- using the raw
        /// camera-to-model vector would tip a turbine over to "look up" at a phone held above it.
        /// </para>
        /// </summary>
        Quaternion ResolveYaw(Vector3 facing, PlaceableModel model)
        {
            var offset = Quaternion.AngleAxis(model.yawOffsetDegrees, Vector3.up);

            if (!m_FaceCameraOnPlace)
                return offset;

            facing.y = 0f;

            // Looking straight up or down: there's no meaningful heading to turn toward.
            if (facing.sqrMagnitude < 1e-6f)
                return offset;

            return Quaternion.LookRotation(facing.normalized, Vector3.up) * offset;
        }
    }
}

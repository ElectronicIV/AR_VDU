using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARVDU
{
    /// <summary>
    /// Works out which detected planes are redundant, so one real surface isn't represented by a
    /// pile of overlapping trackables.
    /// <para>
    /// ARCore already merges planes it is confident about and reports the loser through
    /// <see cref="ARPlane.subsumedBy"/>. What it does not do is reconcile planes it never became
    /// confident enough to merge -- a table routinely ends up as two or three slabs a couple of
    /// centimetres apart. Those all render, all raycast, and a tap can land on whichever one the
    /// provider happens to return first, which is how models end up floating slightly off a
    /// surface or sunk into it.
    /// </para>
    /// <para>
    /// This suppresses rather than destroys. The plane objects belong to
    /// <see cref="ARPlaneManager"/>, and a plane that looks redundant now can grow to become the
    /// dominant one later, so every decision is recomputed from scratch and reversible.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(ARPlaneManager))]
    public class PlaneOverlapFilter : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("How far apart, along the surface normal, two planes can sit and still count as " +
                 "the same surface. Roughly the thickness of a tabletop's detection error.")]
        float m_HeightTolerance = 0.06f;

        [SerializeField]
        [Tooltip("How far apart two planes' normals can point and still count as the same surface.")]
        float m_NormalToleranceDegrees = 12f;

        [SerializeField]
        [Tooltip("Seconds between recomputes. Planes report changes most frames while they grow, " +
                 "and the comparison is pairwise, so there's no point doing it at frame rate.")]
        float m_RefreshInterval = 0.25f;

        ARPlaneManager m_PlaneManager;

        readonly HashSet<TrackableId> m_Suppressed = new();
        readonly List<ARPlane> m_Candidates = new();
        readonly List<TrackableId> m_Previous = new();

        bool m_Dirty = true;
        float m_NextRefresh;

        /// <summary>Raised when the set of suppressed planes changes.</summary>
        public event Action suppressionChanged;

        /// <summary>
        /// True when this plane should be neither drawn nor placed on, because another plane
        /// already represents the same real surface.
        /// </summary>
        public bool IsRedundant(ARPlane plane)
        {
            if (plane == null)
                return true;

            return plane.subsumedBy != null || m_Suppressed.Contains(plane.trackableId);
        }

        void Awake() => m_PlaneManager = GetComponent<ARPlaneManager>();

        void OnEnable()
        {
            m_PlaneManager.trackablesChanged.AddListener(OnTrackablesChanged);
            m_Dirty = true;
        }

        void OnDisable()
        {
            m_PlaneManager.trackablesChanged.RemoveListener(OnTrackablesChanged);

            if (m_Suppressed.Count == 0)
                return;

            // Leaving planes suppressed after this component is switched off would strand them
            // invisible with nothing left to revive them.
            m_Suppressed.Clear();
            suppressionChanged?.Invoke();
        }

        void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> changes) => m_Dirty = true;

        void Update()
        {
            if (!m_Dirty || Time.time < m_NextRefresh)
                return;

            m_Dirty = false;
            m_NextRefresh = Time.time + m_RefreshInterval;
            Recompute();
        }

        void Recompute()
        {
            m_Previous.Clear();
            foreach (var id in m_Suppressed)
                m_Previous.Add(id);

            m_Suppressed.Clear();
            m_Candidates.Clear();

            foreach (var plane in m_PlaneManager.trackables)
            {
                // Untracked planes are stale and subsumed ones are already the provider's own
                // losers; neither should win a comparison or be worth suppressing again.
                if (plane != null && plane.subsumedBy == null &&
                    plane.trackingState == TrackingState.Tracking)
                {
                    m_Candidates.Add(plane);
                }
            }

            // Largest first, so the biggest plane covering a surface is the one that survives and
            // everything sitting on top of it loses.
            m_Candidates.Sort(static (a, b) => Area(b).CompareTo(Area(a)));

            for (var i = 0; i < m_Candidates.Count; i++)
            {
                var winner = m_Candidates[i];
                if (m_Suppressed.Contains(winner.trackableId))
                    continue;

                for (var j = i + 1; j < m_Candidates.Count; j++)
                {
                    var loser = m_Candidates[j];
                    if (m_Suppressed.Contains(loser.trackableId))
                        continue;

                    if (IsSameSurface(winner, loser))
                        m_Suppressed.Add(loser.trackableId);
                }
            }

            if (HasChanged())
                suppressionChanged?.Invoke();
        }

        bool HasChanged()
        {
            if (m_Previous.Count != m_Suppressed.Count)
                return true;

            foreach (var id in m_Previous)
            {
                if (!m_Suppressed.Contains(id))
                    return true;
            }

            return false;
        }

        static float Area(ARPlane plane) => plane.size.x * plane.size.y;

        /// <summary>
        /// Whether <paramref name="loser"/> is describing the same real surface as
        /// <paramref name="winner"/>: pointing the same way, sitting at the same height, and
        /// centred somewhere on top of it.
        /// <para>
        /// Deliberately conservative -- it requires the smaller plane's centre to fall inside the
        /// larger plane's boundary, so a plane that merely clips a corner survives. Showing one
        /// redundant plane is a much cheaper mistake than hiding a surface the user wanted to tap.
        /// </para>
        /// </summary>
        bool IsSameSurface(ARPlane winner, ARPlane loser)
        {
            if (winner.alignment != loser.alignment)
                return false;

            if (Vector3.Angle(winner.normal, loser.normal) > m_NormalToleranceDegrees)
                return false;

            var delta = loser.center - winner.center;
            if (Mathf.Abs(Vector3.Dot(delta, winner.normal)) > m_HeightTolerance)
                return false;

            return ContainsPoint(winner, loser.center);
        }

        /// <summary>
        /// Even-odd point-in-polygon test against a plane's real boundary, rather than treating
        /// the plane as its bounding rectangle -- ARCore boundaries are frequently far from
        /// rectangular, and the bounding box overlaps neighbouring surfaces it doesn't touch.
        /// </summary>
        static bool ContainsPoint(ARPlane plane, Vector3 worldPoint)
        {
            // Plane space maps a boundary's (x, y) onto the transform's local (x, _, z).
            var local = plane.transform.InverseTransformPoint(worldPoint);
            var point = new Vector2(local.x, local.z);

            var boundary = plane.boundary;
            if (boundary.Length < 3)
                return false;

            var inside = false;
            for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
            {
                var a = boundary[i];
                var b = boundary[j];

                if (a.y > point.y == b.y > point.y)
                    continue;

                var t = (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
                if (point.x < t)
                    inside = !inside;
            }

            return inside;
        }
    }
}

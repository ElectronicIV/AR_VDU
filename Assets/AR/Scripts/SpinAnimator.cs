using UnityEngine;

namespace ARVDU
{
    /// <summary>
    /// Constant-rate rotation plus an optional vertical bob. Used to give the marker models a
    /// little life without pulling in an Animator and a clip for every one of them.
    /// </summary>
    public class SpinAnimator : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Local axis to rotate about.")]
        Vector3 m_Axis = Vector3.up;

        [SerializeField]
        [Tooltip("Degrees per second. Negative reverses the direction.")]
        float m_DegreesPerSecond = 45f;

        [SerializeField]
        [Tooltip("Peak vertical travel, in local units. 0 disables the bob.")]
        float m_BobAmplitude;

        [SerializeField]
        float m_BobCyclesPerSecond = 0.5f;

        Vector3 m_StartLocalPosition;
        float m_Phase;

        public float degreesPerSecond
        {
            get => m_DegreesPerSecond;
            set => m_DegreesPerSecond = value;
        }

        void Awake() => m_StartLocalPosition = transform.localPosition;

        void Update()
        {
            var dt = Time.deltaTime;

            if (m_Axis != Vector3.zero && m_DegreesPerSecond != 0f)
                transform.Rotate(m_Axis.normalized, m_DegreesPerSecond * dt, Space.Self);

            if (m_BobAmplitude == 0f)
                return;

            m_Phase += dt * m_BobCyclesPerSecond * Mathf.PI * 2f;
            transform.localPosition =
                m_StartLocalPosition + Vector3.up * (Mathf.Sin(m_Phase) * m_BobAmplitude);
        }
    }
}

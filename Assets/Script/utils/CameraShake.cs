using System.Collections;
using UnityEngine;

public class CameraShake : MonoBehaviour
{
    public static CameraShake Instance { get; private set; }

    [Header("Default Camera Shake")]
    [Min(0f)]
    [SerializeField] private float defaultDuration = 0.3f;
    [Min(0f)]
    [SerializeField] private float defaultMagnitude = 0.2f;

    [Header("Player Trigger Shake")]
    [Tooltip("Enable only when this component is attached to a Collider2D trigger, not the camera.")]
    [SerializeField] private bool shakeOnPlayerTrigger;
    [Min(0f)]
    [SerializeField] private float triggerDuration = 0.2f;
    [Min(0f)]
    [SerializeField] private float triggerMagnitude = 0.08f;
    [SerializeField] private bool triggerOnce;

    private bool hasTriggered;

    private void Awake()
    {
        // Trigger instances only request a shake from the camera instance.
        if (shakeOnPlayerTrigger)
        {
            return;
        }

        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    public void Shake()
    {
        Shake(defaultDuration, defaultMagnitude);
    }

    public void Shake(float duration, float magnitude)
    {
        StartCoroutine(DoShake(duration, magnitude));
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!shakeOnPlayerTrigger || (triggerOnce && hasTriggered))
        {
            return;
        }

        PlayerMovement player = other.GetComponentInParent<PlayerMovement>();
        if (player == null || player != PlayerMovement.LocalPlayer)
        {
            return;
        }

        Instance?.Shake(triggerDuration, triggerMagnitude);
        hasTriggered = true;
    }

    private IEnumerator DoShake(float duration, float magnitude)
    {
        Vector3 origin = transform.localPosition;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            float x = Random.Range(-1f, 1f) * magnitude;
            float y = Random.Range(-1f, 1f) * magnitude;
            transform.localPosition = new Vector3(origin.x + x, origin.y + y, origin.z);
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        transform.localPosition = origin;
    }
}

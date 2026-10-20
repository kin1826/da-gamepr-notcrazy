using System.Collections;
using System;
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
    private Coroutine activeShake;
    private Vector3 restLocalPosition;

    public float DefaultDuration => defaultDuration;
    public float DefaultMagnitude => defaultMagnitude;

    private void Awake()
    {
        restLocalPosition = transform.localPosition;

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
        Shake(duration, magnitude, null);
    }

    public void Shake(float duration, float magnitude, Action onCompleted)
    {
        if (duration <= 0f || magnitude <= 0f)
        {
            transform.localPosition = restLocalPosition;
            onCompleted?.Invoke();
            return;
        }

        if (activeShake != null)
        {
            StopCoroutine(activeShake);
            activeShake = null;
            transform.localPosition = restLocalPosition;
        }

        activeShake = StartCoroutine(DoShake(duration, magnitude, onCompleted));
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
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

    private IEnumerator DoShake(float duration, float magnitude, Action onCompleted)
    {
        float elapsed = 0f;

        while (elapsed < duration)
        {
            float x = UnityEngine.Random.Range(-1f, 1f) * magnitude;
            float y = UnityEngine.Random.Range(-1f, 1f) * magnitude;
            transform.localPosition = new Vector3(
                restLocalPosition.x + x,
                restLocalPosition.y + y,
                restLocalPosition.z
            );
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        transform.localPosition = restLocalPosition;
        activeShake = null;
        onCompleted?.Invoke();
    }
}

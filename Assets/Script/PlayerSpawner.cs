using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlayerSpawner : NetworkBehaviour
{
    [SerializeField, Min(0f)] private float spawnProtectionDuration = 0.15f;

    private readonly NetworkVariable<bool> networkSpawnProtected = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private bool localSpawnProtected;
    private Coroutine spawnRoutine;

    public bool IsSpawnProtected => localSpawnProtected
        || (IsSpawned && networkSpawnProtected.Value);

    private void Start()
    {
        // Single-player instances are instantiated locally and never receive
        // OnNetworkSpawn, so they run the same stabilization sequence here.
        if (!IsSpawned)
        {
            StartSpawnSequence();
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        SceneManager.sceneLoaded += OnSceneLoaded;

        if (SceneManager.GetActiveScene().name == "Menu")
        {
            BeginSpawnProtection();
            SetPhysics(false);
            SetVisible(false);
            return;
        }

        StartSpawnSequence();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!IsOwner) return;

        if (scene.name == "Menu")
        {
            StopSpawnSequence();
            BeginSpawnProtection();
            SetPhysics(false);
            SetVisible(false);
            return;
        }

        StartSpawnSequence();
    }

    public void BeginSpawnProtection()
    {
        // Only the owner writes the networked protection state. Non-owner
        // copies receive it through the NetworkVariable.
        if (IsSpawned && !IsOwner)
        {
            return;
        }

        localSpawnProtected = true;
        if (IsSpawned)
        {
            networkSpawnProtected.Value = true;
        }
    }

    private void EndSpawnProtection()
    {
        if (IsSpawned && !IsOwner)
        {
            return;
        }

        localSpawnProtected = false;
        if (IsSpawned)
        {
            networkSpawnProtected.Value = false;
        }
    }

    private void StartSpawnSequence()
    {
        StopSpawnSequence();
        BeginSpawnProtection();
        SetPhysics(false);
        SetVisible(false);
        spawnRoutine = StartCoroutine(SpawnSequence());
    }

    private void StopSpawnSequence()
    {
        if (spawnRoutine == null)
        {
            return;
        }

        StopCoroutine(spawnRoutine);
        spawnRoutine = null;
    }

    private IEnumerator SpawnSequence()
    {
        // Frame 1: scene is loaded but NGO hasn't finished syncing in-scene NetworkObjects.
        yield return null;

        // Wait until spawn points exist — guarantees scene objects are initialized.
        yield return new WaitUntil(() =>
            GameObject.FindGameObjectsWithTag("Spawn").Length > 0);

        // Frame 2: let physics engine process the scene's colliders for 1 extra tick.
        yield return new WaitForFixedUpdate();

        // Set position while physics is still disabled (avoids teleport-under-load jitter).
        TryMoveToSpawnPoint();
        Physics2D.SyncTransforms();

        // Give the physics world one full step to register the teleported
        // collider before allowing the player to move again.
        yield return new WaitForFixedUpdate();

        // Re-enable physics.
        SetPhysics(true);

        // Wait one FixedUpdate so the physics engine registers the new position
        // before the player becomes visible. Prevents 1-frame pop at wrong position.
        yield return new WaitForFixedUpdate();

        SetVisible(true);

        if (spawnProtectionDuration > 0f)
        {
            yield return new WaitForSecondsRealtime(spawnProtectionDuration);
        }

        EndSpawnProtection();
        spawnRoutine = null;
    }

    private void TryMoveToSpawnPoint()
    {
        if (SceneManager.GetActiveScene().name == "Menu") return;

        try
        {
            GameObject[] spawns = GameObject.FindGameObjectsWithTag("Spawn");

            if (spawns == null || spawns.Length == 0)
            {
                Debug.LogError("PlayerSpawner: No spawn points found.");
                return;
            }

            Array.Sort(spawns, (a, b) => string.CompareOrdinal(a.name, b.name));

            int index = (int)OwnerClientId;
            if (index >= spawns.Length)
                index = spawns.Length - 1;

            Vector3 spawnPos = spawns[index].transform.position;

            Rigidbody2D rb = GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.position = spawnPos;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }

            transform.position = spawnPos;
        }
        catch (Exception ex)
        {
            Debug.LogError($"PlayerSpawner: Error spawning player {OwnerClientId}: {ex.Message}");
        }
    }

    private void SetPhysics(bool enabled)
    {
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb != null) rb.simulated = enabled;
    }

    private void SetVisible(bool visible)
    {
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            r.enabled = visible;
    }

    public override void OnNetworkDespawn()
    {
        StopSpawnSequence();

        if (IsOwner)
            SceneManager.sceneLoaded -= OnSceneLoaded;

        base.OnNetworkDespawn();
    }
}

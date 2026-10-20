using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

public class FunctionPanelController : MonoBehaviour
{
    public static FunctionPanelController Instance;

    [Header("Panels")]
    public GameObject pausePanel;
    public GameObject confirmPanel;

    [Header("Pause Button Icons")]
    public GameObject iconSingle;
    public GameObject iconMulti;

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        iconSingle.SetActive(!GameData.IsMultiplayer);
        iconMulti.SetActive(GameData.IsMultiplayer);
    }

    // nút Pause/Exit gọi hàm này
    public void OnPauseButtonPressed()
    {
        if (GameData.IsMultiplayer)
        {
            confirmPanel.SetActive(true);
        }
        else
        {
            bool isPausing = !pausePanel.activeSelf;
            pausePanel.SetActive(isPausing);
            Time.timeScale = isPausing ? 0f : 1f;
        }
    }

    // ── SINGLE PLAYER ─────────────────────────────────────

    public void Resume()
    {
        pausePanel.SetActive(false);
        confirmPanel.SetActive(false);
        Time.timeScale = 1f;
    }

    public void Restart()
    {
        Time.timeScale = 1f;
        LevelManager.Instance.LoadLevel(SceneManager.GetActiveScene().name);
    }

    public void MainMenu()
    {
        Time.timeScale = 1f;
        LevelManager.Instance.LoadLevel("Menu");
    }

    // ── MULTIPLAYER CONFIRM ────────────────────────────────

    public void CancelExit()
    {
        confirmPanel.SetActive(false);
    }

    public async void ConfirmExit()
    {
        confirmPanel.SetActive(false);
        GameData.IsLeavingMatchIntentionally = true;

        if (NetworkConfig.Instance != null)
        {
            await NetworkConfig.Instance.LeaveActiveSessionAsync();
        }
        else if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
        }

        SceneManager.LoadScene("Menu");

        
    }
}

using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LevelSelectManager : MonoBehaviour
{
    public static LevelSelectManager Instance { get; private set; }

    public GameObject
        levelButtonPrefab;

    public Transform content;

    public int totalLevel = 20;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void GenerateAgain()
    {
        if (content == null)
        {
            Debug.LogError("LevelSelectManager: Content reference is missing or has been destroyed.");
            return;
        }

        for (int i = content.childCount - 1; i >= 0; i--)
        {
            GameObject child = content.GetChild(i).gameObject;
            child.SetActive(false);
            Destroy(child);
        }

        GenerateLevels();
    }

    void GenerateLevels()
    {
        for(
            int i=1;
            i<=totalLevel;
            i++
        )
        {
            GameObject obj =
                Instantiate(
                    levelButtonPrefab,
                    content
                );

            int level = i;

            TMP_Text txt =
                obj.GetComponentInChildren
                <TMP_Text>();

            txt.text =
                level.ToString();

            Button btn =
                obj.GetComponent<Button>();

            btn.interactable =
                level <=
                SaveManager
                .Data
                .highestLevel;

            btn.onClick
            .AddListener(
                ()=>SelectLevel(
                    level
                )
            );
        }
    }

    public void SelectLevel(
        int level
    )
    {
        GameData.SelectedLevel =
            level;

        MainManager.Instance.StartGame();
    }
}

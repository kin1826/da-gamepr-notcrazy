using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Gắn trực tiếp lên GameObject LoginPanel trong Canvas của Menu scene.
// Panel này tự ẩn khi user đã login trước đó.
public class AuthUIManager : MonoBehaviour
{
    [Header("Sub-panels")]
    public GameObject loginSubPanel;
    public GameObject registerSubPanel;
    public GameObject profileSubPanel;
    public GameObject loadingOverlay;

    [Header("Login")]
    public TMP_InputField loginEmail;
    public TMP_InputField loginPassword;
    public TMP_Text       loginError;

    [Header("Register")]
    public TMP_InputField registerName;
    public TMP_InputField registerEmail;
    public TMP_InputField registerPassword;
    public TMP_Text       registerError;

    [Header("Profile")]
    public TMP_Text profileName;
    public TMP_Text profileEmail;

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (UserSession.IsGuest || UserSession.TryRestore())
        {
            UserSession.CommitPendingLevelRewards();
            ClosePanel();
            MainManager.Instance?.RefreshPlayerName();
            MainManager.Instance?.RefreshGold();
            if (!UserSession.IsGuest)
            {
                ApiManager.Instance?.GetGold(serverGold =>
                {
                    UserSession.ApplyServerGold(serverGold);
                });
                ApiManager.Instance?.StartGoldPolling();
                DailyRewardPanel.Instance?.TryAutoShow();
            }
            return;
        }

        ShowLogin();
    }

    // ── PANEL SWITCHING ───────────────────────────────────────────────────────

    public void ShowLogin()
    {
        SetPanel(loginSubPanel);
        loginError.text = "";
    }

    public void ShowRegister()
    {
        SetPanel(registerSubPanel);
        registerError.text = "";
    }

    public void ShowProfile()
    {
        var user = UserSession.Current;
        if (user == null) return;

        profileName.text  = user.name;
        profileEmail.text = UserSession.IsGuest ? "Chế độ khách" : user.email;
        SetPanel(profileSubPanel);
    }

    public void SetPanel(GameObject active)
    {
        loginSubPanel.SetActive(loginSubPanel == active);
        registerSubPanel.SetActive(registerSubPanel == active);
        profileSubPanel.SetActive(profileSubPanel == active);
        loadingOverlay.SetActive(false);
    }

    // ── LOGIN ─────────────────────────────────────────────────────────────────

    public void OnLoginClick()
    {
        string email = loginEmail.text.Trim();
        string pass  = loginPassword.text;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass))
        {
            loginError.text = "Vui lòng nhập đầy đủ thông tin.";
            return;
        }

        loadingOverlay.SetActive(true);

        ApiManager.Instance.Login(
            new LoginRequest { email = email, password = pass },
            onSuccess: res =>
            {
                UserSession.Set(res);
                MainManager.Instance?.RefreshPlayerName();
                MainManager.Instance?.RefreshGold();
                ApiManager.Instance?.StartGoldPolling();
                ClosePanel();
                DailyRewardPanel.Instance?.TryAutoShow();
            },
            onError: err =>
            {
                loadingOverlay.SetActive(false);
                loginError.text = err;
            });
    }

    // ── REGISTER ──────────────────────────────────────────────────────────────

    public void OnRegisterClick()
    {
        string name  = registerName.text.Trim();
        string email = registerEmail.text.Trim();
        string pass  = registerPassword.text;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass))
        {
            registerError.text = "Vui lòng nhập đầy đủ thông tin.";
            return;
        }

        if (pass.Length < 6)
        {
            registerError.text = "Mật khẩu phải ít nhất 6 ký tự.";
            return;
        }

        loadingOverlay.SetActive(true);

        ApiManager.Instance.Register(
            new RegisterRequest { name = name, email = email, password = pass },
            onSuccess: res =>
            {
                UserSession.Set(res);
                MainManager.Instance?.RefreshPlayerName();
                MainManager.Instance?.RefreshGold();
                ApiManager.Instance?.StartGoldPolling();
                ClosePanel();
                DailyRewardPanel.Instance?.TryAutoShow();
            },
            onError: err =>
            {
                loadingOverlay.SetActive(false);
                registerError.text = err;
            });
    }

    // ── CLOSE PANEL ──────────────────────────────────────────────────────────

    public void ClosePanel()
    {
        if (!UserSession.IsLoggedIn && !UserSession.IsGuest)
        {
            ShowLogin();
            return;
        }

        loginSubPanel.SetActive(false);
        registerSubPanel.SetActive(false);
        profileSubPanel.SetActive(false);
        loadingOverlay.SetActive(false);
    }

    // ── LOGOUT ────────────────────────────────────────────────────────────────

    public void OnLogoutClick()
    {
        UserSession.Clear();
        ShowLogin();
    }

    // ── GUEST LOGIN ───────────────────────────────────────────────────────────

    public void OnGuestLoginClick()
    {
        UserSession.SetGuest();
        UserSession.CommitPendingLevelRewards();
        MainManager.Instance?.RefreshPlayerName();
        ClosePanel();
    }

    // ── GOOGLE SIGN-IN ────────────────────────────────────────────────────────

    public void OnGoogleSignInClick()
    {
#if UNITY_ANDROID || UNITY_IOS
        loadingOverlay.SetActive(true);
        StartGoogleSignIn();
#else
        loginError.text = "Google Sign-In chỉ hoạt động trên Android/iOS.";
#endif
    }

    private void StartGoogleSignIn()
    {
        // Bỏ comment sau khi cài Google Sign-In plugin
        // GoogleSignIn.Configuration = new GoogleSignInConfiguration
        // {
        //     WebClientId    = "YOUR_WEB_CLIENT_ID.apps.googleusercontent.com",
        //     RequestIdToken = true
        // };
        // GoogleSignIn.DefaultInstance.SignIn().ContinueWith(task =>
        // {
        //     if (task.IsFaulted || task.IsCanceled)
        //     {
        //         UnityMainThreadDispatcher.Instance.Enqueue(() =>
        //         {
        //             loadingOverlay.SetActive(false);
        //             loginError.text = "Google Sign-In thất bại.";
        //         });
        //         return;
        //     }
        //     UnityMainThreadDispatcher.Instance.Enqueue(() =>
        //         SendGoogleToken(task.Result.IdToken));
        // });

        loadingOverlay.SetActive(false);
        loginError.text = "Chưa cài Google Sign-In plugin.";
    }

    public void SendGoogleToken(string idToken)
    {
        loadingOverlay.SetActive(true);

        ApiManager.Instance.GoogleLogin(
            new GoogleLoginRequest { idToken = idToken },
            onSuccess: res =>
            {
                UserSession.Set(res);
                ClosePanel();
            },
            onError: err =>
            {
                loadingOverlay.SetActive(false);
                loginError.text = err;
            });
    }
}

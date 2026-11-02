using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Singleton game manager for the Infest XR cockroach game.
/// Score-based: kill as many cockroaches as possible before time runs out.
/// After the round ends, A or X starts a new round in the same room;
/// B or Y restarts entirely (scene reload → back to the room scan screen).
/// All values are configurable in the Inspector.
/// </summary>
public class InfestGameManager : MonoBehaviour
{
    public static InfestGameManager Instance { get; private set; }

    // ─── Configurable Settings ──────────────────────────────────────────────────
    [Header("Game Settings")]
    [Tooltip("Game duration in seconds (default 5 minutes = 300)")]
    public float gameDurationSeconds = 300f;

    [Tooltip("Max cockroaches alive at the same time")]
    public int maxAliveAtOnce = 22;

    [Tooltip("Game length (minutes) that gets the base spray/flashlight budgets")]
    public float resourceBaselineMinutes = 3f;

    [Tooltip("Extra seconds of spray AND flashlight per minute of game length above the baseline")]
    public float resourceBonusPerMinute = 30f;

    /// <summary>
    /// Extra seconds added to both the spray and flashlight budgets, based
    /// on the game length the player picked (e.g. +30s per minute above 3).
    /// </summary>
    public float ResourceBonusSeconds =>
        Mathf.Max(0f, gameDurationSeconds / 60f - resourceBaselineMinutes) * resourceBonusPerMinute;

    // ─── HUD ────────────────────────────────────────────────────────────────────
    [Header("HUD (Auto-created if null)")]
    [Tooltip("World-space TextMeshPro for the score")]
    public TextMeshPro killCountText;

    [Tooltip("World-space TextMeshPro for the timer")]
    public TextMeshPro timerText;

    [Tooltip("World-space TextMeshPro for game over message")]
    public TextMeshPro gameOverText;

    [Tooltip("Offset from the camera for the HUD — positive Y puts it at the top of the view")]
    public Vector3 hudOffset = new Vector3(0f, 0.25f, 0.8f);

    // ─── Events ─────────────────────────────────────────────────────────────────
    [Header("Events")]
    public UnityEvent<int> OnScoreChanged;             // (score)
    public UnityEvent<float> OnTimerTick;              // (remaining seconds)
    public UnityEvent<int> OnGameOver;                 // (final score)
    public UnityEvent OnGameStarted;

    // ─── Runtime State ──────────────────────────────────────────────────────────
    public int KillCount { get; private set; }
    public int SpawnedCount { get; private set; }
    public int AliveCount { get; private set; }
    public float TimeRemaining { get; private set; }
    public bool IsGameActive { get; private set; }
    public bool IsGameOver { get; private set; }

    private Transform hudParent;
    private Camera mainCam;

    // Hold B / Y this long during a round to abandon it and go back to the
    // room scan screen (a plain press would be too easy to hit by accident)
    private const float FullRestartHoldSeconds = 2f;
    private float fullRestartHold = 0f;

    // ─── Lifecycle ──────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Update()
    {
        // After a round ends: A/X plays again in the same room,
        // B/Y restarts entirely — back to the room scan screen
        if (IsGameOver)
        {
            if (OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three))
            {
                RestartGame();
            }
            else if (OVRInput.GetDown(OVRInput.Button.Two) || OVRInput.GetDown(OVRInput.Button.Four))
            {
                FullRestart();
            }
            return;
        }

        if (!IsGameActive) return;

        // Countdown timer
        TimeRemaining -= Time.deltaTime;
        TimeRemaining = Mathf.Max(0f, TimeRemaining);

        OnTimerTick?.Invoke(TimeRemaining);
        UpdateHUD();

        // Escape hatch mid-round: hold B or Y to abandon the game and return
        // to the room scan screen (wrong room scanned, darkness too harsh…)
        if (OVRInput.Get(OVRInput.Button.Two) || OVRInput.Get(OVRInput.Button.Four))
        {
            fullRestartHold += Time.deltaTime;
            if (timerText != null)
                timerText.text = $"RESTART IN {Mathf.CeilToInt(FullRestartHoldSeconds - fullRestartHold)}…";
            if (fullRestartHold >= FullRestartHoldSeconds)
            {
                FullRestart();
                return;
            }
        }
        else
        {
            fullRestartHold = 0f;
        }

        // Round ends when time runs out
        if (TimeRemaining <= 0f)
        {
            EndGame("TIME'S UP!");
        }
    }

    /// <summary>
    /// Restart entirely: reload the scene, which brings back the room scan
    /// prompt and the whole setup flow (darkness, battery, game length).
    /// Statics survive the reload but all of them self-heal — FurnitureCover
    /// re-reads MRUK on its periodic refresh and cached transforms are
    /// re-found once Unity reports the old ones as destroyed.
    /// </summary>
    public void FullRestart()
    {
        Debug.Log("[InfestGame] Full restart — reloading scene, back to the room scan screen");
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    /// <summary>
    /// Called by SprayCanController when the can runs dry — no spray means
    /// no way to score, so the round ends immediately.
    /// </summary>
    public void NotifySprayEmpty()
    {
        if (!IsGameActive || IsGameOver) return;
        EndGame("OUT OF SPRAY!");
    }


    void LateUpdate()
    {
        // Keep HUD in front of camera
        if (hudParent != null && mainCam != null)
        {
            hudParent.position = mainCam.transform.position + mainCam.transform.TransformDirection(hudOffset);
            hudParent.rotation = Quaternion.LookRotation(hudParent.position - mainCam.transform.position);
        }
    }

    // ─── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this when the room is ready and hands are assigned to begin the game.
    /// </summary>
    public void StartGame()
    {
        if (IsGameActive) return;

        KillCount = 0;
        SpawnedCount = 0;
        AliveCount = 0;
        TimeRemaining = gameDurationSeconds;
        IsGameActive = true;
        IsGameOver = false;
        fullRestartHold = 0f;

        SetupHUD();
        UpdateHUD();

        // Bring the score/timer back (hidden during the game over screen)
        if (killCountText != null) killCountText.gameObject.SetActive(true);
        if (timerText != null) timerText.gameObject.SetActive(true);

        if (gameOverText != null)
            gameOverText.gameObject.SetActive(false);

        Debug.Log($"[InfestGame] Game started! Kill as many cockroaches as you can in {gameDurationSeconds}s");
        OnGameStarted?.Invoke();
    }

    /// <summary>
    /// Starts a fresh round after game over: clears leftover cockroaches,
    /// resets score and timer, and restarts the spawner.
    /// </summary>
    public void RestartGame()
    {
        if (!IsGameOver) return;

        // Clear any roaches left over from the previous round
        foreach (var roach in FindObjectsByType<CockroachBehavior>(FindObjectsSortMode.None))
        {
            Destroy(roach.gameObject);
        }

        IsGameOver = false;
        IsGameActive = false;
        StartGame();

        var spawner = FindAnyObjectByType<CockroachSpawner>();
        if (spawner != null)
        {
            spawner.RestartSpawning();
        }

        Debug.Log("[InfestGame] New game started");
    }

    /// <summary>
    /// Called by CockroachSpawner when a new cockroach is instantiated.
    /// No total budget — only the max-alive cap limits spawning.
    /// </summary>
    public bool CanSpawnMore()
    {
        return IsGameActive && !IsGameOver
            && AliveCount < maxAliveAtOnce;
    }

    /// <summary>
    /// Register a newly spawned cockroach.
    /// </summary>
    public void OnCockroachSpawned()
    {
        SpawnedCount++;
        AliveCount++;
        UpdateHUD();
    }

    /// <summary>
    /// Called by CockroachBehavior when a cockroach is killed.
    /// </summary>
    public void OnCockroachKilled()
    {
        KillCount++;
        AliveCount--;
        AliveCount = Mathf.Max(0, AliveCount);

        Debug.Log($"[InfestGame] Kill! Score: {KillCount}");
        OnScoreChanged?.Invoke(KillCount);
        UpdateHUD();
    }

    /// <summary>
    /// Called if a cockroach is destroyed without being killed (e.g., cleanup).
    /// </summary>
    public void OnCockroachRemoved()
    {
        AliveCount--;
        AliveCount = Mathf.Max(0, AliveCount);
    }

    // ─── Internal ───────────────────────────────────────────────────────────────

    private void EndGame(string title)
    {
        IsGameOver = true;
        IsGameActive = false;

        string msg = $"{title}\nFinal Score: {KillCount}\n\n" +
                     "Press A or X to play again\n" +
                     "Press B or Y to restart from room scan";

        Debug.Log($"[InfestGame] Game Over ({title}) — Final Score: {KillCount}");

        // Hide the score/timer HUD — only the game over screen remains
        if (killCountText != null) killCountText.gameObject.SetActive(false);
        if (timerText != null) timerText.gameObject.SetActive(false);

        if (gameOverText != null)
        {
            gameOverText.gameObject.SetActive(true);
            gameOverText.text = msg;
        }

        OnGameOver?.Invoke(KillCount);
    }

    private void SetupHUD()
    {
        mainCam = Camera.main;
        if (mainCam == null)
        {
            // Try to find the OVR center eye camera
            var centerEye = GameObject.Find("CenterEyeAnchor");
            if (centerEye != null)
                mainCam = centerEye.GetComponent<Camera>();
        }

        // Auto-create HUD if not assigned
        if (killCountText == null || timerText == null)
        {
            hudParent = new GameObject("InfestHUD").transform;

            if (killCountText == null)
            {
                var killObj = new GameObject("KillCountText");
                killObj.transform.SetParent(hudParent);
                killObj.transform.localPosition = new Vector3(-0.15f, 0f, 0f);
                killCountText = killObj.AddComponent<TextMeshPro>();
                killCountText.fontSize = 0.5f;
                killCountText.alignment = TextAlignmentOptions.Center;
                killCountText.color = Color.white;
                killCountText.rectTransform.sizeDelta = new Vector2(0.3f, 0.1f);
            }

            if (timerText == null)
            {
                var timerObj = new GameObject("TimerText");
                timerObj.transform.SetParent(hudParent);
                timerObj.transform.localPosition = new Vector3(0.15f, 0f, 0f);
                timerText = timerObj.AddComponent<TextMeshPro>();
                timerText.fontSize = 0.5f;
                timerText.alignment = TextAlignmentOptions.Center;
                timerText.color = Color.white;
                timerText.rectTransform.sizeDelta = new Vector2(0.3f, 0.1f);
            }

            if (gameOverText == null)
            {
                var goObj = new GameObject("GameOverText");
                goObj.transform.SetParent(hudParent);
                // HUD parent sits at the top of the view — drop the game over
                // message down so it reads at eye level
                goObj.transform.localPosition = new Vector3(0f, -0.25f, 0f);
                gameOverText = goObj.AddComponent<TextMeshPro>();
                gameOverText.fontSize = 0.8f;
                gameOverText.alignment = TextAlignmentOptions.Center;
                gameOverText.color = Color.red;
                gameOverText.rectTransform.sizeDelta = new Vector2(0.5f, 0.2f);
                goObj.SetActive(false);
            }
        }
        else
        {
            hudParent = killCountText.transform.parent;
        }
    }

    private void UpdateHUD()
    {
        if (killCountText != null)
            killCountText.text = $"Score: {KillCount}";

        if (timerText != null)
        {
            int minutes = Mathf.FloorToInt(TimeRemaining / 60f);
            int seconds = Mathf.FloorToInt(TimeRemaining % 60f);
            timerText.text = $"{minutes}:{seconds:D2}";
        }
    }
}

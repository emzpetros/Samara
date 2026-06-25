using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [SerializeField] private Button startButton;
    [SerializeField] private MoveLevel level;
    [SerializeField] private GameObject gameOverScreen;

    [SerializeField] private GameObject winUI;

    private PlayerController playerController;

    private void Awake() {
        Instance = this;
    }
    private void Start() {
        startButton.onClick.AddListener(OnStart);
        playerController = PlayerController.Instance;
        playerController.OnNoLift += PlayerController_OnNoLift;
    }

    private void PlayerController_OnNoLift(object sender, System.EventArgs e) {
        Time.timeScale = 0;
        gameOverScreen.SetActive(true);
    }

    private void OnStart() {
        startButton.gameObject.SetActive(false);
        playerController.ToggleGravity();
        playerController.ToggleControl();
        playerController.ToggleLift();

        level.ToggleMovement();
    }

    public void LevelComplete() {
        Time.timeScale = 0;
        winUI.SetActive(true);
    }
}

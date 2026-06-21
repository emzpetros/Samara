using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [SerializeField] private Button startButton;
    [SerializeField] private MoveLevel level;

    private PlayerController playerController;

    private void Awake() {
        Instance = this;
    }
    private void Start() {
        startButton.onClick.AddListener(OnStart);
        playerController = PlayerController.Instance;
    }

    private void OnStart() {
        startButton.gameObject.SetActive(false);
        playerController.ToggleGravity();
        playerController.ToggleControl();
        playerController.ToggleLift();

        level.ToggleMovement();
    }

    public void EndLevel() {
        Debug.Log("End");
    }
}

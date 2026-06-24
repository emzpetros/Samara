using UnityEngine.UI;
using UnityEngine;
using System;
using UnityEngine.SceneManagement;

public class PauseUI : MonoBehaviour
{
    [SerializeField] private GameObject pauseVisuals;
    [SerializeField] private Button mainMenuButton;
    [SerializeField] private Button resumeButton;

    private bool paused = false;
    private GameInput gameInput;


    private void Start() {
        gameInput = GameInput.Instance;
        mainMenuButton.onClick.AddListener(MainMenu_OnClick);


        resumeButton.onClick.AddListener(Resume_OnClick);
        pauseVisuals.SetActive(false);
        gameInput.OnPause += GameInput_OnPause;
    }

    private void GameInput_OnPause(object sender, EventArgs e) {
        if (!paused) {

            pauseVisuals.SetActive(true);
            Time.timeScale = 0;
            paused = true;
        }
        else {
            Unpause();
        }
    }

    private void Resume_OnClick() {
        Unpause();
    }

    private void MainMenu_OnClick() {
        Unpause();
        SceneManager.LoadScene(Scenes.MainMenu.ToString());
    }

  

    private void Unpause() {
        pauseVisuals.SetActive(false);
        Time.timeScale = 1;
        paused = false;
    }
}

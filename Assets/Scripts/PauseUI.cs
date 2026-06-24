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

    private void Awake() {
        mainMenuButton.onClick.AddListener(MainMenu_OnClick);


        resumeButton.onClick.AddListener(Resume_OnClick);
    }

    private void Resume_OnClick() {
        Unpause();
    }

    private void MainMenu_OnClick() {
        Unpause();
        SceneManager.LoadScene(Scenes.MainMenu.ToString());
    }

    // Update is called once per frame
    void Update() {
        if (Input.GetKeyDown(KeyCode.Escape)) {
            if (!paused) {

                pauseVisuals.SetActive(true);
                Time.timeScale = 0;
                paused = true;
            }
            else {
                Unpause();
            }
        }
    }

    private void Unpause() {
        pauseVisuals.SetActive(false);
        Time.timeScale = 1;
        paused = false;
    }
}

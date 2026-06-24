using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
public class GameOverUI : MonoBehaviour
{
    [SerializeField] private GameObject gameOverVisuals;
    [SerializeField] private Button mainMenuButton;
    [SerializeField] private Button restartButton;


    private void Start() {
        mainMenuButton.onClick.AddListener(MainMenu_OnClick);


        restartButton.onClick.AddListener(Restart_OnClick);
        gameOverVisuals.SetActive(false);
    }


    
    private void Restart_OnClick() {
        Time.timeScale = 1;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    private void MainMenu_OnClick() {
        Time.timeScale = 1;
        SceneManager.LoadScene(Scenes.MainMenu.ToString());
    }




}

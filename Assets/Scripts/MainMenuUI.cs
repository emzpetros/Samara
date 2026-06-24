using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System;

public enum Scenes { Level1, Level2, Level3, MainMenu }
public class MainMenuUI : MonoBehaviour
{
    
    [SerializeField] private Button level1Button;
    [SerializeField] private Button level2Button;
    [SerializeField] private Button level3Button;
    [SerializeField] private Button QuitButton;

    private void Awake() {
        level1Button.onClick.AddListener(Level1_OnClick);
        level1Button.onClick.AddListener(Level2_OnClick);
        level1Button.onClick.AddListener(Level3_OnClick);
        level1Button.onClick.AddListener(Quit_OnClick);
    }

    private void Quit_OnClick() {
        Application.Quit();
    }

    private void Level3_OnClick() {
        SceneManager.LoadScene(Scenes.Level3.ToString());
    }

    private void Level2_OnClick() {
        SceneManager.LoadScene(Scenes.Level2.ToString());
    }

    private void Level1_OnClick() {
        SceneManager.LoadScene(Scenes.Level1.ToString());
    }
}

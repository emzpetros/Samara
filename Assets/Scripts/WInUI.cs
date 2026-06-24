using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class WInUI : MonoBehaviour {
    [SerializeField] private GameObject winVisuals;
    [SerializeField] private Button mainMenuButton;
    [SerializeField] private Button nextLevelButton;


    private void Start() {
        mainMenuButton.onClick.AddListener(MainMenu_OnClick);


        nextLevelButton.onClick.AddListener(nextLevel_OnClick);
        winVisuals.SetActive(false);
    }



    private void nextLevel_OnClick() {
        Time.timeScale = 1;

        string currentScene = SceneManager.GetActiveScene().name;
        Scenes nextScene = 0;
        switch (currentScene) {
            case "Level1":
                nextScene = Scenes.Level2;
                break;
            case "Level2":

                nextScene = Scenes.Level3;
                break;
            case "Level3":
                nextScene = Scenes.Credits;
                break;
        }

        SceneManager.LoadScene(nextScene.ToString());
    }

    private void MainMenu_OnClick() {
        Time.timeScale = 1;
        SceneManager.LoadScene(Scenes.MainMenu.ToString());
    }


}

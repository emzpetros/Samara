using UnityEngine;

using UnityEngine.UI;

public class SpinLIftBarUI : MonoBehaviour
{
    private Slider slider;
    private PlayerController player;

    private void Start() {
        slider = GetComponent<Slider>();
        player = PlayerController.Instance;

        slider.maxValue = player.LiftMaxAmount;
    }

    private void Update() {
        UpdateBar();
    }
    private void UpdateBar() {
        slider.value = player.LiftAmount;

    }
}

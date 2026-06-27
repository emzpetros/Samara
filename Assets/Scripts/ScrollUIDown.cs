using UnityEngine;
using UnityEngine.SceneManagement;

public class UIScrollDown : MonoBehaviour {
    [Tooltip("Speed at which the UI object scrolls down (units per second)")]
    public float scrollSpeed = 100f;

    private RectTransform rectTransform;

    void Awake() {
        rectTransform = GetComponent<RectTransform>();
        if (rectTransform == null) {
            Debug.LogError("UIScrollDown script must be attached to a UI object with RectTransform.");
        }
    }

    void Update() {
        if (rectTransform != null) {
            // Move downward by scrollSpeed pixels per second
            rectTransform.anchoredPosition -= Vector2.down * scrollSpeed * Time.deltaTime;
        }

        if (this.rectTransform.position.y > 2000) {
            SceneManager.LoadScene("Start");
        }
    }
}

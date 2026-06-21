using UnityEngine;

public class EndZone : MonoBehaviour
{
    private void OnTriggerEnter(Collider other) {
        PlayerController player;
        if(other.gameObject.TryGetComponent<PlayerController>(out player)) {
            GameManager.Instance.EndLevel();
        }
    }
}

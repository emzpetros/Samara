using UnityEngine;

public class Obstacle : MonoBehaviour
{
    [SerializeField] private const float LIFT_SUBTRACT_AMOUNT = -0.5f;


    private void OnTriggerEnter(Collider other) {
        PlayerController player;
        if (other.gameObject.TryGetComponent<PlayerController>(out player)) {
            player.AddLiftAmount(LIFT_SUBTRACT_AMOUNT);
        }


    }
}

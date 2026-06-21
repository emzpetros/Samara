using UnityEngine;

public class LiftPickup : MonoBehaviour
{
    [SerializeField]private const float LIFT_ADD_AMOUNT = 2f;
    private void OnTriggerEnter(Collider other) {
        PlayerController player;
        if (other.gameObject.TryGetComponent<PlayerController>(out player) ){
            player.AddLiftAmount(LIFT_ADD_AMOUNT);
        }

        
    }
}

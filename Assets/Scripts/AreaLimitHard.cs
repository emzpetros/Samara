using Unity.VisualScripting;
using UnityEngine;

public class AreaLimitHard : MonoBehaviour
{
    [SerializeField] private SLOW_DIR dir;
    
    private void OnTriggerEnter(Collider other) {

        ToggleSlowEFfect(other);
    }
    private void OnTriggerExit(Collider other) {
        ToggleSlowEFfect(other);
    }

    private void ToggleSlowEFfect(Collider other) {
        

        PlayerController player;
        if (other.gameObject.TryGetComponent<PlayerController>(out player)) {

            player.ToggleStop(dir);

      
        }
    }
}

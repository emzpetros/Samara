using UnityEngine;

public class UpdraftZone : MonoBehaviour
{
    [SerializeField] private float draftForce = 1000;

    private void OnTriggerEnter(Collider other) {
        PlayerController player;
        if(other.gameObject.TryGetComponent<PlayerController>(out player)) {
            player.ApplyUpdraft(draftForce);
        }
    }
}

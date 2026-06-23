using UnityEngine;

public class FollowPlayer : MonoBehaviour
{
    private PlayerController player;

    private void Start() {
        player = PlayerController.Instance;
    }

    private void LateUpdate() {
        //Vector3 currentPos = this.transform.position;
        this.transform.position = new Vector3(player.transform.position.x, player.transform.position.y, player.transform.position.z);
    }
}

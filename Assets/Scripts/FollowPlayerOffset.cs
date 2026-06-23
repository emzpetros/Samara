using UnityEngine;

public class FollowPlayerOffset : MonoBehaviour
{
    private PlayerController player;
    //private Vector3 offset;

    private void Start() {
        player = PlayerController.Instance;
        //offset = this.transform.position - player.transform.position; 
    }

    private void Update() {
        Vector3 currentPos = this.transform.position;
        this.transform.position = new Vector3(currentPos.x, currentPos.y, player.transform.position.z);
    }
}

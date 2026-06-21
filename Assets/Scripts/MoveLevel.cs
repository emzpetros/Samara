using UnityEngine;
using UnityEngine.Rendering.Universal;

public class MoveLevel : MonoBehaviour
{
    private bool canMove = false;
    [SerializeField] private float FORWARD_SPEED = 18f;

    private void FixedUpdate() {
        if (canMove) {
            this.transform.position += Vector3.back * FORWARD_SPEED * Time.deltaTime;
        }
    }
    public void ToggleMovement() {
        canMove = !canMove;
    }
}

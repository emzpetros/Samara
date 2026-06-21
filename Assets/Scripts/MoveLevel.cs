using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Splines;

public class MoveLevel : MonoBehaviour
{
    private bool canMove = false;
    [SerializeField] private float FORWARD_SPEED = 18f;
    [SerializeField] private SplineContainer splineContainer;
    private Vector3 playerStart;
    private Vector3 groundOffset;

    private float t = 0f;

    private void Start() {
        playerStart = PlayerController.Instance.transform.position;
        groundOffset = transform.position - playerStart;
    }

    void Update() {
        if (canMove) {

            t += (FORWARD_SPEED / splineContainer.Spline.GetLength()) * Time.deltaTime;
            t = Mathf.Clamp01(t);

            splineContainer.Spline.Evaluate(t, out var pos, out var tangent, out var up);

            // snap the level so current spline point sits exactly at player position
            transform.position = playerStart + groundOffset- (Vector3)pos;
        }
    }


    public void ToggleMovement() {
        canMove = !canMove;
    }
}

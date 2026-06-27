using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Splines;

public class MoveLevel : MonoBehaviour
{
    public static MoveLevel Instance { get; private set; }
    private bool canMove = false;
    [SerializeField] private float FORWARD_SPEED = 18f;
    [SerializeField] private SplineContainer splineContainer;
    private Vector3 playerStart;
    private Vector3 groundOffset;

    private float t = 0f;

    private void Awake() {
        Instance = this;
    }
    private void Start() {
        playerStart = PlayerController.Instance.transform.position;
        groundOffset = transform.position - playerStart;
    }

    void Update() {
        if (canMove) {

            t += (FORWARD_SPEED / splineContainer.Spline.GetLength()) * Time.deltaTime;
            t = Mathf.Clamp01(t);

            splineContainer.Spline.Evaluate(t, out var pos, out var tangent, out var up);

            //// snap the level so current spline point sits exactly at player position
            //transform.position = playerStart + groundOffset- (Vector3)pos;

            // 1. ROTATION FIRST
            if ((Vector3)tangent != Vector3.zero) {
                Quaternion targetRot = Quaternion.FromToRotation((Vector3)tangent, Vector3.forward);
                transform.rotation = Quaternion.Euler(0, targetRot.eulerAngles.y, 0);
            }

            // 2. POSITION SECOND — now uses the rotated transform
            // rotate pos by the level's new rotation before subtracting
            Vector3 rotatedPos = transform.rotation * (Vector3)pos;
            transform.position = playerStart + groundOffset - rotatedPos;
        }

/*        if (canMove) {
                t += (FORWARD_SPEED / splineContainer.Spline.GetLength()) * Time.deltaTime;
                t = Mathf.Clamp01(t);
                splineContainer.Spline.Evaluate(t, out var pos, out var tangent, out var up);

                // Translation only — no rotation
                transform.position = playerStart + groundOffset - (Vector3)pos;
                // transform.rotation stays at default — never touch it
            
        }*/
    }


    public void ToggleMovement() {
        canMove = !canMove;
    }
}

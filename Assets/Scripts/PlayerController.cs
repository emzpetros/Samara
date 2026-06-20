using UnityEngine;

public class PlayerController : MonoBehaviour
{

    public static PlayerController Instance { get; private set; }

    private GameInput gameInput;
    private Rigidbody rigidbody;

    private const float SPIN_TORQUE = 10;
    private const float MOVE_SPEED = 50;

    private bool jump = false;
    private bool updraft = false;

    private float draftForce;

    private void Awake() {
        if (Instance != null) {
            Debug.LogError("More than one player instance");
        }
        Instance = this;

        this.rigidbody = GetComponent<Rigidbody>();
    }
    private void Start() {
        this.gameInput = GameInput.Instance;
        gameInput.OnJumpInput += GameInput_OnJumpInput;
    }

    private void GameInput_OnJumpInput(object sender, System.EventArgs e) {
        jump = true;
    }

    private void FixedUpdate() {
        HandleMovement();

        updraft = false;
    }

    private void HandleMovement() {
        Vector2 moveInput = gameInput.GetMovementInput();
        Vector3 windInput = Vector3.zero;

        

        float xInput = moveInput.x;
        rigidbody.AddTorque(Vector3.up * xInput * SPIN_TORQUE );

        if (updraft) {
            windInput = Vector3.up * draftForce;

            Debug.Log(windInput);
        }

        rigidbody.AddForce( Vector3.right * xInput * MOVE_SPEED  + windInput);
    }

    //private void HandleWind() {
    //    if (updraft) {
    //        rigidbody.AddForce(
    //    }
    //}

   public void ApplyUpdraft(float draftForce) {
        updraft = true;
        this.draftForce = draftForce;
    }
}

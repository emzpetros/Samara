using AssetInventory;
using UnityEngine;

public class PlayerController : MonoBehaviour
{

    public static PlayerController Instance { get; private set; }
    public float LiftMaxAmount { get => liftMaxAmount; set => liftMaxAmount = value; }
    public float LiftAmount { get => liftAmount; set => liftAmount = value; }

    private GameInput gameInput;
    private Rigidbody rigidbody;

    private const float SPIN_TORQUE = 150;
    private const float MOVE_FORCE = 3;
    [SerializeField] private const float LIFT_FORCE = 100;
    [SerializeField] private const float GRAVITY_ACCELERATION = 0.2f;
    [SerializeField] private const float FORWARD_ACCELERATION = 0.1f;

    private bool spinLift = false;
    private bool updraft = false;

    private float draftForce;

    private static float liftMaxAmount = 10f;
    private float liftAmount;
    private float liftConsumptionAmount = 1f;

    private void Awake() {
        if (Instance != null) {
            Debug.LogError("More than one player instance");
        }
        Instance = this;

        this.rigidbody = GetComponent<Rigidbody>();
        liftAmount = LiftMaxAmount;
 
    }
    private void Start() {
        this.gameInput = GameInput.Instance;
        gameInput.OnSpinLiftInput += GameInput_OnSpinLiftInput;
        gameInput.OnSpinLiftCancelInput += GameInput_OnSpinLiftCancelInput;
    }

    private void GameInput_OnSpinLiftCancelInput(object sender, System.EventArgs e) {
        spinLift = false;
    }

    private void GameInput_OnSpinLiftInput(object sender, System.EventArgs e) {
        spinLift = true;
    }

    private void FixedUpdate() {

        HandleMovement();
        HandleWind();
        HandleGravity();
        HandleSpinLift();

        updraft = false;
    }

    private void HandleMovement() {

        Vector2 moveInput = gameInput.GetMovementInput();
       
        float xInput = moveInput.x;
        //rigidbody.AddTorque(Vector3.up * xInput * SPIN_TORQUE );

        rigidbody.AddForce(Vector2.right * xInput * MOVE_FORCE);
    }

    private void HandleWind() {
        Vector3 windInput = Vector3.zero;
        if (updraft) {
            windInput = Vector3.up * draftForce;
            rigidbody.AddForce(windInput, ForceMode.Acceleration);
        }
    }

    private void HandleGravity() {
        rigidbody.AddForce( Physics.gravity * GRAVITY_ACCELERATION, ForceMode.Acceleration);

        rigidbody.AddForce( Vector3.forward  * FORWARD_ACCELERATION, ForceMode.Acceleration);
    }

    private void HandleSpinLift() {
        if (liftAmount <= 0) {
            return;
        }

        if (spinLift) {
            rigidbody.AddTorque(Vector3.up * SPIN_TORQUE * Time.deltaTime);
            rigidbody.AddForce(Vector3.up * LIFT_FORCE * Time.deltaTime);
            liftAmount -= liftConsumptionAmount * Time.deltaTime;
        }
    }

    public void ApplyUpdraft(float draftForce) {
        updraft = true;
        this.draftForce = draftForce;
    }
}

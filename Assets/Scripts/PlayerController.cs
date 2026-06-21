using Unity.VisualScripting;
using UnityEngine;

public class PlayerController : MonoBehaviour
{

    public static PlayerController Instance { get; private set; }
    public float LiftMaxAmount { get => liftMaxAmount; set => liftMaxAmount = value; }
    public float LiftAmount { get => liftAmount; set => liftAmount = value; }

    private GameInput gameInput;
    private Rigidbody rigidbody;

    private const float SPIN_TORQUE = 150;
    private const float MOVE_FORCE = 5;
    [SerializeField] private  float LIFT_FORCE = 500;
    [SerializeField] private  float GRAVITY_ACCELERATION = 0.5f;

    private bool spinLift = false;
    private bool updraft = false;
    private bool hasGravity = false;
    private bool controlActive = false;
    private bool canLift = false;
    private bool isSlowedVertically = false;
    private bool isRightSlowed = false;
    private bool isLeftSlowed = false;

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
        if (controlActive) {
            Vector2 moveInput = gameInput.GetMovementInput();

            float xInput = moveInput.x;
            //rigidbody.AddTorque(Vector3.up * xInput * SPIN_TORQUE );

            rigidbody.AddForce(Vector2.right * xInput * MOVE_FORCE);
        }
        float horizontalSlowMultipler = 0.75f;
        if (isRightSlowed || isLeftSlowed) {

            rigidbody.linearVelocity = new Vector3(0f, rigidbody.linearVelocity.y, rigidbody.linearVelocity.z);
        }
        if (isRightSlowed) {
            rigidbody.AddForce(Vector3.left * LIFT_FORCE * horizontalSlowMultipler * Time.deltaTime);
        }
        if (isLeftSlowed) {
            rigidbody.AddForce(Vector3.right * LIFT_FORCE * horizontalSlowMultipler * Time.deltaTime);
        }

   
        }

    private void HandleWind() {
        Vector3 windInput = Vector3.zero;
        if (updraft) {
            windInput = Vector3.up * draftForce;
            rigidbody.AddForce(windInput, ForceMode.Acceleration);
        }
    }

    private void HandleGravity() {
        if (hasGravity) { 
            rigidbody.AddForce(Physics.gravity * GRAVITY_ACCELERATION, ForceMode.Acceleration);
    }}

    private void HandleSpinLift() {

        if (canLift) {
            if (liftAmount <= 0) {
                return;
            }

            if (spinLift) {
                rigidbody.AddTorque(Vector3.up * SPIN_TORQUE * Time.deltaTime);
                rigidbody.AddForce(Vector3.up * LIFT_FORCE * Time.deltaTime);
                liftAmount -= liftConsumptionAmount * Time.deltaTime;
            }
        }

        if (isSlowedVertically) {
            rigidbody.linearVelocity = new Vector3(rigidbody.linearVelocity.x, 0f, rigidbody.linearVelocity.z);
            rigidbody.AddForce(Vector3.down * LIFT_FORCE * 0.75f * Time.deltaTime);
        }
    }

    public void ApplyUpdraft(float draftForce) {
        updraft = true;
        this.draftForce = draftForce;
    }

    public void AddLiftAmount(float amount) {
        this.liftAmount += amount;
    }

    public void ToggleGravity() {
        hasGravity = !hasGravity;
    }

    public void ToggleControl() {
        controlActive = !controlActive;
    }

    public void ToggleLift() {
        canLift = !canLift;
    }

    public void ToggleVerticalSlow() {
    }

    public void ToggleRightSlow() {
        
    }
    public void ToggleLeftSlow() {
    }

    public void ToggleSlow(SLOW_DIR dir) {
        switch (dir) {
          case SLOW_DIR.Left:
            
        isLeftSlowed = !isLeftSlowed;
                Debug.Log("leftbound");
        break;
        case SLOW_DIR.Right:
            isRightSlowed = !isRightSlowed;
                Debug.Log("right bound");
                break;
        case SLOW_DIR.Up:
                ToggleLift();
            isSlowedVertically = !isSlowedVertically;
                Debug.Log("up bound");
                break;
    }
    }

    public void EnableNormalBehavior() {
        isRightSlowed = false;
        isLeftSlowed = false;
        isSlowedVertically = false;

        canLift = true;
        controlActive = true;
        hasGravity = true;
    }
}



using System;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;

public class PlayerController : MonoBehaviour
{
    public event EventHandler OnSpinStart;
    public event EventHandler OnSpinCancel;
    public event EventHandler OnObstacleHit;

    public static PlayerController Instance { get; private set; }
    public float LiftMaxAmount { get => liftMaxAmount; set => liftMaxAmount = value; }
    public float LiftAmount { get => liftAmount; set => liftAmount = value; }

    private GameInput gameInput;
    private Rigidbody rigidBody;

    [SerializeField] private  float SPIN_TORQUE = 150;
    [SerializeField] private  float MOVE_SPEED = 200;
    [SerializeField] private  float LIFT_FORCE = 750;
    [SerializeField] private  float GRAVITY_ACCELERATION = 0.4f;

    private bool spinLift = false;
    private bool updraft = false;
    private bool hasGravity = false;
    private bool controlActive = false;
    private bool canLift = false;
    private bool isStoppedVertically = false;
    private bool isSlowedVertically = false;
    private bool isRightSlowed = false;
    private bool isLeftSlowed = false;
    private bool allowLeft = true;
    private bool allowRight = true;

    private float draftForce;

    [SerializeField] private float liftMaxAmount = 10f;
    private float liftAmount;
    private float liftConsumptionAmount = 1f;

    private Camera mainCam;

    [SerializeField] private float viewportPadding = 0.1f;

    [SerializeField] private bool clampVertical = true;
    [SerializeField] private float viewportPaddingTop = 0.05f;
    
    [SerializeField] private float viewportPaddingBottom = 0.25f;

    [SerializeField] private float viewportPaddingVertical = 0.25f;


    private void Awake() {
        if (Instance != null) {
            Debug.LogError("More than one player instance");
        }
        Instance = this;

        this.rigidBody = GetComponent<Rigidbody>();
        liftAmount = LiftMaxAmount;
 
    }
    private void Start() {
        this.gameInput = GameInput.Instance;

        mainCam = Camera.main;
        gameInput.OnSpinLiftInput += GameInput_OnSpinLiftInput;
        gameInput.OnSpinLiftCancelInput += GameInput_OnSpinLiftCancelInput;
    }

    private void GameInput_OnSpinLiftCancelInput(object sender, System.EventArgs e) {
        spinLift = false;
        OnSpinCancel?.Invoke(this, EventArgs.Empty);
    }

    private void GameInput_OnSpinLiftInput(object sender, System.EventArgs e) {
        if (canLift) {

            spinLift = true;
            OnSpinStart?.Invoke(this, EventArgs.Empty);
        }

    }

    private void FixedUpdate() {

        HandleMovement();
        HandleWind();
        HandleGravity();
        HandleSpinLift();
        ClampToViewport();

        updraft = false;
    }
    private void ClampToViewport() {

        Vector3 viewPos = mainCam.WorldToViewportPoint(transform.position);

        float clampedX = Mathf.Clamp(viewPos.x, viewportPadding, 1f - viewportPadding);
        float clampedY = clampVertical
            ? Mathf.Clamp(viewPos.y, viewportPaddingVertical, 1f - viewportPaddingVertical)
            : viewPos.y;

        Vector3 clampedWorld = mainCam.ViewportToWorldPoint(new Vector3(clampedX, clampedY, viewPos.z));


        rigidBody.MovePosition(new Vector3(clampedWorld.x, clampedWorld.y, transform.position.z));

        float vx = rigidBody.linearVelocity.x;
        float vy = rigidBody.linearVelocity.y;

        if (viewPos.x <= viewportPadding && vx < 0f) vx = 0f;
        if (viewPos.x >= 1f - viewportPadding && vx > 0f) vx = 0f;

        if (clampVertical) {
            if (viewPos.y >= 1f - viewportPaddingVertical && vy > 0f) vy = 0f;
            if (viewPos.y <= viewportPaddingVertical && vy < 0f) {
                vy = 0f;
                SlowDecreaseLift();
            }
        }

        rigidBody.linearVelocity = new Vector3(vx, vy, rigidBody.linearVelocity.z);
    }

    private void SlowDecreaseLift() {
        liftAmount -= 0.01f;
    }

    private void HandleMovement() {
        if (controlActive) {
            Vector2 moveInput = gameInput.GetMovementInput();

            float xInput = moveInput.x;
            //rigidbody.AddTorque(Vector3.up * xInput * SPIN_TORQUE );

            if (!allowLeft) {
                xInput = Mathf.Clamp(xInput, 0f, 1f);
            }
            else if (!allowRight) {
                xInput = Mathf.Clamp(-xInput, -1f, 0f);
            }

            rigidBody.linearVelocity = new Vector2( xInput * MOVE_SPEED * Time.deltaTime, rigidBody.linearVelocity.y);
           
        }
        float horizontalSlowMultipler = 2f;
        if (isRightSlowed || isLeftSlowed) {

            rigidBody.linearVelocity = new Vector3(0f, rigidBody.linearVelocity.y, rigidBody.linearVelocity.z);
        }
        if (isRightSlowed) {
            rigidBody.AddForce(Vector3.left * LIFT_FORCE * horizontalSlowMultipler * Time.deltaTime);
        }
        if (isLeftSlowed) {
            rigidBody.AddForce(Vector3.right * LIFT_FORCE * horizontalSlowMultipler * Time.deltaTime);
        }

   
        }

    private void HandleWind() {
        Vector3 windInput = Vector3.zero;
        if (updraft) {
            windInput = Vector3.up * draftForce;
            rigidBody.AddForce(windInput, ForceMode.Acceleration);
        }
    }

    private void HandleGravity() {
        if (hasGravity) { 
            rigidBody.AddForce(Physics.gravity * GRAVITY_ACCELERATION, ForceMode.Acceleration);
    }}

    private void HandleSpinLift() {

        if (canLift) {
            if (liftAmount <= 0) {
                return;
            }

            if (spinLift) {
                rigidBody.AddTorque(Vector3.up * SPIN_TORQUE * Time.deltaTime);
                rigidBody.AddForce(Vector3.up * LIFT_FORCE * Time.deltaTime);
                liftAmount -= liftConsumptionAmount * Time.deltaTime;
            }
        }

        if (isSlowedVertically) {
            rigidBody.AddForce(Vector3.down * LIFT_FORCE * 1.1f * Time.deltaTime);
        }

        if (isStoppedVertically) {
            rigidBody.linearVelocity = new Vector3(rigidBody.linearVelocity.x, 0f, rigidBody.linearVelocity.z);
            rigidBody.AddForce(Vector3.down * LIFT_FORCE * 0.75f * Time.deltaTime);
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

    public void ToggleStop(SLOW_DIR dir) {
        switch (dir) {
          case SLOW_DIR.Left:
                allowLeft = !allowLeft;
        isLeftSlowed = !isLeftSlowed;
        break;
        case SLOW_DIR.Right:
                allowRight = !allowRight;
            isRightSlowed = !isRightSlowed;
                break;
        case SLOW_DIR.Up:
                ToggleLift();
            isStoppedVertically = !isStoppedVertically;
             
                break;
    }
    }

    public void ToggleSlow(SLOW_DIR dir) {
        ToggleLift();
        isSlowedVertically = !isSlowedVertically;
    }

    public void EnableNormalBehavior() {
        isRightSlowed = false;
        isLeftSlowed = false;
        isStoppedVertically = false;

        canLift = true;
        controlActive = true;
        hasGravity = true;
    }

    public void ObstacleHit(float hitPenalty) {
        liftAmount += hitPenalty;
        OnObstacleHit?.Invoke(this, EventArgs.Empty);
    }

}



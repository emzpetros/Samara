using UnityEngine;
using UnityEngine.InputSystem;
using System;

public class GameInput : MonoBehaviour {
    public static GameInput Instance { get; private set; }

    private InputSystem_Actions playerInputActions;

    public event EventHandler OnSpinLiftInput;
    public event EventHandler OnSpinLiftCancelInput;
    public event EventHandler OnAttackInput;
    public event EventHandler OnItemInput;
    public event EventHandler OnPause;

    private void Awake() {
        Instance = this;

        playerInputActions = new InputSystem_Actions();
        playerInputActions.Enable();

    }

    private void Start() {

        playerInputActions.PlayerSamara.SpinLift.performed += SpinLift_performed;
        playerInputActions.PlayerSamara.SpinLift.canceled += SpinLift_canceled;
        playerInputActions.PlayerSamara.Pause.performed += Pause_performed;
        //playerInputActions.PlayerSamara.Attack.performed += Attack_performed;
        //playerInputActions.PlayerSamara.Item1.performed += Item_performed;

    }

    private void Pause_performed(InputAction.CallbackContext obj) {
        OnPause?.Invoke(this, EventArgs.Empty);
    }

    private void SpinLift_canceled(InputAction.CallbackContext obj) {
        OnSpinLiftCancelInput?.Invoke(this, EventArgs.Empty);
    }

    private void Item_performed(InputAction.CallbackContext context) {
        OnItemInput?.Invoke(this, EventArgs.Empty);
    }

    private void Attack_performed(InputAction.CallbackContext context) {
        OnAttackInput?.Invoke(this, EventArgs.Empty);
    }

    private void SpinLift_performed(InputAction.CallbackContext context) {
        OnSpinLiftInput?.Invoke(this, EventArgs.Empty);
    }

    public Vector2 GetMovementInput() {
        Vector2 input = playerInputActions.PlayerSamara.Move.ReadValue<Vector2>();

        return input.normalized;
    }
}

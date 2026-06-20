using UnityEngine;
using UnityEngine.InputSystem;
using System;

public class GameInput : MonoBehaviour {
    public static GameInput Instance { get; private set; }

    private InputSystem_Actions playerInputActions;

    public event EventHandler OnJumpInput;
    public event EventHandler OnAttackInput;
    public event EventHandler OnItemInput;

    private void Awake() {
        Instance = this;

        playerInputActions = new InputSystem_Actions();
        playerInputActions.Enable();

    }

    private void Start() {

        playerInputActions.PlayerSamara.Jump.performed += Jump_performed;
        //playerInputActions.PlayerSamara.Attack.performed += Attack_performed;
        //playerInputActions.PlayerSamara.Item1.performed += Item_performed;

    }

    private void Item_performed(InputAction.CallbackContext context) {
        OnItemInput?.Invoke(this, EventArgs.Empty);
    }

    private void Attack_performed(InputAction.CallbackContext context) {
        OnAttackInput?.Invoke(this, EventArgs.Empty);
    }

    private void Jump_performed(InputAction.CallbackContext context) {
        OnJumpInput?.Invoke(this, EventArgs.Empty);
    }

    public Vector2 GetMovementInput() {
        Vector2 input = playerInputActions.PlayerSamara.Move.ReadValue<Vector2>();

        return input.normalized;
    }
}

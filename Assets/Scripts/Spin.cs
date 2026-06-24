using UnityEngine;

public class Spin : MonoBehaviour
{
    private float SPIN_TORQUE = 150;
    private Rigidbody rigidBody;

    private void Awake() {
        rigidBody = GetComponent<Rigidbody>();
    }

    private void FixedUpdate() {
        rigidBody.AddTorque(Vector3.up * SPIN_TORQUE * Time.deltaTime);
    }
}

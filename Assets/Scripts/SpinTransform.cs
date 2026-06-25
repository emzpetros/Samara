using UnityEngine;

public class SpinTransform : MonoBehaviour
{
    [SerializeField] private float SPIN_SPEED = 10f;

    // Update is called once per frame
    void Update()
    {
        this.transform.Rotate(Vector3.forward * SPIN_SPEED);
    }
}

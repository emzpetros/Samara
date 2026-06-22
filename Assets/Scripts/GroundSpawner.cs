using UnityEngine;
using UnityEngine.Splines;

public class GroundSpawner : MonoBehaviour
{
    [SerializeField] private SplineContainer splineContainer;
    [SerializeField] private GameObject groundPrefab;
    private float groundSize = 50f;
    private Spline spline;

 
    [ContextMenu("Generate")]
    public void Generate() {

        spline = splineContainer.Spline;
        float length = spline.GetLength();
        Debug.Log(length);
        int count = Mathf.FloorToInt(length / groundSize);
        Debug.Log(count);

        for (int i = 0; i < count; i++) {
            float t = i / (float)count;
            spline.Evaluate(t, out var pos, out var tangent, out var up);

            float zJitter = Random.Range(-0.1f, 0.1f);
            pos += up * zJitter;

            var tile = Instantiate(groundPrefab, pos, Quaternion.identity, transform);
            tile.transform.rotation = Quaternion.LookRotation(tangent, up);
        }
    }

    [ContextMenu("Clear Ground")]
    public void Clear() {
        // destroy all children so regeneration starts fresh
        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }
    }
}

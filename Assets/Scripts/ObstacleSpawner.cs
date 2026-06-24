using UnityEngine;
using UnityEngine.Splines;

public class ObstacleSpawner : MonoBehaviour {
    [SerializeField] private GameObject[] branchPrefabs;
    [SerializeField] private GameObject[] treePrefabs;
    [SerializeField] private GameObject[] rockPrefabs;

    [SerializeField] private SplineContainer spline;
    private Bounds bounds;

    private void Start() {
        bounds = this.GetComponent<BoxCollider>().bounds;

        float xDivisions = bounds.extents.x / 6f;
        float yDivisions = bounds.extents.y / 6f;

        float[] xPositions = { xDivisions * 2, xDivisions * 4, xDivisions * 6 };

        float[] yPositions = { yDivisions * 2, yDivisions * 4, yDivisions * 6 };

        for (int i = 0; i < xPositions.Length; i++) {
            for (int j = 0; j < yPositions.Length; j++) {
                GameObject spawn = Instantiate(branchPrefabs[0], this.transform.position, Quaternion.identity, this.transform);
                spawn.transform.localPosition = new Vector3(xPositions[i], yPositions[j],0f);

            }
        }

    }
}
using UnityEngine;
using UnityEngine.Splines;

public class ObstacleSpawner : MonoBehaviour {
    [SerializeField] private GameObject[] branchPrefabs;
    [SerializeField] private GameObject[] pickupPrefabs;
    [SerializeField] private GameObject[] rockPrefabs;

    [SerializeField] private SplineContainer splineContainer;

    [SerializeField] private float spawnCutfOff = 
        .2f;
    private Bounds bounds;

    [SerializeField] private int seed = 120;
    [SerializeField] private float spacing = 10f;

    //private void Start() {
    //    bounds = this.GetComponent<BoxCollider>().bounds;

    //    float xDivisions = bounds.extents.x / 6f;
    //    float yDivisions = bounds.extents.y / 6f;

    //    float[] xPositions = { xDivisions * 2, xDivisions * 4, xDivisions * 6 };

    //    float[] yPositions = { yDivisions * 2, yDivisions * 4, yDivisions * 6 };

    //    for (int i = 0; i < xPositions.Length; i++) {
    //        for (int j = 0; j < yPositions.Length; j++) {
    //            GameObject spawn = Instantiate(branchPrefabs[0], this.transform.position, Quaternion.identity, this.transform);
    //            spawn.transform.localPosition = new Vector3(xPositions[i], yPositions[j],0f);

    //        }
    //    }

    //}

    [ContextMenu("GenerateObstacles")]
    public void Generate() {
        Random.InitState(seed);

        Spline spline = splineContainer.Spline;
        float length = spline.GetLength();
        int count = Mathf.FloorToInt(length / spacing);
        GameObject prefabToSpawn = null;
        Debug.Log(length);

        for (int i = 0; i < count; i++) {
            float randNum = Random.Range(0f, 1f);

            if (randNum < spawnCutfOff) {
                prefabToSpawn = pickupPrefabs[Random.Range(0, pickupPrefabs.Length)] ;
            }else if (spawnCutfOff <= randNum && randNum <= .9f) {
                if (Random.Range(0, 2) == 0) {
                    prefabToSpawn = branchPrefabs[Random.Range(0, branchPrefabs.Length)];
                }
                else {
                    prefabToSpawn = rockPrefabs[Random.Range(0, rockPrefabs.Length)];
                }
            }
            else {
                continue;
            }


                float t = i / (float)count;
            spline.Evaluate(t, out var pos, out var tangent, out var up);


            float zJitter = Random.Range(-0.1f, 0.1f);
            //pos += up * zJitter;

            // pos is in spline local space — convert to spawner's local space
            Vector3 localPos = splineContainer.transform.TransformPoint(pos);
            localPos = transform.InverseTransformPoint(localPos); // make it relative to spawner

            var spawnedPrefab = Instantiate(prefabToSpawn, Vector3.zero, Quaternion.identity, transform);
            spawnedPrefab.transform.localPosition = localPos; // set local, not world
        }


    }

    [ContextMenu("Clear Obstacles")]
    public void Clear() {
        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }
    }

}
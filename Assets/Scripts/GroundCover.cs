using UnityEngine;

public class GroundCover : MonoBehaviour {
    private enum GROUND_COVER { Leaf, Dry, Viola };
    [SerializeField] private GameObject leafPrefab;
    [SerializeField] private GameObject dryLeafPrefab;
    [SerializeField] private GameObject violaPrefab;


    [SerializeField] private GameObject[] treePrefabs;
    private Vector3 size;
    private int objectCountMin = 0;
    private int objectCountMax = 10;


    private int treeCountMin = 5;
    private int treeCountMax = 10;
    private float treeEdgeOffset = 2f;
    private float treeXJitter = 10f;

    private int seed = 12315;
    private Bounds bounds;

    [ContextMenu("GenerateGroundCoverTrees")]
    public void GenerateGroundCoverTrees() {
        seed += transform.GetSiblingIndex();
        Random.InitState(seed);
        Renderer renderer = GetComponent<Renderer>();
        bounds = renderer.bounds;

        size = transform.localScale.x * new Vector3(5, 0, 5);
        int objectCount = Random.Range(objectCountMin, objectCountMax);


        for (int i = 1; i < objectCount; i++) {
            int objectType = Random.Range(1, 4);
            GameObject prefabToSpawn = null;
            switch (objectType) {
                case 1:
                    prefabToSpawn = leafPrefab;
                    break;
                case 2:
                    prefabToSpawn = dryLeafPrefab;
                    break;
                case 3:
                    prefabToSpawn = violaPrefab;
                    break;
            }

            float xPos = Random.Range(-bounds.extents.x, bounds.extents.x);
            float zPos = Random.Range(-bounds.extents.z, bounds.extents.z);

            Vector3 worldPos = new Vector3(bounds.center.x + xPos, bounds.max.y + 0.01f, bounds.center.z + zPos);

            
            GameObject spawnedPrefab = Instantiate(prefabToSpawn, worldPos, transform.rotation, transform);
            spawnedPrefab.transform.localRotation =  Quaternion.Euler( Vector3.up * Random.Range(10f, 360f));

        }

        int treeCount = Random.Range(treeCountMin, treeCountMax);

            for (int i = 0; i < treeCount; i++) {
                GameObject treePrefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
                bool spawnLeft = Random.value < 0.5f;

                float edgeX = spawnLeft
                    ? bounds.min.x - treeEdgeOffset
                    : bounds.max.x + treeEdgeOffset;

                edgeX += Random.Range(-treeXJitter, treeXJitter);

                float zPos = Random.Range(bounds.min.z, bounds.max.z);

                Vector3 worldPos = new Vector3(edgeX, bounds.max.y, zPos);
                GameObject tree = Instantiate(treePrefab, worldPos, transform.rotation, transform);

            tree.transform.localRotation = Quaternion.Euler(Vector3.up * Random.Range(10f, 360f));
        }
        }

    [ContextMenu("Clear Ground Cover Trees")]
    public void Clear() {
        // destroy all children so regeneration starts fresh
        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }
    }



}

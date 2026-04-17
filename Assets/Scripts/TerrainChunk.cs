using System.Collections.Generic;
using UnityEngine;

// Plain C# data class — not a MonoBehaviour.
// Owns the mesh GameObjects for one 25x25 region of the grid.
public class TerrainChunk
{
    public readonly int OriginX;
    public readonly int OriginZ;
    public readonly Transform Parent;

    public bool IsDirty { get; private set; } = true;

    private readonly List<GameObject> meshObjects = new List<GameObject>();
    private readonly MeshCollider meshCollider;

    public TerrainChunk(int originX, int originZ, Transform parent)
    {
        OriginX = originX;
        OriginZ = originZ;
        Parent  = parent;

        // Invisible collider child used by the brush raycast
        var colliderGo = new GameObject("_Collider");
        colliderGo.transform.SetParent(parent);
        colliderGo.transform.localPosition = Vector3.zero;
        meshCollider = colliderGo.AddComponent<MeshCollider>();
    }

    // Replaces the collider mesh after each chunk rebuild.
    public void UpdateCollider(Mesh mesh) => meshCollider.sharedMesh = mesh;

    public void MarkDirty() => IsDirty = true;
    public void MarkClean() => IsDirty = false;

    public void AddMeshObject(GameObject go) => meshObjects.Add(go);

    public void ClearMeshObjects()
    {
        foreach (var go in meshObjects)
            if (go != null) Object.DestroyImmediate(go);
        meshObjects.Clear();
    }
}

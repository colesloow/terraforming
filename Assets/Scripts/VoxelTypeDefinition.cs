using UnityEngine;

// By convention, ID 0 is always AIR (unset voxel). Do not assign ID 0 to any real block.
[CreateAssetMenu(menuName = "Terrain/Voxel Type")]
public class VoxelTypeDefinition : ScriptableObject
{
    public byte id;
    public string displayName;

    // Solid blocks block adjacent faces from being emitted into the mesh.
    // Non-solid blocks (water, air) allow neighbors to show their faces through them.
    public bool isSolid;

}

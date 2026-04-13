using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(menuName = "Terrain/Mesh Layer")]
public class VoxelMeshLayer : ScriptableObject
{
    public enum FaceMode
    {
        // Only the top (+Y) face, when directly exposed to air.
        // Pair with Body on the same voxel type to split top/sides into two materials (e.g. grass green top + dirt body).
        TopSurfaceOnly,

        // All exposed faces EXCEPT the top face toward air.
        // Use alongside TopSurfaceOnly when the top surface has a different material than the sides and bottom (e.g. dirt body under a grass top).
        Body,

        // Only faces directly adjacent to air. Faces touching any solid or non-air block are hidden.
        // Use for transparent blocks where only the outer shell should be visible (e.g. water).
        ExposedToAir,

        // All exposed faces using a single material — top included.
        // Use when a block looks the same on every side and does not need a separate surface layer (e.g. sand, stone).
        AllExposed,
    }

    [SerializeField] private VoxelTypeDefinition targetType;
    [SerializeField] private Material material;
    [SerializeField] private string objectName;
    [Tooltip("Scales the Y dimension of all quads in this layer (1 = full block height, 0.8 = 80% height). Use for water to visually lower the surface below the block top.")]
    [SerializeField][Range(0f, 1f)] private float heightScale = 1f;
    [Tooltip(
        "TopSurfaceOnly — top face only, exposed to air. For blocks with a different texture on top (e.g. grass green surface). Always pair with a Body layer on the same voxel type.\n\n" +
        "Body — sides and bottom only (top-to-air is excluded). For the body of a block whose top is handled by a TopSurfaceOnly layer (e.g. dirt under grass).\n\n" +
        "ExposedToAir — only faces touching air. For transparent blocks: hides faces buried in terrain, keeps only the visible outer shell (e.g. water).\n\n" +
        "AllExposed — all exposed faces, same material on every side. For blocks that look identical on all faces and need no separate top layer (e.g. sand, stone)."
    )]
    [SerializeField] private FaceMode faceMode;

    public byte TargetTypeId => targetType != null ? targetType.id : (byte)0;
    public Material Material  => material;
    public string ObjectName  => objectName;
    public float HeightScale  => heightScale;

    private const int  FACE_TOP = 2; // index of +Y face in TerrainGenerator.FaceVertices
    private const byte AIR_ID   = 0; // unset voxel / out-of-bounds

    public bool ShouldEmitFace(int faceIndex, byte neighbor, Dictionary<byte, VoxelTypeDefinition> registry)
    {
        switch (faceMode)
        {
            case FaceMode.TopSurfaceOnly:
                return faceIndex == FACE_TOP && neighbor == AIR_ID;

            case FaceMode.Body:
                // solid neighbor always hides the face
                if (registry.TryGetValue(neighbor, out var bn) && bn.isSolid) return false;
                // top face exposed to air belongs to the surface layer
                if (faceIndex == FACE_TOP && neighbor == AIR_ID) return false;
                return true;

            case FaceMode.ExposedToAir:
                return neighbor == AIR_ID;

            case FaceMode.AllExposed:
                // hide faces touching the same block type (e.g. two sand blocks)
                if (neighbor == TargetTypeId) return false;
                // hide faces touching solid blocks
                if (registry.TryGetValue(neighbor, out var en) && en.isSolid) return false;
                return true;

            default:
                return false;
        }
    }
}

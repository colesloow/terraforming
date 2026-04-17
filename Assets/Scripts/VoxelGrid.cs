// Pure data container for the 3D voxel grid.
// No game logic here — voxel type assignment lives in TerrainGenerator.
public class VoxelGrid
{
    public readonly int SizeX;
    public readonly int SizeZ;
    public readonly int MaxHeight;

    private const byte AIR = 0;
    private readonly byte[,,] voxels;

    public VoxelGrid(int sizeX, int maxHeight, int sizeZ)
    {
        SizeX = sizeX;
        SizeZ = sizeZ;
        MaxHeight = maxHeight;
        voxels = new byte[sizeX, maxHeight + 1, sizeZ];
    }

    public byte Get(int x, int y, int z) => voxels[x, y, z];

    public void Set(int x, int y, int z, byte id) => voxels[x, y, z] = id;

    public bool InBounds(int x, int y, int z) =>
        x >= 0 && x < SizeX  &&
        y >= 0 && y <= MaxHeight &&
        z >= 0 && z < SizeZ;

    // Returns the Y of the topmost non-air voxel in this column, or -1 if empty.
    public int GetSurfaceHeight(int x, int z)
    {
        for (int y = MaxHeight; y >= 0; y--)
            if (voxels[x, y, z] != AIR) return y;
        return -1;
    }

    public void ClearColumn(int x, int z)
    {
        for (int y = 0; y <= MaxHeight; y++)
            voxels[x, y, z] = AIR;
    }

    // Returns the number of solid blocks in the column (excluding AIR and skipId).
    // This matches the terrainHeight parameter expected by ApplyColumn.
    public int GetTerrainHeight(int x, int z, byte skipId)
    {
        for (int y = MaxHeight; y >= 0; y--)
            if (voxels[x, y, z] != AIR && voxels[x, y, z] != skipId)
                return y + 1;
        return 1;
    }
}

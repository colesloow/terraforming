using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;

[ExecuteAlways]
public class TerrainGenerator : MonoBehaviour
{
    [Header("Grid Settings")]
    [SerializeField] private int gridSizeX = 64;
    [SerializeField] private int gridSizeZ = 64;
    [SerializeField] private int maxHeight = 20;
    [SerializeField] private float cellSize = 1f;

    [Header("Voxel Types")]
    [SerializeField] private VoxelTypeDefinition grassType;
    [SerializeField] private VoxelTypeDefinition waterType;
    [SerializeField] private VoxelTypeDefinition sandType;
    [SerializeField] private VoxelTypeDefinition stoneType;
    // Each layer defines one mesh (material + face emission rule) for one voxel type.
    // Add a layer SO here to register it; no code changes needed for new terrain types.
    [Header("Mesh Layers")]
    [SerializeField] private VoxelMeshLayer[] meshLayers;

    [Header("Water Settings")]
    [SerializeField][Range(0f, 1f)] private float waterLevel = 0.3f;
    [SerializeField] private int sandRange = 2;

    [Header("Stone Settings")]
    [Tooltip("Number of blocks above the waterline/sand that are stone. Grass appears above this band. 0 = no stone, higher = stone reaches further up the mountain.")]
    [SerializeField] private int stoneDepth = 6;

    [Header("Snow Settings")]
    [Tooltip("Terrain height fraction above which the top block becomes snow (0 = everywhere, 1 = never).")]
    [SerializeField][Range(0f, 1f)] private float snowAltitude = 0.75f;

    // Surface overlays are not voxel types — they are top faces emitted on top of any solid block
    // based on column altitude, independent of what block is underneath.
    [Header("Surface Materials")]
    [SerializeField] private Material grassTopMaterial;
    [SerializeField] private Material snowMaterial;

    public enum NOISE_TYPE { CELLULAR, PERLIN, SIMPLEX }

    [Header("Detail Noise")]
    [SerializeField] private NOISE_TYPE noiseType = NOISE_TYPE.PERLIN;
    [SerializeField] private float2 noiseOffset;
    [SerializeField] private float noiseScale = 20f;
    [SerializeField] private float resultPow = 1f;

    [Header("Continental Noise")]
    [SerializeField] private NOISE_TYPE continentalNoiseType = NOISE_TYPE.PERLIN;
    [SerializeField] private float2 continentalOffset;
    [SerializeField] private float continentalScale = 80f;
    [SerializeField] private float continentalPow = 1f;
    [Tooltip("S-curve contrast on the continental map. Values below 0.5 become flatter, above 0.5 become taller. 1 = no effect, 3 = strong contrast.")]
    [SerializeField][Range(1f, 6f)] private float continentalContrast = 1f;

    // AIR is always ID 0 — the default value of an unset voxel
    private const byte AIR = 0;

    private int seed;

    // flat 3D array storing the voxel type at each grid position
    private byte[,,] voxels;

    // FaceVertices defines the 4 corners of each face on a unit cube [0,1].
    // Vertices are in CCW order when viewed from outside, so Unity's back-face
    // culling correctly hides interior faces without needing a two-sided shader.
    private static readonly Vector3[][] FaceVertices =
    {
        // +X
        new[] { new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(1,0,1) },
        // -X
        new[] { new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0), new Vector3(0,0,0) },
        // +Y
        new[] { new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,1,0) },
        // -Y
        new[] { new Vector3(0,0,1), new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1) },
        // +Z
        new[] { new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1), new Vector3(0,0,1) },
        // -Z
        new[] { new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,0,0) },
    };

    // one entry per face, matched by index to FaceVertices
    private static readonly Vector3Int[] FaceDirections =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.up,
        Vector3Int.down,
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 0, -1),
    };

    private void OnValidate()
    {
        // delayCall avoids Unity errors when OnValidate fires mid-frame during inspector edits
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this != null) Generate();
        };
    }

    [ContextMenu("GenerateSeed()")]
    public void GenerateSeed()
    {
        seed = UnityEngine.Random.Range(0, 99999);
        UnityEngine.Random.InitState(seed);

        noiseScale = UnityEngine.Random.Range(10f, 40f);
        noiseOffset = new float2(
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f)
        );

        continentalScale = UnityEngine.Random.Range(60f, 200f);
        continentalOffset = new float2(
            UnityEngine.Random.Range(-1000f, 1000f),
            UnityEngine.Random.Range(-1000f, 1000f)
        );

        Generate();
    }

    private void Generate()
    {
        ClearMeshes();
        if (grassType == null || waterType == null || sandType == null || stoneType == null) return;
        if (meshLayers == null || meshLayers.Length == 0) return;

        BuildVoxels();

        // build a lookup table so each layer can query neighbor properties by ID
        var registry = new Dictionary<byte, VoxelTypeDefinition>();
        foreach (var type in new[] { grassType, waterType, sandType, stoneType })
            if (type != null) registry[type.id] = type;

        // one mesh object per layer — no code change needed when adding new terrain types
        foreach (var layer in meshLayers)
            if (layer != null) BuildMeshForLayer(layer, registry);

        BuildSurfaceMesh();
    }

    private void BuildVoxels()
    {
        voxels = new byte[gridSizeX, maxHeight + 1, gridSizeZ];

        // seed shifts the noise sampling origin so each seed produces a unique world
        float2 seedOffset = new float2(seed, seed * 0.5f);
        int waterHeightInt = Mathf.RoundToInt(waterLevel * maxHeight);

        // Pre-compute continental values so we can normalize their distribution.
        // Without this, the noise can land in a region that's mostly high or mostly low
        // depending on offset/seed, making the terrain uniformly mountainous or flat.
        var continentalRaw = new float[gridSizeX, gridSizeZ];
        float continentalSum = 0f;
        for (int z = 0; z < gridSizeZ; z++)
        for (int x = 0; x < gridSizeX; x++)
        {
            float2 pos = new float2(
                x * cellSize / continentalScale + continentalOffset.x + seedOffset.x,
                z * cellSize / continentalScale + continentalOffset.y + seedOffset.y
            );
            float v = math.pow(SampleNoise(pos, continentalNoiseType), continentalPow);
            continentalRaw[x, z] = v;
            continentalSum += v;
        }
        // shift all values so the mean lands at 0.5, ensuring ~equal mountain/plain coverage
        float continentalShift = 0.5f - (continentalSum / (gridSizeX * gridSizeZ));

        for (int z = 0; z < gridSizeZ; z++)
        {
            for (int x = 0; x < gridSizeX; x++)
            {
                // dividing by scale instead of normalizing by grid size ensures
                // the noise pattern doesn't compress when the grid grows (it extends)
                float2 detailPos = new float2(
                    x * cellSize / noiseScale + noiseOffset.x + seedOffset.x,
                    z * cellSize / noiseScale + noiseOffset.y + seedOffset.y
                );

                float detail = math.pow(SampleNoise(detailPos, noiseType), resultPow);
                float continental = Mathf.Clamp01(continentalRaw[x, z] + continentalShift);
                // S-curve contrast: values below 0.5 are pushed toward 0, above 0.5 toward 1
                continental = continental < 0.5f
                    ? 0.5f * Mathf.Pow(2f * continental, continentalContrast)
                    : 1f - 0.5f * Mathf.Pow(2f * (1f - continental), continentalContrast);

                // multiplying both noises creates a continental mask: detail noise can only
                // produce mountains where the continental noise is high, leaving flat plains elsewhere
                int terrainHeight = Mathf.Max(1, Mathf.RoundToInt(detail * continental * maxHeight));

                int sandMaxY  = Mathf.Min(waterHeightInt + sandRange, maxHeight);
                // stone rises stoneDepth blocks above the sand/waterline; grass sits on top
                int stoneMaxY = sandMaxY + stoneDepth;

                for (int y = 0; y < terrainHeight && y <= maxHeight; y++)
                {
                    if (y <= sandMaxY)
                        voxels[x, y, z] = sandType.id;   // sand near the waterline
                    else if (y <= stoneMaxY)
                        voxels[x, y, z] = stoneType.id;  // stone band above sand
                    else
                        voxels[x, y, z] = grassType.id;  // grass near the surface
                }

                // water fills the gap between terrain and the fixed water level,
                // so low-lying areas become lakes rather than empty air
                if (terrainHeight <= waterHeightInt)
                {
                    for (int y = terrainHeight; y <= waterHeightInt && y <= maxHeight; y++)
                        voxels[x, y, z] = waterType.id;
                }
            }
        }
    }

    private void BuildSurfaceMesh()
    {
        var grassBuf = new MeshBuffers();
        var snowBuf = new MeshBuffers();
        int snowThresholdInt = Mathf.RoundToInt(snowAltitude * maxHeight);
        const int FACE_TOP = 2;

        for (int z = 0; z < gridSizeZ; z++)
        for (int x = 0; x < gridSizeX; x++)
        {
            // scan downward to find the highest solid, non-water block
            for (int y = maxHeight; y >= 0; y--)
            {
                byte v = voxels[x, y, z];
                if (v == AIR || v == waterType.id) continue;

                // only emit top face if nothing is above it
                int above = y + 1;
                if (IsInBounds(x, above, z) && voxels[x, above, z] != AIR) break;

                // sand is AllExposed and already renders its own top face — skip overlay
                if (v != sandType.id)
                {
                    if (y >= snowThresholdInt)
                        snowBuf.AddQuad(x, y, z, FACE_TOP, cellSize);
                    else
                        grassBuf.AddQuad(x, y, z, FACE_TOP, cellSize);
                }

                break;
            }
        }

        if (grassTopMaterial != null) CreateMeshObject(grassBuf, grassTopMaterial, "_GrassTopMesh");
        if (snowMaterial != null) CreateMeshObject(snowBuf, snowMaterial, "_SnowMesh");
    }

    private void BuildMeshForLayer(VoxelMeshLayer layer, Dictionary<byte, VoxelTypeDefinition> registry)
    {
        var buf = new MeshBuffers();
        byte targetId = layer.TargetTypeId;

        for (int y = 0; y <= maxHeight; y++)
        for (int z = 0; z < gridSizeZ; z++)
        for (int x = 0; x < gridSizeX; x++)
        {
            if (voxels[x, y, z] != targetId) continue;

            // heightScale applies only to the surface block (top exposed to air); body blocks stay full height
            byte blockAbove = IsInBounds(x, y + 1, z) ? voxels[x, y + 1, z] : AIR;
            float hs = (layer.HeightScale < 1f && blockAbove == AIR) ? layer.HeightScale : 1f;

            for (int f = 0; f < 6; f++)
            {
                Vector3Int dir = FaceDirections[f];
                int nx = x + dir.x, ny = y + dir.y, nz = z + dir.z;
                byte neighbor = IsInBounds(nx, ny, nz) ? voxels[nx, ny, nz] : AIR;

                if (layer.ShouldEmitFace(f, neighbor, registry))
                    buf.AddQuad(x, y, z, f, cellSize, hs);
            }
        }

        CreateMeshObject(buf, layer.Material, layer.ObjectName);
    }

    private void CreateMeshObject(MeshBuffers buf, Material material, string objectName)
    {
        // nothing to render — skip rather than uploading a null/empty mesh
        if (buf.vertices == null || buf.vertices.Count == 0) return;

        var mesh = new Mesh();

        // UInt32 index format supports up to ~4 billion vertices;
        // the default UInt16 overflows above 65k which large voxel terrains easily exceed
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(buf.vertices);
        mesh.SetTriangles(buf.triangles, 0);
        mesh.SetUVs(0, buf.uvs);
        mesh.RecalculateNormals();
        // Use the full terrain footprint as bounds instead of the tight per-mesh AABB.
        // Sparse meshes (e.g. snow on a few peaks) would otherwise get a tiny bounding box
        // that Unity incorrectly frustum-culls when the camera moves.
        mesh.bounds = new Bounds(
            new Vector3(gridSizeX * cellSize * 0.5f, maxHeight * cellSize * 0.5f, gridSizeZ * cellSize * 0.5f),
            new Vector3(gridSizeX * cellSize, (maxHeight + 1) * cellSize, gridSizeZ * cellSize)
        );

        var go = new GameObject(objectName);
        go.transform.SetParent(transform);
        go.transform.localPosition = Vector3.zero;
        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private bool IsInBounds(int x, int y, int z)
    {
        return x >= 0 && x < gridSizeX &&
               y >= 0 && y <= maxHeight &&
               z >= 0 && z < gridSizeZ;
    }

    // Perlin and Simplex return values in [-1, 1], so we remap to [0, 1]
    // to keep noise values consistent regardless of which type is chosen
    private float SampleNoise(float2 pos, NOISE_TYPE type)
    {
        switch (type)
        {
            case NOISE_TYPE.SIMPLEX:  return math.remap(-1f, 1f, 0f, 1f, noise.snoise(pos));
            case NOISE_TYPE.PERLIN:   return math.remap(-1f, 1f, 0f, 1f, noise.cnoise(pos));
            case NOISE_TYPE.CELLULAR: return noise.cellular(pos).x;
            default: return 0f;
        }
    }

    private void ClearMeshes()
    {
        // iterate backwards to safely destroy while modifying the child list
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    // helper struct to accumulate mesh data before uploading to the GPU
    private struct MeshBuffers
    {
        public List<Vector3> vertices;
        public List<int> triangles;
        public List<Vector2> uvs;

        public MeshBuffers(bool _ = false)
        {
            vertices = new List<Vector3>();
            triangles = new List<int>();
            uvs = new List<Vector2>();
        }

        public void AddQuad(int x, int y, int z, int faceIndex, float cellSize, float heightScale = 1f)
        {
            // initialize lists on first use
            if (vertices == null)
            {
                vertices = new List<Vector3>();
                triangles = new List<int>();
                uvs = new List<Vector2>();
            }

            // Vertices
            int vertBase = vertices.Count;
            Vector3 origin = new Vector3(x, y, z) * cellSize;

            foreach (Vector3 v in FaceVertices[faceIndex])
                vertices.Add(origin + new Vector3(v.x * cellSize, v.y * cellSize * heightScale, v.z * cellSize));

            // Triangles: two triangles per quad (0-1-2, 0-2-3)
            triangles.Add(vertBase); triangles.Add(vertBase + 1); triangles.Add(vertBase + 2);
            triangles.Add(vertBase); triangles.Add(vertBase + 2); triangles.Add(vertBase + 3);

            // UVs
            uvs.Add(new Vector2(0, 0));
            uvs.Add(new Vector2(0, 1));
            uvs.Add(new Vector2(1, 1));
            uvs.Add(new Vector2(1, 0));
        }
    }
}

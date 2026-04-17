using UnityEngine;
using UnityEngine.InputSystem;

public class TerrainBrush : MonoBehaviour
{
    public enum BrushMode { Raise, Lower, Smooth, Flatten }

    [Header("References")]
    [SerializeField] private TerrainGenerator terrain;
    [SerializeField] private Camera brushCamera;

    [Header("Brush Settings")]
    [SerializeField] private BrushMode mode = BrushMode.Raise;
    [SerializeField][Range(1, 15)] private int radius = 5;
    [SerializeField][Range(1, 10)] private int strength = 3;
    [Tooltip("Controls how the brush tapers toward its edge. Low = very gradual falloff, high = flat plateau with sharp edge.")]
    [SerializeField][Range(0.5f, 4f)] private float falloffPower = 1f;

    // Flatten: target height captured once on the first frame of the click
    private int flattenTargetHeight;

    // Per-column float accumulator for Raise/Lower: fractional block changes are
    // stored here and only applied when they cross a whole block boundary.
    // This prevents the noisy alternating-column artifact from RoundToInt on small deltas.
    private float[,] heightAccum;
    private bool wasMouseHeld;

    private LineRenderer brushCircle;

    private void Awake()
    {
        brushCircle = gameObject.AddComponent<LineRenderer>();
        brushCircle.loop = true;
        brushCircle.positionCount = 48;
        brushCircle.useWorldSpace = true;
        brushCircle.widthMultiplier = 0.08f;
        brushCircle.material = new Material(Shader.Find("Sprites/Default"));
        brushCircle.startColor = Color.white;
        brushCircle.endColor = Color.white;
        brushCircle.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        brushCircle.receiveShadows = false;
        brushCircle.enabled = false;
    }

    private void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null) { brushCircle.enabled = false; return; }

        Ray ray = brushCamera.ScreenPointToRay(mouse.position.ReadValue());
        bool hitTerrain = Physics.Raycast(ray, out RaycastHit hit);

        brushCircle.enabled = hitTerrain;
        if (!hitTerrain) return;

        UpdateBrushCircle(hit.point);

        Vector3 local = terrain.transform.InverseTransformPoint(hit.point);
        int hitX = Mathf.FloorToInt(local.x / terrain.CellSize);
        int hitZ = Mathf.FloorToInt(local.z / terrain.CellSize);

        bool mouseDown = mouse.leftButton.wasPressedThisFrame;
        bool mouseHeld = mouse.leftButton.isPressed;

        // Clear accumulator when the user releases the mouse so each stroke starts fresh.
        if (!mouseHeld && wasMouseHeld) heightAccum = null;
        wasMouseHeld = mouseHeld;

        if (!mouseDown && !mouseHeld) return;

        if (mouseDown && mode == BrushMode.Flatten)
            flattenTargetHeight = terrain.Grid.GetTerrainHeight(hitX, hitZ, terrain.WaterTypeId);

        ApplyBrush(hitX, hitZ);
    }

    private void UpdateBrushCircle(Vector3 center)
    {
        float worldRadius = radius * terrain.CellSize;
        // More segments for larger radii so the circle stays smooth at any size
        int segments = Mathf.Max(48, radius * 8);
        if (brushCircle.positionCount != segments)
            brushCircle.positionCount = segments;

        for (int i = 0; i < segments; i++)
        {
            float angle = (float)i / segments * Mathf.PI * 2f;
            brushCircle.SetPosition(i, new Vector3(
                center.x + Mathf.Cos(angle) * worldRadius,
                center.y + 0.05f,
                center.z + Mathf.Sin(angle) * worldRadius
            ));
        }
    }

    private void ApplyBrush(int centerX, int centerZ)
    {
        // Smooth needs a per-frame snapshot of the area so that the order columns
        // are processed doesn't affect the result (no directional smear).
        int[,] snap = mode == BrushMode.Smooth ? SnapshotArea(centerX, centerZ) : null;

        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int x = centerX + dx;
            int z = centerZ + dz;
            if (!terrain.Grid.InBounds(x, 0, z)) continue;

            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > radius) continue;

            // falloffPower shapes the brush profile: 0.5 = very gradual, 1 = linear, 4 = flat plateau
            float falloff = Mathf.Pow(1f - dist / radius, falloffPower);

            int current = terrain.Grid.GetTerrainHeight(x, z, terrain.WaterTypeId);
            int newHeight = ComputeNewHeight(x, z, dx, dz, current, falloff, snap);
            newHeight = Mathf.Clamp(newHeight, 1, terrain.MaxHeight);

            if (newHeight == current) continue;

            terrain.ApplyColumn(x, z, newHeight);
            terrain.MarkDirty(x, z);
        }
    }

    // Snapshots heights in a (radius+1) margin around the brush center.
    // The +1 margin lets the 3×3 neighbor lookup work at the brush edge.
    private int[,] SnapshotArea(int cx, int cz)
    {
        int margin = radius + 1;
        int size = 2 * margin + 1;
        var snap = new int[size, size];
        for (int dz = -margin; dz <= margin; dz++)
        for (int dx = -margin; dx <= margin; dx++)
        {
            int x = cx + dx, z = cz + dz;
            snap[dx + margin, dz + margin] = terrain.Grid.InBounds(x, 0, z)
                ? terrain.Grid.GetTerrainHeight(x, z, terrain.WaterTypeId)
                : 1;
        }
        return snap;
    }

    private int ComputeNewHeight(int x, int z, int dx, int dz, int current, float falloff, int[,] snap)
    {
        // strength (1-10) scales blocks-per-second; Time.deltaTime makes it framerate-independent.
        float rate = strength * 8f * Time.deltaTime;

        switch (mode)
        {
            case BrushMode.Raise:
            case BrushMode.Lower:
            {
                // Accumulate fractional block changes per column so that even the tapered
                // edges of the brush rise/fall smoothly instead of flickering between 0 and 1.
                heightAccum ??= new float[TerrainConstants.GRID_SIZE, TerrainConstants.GRID_SIZE];

                float sign = mode == BrushMode.Raise ? 1f : -1f;
                heightAccum[x, z] += sign * rate * falloff;
                int delta = (int)heightAccum[x, z]; // truncates toward zero
                heightAccum[x, z] -= delta;
                return current + delta;
            }

            case BrushMode.Smooth:
            {
                // Average 3×3 neighborhood from the per-frame snapshot
                int margin = radius + 1;
                float sum = 0f; int count = 0;
                for (int nz = -1; nz <= 1; nz++)
                for (int nx = -1; nx <= 1; nx++)
                {
                    int si = dx + nx + margin, sj = dz + nz + margin;
                    if (si >= 0 && si < snap.GetLength(0) && sj >= 0 && sj < snap.GetLength(1))
                    { sum += snap[si, sj]; count++; }
                }
                float avg = count > 0 ? sum / count : current;

                // Same accumulator mechanism as Raise/Lower: sub-block differences accumulate
                // until they cross an integer boundary, so even gentle smoothing is visible.
                heightAccum ??= new float[TerrainConstants.GRID_SIZE, TerrainConstants.GRID_SIZE];
                heightAccum[x, z] += (avg - current) * falloff * strength * Time.deltaTime * 3f;
                int delta = (int)heightAccum[x, z];
                heightAccum[x, z] -= delta;
                return current + delta;
            }

            case BrushMode.Flatten:
            {
                float lerpT = Mathf.Clamp01(falloff * strength * Time.deltaTime * 5f);
                return Mathf.RoundToInt(Mathf.Lerp(current, flattenTargetHeight, lerpT));
            }

            default:
                return current;
        }
    }
}

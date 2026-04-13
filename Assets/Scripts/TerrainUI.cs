using UnityEngine;
using UnityEngine.UI;

public class TerrainUI : MonoBehaviour
{
    [SerializeField] private TerrainGenerator terrainGenerator;
    [SerializeField] private Button generateSeedButton;

    private void Awake()
    {
        generateSeedButton.onClick.AddListener(terrainGenerator.GenerateSeed);
    }

    private void OnDestroy()
    {
        generateSeedButton.onClick.RemoveListener(terrainGenerator.GenerateSeed);
    }
}

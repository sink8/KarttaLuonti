using System.Globalization;
using UnityEngine;

public class LandColor : MonoBehaviour
{

    public Terrain terrain;
    public TextAsset landcoverAsc;   // the .asc renamed to .txt

    public bool flipX = false;
    public bool flipY = true;
    public bool transpose = false;

    static readonly Color[] PaletteColors = new Color[]
    {
        new Color(0.78f, 0.90f, 0.71f), // 0 dense forest
        new Color(0.98f, 0.98f, 0.98f), // 1 open forest (white)
        new Color(0.78f, 0.94f, 0.55f), // 2 scrub/clearcut
        new Color(0.96f, 0.88f, 0.47f), // 3 open/field
        new Color(0.78f, 0.88f, 0.90f), // 4 wetland
        new Color(0.47f, 0.74f, 0.90f), // 5 water
    };

    static int ClassToPalette(int v) {
        if (v >= 25 && v <= 29) return 0;
        if (v == 23 || v == 24) return 1;
        if (v >= 33 && v <= 37) return 2;
        if (v == 21 || v == 22) return 3;
        if (v >= 38 && v <= 46) return 4;
        if (v >= 47 && v <= 49) return 5;
        return 1; // default + NoData -> white
    }

    // Parse the ASCII grid into a 2D int array.
    int[,] ParseAsc(out int cols, out int rows) {
        cols = rows = 0;
        var lines = landcoverAsc.text.Split('\n');
        int dataStart = 0;
        // header lines start with letters (ncols, nrows, xllcorner, ...)
        for (int i = 0; i < lines.Length; i++) {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (char.IsLetter(t[0])) {
                var parts = t.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (t.StartsWith("ncols")) cols = int.Parse(parts[1]);
                else if (t.StartsWith("nrows")) rows = int.Parse(parts[1]);
                dataStart = i + 1;
            } else break;
        }

        var grid = new int[rows, cols];
        int r = 0;
        for (int i = dataStart; i < lines.Length && r < rows; i++) {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            var parts = t.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int c = 0; c < cols && c < parts.Length; c++)
                grid[r, c] = (int)float.Parse(parts[c], CultureInfo.InvariantCulture);
            r++;
        }
        return grid;
    }

    public void Paint() {
        TerrainData td = terrain.terrainData;

        var layers = new TerrainLayer[PaletteColors.Length];
        for (int i = 0; i < PaletteColors.Length; i++) {
            var tex = new Texture2D(8, 8);
            var px = new Color[64];
            for (int p = 0; p < 64; p++) px[p] = PaletteColors[i];
            tex.SetPixels(px); tex.Apply();

            layers[i] = new TerrainLayer();
            layers[i].diffuseTexture = tex;
            layers[i].tileSize = new Vector2(td.size.x, td.size.z);
            layers[i].smoothness = 0f;
            layers[i].metallic = 0f;
        }
        td.terrainLayers = layers;

        int gcols, grows;
        int[,] grid = ParseAsc(out grows, out gcols);
        Debug.Log($"Parsed grid {gcols} x {grows}, center value = {grid[grows / 2, gcols / 2]}");

        int res = td.alphamapResolution;
        float[,,] splat = new float[res, res, layers.Length];

        for (int y = 0; y < res; y++) {
            for (int x = 0; x < res; x++) {
                // map terrain cell -> grid cell. Note grid row 0 is the TOP (north).
                int gx = Mathf.Clamp((int)((float)x / res * gcols), 0, gcols - 1);
                int gy = Mathf.Clamp((int)((float)y / res * grows), 0, grows - 1);

                if (transpose) { int t = gx; gx = gy; gy = t; }
                if (flipX) gx = gcols - 1 - gx;
                if (flipY) gy = grows - 1 - gy;

                int classValue = grid[gy, gx];
                int layer = ClassToPalette(classValue);
                for (int l = 0; l < layers.Length; l++)
                    splat[y, x, l] = (l == layer) ? 1f : 0f;
            }
        }
        td.SetAlphamaps(0, 0, splat);
        Debug.Log("Terrain painted from landcover (asc).");
    }

    
    }

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(LandColor))]
public class LandcoverPainterEditor : UnityEditor.Editor {
    public override void OnInspectorGUI() {
        DrawDefaultInspector();                 // shows the Terrain + PNG slots
        var painter = (LandColor)target;
        if (GUILayout.Button("Paint Terrain From Landcover")) {
            painter.SendMessage("Paint");        // calls the Paint() method
        }
    }
}
#endif

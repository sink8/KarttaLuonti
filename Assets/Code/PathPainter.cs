using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public class PathPainter : MonoBehaviour
{
    [Header("Assign these")]

    public Terrain terrain;
    public TextAsset trailGeoJson;      // clipped trails .json.txt

    [Header("Tile bounds (EPSG:3067)")]
    public double tileMinE = 590000;
    public double tileMinN = 7362000;
    public double tileSize = 6000;
    public bool flipX = true;           // toggle if mirrored

    [Header("Method A — splatmap paint")]
    public int trailLayerIndex = 0;     // index of your brown trail TerrainLayer
    public float paintWidth = 4f;       // meters
    public float edgeSoftness = 1.5f;   // cells of fade; bigger = softer

    [Header("Method B — ribbon mesh")]
    public Material trailMaterial;
    public float ribbonWidth = 3f;
    public float ribbonLift = 0.2f;

    // ---------------- shared ----------------

    // Update is called once per frame
    bool ToTerrain(double e, double n, out Vector3 pos) {
        pos = Vector3.zero;
        float u = (float)((e - tileMinE) / tileSize);
        float v = (float)((n - tileMinN) / tileSize);
        if (u < -0.001f || u > 1.001f || v < -0.001f || v > 1.001f) return false;
        u = Mathf.Clamp01(u); v = Mathf.Clamp01(v);
        if (flipX) u = 1f - u;
        Vector3 size = terrain.terrainData.size;
        pos = new Vector3(u * size.x, 0, v * size.z) + terrain.transform.position;
        pos.y = terrain.SampleHeight(pos) + terrain.transform.position.y;
        return true;
    }

    // Parse all coordinate runs (handles LineString + MultiLineString, ignores 3rd value).
    List<List<Vector3>> ParseLines() {
        var result = new List<List<Vector3>>();
        string json = trailGeoJson.text;
        int i = 0;

        while ((i = json.IndexOf("coordinates", i)) != -1) {
            int open = json.IndexOf('[', i);
            int depth = 0, j = open;
            for (; j < json.Length; j++) {
                if (json[j] == '[') depth++;
                else if (json[j] == ']') { depth--; if (depth == 0) break; }
            }
            string block = json.Substring(open, j - open + 1);

            var current = new List<Vector3>();
            int k = 0;
            while (k < block.Length) {
                if (block[k] == '[') {
                    int p = k + 1;
                    while (p < block.Length && block[p] == ' ') p++;
                    if (p < block.Length && (char.IsDigit(block[p]) || block[p] == '-')) {
                        int endPair = block.IndexOf(']', k);
                        string inner = block.Substring(k + 1, endPair - k - 1);
                        var nums = inner.Split(',');
                        if (nums.Length >= 2 &&
                            double.TryParse(nums[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double e) &&
                            double.TryParse(nums[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double n)) {
                            if (ToTerrain(e, n, out Vector3 pt)) current.Add(pt);
                            else { if (current.Count > 1) result.Add(current); current = new List<Vector3>(); }
                        }
                        k = endPair + 1;
                        int q = k; while (q < block.Length && block[q] == ' ') q++;
                        if (q < block.Length && block[q] == ']') {
                            if (current.Count > 1) result.Add(current);
                            current = new List<Vector3>();
                        }
                        continue;
                    }
                }
                k++;
            }
            if (current.Count > 1) result.Add(current);
            i = j;
        }
        return result;
    }

    Vector2 ToCell(Vector3 worldPos, TerrainData td, int res) {
        float u = (worldPos.x - terrain.transform.position.x) / td.size.x;
        float v = (worldPos.z - terrain.transform.position.z) / td.size.z;
        return new Vector2(u * (res - 1), v * (res - 1));
    }

    float DistToSegment(Vector2 p, Vector2 a, Vector2 b) {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + t * ab);
    }

    // ---------------- METHOD A (segment-based, smooth) ----------------
    public void PaintTrails() {
        var td = terrain.terrainData;
        int res = td.alphamapResolution;
        float[,,] splat = td.GetAlphamaps(0, 0, res, res);
        int layers = td.alphamapLayers;
        if (trailLayerIndex >= layers) {
            Debug.LogError("trailLayerIndex out of range — add a trail TerrainLayer first.");
            return;
        }

        float radCells = paintWidth / td.size.x * res * 0.5f;
        var lines = ParseLines();

        foreach (var line in lines) {
            for (int seg = 0; seg < line.Count - 1; seg++) {
                Vector2 a = ToCell(line[seg], td, res);
                Vector2 b = ToCell(line[seg + 1], td, res);

                int minX = Mathf.FloorToInt(Mathf.Min(a.x, b.x) - radCells - 1);
                int maxX = Mathf.CeilToInt(Mathf.Max(a.x, b.x) + radCells + 1);
                int minY = Mathf.FloorToInt(Mathf.Min(a.y, b.y) - radCells - 1);
                int maxY = Mathf.CeilToInt(Mathf.Max(a.y, b.y) + radCells + 1);

                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++) {
                        if (x < 0 || y < 0 || x >= res || y >= res) continue;
                        float dist = DistToSegment(new Vector2(x, y), a, b);
                        if (dist > radCells + edgeSoftness) continue;
                        float strength = Mathf.Clamp01((radCells - dist) / edgeSoftness + 0.5f);
                        float keep = 1f - strength;
                        for (int l = 0; l < layers; l++)
                            splat[y, x, l] = (l == trailLayerIndex)
                                ? splat[y, x, l] * keep + strength
                                : splat[y, x, l] * keep;
                    }
            }
        }
        td.SetAlphamaps(0, 0, splat);
        Debug.Log($"Method A: painted {lines.Count} trails (segment-based).");
    }

    // ---------------- METHOD B ----------------
    public void BuildRibbons() {
        if (trailMaterial == null) { Debug.LogError("Assign a Trail Material first."); return; }

        var old = GameObject.Find("Trails_Ribbons");
        if (old != null) DestroyImmediate(old);

        var lines = ParseLines();
        var parent = new GameObject("Trails_Ribbons").transform;
        float half = ribbonWidth * 0.5f;

        foreach (var pts in lines) {
            if (pts.Count < 2) continue;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();

            for (int k = 0; k < pts.Count; k++) {
                Vector3 fwd = (k < pts.Count - 1) ? (pts[k + 1] - pts[k]) : (pts[k] - pts[k - 1]);
                fwd.y = 0; fwd.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 lift = Vector3.up * ribbonLift;
                verts.Add(pts[k] - side * half + lift);
                verts.Add(pts[k] + side * half + lift);
                uvs.Add(new Vector2(0, k * 0.2f));
                uvs.Add(new Vector2(1, k * 0.2f));
            }
            for (int k = 0; k < pts.Count - 1; k++) {
                int b = k * 2;
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }

            var go = new GameObject("Trail");
            go.transform.parent = parent;
            var mf = go.AddComponent<MeshFilter>();
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.SetUVs(0, uvs);
            m.RecalculateNormals(); m.RecalculateBounds();
            mf.sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterial = trailMaterial;
        }
        Debug.Log($"Method B: built {lines.Count} ribbons.");
    }
}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(PathPainter))]
public class PathPainterEditor : UnityEditor.Editor {
    public override void OnInspectorGUI() {
        DrawDefaultInspector();
        var t = (PathPainter)target;
        UnityEditor.EditorGUILayout.Space();
        if (GUILayout.Button("Method A: Paint Trails")) t.PaintTrails();
        if (GUILayout.Button("Method B: Build Ribbons")) t.BuildRibbons();
    }
}
#endif
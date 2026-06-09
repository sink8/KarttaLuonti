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
    public bool flipX = true;           // matches your flipped heightmap; toggle if mirrored

    [Header("Method A — splatmap paint")]
    public int trailLayerIndex = 0;     // index of your brown trail TerrainLayer
    public float paintWidth = 4f;       // meters

    [Header("Method B — ribbon mesh")]
    public Material trailMaterial;
    public float ribbonWidth = 3f;      // meters
    public float ribbonLift = 0.2f;     // meters above ground

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

            // each "[E,N,Z]" is a point; "],[" separates points within a line,
            // and a new "[[" after "]]" starts a new sub-line. Split on point groups.
            // We walk the block and collect numeric pairs, breaking a line whenever
            // we cross a "]]" boundary (end of a sub-line in MultiLineString).
            var current = new List<Vector3>();
            int k = 0;
            while (k < block.Length) {
                if (block[k] == '[') {
                    // is this the start of a coordinate pair (next char is a digit/minus)?
                    int p = k + 1;
                    while (p < block.Length && (block[p] == ' ')) p++;
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
                        // detect end-of-subline: "]]" closes a sub-line
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

    // ---------------- METHOD A ----------------
    public void PaintTrails() {
        var td = terrain.terrainData;
        int res = td.alphamapResolution;
        float[,,] splat = td.GetAlphamaps(0, 0, res, res);
        int layers = td.alphamapLayers;
        if (trailLayerIndex >= layers) { Debug.LogError("trailLayerIndex out of range — add a trail TerrainLayer first."); return; }

        float radCells = paintWidth / td.size.x * res * 0.5f;
        int r = Mathf.CeilToInt(radCells);
        var lines = ParseLines();

        foreach (var line in lines)
            foreach (var p in line) {
                float u = (p.x - terrain.transform.position.x) / td.size.x;
                float v = (p.z - terrain.transform.position.z) / td.size.z;
                int cx = Mathf.RoundToInt(u * (res - 1));
                int cy = Mathf.RoundToInt(v * (res - 1));
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++) {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= res || y >= res) continue;
                        if (dx * dx + dy * dy > radCells * radCells) continue;
                        for (int l = 0; l < layers; l++) splat[y, x, l] = (l == trailLayerIndex) ? 1f : 0f;
                    }
            }
        td.SetAlphamaps(0, 0, splat);
        Debug.Log($"Method A: painted {lines.Count} trail segments.");
    }

    // ---------------- METHOD B ----------------
    public void BuildRibbons() {
        if (trailMaterial == null) { Debug.LogError("Assign a Trail Material first."); return; }

        // clear previous run
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


Vector3 WorldToTerrain(double easting, double northing, Terrain terrain) {
        double tileMinE = 590000, tileMinN = 7362000, tileSize = 6000;
        float u = (float)((easting - tileMinE) / tileSize); // 0..1 west->east
        float v = (float)((northing - tileMinN) / tileSize); // 0..1 south->north

        Vector3 size = terrain.terrainData.size;
        float localX = u * size.x;
        float localZ = v * size.z;
        float y = terrain.SampleHeight(new Vector3(localX, 0, localZ) + terrain.transform.position);
        return new Vector3(localX, y, localZ) + terrain.transform.position;
    }

    public void PaintLines(TextAsset geojson, Terrain terrain, int trailLayerIndex, float widthMeters) {
        var td = terrain.terrainData;
        int res = td.alphamapResolution;
        float[,,] splat = td.GetAlphamaps(0, 0, res, res);
        var lines = ParseLines(geojson.text, terrain);

        int layers = td.alphamapLayers;
        float radCells = widthMeters / td.size.x * res * 0.5f;

        foreach (var line in lines)
            foreach (var p in line.points) {
                // local pos back to 0..1 then to alphamap cell
                float u = (p.x - terrain.transform.position.x) / td.size.x;
                float v = (p.z - terrain.transform.position.z) / td.size.z;
                int cx = Mathf.RoundToInt(u * (res - 1));
                int cy = Mathf.RoundToInt(v * (res - 1));
                int r = Mathf.CeilToInt(radCells);
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++) {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= res || y >= res) continue;
                        if (dx * dx + dy * dy > radCells * radCells) continue;
                        for (int l = 0; l < layers; l++) splat[y, x, l] = (l == trailLayerIndex) ? 1f : 0f;
                    }
            }
        td.SetAlphamaps(0, 0, splat);
        Debug.Log("Lines painted into splatmap.");
    }

    public void BuildРibbons(TextAsset geojson, Terrain terrain, Material trailMat, float widthMeters) {
        var lines = ParseLines(geojson.text, terrain);
        float half = widthMeters * 0.5f;

        foreach (var line in lines) {
            if (line.points.Count < 2) continue;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();

            for (int i = 0; i < line.points.Count; i++) {
                Vector3 fwd = (i < line.points.Count - 1)
                    ? (line.points[i + 1] - line.points[i]).normalized
                    : (line.points[i] - line.points[i - 1]).normalized;
                Vector3 sideDir = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 lift = Vector3.up * 0.15f; // sit just above ground
                verts.Add(line.points[i] - sideDir * half + lift);
                verts.Add(line.points[i] + sideDir * half + lift);
                uvs.Add(new Vector2(0, i)); uvs.Add(new Vector2(1, i));
            }
            for (int i = 0; i < line.points.Count - 1; i++) {
                int b = i * 2;
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }

            var go = new GameObject("Trail");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = trailMat;
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts); m.SetTriangles(tris, 0); m.SetUVs(0, uvs);
            m.RecalculateNormals();
            mf.sharedMesh = m;
        }
        Debug.Log("Trail ribbons built.");
    }

    [System.Serializable]
public class LineFeature { public List<Vector3> points = new List<Vector3>(); }

List<LineFeature> ParseLines(string json, Terrain terrain) {
    var result = new List<LineFeature>();
    // crude but effective: find each "coordinates":[ ... ] block of a LineString
    int i = 0;
    while ((i = json.IndexOf("LineString", i)) != -1) {
        int c = json.IndexOf("coordinates", i);
        int open = json.IndexOf('[', c);
        int close = json.IndexOf(']', open);
        // grab inner pairs: [[e,n],[e,n],...]
        int blockEnd = json.IndexOf("]]", open);
        string block = json.Substring(open, blockEnd - open + 2);
        var feat = new LineFeature();
        foreach (var pair in block.Split(new[] { "],[" }, System.StringSplitOptions.None)) {
            var nums = pair.Replace("[", "").Replace("]", "").Split(',');
            if (nums.Length >= 2 &&
                double.TryParse(nums[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double e) &&
                double.TryParse(nums[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double n))
                feat.points.Add(WorldToTerrain(e, n, terrain));
        }
        if (feat.points.Count > 1) result.Add(feat);
        i = blockEnd;
    }
    return result;
}

}
#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(PathPainter))]
public class TrailMapperEditor : UnityEditor.Editor {
    public override void OnInspectorGUI() {
        DrawDefaultInspector();
        var t = (PathPainter)target;
        UnityEditor.EditorGUILayout.Space();
        if (GUILayout.Button("Method A: Paint Trails")) t.PaintTrails();
        if (GUILayout.Button("Method B: Build Ribbons")) t.BuildRibbons();
    }
}
#endif
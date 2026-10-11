// GuideSketch10 - live top-down map sketch for the guide book (UnityEngine + PhysicsModule; host and FISHING.exe).
// Raycasts a grid straight down over the map window and colours each cell: lake (ground below the water level, darker =
// deeper), land (lighter = lower, with hill shading). Markers (spots, hotspots, ponds, stand, roulette, you) are drawn on
// top. The terrain part is cached per scene + water level; markers are redrawn on every call.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class GuideSketch10
{
    public class Mark { public float x, z, r; public Color c; public int shape; public char label; }   // shape 0 ring, 1 dot, 2 cross, 3 square, 4 ring+dot; label = letter A-H next to it
    public const int Ring = 0, Dot = 1, Cross = 2, Square = 3, RingDot = 4;
    public static int N = 160, Px = 320;

    // one cached terrain + texture per slot ("map", "zones", ...) so several sketches can live in one book
    class Slot { public string cacheKey; public Color[] basePx; public Texture2D tex; }
    static readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();

    public static Texture2D Build(string scene, float cx, float cz, float size, float waterY, int mask, List<Mark> marks) { return Build("map", scene, cx, cz, size, waterY, mask, marks); }
    public static Texture2D Build(string slotName, string scene, float cx, float cz, float size, float waterY, int mask, List<Mark> marks)
    {
        Slot sl; if (!slots.TryGetValue(slotName, out sl)) { sl = new Slot(); slots[slotName] = sl; }
        string key = scene + "|" + cx + "|" + cz + "|" + size + "|" + waterY.ToString("0.0");
        Color[] basePx = sl.basePx;
        if (key != sl.cacheKey || basePx == null)
        {
            var h = new float[N * N]; var hit = new bool[N * N];
            float step = size / N, x0 = cx - size / 2f, z0 = cz - size / 2f;
            float top = (float.IsNaN(waterY) ? 0f : waterY) + 400f;
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    RaycastHit rh;
                    var o = new Vector3(x0 + (i + 0.5f) * step, top, z0 + (j + 0.5f) * step);
                    if (Physics.Raycast(o, Vector3.down, out rh, 1200f, mask, QueryTriggerInteraction.Ignore)) { h[j * N + i] = rh.point.y; hit[j * N + i] = true; }
                    else h[j * N + i] = float.NegativeInfinity;
                }
            float wy = float.IsNaN(waterY) ? float.NegativeInfinity : waterY;
            float hmax = wy + 1f; foreach (float v in h) if (!float.IsInfinity(v)) hmax = Math.Max(hmax, v);
            var cells = new Color[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float v = h[j * N + i];
                    Color c;
                    if (!hit[j * N + i] || v < wy)
                    {
                        float depth = hit[j * N + i] ? wy - v : 30f;
                        c = Color.Lerp(new Color(0.55f, 0.75f, 0.85f), new Color(0.12f, 0.3f, 0.55f), Mathf.Clamp01(depth / 25f));
                    }
                    else
                    {
                        float k = Mathf.Clamp01((v - wy) / Math.Max(10f, Math.Min(80f, hmax - wy)));
                        c = Color.Lerp(new Color(0.85f, 0.86f, 0.78f), new Color(0.38f, 0.42f, 0.3f), k);
                        float hx = i > 0 && hit[j * N + i - 1] ? h[j * N + i - 1] : v, hz = j > 0 && hit[(j - 1) * N + i] ? h[(j - 1) * N + i] : v;
                        float shade = Mathf.Clamp((v - hx + v - hz) / step * 0.25f, -0.3f, 0.3f);
                        c = c * (1f + shade); c.a = 1f;
                    }
                    cells[j * N + i] = c;
                }
            // upscale to Px with nearest + light grid every 50 m
            basePx = new Color[Px * Px];
            float mPerPx = size / Px;
            for (int y = 0; y < Px; y++)
                for (int x = 0; x < Px; x++)
                {
                    Color c = cells[(y * N / Px) * N + (x * N / Px)];
                    float wx = x0 + x * mPerPx, wz = z0 + y * mPerPx;
                    if (Mathf.Abs(Mathf.Repeat(wx, 50f)) < mPerPx || Mathf.Abs(Mathf.Repeat(wz, 50f)) < mPerPx) c = Color.Lerp(c, new Color(0.3f, 0.25f, 0.2f), 0.25f);
                    basePx[y * Px + x] = c;
                }
            sl.cacheKey = key; sl.basePx = basePx;
        }
        if (sl.tex == null) { sl.tex = new Texture2D(Px, Px, TextureFormat.RGBA32, false); sl.tex.wrapMode = TextureWrapMode.Clamp; sl.tex.filterMode = FilterMode.Bilinear; }
        Texture2D tex = sl.tex;
        var px = (Color[])basePx.Clone();
        float s = Px / size, ox = cx - size / 2f, oz = cz - size / 2f;
        foreach (var m in marks ?? new List<Mark>())
        {
            float mx = (m.x - ox) * s, mz = (m.z - oz) * s, mr = Math.Max(3f, m.r * s);
            if (m.shape == Ring || m.shape == RingDot) Circle(px, mx, mz, mr, 2.2f, m.c);
            if (m.shape == Dot || m.shape == RingDot) Disc(px, mx, mz, m.shape == RingDot ? 3f : Math.Max(3f, mr), m.c);
            if (m.shape == Cross) { Line(px, mx - 7, mz - 7, mx + 7, mz + 7, 2f, m.c); Line(px, mx - 7, mz + 7, mx + 7, mz - 7, 2f, m.c); }
            if (m.label != (char)0) Letter(px, mx + mr * 0.72f + 3f, mz + mr * 0.72f + 3f, m.label, m.c);
            if (m.shape == Square) { for (int dy = -5; dy <= 5; dy++) for (int dx = -5; dx <= 5; dx++) Put(px, (int)mx + dx, (int)mz + dy, Math.Abs(dx) >= 4 || Math.Abs(dy) >= 4 ? Color.black : m.c); }
        }
        // frame
        for (int i = 0; i < Px; i++) { Put(px, i, 0, Color.black); Put(px, i, Px - 1, Color.black); Put(px, 0, i, Color.black); Put(px, Px - 1, i, Color.black); }
        tex.SetPixels(px); tex.Apply();
        return tex;
    }

    // 5x7 letters A-H, drawn 2x with a light outline (y up = north, rows listed top to bottom)
    static readonly string[] Font = {
        "01110100011000111111100011000110001", "11110100011000111110100011000111110", "01110100011000010000100001000101110", "11110100011000110001100011000111110",
        "11111100001000011110100001000011111", "11111100001000011110100001000010000", "01110100011000010111100011000101111", "10001100011000111111100011000110001" };
    static void Letter(Color[] px, float x0, float y0, char ch, Color c)
    {
        int li = ch - 'A'; if (li < 0 || li >= Font.Length) return;
        string bits = Font[li];
        for (int pass = 0; pass < 2; pass++)
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                {
                    if (bits[row * 5 + col] != '1') continue;
                    int bx = (int)x0 + col * 2, by = (int)y0 + (6 - row) * 2;
                    for (int dy = -1; dy <= 2; dy++) for (int dx = -1; dx <= 2; dx++)
                    {
                        bool core = dx >= 0 && dx <= 1 && dy >= 0 && dy <= 1;
                        if (pass == 0 && !core) Put(px, bx + dx, by + dy, new Color(1f, 1f, 0.95f));
                        if (pass == 1 && core) Put(px, bx + dx, by + dy, c);
                    }
                }
    }
    static void Put(Color[] px, int x, int y, Color c) { if (x >= 0 && y >= 0 && x < Px && y < Px) px[y * Px + x] = c; }
    static void Circle(Color[] px, float cx, float cy, float r, float w, Color c)
    {
        int r0 = (int)(r + w + 1);
        for (int y = -r0; y <= r0; y++) for (int x = -r0; x <= r0; x++) { float d = Mathf.Sqrt(x * x + y * y); if (Mathf.Abs(d - r) <= w / 2f) Put(px, (int)cx + x, (int)cy + y, c); }
    }
    static void Disc(Color[] px, float cx, float cy, float r, Color c)
    {
        int r0 = (int)r + 1;
        for (int y = -r0; y <= r0; y++) for (int x = -r0; x <= r0; x++) if (x * x + y * y <= r * r) Put(px, (int)cx + x, (int)cy + y, c);
    }
    static void Line(Color[] px, float x0, float y0, float x1, float y1, float w, Color c)
    {
        int n = (int)(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) + 1);
        for (int i = 0; i <= n; i++) { float t = i / (float)n; Disc(px, Mathf.Lerp(x0, x1, t), Mathf.Lerp(y0, y1, t), w / 2f, c); }
    }
}

// Engine-independent fishing-pole icon rasterizer (same art as FishIcon2.cs).
// Buffer: RGBA floats, 128x128, row 0 = BOTTOM (Unity texture convention).
using System;

public static class NoWeaponsIconPixels
{
    public const int N = 128;
    static float[] p;

    static void Set(int x, int y, float r, float g, float b, float a)
    {
        int i = (y * N + x) * 4; p[i] = r; p[i + 1] = g; p[i + 2] = b; p[i + 3] = a;
    }
    static void Blend(int x, int y, float r, float g, float b, float a, float k) // lerp(c, existing, k)
    {
        int i = (y * N + x) * 4;
        p[i] = r + (p[i] - r) * k; p[i + 1] = g + (p[i + 1] - g) * k; p[i + 2] = b + (p[i + 2] - b) * k; p[i + 3] = a + (p[i + 3] - a) * k;
    }

    static void Disc(float cx, float cy, float rad, float r, float g, float b, float a)
    {
        for (int y = (int)(cy - rad - 1); y <= cy + rad + 1; y++)
            for (int x = (int)(cx - rad - 1); x <= cx + rad + 1; x++)
            {
                if (x < 0 || y < 0 || x >= N || y >= N) continue;
                float d = (float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d <= rad) Set(x, y, r, g, b, a);
                else if (d <= rad + 1) Blend(x, y, r, g, b, a, d - rad);
            }
    }

    static void Line(float x0, float y0, float x1, float y1, float w, float r, float g, float b, float a)
    {
        int steps = (int)(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) * 2) + 1;
        for (int i = 0; i <= steps; i++)
        {
            float k = i / (float)steps;
            Disc(x0 + (x1 - x0) * k, y0 + (y1 - y0) * k, w, r, g, b, a);
        }
    }

    public static float[] Draw()
    {
        p = new float[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float k = y / 127f;
                bool corner = (x < 10 || x > 117) && (y < 10 || y > 117);
                if (corner) Set(x, y, 0, 0, 0, 0);
                else Set(x, y, 0.04f + 0.06f * k, 0.16f + 0.26f * k, 0.30f + 0.32f * k, 1f);
            }
        for (int i = 0; i < 3; i++) Line(18 + i * 8, 26 - i * 6, 74 - i * 4, 26 - i * 6, 0.8f, 0.55f, 0.82f, 0.95f, 0.8f);  // ripples
        Line(22, 34, 104, 112, 3.2f, 0.35f, 0.20f, 0.08f, 1f);   // rod
        Line(22, 34, 46, 57, 4.4f, 0.15f, 0.10f, 0.06f, 1f);     // grip
        Disc(44, 52, 8, 0.75f, 0.75f, 0.78f, 1f);                // reel
        Disc(44, 52, 3.5f, 0.30f, 0.30f, 0.33f, 1f);
        Line(104, 112, 92, 46, 0.7f, 0.95f, 0.95f, 0.95f, 1f);   // fishing line
        Disc(92, 40, 8, 1f, 1f, 1f, 1f);                         // bobber (white)
        for (int y = 40; y < 50; y++) for (int x = 82; x < 102; x++)
            if ((x - 92) * (x - 92) + (y - 40) * (y - 40) <= 64) Set(x, y, 0.85f, 0.10f, 0.10f, 1f);  // red top
        for (int i = 10; i < 118; i++)                           // gold frame
        {
            Set(i, 3, 0.85f, 0.75f, 0.35f, 1f); Set(3, i, 0.85f, 0.75f, 0.35f, 1f);
            Set(i, 124, 0.85f, 0.75f, 0.35f, 1f); Set(124, i, 0.85f, 0.75f, 0.35f, 1f);
        }
        float[] r = p; p = null; return r;
    }

    // "Rod Whip" (right click): the rod swung hard to the right, white motion swooshes behind it, a hit spark at the tip
    public static float[] DrawWhip()
    {
        p = new float[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float k = y / 127f;
                bool corner = (x < 10 || x > 117) && (y < 10 || y > 117);
                if (corner) Set(x, y, 0, 0, 0, 0);
                else Set(x, y, 0.30f + 0.22f * k, 0.08f + 0.10f * k, 0.06f + 0.06f * k, 1f);   // dark red -> rust
            }
        // motion swooshes: arcs around the grip (20,22), from the rod's angle (~35 deg) back toward vertical
        float gx = 20, gy = 22;
        float[] radii = { 62f, 76f, 90f };
        for (int j = 0; j < radii.Length; j++)
        {
            float rad = radii[j];
            for (float deg = 42f; deg <= 92f; deg += 0.5f)
            {
                float t = (deg - 42f) / 50f;                       // 0 at the rod, 1 at the trailing end
                float a = (1f - t) * (0.95f - 0.15f * j);
                double rr = deg * Math.PI / 180.0;
                float x = gx + rad * (float)Math.Cos(rr), y = gy + rad * (float)Math.Sin(rr);
                float w = 2.2f * (1f - t) + 0.4f;
                for (int yy = (int)(y - w - 1); yy <= y + w + 1; yy++)
                    for (int xx = (int)(x - w - 1); xx <= x + w + 1; xx++)
                    {
                        if (xx < 6 || yy < 6 || xx >= N - 6 || yy >= N - 6) continue;
                        float d = (float)Math.Sqrt((xx - x) * (xx - x) + (yy - y) * (yy - y));
                        if (d <= w) Blend(xx, yy, 1f, 0.97f, 0.88f, 1f, 1f - a);
                    }
            }
        }
        // rod (35 deg, slightly bent at the tip), grip, reel
        Line(20, 22, 66, 54, 3.4f, 0.35f, 0.20f, 0.08f, 1f);
        Line(66, 54, 98, 72, 2.6f, 0.38f, 0.22f, 0.09f, 1f);
        Line(98, 72, 108, 76, 1.8f, 0.40f, 0.24f, 0.10f, 1f);
        Line(20, 22, 42, 37, 4.6f, 0.15f, 0.10f, 0.06f, 1f);
        Disc(40, 37, 8, 0.75f, 0.75f, 0.78f, 1f);
        Disc(40, 37, 3.5f, 0.30f, 0.30f, 0.33f, 1f);
        // snapping line: a curl flicking off the tip
        float px = 108, py = 76;
        for (int i = 1; i <= 40; i++)
        {
            float t = i / 40f;
            float x = 108 + 10f * t + 6f * (float)Math.Sin(t * Math.PI * 2), y = 76 - 30f * t + 5f * (float)Math.Cos(t * Math.PI * 2) - 5f;
            Line(px, py, x, y, 0.7f, 0.95f, 0.95f, 0.95f, 1f);
            px = x; py = y;
        }
        // hit spark (yellow star) at the end of the line
        float sx = 112, sy = 40;
        for (int i = 0; i < 8; i++)
        {
            double an = i * Math.PI / 4;
            float len = i % 2 == 0 ? 13f : 8f;
            Line(sx, sy, sx + len * (float)Math.Cos(an), sy + len * (float)Math.Sin(an), 1.3f, 1f, 0.85f, 0.2f, 1f);
        }
        Disc(sx, sy, 4.5f, 1f, 0.95f, 0.6f, 1f);
        for (int i = 10; i < 118; i++)                           // gold frame
        {
            Set(i, 3, 0.85f, 0.75f, 0.35f, 1f); Set(3, i, 0.85f, 0.75f, 0.35f, 1f);
            Set(i, 124, 0.85f, 0.75f, 0.35f, 1f); Set(124, i, 0.85f, 0.75f, 0.35f, 1f);
        }
        float[] r = p; p = null; return r;
    }
}


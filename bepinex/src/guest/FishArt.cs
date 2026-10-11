// FishArt - procedural side-view fish textures (128 x 64, transparent background, head on the right).
// Pure C# (no Unity types) so the exact same pixels can be rendered in-game (Texture2D) and in a
// standalone preview tool (System.Drawing). Output: RGBA floats, row 0 = TOP of the image.
using System;

public static class FishArt
{
    public const int W = 128, H = 64;
    public static readonly string[] Species = { "Minnow", "Perch", "Trout", "Bass", "Salmon", "Swordfish", "Golden Koi", "Moon Eel", "Old Boot",
                                                      "Clownfish", "Catfish", "Pufferfish", "Anglerfish", "Ghost Fish" };

    // ---------------------------------------------------------------- helpers
    static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
    static float Smooth(float e0, float e1, float x) { float t = Clamp01((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }
    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
    static float Noise(float x, float y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }
    static float Fbm(float x, float y, int seed) { return Noise(x, y, seed) * 0.65f + Noise(x * 2.1f, y * 2.1f, seed + 7) * 0.35f; }

    struct C { public float r, g, b, a; public C(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    static C Rgb(float r, float g, float b) { return new C(r, g, b, 1); }
    static C Mix(C x, C y, float t) { return new C(Lerp(x.r, y.r, t), Lerp(x.g, y.g, t), Lerp(x.b, y.b, t), Lerp(x.a, y.a, t)); }
    static C Mul(C x, float k) { return new C(x.r * k, x.g * k, x.b * k, x.a); }
    static C Over(C top, C under)   // straight-alpha "top over under"
    {
        float a = top.a + under.a * (1 - top.a);
        if (a <= 0.0001f) return new C(0, 0, 0, 0);
        return new C((top.r * top.a + under.r * under.a * (1 - top.a)) / a,
                     (top.g * top.a + under.g * under.a * (1 - top.a)) / a,
                     (top.b * top.a + under.b * under.a * (1 - top.a)) / a, a);
    }
    static readonly C Clear = new C(0, 0, 0, 0);

    // ---------------------------------------------------------------- species definition
    class Spec
    {
        public float x0, xb, x1, cy, bh, peak = 0.45f, ped = 0.2f, bellyK = 0.95f;
        public float tailSpread, fork = 0.55f;
        public float dorsal0, dorsal1, dorsalH, dorsal2_0, dorsal2_1, dorsal2H;  // as t (0 = tail end, 1 = nose)
        public float analT = 0.25f, analH = 6, pelvicT = 0.55f, pelvicH = 6;
        public C back, belly, fin; public float finA = 0.85f;
        public float eyeR = 3f, eyeT = 0.88f; public C iris = Rgb(0.9f, 0.85f, 0.4f);
        public int kind;   // 0 normal fish, 1 eel, 2 boot, 3 pufferfish, 4 anglerfish
        public bool blunt, finEdge; public float bodyA = 1, glowA = 0; public C glowC;
    }

    static Spec Get(string sp)
    {
        var s = new Spec { x0 = 5, xb = 30, x1 = 122, cy = 32, bh = 16, tailSpread = 17, dorsal0 = 0.42f, dorsal1 = 0.68f, dorsalH = 9,
                           back = Rgb(0.4f, 0.45f, 0.5f), belly = Rgb(0.92f, 0.92f, 0.92f), fin = Rgb(0.7f, 0.7f, 0.7f) };
        switch (sp)
        {
            case "Minnow":
                s.bh = 12; s.tailSpread = 14; s.dorsalH = 8; s.dorsal0 = 0.45f; s.dorsal1 = 0.62f; s.analH = 5; s.pelvicH = 4;
                s.back = Rgb(0.42f, 0.5f, 0.52f); s.belly = Rgb(0.93f, 0.95f, 0.97f); s.fin = Rgb(0.78f, 0.82f, 0.84f); s.finA = 0.6f; s.eyeR = 3.2f; break;
            case "Perch":
                s.bh = 19; s.peak = 0.5f; s.tailSpread = 17; s.dorsal0 = 0.52f; s.dorsal1 = 0.8f; s.dorsalH = 12; s.dorsal2_0 = 0.25f; s.dorsal2_1 = 0.47f; s.dorsal2H = 9;
                s.back = Rgb(0.36f, 0.5f, 0.12f); s.belly = Rgb(0.93f, 0.88f, 0.5f); s.fin = Rgb(1f, 0.48f, 0.08f); s.analH = 8; s.pelvicH = 8; break;
            case "Trout":
                s.bh = 15; s.tailSpread = 16; s.fork = 0.3f; s.dorsal0 = 0.45f; s.dorsal1 = 0.62f; s.dorsalH = 9;
                s.back = Rgb(0.38f, 0.42f, 0.18f); s.belly = Rgb(0.96f, 0.92f, 0.82f); s.fin = Rgb(0.6f, 0.55f, 0.35f); break;
            case "Bass":
                s.bh = 19; s.x1 = 123; s.tailSpread = 17; s.fork = 0.25f; s.dorsal0 = 0.32f; s.dorsal1 = 0.72f; s.dorsalH = 10; s.eyeT = 0.86f;
                s.back = Rgb(0.16f, 0.32f, 0.12f); s.belly = Rgb(0.93f, 0.93f, 0.78f); s.fin = Rgb(0.32f, 0.42f, 0.2f); break;
            case "Salmon":
                s.bh = 16; s.peak = 0.5f; s.tailSpread = 18; s.fork = 0.35f; s.dorsal0 = 0.48f; s.dorsal1 = 0.64f; s.dorsalH = 10;
                s.back = Rgb(0.4f, 0.47f, 0.58f); s.belly = Rgb(1f, 0.78f, 0.68f); s.fin = Rgb(0.55f, 0.55f, 0.62f); break;
            case "Swordfish":
                s.x0 = 3; s.xb = 22; s.x1 = 96; s.bh = 14; s.tailSpread = 23; s.fork = 0.85f; s.ped = 0.15f;
                s.dorsal0 = 0.6f; s.dorsal1 = 0.8f; s.dorsalH = 16; s.eyeT = 0.84f; s.eyeR = 3.4f;
                s.back = Rgb(0.07f, 0.14f, 0.45f); s.belly = Rgb(0.84f, 0.87f, 0.92f); s.fin = Rgb(0.1f, 0.16f, 0.4f); s.finA = 0.95f; break;
            case "Golden Koi":
                s.bh = 16; s.tailSpread = 20; s.fork = 0.2f; s.dorsal0 = 0.3f; s.dorsal1 = 0.7f; s.dorsalH = 8; s.analH = 8; s.pelvicH = 9;
                s.back = Rgb(1f, 0.55f, 0.08f); s.belly = Rgb(1f, 0.82f, 0.3f); s.fin = Rgb(1f, 0.65f, 0.2f); s.finA = 0.7f; s.iris = Rgb(0.2f, 0.15f, 0.1f); break;
            case "Clownfish":
                s.bh = 17; s.peak = 0.5f; s.tailSpread = 15; s.fork = 0.04f; s.dorsal0 = 0.28f; s.dorsal1 = 0.74f; s.dorsalH = 9; s.analH = 8; s.pelvicH = 7;
                s.back = Rgb(1f, 0.42f, 0.03f); s.belly = Rgb(1f, 0.56f, 0.16f); s.fin = Rgb(1f, 0.48f, 0.06f); s.finA = 0.95f; s.finEdge = true;
                s.eyeR = 3.4f; s.iris = Rgb(0.95f, 0.5f, 0.1f); break;
            case "Catfish":
                s.x1 = 110; s.bh = 13; s.peak = 0.6f; s.ped = 0.25f; s.blunt = true; s.bellyK = 0.8f; s.tailSpread = 15; s.fork = 0.3f;
                s.dorsal0 = 0.6f; s.dorsal1 = 0.72f; s.dorsalH = 11; s.analT = 0.3f; s.analH = 6; s.pelvicH = 5; s.eyeR = 2.2f; s.eyeT = 0.9f;
                s.back = Rgb(0.33f, 0.3f, 0.25f); s.belly = Rgb(0.78f, 0.74f, 0.64f); s.fin = Rgb(0.36f, 0.32f, 0.26f); s.finA = 0.9f; s.iris = Rgb(0.75f, 0.65f, 0.3f); break;
            case "Ghost Fish":
                s.bh = 15; s.tailSpread = 20; s.fork = 0.45f; s.dorsal0 = 0.35f; s.dorsal1 = 0.7f; s.dorsalH = 11; s.analH = 8; s.pelvicH = 8;
                s.back = Rgb(0.62f, 0.9f, 0.88f); s.belly = Rgb(0.95f, 0.99f, 1f); s.fin = Rgb(0.75f, 0.97f, 0.95f); s.finA = 0.32f;
                s.bodyA = 0.62f; s.glowA = 0.32f; s.glowC = Rgb(0.55f, 1f, 0.95f); s.iris = Rgb(0.4f, 1f, 0.95f); s.eyeR = 3.2f; break;
            case "Pufferfish": s.kind = 3; break;
            case "Anglerfish": s.kind = 4; break;
            case "Moon Eel": s.kind = 1; break;
            case "Old Boot": s.kind = 2; break;
        }
        return s;
    }

    // ---------------------------------------------------------------- body geometry
    static float Profile(Spec s, float t)   // 0..1 relative half-height at body parameter t (0 = peduncle, 1 = nose)
    {
        if (t < 0 || t > 1) return 0;
        if (t < s.peak) return Lerp(s.ped, 1f, (float)Math.Sin(t / s.peak * Math.PI / 2));
        float u = (t - s.peak) / (1 - s.peak);
        if (s.blunt) return (float)Math.Sqrt(Math.Max(0, 1 - Math.Pow(u, 6))) * (1 - 0.08f * u);
        return (float)Math.Sqrt(Math.Max(0, 1 - u * u * u * 1.0f)) * (1 - 0.15f * u);
    }

    static C SampleFish(Spec s, string sp, float x, float y)
    {
        float t = (x - s.xb) / (s.x1 - s.xb);
        float prof = Profile(s, t);
        float hTop = s.bh * prof, hBot = s.bh * prof * s.bellyK;
        float topY = s.cy - hTop, botY = s.cy + hBot;
        C col = Clear;
        if (s.glowA > 0)
        {
            float gd = (t >= 0 && t <= 1) ? Math.Max(0, Math.Max(y - botY, topY - y)) : 99;
            if (t < 0) { float ex = Math.Max(0, s.x0 - x), ey = Math.Max(0, Math.Abs(y - s.cy) - s.tailSpread * Clamp01((s.xb - x) / (s.xb - s.x0))); gd = Math.Min(gd, (float)Math.Sqrt(ex * ex + ey * ey) + 2); }
            if (t > 1) { float ex = x - s.x1, ey = Math.Max(0, Math.Abs(y - s.cy) - 3); gd = Math.Min(gd, (float)Math.Sqrt(ex * ex + ey * ey)); }
            C g = s.glowC; g.a = Math.Max(0, s.glowA * (float)Math.Exp(-gd / 3.5f) - 0.012f) * Smooth(0, 6, Math.Min(Math.Min(x, W - x), Math.Min(y, H - y))); col = g;
        }

        // --- fins behind the body
        float finAlpha = s.finA;
        // tail
        float tailEnd = s.xb + 3;
        if (x >= s.x0 && x <= tailEnd)
        {
            float k = (tailEnd - x) / (tailEnd - s.x0);          // 0 at peduncle, 1 at tail tip
            float spread = Lerp(s.bh * s.ped, s.tailSpread, (float)Math.Pow(k, 0.8));
            float notch = spread * s.fork * (float)Math.Pow(k, 1.6);
            float dy = Math.Abs(y - s.cy);
            if (dy < spread && dy >= notch)
            {
                C f = s.fin; f.a = finAlpha;
                if ((x * 0.5f + dy * 0.7f) % 3f < 0.8f) f = Mul(f, 0.8f);   // fin rays
                if (sp == "Golden Koi") f.a *= 0.6f + 0.4f * (1 - k);
                if (s.finEdge && (spread - dy < 1.3f || x - s.x0 < 1.3f)) f = Rgb(0.05f, 0.04f, 0.04f);
                else if (s.finEdge && (spread - dy < 2.3f || x - s.x0 < 2.3f)) f = Rgb(1f, 1f, 1f);
                col = Over(f, col);
            }
        }
        // dorsal fins
        DorsalFin(s, s.dorsal0, s.dorsal1, s.dorsalH, x, y, ref col, sp);
        if (s.dorsal2H > 0) DorsalFin(s, s.dorsal2_0, s.dorsal2_1, s.dorsal2H, x, y, ref col, sp);
        // anal + pelvic fins (triangles below the belly, swept back)
        LowerFin(s, s.analT, s.analH, x, y, ref col);
        LowerFin(s, s.pelvicT, s.pelvicH, x, y, ref col);

        // --- body
        if (t >= 0 && t <= 1 && y >= topY && y <= botY && prof > 0)
        {
            float v = (y - topY) / Math.Max(0.001f, (hTop + hBot));   // 0 back .. 1 belly
            C b = Mix(s.back, s.belly, Smooth(0.3f, 0.8f, v));
            b = Pattern(sp, s, b, x, y, t, v);
            // scale shimmer + top lighting
            b = Mul(b, 0.96f + 0.04f * (float)(Math.Sin(x * 1.4f + y * 0.7f) * Math.Sin(y * 1.4f - x * 0.7f)));
            b = Mul(b, 1.08f - 0.22f * Math.Abs(v - 0.35f));
            // gill arc
            float gx = s.xb + (s.x1 - s.xb) * 0.78f + (float)Math.Abs(y - s.cy) * 0.25f;
            if (Math.Abs(x - gx) < 0.7f && Math.Abs(y - s.cy) < s.bh * 0.7f) b = Mul(b, 0.6f);
            // outline
            float edge = Math.Min(y - topY, botY - y);
            float nose = (1 - t) * (s.x1 - s.xb);
            if (edge < 1.1f || nose < 1.1f) b = Mul(b, 0.5f);
            b.a = s.bodyA;
            col = Over(b, col);
            // pectoral fin on top of the body
            float px = s.xb + (s.x1 - s.xb) * 0.7f, py = s.cy + s.bh * 0.25f;
            float ex = (x - px) / (s.bh * 0.55f), ey = (y - py) / (s.bh * 0.22f);
            float rx = ex * 0.9f + ey * 0.45f, ry = -ex * 0.45f + ey * 0.9f;   // tilted ellipse
            if (rx * rx + ry * ry < 1 && x < px + 2)
            {
                C f = s.fin; f.a = s.finA * 0.8f;
                if ((x + y) % 3f < 0.8f) f = Mul(f, 0.85f);
                col = Over(f, col);
            }
            // mouth
            float mouthLen = sp == "Bass" ? 9 : sp == "Catfish" ? 8 : 4;
            float my = s.cy + s.bh * (sp == "Bass" ? 0.18f : sp == "Catfish" ? 0.3f : 0.12f);
            if (nose < mouthLen && Math.Abs(y - my) < 0.6f) col = Over(Rgb(0.15f, 0.08f, 0.06f), col);
            // eye
            float exc = s.xb + (s.x1 - s.xb) * s.eyeT, eyc = s.cy - s.bh * 0.22f;
            float d = (float)Math.Sqrt((x - exc) * (x - exc) + (y - eyc) * (y - eyc));
            if (d < s.eyeR)
            {
                C e = d < s.eyeR * 0.55f ? Rgb(0.03f, 0.03f, 0.04f) : d < s.eyeR * 0.82f ? s.iris : Rgb(0.1f, 0.1f, 0.1f);
                float hx = x - (exc + s.eyeR * 0.2f), hy = y - (eyc - s.eyeR * 0.25f);
                if (hx * hx + hy * hy < s.eyeR * s.eyeR * 0.05f) e = Rgb(1, 1, 1);
                col = Over(e, col);
            }
        }

        // swordfish bill
        if (sp == "Swordfish" && x > s.x1 - 6 && x < 127)
        {
            float k = (x - (s.x1 - 6)) / (127 - (s.x1 - 6));
            float half = Lerp(2.6f, 0.4f, k);
            float by = s.cy - s.bh * 0.05f;
            if (Math.Abs(y - by) < half)
            {
                C b = Mix(Rgb(0.15f, 0.2f, 0.4f), Rgb(0.6f, 0.62f, 0.68f), Clamp01((y - by + half) / (2 * half)));
                if (Math.Abs(y - by) > half - 0.6f) b = Mul(b, 0.55f);
                col = Over(b, col);
            }
        }
        if (sp == "Catfish")
        {
            for (int w = 0; w < Whisk.Length; w += 6)
            {
                float wt; float wd = BezierDist(x, y, Whisk[w], Whisk[w + 1], Whisk[w + 2], Whisk[w + 3], Whisk[w + 4], Whisk[w + 5], out wt);
                if (wd < Lerp(1.0f, 0.35f, wt)) col = Over(Mix(Rgb(0.22f, 0.18f, 0.13f), Rgb(0.45f, 0.4f, 0.32f), wt), col);
            }
        }
        return col;
    }

    static readonly float[] Whisk = {
        108, 33, 123, 25, 126, 46,
        108, 35, 119, 40, 114, 57,
        104, 40, 109, 48, 100, 55,
        105, 40, 113, 51, 107, 60 };
    static float BezierDist(float x, float y, float ax, float ay, float bx, float by, float cx, float cy, out float bestT)
    {
        float best = 1e9f; bestT = 0;
        for (int i = 0; i <= 40; i++)
        {
            float t = i / 40f, u = 1 - t;
            float px = u * u * ax + 2 * u * t * bx + t * t * cx, py = u * u * ay + 2 * u * t * by + t * t * cy;
            float d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d < best) { best = d; bestT = t; }
        }
        return (float)Math.Sqrt(best);
    }

    static void DorsalFin(Spec s, float t0, float t1, float hgt, float x, float y, ref C col, string sp)
    {
        float xa = s.xb + (s.x1 - s.xb) * t0, xz = s.xb + (s.x1 - s.xb) * t1;
        if (x < xa - 4 || x > xz) return;
        float f = (x - (xa - 4)) / (xz - (xa - 4));                     // 0 back end .. 1 front end
        float h = f > 0.65f ? hgt * (1 - f) / 0.35f : hgt * (float)Math.Pow(f / 0.65f, 0.7);
        if (sp == "Swordfish") h = f > 0.5f ? hgt * (1 - f) / 0.5f * (1.2f - 0.2f * f) : hgt * f / 0.5f * 0.9f;
        float tt = (x - s.xb) / (s.x1 - s.xb);
        float top = s.cy - s.bh * Profile(s, tt);
        if (y < top + 1.5f && y > top - h)
        {
            C c = s.fin; c.a = s.finA;
            if (x % 3f < 0.9f) c = Mul(c, 0.75f);                         // spines
            if (s.finEdge && y < top - h + 1.4f) c = Rgb(0.05f, 0.04f, 0.04f);
            if (sp == "Perch" && t0 > 0.5f && f > 0.05f && f < 0.3f && y > top - h * 0.6f) c = Rgb(0.08f, 0.08f, 0.06f);   // dark spot
            if (sp == "Perch" && t0 > 0.5f) c = Mix(Rgb(0.45f, 0.55f, 0.25f), c, 0.3f);   // spiny dorsal greenish
            col = Over(c, col);
        }
    }

    static void LowerFin(Spec s, float t, float hgt, float x, float y, ref C col)
    {
        float xc = s.xb + (s.x1 - s.xb) * t;
        float w = hgt * 1.1f;
        if (x < xc - w || x > xc + 1) return;
        float f = (x - (xc - w)) / (w + 1);                                // 0 rear tip .. 1 front root
        float bot = s.cy + s.bh * Profile(s, (x - s.xb) / (s.x1 - s.xb)) * s.bellyK;
        float h = hgt * (float)Math.Pow(1 - f, 0.5) * (f < 0.2f ? f / 0.2f : 1);
        if (y > bot - 1.5f && y < bot + h)
        {
            C c = s.fin; c.a = s.finA;
            if (x % 2.5f < 0.7f) c = Mul(c, 0.8f);
            if (s.finEdge && y > bot + h - 1.4f) c = Rgb(0.05f, 0.04f, 0.04f);
            col = Over(c, col);
        }
    }

    static C Pattern(string sp, Spec s, C b, float x, float y, float t, float v)
    {
        switch (sp)
        {
            case "Minnow":
                if (Math.Abs(y - (s.cy + 1)) < 1.7f && t > 0.02f && t < 0.86f) b = Mix(b, Rgb(0.16f, 0.18f, 0.2f), 0.85f);
                break;
            case "Perch":
                {
                    float bar = (float)Math.Sin(t * Math.PI * 13 + 0.4f + (v * 1.2f));
                    if (bar > 0.35f && v < 0.78f && t > 0.05f && t < 0.78f) b = Mix(b, Rgb(0.1f, 0.16f, 0.05f), 0.65f * Smooth(0.78f, 0.45f, v));
                    break;
                }
            case "Trout":
                {
                    float band = 1 - Smooth(0.07f, 0.14f, Math.Abs(v - 0.52f));
                    b = Mix(b, Rgb(0.95f, 0.45f, 0.55f), band * 0.75f);
                    int cx = (int)Math.Floor(x / 5), cyc = (int)Math.Floor(y / 5);
                    for (int i = -1; i <= 1; i++)
                        for (int j = -1; j <= 1; j++)
                        {
                            float sx = (cx + i) * 5 + Hash(cx + i, cyc + j, 3) * 5, sy = (cyc + j) * 5 + Hash(cx + i, cyc + j, 9) * 5;
                            if ((x - sx) * (x - sx) + (y - sy) * (y - sy) < 1.0f && v < 0.68f && Hash(cx + i, cyc + j, 5) < 0.8f) b = Rgb(0.12f, 0.1f, 0.08f);
                        }
                    break;
                }
            case "Bass":
                {
                    float n = Fbm(x * 0.25f, 3, 11);
                    float w = 0.07f + 0.09f * n;
                    if (Math.Abs(v - 0.5f + 0.05f * (Noise(x * 0.4f, 1, 5) - 0.5f)) < w && t > 0.02f && t < 0.85f) b = Mix(b, Rgb(0.07f, 0.13f, 0.05f), 0.85f);
                    if (v < 0.4f) b = Mix(b, Rgb(0.08f, 0.18f, 0.06f), 0.3f * Fbm(x * 0.3f, y * 0.3f, 2));
                    break;
                }
            case "Salmon":
                {
                    if (v > 0.25f && v < 0.6f) b = Mix(b, Rgb(0.85f, 0.86f, 0.9f), 0.6f * (1 - Math.Abs(v - 0.42f) / 0.18f));
                    b = Mix(b, Rgb(1f, 0.55f, 0.35f), 0.45f * (1 - Smooth(0.05f, 0.25f, Math.Abs(v - 0.62f))));
                    int cx = (int)Math.Floor(x / 4), cyc = (int)Math.Floor(y / 4);
                    float sx = cx * 4 + 2, sy = cyc * 4 + 2;
                    if (v < 0.3f && Hash(cx, cyc, 21) < 0.45f && (x - sx) * (x - sx) + (y - sy) * (y - sy) < 0.7f) b = Rgb(0.08f, 0.08f, 0.1f);
                    break;
                }
            case "Swordfish":
                b = Mix(b, Rgb(0.35f, 0.45f, 0.75f), 0.25f * (1 - Smooth(0.0f, 0.1f, Math.Abs(v - 0.45f))));
                break;
            case "Golden Koi":
                {
                    float n = Fbm(x * 0.09f, y * 0.12f, 77);
                    if (n > 0.55f) b = Mix(b, Rgb(1f, 0.98f, 0.94f), Smooth(0.55f, 0.6f, n));
                    else if (n < 0.24f && v < 0.6f) b = Mix(b, Rgb(0.95f, 0.25f, 0.05f), 0.6f);
                    b = Mul(b, 1.08f);
                    break;
                }
            case "Clownfish":
                {
                    float[] bc = { 0.13f, 0.5f, 0.8f }, bw = { 0.04f, 0.065f, 0.05f };
                    for (int i = 0; i < 3; i++)
                    {
                        float tc = bc[i] + (i == 2 ? 0.035f : 0.02f) * (float)Math.Sin(v * Math.PI);   // bands bow toward the head
                        float dd = Math.Abs(t - tc);
                        if (dd < bw[i]) b = Rgb(0.99f, 0.99f, 0.97f);
                        else if (dd < bw[i] + 0.018f) b = Rgb(0.05f, 0.04f, 0.04f);
                    }
                    break;
                }
            case "Catfish":
                {
                    int cx = (int)Math.Floor(x / 4), cyc = (int)Math.Floor(y / 4);
                    float sx = cx * 4 + 1 + Hash(cx, cyc, 4) * 2, sy = cyc * 4 + 1 + Hash(cx, cyc, 8) * 2;
                    if (v < 0.55f && Hash(cx, cyc, 17) < 0.5f && (x - sx) * (x - sx) + (y - sy) * (y - sy) < 1.1f) b = Mix(b, Rgb(0.15f, 0.13f, 0.1f), 0.7f);
                    b = Mul(b, 0.9f + 0.2f * Fbm(x * 0.2f, y * 0.2f, 61));
                    break;
                }
            case "Ghost Fish":
                {
                    if (Math.Abs(y - s.cy) < 0.6f && t > 0.03f && t < 0.8f) b = Mix(b, Rgb(0.3f, 0.65f, 0.7f), 0.6f);          // spine
                    float rib = (t * 22f) % 1f;
                    if (rib < 0.18f && t > 0.15f && t < 0.72f && Math.Abs(y - s.cy) < s.bh * 0.55f * Profile(s, t)) b = Mix(b, Rgb(0.4f, 0.75f, 0.8f), 0.45f);
                    if (Fbm(x * 0.3f, y * 0.3f, 91) > 0.66f) b = Mix(b, Rgb(1f, 1f, 1f), 0.5f);
                    break;
                }
        }
        return b;
    }

    // ---------------------------------------------------------------- Moon Eel
    static C SampleEel(float x, float y)
    {
        float x0 = 4, x1 = 124;
        float t = (x - x0) / (x1 - x0);          // 0 tail tip .. 1 head
        float cy = 32 + 5f * (float)Math.Sin(t * Math.PI * 2.2 + 0.6) * (1 - t * 0.6f);
        float half = 0;
        if (t >= 0 && t <= 1)
        {
            half = 6.5f * (float)Math.Pow(Math.Min(1, t / 0.55f), 0.75);
            if (t > 0.9f) half *= (float)Math.Sqrt(Math.Max(0, 1 - Math.Pow((t - 0.9f) / 0.1f, 2)));
        }
        float dy = y - cy;
        float d = Math.Abs(dy) - half;                      // <0 inside
        if (t < 0) d = Math.Max(d, (x0 - x));
        if (t > 1) d = Math.Max(d, (x - x1));
        C col = Clear;
        // glow
        float gd = Math.Max(0, d);
        float glow = 0.55f * (float)Math.Exp(-gd / 3.5f);
        col = new C(0.72f, 0.58f, 1f, glow);
        // fringe fin (top + bottom) along the back half
        if (t > 0.04f && t < 0.82f && d > -0.5f && d < 2.6f * Math.Min(1, t / 0.1f))
        {
            C f = Rgb(0.8f, 0.68f, 1f); f.a = 0.75f;
            if (x % 2.5f < 0.7f) f = Mul(f, 0.85f);
            col = Over(f, col);
        }
        if (d < 0 && t >= 0 && t <= 1)
        {
            float v = (dy + half) / Math.Max(0.01f, 2 * half);
            C b = Mix(Rgb(0.7f, 0.62f, 0.95f), Rgb(0.95f, 0.93f, 1f), Smooth(0.3f, 0.85f, v));
            float spot = Fbm(x * 0.35f, y * 0.35f, 31);
            if (spot > 0.62f && v < 0.6f) b = Mix(b, Rgb(0.98f, 0.95f, 1f), 0.7f);   // moon-white speckles
            b = Mul(b, 1.05f - 0.2f * Math.Abs(v - 0.35f));
            if (d > -1.0f) b = Mul(b, 0.6f);
            // eye + mouth
            float ex = x1 - 7, ey = cy - half * 0.3f;
            float ed = (float)Math.Sqrt((x - ex) * (x - ex) + (y - ey) * (y - ey));
            if (ed < 2.3f) b = ed < 1.3f ? Rgb(0.05f, 0.02f, 0.12f) : Rgb(0.55f, 0.95f, 1f);
            if (x > x1 - 5 && Math.Abs(y - (cy + half * 0.25f)) < 0.5f) b = Rgb(0.3f, 0.2f, 0.4f);
            b.a = 1;
            col = Over(b, col);
        }
        return col;
    }

    // ---------------------------------------------------------------- Old Boot
    static bool InRect(float x, float y, float x0, float y0, float x1, float y1) { return x >= x0 && x <= x1 && y >= y0 && y <= y1; }
    static C SampleBoot(float x, float y)
    {
        C leather = Rgb(0.46f, 0.29f, 0.14f), dark = Rgb(0.2f, 0.13f, 0.07f), sole = Rgb(0.16f, 0.11f, 0.08f);
        C col = Clear;
        // shaft leans slightly back
        float lean = (44 - y) * 0.12f;
        bool shaft = InRect(x + lean, y, 30, 6, 62, 44);
        // foot: from heel to a rounded toe
        bool foot = InRect(x, y, 30, 34, 100, 54);
        float tx = (x - 100) / 14f, ty = (y - 45) / 9.5f;
        bool toe = tx * tx + ty * ty < 1 && x >= 100;
        // instep curve between shaft front and foot top
        float ix = (x - 62) / 38f, iy = (y - 34) / 6f;
        bool instep = x > 58 && x < 100 && y > 34 - 6 * (1 - Clamp01(ix)) && y <= 36;
        bool body = shaft || foot || toe || instep;
        bool soleR = InRect(x, y, 27, 53, 112, 58) && !(x > 108 && y > 55);
        bool heel = InRect(x, y, 29, 56, 48, 61);
        if (soleR || heel)
        {
            C s = sole; if (heel && ((int)x % 4 == 0)) s = Mul(s, 0.7f);
            col = s;
        }
        if (body && !(soleR || heel))
        {
            C b = leather;
            float n = Fbm(x * 0.2f, y * 0.2f, 13);
            b = Mul(b, 0.8f + 0.4f * n);
            if (Fbm(x * 0.5f, y * 0.5f, 3) > 0.7f) b = Mix(b, Rgb(0.62f, 0.48f, 0.3f), 0.5f);   // scuffs
            if (Fbm(x * 0.15f + 9, y * 0.15f, 41) > 0.68f && y > 38) b = Mix(b, Rgb(0.25f, 0.45f, 0.15f), 0.6f); // algae
            // toe cap seam + stitches
            float sx = (x - 92) / 18f, sy = (y - 45) / 11f;
            float sr = sx * sx + sy * sy;
            if (Math.Abs(sr - 1) < 0.08f && x > 80) b = Mul(b, 0.6f);
            if (Math.Abs(y - 51) < 0.6f && (int)x % 3 == 0) b = Rgb(0.78f, 0.7f, 0.5f);
            // laces along the shaft front
            float lx = 58 - lean * 0.5f;
            for (int i = 0; i < 6; i++)
            {
                float ly = 12 + i * 5;
                if (Math.Abs(y - ly - (x - lx) * 0.35f) < 0.8f && x > lx - 6 && x < lx + 4) b = Rgb(0.85f, 0.8f, 0.65f);
                if (Math.Abs(y - ly - (lx - x) * 0.35f) < 0.8f && x > lx - 6 && x < lx + 4) b = Rgb(0.8f, 0.74f, 0.58f);
                float hx = x - (lx + 4), hy = y - ly;
                if (hx * hx + hy * hy < 1.4f) b = Rgb(0.7f, 0.7f, 0.68f);  // eyelets
            }
            // outline: sample neighbours
            bool edge = false;
            for (int k = 0; k < 4 && !edge; k++)
            {
                float ox = k == 0 ? 1.2f : k == 1 ? -1.2f : 0, oy = k == 2 ? 1.2f : k == 3 ? -1.2f : 0;
                float xx = x + ox, yy = y + oy; float ll = (44 - yy) * 0.12f;
                float tx2 = (xx - 100) / 14f, ty2 = (yy - 45) / 9.5f;
                float ix2 = (xx - 62) / 38f;
                bool inn = InRect(xx + ll, yy, 30, 6, 62, 44) || InRect(xx, yy, 30, 34, 100, 54) || (tx2 * tx2 + ty2 * ty2 < 1 && xx >= 100)
                           || (xx > 58 && xx < 100 && yy > 34 - 6 * (1 - Clamp01(ix2)) && yy <= 36) || InRect(xx, yy, 27, 53, 112, 58);
                if (!inn) edge = true;
            }
            if (edge) b = Mul(b, 0.45f);
            col = b;
        }
        // shaft opening (dark ellipse at the top)
        float ox2 = (x + lean - 46) / 16f, oy2 = (y - 7) / 2.6f;
        if (ox2 * ox2 + oy2 * oy2 < 1) col = Rgb(0.1f, 0.06f, 0.03f);
        // a drip of water
        float dx = x - 104, dy = y - 61;
        if (dx * dx + dy * dy * 0.5f < 2.2f) col = new C(0.5f, 0.75f, 1f, 0.8f);
        return col;
    }

    // ---------------------------------------------------------------- Pufferfish (round, spiky, big eyes)
    static float Ell(float x, float y, float cx, float cy, float rx, float ry)   // approx signed distance, <0 inside
    {
        float nx = (x - cx) / rx, ny = (y - cy) / ry;
        return ((float)Math.Sqrt(nx * nx + ny * ny) - 1) * Math.Min(rx, ry);
    }
    static C SamplePuffer(float x, float y)
    {
        const float cx = 64, cy = 33, rx = 24, ry = 21;
        C col = Clear;
        float d = Ell(x, y, cx, cy, rx, ry);
        // tail fan (behind)
        if (x >= 27 && x <= 44)
        {
            float k = (44 - x) / 17f, spread = Lerp(4, 11, k);
            float dy = Math.Abs(y - cy);
            if (dy < spread && !(k > 0.7f && dy < 2.5f * (k - 0.7f) / 0.3f))
            {
                C f = Rgb(0.88f, 0.74f, 0.36f); f.a = 0.85f;
                if ((x * 0.6f + dy * 0.6f) % 3f < 0.8f) f = Mul(f, 0.8f);
                if (spread - dy < 1.1f || x < 28.2f) f = Mul(f, 0.55f);
                col = Over(f, col);
            }
        }
        // small dorsal + anal fins near the tail
        float df = Ell(x, y, 48, 14, 5, 4), af = Ell(x, y, 48, 52, 5, 4);
        if (df < 0 || af < 0) { C f = Rgb(0.9f, 0.78f, 0.42f); f.a = 0.8f; col = Over(f, col); }
        // spikes
        float ang = (float)Math.Atan2((y - cy) / ry, (x - cx) / rx);
        float frac = (float)(((ang / (2 * Math.PI)) * 26 % 1 + 1) % 1);
        float tri = 1 - Math.Abs(frac - 0.5f) * 2;
        bool nearTail = Math.Abs(Math.Abs(ang) - Math.PI) < 0.35f;
        if (d >= -0.5f && !nearTail && d < 5.5f * (tri - 0.45f) / 0.55f)
        {
            C sp = Mix(Rgb(0.7f, 0.58f, 0.3f), Rgb(0.95f, 0.9f, 0.75f), Clamp01(d / 5.5f));
            if (d > 5.5f * (tri - 0.45f) / 0.55f - 0.9f) sp = Mul(sp, 0.55f);
            col = Over(sp, col);
        }
        if (d < 0)
        {
            float v = (y - (cy - ry)) / (2 * ry);
            C b = Mix(Rgb(0.82f, 0.68f, 0.3f), Rgb(0.98f, 0.95f, 0.82f), Smooth(0.45f, 0.75f, v));
            int gx = (int)Math.Floor(x / 5), gy = (int)Math.Floor(y / 5);
            float sx = gx * 5 + 1.5f + Hash(gx, gy, 2) * 2, sy = gy * 5 + 1.5f + Hash(gx, gy, 6) * 2;
            if (v < 0.55f && Hash(gx, gy, 12) < 0.55f && (x - sx) * (x - sx) + (y - sy) * (y - sy) < 1.6f) b = Rgb(0.35f, 0.26f, 0.12f);
            b = Mul(b, 1.1f - 0.25f * Math.Abs(v - 0.3f));
            // shading toward the rim (round look)
            b = Mul(b, 0.8f + 0.2f * Clamp01(-d / 6f));
            if (d > -1.1f) b = Mul(b, 0.5f);
            // pectoral fin
            float px = (x - 70) / 5f, py = (y - 38) / 3f;
            if (px * px + py * py < 1) b = Mix(b, Rgb(0.95f, 0.85f, 0.5f), 0.6f);
            // mouth (small pursed 'o' at the front)
            float mx = (x - 86.5f) / 1.8f, my = (y - 37) / 1.4f;
            if (mx * mx + my * my < 1) b = Rgb(0.45f, 0.18f, 0.15f);
            // big eye
            float ed = (float)Math.Sqrt((x - 75) * (x - 75) + (y - 24) * (y - 24));
            if (ed < 7f)
            {
                b = ed > 6.2f ? Rgb(0.2f, 0.15f, 0.08f) : ed > 4.5f ? Rgb(0.98f, 0.97f, 0.92f) : ed > 2.4f ? Rgb(0.15f, 0.45f, 0.35f) : Rgb(0.02f, 0.02f, 0.03f);
                float hx = x - 76.5f, hy = y - 22.3f;
                if (hx * hx + hy * hy < 1.5f) b = Rgb(1, 1, 1);
            }
            b.a = 1;
            col = Over(b, col);
        }
        return col;
    }

    // ---------------------------------------------------------------- Anglerfish (huge jaw, teeth, glowing lure)
    static C SampleAngler(float x, float y)
    {
        C col = Clear;
        // body = big head + tapering rear + protruding lower jaw
        float d = Math.Min(Ell(x, y, 70, 36, 32, 21), Ell(x, y, 40, 37, 24, 13));
        d = Math.Min(d, Ell(x, y, 92, 45, 19, 7));
        // lure stalk/bulb positions
        const float bx = 106, by = 8;
        // tail fan
        if (x >= 6 && x <= 22)
        {
            float k = (22 - x) / 16f, spread = Lerp(5, 13, (float)Math.Pow(k, 0.8));
            float dy = Math.Abs(y - 37);
            if (dy < spread)
            {
                C f = Rgb(0.2f, 0.13f, 0.1f); f.a = 0.9f;
                if ((x * 0.5f + dy * 0.7f) % 3f < 0.8f) f = Mul(f, 0.7f);
                if (spread - dy < 1.1f || x < 7.2f) f = Mul(f, 0.5f);
                col = Over(f, col);
            }
        }
        // dorsal ridge fin
        if (x > 34 && x < 52) { float top = 37 - 13 * (float)Math.Sqrt(Math.Max(0, 1 - Math.Pow((x - 40) / 24.0, 2))); float h = 6 * (float)Math.Sin((x - 34) / 18 * Math.PI); if (y < top + 1 && y > top - h) { C f = Rgb(0.18f, 0.12f, 0.09f); f.a = 0.85f; col = Over(f, col); } }
        if (d < 0)
        {
            float v = (y - 15) / 42f;
            C b = Mix(Rgb(0.2f, 0.13f, 0.09f), Rgb(0.33f, 0.23f, 0.17f), Smooth(0.4f, 0.9f, v));
            b = Mul(b, 0.75f + 0.45f * Fbm(x * 0.18f, y * 0.18f, 53));
            if (Fbm(x * 0.45f, y * 0.45f, 29) > 0.68f) b = Mix(b, Rgb(0.08f, 0.05f, 0.05f), 0.6f);   // warty blotches
            if (d > -1.2f) b = Rgb(0.04f, 0.03f, 0.03f);
            // gaping mouth: wedge from the hinge between the upper jaw line and the lower jaw line
            const float hx = 80, hy = 39;
            float uy = hy - (x - hx) * 0.36f;            // upper jaw edge
            float ly = hy + (x - hx) * 0.38f;            // lower jaw edge
            if (x > hx && y > uy && y < ly && d < -1.2f)
            {
                b = Mix(Rgb(0.32f, 0.05f, 0.07f), Rgb(0.08f, 0.01f, 0.02f), Clamp01((x - hx) / 10f) * 0.6f + Clamp01(1 - (x - hx) / 25f) * 0.4f);
                float per = (x % 4.5f) / 4.5f, tri = 1 - Math.Abs(per - 0.5f) * 2;
                float tl = 4.5f * tri * Clamp01((x - hx - 3) / 6f);
                if (y - uy < tl || ly - y < tl * 1.1f) b = Mix(Rgb(0.98f, 0.97f, 0.88f), Rgb(0.75f, 0.72f, 0.6f), Clamp01(Math.Min(y - uy, ly - y) / Math.Max(0.1f, tl)));
            }
            else if (x > hx && (Math.Abs(y - uy) < 0.9f || Math.Abs(y - ly) < 0.9f)) b = Rgb(0.04f, 0.03f, 0.03f);   // lips
            // small pale eye
            float ed = (float)Math.Sqrt((x - 84) * (x - 84) + (y - 25) * (y - 25));
            if (ed < 3f) b = ed < 1.5f ? Rgb(0.02f, 0.02f, 0.02f) : Rgb(0.8f, 0.85f, 0.7f);
            // pectoral fin
            float px = (x - 58) / 7f, py = (y - 44) / 3.5f;
            if (px * px + py * py < 1) b = Mix(b, Rgb(0.4f, 0.28f, 0.2f), 0.6f);
            b.a = 1;
            col = Over(b, col);
        }
        // illicium (stalk) from the forehead to the lure
        float st; float sd = BezierDist(x, y, 74, 16, 86, -4, bx, by - 1, out st);
        if (sd < Lerp(1.1f, 0.6f, st)) col = Over(Rgb(0.3f, 0.2f, 0.14f), col);
        // lure glow + bulb
        float ld = (float)Math.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by));
        C g = Rgb(0.25f, 0.95f, 1f); g.a = 0.75f * (float)Math.Exp(-Math.Max(0, ld - 3.2f) / 3.5f);
        col = Over(g, col);
        if (ld < 3.4f) col = Over(Mix(Rgb(0.95f, 1f, 1f), Rgb(0.3f, 0.95f, 1f), Smooth(0.8f, 3.4f, ld)), col);
        return col;
    }

    // ---------------------------------------------------------------- render    /// RGBA floats (straight alpha), row 0 = top, 3x3 supersampled.
    public static float[] Render(string sp)
    {
        var o = new float[W * H * 4];
        Spec s = Get(sp);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 3; i++)
                    {
                        float fx = x + (i + 0.5f) / 3f, fy = y + (j + 0.5f) / 3f;
                        C c = s.kind == 1 ? SampleEel(fx, fy) : s.kind == 2 ? SampleBoot(fx, fy) : s.kind == 3 ? SamplePuffer(fx, fy) : s.kind == 4 ? SampleAngler(fx, fy) : SampleFish(s, sp, fx, fy);
                        r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                    }
                int p = (y * W + x) * 4;
                if (a > 0.0001f) { o[p] = Clamp01(r / a); o[p + 1] = Clamp01(g / a); o[p + 2] = Clamp01(b / a); }
                o[p + 3] = Clamp01(a / 9f);
            }
        return o;
    }

    /// RGBA bytes, row 0 = BOTTOM (Unity Texture2D.SetPixels32 / LoadRawTextureData order).
    public static byte[] RenderBytesBottomUp(string sp)
    {
        float[] f = Render(sp);
        var o = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int src = (y * W + x) * 4, dst = ((H - 1 - y) * W + x) * 4;
                for (int k = 0; k < 4; k++) o[dst + k] = (byte)(f[src + k] * 255f + 0.5f);
            }
        return o;
    }
}

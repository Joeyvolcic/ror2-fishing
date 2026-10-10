// Standalone preview: renders FishArt textures to PNGs (+ a 4x contact sheet on a water-blue background).
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static class FishPreview
{
    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "fish_previews";
        Directory.CreateDirectory(dir);
        int sc = 3;
        using (var sheet = new Bitmap(FishArt.W * sc * 3 + 40, (FishArt.H * sc + 30) * ((FishArt.Species.Length + 2) / 3) + 10, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(255, 30, 90, 150));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            int idx = 0;
            foreach (string sp in FishArt.Species)
            {
                float[] f = FishArt.Render(sp);
                using (var bmp = new Bitmap(FishArt.W, FishArt.H, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < FishArt.H; y++)
                        for (int x = 0; x < FishArt.W; x++)
                        {
                            int p = (y * FishArt.W + x) * 4;
                            bmp.SetPixel(x, y, Color.FromArgb((int)(f[p + 3] * 255 + .5f), (int)(f[p] * 255 + .5f), (int)(f[p + 1] * 255 + .5f), (int)(f[p + 2] * 255 + .5f)));
                        }
                    bmp.Save(Path.Combine(dir, sp + ".png"), ImageFormat.Png);
                    int cx = 10 + (idx % 3) * (FishArt.W * sc + 10), cy = 5 + (idx / 3) * (FishArt.H * sc + 30);
                    g.DrawImage(bmp, new Rectangle(cx, cy, FishArt.W * sc, FishArt.H * sc));
                    g.DrawString(sp, new Font("Segoe UI", 11, FontStyle.Bold), Brushes.White, cx, cy + FishArt.H * sc + 2);
                }
                Console.WriteLine("wrote " + sp);
                idx++;
            }
            sheet.Save(Path.Combine(dir, "_all_fish_sheet.png"), ImageFormat.Png);
        }
        return 0;
    }
}

// Generates maroom.ico (16–256 px) and a preview maroom-256.png.
// © 2026 Timo Dörsam
// Motif: two overlapping "rooms" (windows) on a blue background, with a wave below
// (Marea = tide). Small sizes without wave and details so they stay sharp.
// Build and run manually: see README (the generated maroom.ico is checked in).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGenerator
{
    static void Main(string[] args)
    {
        string target = args.Length > 0 ? args[0] : ".";
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        byte[][] pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (Bitmap b = Draw(sizes[i]))
            {
                if (sizes[i] == 256)
                {
                    using (MemoryStream m = new MemoryStream())
                    {
                        b.Save(m, ImageFormat.Png);
                        pngs[i] = m.ToArray();
                    }
                    File.WriteAllBytes(Path.Combine(target, "maroom-256.png"), pngs[i]);
                }
                else pngs[i] = Dib(b);
            }
        }
        // ICO: 256 px as PNG, smaller sizes as classic 32-bit bitmaps (System.Drawing.Icon cannot read PNG entries)
        using (FileStream f = File.Create(Path.Combine(target, "maroom.ico")))
        using (BinaryWriter w = new BinaryWriter(f))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] p in pngs) w.Write(p);
        }
    }

    // Icon bitmap: BITMAPINFOHEADER (double height), BGRA bottom-up, empty AND mask
    static byte[] Dib(Bitmap b)
    {
        int s = b.Width;
        int maskRow = ((s + 31) / 32) * 4;
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskRow * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    Color c = b.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            w.Write(new byte[maskRow * s]);
            return m.ToArray();
        }
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static RectangleF Rect(int s, float x1, float y1, float x2, float y2)
    {
        return new RectangleF(x1 * s, y1 * s, (x2 - x1) * s, (y2 - y1) * s);
    }

    static Bitmap Draw(int s)
    {
        Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        bool small = s <= 24;
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // background: rounded square, blue gradient
            RectangleF background = Rect(s, 0.03f, 0.03f, 0.97f, 0.97f);
            using (GraphicsPath p = RoundRect(background, s * 0.22f))
            using (LinearGradientBrush br = new LinearGradientBrush(background, Color.FromArgb(56, 152, 236), Color.FromArgb(10, 72, 150), 45f))
                g.FillPath(br, p);

            float rad = s * (small ? 0.06f : 0.07f);
            float y0 = small ? 0.20f : 0.17f;
            // back room
            RectangleF back = small ? Rect(s, 0.16f, y0, 0.66f, 0.62f) : Rect(s, 0.17f, y0, 0.67f, 0.55f);
            using (GraphicsPath p = RoundRect(back, rad))
            using (Brush br = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                g.FillPath(br, p);
            // front room
            RectangleF front = small ? Rect(s, 0.34f, 0.38f, 0.84f, 0.80f) : Rect(s, 0.33f, 0.31f, 0.83f, 0.69f);
            using (GraphicsPath p = RoundRect(front, rad))
            {
                g.FillPath(Brushes.White, p);
                if (!small)
                {
                    // title bar of the front room
                    using (Brush bar = new SolidBrush(Color.FromArgb(0, 120, 212)))
                    {
                        g.SetClip(p);
                        g.FillRectangle(bar, front.X, front.Y, front.Width, front.Height * 0.2f);
                        g.ResetClip();
                    }
                }
            }

            // wave (Marea)
            if (!small)
            {
                PointF[] points = new PointF[41];
                for (int i = 0; i <= 40; i++)
                {
                    float x = 0.15f + 0.70f * i / 40f;
                    float y = 0.82f + 0.035f * (float)Math.Sin(i / 40f * Math.PI * 3);
                    points[i] = new PointF(x * s, y * s);
                }
                using (Pen pen = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1.5f, s * 0.05f)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawCurve(pen, points, 0.5f);
                }
            }
        }
        return b;
    }
}

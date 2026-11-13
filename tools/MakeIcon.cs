// 生成 Windows/app.ico：青紫渐变圆底 + 白色麦克风。用法：MakeIcon.exe <输出.ico>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    private static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float pad = size * 0.04f;
            RectangleF circle = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            using (LinearGradientBrush back = new LinearGradientBrush(circle, Color.FromArgb(34, 211, 238), Color.FromArgb(139, 92, 246), 45f))
                g.FillEllipse(back, circle);
            using (Pen rim = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, size / 48f)))
                g.DrawEllipse(rim, circle.X + 0.5f, circle.Y + 0.5f, circle.Width - 1, circle.Height - 1);
            // 麦克风：胶囊 + U 形支架 + 立杆 + 底座
            float c = size / 2f;
            float capW = size * 0.24f, capH = size * 0.38f, capTop = size * 0.18f;
            float stroke = Math.Max(1.6f, size * 0.075f);
            using (GraphicsPath capsule = new GraphicsPath())
            {
                capsule.AddArc(c - capW / 2, capTop, capW, capW, 180, 180);
                capsule.AddArc(c - capW / 2, capTop + capH - capW, capW, capW, 0, 180);
                capsule.CloseFigure();
                g.FillPath(Brushes.White, capsule);
            }
            using (Pen pen = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float arcW = size * 0.42f, arcTop = capTop + capH * 0.35f, arcH = capH * 0.78f;
                g.DrawArc(pen, c - arcW / 2, arcTop, arcW, arcH, 0, 180);
                float stemTop = arcTop + arcH, stemBottom = size * 0.80f;
                g.DrawLine(pen, c, stemTop, c, stemBottom);
                g.DrawLine(pen, c - size * 0.13f, stemBottom, c + size * 0.13f, stemBottom);
            }
        }
        return bmp;
    }
    private static byte[] Dib(Bitmap bmp)
    {
        int size = bmp.Width;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                { Color p = bmp.GetPixel(x, y); w.Write(p.B); w.Write(p.G); w.Write(p.R); w.Write(p.A); }
            int maskRow = ((size + 31) / 32) * 4;
            for (int y = size - 1; y >= 0; y--)
            {
                byte[] row = new byte[maskRow];
                for (int x = 0; x < size; x++) if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
            return ms.ToArray();
        }
    }
    private static int Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int s in sizes)
            using (Bitmap bmp = Draw(s))
            {
                if (s >= 128) { using (MemoryStream png = new MemoryStream()) { bmp.Save(png, ImageFormat.Png); images.Add(png.ToArray()); } }
                else images.Add(Dib(bmp));
                if (s == 256) bmp.Save(Path.ChangeExtension(args[0], ".png"), ImageFormat.Png);
            }
        using (FileStream file = File.Create(args[0]))
        using (BinaryWriter w = new BinaryWriter(file))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset); offset += images[i].Length;
            }
            foreach (byte[] image in images) w.Write(image);
        }
        return 0;
    }
}

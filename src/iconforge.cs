// iconforge.cs —— 生成多尺寸 app.ico（16/32/48 用 DIB，256 用 PNG）
// 编译: csc /out:iconforge.exe iconforge.cs
// 用法: iconforge.exe <输出.ico>
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

static class IconForge
{
    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        float d = rad * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int px)
    {
        var b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float pad = px * 0.04f;
            var box = new RectangleF(pad, pad, px - pad * 2, px - pad * 2);
            float rad = px * 0.24f;

            using (var path = RoundRect(box, rad))
            using (var br = new LinearGradientBrush(box,
                       Color.FromArgb(47, 129, 247), Color.FromArgb(10, 22, 40), 45f))
                g.FillPath(br, path);

            // 内描边，让小尺寸也有轮廓
            float inPad = px * 0.09f;
            var inner = new RectangleF(box.X + inPad, box.Y + inPad,
                                       box.Width - inPad * 2, box.Height - inPad * 2);
            using (var path = RoundRect(inner, rad * 0.78f))
            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1f, px * 0.03f)))
                g.DrawPath(pen, path);

            // 居中的 "8"（小米8）
            using (var f = new Font("Segoe UI", px * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("8", f, Brushes.White, box, sf);
            }
        }
        return b;
    }

    // 32bpp DIB：BITMAPINFOHEADER + BGRA(自底向上) + AND 掩码
    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int xorSize = w * h * 4;
        int maskStride = ((w + 31) / 32) * 4;
        int maskSize = maskStride * h;

        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);

        bw.Write(40);                 // biSize
        bw.Write(w);                  // biWidth
        bw.Write(h * 2);              // biHeight (XOR + AND)
        bw.Write((short)1);           // biPlanes
        bw.Write((short)32);          // biBitCount
        bw.Write(0);                  // biCompression = BI_RGB
        bw.Write(xorSize + maskSize); // biSizeImage
        bw.Write(0); bw.Write(0);     // biXPelsPerMeter / biYPelsPerMeter
        bw.Write(0);                  // biClrUsed
        bw.Write(0);                  // biClrImportant

        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[w * 4];
            bool topDown = data.Stride < 0;
            int strideAbs = Math.Abs(data.Stride);
            byte[] all = new byte[strideAbs * h];
            Marshal.Copy(data.Scan0, all, 0, all.Length);

            for (int y = 0; y < h; y++)
            {
                int srcY = topDown ? y : (h - 1 - y);   // 输出自底向上
                Array.Copy(all, srcY * strideAbs, row, 0, w * 4);
                for (int x = 0; x < w; x++)             // BGRA
                {
                    byte r = row[x * 4 + 2], gg = row[x * 4 + 1];
                    byte bl = row[x * 4 + 0], a = row[x * 4 + 3];
                    bw.Write(bl); bw.Write(gg); bw.Write(r); bw.Write(a);
                }
            }
            // AND 掩码全 0：透明度完全由 alpha 决定
            for (int i = 0; i < maskSize; i++) bw.Write((byte)0);
        }
        finally { bmp.UnlockBits(data); }

        bw.Flush();
        return ms.ToArray();
    }

    static byte[] ToPng(Bitmap bmp)
    {
        var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static byte[] BuildIco(byte[][] payloads, int[] sizes)
    {
        int n = payloads.Length;
        int headerSize = 6 + 16 * n;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((short)0);          // reserved
        bw.Write((short)1);          // type = icon
        bw.Write((short)n);
        int off = headerSize;
        for (int i = 0; i < n; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)0);       // colors
            bw.Write((byte)0);       // reserved
            bw.Write((short)1);      // planes
            bw.Write((short)32);     // bpp
            bw.Write(payloads[i].Length);
            bw.Write(off);
            off += payloads[i].Length;
        }
        for (int i = 0; i < n; i++) bw.Write(payloads[i]);
        bw.Flush();
        return ms.ToArray();
    }

    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = { 16, 32, 48, 256 };
        var payloads = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (var bmp = Draw(sizes[i]))
                payloads[i] = (sizes[i] >= 256) ? ToPng(bmp) : ToDib(bmp);
        }
        File.WriteAllBytes(outPath, BuildIco(payloads, sizes));
        Console.WriteLine("OK " + outPath + " (" + new FileInfo(outPath).Length + " bytes)");
    }
}

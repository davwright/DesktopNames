using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Generates DesktopNames branding:
//   ../logo-256.png, logo-512.png, logo-1024.png
//   ../../DesktopNames/app.ico  (multi-resolution: 16, 24, 32, 48, 64, 128, 256)
//
// The mark: three stacked rounded tiles on a dark squircle backdrop, with the
// middle tile in the Win11 accent blue plus a white underline bar — echoing
// the in-app "active desktop" indicator.

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;

    float s = size / 256f;
    RectangleF R(float x, float y, float w, float h) => new(x * s, y * s, w * s, h * s);
    float U(float v) => v * s;

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Backdrop squircle
    using (var path = RoundRect(R(8, 8, 240, 240), U(48)))
    using (var brush = new SolidBrush(Color.FromArgb(0x1F, 0x24, 0x30)))
        g.FillPath(brush, path);

    // Tile color palette
    var inactiveFill = Color.FromArgb(0x3A, 0x41, 0x51);
    var accent = Color.FromArgb(0x2A, 0x6D, 0xF4);
    var dimText = Color.FromArgb(0x7F, 0x8A, 0xA0);
    var dimTextQuiet = Color.FromArgb(0x56, 0x5E, 0x72);
    var white = Color.White;
    var lightBlue = Color.FromArgb(0xC7, 0xD6, 0xFF);

    // Left tile
    using (var path = RoundRect(R(36, 80, 56, 96), U(12)))
    using (var brush = new SolidBrush(inactiveFill))
        g.FillPath(brush, path);
    using (var path = RoundRect(R(48, 96, 32, 6), U(3))) using (var br = new SolidBrush(dimText)) g.FillPath(br, path);
    using (var path = RoundRect(R(48, 110, 24, 6), U(3))) using (var br = new SolidBrush(dimTextQuiet)) g.FillPath(br, path);

    // Middle tile (active) — slightly taller
    using (var path = RoundRect(R(100, 64, 56, 112), U(12)))
    using (var brush = new SolidBrush(accent))
        g.FillPath(brush, path);
    using (var path = RoundRect(R(112, 82, 32, 6), U(3))) using (var br = new SolidBrush(white)) g.FillPath(br, path);
    using (var path = RoundRect(R(112, 96, 24, 6), U(3))) using (var br = new SolidBrush(lightBlue)) g.FillPath(br, path);
    // underline bar
    using (var path = RoundRect(R(114, 160, 28, 6), U(3))) using (var br = new SolidBrush(white)) g.FillPath(br, path);

    // Right tile
    using (var path = RoundRect(R(164, 80, 56, 96), U(12)))
    using (var brush = new SolidBrush(inactiveFill))
        g.FillPath(brush, path);
    using (var path = RoundRect(R(176, 96, 32, 6), U(3))) using (var br = new SolidBrush(dimText)) g.FillPath(br, path);
    using (var path = RoundRect(R(176, 110, 24, 6), U(3))) using (var br = new SolidBrush(dimTextQuiet)) g.FillPath(br, path);

    // Taskbar ground line
    using (var path = RoundRect(R(36, 196, 184, 6), U(3)))
    using (var brush = new SolidBrush(Color.FromArgb(115, accent)))
        g.FillPath(brush, path);

    return bmp;
}

string brandDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string outDir = Path.Combine(brandDir);
string appDir = Path.GetFullPath(Path.Combine(brandDir, "..", "DesktopNames"));

foreach (int s in new[] { 256, 512, 1024 })
{
    using var bmp = Render(s);
    string path = Path.Combine(outDir, $"logo-{s}.png");
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine($"wrote {path}");
}

// Build a multi-resolution .ico by concatenating PNG-encoded frames.
int[] iconSizes = { 16, 24, 32, 48, 64, 128, 256 };
var frames = new List<byte[]>();
foreach (var sz in iconSizes)
{
    using var bmp = Render(sz);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    frames.Add(ms.ToArray());
}

string icoPath = Path.Combine(appDir, "app.ico");
using (var fs = File.Create(icoPath))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);          // reserved
    w.Write((ushort)1);          // type: 1 = ICO
    w.Write((ushort)iconSizes.Length);

    int headerSize = 6 + iconSizes.Length * 16;
    int offset = headerSize;

    for (int i = 0; i < iconSizes.Length; i++)
    {
        int sz = iconSizes[i];
        byte szByte = sz >= 256 ? (byte)0 : (byte)sz;
        w.Write(szByte);         // width
        w.Write(szByte);         // height
        w.Write((byte)0);        // color count (0 = ≥256)
        w.Write((byte)0);        // reserved
        w.Write((ushort)1);      // planes
        w.Write((ushort)32);     // bpp
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }
    foreach (var f in frames) w.Write(f);
}
Console.WriteLine($"wrote {icoPath}");

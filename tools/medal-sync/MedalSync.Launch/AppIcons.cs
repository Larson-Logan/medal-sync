using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MedalSync.Session;

namespace MedalSync.Launch;

/// <summary>
/// The Medal Sync icon (<c>medal-sync.ico</c>, drawn by <c>scripts/generate-icon.py</c>), and for the notification
/// area one copy per state with a small colored dot. The tooltip always names the state, so color is never the only cue.
/// </summary>
internal sealed class AppIcons : IDisposable
{
    private const string Resource = "MedalSync.Launch.medal-sync.ico";
    private readonly Dictionary<SessionPhase, Icon> _tray = [];

    /// <param name="traySize">The notification area's icon size; it grows with display scaling.</param>
    public AppIcons(Size? traySize = null)
    {
        Window = LoadApplicationIcon();
        var size = traySize ?? SystemInformation.SmallIconSize;
        using var small = new Icon(Window, size);
        using var plain = small.ToBitmap();
        foreach (var phase in Enum.GetValues<SessionPhase>()) _tray[phase] = WithDot(plain, size, DotColor(phase));
    }

    /// <summary>Every size of the icon, for window title bars and Alt+Tab.</summary>
    public Icon Window { get; }

    public Icon For(SessionPhase phase) => _tray.TryGetValue(phase, out var icon) ? icon : _tray[SessionPhase.Unknown];

    public static Icon LoadApplicationIcon()
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The embedded icon {Resource} is missing.");
        return new Icon(stream);
    }

    /// <summary>Green when it is safe to switch, red while recording, blue while working, gray while waiting, amber when attention is needed.</summary>
    internal static Color DotColor(SessionPhase phase) => phase switch
    {
        SessionPhase.Ready => Color.FromArgb(34, 197, 94),
        SessionPhase.Active => Color.FromArgb(239, 68, 68),
        SessionPhase.Starting or SessionPhase.Uploading or SessionPhase.Verifying => Color.FromArgb(56, 189, 248),
        SessionPhase.Waiting or SessionPhase.WaitingForStorage => Color.FromArgb(156, 163, 175),
        _ => Color.FromArgb(245, 158, 11),
    };

    private static Icon WithDot(Bitmap plain, Size size, Color color)
    {
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(plain, new Rectangle(Point.Empty, size));
            // About half the icon, in the bottom-right corner, with a white ring to separate it from the background.
            float diameter = MathF.Round(Math.Min(size.Width, size.Height) * 0.5f);
            float ring = MathF.Max(1, diameter / 8);
            var outer = new RectangleF(size.Width - diameter, size.Height - diameter, diameter, diameter);
            using (var white = new SolidBrush(Color.White)) graphics.FillEllipse(white, outer);
            outer.Inflate(-ring, -ring);
            using var fill = new SolidBrush(color);
            graphics.FillEllipse(fill, outer);
        }
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        try
        {
            pixels = new byte[size.Width * size.Height * 4];
            for (int row = 0; row < size.Height; row++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * size.Width * 4, size.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        // An icon built from its own file bytes owns its handle, so disposing it frees everything.
        using var stream = new MemoryStream(IconFile.FromPixels(size.Width, size.Height, pixels));
        return new Icon(stream);
    }

    public void Dispose()
    {
        foreach (var icon in _tray.Values) icon.Dispose();
        Window.Dispose();
    }
}

/// <summary>Writes a one-image .ico file. No Windows APIs, so it can be checked on any system.</summary>
internal static class IconFile
{
    /// <param name="bgra">Top-down rows of 32-bit pixels in blue, green, red, alpha order, as Windows bitmaps store them.</param>
    public static byte[] FromPixels(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width is < 1 or > 256 || height is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(width), "An icon image is 1 to 256 pixels wide and high.");
        if (bgra.Length != width * height * 4) throw new ArgumentException("Expected four bytes per pixel.", nameof(bgra));
        const int directory = 6 + 16;
        const int header = 40;
        int maskStride = (width + 31) / 32 * 4;
        int imageBytes = header + bgra.Length + maskStride * height;
        var file = new byte[directory + imageBytes];
        var span = file.AsSpan();
        // ICONDIR: reserved, type 1 (icon), one image.
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        // ICONDIRENTRY: 0 means 256; no palette; one plane; 32 bits per pixel.
        span[6] = (byte)(width == 256 ? 0 : width);
        span[7] = (byte)(height == 256 ? 0 : height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], directory);
        // BITMAPINFOHEADER: the height counts the color image and the mask together.
        var info = span[directory..];
        BinaryPrimitives.WriteInt32LittleEndian(info, header);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(info[20..], bgra.Length + maskStride * height);
        // Rows are stored bottom-up. The mask stays zero: the alpha channel decides transparency.
        int rowBytes = width * 4;
        for (int row = 0; row < height; row++)
            bgra.Slice((height - 1 - row) * rowBytes, rowBytes).CopyTo(info[(header + row * rowBytes)..]);
        return file;
    }
}

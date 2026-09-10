using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SyncOTP.App;

/// <summary>
/// Draws the tray icons at runtime instead of shipping .ico assets. Three states, distinguished by
/// colour so they read at 16x16: idle (blue), a code just arrived (green), paused or disconnected
/// (grey).
/// </summary>
public static partial class TrayIcons
{
    public static Icon Idle { get; } = Build(Color.FromArgb(0, 120, 212));
    public static Icon Active { get; } = Build(Color.FromArgb(16, 137, 62));
    public static Icon Paused { get; } = Build(Color.FromArgb(120, 120, 120));
    public static Icon Problem { get; } = Build(Color.FromArgb(196, 43, 28));

    // An update is a fifth thing to say with four colours already spoken for, so it is a corner
    // dot on the existing icon rather than a new colour. Cached, because every Build leaks a GDI
    // handle if it is called on every UpdateIcon.
    public static Icon IdleWithUpdate { get; } = Build(Color.FromArgb(0, 120, 212), badge: true);
    public static Icon PausedWithUpdate { get; } = Build(Color.FromArgb(120, 120, 120), badge: true);
    public static Icon ProblemWithUpdate { get; } = Build(Color.FromArgb(196, 43, 28), badge: true);

    /// <summary>The same icon with an update badge, for the four states the tray can be in.</summary>
    public static Icon WithUpdateBadge(Icon icon)
    {
        if (ReferenceEquals(icon, Paused)) return PausedWithUpdate;
        if (ReferenceEquals(icon, Problem)) return ProblemWithUpdate;
        return IdleWithUpdate;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    private static Icon Build(Color color, bool badge = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(color);
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            g.FillPath(background, path);

            // A dot-dot-dash motif standing in for a short numeric code.
            using var foreground = new SolidBrush(Color.White);
            g.FillEllipse(foreground, 7, 14, 5, 5);
            g.FillEllipse(foreground, 14, 14, 5, 5);
            g.FillRectangle(foreground, 21, 14, 5, 5);

            if (badge)
            {
                // Ringed in the background colour so it reads as a badge at 16x16 rather than as
                // part of the motif.
                using var ring = new SolidBrush(color);
                using var dot = new SolidBrush(Color.FromArgb(255, 185, 0));
                g.FillEllipse(ring, 17, -1, 16, 16);
                g.FillEllipse(dot, 20, 2, 10, 10);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives the HICON being destroyed below.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}

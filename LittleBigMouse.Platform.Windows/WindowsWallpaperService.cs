#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using HLab.Sys.Windows.API;
using LittleBigMouse.DisplayLayout.Monitors;
using LittleBigMouse.Plugins;

namespace LittleBigMouse.Platform.Windows;

/// <summary>
/// Writes per-screen wallpapers through the shell's IDesktopWallpaper COM API — the same
/// object WindowsLayoutMapping reads from. Screens are paired with the shell's monitor slots
/// by rectangle (LogicalBounds and GetMonitorRECT are both in desktop pixel coordinates), in
/// an order that follows how the shell paints those slots; see <see cref="WallpaperSlotOrder"/>.
/// Windows limitations mapped as-is: the fit position and the background color are
/// desktop-global, so the first image style and the first solid color win when screens disagree.
/// </summary>
public class WindowsWallpaperService : IWallpaperService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public Task ApplyAsync(IReadOnlyList<ScreenWallpaper> screens)
        => Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows() || screens.Count == 0) return;
            try
            {
                Apply(screens);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"IDesktopWallpaper apply failed: {e.Message}");
            }
        });

    [SupportedOSPlatform("windows")]
    static void Apply(IReadOnlyList<ScreenWallpaper> screens)
    {
        var com = (IDesktopWallpaper)new DesktopWallpaperClass();
        var desktop = new DesktopWallpaperAdapter(com);

        // File each image under the slot the shell will paint it with; its filing order and
        // its painting order are two different lists (see WallpaperSlotOrder).
        if (!ApplyPlan(desktop, screens)) return;

        var style = screens.FirstOrDefault(s => s.ImagePath != null)?.Style;
        if (style != null)
            com.SetPosition(Position(style.Value));

        var color = screens.FirstOrDefault(s => s.ImagePath == null && s.Color != null)?.Color;
        if (color is { } rgb)
        {
            var bytes = rgb.To<byte>();
            // COLORREF is 0x00BBGGRR.
            com.SetBackgroundColor((uint)(bytes.Red | bytes.Green << 8 | bytes.Blue << 16));
        }
    }

    /// <summary>
    /// Writes every screen according to the plan of
    /// <see cref="WallpaperSlotOrder.BuildWritePlan"/>. Returns false when nothing was written,
    /// either because the snapshot could not be read completely or because every write failed.
    /// </summary>
    internal static bool ApplyPlan(IWallpaperDesktop desktop, IReadOnlyList<ScreenWallpaper> screens)
    {
        if (!WallpaperSlotOrder.TryRead(desktop, out var slots))
        {
            // Observed as RPC_X_NULL_REF_POINTER (0x800706F4) from a non-elevated caller. The two
            // orders differ, so a guess would put every image one slot off: the layout is left as
            // it is until the next apply rather than written wrong.
            Console.Error.WriteLine(
                $"IDesktopWallpaper cannot enumerate its monitor slots; {screens.Count} screen(s) " +
                "left untouched.");
            return false;
        }

        var plan = WallpaperSlotOrder.BuildWritePlan(slots, screens);
        var applied = false;

        foreach (var write in plan.Slots)
        {
            // The image of the screen painted on this slot, filed under the slot's own path.
            if (desktop.SetImage(write.Filed.DevicePath, write.Screen.ImagePath ?? "")) applied = true;
        }

        foreach (var write in plan.Direct)
        {
            // SetWallpaper wants a display device interface path; the plan keeps only those.
            if (desktop.SetImage(write.DevicePath, write.Screen.ImagePath ?? "")) applied = true;
        }

        return applied;
    }

    static DesktopWallpaperPosition Position(WallpaperStyle style) => style switch
    {
        WallpaperStyle.Fit => DesktopWallpaperPosition.Fit,
        WallpaperStyle.Stretch => DesktopWallpaperPosition.Stretch,
        WallpaperStyle.Tile => DesktopWallpaperPosition.Tile,
        WallpaperStyle.Center => DesktopWallpaperPosition.Center,
        WallpaperStyle.Span => DesktopWallpaperPosition.Span,
        _ => DesktopWallpaperPosition.Fill,
    };
}

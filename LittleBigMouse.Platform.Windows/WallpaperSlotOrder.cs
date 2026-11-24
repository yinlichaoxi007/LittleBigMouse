#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HLab.Geo;
using HLab.Sys.Windows.API;
using LittleBigMouse.Plugins;

namespace LittleBigMouse.Platform.Windows;

/// <summary>
/// Windows keeps two different monitor orders for a span wallpaper: the one the shell paints
/// with (primary monitor first, then left to right) and the one <c>IDesktopWallpaper</c> files
/// images under (<c>GetMonitorDevicePathAt</c>, i.e. <c>TranscodedImageCache_00k</c>). They
/// differ as soon as the primary monitor is not the leftmost one, and the shell paints slot k
/// on the k-th monitor of the paint order, so every image lands one slot off.
/// <para>
/// The only lever is therefore to write each screen's slice under the path of the monitor that
/// is painted in that position, and to apply the same pairing when reading the images back for
/// the editor preview.
/// </para>
/// <para>
/// Both orders are monitor lists, so a slot must keep its index: dropping a slot whose lookup
/// failed shifts every later one by one, and slot k is painted on the k-th painted monitor.
/// A snapshot that could not be read completely is treated as invalid rather than as a
/// shorter list.
/// </para>
/// </summary>
internal static class WallpaperSlotOrder
{
    /// <summary>One filled slot: the path its image is filed under, that monitor's rectangle
    /// and the image itself. An empty <paramref name="ImagePath"/> is a solid colour, which is
    /// a successful read and must stay distinct from a failed one.</summary>
    internal readonly record struct Slot(string DevicePath, WinDef.Rect Rect, string ImagePath);

    /// <summary>Attempts made before a slot snapshot is declared invalid.</summary>
    internal const int ReadAttempts = 3;

    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// A screen image to file under the slot of the monitor painted at <paramref name="Painted"/>.
    /// </summary>
    internal readonly record struct SlotWrite(Slot Filed, WinDef.Rect Painted, ScreenWallpaper Screen);

    /// <summary>A screen the snapshot does not cover, written through its own device path.</summary>
    internal readonly record struct DirectWrite(ScreenWallpaper Screen, string DevicePath);

    /// <summary>
    /// Where every screen's image goes. A screen paired with a slot is written through it only:
    /// a failed write must not be retried through the screen's own path, which on a layout whose
    /// two orders differ holds another screen's image. Direct writes are left to screens the
    /// snapshot does not cover and never target a path a slot already owns.
    /// </summary>
    internal sealed record WritePlan(IReadOnlyList<SlotWrite> Slots, IReadOnlyList<DirectWrite> Direct);

    /// <summary>
    /// Reads every slot, retrying a failed lookup: a partial list would shift all the following
    /// slots. Returns false when no attempt could read the snapshot completely; the caller must
    /// then write nothing rather than guess.
    /// </summary>
    internal static bool TryRead(IWallpaperDesktop desktop, out List<Slot> slots)
        => TryRead(desktop, ReadAttempts, RetryDelay, out slots);

    internal static bool TryRead(IWallpaperDesktop desktop, int attempts, TimeSpan retryDelay, out List<Slot> slots)
    {
        slots = [];

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0 && retryDelay > TimeSpan.Zero) Thread.Sleep(retryDelay);

            if (!ReadOnce(desktop, out slots)) continue;
            if (slots.Count > 0) return true;
        }

        slots = [];
        return false;
    }

    static bool ReadOnce(IWallpaperDesktop desktop, out List<Slot> slots)
    {
        slots = [];

        if (!desktop.TryGetMonitorCount(out var count)) return false;

        for (uint i = 0; i < count; i++)
        {
            if (!desktop.TryGetDevicePath(i, out var devicePath)) return false;
            if (!desktop.TryGetRect(devicePath, out var rect)) return false;

            // An empty rectangle is a monitor that was unplugged but is still listed. It takes
            // no paint position, so dropping it does not move the remaining slots.
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;

            // A failed image read is not a solid colour: it is an incomplete snapshot.
            if (!desktop.TryGetImage(devicePath, out var imagePath)) return false;

            slots.Add(new Slot(devicePath, rect, imagePath));
        }

        return true;
    }

    /// <summary>
    /// Pairs each filed slot with the monitor it is painted on, both in paint order: slot k is
    /// painted on the k-th monitor of the paint order.
    /// </summary>
    internal static List<(Slot Filed, Slot Painted)> Pair(IReadOnlyList<Slot> slots)
    {
        var painted = slots
            .OrderBy(s => s.Rect.X == 0 && s.Rect.Y == 0 ? 0 : 1)
            .ThenBy(s => s.Rect.X)
            .ThenBy(s => s.Rect.Y)
            .ToList();

        var pairs = new List<(Slot, Slot)>(Math.Min(slots.Count, painted.Count));

        for (var i = 0; i < slots.Count && i < painted.Count; i++)
            pairs.Add((slots[i], painted[i]));

        return pairs;
    }

    /// <summary>True when a slot rectangle is the logical rectangle of a screen (2 px tolerance
    /// for the rounding the shell does on scaled layouts).</summary>
    internal static bool Covers(WinDef.Rect rect, Rect bounds)
        => Math.Abs(rect.X - bounds.X) <= 2
           && Math.Abs(rect.Y - bounds.Y) <= 2
           && Math.Abs(rect.Width - bounds.Width) <= 2
           && Math.Abs(rect.Height - bounds.Height) <= 2;

    /// <summary>Decides where every screen's image is written; see <see cref="WritePlan"/>.</summary>
    internal static WritePlan BuildWritePlan(IReadOnlyList<Slot> slots, IReadOnlyList<ScreenWallpaper> screens)
    {
        var slotWrites = new List<SlotWrite>();
        var matched = new List<ScreenWallpaper>();

        foreach (var (filed, painted) in Pair(slots))
        {
            var screen = screens.FirstOrDefault(s => Covers(painted.Rect, s.LogicalBounds));
            if (screen is null) continue;

            slotWrites.Add(new SlotWrite(filed, painted.Rect, screen));
            matched.Add(screen);
        }

        var slotPaths = slotWrites
            .Select(w => w.Filed.DevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var direct = new List<DirectWrite>();

        foreach (var screen in screens)
        {
            if (matched.Contains(screen)) continue;
            if (DisplayPath(screen.DeviceId) is not { } path) continue;

            // Writing here would overwrite the image a slot has just filed.
            if (slotPaths.Contains(path)) continue;

            direct.Add(new DirectWrite(screen, path));
        }

        return new WritePlan(slotWrites, direct);
    }

    /// <summary>
    /// The image each painted rectangle must show, in paint order. An empty path is a solid
    /// colour and must clear whatever the device-path pass bound to that screen.
    /// </summary>
    internal static IReadOnlyList<(WinDef.Rect Painted, string ImagePath)> PreviewAssignments(IReadOnlyList<Slot> slots)
        => Pair(slots).Select(p => (p.Painted.Rect, p.Filed.ImagePath)).ToList();

    /// <summary>
    /// The device interface path of a display source, when it is one. Falls back to the
    /// interface name for layouts stored before the path was captured.
    /// </summary>
    internal static string? DisplayPath(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        if (deviceId.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return deviceId;

        var id = deviceId.Replace('#', '\\');
        return $@"\\?\DISPLAY#{id}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}";
    }
}

/// <summary>
/// The <c>IDesktopWallpaper</c> calls the slot logic needs, as booleans: a failure is a value
/// to handle, not an exception, and the shell's answers can be faked in tests.
/// </summary>
internal interface IWallpaperDesktop
{
    bool TryGetMonitorCount(out uint count);
    bool TryGetDevicePath(uint index, out string devicePath);
    bool TryGetRect(string devicePath, out WinDef.Rect rect);

    /// <summary>Reads the image filed under a path; an empty image is a solid colour.</summary>
    bool TryGetImage(string devicePath, out string imagePath);

    /// <summary>Files an image under a path; an empty image asks for the solid colour.</summary>
    bool SetImage(string devicePath, string imagePath);
}

/// <summary>The COM implementation of <see cref="IWallpaperDesktop"/>.</summary>
internal sealed class DesktopWallpaperAdapter : IWallpaperDesktop
{
    readonly IDesktopWallpaper _desktop;

    internal DesktopWallpaperAdapter(IDesktopWallpaper desktop)
    {
        _desktop = desktop;
    }

    public bool TryGetMonitorCount(out uint count)
    {
        count = 0;
        return _desktop.GetMonitorDevicePathCount(ref count) == HResult.Ok;
    }

    public bool TryGetDevicePath(uint index, out string devicePath)
    {
        devicePath = "";
        var value = "";
        if (_desktop.GetMonitorDevicePathAt(index, ref value) != HResult.Ok) return false;

        devicePath = value ?? "";
        return devicePath.Length > 0;
    }

    public bool TryGetRect(string devicePath, out WinDef.Rect rect)
    {
        rect = default;
        var value = default(WinDef.Rect);
        if (_desktop.GetMonitorRECT(devicePath, ref value) != HResult.Ok) return false;

        rect = value;
        return true;
    }

    public bool TryGetImage(string devicePath, out string imagePath)
    {
        imagePath = "";
        var value = "";
        if (_desktop.GetWallpaper(devicePath, ref value) != HResult.Ok) return false;

        imagePath = value ?? "";
        return true;
    }

    public bool SetImage(string devicePath, string imagePath)
        => _desktop.SetWallpaper(devicePath, imagePath) == HResult.Ok;
}

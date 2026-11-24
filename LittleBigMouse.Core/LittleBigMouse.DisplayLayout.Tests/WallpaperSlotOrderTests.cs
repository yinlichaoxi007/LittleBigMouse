using HLab.Geo;
using HLab.Sys.Windows.API;
using LittleBigMouse.DisplayLayout.Monitors;
using LittleBigMouse.Platform.Windows;
using LittleBigMouse.Plugins;

namespace LittleBigMouse.DisplayLayout.Tests;

public class WallpaperSlotOrderTests
{
    const string Portrait =
        @"\\?\DISPLAY#PHL090F#5&46607fc&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    const string Landscape =
        @"\\?\DISPLAY#LEN66CB#5&46607fc&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    const string Extra =
        @"\\?\DISPLAY#DEL0001#1&2&3#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    static WinDef.Rect R(int x, int y, int w, int h) => new(x, y, x + w, y + h);

    static ScreenWallpaper Screen(int x, int y, int w, int h, string? image, string? deviceId = null)
        => new(new Rect(x, y, w, h), image, WallpaperStyle.Span, null, deviceId);

    /// <summary>
    /// Stands in for the shell: the monitor slots it files images under, with per-index and
    /// per-path failures the real COM object produces during a wallpaper fade.
    /// </summary>
    sealed class FakeDesktop : IWallpaperDesktop
    {
        public readonly List<(string Path, WinDef.Rect Rect, string Image)> Slots = [];

        /// <summary>Remaining failures per slot index; a negative count never recovers.</summary>
        public readonly Dictionary<uint, int> Fails = [];

        public readonly HashSet<string> FailWrites = [];
        public readonly List<(string Path, string Image)> Writes = [];

        public bool TryGetMonitorCount(out uint count)
        {
            count = (uint)Slots.Count;
            return true;
        }

        public bool TryGetDevicePath(uint index, out string devicePath)
        {
            devicePath = "";

            if (Fails.TryGetValue(index, out var left) && left != 0)
            {
                if (left > 0) Fails[index] = left - 1;
                return false;
            }

            devicePath = Slots[(int)index].Path;
            return true;
        }

        public bool TryGetRect(string devicePath, out WinDef.Rect rect)
        {
            rect = Slots.Single(s => s.Path == devicePath).Rect;
            return true;
        }

        public bool TryGetImage(string devicePath, out string imagePath)
        {
            imagePath = Slots.Single(s => s.Path == devicePath).Image;
            return true;
        }

        public bool SetImage(string devicePath, string imagePath)
        {
            if (FailWrites.Contains(devicePath)) return false;

            Writes.Add((devicePath, imagePath));
            return true;
        }
    }

    /// <summary>The real layout: portrait filed first, primary landscape filed second.</summary>
    static FakeDesktop TwoScreens(string portraitImage, string landscapeImage)
    {
        var desktop = new FakeDesktop();
        desktop.Slots.Add((Portrait, R(-1080, -329, 1080, 1920), portraitImage));
        desktop.Slots.Add((Landscape, R(0, 0, 1920, 1080), landscapeImage));
        return desktop;
    }

    static ScreenWallpaper[] TwoScreenTargets(string portraitImage, string landscapeImage)
        =>
        [
            Screen(-1080, -329, 1080, 1920, portraitImage, Portrait),
            Screen(0, 0, 1920, 1080, landscapeImage, Landscape),
        ];

    [Fact]
    public void PhotoAndSolidColourKeepTheirOwnScreens()
    {
        // Portrait slot holds a solid colour, landscape slot the only photo. Paint order is
        // landscape first, so the photo must show on the portrait monitor and the solid colour
        // must clear the primary one instead of keeping the device-path photo.
        var desktop = TwoScreens("", @"C:\pictures\photo.png");

        Assert.True(WallpaperSlotOrder.TryRead(desktop, 1, TimeSpan.Zero, out var slots));

        var preview = WallpaperSlotOrder.PreviewAssignments(slots);

        Assert.Equal("", preview.Single(p => p.Painted.X == 0).ImagePath);
        Assert.Equal(@"C:\pictures\photo.png", preview.Single(p => p.Painted.X == -1080).ImagePath);

        // Both screens have a slot: neither may be written again through its own path, which
        // would put the photo back on the primary monitor after a layout rebuild.
        Assert.Empty(WallpaperSlotOrder.BuildWritePlan(slots, TwoScreenTargets("", @"C:\pictures\photo.png")).Direct);
    }

    [Fact]
    public void FailedSlotLookupIsRetriedInsteadOfShorteningTheList()
    {
        // Three monitors: filing order A/B/primary, paint order primary/A/B. Losing the middle
        // path would pair A with the primary slot, which is painted on B.
        var desktop = new FakeDesktop();
        desktop.Slots.Add(("A", R(-1920, 0, 1920, 1080), ""));
        desktop.Slots.Add(("B", R(1920, 0, 1920, 1080), @"C:\b.png"));
        desktop.Slots.Add(("P", R(0, 0, 1920, 1080), @"C:\p.png"));
        desktop.Fails[1] = 1;

        Assert.True(WallpaperSlotOrder.TryRead(desktop, 3, TimeSpan.Zero, out var slots));
        Assert.Equal(3, slots.Count);

        var pairs = WallpaperSlotOrder.Pair(slots);
        Assert.Equal("A", pairs[0].Filed.DevicePath);
        Assert.Equal(0, pairs[0].Painted.Rect.X);
        Assert.Equal("B", pairs[1].Filed.DevicePath);
        Assert.Equal(-1920, pairs[1].Painted.Rect.X);
    }

    [Fact]
    public void UnreadableSnapshotWritesNothing()
    {
        // A snapshot that keeps failing must not be guessed around: on a layout whose two
        // orders differ, a direct write would land on another screen's monitor.
        var desktop = new FakeDesktop();
        desktop.Slots.Add(("A", R(-1920, 0, 1920, 1080), ""));
        desktop.Slots.Add(("B", R(1920, 0, 1920, 1080), ""));
        desktop.Slots.Add(("P", R(0, 0, 1920, 1080), ""));
        desktop.Fails[1] = -1;

        var screens = new[] { Screen(-1920, 0, 1920, 1080, @"C:\a.png", "A") };

        Assert.False(WindowsWallpaperService.ApplyPlan(desktop, screens));
        Assert.Empty(desktop.Writes);
    }

    [Fact]
    public void FailedWriteInASwappedPairDoesNotOverwriteTheOtherScreen()
    {
        // Swapped pair: the portrait slot gets the landscape image and the landscape slot the
        // portrait one. When the second write fails, retrying it through the landscape path
        // would overwrite the image just filed there.
        var desktop = TwoScreens(@"C:\portrait.png", @"C:\landscape.png");
        desktop.FailWrites.Add(Landscape);

        var screens = TwoScreenTargets(@"C:\portrait.png", @"C:\landscape.png");

        Assert.True(WindowsWallpaperService.ApplyPlan(desktop, screens));

        var write = Assert.Single(desktop.Writes);
        Assert.Equal(Portrait, write.Path);
        Assert.Equal(@"C:\landscape.png", write.Image);
    }

    [Fact]
    public void ScreenWithoutASlotFallsBackToItsOwnDevicePath()
    {
        var desktop = TwoScreens(@"C:\portrait.png", @"C:\landscape.png");

        var screens = TwoScreenTargets(@"C:\portrait.png", @"C:\landscape.png")
            .Append(Screen(1920, -200, 1280, 720, @"C:\extra.png", Extra))
            .ToArray();

        Assert.True(WindowsWallpaperService.ApplyPlan(desktop, screens));
        Assert.Equal(3, desktop.Writes.Count);
        Assert.Contains(desktop.Writes, w => w.Path == Extra && w.Image == @"C:\extra.png");
    }

    [Fact]
    public void RebuildKeepsPaintOrderInsteadOfDeviceOrder()
    {
        // Both slots hold photos. After the cross-write, slot 1 holds the slice of the monitor
        // it is painted on, so a layout rebuild must read it for the primary screen — not the
        // image filed under the primary monitor's own path, which is the device order.
        var desktop = TwoScreens(@"C:\primary-slice.png", @"C:\portrait-slice.png");

        Assert.True(WallpaperSlotOrder.TryRead(desktop, 1, TimeSpan.Zero, out var slots));
        var preview = WallpaperSlotOrder.PreviewAssignments(slots);

        Assert.Equal(@"C:\primary-slice.png", preview.Single(p => p.Painted.X == 0).ImagePath);
        Assert.Equal(@"C:\portrait-slice.png", preview.Single(p => p.Painted.X == -1080).ImagePath);
    }
}

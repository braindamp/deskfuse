using System.Runtime.InteropServices;
using FocusWindow.Core.Display;
using Xunit;
namespace FocusWindow.Tests;
/// <summary>
/// Pins every P/Invoke structure to its documented Win32 size. Blittable layout is identical on any
/// 64-bit OS, so this runs (and can fail) here, long before the code meets Windows.
/// </summary>
public class InteropLayoutTests
{
    [Fact]
    public void Display_config_structures_have_the_documented_win32_sizes()
    {
        Assert.Equal(8, Marshal.SizeOf<Win32Layout.Luid>());
        Assert.Equal(20, Marshal.SizeOf<Win32Layout.PathSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<Win32Layout.PathTargetInfo>());
        Assert.Equal(72, Marshal.SizeOf<Win32Layout.PathInfo>());      // DISPLAYCONFIG_PATH_INFO
        Assert.Equal(64, Marshal.SizeOf<Win32Layout.ModeInfo>());      // DISPLAYCONFIG_MODE_INFO
        Assert.Equal(20, Marshal.SizeOf<Win32Layout.DeviceInfoHeader>());
    }
    [Fact]
    public unsafe void Device_name_packets_have_the_documented_sizes()
    {
        Assert.Equal(84, sizeof(Win32Layout.SourceDeviceName));        // 20 + 32 WCHAR
        Assert.Equal(420, sizeof(Win32Layout.TargetDeviceName));       // 20 + 4 + 4 + 2 + 2 + 4 + 64 WCHAR + 128 WCHAR
        // sizeof() alone missed the bug: P/Invoke marshaled fixed char buffers
        // as ANSI while the Windows DISPLAYCONFIG API writes UTF-16 WCHARs.
        Assert.Equal(84, Marshal.SizeOf<Win32Layout.SourceDeviceName>());
        Assert.Equal(420, Marshal.SizeOf<Win32Layout.TargetDeviceName>());
        Assert.Equal(CharSet.Unicode, typeof(Win32Layout.SourceDeviceName).StructLayoutAttribute!.CharSet);
        Assert.Equal(CharSet.Unicode, typeof(Win32Layout.TargetDeviceName).StructLayoutAttribute!.CharSet);
    }
    [Fact]
    public void The_undocumented_dpi_scale_packets_have_the_sizes_the_reverse_engineered_sources_assert()
    {
        Assert.Equal(0x20, Marshal.SizeOf<Win32Layout.DpiScaleGet>());
        Assert.Equal(0x18, Marshal.SizeOf<Win32Layout.DpiScaleSet>());
        Assert.Equal(-3, Win32Layout.DeviceInfoGetDpiScale);
        Assert.Equal(-4, Win32Layout.DeviceInfoSetDpiScale);
    }
    [Fact]
    public void Send_input_structures_match_the_x64_layout_sendinput_demands()
    {
        Assert.Equal(40, Marshal.SizeOf<Win32Layout.Input>());          // SendInput fails unless cbSize == 40
        Assert.Equal(32, Marshal.SizeOf<Win32Layout.MouseInput>());
        Assert.Equal(24, Marshal.SizeOf<Win32Layout.KeybdInput>());
        Assert.Equal(8, (int)Marshal.OffsetOf<Win32Layout.Input>(nameof(Win32Layout.Input.U)));
    }
    [Fact]
    public void Driver_ioctl_structures_match_the_cpp_header()
    {
        Assert.Equal(28, Marshal.SizeOf<DriverContract.SetTarget>());
        Assert.Equal(48, Marshal.SizeOf<DriverContract.Status>());
    }
    [Fact]
    public void Path_flags_are_decoded_correctly()
    {
        Assert.True(new Win32Layout.PathInfo { Flags = 1 }.IsActive);
        Assert.False(new Win32Layout.PathInfo { Flags = 0 }.IsActive);
    }
}

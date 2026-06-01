using System.Runtime.InteropServices;

namespace ANEVRED.Services;

public sealed class DisplayColorFilterService : IDisposable
{
    private const int RampSize = 256;
    private const int MonitorDefaultToNearest = 0x00000002;
    private readonly ushort[] _originalRamp = new ushort[RampSize * 3];
    private string _deviceName = string.Empty;
    private bool _hasOriginalRamp;
    private bool _isApplied;
    private bool _magnificationInitialized;
    private bool _magnificationApplied;

    public bool Apply(
        double redPercent,
        double greenPercent,
        double bluePercent,
        double contrastPercent,
        double brightnessPercent,
        double gammaPercent,
        double temperature,
        double tint,
        System.Drawing.Rectangle targetBounds)
    {
        var deviceName = GetDeviceNameForBounds(targetBounds);
        if (_isApplied && _hasOriginalRamp && !_deviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
        {
            Restore();
        }

        var device = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        var usedDesktopFallback = false;
        if (device == IntPtr.Zero)
        {
            device = GetDC(IntPtr.Zero);
            usedDesktopFallback = true;
        }

        if (device == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!_hasOriginalRamp && !GetDeviceGammaRamp(device, _originalRamp))
            {
                return false;
            }

            _hasOriginalRamp = true;
            _deviceName = deviceName;
            var adjusted = new ushort[RampSize * 3];
            var channelScales = BuildChannelScales(redPercent, greenPercent, bluePercent, temperature, tint);
            CopyScaledChannel(_originalRamp, adjusted, 0, channelScales.Red, contrastPercent, brightnessPercent, gammaPercent);
            CopyScaledChannel(_originalRamp, adjusted, RampSize, channelScales.Green, contrastPercent, brightnessPercent, gammaPercent);
            CopyScaledChannel(_originalRamp, adjusted, RampSize * 2, channelScales.Blue, contrastPercent, brightnessPercent, gammaPercent);
            _isApplied = SetDeviceGammaRamp(device, adjusted);
            return _isApplied;
        }
        finally
        {
            if (usedDesktopFallback)
            {
                _ = ReleaseDC(IntPtr.Zero, device);
            }
            else
            {
                _ = DeleteDC(device);
            }
        }
    }

    public void Restore()
    {
        RestoreMagnificationColorFilter();

        if (!_hasOriginalRamp || !_isApplied)
        {
            return;
        }

        var device = string.IsNullOrWhiteSpace(_deviceName)
            ? GetDC(IntPtr.Zero)
            : CreateDC("DISPLAY", _deviceName, null, IntPtr.Zero);
        if (device == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = SetDeviceGammaRamp(device, _originalRamp);
            _isApplied = false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(_deviceName))
            {
                _ = DeleteDC(device);
            }
            else
            {
                _ = ReleaseDC(IntPtr.Zero, device);
            }
        }
    }

    public void Dispose()
    {
        Restore();
        if (_magnificationInitialized)
        {
            _ = MagUninitialize();
            _magnificationInitialized = false;
        }
    }

    private bool ApplyMagnificationColorFilter(
        double redPercent,
        double greenPercent,
        double bluePercent,
        double contrastPercent,
        double brightnessPercent)
    {
        try
        {
            if (!_magnificationInitialized)
            {
                _magnificationInitialized = MagInitialize();
            }

            if (!_magnificationInitialized)
            {
                return false;
            }

            var matrix = ColorEffect.Identity();
            var contrast = Math.Clamp(contrastPercent, 50, 150) / 100d;
            var brightness = Math.Clamp(brightnessPercent, -50, 50) / 100d;
            var offset = (0.5d * (1d - contrast)) + brightness;

            matrix.M00 = (float)((Math.Clamp(redPercent, 50, 120) / 100d) * contrast);
            matrix.M11 = (float)((Math.Clamp(greenPercent, 50, 120) / 100d) * contrast);
            matrix.M22 = (float)((Math.Clamp(bluePercent, 50, 120) / 100d) * contrast);
            matrix.M40 = (float)offset;
            matrix.M41 = (float)offset;
            matrix.M42 = (float)offset;
            _magnificationApplied = MagSetFullscreenColorEffect(ref matrix);
            return _magnificationApplied;
        }
        catch
        {
            return false;
        }
    }

    private void RestoreMagnificationColorFilter()
    {
        if (!_magnificationApplied || !_magnificationInitialized)
        {
            return;
        }

        var identity = ColorEffect.Identity();
        _ = MagSetFullscreenColorEffect(ref identity);
        _magnificationApplied = false;
    }

    private static void CopyScaledChannel(
        ushort[] source,
        ushort[] target,
        int offset,
        double channelScale,
        double contrastPercent,
        double brightnessPercent,
        double gammaPercent)
    {
        var contrast = Math.Clamp(contrastPercent, 50, 150) / 100d;
        var brightness = Math.Clamp(brightnessPercent, -50, 50) / 100d;
        var gamma = 100d / Math.Clamp(gammaPercent, 50, 150);
        for (var index = 0; index < RampSize; index++)
        {
            var normalized = source[offset + index] / (double)ushort.MaxValue;
            var adjusted = (((normalized - 0.5d) * contrast) + 0.5d + brightness) * channelScale;
            adjusted = Math.Pow(Math.Clamp(adjusted, 0d, 1d), gamma);
            target[offset + index] = (ushort)Math.Clamp(Math.Round(adjusted * ushort.MaxValue), 0, ushort.MaxValue);
        }
    }

    private static (double Red, double Green, double Blue) BuildChannelScales(
        double redPercent,
        double greenPercent,
        double bluePercent,
        double temperature,
        double tint)
    {
        var warmth = Math.Clamp(temperature, -50, 50) / 50d;
        var tintShift = Math.Clamp(tint, -50, 50) / 50d;

        var red = Math.Clamp(redPercent, 50, 120) / 100d;
        var green = Math.Clamp(greenPercent, 50, 120) / 100d;
        var blue = Math.Clamp(bluePercent, 50, 120) / 100d;

        if (warmth > 0)
        {
            red *= 1d + (0.08d * warmth);
            blue *= 1d - (0.10d * warmth);
        }
        else
        {
            red *= 1d + (0.08d * warmth);
            blue *= 1d - (0.10d * warmth);
        }

        if (tintShift > 0)
        {
            red *= 1d + (0.04d * tintShift);
            blue *= 1d + (0.04d * tintShift);
            green *= 1d - (0.08d * tintShift);
        }
        else
        {
            green *= 1d - (0.08d * tintShift);
            red *= 1d + (0.04d * tintShift);
            blue *= 1d + (0.04d * tintShift);
        }

        return (
            Math.Clamp(red, 0.5d, 1.2d),
            Math.Clamp(green, 0.5d, 1.2d),
            Math.Clamp(blue, 0.5d, 1.2d));
    }

    private static string GetDeviceNameForBounds(System.Drawing.Rectangle bounds)
    {
        var rect = new NativeRect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return "DISPLAY";
        }

        var info = new MonitorInfoEx();
        info.Size = Marshal.SizeOf<MonitorInfoEx>();
        return GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName)
            ? info.DeviceName
            : "DISPLAY";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driverName, string deviceName, string? output, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("Magnification.dll", ExactSpelling = true)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll", ExactSpelling = true)]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll", ExactSpelling = true)]
    private static extern bool MagSetFullscreenColorEffect(ref ColorEffect effect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorEffect
    {
        public float M00;
        public float M01;
        public float M02;
        public float M03;
        public float M04;
        public float M10;
        public float M11;
        public float M12;
        public float M13;
        public float M14;
        public float M20;
        public float M21;
        public float M22;
        public float M23;
        public float M24;
        public float M30;
        public float M31;
        public float M32;
        public float M33;
        public float M34;
        public float M40;
        public float M41;
        public float M42;
        public float M43;
        public float M44;

        public static ColorEffect Identity()
        {
            return new ColorEffect
            {
                M00 = 1,
                M11 = 1,
                M22 = 1,
                M33 = 1,
                M44 = 1
            };
        }
    }
}

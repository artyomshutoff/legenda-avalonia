using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace Legenda.App;

public sealed record CameraDeviceInfo(int Index, string Name)
{
    public override string ToString() => Name;
}

public interface ICameraScannerService
{
    event Action<byte[]>? FrameReady;
    event Action<string>? CodeDetected;
    event Action<string, bool>? StatusChanged;
    int SelectedDeviceIndex { get; set; }
    Task<IReadOnlyList<CameraDeviceInfo>> GetAvailableDevicesAsync();
    Task StartAsync();
    Task StopAsync();
}

public sealed class CameraScannerService : ICameraScannerService
{
    private CancellationTokenSource? _cancellation;
    private Task? _captureTask;
    private string? _lastCode;
    private DateTime _lastDetection = DateTime.MinValue;
    private IReadOnlyList<CameraDeviceInfo>? _devices;

    public event Action<byte[]>? FrameReady;
    public event Action<string>? CodeDetected;
    public event Action<string, bool>? StatusChanged;
    public int SelectedDeviceIndex { get; set; }

    public async Task<IReadOnlyList<CameraDeviceInfo>> GetAvailableDevicesAsync()
    {
        _devices = await Task.Run(CameraDeviceEnumerator.Enumerate);
        return _devices;
    }

    public async Task StartAsync()
    {
        if (_captureTask is { IsCompleted: false }) return;
        var devices = _devices ?? await GetAvailableDevicesAsync();
        // Enumeration failure must not prevent opening the default camera.
        if (devices.Count > 0 && devices.All(device => device.Index != SelectedDeviceIndex))
            SelectedDeviceIndex = devices[0].Index;
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var deviceIndex = SelectedDeviceIndex;
        var token = _cancellation.Token;
        _captureTask = Task.Run(() => CaptureLoop(deviceIndex, token));
    }

    public async Task StopAsync()
    {
        if (_cancellation is null) return;
        await _cancellation.CancelAsync();
        if (_captureTask is not null)
        {
            try { await _captureTask; }
            catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
        _cancellation = null;
        _captureTask = null;
    }

    private async Task CaptureLoop(int deviceIndex, CancellationToken cancellationToken)
    {
        try
        {
            using var camera = OpenCamera(deviceIndex);
            if (!camera.IsOpened())
            {
                StatusChanged?.Invoke("Камера не найдена или доступ к ней запрещён", true);
                return;
            }

            camera.Set(VideoCaptureProperties.FrameWidth, 1280);
            camera.Set(VideoCaptureProperties.FrameHeight, 720);
            camera.Set(VideoCaptureProperties.Fps, 24);
            StatusChanged?.Invoke("Камера подключена", false);

            using var frame = new Mat();
            var frameNumber = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!camera.Read(frame) || frame.Empty())
                {
                    await Task.Delay(80, cancellationToken);
                    continue;
                }

                Cv2.ImEncode(".jpg", frame, out var encoded, new ImageEncodingParam(ImwriteFlags.JpegQuality, 78));
                if (encoded.Length > 0)
                    FrameReady?.Invoke(encoded);

                if (++frameNumber % 3 == 0)
                {
                    var result = Decode(frame);
                    if (!string.IsNullOrWhiteSpace(result) && CanReport(result))
                        CodeDetected?.Invoke(result);
                }

                await Task.Delay(35, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"Не удалось запустить камеру: {exception.Message}", true);
        }
    }

    private static VideoCapture OpenCamera(int deviceIndex)
    {
        var camera = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
        if (camera.IsOpened()) return camera;
        camera.Dispose();
        return new VideoCapture(deviceIndex, VideoCaptureAPIs.ANY);
    }

    private static string? Decode(Mat frame)
    {
        if (frame.Type() != MatType.CV_8UC3) return null;
        using var continuous = frame.IsContinuous() ? frame.Clone() : frame.Clone();
        var length = checked((int)(continuous.Total() * continuous.ElemSize()));
        var pixels = new byte[length];
        Marshal.Copy(continuous.Data, pixels, 0, length);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = false,
                PossibleFormats = QrFormats
            }
        };
        var source = new RGBLuminanceSource(pixels, continuous.Width, continuous.Height, RGBLuminanceSource.BitmapFormat.BGR24);
        return reader.Decode(source)?.Text;
    }

    private bool CanReport(string code)
    {
        var now = DateTime.UtcNow;
        if (code == _lastCode && now - _lastDetection < TimeSpan.FromSeconds(2)) return false;
        _lastCode = code;
        _lastDetection = now;
        return true;
    }

    private static readonly IList<BarcodeFormat> QrFormats = new[] { BarcodeFormat.QR_CODE };
}

public sealed class DisabledCameraScannerService : ICameraScannerService
{
    public event Action<byte[]>? FrameReady { add { } remove { } }
    public event Action<string>? CodeDetected { add { } remove { } }
    public event Action<string, bool>? StatusChanged;
    public int SelectedDeviceIndex { get; set; }
    public Task<IReadOnlyList<CameraDeviceInfo>> GetAvailableDevicesAsync() =>
        Task.FromResult<IReadOnlyList<CameraDeviceInfo>>(new[] { new CameraDeviceInfo(0, "Тестовая камера") });
    public Task StartAsync()
    {
        StatusChanged?.Invoke("Камера отключена в тестовом режиме", true);
        return Task.CompletedTask;
    }
    public Task StopAsync() => Task.CompletedTask;
}

internal static class CameraDeviceEnumerator
{
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid PropertyBagId = new("55272A00-42CB-11CE-8135-00AA004BB851");

    public static IReadOnlyList<CameraDeviceInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            return new[] { new CameraDeviceInfo(0, "Камера 1") };

        var devices = new List<CameraDeviceInfo>();
        object? deviceEnumerator = null;
        IEnumMoniker? monikers = null;
        try
        {
            deviceEnumerator = new CreateDevEnum();
            var createDevEnum = (ICreateDevEnum)deviceEnumerator;
            var category = VideoInputDeviceCategory;
            if (createDevEnum.CreateClassEnumerator(ref category, out monikers, 0) != 0 || monikers is null)
                return devices;

            var fetched = IntPtr.Zero;
            var monikerArray = new IMoniker[1];
            while (monikers.Next(1, monikerArray, fetched) == 0)
            {
                var moniker = monikerArray[0];
                object? bag = null;
                try
                {
                    var bagId = PropertyBagId;
                    moniker.BindToStorage(null!, null, ref bagId, out var storage);
                    bag = storage;
                    var name = ((IPropertyBag)bag).Read("FriendlyName", out var value, IntPtr.Zero) == 0
                        ? value?.ToString()
                        : null;
                    devices.Add(new CameraDeviceInfo(devices.Count, string.IsNullOrWhiteSpace(name) ? $"Камера {devices.Count + 1}" : name));
                }
                catch
                {
                    devices.Add(new CameraDeviceInfo(devices.Count, $"Камера {devices.Count + 1}"));
                }
                finally
                {
                    if (bag is not null && Marshal.IsComObject(bag)) Marshal.ReleaseComObject(bag);
                    if (Marshal.IsComObject(moniker)) Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch
        {
            return devices;
        }
        finally
        {
            if (monikers is not null && Marshal.IsComObject(monikers)) Marshal.ReleaseComObject(monikers);
            if (deviceEnumerator is not null && Marshal.IsComObject(deviceEnumerator)) Marshal.ReleaseComObject(deviceEnumerator);
        }
        return devices;
    }

    [ComImport, Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86")]
    private sealed class CreateDevEnum;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker? enumMoniker, int flags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] out object? value, IntPtr errorLog);

        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}

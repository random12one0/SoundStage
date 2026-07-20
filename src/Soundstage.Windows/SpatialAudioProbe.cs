using System.Runtime.InteropServices;
using Soundstage.Core.Automation;

namespace Soundstage.Windows;

/// <summary>
/// Best-effort detection of Windows Spatial Audio (Dolby Atmos for Home Theater, DTS:X,
/// Windows Sonic) engagement on an endpoint, via <c>ISpatialAudioClient</c>: a spatial
/// format is active when the endpoint reports dynamic audio objects.
///
/// Everything here degrades to <see cref="SpatialAudioState.Unknown"/> — this powers a
/// status readout only and must never gate behavior.
/// </summary>
public static class SpatialAudioProbe
{
    public static SpatialAudioState Probe(string endpointId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return SpatialAudioState.Unknown;
        }

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.GetDevice(endpointId, out var device) != 0 || device is null)
                {
                    return SpatialAudioState.Unknown;
                }

                try
                {
                    var spatialAudioClientIid = new Guid("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F");
                    if (device.Activate(ref spatialAudioClientIid, ClsctxAll, IntPtr.Zero, out var clientObj) != 0 || clientObj is null)
                    {
                        return SpatialAudioState.Off;
                    }

                    var client = (ISpatialAudioClient)clientObj;
                    try
                    {
                        return client.GetMaxDynamicObjectCount(out var count) == 0 && count > 0
                            ? SpatialAudioState.On
                            : SpatialAudioState.Off;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(client);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch
        {
            return SpatialAudioState.Unknown;
        }
    }

    private const uint ClsctxAll = 0x17;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    /// <summary>
    /// Partial vtable of ISpatialAudioClient — methods must be declared in interface order
    /// up to the one we call.
    /// </summary>
    [ComImport]
    [Guid("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpatialAudioClient
    {
        int GetStaticObjectPosition(int type, out float x, out float y, out float z);

        int GetNativeStaticObjectTypeMask(out int mask);

        int GetMaxDynamicObjectCount(out int value);
    }
}

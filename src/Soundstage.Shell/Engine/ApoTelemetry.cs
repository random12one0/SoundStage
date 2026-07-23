using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Soundstage.Shell.Engine;

/// <summary>
/// Reads the live meter telemetry the audio plugin publishes.
/// </summary>
/// <remarks>
/// <para>
/// In plugin mode the app is no longer in the audio path — the plugin does the processing inside
/// Windows — so the app has nothing of its own to meter. Without this the level meters and the
/// "playing / nothing playing" status would sit dead, which is exactly what users reported. The
/// plugin therefore writes its live per-speaker levels and a heartbeat into a small file, and this
/// class reads them back.
/// </para>
/// <para>
/// The app OWNS this file: it creates it, sizes it, and grants write access to everyone, because the
/// plugin runs inside audiodg.exe under a restricted token that must still be able to map it
/// read-write. That is the reverse of the settings file (which the app writes and the plugin reads).
/// </para>
/// <para>
/// No lock is used. Every field is a single 32-bit value, the writer bumps the heartbeat last, and a
/// metering number one frame stale is invisible. A display side-channel does not need a seqlock.
/// </para>
/// </remarks>
public sealed class ApoTelemetry : IDisposable
{
    /// <summary>Must match <c>SOUNDSTAGE_TELEMETRY_PATH</c> in SoundstageApo.h.</summary>
    public static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Soundstage", "engine-telemetry.bin");

    // struct SoundstageTelemetry: uint heartbeat, channels, sampleRate, _pad; float[8] peaks;
    // float inPeak, outPeak.  = 4*4 + 8*4 + 2*4 = 56 bytes. Rounded up for headroom.
    private const int BlockSize = 256;
    private const int OffHeartbeat = 0;
    private const int OffChannels = 4;
    private const int OffChannelPeak = 16;   // float[8]
    private const int OffOutPeak = 52;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private bool _disposed;

    private uint _lastHeartbeat;
    private int _stallTicks;

    /// <summary>The per-speaker output levels (0..1), Windows channel order. Post-processing, so an
    /// upmix's surround fill shows up here.</summary>
    public readonly float[] ChannelPeaks = new float[8];

    /// <summary>Overall output level, 0..1.</summary>
    public float OutPeak { get; private set; }

    /// <summary>Channels the plugin is currently running at (0 if unknown / not running).</summary>
    public int Channels { get; private set; }

    /// <summary>
    /// True when the plugin's heartbeat has advanced recently — i.e. audio really is flowing through
    /// it right now. This is what makes "playing" honest in plugin mode.
    /// </summary>
    public bool Active => _stallTicks < StallLimit;

    // At an 80 ms meter tick, ~6 ticks ≈ half a second of no heartbeat before we call it stopped.
    private const int StallLimit = 6;

    public ApoTelemetry()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            var fs = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                    FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < BlockSize)
            {
                fs.SetLength(BlockSize);
            }

            _mmf = MemoryMappedFile.CreateFromFile(fs, null, BlockSize,
                                                   MemoryMappedFileAccess.ReadWrite,
                                                   HandleInheritability.None, leaveOpen: false);
            _view = _mmf.CreateViewAccessor(0, BlockSize, MemoryMappedFileAccess.ReadWrite);

            GrantWriteToEveryone();
        }
        catch
        {
            _view = null;   // metering just won't work; never fatal
        }
    }

    /// <summary>
    /// Pull the latest telemetry. Returns true if audio is currently flowing through the plugin.
    /// Call on the UI meter tick; it is a few field reads.
    /// </summary>
    public bool Poll()
    {
        _stallTicks = Math.Min(_stallTicks + 1, StallLimit + 1);   // assume stalled until proven live

        if (_view == null || _disposed)
        {
            return false;
        }

        try
        {
            uint hb = _view.ReadUInt32(OffHeartbeat);
            Channels = (int)_view.ReadUInt32(OffChannels);
            for (int c = 0; c < 8; c++)
            {
                ChannelPeaks[c] = _view.ReadSingle(OffChannelPeak + c * 4);
            }

            OutPeak = _view.ReadSingle(OffOutPeak);

            if (hb != _lastHeartbeat)
            {
                _lastHeartbeat = hb;
                _stallTicks = 0;   // fresh beat: audio is flowing
            }
        }
        catch
        {
            return false;
        }

        return Active;
    }

    private static void GrantWriteToEveryone()
    {
        try
        {
            var info = new FileInfo(Path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch
        {
            // If we can't widen the ACL the plugin may not be able to write — the meters just stay
            // quiet, same as when the plugin isn't installed. Not worth failing over.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }
}

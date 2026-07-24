using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Soundstage.Shell.Engine;

/// <summary>
/// Publishes the app's settings to the Soundstage APO — the plugin that runs inside Windows' own
/// audio engine.
/// </summary>
/// <remarks>
/// <para>
/// The two halves of Soundstage cannot talk to each other directly. The app runs as the user; the APO
/// runs inside <c>audiodg.exe</c>, a protected process in session 0. No pipes, no COM, no window
/// messages. What they can both reach is a file, so that is the channel: the app writes its settings
/// into a fixed-size block and the APO reads them at the top of each buffer.
/// </para>
/// <para>
/// A file rather than a named shared-memory object because named objects are session-scoped — a
/// <c>Local\</c> name created here is invisible from session 0, and <c>Global\</c> needs a privilege
/// a normal account does not have. Once mapped, it behaves as memory; nothing reaches the disk on the
/// audio path.
/// </para>
/// <para>
/// The reader is on a real-time thread and cannot take a lock, so coordination is a seqlock: the
/// counter goes odd before a write and even after. A reader that sees odd, or sees the counter change
/// across its copy, skips that update. Settings landing one buffer late is inaudible; acting on half
/// an update would not be.
/// </para>
/// </remarks>
public sealed class ApoBridge : IDisposable
{
    /// <summary>Must match <c>SOUNDSTAGE_SHARED_PATH</c> in SoundstageApo.h.</summary>
    public static readonly string StatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "Soundstage", "engine-state.bin");

    /// <summary>Must match <c>SOUNDSTAGE_SHARED_VERSION</c>. Bump when the layout changes.</summary>
    private const uint LayoutVersion = 1;

    public const int MaxEqBands = 36;

    // Generous: the struct is well under this, and a fixed, over-sized block means adding a field
    // later doesn't change the file size and confuse a plugin built against the old one.
    private const int BlockSize = 8192;

    private readonly object _gate = new();
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private uint _sequence;
    private bool _disposed;

    /// <summary>The settings as last published. Mutate, then call <see cref="Publish"/>.</summary>
    public readonly ApoSettings Settings = new();

    public bool IsOpen => _view != null;

    /// <summary>Last failure, if the bridge could not open. Null when healthy.</summary>
    public string? Error { get; private set; }

    public ApoBridge()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);

            // The APO holds this open for reading while we hold it for writing, so both sides must
            // share generously or one of them gets a sharing violation.
            var fs = new FileStream(StatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
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
        catch (Exception ex)
        {
            Error = ex.Message;
            _view = null;
        }
    }

    /// <summary>
    /// Let any account read AND write the state file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader is <c>audiodg.exe</c>, which runs under a stripped-down token that is not
    /// necessarily a member of Users, so inheriting ProgramData's default permissions is not enough
    /// to guarantee it can open the file.
    /// </para>
    /// <para>
    /// Write is granted too, not just read — and this matters. If the file was ever created by an
    /// elevated process it is owned by Administrators and a normal (non-elevated) run of this app gets
    /// only the inherited Users read access, so it cannot open the file for writing. The bridge then
    /// silently fails and the plugin is stuck on stale settings: audio still plays, meters still move,
    /// but no EQ or effect change ever reaches it. Granting Modify (exactly as the telemetry file
    /// does) keeps the channel writable regardless of who created the file. The exposure is only that
    /// another local account could change the EQ — the same trade the telemetry file already makes.
    /// </para>
    /// </remarks>
    private static void GrantWriteToEveryone()
    {
        try
        {
            var info = new FileInfo(StatePath);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch
        {
            // Not fatal. Worst case the plugin can't open the file and stays transparent, which is
            // exactly what happens when the app isn't running at all.
        }
    }

    /// <summary>
    /// Write the current settings out for the plugin. Safe to call from any thread and cheap enough
    /// to call on every control change — it is a few hundred bytes into a mapped page.
    /// </summary>
    public void Publish()
    {
        if (_view == null || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            var w = new Packer(stackalloc byte[BlockSize]);

            // Odd first: any reader that arrives mid-write sees the odd counter and skips.
            _sequence++;
            if ((_sequence & 1) == 0)
            {
                _sequence++;
            }

            w.Int((int)_sequence);
            w.Int((int)LayoutVersion);
            Settings.Write(ref w);

            var bytes = w.Written;

            // Body first, then the counter. The reader keys off the counter, so it must be the last
            // thing to become visible — otherwise it could see a fresh count over stale data.
            _view.WriteArray(8, bytes.ToArray(), 8, bytes.Length - 8);
            Thread.MemoryBarrier();

            _sequence++;   // back to even: the block is stable
            _view.Write(0, _sequence);
            _view.Write(4, LayoutVersion);
            _view.Flush();
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

    /// <summary>
    /// Lays out primitives the way a 64-bit C++ compiler would: every value on a boundary of its own
    /// size. Written by hand rather than via struct marshalling because the two sides only agree if
    /// the padding agrees, and padding is exactly what marshalling attributes make hard to see.
    /// </summary>
    public ref struct Packer
    {
        private readonly Span<byte> _buf;
        private int _at;

        public Packer(Span<byte> buffer)
        {
            _buf = buffer;
            _at = 0;
        }

        public readonly ReadOnlySpan<byte> Written => _buf[.._at];

        private void Align(int to)
        {
            var over = _at % to;
            if (over != 0)
            {
                _at += to - over;
            }
        }

        public void Int(int v)
        {
            Align(4);
            BitConverter.TryWriteBytes(_buf[_at..], v);
            _at += 4;
        }

        public void Bool(bool v) => Int(v ? 1 : 0);

        public void Double(double v)
        {
            Align(8);
            BitConverter.TryWriteBytes(_buf[_at..], v);
            _at += 8;
        }
    }
}

/// <summary>
/// A mirror of every parameter the engine takes, in the order the C++ <c>SoundstageSettings</c>
/// declares them. The order is the contract — a field moved here without moving it there silently
/// shifts everything after it.
/// </summary>
public sealed class ApoSettings
{
    public bool MasterOn = true;
    public double OutputGainDb;

    public bool EqOn = true;
    public int EqBandCount;
    public readonly EqBand[] EqBands = new EqBand[ApoBridge.MaxEqBands];

    public bool BassOn;
    public double BassAmount, BassCrossover = 90, BassDrive = 1.5;

    public bool CompOn;
    public double CompThresholdDb = -18, CompRatio = 3, CompKneeDb = 6,
                  CompMakeupDb, CompAttackMs = 15, CompReleaseMs = 150;

    public bool NightOn;
    public double NightThresholdDb = -28, NightRatio = 5, NightMakeupDb = 6,
                  NightAttackMs = 5, NightReleaseMs = 250;

    public bool WidthOn;
    public double Width = 1.0;

    public bool ReverbOn;
    public double RvSize = 0.5, RvDecay = 1.6, RvDamping = 0.5, RvPreDelayMs = 20,
                  RvWidth = 1.0, RvMix, RvDiffusion = 0.7, RvLowCutHz = 200,
                  RvHighCutHz = 8000, RvEarly = 0.4, RvModulation = 0.3;

    public bool UpmixOn;
    public double UpmixAmount = 0.7, UpmixCenter = 1.0, UpmixLfe = 1.0;

    public bool SubFeedOn;

    public bool BassMgmtOn;
    public double BmCrossover = 80, BmSubGain = 1.0;
    public int BmSmallMask = 0b1111_0111;   // everything Small except the LFE itself

    public bool LimiterOn = true;
    public double LimCeilingDb = -1.0, LimReleaseMs = 80;

    public readonly double[] ChannelTrimDb = new double[8];

    public struct EqBand
    {
        public int Type;
        public double Freq, GainDb, Q;
    }

    internal void Write(ref ApoBridge.Packer w)
    {
        w.Bool(MasterOn);
        w.Double(OutputGainDb);

        w.Bool(EqOn);
        w.Int(EqBandCount);
        foreach (var b in EqBands)
        {
            w.Int(b.Type);
            w.Double(b.Freq);
            w.Double(b.GainDb);
            w.Double(b.Q);
        }

        w.Bool(BassOn);
        w.Double(BassAmount); w.Double(BassCrossover); w.Double(BassDrive);

        w.Bool(CompOn);
        w.Double(CompThresholdDb); w.Double(CompRatio); w.Double(CompKneeDb);
        w.Double(CompMakeupDb); w.Double(CompAttackMs); w.Double(CompReleaseMs);

        w.Bool(NightOn);
        w.Double(NightThresholdDb); w.Double(NightRatio); w.Double(NightMakeupDb);
        w.Double(NightAttackMs); w.Double(NightReleaseMs);

        w.Bool(WidthOn);
        w.Double(Width);

        w.Bool(ReverbOn);
        w.Double(RvSize); w.Double(RvDecay); w.Double(RvDamping); w.Double(RvPreDelayMs);
        w.Double(RvWidth); w.Double(RvMix); w.Double(RvDiffusion); w.Double(RvLowCutHz);
        w.Double(RvHighCutHz); w.Double(RvEarly); w.Double(RvModulation);

        w.Bool(UpmixOn);
        w.Double(UpmixAmount); w.Double(UpmixCenter); w.Double(UpmixLfe);

        w.Bool(SubFeedOn);

        w.Bool(BassMgmtOn);
        w.Double(BmCrossover); w.Double(BmSubGain);
        w.Int(BmSmallMask);

        w.Bool(LimiterOn);
        w.Double(LimCeilingDb); w.Double(LimReleaseMs);

        foreach (var t in ChannelTrimDb)
        {
            w.Double(t);
        }
    }
}

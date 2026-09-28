using BetterDemo.Core.Contracts;

namespace BetterDemo.Remote.Protocol;

public readonly record struct CameraCornerState(double AngleDegrees, double Scale, double Margin);

public readonly record struct PanState(double X, double Y);

public readonly record struct BlurState(bool Enabled, double Radius);

public sealed class RemoteSceneSnapshot
{
    public RemoteSceneSnapshot(
        int protocolVersion,
        ulong stateSequence,
        SceneMode mode,
        OutputWindowId outputWindowId,
        CameraCornerState cameraCorner,
        double zoom,
        PanState pan,
        BlurState blur,
        IReadOnlyDictionary<string, bool> layerVisibility,
        bool outputVisible = true)
    {
        RemoteProtocol.ValidateVersion(protocolVersion);
        if (stateSequence == 0) throw new ArgumentException("Remote state sequences start at one.", nameof(stateSequence));
        if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        ProtocolVersion = protocolVersion;
        StateSequence = stateSequence;
        Mode = mode;
        OutputWindowId = outputWindowId;
        CameraCorner = cameraCorner;
        Zoom = zoom;
        Pan = pan;
        Blur = blur;
        LayerVisibility = new Dictionary<string, bool>(layerVisibility, StringComparer.Ordinal);
        OutputVisible = outputVisible;
    }

    public int ProtocolVersion { get; }
    public ulong StateSequence { get; }
    public SceneMode Mode { get; }
    public SceneMode SceneMode => Mode;
    public OutputWindowId OutputWindowId { get; }
    public CameraCornerState CameraCorner { get; }
    public double Zoom { get; }
    public PanState Pan { get; }
    public BlurState Blur { get; }
    public bool BlurEnabled => Blur.Enabled;
    public double BlurRadius => Blur.Radius;
    public IReadOnlyDictionary<string, bool> LayerVisibility { get; }
    public bool OutputVisible { get; }
}

public sealed class RemoteStateStore : IRemoteStateSnapshotSource
{
    private readonly object gate = new();
    private readonly OutputWindowId outputWindowId;
    private SceneMode mode = SceneMode.Black;
    private CameraCornerState cameraCorner = new(0, 0.24, 28);
    private double zoom = 1;
    private PanState pan;
    private BlurState blur;
    private bool outputVisible = true;
    private readonly Dictionary<string, bool> layerVisibility = new(StringComparer.Ordinal);
    private ulong stateSequence = 1;

    public RemoteStateStore(OutputWindowId? outputWindowId = null)
    {
        this.outputWindowId = outputWindowId ?? new OutputWindowId("output-window");
    }

    public RemoteSceneSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public ValueTask<BetterDemo.Core.Contracts.RemoteStateSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot;
        return ValueTask.FromResult(new BetterDemo.Core.Contracts.RemoteStateSnapshot(
            snapshot.ProtocolVersion,
            snapshot.StateSequence,
            snapshot.Mode,
            snapshot.OutputWindowId));
    }

    internal RemoteSceneSnapshot SetMode(SceneMode value)
    {
        lock (gate) { mode = value; return MutatedSnapshot(); }
    }

    internal RemoteSceneSnapshot SetCameraCorner(CameraCornerState value)
    {
        if (!double.IsFinite(value.AngleDegrees) || value.AngleDegrees is < -360 or > 360)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!double.IsFinite(value.Scale) || value.Scale is < 0.05 or > 0.75)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!double.IsFinite(value.Margin) || value.Margin is < 0 or > 300)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) { cameraCorner = value; return MutatedSnapshot(); }
    }

    internal RemoteSceneSnapshot SetZoom(double value)
    {
        if (!double.IsFinite(value) || value is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) { zoom = value; return MutatedSnapshot(); }
    }

    internal RemoteSceneSnapshot SetTransform(double zoomValue, double panX, double panY)
    {
        if (!double.IsFinite(zoomValue) || zoomValue is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(zoomValue));
        if (!double.IsFinite(panX) || panX is < -4 or > 4) throw new ArgumentOutOfRangeException(nameof(panX));
        if (!double.IsFinite(panY) || panY is < -4 or > 4) throw new ArgumentOutOfRangeException(nameof(panY));
        lock (gate)
        {
            zoom = zoomValue;
            pan = new PanState(panX, panY);
            return MutatedSnapshot();
        }
    }

    internal RemoteSceneSnapshot PanBy(PanState value)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate)
        {
            var next = new PanState(pan.X + value.X, pan.Y + value.Y);
            if (next.X is < -4 or > 4 || next.Y is < -4 or > 4) throw new ArgumentOutOfRangeException(nameof(value));
            pan = next;
            return MutatedSnapshot();
        }
    }

    internal RemoteSceneSnapshot ResetTransform()
    {
        lock (gate)
        {
            cameraCorner = new CameraCornerState(0, 0.24, 28);
            zoom = 1;
            pan = default;
            return MutatedSnapshot();
        }
    }

    internal RemoteSceneSnapshot SetBlur(BlurState value)
    {
        if (!double.IsFinite(value.Radius) || value.Radius is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) { blur = value; return MutatedSnapshot(); }
    }

    internal RemoteSceneSnapshot SetOutputVisibility(bool visible)
    {
        lock (gate) { outputVisible = visible; return MutatedSnapshot(); }
    }

    internal RemoteSceneSnapshot SetLayerVisibility(string layerId, bool visible)
    {
        lock (gate) { layerVisibility[layerId] = visible; return MutatedSnapshot(); }
    }

    public RemoteSceneSnapshot UpdateLocalMode(SceneMode value) => SetMode(value);

    public RemoteSceneSnapshot UpdateLocalCameraCorner(CameraCornerState value) => SetCameraCorner(value);

    public RemoteSceneSnapshot UpdateLocalZoom(double value) => SetZoom(value);

    public RemoteSceneSnapshot UpdateLocalTransform(double zoomValue, double panX, double panY) => SetTransform(zoomValue, panX, panY);

    public RemoteSceneSnapshot UpdateLocalPan(PanState value) => PanBy(value);

    public RemoteSceneSnapshot UpdateLocalBlur(BlurState value) => SetBlur(value);

    public RemoteSceneSnapshot UpdateLocalLayerVisibility(string layerId, bool isVisible) =>
        SetLayerVisibility(layerId, isVisible);

    public RemoteSceneSnapshot UpdateLocalOutputVisibility(bool visible) => SetOutputVisibility(visible);

    private RemoteSceneSnapshot MutatedSnapshot()
    {
        if (stateSequence == ulong.MaxValue) throw new InvalidOperationException("Remote state sequence exhausted.");
        stateSequence++;
        return CreateSnapshot();
    }

    private RemoteSceneSnapshot CreateSnapshot() => new(
        RemoteProtocol.CurrentVersion,
        stateSequence,
        mode,
        outputWindowId,
        cameraCorner,
        zoom,
        pan,
        blur,
        layerVisibility,
        outputVisible);
}

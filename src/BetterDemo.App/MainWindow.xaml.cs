using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using BetterDemo.Audio;
using BetterDemo.App.Assets;
using BetterDemo.App.Rendering;
using BetterDemo.App.Remote;
using BetterDemo.App.Scenes;
using BetterDemo.Capture;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Core.UndoRedo;
using BetterDemo.Interop.D3D11;
using BetterDemo.Interop.MediaFoundation;
using BetterDemo.Interop.Wasapi;
using BetterDemo.Interop.Win32;
using BetterDemo.Remote;
using BetterDemo.Remote.Pairing;
using BetterDemo.Remote.Protocol;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace BetterDemo.App;

public sealed partial class MainWindow : Window
{
    private static readonly OutputSize DefaultOutputSize = new(1280, 720);
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromMilliseconds(750);
    private static readonly OutputWindowId OutputWindowId = new("discord-output-window");

    private readonly DispatcherQueue dispatcherQueue;
    private readonly DispatcherQueueTimer modeSyncTimer;
    private readonly SemaphoreSlim physicalSourceGate = new(1, 1);
    private readonly SemaphoreSlim physicalDeviceRefreshGate = new(1, 1);
    private readonly RemoteStateStore remoteStateStore;
    private readonly RemotePreviewFrameStore remotePreviewFrames = new();
    private readonly SceneAssetStore sceneAssetStore = new();
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly Dictionary<SceneLayerId, ImportedLayerContent> importedLayerContents = new();
    private readonly LatestFrameSlot obsFrames = new();
    private readonly LatestFrameSlot physicalFrames = new();
    private readonly ConcurrentQueue<string> pendingDiagnostics = new();
    private ImportedImageLayer[] importedImageLayers = [];
    private SceneDocument customSceneDocument = new();
    private SceneCommandHistory customSceneHistory = new();
    private readonly OutputPlacementStore outputPlacement = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDemo", "output-position.json"));
    private MediaFoundationVideoAdapter? videoAdapter;
    private Win32WindowAdapter? windowAdapter;
    private D3D11RenderAdapter? renderAdapter;
    private D3D11Renderer? renderer;
    private ObsVirtualCameraCapture? obsCapture;
    private PhysicalCameraCapture? physicalCapture;
    private CancellationTokenSource? obsPumpCancellation;
    private CancellationTokenSource? physicalPumpCancellation;
    private CancellationTokenSource? renderCancellation;
    private Task? obsPumpTask;
    private Task? physicalPumpTask;
    private Task? renderTask;
    private RemoteServer? remoteServer;
    private RemotePreviewPublisher? previewPublisher;
    private WasapiAudioAdapter? audioAdapter;
    private ProcessAudioRoute? audioRoute;
    private VideoDeviceFormatSelection? selectedPhysical;
    private OutputSize outputSize = DefaultOutputSize;
    private int selectedMode = (int)SceneMode.Screen;
    private bool lastOutputVisibility = true;
    private bool populatingPhysicalDevices;
    private bool synchronizingRemoteMode;
    private bool updatingImageProperties;
    private bool transformSliderGestureActive;
    private bool transformGestureChanged;
    private bool starting;
    private bool stopping;

    public MainWindow()
    {
        InitializeComponent();
        Title = "BetterDemo Editor";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? new MicaBackdrop()
            : new DesktopAcrylicBackdrop();
        dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The WinUI dispatcher is unavailable.");
        modeSyncTimer = dispatcherQueue.CreateTimer();
        modeSyncTimer.Interval = TimeSpan.FromMilliseconds(100);
        modeSyncTimer.Tick += (_, _) => SynchronizeModePicker();
        remoteStateStore = new RemoteStateStore(OutputWindowId);
        remoteStateStore.UpdateLocalMode(SceneMode.Screen);
        PopulateLanAddresses();
        ModePicker.Items.Add(new ComboBoxItem { Content = "OBS camera", Tag = SceneMode.Screen });
        ModePicker.Items.Add(new ComboBoxItem { Content = "Physical camera", Tag = SceneMode.PhysicalCamera });
        ModePicker.Items.Add(new ComboBoxItem { Content = "OBS + camera corner", Tag = SceneMode.ScreenPlusPhysicalCameraCorner });
        ModePicker.Items.Add(new ComboBoxItem { Content = "Black", Tag = SceneMode.Black });
        ModePicker.SelectedIndex = 0;
        PhysicalCameraPicker.Items.Add(new ComboBoxItem { Content = "No camera", Tag = null });
        PhysicalCameraPicker.SelectedIndex = 0;
        OutputResolutionPicker.Items.Add(new ComboBoxItem { Content = "1280×720 (720p)", Tag = DefaultOutputSize });
        OutputResolutionPicker.Items.Add(new ComboBoxItem { Content = "1920×1080 (1080p)", Tag = new OutputSize(1920, 1080) });
        OutputResolutionPicker.SelectedIndex = 0;
        modeSyncTimer.Start();
        AppRoot.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadBrandArtworkAsync();
        await LoadDemoCardPreviewAsync();
        await RefreshPhysicalDevicesAsync();
        await RefreshAudioEndpointsAsync();
    }

    private async Task LoadBrandArtworkAsync()
    {
        try
        {
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
            var imageFile = await StorageFile.GetFileFromPathAsync(Path.Combine(assetsPath, "BetterDemo-Brand.png"));
            using var imageStream = await imageFile.OpenAsync(FileAccessMode.Read);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(imageStream);
            BrandMark.Source = bitmap;
            AppWindow.SetIcon(Path.Combine(assetsPath, "BetterDemo.ico"));
        }
        catch (Exception exception)
        {
            Report($"Brand artwork could not be loaded: {exception.Message}");
        }
    }

    private async Task LoadDemoCardPreviewAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "BetterDemo-DemoCard.png");
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            DemoCardPreview.Source = bitmap;
        }
        catch (Exception exception)
        {
            Report($"Built-in demo image could not be loaded: {exception.Message}");
        }
    }

    private async void StartPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (starting || stopping) return;
        if (renderer is not null)
        {
            await StopPreviewAsync();
            return;
        }

        starting = true;
        StartPreviewButton.IsEnabled = false;
        OutputResolutionPicker.IsEnabled = false;
        PerformanceText.Text = "Preview metrics: starting…";
        SetStatus("Starting OutputWindow and D3D11 renderer…");
        try
        {
            await StartPreviewAsync();
            StartPreviewButton.Content = "Stop preview";
            ShowOutputButton.IsEnabled = true;
            HideOutputButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            await StopPreviewAsync();
            Report($"Preview could not start: {exception.Message}");
        }
        finally
        {
            starting = false;
            StartPreviewButton.IsEnabled = true;
            if (renderer is null) OutputResolutionPicker.IsEnabled = true;
        }
    }

    private async void ShowOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (windowAdapter is null) return;
        try
        {
            await windowAdapter.CreateOutputWindowAsync(OutputWindowId);
            await windowAdapter.ShowAsync(OutputWindowId);
            remoteStateStore.UpdateLocalOutputVisibility(true);
            Report("OutputWindow is visible and ready to select in Discord Go Live.");
        }
        catch (Exception exception)
        {
            Report($"OutputWindow could not be shown: {exception.Message}");
        }
    }

    private async void HideOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (windowAdapter is null) return;
        try
        {
            await windowAdapter.HideAsync(OutputWindowId);
            remoteStateStore.UpdateLocalOutputVisibility(false);
            Report("OutputWindow is hidden. Use Show OutputWindow to restore it.");
        }
        catch (Exception exception)
        {
            Report($"OutputWindow could not be hidden: {exception.Message}");
        }
    }

    private async void RemoteServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (remoteServer is not null)
        {
            await StopRemoteServerAsync();
            return;
        }

        if (LanAddressPicker.SelectedItem is not ComboBoxItem { Tag: IPAddress address })
        {
            Report("Select an available private IPv4 address before starting LAN control.");
            return;
        }

        RemoteServerButton.IsEnabled = false;
        RemoteServer? server = null;
        var publisher = new RemotePreviewPublisher(remotePreviewFrames);
        try
        {
            var pairing = new PairingService();
            server = new RemoteServer(
                address,
                47829,
                pairing,
                new RemoteCommandProcessor(pairing, remoteStateStore),
                remotePreviewFrames);
            previewPublisher = publisher;
            await server.StartAsync();
            remoteServer = server;
            RemoteServerButton.Content = "Stop LAN remote";
            PairingInfoText.Text = $"Address: {server.BaseAddress}\nOne-time code: {server.PairingCode.Code}\nTLS certificate SHA-256: {FormatFingerprint(server.CertificateSha256)}\nCode expires: {server.PairingCode.ExpiresAt.LocalDateTime:T}";
            await ShowPairingQrAsync(server);
            Report("Authenticated LAN control is active on the selected private network interface.");
        }
        catch (Exception exception)
        {
            previewPublisher = null;
            await publisher.DisposeAsync();
            remotePreviewFrames.Clear();
            if (server is not null) await server.DisposeAsync();
            await StopRemoteServerAsync();
            Report($"LAN remote could not start: {exception.Message}");
        }
        finally
        {
            RemoteServerButton.IsEnabled = true;
        }
    }

    private void ModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModePicker.SelectedItem is ComboBoxItem { Tag: SceneMode mode })
        {
            Volatile.Write(ref selectedMode, (int)mode);
            if (!synchronizingRemoteMode) remoteStateStore.UpdateLocalMode(mode);
        }
    }

    private void SynchronizeModePicker()
    {
        var mode = remoteStateStore.Snapshot.Mode;
        if ((SceneMode)Volatile.Read(ref selectedMode) == mode) return;
        var item = ModePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => candidate.Tag is SceneMode candidateMode && candidateMode == mode);
        if (item is null) return;

        synchronizingRemoteMode = true;
        try { ModePicker.SelectedItem = item; }
        finally { synchronizingRemoteMode = false; }
    }

    private async void PhysicalCameraPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (populatingPhysicalDevices || videoAdapter is null || renderer is null) return;
        selectedPhysical = GetSelectedPhysicalCamera();
        await ReplacePhysicalCaptureAsync(selectedPhysical);
    }

    private async void RefreshPhysicalCamerasButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshPhysicalDevicesAsync();

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        windowLifetime.Cancel();
        modeSyncTimer.Stop();
        await StopPreviewAsync();
        await StopRemoteServerAsync();
        await StopAudioRouteAsync();
        if (audioAdapter is not null) await audioAdapter.DisposeAsync();
        await sceneAssetStore.DisposeAsync();
    }

    private async void AddImageSourceButton_Click(object sender, RoutedEventArgs e)
    {
        SourcesPanel.IsEnabled = false;
        try
        {
            var currentLayers = Volatile.Read(ref importedImageLayers);
            if (currentLayers.Length >= 16)
            {
                Report("A scene can contain up to 16 imported image sources.");
                return;
            }

            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selectedFile = await picker.PickSingleFileAsync();
            if (selectedFile is null || windowLifetime.IsCancellationRequested) return;
            await AddImportedImageSourceAsync(selectedFile.Path, selectedFile.Name);
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Image source could not be added: {exception.Message}");
        }
        finally
        {
            SourcesPanel.IsEnabled = true;
        }
    }

    private async void AddDemoSourceButton_Click(object sender, RoutedEventArgs e)
    {
        AddDemoSourceButton.IsEnabled = false;
        SourcesPanel.IsEnabled = false;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "BetterDemo-DemoCard.png");
            await AddImportedImageSourceAsync(path, "BetterDemo Demo Signal");
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Demo image could not be added: {exception.Message}");
        }
        finally
        {
            AddDemoSourceButton.IsEnabled = true;
            SourcesPanel.IsEnabled = true;
        }
    }

    private async Task AddImportedImageSourceAsync(string path, string displayName)
    {
        var currentLayers = Volatile.Read(ref importedImageLayers);
        if (currentLayers.Length >= 16)
        {
            Report("A scene can contain up to 16 imported image sources.");
            return;
        }

        var asset = await sceneAssetStore.ImportImageAsync(path, windowLifetime.Token);
        if (!sceneAssetStore.TryGetFrame(asset, out var frame) || frame is null)
            throw new InvalidDataException("The imported image could not be decoded for scene rendering.");

        var entry = new ImportedImageLayer(
            displayName,
            new SceneLayer(
                new SceneLayerId($"image:{asset.AssetId}"),
                SceneLayerKind.Image,
                0,
                new NormalizedTransform(0, 0, 1, 1),
                assetReference: asset),
            frame);
        importedLayerContents[entry.Layer.Id] = new ImportedLayerContent(entry.DisplayName, entry.Frame);
        var updatedLayers = currentLayers.Append(entry).ToArray();
        CommitImportedImageLayers(updatedLayers, entry.Layer.Id, "Add image source");
        Report($"Added image source '{displayName}' ({frame.Format.Width}×{frame.Format.Height}).");
    }

    private void RemoveImageSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageSourcesList.SelectedItem is not ListViewItem { Tag: ImportedImageLayer selected }) return;
        var currentLayers = Volatile.Read(ref importedImageLayers);
        var selectedIndex = Array.FindIndex(currentLayers, layer => layer.Layer.Id == selected.Layer.Id);
        var updatedLayers = currentLayers.Where(layer => layer.Layer.Id != selected.Layer.Id).ToArray();
        SceneLayerId? nextSelection = updatedLayers.Length == 0
            ? null
            : updatedLayers[Math.Min(Math.Max(selectedIndex, 0), updatedLayers.Length - 1)].Layer.Id;
        CommitImportedImageLayers(updatedLayers, nextSelection, "Remove image source");
        Report($"Removed image source '{selected.DisplayName}' from the active scene.");
    }

    private async void SaveSceneButton_Click(object sender, RoutedEventArgs e)
    {
        SourcesPanel.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("BetterDemo scene", new List<string> { ".json" });
            picker.SuggestedFileName = "BetterDemo-scene";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selectedFile = await picker.PickSaveFileAsync();
            if (selectedFile is null || windowLifetime.IsCancellationRequested) return;

            var state = remoteStateStore.Snapshot;
            var customLayers = Volatile.Read(ref importedImageLayers);
            var corner = CreateCameraCornerLayout(state.CameraCorner, outputSize);
            var document = BuildSceneDocument(state.Mode, corner, customLayers, state.LayerVisibility);
            var names = customLayers.ToDictionary(layer => layer.Layer.Id.Value, layer => layer.DisplayName, StringComparer.Ordinal);
            var json = UserSceneFileSerializer.Serialize(state, document, names);
            await File.WriteAllTextAsync(selectedFile.Path, json, windowLifetime.Token);
            Report($"Saved scene '{selectedFile.Name}' with {customLayers.Length} custom layer(s).");
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Scene could not be saved: {exception.Message}");
        }
        finally
        {
            SourcesPanel.IsEnabled = true;
        }
    }

    private async void LoadSceneButton_Click(object sender, RoutedEventArgs e)
    {
        SourcesPanel.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selectedFile = await picker.PickSingleFileAsync();
            if (selectedFile is null || windowLifetime.IsCancellationRequested) return;

            const long maximumSceneFileBytes = 4 * 1024 * 1024;
            if (new FileInfo(selectedFile.Path).Length > maximumSceneFileBytes)
                throw new InvalidDataException("Scene files must not exceed 4 MiB.");
            var json = await File.ReadAllTextAsync(selectedFile.Path, windowLifetime.Token);
            var diagnostics = new List<DiagnosticEvent>();
            var loaded = UserSceneFileSerializer.Deserialize(
                json,
                new DelegateSceneAssetResolver(asset => asset.Location.StartsWith("source://", StringComparison.Ordinal) || File.Exists(asset.Location)),
                new DelegateDiagnosticsSink(diagnostics.Add));
            var customLayers = loaded.Document.Layers
                .Where(layer => !layer.Id.Value.StartsWith("preset:", StringComparison.Ordinal))
                .ToArray();
            if (customLayers.Length > 16)
                throw new InvalidDataException("Scene files may contain at most 16 custom layers.");

            var restoredLayers = new List<ImportedImageLayer>(customLayers.Length);
            foreach (var originalLayer in customLayers)
            {
                var layer = originalLayer;
                VideoFrame? frame = null;
                if (layer.Kind == SceneLayerKind.Image && layer.AssetReference is { } originalAsset)
                {
                    try
                    {
                        var asset = await sceneAssetStore.ImportOrLoadImageAsync(originalAsset, windowLifetime.Token);
                        if (!sceneAssetStore.TryGetFrame(asset, out frame) || frame is null)
                            throw new InvalidDataException("The image asset did not produce a renderable frame.");
                        layer = CloneLayerWithZIndex(layer, layer.ZIndex, asset);
                    }
                    catch (Exception exception)
                    {
                        layer = layer.AsDiagnosticPlaceholder($"Image asset '{originalAsset.AssetId}' could not be loaded: {exception.Message}");
                        diagnostics.Add(new DiagnosticEvent(
                            DiagnosticSeverity.Warning,
                            DiagnosticCode.SceneAssetMissing,
                            "scene",
                            layer.DiagnosticMessage!));
                    }
                }

                var displayName = loaded.LayerDisplayNames.TryGetValue(layer.Id.Value, out var savedName)
                    ? savedName
                    : layer.AssetReference is { } assetReference
                        ? Path.GetFileName(assetReference.Location)
                        : $"{layer.Kind} layer";
                restoredLayers.Add(new ImportedImageLayer(displayName, layer, frame));
            }

            var modeItem = ModePicker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is SceneMode mode && mode == loaded.Mode)
                ?? throw new InvalidDataException($"Scene mode '{loaded.Mode}' is unavailable in the editor.");
            ModePicker.SelectedItem = modeItem;
            foreach (var layer in loaded.Document.Layers.Where(layer => layer.Id.Value.StartsWith("preset:", StringComparison.Ordinal)))
                remoteStateStore.UpdateLocalLayerVisibility(layer.Id.Value, layer.IsVisible);
            remoteStateStore.UpdateLocalCameraCorner(loaded.CameraCorner);
            remoteStateStore.UpdateLocalTransform(loaded.Zoom, loaded.Pan.X, loaded.Pan.Y);
            remoteStateStore.UpdateLocalBlur(loaded.Blur);
            ResetCustomScene(restoredLayers, restoredLayers.FirstOrDefault()?.Layer.Id);
            Report($"Loaded scene '{selectedFile.Name}' with {restoredLayers.Count} custom layer(s); {diagnostics.Count} asset diagnostic(s).");
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Scene could not be loaded: {exception.Message}");
        }
        finally
        {
            SourcesPanel.IsEnabled = true;
        }
    }

    private void ToggleImageSourceVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageSourcesList.SelectedItem is not ListViewItem { Tag: ImportedImageLayer selected }) return;
        var currentLayers = Volatile.Read(ref importedImageLayers);
        var updatedLayers = currentLayers.Select(layer => layer.Layer.Id == selected.Layer.Id
            ? layer with { Layer = layer.Layer.WithVisibility(!layer.Layer.IsVisible) }
            : layer).ToArray();
        CommitImportedImageLayers(updatedLayers, selected.Layer.Id, "Toggle source visibility");
    }

    private void UndoSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!customSceneHistory.CanUndo) return;
        var selectedId = GetSelectedImageSourceId();
        if (customSceneHistory.Undo(customSceneDocument)) RefreshImportedImageLayers(selectedId);
    }

    private void RedoSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!customSceneHistory.CanRedo) return;
        var selectedId = GetSelectedImageSourceId();
        if (customSceneHistory.Redo(customSceneDocument)) RefreshImportedImageLayers(selectedId);
    }

    private void BringImageSourceForwardButton_Click(object sender, RoutedEventArgs e) => MoveSelectedImageSource(1);

    private void SendImageSourceBackwardButton_Click(object sender, RoutedEventArgs e) => MoveSelectedImageSource(-1);

    private void ImageSourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedId = (ImageSourcesList.SelectedItem as ListViewItem)?.Tag is ImportedImageLayer selected
            ? selected.Layer.Id
            : (SceneLayerId?)null;
        var layers = Volatile.Read(ref importedImageLayers);
        var index = selectedId is { } id ? Array.FindIndex(layers, layer => layer.Layer.Id == id) : -1;
        var hasSelection = index >= 0;
        RemoveImageSourceButton.IsEnabled = hasSelection;
        ToggleImageSourceVisibilityButton.IsEnabled = hasSelection;
        ToggleImageSourceVisibilityButton.Content = hasSelection && layers[index].Layer.IsVisible ? "Hide" : "Show";
        BringImageSourceForwardButton.IsEnabled = hasSelection && index < layers.Length - 1;
        SendImageSourceBackwardButton.IsEnabled = hasSelection && index > 0;

        updatingImageProperties = true;
        try
        {
            ImageSourceXSlider.IsEnabled = hasSelection;
            ImageSourceYSlider.IsEnabled = hasSelection;
            ImageSourceWidthSlider.IsEnabled = hasSelection;
            ImageSourceHeightSlider.IsEnabled = hasSelection;
            ImageSourceRotationSlider.IsEnabled = hasSelection;
            if (hasSelection)
            {
                var transform = layers[index].Layer.Transform;
                ImageSourceXSlider.Value = transform.X;
                ImageSourceYSlider.Value = transform.Y;
                ImageSourceWidthSlider.Value = transform.Width;
                ImageSourceHeightSlider.Value = transform.Height;
                ImageSourceRotationSlider.Value = transform.RotationDegrees;
            }
        }
        finally
        {
            updatingImageProperties = false;
        }
    }

    private void ImageSourceTransform_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (updatingImageProperties || ImageSourcesList.SelectedItem is not ListViewItem { Tag: ImportedImageLayer selected }) return;
        var currentLayers = Volatile.Read(ref importedImageLayers);
        var selectedIndex = Array.FindIndex(currentLayers, layer => layer.Layer.Id == selected.Layer.Id);
        if (selectedIndex < 0) return;

        var updatedLayers = currentLayers.ToArray();
        var transform = new NormalizedTransform(
            ImageSourceXSlider.Value,
            ImageSourceYSlider.Value,
            ImageSourceWidthSlider.Value,
            ImageSourceHeightSlider.Value,
            ImageSourceRotationSlider.Value);
        if (currentLayers[selectedIndex].Layer.Transform == transform) return;
        updatedLayers[selectedIndex] = currentLayers[selectedIndex] with
        {
            Layer = currentLayers[selectedIndex].Layer.WithTransform(transform)
        };
        Volatile.Write(ref importedImageLayers, updatedLayers);
        if (ImageSourcesList.SelectedItem is ListViewItem selectedItem)
            selectedItem.Tag = updatedLayers[selectedIndex];
        if (transformSliderGestureActive)
        {
            transformGestureChanged = true;
        }
        else
        {
            CommitImportedImageLayers(updatedLayers, selected.Layer.Id, "Transform source");
        }
    }

    private void ImageSourceTransform_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ImageSourcesList.SelectedItem is not ListViewItem { Tag: ImportedImageLayer }) return;
        transformSliderGestureActive = true;
        transformGestureChanged = false;
    }

    private void ImageSourceTransform_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!transformSliderGestureActive) return;
        transformSliderGestureActive = false;
        if (!transformGestureChanged) return;
        transformGestureChanged = false;
        var selectedId = GetSelectedImageSourceId();
        CommitImportedImageLayers(Volatile.Read(ref importedImageLayers), selectedId, "Transform source");
    }

    private void MoveSelectedImageSource(int offset)
    {
        if (ImageSourcesList.SelectedItem is not ListViewItem { Tag: ImportedImageLayer selected }) return;
        var currentLayers = Volatile.Read(ref importedImageLayers);
        var currentIndex = Array.FindIndex(currentLayers, layer => layer.Layer.Id == selected.Layer.Id);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= currentLayers.Length) return;

        var updatedLayers = currentLayers.ToArray();
        (updatedLayers[currentIndex], updatedLayers[targetIndex]) = (updatedLayers[targetIndex], updatedLayers[currentIndex]);
        CommitImportedImageLayers(updatedLayers, selected.Layer.Id, "Reorder sources");
    }

    private void CommitImportedImageLayers(IEnumerable<ImportedImageLayer> layers, SceneLayerId? selectedId, string commandName)
    {
        var entries = layers.ToArray();
        foreach (var entry in entries)
            importedLayerContents[entry.Layer.Id] = new ImportedLayerContent(entry.DisplayName, entry.Frame);

        customSceneHistory.Execute(customSceneDocument, new DelegateSceneCommand(commandName, document =>
        {
            foreach (var existing in document.Layers.ToArray()) document.RemoveLayer(existing.Id);
            for (var index = 0; index < entries.Length; index++)
                document.AddLayer(CloneLayerWithZIndex(entries[index].Layer, index));
        }));
        RefreshImportedImageLayers(selectedId);
    }

    private void ResetCustomScene(IEnumerable<ImportedImageLayer> layers, SceneLayerId? selectedId)
    {
        var entries = layers.ToArray();
        importedLayerContents.Clear();
        foreach (var entry in entries)
            importedLayerContents[entry.Layer.Id] = new ImportedLayerContent(entry.DisplayName, entry.Frame);
        customSceneDocument = BuildCustomSceneDocument(entries);
        customSceneHistory = new SceneCommandHistory();
        RefreshImportedImageLayers(selectedId);
    }

    private void RefreshImportedImageLayers(SceneLayerId? selectedId)
    {
        var snapshot = customSceneDocument.Layers.Select(layer =>
        {
            if (!importedLayerContents.TryGetValue(layer.Id, out var content))
                throw new InvalidOperationException($"Source metadata for '{layer.Id}' was not found.");
            return new ImportedImageLayer(content.DisplayName, layer, content.Frame);
        }).ToArray();
        Volatile.Write(ref importedImageLayers, snapshot);
        ImageSourcesList.Items.Clear();
        foreach (var layer in snapshot)
        {
            var visibility = layer.Layer.IsVisible ? "Visible" : "Hidden";
            ImageSourcesList.Items.Add(new ListViewItem
            {
                Content = $"{visibility} — {layer.Layer.Kind}: {layer.DisplayName}",
                Tag = layer
            });
        }

        var selectedIndex = selectedId is { } id
            ? Array.FindIndex(snapshot, layer => layer.Layer.Id == id)
            : -1;
        if (selectedIndex < 0 && snapshot.Length > 0) selectedIndex = 0;
        ImageSourcesList.SelectedIndex = selectedIndex;
        UndoSceneButton.IsEnabled = customSceneHistory.CanUndo;
        RedoSceneButton.IsEnabled = customSceneHistory.CanRedo;
    }

    private SceneLayerId? GetSelectedImageSourceId() =>
        (ImageSourcesList.SelectedItem as ListViewItem)?.Tag is ImportedImageLayer selected
            ? selected.Layer.Id
            : null;

    private async void RefreshAudioButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAudioEndpointsAsync();
    }

    private async Task RefreshAudioEndpointsAsync()
    {
        audioAdapter ??= new WasapiAudioAdapter();
        try
        {
            var endpoints = await audioAdapter.EnumerateActiveRenderEndpointsAsync();
            AudioOutputPicker.Items.Clear();
            foreach (var endpoint in endpoints)
            {
                var label = endpoint.IsVirtualCable ? $"{endpoint.DisplayName} (virtual cable)" : endpoint.DisplayName;
                AudioOutputPicker.Items.Add(new ComboBoxItem { Content = label, Tag = endpoint });
            }
            var cableIndex = endpoints.ToList().FindIndex(endpoint => endpoint.IsVirtualCable);
            if (endpoints.Count > 0) AudioOutputPicker.SelectedIndex = cableIndex >= 0 ? cableIndex : 0;
            AudioStatusText.Text = endpoints.Count == 0
                ? "No active audio output devices found. Connect or enable an output device, then refresh."
                : $"Found {endpoints.Count} active output device(s). System capture always excludes Discord.";
        }
        catch (Exception exception)
        {
            AudioStatusText.Text = $"Audio device refresh failed: {exception.Message}";
        }
    }

    private async void AudioRouteButton_Click(object sender, RoutedEventArgs e)
    {
        AudioRouteButton.IsEnabled = false;
        try
        {
            if (audioRoute is not null)
            {
                await StopAudioRouteAsync();
                return;
            }

            if (AudioOutputPicker.SelectedItem is not ComboBoxItem { Tag: AudioRenderEndpoint endpoint })
            {
                AudioStatusText.Text = "Refresh and select an active audio output device.";
                return;
            }

            audioAdapter ??= new WasapiAudioAdapter();
            var route = new ProcessAudioRoute(audioAdapter);
            try
            {
                await route.StartAsync(endpoint);
                route.IsMuted = AudioMuteSwitch.IsOn;
                route.OutputVolume = (float)(AudioVolumeSlider.Value / 100d);
                audioRoute = route;
                AudioRouteButton.Content = "Stop system audio";
                AudioToneButton.IsEnabled = true;
                AudioStatusText.Text = $"Routing all system audio except Discord to {endpoint.DisplayName}.";
            }
            catch
            {
                await route.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception)
        {
            AudioStatusText.Text = $"Audio route could not start: {exception.Message}";
            Report(AudioStatusText.Text);
        }
        finally
        {
            AudioRouteButton.IsEnabled = true;
        }
    }

    private async Task StopAudioRouteAsync()
    {
        var route = audioRoute;
        audioRoute = null;
        if (route is null) return;
        try
        {
            await route.DisposeAsync();
            AudioStatusText.Text = route.LastFailure is null
                ? "Audio route stopped."
                : $"Audio route stopped after error: {route.LastFailure.Message}";
        }
        catch (Exception exception)
        {
            AudioStatusText.Text = $"Audio route cleanup reported an error: {exception.Message}";
        }
        finally
        {
            AudioRouteButton.Content = "Start system audio";
            AudioToneButton.IsEnabled = false;
        }
    }

    private async void AudioToneButton_Click(object sender, RoutedEventArgs e)
    {
        if (audioRoute is null) return;
        AudioToneButton.IsEnabled = false;
        try
        {
            await audioRoute.TestToneAsync();
            AudioStatusText.Text = "Sent a 0.5-second test tone to the selected output device.";
        }
        catch (Exception exception)
        {
            AudioStatusText.Text = $"Test tone failed: {exception.Message}";
        }
        finally
        {
            AudioToneButton.IsEnabled = audioRoute is not null;
        }
    }

    private void AudioVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (audioRoute is not null) audioRoute.OutputVolume = (float)(e.NewValue / 100d);
    }

    private void AudioMuteSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (audioRoute is not null) audioRoute.IsMuted = AudioMuteSwitch.IsOn;
    }

    private async Task StartPreviewAsync()
    {
        outputSize = OutputResolutionPicker.SelectedItem is ComboBoxItem { Tag: OutputSize selectedSize }
            ? selectedSize
            : DefaultOutputSize;
        windowAdapter = new Win32WindowAdapter(EnqueueOnUiThread, "BetterDemo OutputWindow", outputSize.Width, outputSize.Height);
        await windowAdapter.CreateOutputWindowAsync(OutputWindowId);
        if (outputPlacement.Load() is { } savedPosition)
        {
            try
            {
                if (await windowAdapter.TryRestorePositionAsync(OutputWindowId, savedPosition))
                    Report("OutputWindow position restored.");
                else
                    Report("Saved OutputWindow position is outside the available monitors; using the default position.");
            }
            catch (Exception exception)
            {
                Report($"OutputWindow position could not be restored: {exception.Message}");
            }
        }
        await windowAdapter.ShowAsync(OutputWindowId);
        remoteStateStore.UpdateLocalOutputVisibility(true);

        renderAdapter = new D3D11RenderAdapter(windowAdapter);
        renderer = new D3D11Renderer(renderAdapter, OutputWindowId);
        await renderer.InitializeAsync(outputSize.Width, outputSize.Height);

        videoAdapter = new MediaFoundationVideoAdapter();
        var devices = await RefreshPhysicalDevicesAsync();

        var obsDevice = devices.FirstOrDefault(device =>
            device.Kind == VideoDeviceKind.ObsVirtualCamera && device.Formats.Count > 0);
        if (obsDevice is null)
        {
            Report("OBS Virtual Camera is unavailable. OutputWindow will show a diagnostic placeholder.");
        }
        else
        {
            var obsFormat = SelectFormat(obsDevice.Formats);
            obsCapture = new ObsVirtualCameraCapture(videoAdapter, obsFormat, new DiagnosticsSink(Report));
            await Task.Run(async () => await obsCapture.StartAsync());
            if (obsCapture.State == SourceState.Running)
            {
                (obsPumpCancellation, obsPumpTask) = StartFramePump(obsCapture, obsFrames, "OBS Virtual Camera");
                Report($"Capturing OBS Virtual Camera at {obsFormat.Width}×{obsFormat.Height} {obsFormat.PixelFormat}.");
            }
            else
            {
                Report("OBS Virtual Camera is unavailable. OutputWindow will show a diagnostic placeholder.");
            }
        }

        selectedPhysical = GetSelectedPhysicalCamera();
        await ReplacePhysicalCaptureAsync(selectedPhysical);

        renderCancellation = new CancellationTokenSource();
        renderTask = Task.Run(() => RenderLoopAsync(renderCancellation.Token));
        SetStatus("Preview is running. Share the separate OutputWindow through Discord Go Live.");
    }

    private async Task StopPreviewAsync()
    {
        if (stopping) return;
        stopping = true;
        renderCancellation?.Cancel();
        try
        {
            if (renderTask is not null)
            {
                try { await renderTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { Report("Renderer did not stop within five seconds."); }
            }

            await StopPhysicalCaptureCoreAsync();
            await StopObsCaptureAsync();
            obsFrames.Clear();
            physicalFrames.Clear();

            if (renderer is not null)
            {
                await renderer.DisposeAsync();
                renderer = null;
            }
            if (renderAdapter is not null)
            {
                await renderAdapter.DisposeAsync();
                renderAdapter = null;
            }
            if (videoAdapter is not null)
            {
                await videoAdapter.DisposeAsync();
                videoAdapter = null;
            }
            if (windowAdapter is not null)
            {
                try
                {
                    if (await windowAdapter.GetPositionAsync(OutputWindowId) is { } currentPosition)
                        outputPlacement.Save(currentPosition);
                }
                catch (InvalidOperationException)
                {
                    // A manually closed OutputWindow has no position to save.
                }
                catch (Exception exception)
                {
                    Report($"OutputWindow position could not be saved: {exception.Message}");
                }
                await windowAdapter.DisposeAsync();
                windowAdapter = null;
            }

            renderCancellation?.Dispose();
            obsPumpCancellation?.Dispose();
            physicalPumpCancellation?.Dispose();
            renderCancellation = null;
            obsPumpCancellation = null;
            physicalPumpCancellation = null;
            renderTask = null;
            ShowOutputButton.IsEnabled = false;
            HideOutputButton.IsEnabled = false;
            StartPreviewButton.Content = "Start preview";
            OutputResolutionPicker.IsEnabled = true;
            PerformanceText.Text = "Preview metrics: idle.";
            SetStatus("Preview stopped.");
        }
        catch (Exception exception)
        {
            Report($"Preview cleanup reported an error: {exception.Message}");
        }
        finally
        {
            stopping = false;
        }
    }

    private async Task ReplacePhysicalCaptureAsync(VideoDeviceFormatSelection? selection)
    {
        await physicalSourceGate.WaitAsync();
        try
        {
            await StopPhysicalCaptureCoreAsync();
            if (selection is null || videoAdapter is null || renderer is null)
            {
                if (selection is null && (SceneMode)Volatile.Read(ref selectedMode) is SceneMode.PhysicalCamera or SceneMode.ScreenPlusPhysicalCameraCorner)
                {
                    Report("No supported physical camera is selected. A diagnostic placeholder will be shown.");
                }
                return;
            }

            var capture = new PhysicalCameraCapture(
                videoAdapter,
                selection.Device.Id,
                selection.Format,
                new DiagnosticsSink(Report));
            physicalCapture = capture;
            await Task.Run(async () => await capture.StartAsync());
            if (capture.State == SourceState.Running)
            {
                (physicalPumpCancellation, physicalPumpTask) = StartFramePump(capture, physicalFrames, selection.Device.DisplayName);
                Report($"Capturing {selection.Device.DisplayName} at {selection.Format.Width}×{selection.Format.Height} {selection.Format.PixelFormat}.");
            }
            else
            {
                Report($"{selection.Device.DisplayName} is unavailable. A diagnostic placeholder will be shown.");
                await capture.DisposeAsync();
                physicalCapture = null;
            }
        }
        catch (Exception exception)
        {
            Report($"Physical camera could not start: {exception.Message}");
            if (physicalCapture is not null)
            {
                await physicalCapture.DisposeAsync();
                physicalCapture = null;
            }
        }
        finally
        {
            physicalSourceGate.Release();
        }
    }

    private async Task StopPhysicalCaptureCoreAsync()
    {
        if (physicalCapture is not null)
        {
            try { await Task.Run(async () => await physicalCapture.StopAsync()); }
            catch (Exception exception) { Report($"Physical camera stop failed: {exception.Message}"); }
        }
        physicalPumpCancellation?.Cancel();
        if (physicalPumpTask is not null)
        {
            try { await physicalPumpTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Report("Physical camera frame pump did not stop within three seconds."); }
            catch (Exception exception) { Report($"Physical camera frame pump failed: {exception.Message}"); }
        }
        if (physicalCapture is not null)
        {
            try { await physicalCapture.DisposeAsync(); }
            catch (Exception exception) { Report($"Physical camera cleanup failed: {exception.Message}"); }
            physicalCapture = null;
        }
        physicalPumpCancellation?.Dispose();
        physicalPumpCancellation = null;
        physicalPumpTask = null;
        physicalFrames.Clear();
    }

    private async Task StopObsCaptureAsync()
    {
        if (obsCapture is not null)
        {
            try { await Task.Run(async () => await obsCapture.StopAsync()); }
            catch (Exception exception) { Report($"OBS camera stop failed: {exception.Message}"); }
        }
        obsPumpCancellation?.Cancel();
        if (obsPumpTask is not null)
        {
            try { await obsPumpTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Report("OBS frame pump did not stop within three seconds."); }
            catch (Exception exception) { Report($"OBS camera frame pump failed: {exception.Message}"); }
        }
        if (obsCapture is not null)
        {
            try { await obsCapture.DisposeAsync(); }
            catch (Exception exception) { Report($"OBS camera cleanup failed: {exception.Message}"); }
            obsCapture = null;
        }
        obsPumpCancellation?.Dispose();
        obsPumpCancellation = null;
        obsPumpTask = null;
        obsFrames.Clear();
    }

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        var activeOutputSize = outputSize;
        var metricsWindowStart = Stopwatch.GetTimestamp();
        var obsSkippedAtStart = obsFrames.ReplacedFrameCount;
        var physicalSkippedAtStart = physicalFrames.ReplacedFrameCount;
        long renderedFrames = 0;
        long totalRenderTicks = 0;
        long maximumRenderTicks = 0;
        VideoFrame? currentObs = null;
        VideoFrame? currentPhysical = null;
        SceneComposition? lastComposition = null;
        SceneComposition? transitionFrom = null;
        SceneMode? lastSceneMode = null;
        long transitionStartedAt = 0;
        string? lastReportedError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = Stopwatch.GetTimestamp();
                if (obsFrames.TryTakeLatest(out var nextObs))
                {
                    currentObs?.Dispose();
                    currentObs = nextObs;
                }
                if (physicalFrames.TryTakeLatest(out var nextPhysical))
                {
                    currentPhysical?.Dispose();
                    currentPhysical = nextPhysical;
                }

                var nowTicks = Stopwatch.GetTimestamp();
                var nowFrequency = Stopwatch.Frequency;
                if (currentObs is not null && !VideoFrameFreshness.IsFresh(currentObs.Stamp, nowTicks, nowFrequency, MaximumFrameAge))
                {
                    currentObs.Dispose();
                    currentObs = null;
                }
                if (currentPhysical is not null && !VideoFrameFreshness.IsFresh(currentPhysical.Stamp, nowTicks, nowFrequency, MaximumFrameAge))
                {
                    currentPhysical.Dispose();
                    currentPhysical = null;
                }

                try
                {
                    var sceneState = remoteStateStore.Snapshot;
                    if (lastSceneMode is { } previousMode && sceneState.Mode != previousMode)
                    {
                        transitionFrom = lastComposition;
                        transitionStartedAt = Stopwatch.GetTimestamp();
                    }
                    lastSceneMode = sceneState.Mode;
                    if (sceneState.OutputVisible != lastOutputVisibility && windowAdapter is not null)
                    {
                        if (sceneState.OutputVisible)
                            await windowAdapter.ShowAsync(OutputWindowId, cancellationToken).ConfigureAwait(false);
                        else
                            await windowAdapter.HideAsync(OutputWindowId, cancellationToken).ConfigureAwait(false);
                        lastOutputVisibility = sceneState.OutputVisible;
                    }

                    var transform = new SourceViewTransform(
                        zoom: sceneState.Zoom,
                        panX: sceneState.Pan.X,
                        panY: sceneState.Pan.Y);
                    var corner = CreateCameraCornerLayout(
                        sceneState.CameraCorner,
                        new OutputSize(activeOutputSize.Width, activeOutputSize.Height));
                    var blurRegions = sceneState.Blur.Enabled
                        ? new[] { new BlurRegion(0, 0, 1, 1, (int)Math.Clamp(Math.Round(sceneState.Blur.Radius), 1, 32)) }
                        : Array.Empty<BlurRegion>();
                    var options = new SceneRenderOptions(activeOutputSize.Width, activeOutputSize.Height, transform, corner, blurRegions);
                    var imageLayers = Volatile.Read(ref importedImageLayers);
                    var document = BuildSceneDocument(sceneState.Mode, corner, imageLayers, sceneState.LayerVisibility);
                    var layerFrames = new Dictionary<SceneLayerId, VideoFrame>(2 + imageLayers.Length);
                    if (currentObs is not null) layerFrames[SceneDocumentFactory.ObsVirtualCameraLayerId] = currentObs;
                    if (currentPhysical is not null) layerFrames[SceneDocumentFactory.PhysicalCameraLayerId] = currentPhysical;
                    foreach (var imageLayer in imageLayers)
                        if (imageLayer.Frame is { } frame) layerFrames[imageLayer.Layer.Id] = frame;
                    var renderStart = Stopwatch.GetTimestamp();
                    var transitionProgress = transitionFrom is null
                        ? 1d
                        : Math.Clamp(Stopwatch.GetElapsedTime(transitionStartedAt).TotalMilliseconds / 280d, 0, 1);
                    transitionProgress = transitionProgress * transitionProgress * (3 - 2 * transitionProgress);
                    var composition = await renderer!.RenderAsync(
                        document, layerFrames, options, cancellationToken, transitionFrom, transitionProgress);
                    lastComposition = composition;
                    if (transitionProgress >= 1) transitionFrom = null;
                    Volatile.Read(ref previewPublisher)?.TryPublish(composition);
                    var renderTicks = Stopwatch.GetTimestamp() - renderStart;
                    renderedFrames++;
                    totalRenderTicks += renderTicks;
                    maximumRenderTicks = Math.Max(maximumRenderTicks, renderTicks);
                    if (composition.Diagnostics.Count > 0)
                    {
                        var message = string.Join(" ", composition.Diagnostics.Distinct(StringComparer.Ordinal));
                        if (!string.Equals(message, lastReportedError, StringComparison.Ordinal)) Report(message);
                        lastReportedError = message;
                    }
                    else
                    {
                        lastReportedError = null;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    var message = $"Output rendering failed: {exception.Message}";
                    if (!string.Equals(message, lastReportedError, StringComparison.Ordinal)) Report(message);
                    lastReportedError = message;
                }

                var elapsedSeconds = Stopwatch.GetElapsedTime(metricsWindowStart).TotalSeconds;
                if (elapsedSeconds >= 5)
                {
                    var fps = renderedFrames / elapsedSeconds;
                    var averageMs = renderedFrames == 0 ? 0 : totalRenderTicks * 1000.0 / Stopwatch.Frequency / renderedFrames;
                    var maximumMs = maximumRenderTicks * 1000.0 / Stopwatch.Frequency;
                    var obsSkippedNow = obsFrames.ReplacedFrameCount;
                    var physicalSkippedNow = physicalFrames.ReplacedFrameCount;
                    var obsSkipped = obsSkippedNow - obsSkippedAtStart;
                    var physicalSkipped = physicalSkippedNow - physicalSkippedAtStart;
                    var obsDropped = obsCapture?.DroppedFrameCount ?? 0;
                    var physicalDropped = physicalCapture?.DroppedFrameCount ?? 0;
                    var metrics = $"{activeOutputSize.Width}×{activeOutputSize.Height}: {fps:F1} rendered FPS; render {averageMs:F1} ms avg / {maximumMs:F1} ms max; latest-frame skips (5s) OBS {obsSkipped}, camera {physicalSkipped}; capture drops OBS {obsDropped}, camera {physicalDropped}.";
                    dispatcherQueue.TryEnqueue(() => PerformanceText.Text = metrics);
                    metricsWindowStart = Stopwatch.GetTimestamp();
                    obsSkippedAtStart = obsSkippedNow;
                    physicalSkippedAtStart = physicalSkippedNow;
                    renderedFrames = 0;
                    totalRenderTicks = 0;
                    maximumRenderTicks = 0;
                }

                await FramePacer.WaitUntilNextFrameAsync(frameStart, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            currentObs?.Dispose();
            currentPhysical?.Dispose();
        }
    }

    private (CancellationTokenSource Cancellation, Task Pump) StartFramePump(
        IVideoSource source,
        LatestFrameSlot slot,
        string sourceName)
    {
        var cancellation = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in source.ReadFramesAsync(cancellation.Token).ConfigureAwait(false))
                {
                    slot.Publish(frame);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Report($"{sourceName} frame stream stopped: {exception.Message}");
            }
        });
        return (cancellation, pump);
    }

    private async Task<IReadOnlyList<VideoDeviceDescriptor>> RefreshPhysicalDevicesAsync()
    {
        await physicalDeviceRefreshGate.WaitAsync(windowLifetime.Token);
        RefreshPhysicalCamerasButton.IsEnabled = false;
        var previousSelection = selectedPhysical ?? GetSelectedPhysicalCamera();
        var preferredDeviceId = previousSelection?.Device.Id;
        var restartCapture = physicalCapture is not null;
        try
        {
            PhysicalCameraStatusText.Text = "Searching for connected cameras…";
            if (restartCapture) await StopPhysicalCaptureCoreAsync();

            IReadOnlyList<VideoDeviceDescriptor> devices;
            if (videoAdapter is not null)
            {
                var activeAdapter = videoAdapter;
                devices = await Task.Run(async () => await activeAdapter.EnumerateDevicesAsync(windowLifetime.Token));
            }
            else
            {
                await using var discoveryAdapter = new MediaFoundationVideoAdapter();
                devices = await Task.Run(async () => await discoveryAdapter.EnumerateDevicesAsync(windowLifetime.Token));
            }

            var physicalDevices = devices
                .Where(device => device.Kind == VideoDeviceKind.PhysicalCamera && device.Formats.Count > 0)
                .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            PopulatePhysicalDevices(physicalDevices, preferredDeviceId);
            selectedPhysical = GetSelectedPhysicalCamera();
            if (physicalDevices.Length == 0)
            {
                Report("No physical camera was found. Connect it, then select Refresh.");
            }
            else
            {
                Report($"Found {physicalDevices.Length} physical camera(s); {selectedPhysical?.Device.DisplayName ?? "No camera"} is selected.");
            }

            return devices;
        }
        catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
        {
            return Array.Empty<VideoDeviceDescriptor>();
        }
        catch (Exception exception)
        {
            PhysicalCameraStatusText.Text = "Camera scan failed. Check that Windows can access the device, then refresh.";
            Report($"Physical cameras could not be enumerated: {exception.Message}");
            selectedPhysical = previousSelection;
            return Array.Empty<VideoDeviceDescriptor>();
        }
        finally
        {
            if (restartCapture && !windowLifetime.IsCancellationRequested)
                await ReplacePhysicalCaptureAsync(selectedPhysical);
            RefreshPhysicalCamerasButton.IsEnabled = true;
            physicalDeviceRefreshGate.Release();
        }
    }

    private void PopulatePhysicalDevices(IReadOnlyList<VideoDeviceDescriptor> devices, VideoDeviceId? preferredDeviceId)
    {
        populatingPhysicalDevices = true;
        try
        {
            PhysicalCameraPicker.Items.Clear();
            PhysicalCameraPicker.Items.Add(new ComboBoxItem { Content = "No camera", Tag = null });
            foreach (var device in devices)
            {
                PhysicalCameraPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{device.DisplayName} ({device.Formats.Max(format => format.Width)}×{device.Formats.Max(format => format.Height)})",
                    Tag = device
                });
            }
            var selectedDeviceIndex = preferredDeviceId is { } preferred
                ? Array.FindIndex(devices.ToArray(), device => device.Id == preferred)
                : -1;
            PhysicalCameraPicker.SelectedIndex = selectedDeviceIndex >= 0
                ? selectedDeviceIndex + 1
                : devices.Count > 0 ? 1 : 0;
            PhysicalCameraStatusText.Text = devices.Count == 0
                ? "No camera found yet. Connect it and press Refresh."
                : $"{devices.Count} camera(s) found. {((ComboBoxItem)PhysicalCameraPicker.SelectedItem).Content} is selected.";
        }
        finally
        {
            populatingPhysicalDevices = false;
        }
    }

    private VideoDeviceFormatSelection? GetSelectedPhysicalCamera()
    {
        if (PhysicalCameraPicker.SelectedItem is not ComboBoxItem { Tag: VideoDeviceDescriptor device }) return null;
        return new VideoDeviceFormatSelection(device, SelectFormat(device.Formats));
    }

    private static VideoFrameFormat SelectFormat(IReadOnlyList<VideoFrameFormat> formats) =>
        formats.OrderByDescending(format => format.PixelFormat == VideoPixelFormat.Bgra32)
            .ThenByDescending(format => (long)format.Width * format.Height)
            .First();

    private bool EnqueueOnUiThread(Action action) => dispatcherQueue.TryEnqueue(() => action());

    private void SetStatus(string message)
    {
        if (dispatcherQueue.HasThreadAccess)
        {
            StatusText.Text = message;
        }
        else
        {
            dispatcherQueue.TryEnqueue(() => StatusText.Text = message);
        }
    }

    private void Report(string message)
    {
        pendingDiagnostics.Enqueue($"{DateTime.Now:T}  {message}");
        dispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = message;
            while (pendingDiagnostics.TryDequeue(out var diagnostic))
            {
                DiagnosticsList.Items.Insert(0, diagnostic);
                while (DiagnosticsList.Items.Count > 40) DiagnosticsList.Items.RemoveAt(DiagnosticsList.Items.Count - 1);
            }
        });
    }

    private void PopulateLanAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses
                    .Select(unicast => new { unicast.Address, network.Name, network.NetworkInterfaceType }))
                .Where(entry => IsPrivateIpv4(entry.Address))
                .GroupBy(entry => entry.Address)
                .Select(group => group.OrderByDescending(entry => entry.NetworkInterfaceType == NetworkInterfaceType.Wireless80211).First())
                .OrderByDescending(entry => entry.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ThenByDescending(entry => entry.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .ThenBy(entry => entry.Address.ToString(), StringComparer.Ordinal)
                .ToArray();
            foreach (var address in addresses)
            {
                var connectionType = address.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    _ => address.Name
                };
                LanAddressPicker.Items.Add(new ComboBoxItem { Content = $"{connectionType} · {address.Address}", Tag = address.Address });
            }
            if (addresses.Length > 0) LanAddressPicker.SelectedIndex = 0;
        }
        catch (NetworkInformationException exception)
        {
            Report($"Private LAN addresses could not be listed: {exception.Message}");
        }
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private async Task StopRemoteServerAsync()
    {
        if (remoteServer is null)
        {
            PairingInfoText.Text = "Remote control is disabled.";
            RemoteServerButton.Content = "Start LAN remote";
            PairingQrPanel.Visibility = Visibility.Collapsed;
            PairingQrImage.Source = null;
            return;
        }

        var server = remoteServer;
        remoteServer = null;
        try { await server.DisposeAsync(); }
        catch (Exception exception) { Report($"LAN remote shutdown reported an error: {exception.Message}"); }
        var publisher = Interlocked.Exchange(ref previewPublisher, null);
        if (publisher is not null)
        {
            try { await publisher.DisposeAsync(); }
            catch (Exception exception) { Report($"LAN preview shutdown reported an error: {exception.Message}"); }
        }
        remotePreviewFrames.Clear();
        PairingInfoText.Text = "Remote control is disabled.";
        RemoteServerButton.Content = "Start LAN remote";
        PairingQrPanel.Visibility = Visibility.Collapsed;
        PairingQrImage.Source = null;
        Report("Authenticated LAN control is stopped.");
    }

    private async Task ShowPairingQrAsync(RemoteServer server)
    {
        var payload = $"betterdemo://pair?host={Uri.EscapeDataString(server.BindAddress.ToString())}" +
                      $"&port={server.Port}&code={Uri.EscapeDataString(server.PairingCode.Code)}" +
                      $"&fingerprint={Uri.EscapeDataString(server.CertificateSha256)}";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        var png = qr.GetGraphic(6);

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        PairingQrImage.Source = bitmap;
        PairingQrPanel.Visibility = Visibility.Visible;
    }

    private static CameraCornerLayout CreateCameraCornerLayout(CameraCornerState state, OutputSize size) => new(
        scale: state.Scale,
        margin: Math.Clamp(state.Margin / Math.Min(size.Width, size.Height), 0, 0.45),
        rotationDegrees: state.AngleDegrees);

    private static SceneDocument BuildSceneDocument(
        SceneMode mode,
        CameraCornerLayout corner,
        IReadOnlyList<ImportedImageLayer> customLayers,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null)
    {
        var document = SceneDocumentFactory.CreatePreset(mode, corner);
        foreach (var customLayer in customLayers)
            document.AddLayer(CloneLayerWithZIndex(customLayer.Layer, document.Layers.Count));
        return visibilityOverrides is null
            ? document
            : SceneDocumentFactory.ApplyVisibilityOverrides(document, visibilityOverrides);
    }

    private static SceneDocument BuildCustomSceneDocument(IReadOnlyList<ImportedImageLayer> customLayers)
    {
        var document = new SceneDocument();
        for (var index = 0; index < customLayers.Count; index++)
            document.AddLayer(CloneLayerWithZIndex(customLayers[index].Layer, index));
        return document;
    }

    private static SceneLayer CloneLayerWithZIndex(SceneLayer layer, int zIndex, SceneAssetReference? assetOverride = null)
    {
        var asset = assetOverride ?? layer.AssetReference;
        return layer.Kind switch
        {
            SceneLayerKind.Color => new SceneLayer(
                layer.Id, layer.Kind, zIndex, layer.Transform, layer.Opacity, layer.IsVisible, layer.IsLocked,
                fillColor: layer.FillColor),
            SceneLayerKind.Text => new SceneLayer(
                layer.Id, layer.Kind, zIndex, layer.Transform, layer.Opacity, layer.IsVisible, layer.IsLocked,
                textContent: layer.TextContent),
            SceneLayerKind.DiagnosticPlaceholder => SceneLayer.CreateDiagnosticPlaceholder(
                layer.Id, zIndex, layer.Transform, layer.Opacity, layer.IsVisible, layer.IsLocked, asset, layer.DiagnosticMessage),
            _ => new SceneLayer(
                layer.Id, layer.Kind, zIndex, layer.Transform, layer.Opacity, layer.IsVisible, layer.IsLocked,
                assetReference: asset)
        };
    }

    private static string FormatFingerprint(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(index => fingerprint.Substring(index * 2, 2)));

    private sealed record ImportedImageLayer(
        string DisplayName,
        SceneLayer Layer,
        VideoFrame? Frame);

    private sealed record ImportedLayerContent(string DisplayName, VideoFrame? Frame);

    private sealed record VideoDeviceFormatSelection(VideoDeviceDescriptor Device, VideoFrameFormat Format);
    private readonly record struct OutputSize(int Width, int Height);

    private sealed class DiagnosticsSink(Action<string> report) : IDiagnosticsSink
    {
        public void Report(DiagnosticEvent diagnostic) => report($"{diagnostic.Severity}: {diagnostic.Component}: {diagnostic.Message}");
    }

    private sealed class LatestFrameSlot
    {
        private readonly object gate = new();
        private VideoFrame? frame;
        private long replacedFrameCount;

        public long ReplacedFrameCount => Interlocked.Read(ref replacedFrameCount);

        public void Publish(VideoFrame next)
        {
            VideoFrame? previous;
            lock (gate)
            {
                previous = frame;
                frame = next;
            }
            if (previous is not null)
            {
                previous.Dispose();
                Interlocked.Increment(ref replacedFrameCount);
            }
        }

        public bool TryTakeLatest(out VideoFrame? latest)
        {
            lock (gate)
            {
                latest = frame;
                frame = null;
                return latest is not null;
            }
        }

        public void Clear()
        {
            VideoFrame? previous;
            lock (gate)
            {
                previous = frame;
                frame = null;
            }
            previous?.Dispose();
        }
    }
}

using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using System.Collections.Generic;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class SceneCompositorTests
{
    private static readonly SceneCompositor Compositor = new();

    [Fact]
    public void Black_mode_is_opaque_and_ignores_both_sources()
    {
        var result = Compositor.Render(SceneMode.Black, null, null, new SceneRenderOptions(2, 1));

        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, result.Pixels.ToArray());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Document_composition_draws_color_and_media_layers_in_z_order_and_skips_hidden_layers()
    {
        var background = new SceneLayer(
            new SceneLayerId("background"),
            SceneLayerKind.Color,
            0,
            new NormalizedTransform(0, 0, 1, 1),
            fillColor: new SceneRgbaColor(0, 255, 0));
        var media = new SceneLayer(
            new SceneLayerId("media"),
            SceneLayerKind.Image,
            1,
            new NormalizedTransform(0.5, 0, 0.5, 1),
            assetReference: new SceneAssetReference("asset", "assets/image.png"));
        var hidden = new SceneLayer(
            new SceneLayerId("hidden"),
            SceneLayerKind.Color,
            2,
            new NormalizedTransform(0, 0, 1, 1),
            isVisible: false,
            fillColor: new SceneRgbaColor(255, 255, 255));
        using var redFrame = SolidFrame(1, 1, 255, 0, 0);
        var document = new SceneDocument([hidden, media, background]);

        var result = Compositor.Render(
            document,
            new Dictionary<SceneLayerId, VideoFrame> { [media.Id] = redFrame },
            new SceneRenderOptions(4, 2));

        Assert.Equal((0, 255, 0, 255), Pixel(result, 0, 0));
        Assert.Equal((0, 255, 0, 255), Pixel(result, 1, 1));
        Assert.Equal((0, 0, 255, 255), Pixel(result, 2, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(result, 3, 1));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Document_composition_blends_layer_alpha_and_reports_missing_media_content()
    {
        var background = new SceneLayer(
            new SceneLayerId("background"),
            SceneLayerKind.Color,
            0,
            new NormalizedTransform(0, 0, 1, 1),
            fillColor: new SceneRgbaColor(255, 0, 0));
        var overlay = new SceneLayer(
            new SceneLayerId("overlay"),
            SceneLayerKind.Color,
            1,
            new NormalizedTransform(0, 0, 0.5, 1),
            opacity: 0.25,
            fillColor: new SceneRgbaColor(0, 0, 255));
        var missingImage = new SceneLayer(
            new SceneLayerId("missing"),
            SceneLayerKind.Image,
            2,
            new NormalizedTransform(0.75, 0, 0.25, 1),
            assetReference: new SceneAssetReference("missing-asset", "assets/missing.png"));

        var result = Compositor.Render(new SceneDocument([background, overlay, missingImage]), null, new SceneRenderOptions(4, 2));

        Assert.Equal((64, 0, 191, 255), Pixel(result, 0, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(result, 2, 0));
        Assert.NotEqual((0, 0, 0, 255), Pixel(result, 3, 0));
        Assert.Contains("missing-asset", Assert.Single(result.Diagnostics));
    }

    [Fact]
    public void Document_composition_uses_supplied_rasterized_content_for_text_layers()
    {
        var text = new SceneLayer(
            new SceneLayerId("title"),
            SceneLayerKind.Text,
            0,
            new NormalizedTransform(0, 0, 1, 1),
            textContent: new SceneTextContent("LIVE", fontSize: 40));
        using var whiteFrame = SolidFrame(1, 1, 255, 255, 255);

        var result = Compositor.Render(
            new SceneDocument([text]),
            new Dictionary<SceneLayerId, VideoFrame> { [text.Id] = whiteFrame },
            new SceneRenderOptions(2, 1));

        Assert.Equal((255, 255, 255, 255), Pixel(result, 0, 0));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Document_image_layer_applies_rotation_to_its_content()
    {
        using var source = Frame(2, 2,
            (0, 0, 255, 255), (0, 255, 0, 255),
            (255, 0, 0, 255), (0, 255, 255, 255));
        var layer = new SceneLayer(
            new SceneLayerId("rotated-image"),
            SceneLayerKind.Image,
            0,
            new NormalizedTransform(0, 0, 1, 1, rotationDegrees: 90),
            assetReference: new SceneAssetReference("rotation-test", "assets/rotation.png"));

        var result = Compositor.Render(
            new SceneDocument([layer]),
            new Dictionary<SceneLayerId, VideoFrame> { [layer.Id] = source },
            new SceneRenderOptions(2, 2));

        Assert.Equal((255, 0, 0, 255), Pixel(result, 0, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(result, 1, 0));
        Assert.Equal((0, 255, 255, 255), Pixel(result, 0, 1));
        Assert.Equal((0, 255, 0, 255), Pixel(result, 1, 1));
    }

    [Theory]
    [InlineData(SceneMode.Black)]
    [InlineData(SceneMode.Screen)]
    [InlineData(SceneMode.PhysicalCamera)]
    [InlineData(SceneMode.ScreenPlusPhysicalCameraCorner)]
    public void Built_in_scene_documents_match_the_legacy_preset_composition(SceneMode mode)
    {
        using var obs = Frame(2, 2,
            (0, 0, 255, 255), (0, 255, 0, 255),
            (255, 0, 0, 255), (255, 255, 0, 255));
        using var physical = SolidFrame(2, 2, 255, 0, 255);
        var cameraCorner = new CameraCornerLayout(scale: 0.5, margin: 0.05, rotationDegrees: 0);
        var options = new SceneRenderOptions(
            4,
            2,
            new SourceViewTransform(zoom: 1.5, panX: 0.1, mirrorHorizontally: true),
            cameraCorner);
        var frames = new Dictionary<SceneLayerId, VideoFrame>
        {
            [SceneDocumentFactory.ObsVirtualCameraLayerId] = obs,
            [SceneDocumentFactory.PhysicalCameraLayerId] = physical
        };

        var legacy = Compositor.Render(mode, obs, physical, options);
        var document = Compositor.Render(SceneDocumentFactory.CreatePreset(mode, cameraCorner), frames, options);

        Assert.Equal(legacy.Pixels.ToArray(), document.Pixels.ToArray());
        Assert.Equal(legacy.Diagnostics, document.Diagnostics);
    }

    [Fact]
    public void Screen_mode_uses_obs_feed_and_physical_mode_uses_webcam_feed()
    {
        using var obs = SolidFrame(2, 1, 0, 0, 255);
        using var physical = SolidFrame(2, 1, 255, 0, 0);
        var options = new SceneRenderOptions(2, 1);

        var screen = Compositor.Render(SceneMode.Screen, obs, physical, options);
        var camera = Compositor.Render(SceneMode.PhysicalCamera, obs, physical, options);

        Assert.Equal((255, 0, 0, 255), Pixel(screen, 0, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(camera, 0, 0));
    }

    [Fact]
    public void Corner_mode_keeps_obs_as_background_and_places_physical_camera_in_top_right()
    {
        using var obs = SolidFrame(1, 1, 0, 0, 255);
        using var physical = SolidFrame(1, 1, 255, 0, 0);
        var options = new SceneRenderOptions(4, 2, cameraCorner: new CameraCornerLayout(scale: 0.5, margin: 0));

        var result = Compositor.Render(SceneMode.ScreenPlusPhysicalCameraCorner, obs, physical, options);

        Assert.Equal((0, 0, 255, 255), Pixel(result, 2, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(result, 3, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(result, 0, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(result, 2, 1));
    }

    [Fact]
    public void Crop_and_mirror_change_the_sampled_source_region()
    {
        using var source = Frame(4, 1,
            (0, 0, 255, 255),
            (0, 255, 0, 255),
            (255, 0, 0, 255),
            (255, 255, 0, 255));
        var options = new SceneRenderOptions(2, 1, new SourceViewTransform(crop: new NormalizedCrop(0.5, 0, 0.5, 1)));

        var cropped = Compositor.Render(SceneMode.Screen, source, null, options);
        var mirrored = Compositor.Render(
            SceneMode.Screen,
            source,
            null,
            new SceneRenderOptions(2, 1, new SourceViewTransform(mirrorHorizontally: true)));

        Assert.Equal((255, 0, 0, 255), Pixel(cropped, 0, 0));
        Assert.Equal((255, 255, 0, 255), Pixel(cropped, 1, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(mirrored, 0, 0));
        Assert.Equal((0, 255, 0, 255), Pixel(mirrored, 1, 0));
    }

    [Fact]
    public void Zoom_and_pan_are_applied_to_the_full_screen_source()
    {
        using var source = Frame(4, 1,
            (0, 0, 255, 255),
            (0, 255, 0, 255),
            (255, 0, 0, 255),
            (255, 255, 0, 255));
        var zoomed = Compositor.Render(
            SceneMode.Screen,
            source,
            null,
            new SceneRenderOptions(2, 1, new SourceViewTransform(zoom: 2)));
        var panned = Compositor.Render(
            SceneMode.Screen,
            source,
            null,
            new SceneRenderOptions(2, 1, new SourceViewTransform(zoom: 2, panX: 0.75)));

        Assert.Equal((0, 255, 0, 255), Pixel(zoomed, 0, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(zoomed, 1, 0));
        Assert.NotEqual(Pixel(zoomed, 1, 0), Pixel(panned, 1, 0));
    }

    [Fact]
    public void Missing_source_returns_visible_diagnostic_placeholder_and_message()
    {
        var result = Compositor.Render(SceneMode.Screen, null, null, new SceneRenderOptions(8, 4));

        Assert.Contains(result.Pixels.Span.ToArray(), value => value > 100);
        Assert.Contains("OBS Virtual Camera", Assert.Single(result.Diagnostics));
    }

    [Fact]
    public void Blur_only_changes_the_selected_region()
    {
        using var source = Frame(3, 1,
            (0, 0, 255, 255),
            (0, 255, 0, 255),
            (255, 0, 0, 255));
        var result = Compositor.Render(
            SceneMode.Screen,
            source,
            null,
            new SceneRenderOptions(3, 1, blurRegions: [new BlurRegion(0, 0, 2d / 3, 1, radius: 1)]));

        Assert.Equal((0, 127, 127, 255), Pixel(result, 0, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(result, 2, 0));
    }

    [Fact]
    public void Blur_can_cover_the_entire_output_frame_including_its_edges()
    {
        using var source = Frame(3, 1,
            (0, 0, 255, 255),
            (0, 255, 0, 255),
            (255, 0, 0, 255));
        var result = Compositor.Render(
            SceneMode.Screen,
            source,
            null,
            new SceneRenderOptions(3, 1, blurRegions: [new BlurRegion(0, 0, 1, 1, radius: 1)]));

        Assert.Equal((0, 127, 127, 255), Pixel(result, 0, 0));
        Assert.Equal((85, 85, 85, 255), Pixel(result, 1, 0));
        Assert.Equal((127, 127, 0, 255), Pixel(result, 2, 0));
    }

    [Fact]
    public void Crossfade_blends_frames_smoothly_and_reaches_the_new_scene()
    {
        using var red = SolidFrame(1, 1, 255, 0, 0);
        using var blue = SolidFrame(1, 1, 0, 0, 255);
        var from = Compositor.Render(SceneMode.Screen, red, null, new SceneRenderOptions(1, 1));
        var to = Compositor.Render(SceneMode.Screen, blue, null, new SceneRenderOptions(1, 1));

        Assert.Equal((0, 0, 255, 255), Pixel(SceneCompositor.Crossfade(from, to, 0), 0, 0));
        Assert.Equal((128, 0, 128, 255), Pixel(SceneCompositor.Crossfade(from, to, 0.5), 0, 0));
        Assert.Same(to, SceneCompositor.Crossfade(from, to, 1));
    }

    [Fact]
    public void Nv12_limited_range_black_and_white_convert_to_bgra()
    {
        var format = new VideoFrameFormat(2, 2, VideoPixelFormat.Nv12, 2);
        using var source = VideoFrame.CopyFrom(
            new VideoDeviceId("nv12-test"),
            format,
            Stamp(1),
            new byte[] { 16, 235, 16, 235, 128, 128 });

        var result = Compositor.Render(SceneMode.Screen, source, null, new SceneRenderOptions(2, 2));

        Assert.Equal((0, 0, 0, 255), Pixel(result, 0, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(result, 1, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    public void Invalid_zoom_is_rejected(double zoom)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceViewTransform(zoom));
    }

    private static VideoFrame SolidFrame(int width, int height, byte red, byte green, byte blue)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }
        return VideoFrame.CopyFrom(new VideoDeviceId("solid-test"), new VideoFrameFormat(width, height, VideoPixelFormat.Bgra32, width * 4), Stamp(1), pixels);
    }

    private static VideoFrame Frame(int width, int height, params (byte Blue, byte Green, byte Red, byte Alpha)[] colors)
    {
        Assert.Equal(width * height, colors.Length);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < colors.Length; index++)
        {
            var offset = index * 4;
            pixels[offset] = colors[index].Blue;
            pixels[offset + 1] = colors[index].Green;
            pixels[offset + 2] = colors[index].Red;
            pixels[offset + 3] = colors[index].Alpha;
        }
        return VideoFrame.CopyFrom(new VideoDeviceId("pattern-test"), new VideoFrameFormat(width, height, VideoPixelFormat.Bgra32, width * 4), Stamp(1), pixels);
    }

    private static (byte Blue, byte Green, byte Red, byte Alpha) Pixel(SceneComposition frame, int x, int y)
    {
        var offset = (y * frame.Width + x) * 4;
        return (frame.Pixels.Span[offset], frame.Pixels.Span[offset + 1], frame.Pixels.Span[offset + 2], frame.Pixels.Span[offset + 3]);
    }

    private static VideoFrameStamp Stamp(ulong sequence) => new(sequence, new QpcTimestamp((long)sequence, 10_000_000));
}

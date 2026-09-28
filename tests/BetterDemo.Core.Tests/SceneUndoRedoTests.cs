using BetterDemo.Core.Scene;
using BetterDemo.Core.UndoRedo;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class SceneUndoRedoTests
{
    [Fact]
    public void Mutate_then_throw_rolls_back_document_and_preserves_undo_redo_branch()
    {
        var layer = new SceneLayer(
            new SceneLayerId("layer"),
            SceneLayerKind.Color,
            0,
            new NormalizedTransform(0, 0, 0.5, 0.5),
            fillColor: SceneRgbaColor.Black);
        var document = new SceneDocument(new[] { layer });
        var history = new SceneCommandHistory();
        history.Execute(document, new SetLayerVisibilityCommand(layer.Id, false));
        Assert.True(history.Undo(document));

        var before = SceneSerializer.Serialize(document);
        var undoCount = history.UndoCount;
        var redoCount = history.RedoCount;
        var throwingCommand = new DelegateSceneCommand("mutate then throw", currentDocument =>
        {
            currentDocument.SetLayerVisibility(layer.Id, false);
            throw new InvalidOperationException("simulated command failure");
        });

        Assert.Throws<InvalidOperationException>(() => history.Execute(document, throwingCommand));

        Assert.Equal(before, SceneSerializer.Serialize(document));
        Assert.Equal(undoCount, history.UndoCount);
        Assert.Equal(redoCount, history.RedoCount);
        Assert.True(history.Redo(document));
        Assert.False(document.Layers.Single().IsVisible);
    }

    [Fact]
    public void Undo_and_redo_restore_exact_scene_state_without_divergence()
    {
        var layer = new SceneLayer(
            new SceneLayerId("layer"),
            SceneLayerKind.Color,
            0,
            new NormalizedTransform(0, 0, 0.5, 0.5),
            fillColor: SceneRgbaColor.Black);
        var document = new SceneDocument(new[] { layer });
        var history = new SceneCommandHistory();
        var initial = SceneSerializer.Serialize(document);
        var movedTransform = new NormalizedTransform(0.25, 0.35, 0.4, 0.3, 18, 1.1, 0.95);

        history.Execute(document, new SetLayerTransformCommand(layer.Id, movedTransform));
        history.Execute(document, new SetLayerVisibilityCommand(layer.Id, false));
        var changed = SceneSerializer.Serialize(document);

        Assert.NotEqual(initial, changed);
        Assert.True(history.Undo(document));
        Assert.True(document.Layers.Single().IsVisible);
        Assert.True(history.Undo(document));
        Assert.Equal(initial, SceneSerializer.Serialize(document));
        Assert.False(history.Undo(document));
        Assert.Equal(initial, SceneSerializer.Serialize(document));

        Assert.True(history.Redo(document));
        Assert.True(history.Redo(document));
        Assert.Equal(changed, SceneSerializer.Serialize(document));
        Assert.False(history.Redo(document));
    }

    [Fact]
    public void New_command_after_undo_discards_redo_branch_and_boundaries_are_safe()
    {
        var layer = new SceneLayer(
            new SceneLayerId("layer"),
            SceneLayerKind.Color,
            0,
            new NormalizedTransform(0, 0, 0.5, 0.5),
            fillColor: SceneRgbaColor.Black);
        var document = new SceneDocument(new[] { layer });
        var history = new SceneCommandHistory();

        Assert.False(history.Undo(document));
        Assert.False(history.Redo(document));

        history.Execute(document, new SetLayerVisibilityCommand(layer.Id, false));
        Assert.True(history.Undo(document));
        history.Execute(document, new SetLayerVisibilityCommand(layer.Id, true));

        Assert.False(history.Redo(document));
        Assert.True(document.Layers.Single().IsVisible);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void Move_layer_command_restores_both_order_and_z_indices()
    {
        var first = new SceneLayer(new SceneLayerId("first"), SceneLayerKind.Color, 0, new NormalizedTransform(0, 0, 0.2, 0.2), fillColor: SceneRgbaColor.Black);
        var second = new SceneLayer(new SceneLayerId("second"), SceneLayerKind.Color, 1, new NormalizedTransform(0.2, 0, 0.2, 0.2), fillColor: SceneRgbaColor.Black);
        var third = new SceneLayer(new SceneLayerId("third"), SceneLayerKind.Color, 2, new NormalizedTransform(0.4, 0, 0.2, 0.2), fillColor: SceneRgbaColor.Black);
        var document = new SceneDocument(new[] { first, second, third });
        var history = new SceneCommandHistory();

        history.Execute(document, new MoveLayerCommand(third.Id, 0));
        Assert.Equal(new[] { "third", "first", "second" }, document.Layers.Select(layer => layer.Id.Value));
        Assert.Equal(new[] { 0, 1, 2 }, document.Layers.Select(layer => layer.ZIndex));

        Assert.True(history.Undo(document));
        Assert.Equal(new[] { "first", "second", "third" }, document.Layers.Select(layer => layer.Id.Value));
        Assert.Equal(new[] { 0, 1, 2 }, document.Layers.Select(layer => layer.ZIndex));
    }
}

using BetterDemo.Core.Scene;

namespace BetterDemo.Core.UndoRedo;

public interface ISceneCommand
{
    string Name { get; }

    void Execute(SceneDocument document);
}

public sealed class SetLayerTransformCommand : ISceneCommand
{
    public SetLayerTransformCommand(SceneLayerId layerId, NormalizedTransform transform)
    {
        LayerId = layerId;
        Transform = transform;
    }

    public SceneLayerId LayerId { get; }
    public NormalizedTransform Transform { get; }
    public string Name => "Set layer transform";

    public void Execute(SceneDocument document) => document.SetLayerTransform(LayerId, Transform);
}

public sealed class SetLayerVisibilityCommand : ISceneCommand
{
    public SetLayerVisibilityCommand(SceneLayerId layerId, bool isVisible)
    {
        LayerId = layerId;
        IsVisible = isVisible;
    }

    public SceneLayerId LayerId { get; }
    public bool IsVisible { get; }
    public string Name => "Set layer visibility";

    public void Execute(SceneDocument document) => document.SetLayerVisibility(LayerId, IsVisible);
}

public sealed class MoveLayerCommand : ISceneCommand
{
    public MoveLayerCommand(SceneLayerId layerId, int targetIndex)
    {
        LayerId = layerId;
        TargetIndex = targetIndex;
    }

    public SceneLayerId LayerId { get; }
    public int TargetIndex { get; }
    public string Name => "Move layer";

    public void Execute(SceneDocument document) => document.MoveLayer(LayerId, TargetIndex);
}

public sealed class DelegateSceneCommand : ISceneCommand
{
    private readonly Action<SceneDocument> execute;

    public DelegateSceneCommand(string name, Action<SceneDocument> execute)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A command name is required.", nameof(name));
        Name = name;
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public string Name { get; }

    public void Execute(SceneDocument document) => execute(document);
}

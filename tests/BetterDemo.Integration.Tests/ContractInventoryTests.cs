using BetterDemo.Core.Contracts;
using BetterDemo.Interop.D3D11;
using BetterDemo.Interop.MediaFoundation;
using BetterDemo.Interop.Wasapi;
using BetterDemo.Interop.Win32;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Integration.Tests;

public sealed class ContractInventoryTests
{
    private readonly ITestOutputHelper output;

    public ContractInventoryTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void Contract_inventory_prints_modes_and_dependency_edges()
    {
        output.WriteLine("SCENE_WIRE_NAMES");
        foreach (var wireName in SceneModeWireNames.All)
        {
            output.WriteLine($"  {wireName}");
        }

        var root = FindRepositoryRoot();
        var projectFiles = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var edges = new List<string>();
        foreach (var projectFile in projectFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            foreach (var line in File.ReadLines(projectFile).Where(line => line.Contains("ProjectReference", StringComparison.Ordinal)))
            {
                var includeStart = line.IndexOf("Include=\"", StringComparison.Ordinal);
                if (includeStart < 0)
                {
                    continue;
                }

                includeStart += "Include=\"".Length;
                var includeEnd = line.IndexOf('"', includeStart);
                var referencePath = line[includeStart..includeEnd];
                edges.Add($"{projectName} -> {Path.GetFileNameWithoutExtension(referencePath)}");
            }
        }

        output.WriteLine("PROJECT_DEPENDENCY_EDGES");
        foreach (var edge in edges.OrderBy(edge => edge, StringComparer.Ordinal))
        {
            output.WriteLine($"  {edge}");
        }

        var expectedEdges = new[]
        {
            "BetterDemo.App -> BetterDemo.Audio",
            "BetterDemo.App -> BetterDemo.Capture",
            "BetterDemo.App -> BetterDemo.Core",
            "BetterDemo.App -> BetterDemo.Interop",
            "BetterDemo.App -> BetterDemo.Remote",
            "BetterDemo.Audio -> BetterDemo.Core",
            "BetterDemo.Audio -> BetterDemo.Interop",
            "BetterDemo.Capture -> BetterDemo.Core",
            "BetterDemo.Capture -> BetterDemo.Interop",
            "BetterDemo.Core.Tests -> BetterDemo.Core",
            "BetterDemo.Core.Tests -> BetterDemo.Remote",
            "BetterDemo.Integration.Tests -> BetterDemo.App",
            "BetterDemo.Integration.Tests -> BetterDemo.Audio",
            "BetterDemo.Integration.Tests -> BetterDemo.Capture",
            "BetterDemo.Integration.Tests -> BetterDemo.Core",
            "BetterDemo.Integration.Tests -> BetterDemo.Interop",
            "BetterDemo.Integration.Tests -> BetterDemo.Remote",
            "BetterDemo.Interop -> BetterDemo.Core",
            "BetterDemo.Remote -> BetterDemo.Core"
        };

        Assert.Equal(
            new[] { "black", "screen", "physicalCamera", "screenPlusPhysicalCameraCorner" },
            SceneModeWireNames.All);
        Assert.Equal(expectedEdges, edges.OrderBy(edge => edge, StringComparer.Ordinal));
        Assert.Equal(edges.Count, edges.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(edges, edge => edge.Contains("BetterDemo.App -> BetterDemo.Core.Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(edges, edge => edge.Contains("BetterDemo.Core ->", StringComparison.Ordinal));

        var projectGraph = edges
            .Select(edge => edge.Split(" -> ", StringSplitOptions.None))
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(parts => parts[1]).ToArray(),
                StringComparer.Ordinal);
        Assert.DoesNotContain(projectGraph.Keys, project => HasDependencyCycle(project, projectGraph));

        var sourceFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        var forbiddenTokens = new[]
        {
            "Windows.Graphics.Capture",
            "IDXGIOutputDuplication",
            "Desktop Duplication",
            "BitBlt",
            "PrintWindow",
            "kernel-mode",
            "waveOut",
            "DirectSound",
            "physical speakers",
            "physical headphones"
        };
        var forbiddenMatches = sourceFiles
            .SelectMany(file => File.ReadLines(file).Select(line => (file, line)))
            .Where(item => forbiddenTokens.Any(token => item.line.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        output.WriteLine("FORBIDDEN_NATIVE_FALLBACK_SCAN");
        output.WriteLine($"  matches: {forbiddenMatches.Length}");
        Assert.Empty(forbiddenMatches);
        var nativeImportsOutsideInterop = sourceFiles
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}BetterDemo.Interop{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadLines(file).Any(line => line.Contains("DllImport", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(nativeImportsOutsideInterop);

        Assert.True(typeof(IMediaFoundationVideoAdapter).IsInterface);
        Assert.True(typeof(ID3D11RenderAdapter).IsInterface);
        Assert.True(typeof(IWasapiAudioAdapter).IsInterface);
        Assert.True(typeof(IWin32WindowAdapter).IsInterface);
        Assert.False(D3D11DeviceHandle.TryCreate(0, out _));
        Assert.False(D3D11SurfaceHandle.TryCreate(0, out _));
        Assert.False(NativeWindowHandle.TryCreate(0, out _));
        Assert.False(default(D3D11DeviceHandle).IsValid);
        Assert.False(default(D3D11SurfaceHandle).IsValid);
        Assert.False(default(NativeWindowHandle).IsValid);
        Assert.True(D3D11DeviceHandle.TryCreate(1, out var deviceHandle));
        Assert.True(NativeWindowHandle.TryCreate(1, out var nativeWindowHandle));
        Assert.True(deviceHandle.IsValid);
        Assert.True(nativeWindowHandle.IsValid);
        var outputWindowId = new OutputWindowId("output-window");
        var binding = new OutputWindowBinding(outputWindowId, nativeWindowHandle);
        Assert.Equal(outputWindowId, binding.OutputWindowId);
        Assert.Equal(nativeWindowHandle, binding.NativeWindowHandle);
        Assert.Throws<ArgumentException>(() => new OutputWindowBinding(outputWindowId, default));
        Assert.Contains(
            typeof(IWin32WindowAdapter).GetMethods(),
            method => method.Name == nameof(IWin32WindowAdapter.CreateOutputWindowAsync) &&
                      method.GetParameters().Any(parameter => parameter.ParameterType == typeof(OutputWindowId)));
        output.WriteLine("NATIVE_BOUNDARIES: PASS");
    }

    private static string FindRepositoryRoot()
    {
        var metadataRoot = typeof(ContractInventoryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BetterDemo.RepositoryRoot")
            ?.Value;
        if (metadataRoot is not null)
        {
            var resolvedMetadataRoot = Path.GetFullPath(metadataRoot);
            if (File.Exists(Path.Combine(resolvedMetadataRoot, "BetterDemo.sln")))
            {
                return resolvedMetadataRoot;
            }
        }

        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "BetterDemo.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("BetterDemo.sln was not found from the configured repository root or working directories.");
    }

    private static bool HasDependencyCycle(
        string project,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        ISet<string>? path = null)
    {
        path ??= new HashSet<string>(StringComparer.Ordinal);
        if (!path.Add(project))
        {
            return true;
        }

        if (!graph.TryGetValue(project, out var dependencies))
        {
            return false;
        }

        return dependencies.Any(dependency => HasDependencyCycle(dependency, graph, new HashSet<string>(path, StringComparer.Ordinal)));
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;

namespace MISAML.GodotAPI;

// Direct Godot API primitives, runs inside the isolated AssemblyLoadContext,
// just a foundation for the API.
public static class API
{
    private static SceneTree? _sceneTree;

    public static bool IsReady => _sceneTree != null;

    internal static void Bootstrap(GodotObject anyBridgeInstance)
    {
        if (_sceneTree != null) return; // only need to do this once, ever
 
        try
        {
            _sceneTree = (SceneTree)Engine.GetMainLoop();
            Log("bootstrapped, SceneTree acquired");
        }
        catch (Exception ex)
        {
            Log($"Bootstrap failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static void Log(string message) => Logger.Write(nameof(API), message);

    public static SceneTree SceneTree
        => _sceneTree ?? throw new InvalidOperationException("GodotAPI is not bootstrapped!");

    public static Node Root => SceneTree.Root;

    // if instance is, true, if not, value, using the 'is' keyword
    public static bool IsGodotObject(object? instance) => instance is GodotObject;

    public static Node[] GetChildren(Node node) => [.. node.GetChildren()];

    // BFS, not the fastest but it works compared to earlier attempts. Just a 
    // generic fallback until real caller needs point at something more
    // specific. Change later with proper API.
    public static Node? FindNodeByTypeName(Node root, string typeName)
    {
        Queue<Node> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            Node node = queue.Dequeue();

            if (node.GetType().Name == typeName)
                return node;

            foreach (Node child in node.GetChildren()) 
                queue.Enqueue(child);
        }

        return null; // not found
    }

    // add more shit later

    public static Color MakeColor(string hex) => new(hex);
 
    public static GodotObject Instantiate(string className)
    {
        var variant = ClassDB.Instantiate(className);
        return variant.AsGodotObject()
            ?? throw new Exception($"ClassDB.Instantiate({className}) returned null/non-object!");
    }
 
    public static void Set<T>(GodotObject instance, string propertyName, T value) where T : notnull
        => instance.Set(propertyName, Variant.From(value));
 
    public static Variant Get(GodotObject instance, string propertyName)
        => instance.Get(propertyName);
 
    public static Variant Call(GodotObject instance, string methodName, params Variant[] args)
        => instance.Call(methodName, args);
 
    public static void AddChildDeferred(Node parent, Node child)
        => parent.CallDeferred("add_child", child);
}

// What Loader talks to via reflection, just relays a live instance into
// API.Bootstrap the first time one shows up.
public static class GodotAPIHost
{
    public static void Initialize() => Logger.Write(nameof(GodotAPIHost), "Initialized, ready to receive relayed instances!");

    public static void OnInstanceRelayed(object instance)
    {
        try
        {
            if (instance is Godot.GodotObject godotObj)
                API.Bootstrap(godotObj);
        }
        catch (Exception e)
        {
            Logger.Write(nameof(GodotAPIHost), "OnInstanceRelayed failed: " + e);
        }
    }
}

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(Logger).Assembly.Location)!,
        "misaml.log"
    );
 
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public static void Write(string @class, string message)
    {
        var line = $"{_stopwatch.Elapsed.TotalSeconds:F6} [MISAML.GodotAPI.{@class}] {message}";
        File.AppendAllText(LogPath, line + "\n");
    }
}

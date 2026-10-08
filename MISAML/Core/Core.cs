using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace MISAML.Core;

// Runs in the .NET Default AssemblyLoadContext (see loader as to why).
// Purpose: always hook GodotBridgePatch when Mnemonimov loads (bootstrap),
// and if MISAML_DEBUG=1, run the MISAML.Debug methods.
public static class StartupHook
{   
    // path for log file
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!, /* null forgiving, location can be null */
        "misaml.log" /* the actual log file */
    );

    // one timer for the entire MISAML.Core/StartupHook thingy
    private static readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // just check env for debug logging
    internal static readonly bool DebugEnabled 
        = System.Environment.GetEnvironmentVariable("MISAML_DEBUG") != null;

    internal static void Log(string @class, string message)
    {
        var line = $"{_stopwatch.Elapsed.TotalSeconds:F6} [MISAML.Core.{@class}] {message}";
        File.AppendAllText(LogPath, line + "\n");
    }

    private static void Log(string message) => Log(nameof(StartupHook), message);

    internal static void DebugLog(string message) { if (DebugEnabled) Log(message); }

    public static void Initialize()
    {
        Log(nameof(StartupHook), "MISAML.Core starting...");

        var handled = new HashSet<Assembly>();

        void HandleAssembly(Assembly assembly)
        {
            if (!handled.Add(assembly)) return; // don't double handle the same assembly!!

            var loadedName = assembly.GetName().Name;

            // bootstrap patch, ALWAYS runs regardless of debug mode!! this is what
            // lets Loader grab a live GodotObject later, not optional!!
            if (loadedName == "Mnemonimov")
                GodotBridgePatch.TryPatch(Hooks.Harmony, assembly);

            // anything past here is just catalog stuff!!!!
            if (!DebugEnabled) return;

            // one search per assembly we actually care about, Bridge+Mnemonimov
            // both come from the same dll so they fire together!!
            switch (loadedName)
            {
                case "Asm":
                    RunSearch("Asm", Debug.Asm.Run(Hooks.Harmony, assembly));
                    break;
                case "Mnemonimov":
                    RunSearch("Bridge", Debug.Bridge.Run(Hooks.Harmony, assembly));
                    RunSearch("Mnemonimov", Debug.Mnemonimov.Run(Hooks.Harmony, assembly));
                    break;
                case "GodotSharp":
                    RunSearch("GodotSharp", Debug.GodotSharp.Run(Hooks.Harmony, assembly));
                    break;
            }
        }
 
        // subscribe FIRST so nothing loading from this point onwards can slip
        // through the gap between  "check what's loaded" and "start listening"
        AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
            HandleAssembly(args.LoadedAssembly);

        // then force check anything that already loaded before getting to this
        // point. before, Mnemonimov (the anchor assembly) was almost always 
        // already loaded by this point, so its AssemblyLoad event already fired 
        // and finished before we could listen for it, and it never loaded
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            HandleAssembly(assembly);
 
        Log(nameof(StartupHook), "MISAML.Core startup complete.");
    }

    //TODO: Add individual timestamping for each method ran in MISAML.Debug
    private static void RunSearch(string label, MISAML.Debug.SearchResult result)
    {
        Log(
            label,
            $"complete: {result.TypesScanned} types, {result.Patchable} patchable, " +
            $"{result.Skipped} skipped, {result.Failed} failed. " +
            "Full list at misaml-debug.log"
        );
    }
}

// The single mod-facing patching thing, this is very much WIP and WILL 
// be changed in the near future. A mod names any type+method it wants
// ("Asm.Running.VirtualMachine", "HandleSystemCall", or anything else)
// and patches, lazily, on first registration.
public static class Hooks
{
    // setup Log for Hooks
    private static void Log(string message) => StartupHook.Log(nameof(Hooks), message);

    // Harmony instance used for all MISAML patches.
    internal static readonly Harmony Harmony = new("MISAML");

    // registered hook handlers, keyed by type + method.
    private static readonly ConcurrentDictionary<
        string, /* name used for the override. the key. */
        Func<
            object?[], /* args passed to the hook, array. */
            HookResult /* value returned by the hook. */
        >
    > Overrides = new();

    // deduplication set for patched methods/Harmony targets.
    private static readonly HashSet<string> PatchedKeys = [];

    // protects patch state from concurrent access.
    private static readonly object PatchLock = new();

    // handler gets the call's args (Harmony's __args) and returns
    // either HookResult.Continue() or HookResult.Replace(value)
    // to skip the original (value is ignored for void methods).
    public static void Register(string typeName, string methodName, Func<object?[], HookResult> handler)
    {
        var key = Key(typeName, methodName);

        if (!Overrides.TryAdd(key, handler))
        {
            Log($"{key} already has a registered override, refusing duplicate.");
            return;
        }

        EnsurePatched(typeName, methodName, key);
    }

    // checks PatchedKeys to see if our method has been patched, if not, patch.
    private static void EnsurePatched(string typeName, string methodName, string key)
    {
        // locks, other threads wait before editing
        lock (PatchLock)
        {
            if (PatchedKeys.Contains(key)) return;

            var method = FindMethod(typeName, methodName);
            if (method != null)
            {
                ApplyPatch(method, key);    
                return;
            }
        }

        // called whenever a new assembly is loaded.
        // checks the loaded assembly for the method we are looking for.
        void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            var found = FindMethodIn(args.LoadedAssembly, typeName, methodName);
            if (found == null) return; /* not found, return */

            lock (PatchLock)
            {
                if (!PatchedKeys.Contains(key))
                    ApplyPatch(found, key);
            }

            // remove handler (OnAssemblyLoad) from AssemblyLoad method once
            // the method is found and patched.
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        // add handler (OnAssemblyLoad) to AssemblyLoad, so whenever
        // AssemblyLoad fires, we also run OnAssemblyLoad.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    // searches every assembly currently loaded for a method.
    private static MethodInfo? FindMethod(string typeName, string methodName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var found = FindMethodIn(asm, typeName, methodName);
            if (found != null) return found;
        }
        return null; /* not found */
    }

    // single assembly search for a method.
    private static MethodInfo? FindMethodIn(Assembly asm, string typeName, string methodName)
    {
        var type = asm.GetType(typeName);
        return type?.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    }

    // applies a Harmony prefix to the target method.
    private static void ApplyPatch(MethodInfo method, string key)
    {
        try
        {
            // void vs non-void needs different prefix signatures a by-ref
            // return (ref int etc.) still fails here either way
            var prefixName = method.ReturnType == typeof(void) ?
                nameof(GenericPrefixVoid) : nameof(GenericPrefixResult);

            var prefix = new HarmonyMethod(
                typeof(Hooks).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static));
            
            Harmony.Patch(method, prefix: prefix);

            // mark the target only after Harmony successfully patches/patched it.
            PatchedKeys.Add(key);
            Log($"patched {key}");
        }
        catch (Exception e)
        {
            // patch failed, so don't add the key to PatchedKeys.
            Log($"failed to patch {key}: {e.GetType().Name}: {e.Message}");
        }
    }

    // remember, SkipOriginal is for fully replacing the original method, if true

    // Harmony prefix for methods that return void.
    // returns true to run the original method, false to skip it.
    private static bool GenericPrefixVoid(MethodBase __originalMethod, object[] __args)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        //
        // out gives extra return from function, an extra result
        if (!TryGetHandler(__originalMethod, out var handler)) return true;

        // call the registered handler with the original method's arguments.
        // InvokeSafely returns a HookResult that details what to do.
        var result = InvokeSafely(handler!, __args, __originalMethod);

        // if SkipOriginal is true, prevent the original method from running.
        // if false, let the original method continue normally.
        return !result.SkipOriginal;
    }
    
    // runs a prefix for methods that return a value.
    //
    // unlike GenericPrefixVoid, this also receives __result.
    // __result is a reference to the original method's return value,
    // so changing it changes what the method returns.
    //
    // ref passes directly to the original value instead of making a copy, sorta.
    private static bool GenericPrefixResult(MethodBase __originalMethod, object[] __args, ref object __result)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(__originalMethod, out var handler)) return true;

        // if SkipOriginal is true, use the Value stored inside result
        // as the original method's return value.
        var result = InvokeSafely(handler!, __args, __originalMethod);
        
        // if SkipOriginal true, we set to Value *inside* result.
        if (result.SkipOriginal) __result = result.Value!;

        // true = run the original method.
        // false = skip the original method.
        return !result.SkipOriginal;
    }

    // finds the handler registered for the original method.
    // out gives us the handler as an extra result from the function.
    private static bool TryGetHandler(MethodBase original, out Func<object?[], HookResult>? handler)
    {
        // make the same key that was used when the handler was registered.
        var key = Key(original.DeclaringType?.FullName ?? "", original.Name);

        // try to find the handler using the key, bool return based on if found.
        return Overrides.TryGetValue(key, out handler);
    }

    // calls a registered handler and safely handles errors.
    // if the handler throws, continue with the original method instead.
    private static HookResult InvokeSafely(Func<object?[], HookResult> handler, object[] args, MethodBase original)
    {
        try
        {   
            // call the handler with the original method's args
            return handler(args);
        }
        catch (Exception e)
        {
            // handler failed, log the error and don't replace the original, continue.
            Log($"handler for {original.DeclaringType?.FullName}.{original.Name} threw: {e}");
            return HookResult.Continue();
        }
    }

    // makes the unique key used to store and find a handler.
    private static string Key(string typeName, string methodName) => $"{typeName}::{methodName}";
}

public readonly struct HookResult
{
    // tells the prefix whether the original method should be skipped.
    public bool SkipOriginal { get; private init; }

    // value to use if the original method is being replaced.
    public object? Value { get; private init; }

    // creates a result that lets the original method run normally.
    public static HookResult Continue() => new() { SkipOriginal = false };

    // creates a result that skips the original method and uses this value.
    public static HookResult Replace(object? value) => new() { SkipOriginal = true, Value = value };
}

// MISAML's own bootstrap stuff, not mod-facing, patches all of Mnemonimov's
// known [GlobalClass] bridge types so Loader can fetch a live GodotObject
// and reach the isolated AssemlyLoadContext. How to find this list:
//   ilspycmd Mnemonimov.dll -o decompiled_mnemonimov -p
//   grep -rl "GlobalClass" decompiled_mnemonimov/
public static class GodotBridgePatch
{
    private static void Log(string message) => StartupHook.Log(nameof(GodotBridgePatch), message);

    private static readonly HashSet<string> SkipNames =
    [
        "InvokeGodotClassMethod",
        "HasGodotClassMethod",
        "SetGodotClassPropertyValue",
        "GetGodotClassPropertyValue",
        "SaveGodotObjectData",
        "RestoreGodotObjectData",
        "GetGodotMethodList",
        "GetGodotPropertyList",
        "InvokeGodotClassStaticMethod",
    ];

    private static readonly string[] BridgeTypeNames =
    [
        "Mnemonimov.src.asm.VirtualMachineRunner",
        "Mnemonimov.src.asm.ShellLexer",
        "Mnemonimov.src.asm.AssemblerRunner",
        "Mnemonimov.src.asm.TextSearcher",
        "Mnemonimov.src.asm.CsUtils",
    ];

    // raised with the live instance on every patched bridge call,
    // how Loader gets a real GodotObject to bootstrap the isolated
    // context.
    public static event Action<object>? OnBridgeInstanceSeen;

    public static void TryPatch(Harmony harmony, Assembly mnemonimovAssembly)
    {
        var prefix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(nameof(BridgePrefix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("BridgePrefix not found!"));

        var postfix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(nameof(BridgePostfix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("BridgePostfix not found!"));

        int totalPatched = 0;
        int totalFailed = 0;

        foreach (var typeName in BridgeTypeNames)
        {
            Type bridgeType;
            try
            {
                bridgeType = mnemonimovAssembly.GetType(typeName)
                    ?? throw new Exception($"{typeName} not found");
            }
            catch (Exception e)
            {
                Log($"Failed to locate {typeName}: {e.Message}");
                continue;
            }

            var methods = bridgeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (SkipNames.Contains(method.Name)) continue;
                if (harmony.GetPatchedMethods().Contains(method)) continue;

                var usePostfix = method.ReturnType != typeof(void) ? postfix : null;

                try
                {
                    harmony.Patch(method, prefix: prefix, postfix: usePostfix);
                    totalPatched++;
                }
                catch (Exception e)
                {
                    Log($"Failed to patch {typeName}.{method.Name}: {e.GetType().Name}: {e.Message}");
                    totalFailed++;
                }
            }
        }

        Log($"complete: {totalPatched} patched across {BridgeTypeNames.Length} types, {totalFailed} failed.");
    }
    private static void BridgePrefix(MethodBase __originalMethod, object[] __args, object __instance)
    {
        if (StartupHook.DebugEnabled)
        {
            try
            {
                string args = __args is { Length: > 0 }
                    ? string.Join(", ", __args.Select(a => a?.ToString() ?? "null"))
                    : "";
                Log($"-> {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({args})");
            }
            catch (Exception e)
            {
                Log("prefix logging failed: " + e.GetType().Name);
            }
        }

        try
        {
            if (__instance != null)
                OnBridgeInstanceSeen?.Invoke(__instance);
        }
        catch (Exception e)
        {
            Log("OnBridgeInstanceSeen subscriber threw: " + e);
        }
    }

    private static void BridgePostfix(MethodBase __originalMethod, object __result)
    {
        if (!StartupHook.DebugEnabled) return;

        try
        {
            string result = __result?.ToString() ?? "void";
            Log($"<- {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} returned {result}");
        }
        catch (Exception e)
        {
            Log("postfix logging failed: " + e.GetType().Name);
        }

    }
}
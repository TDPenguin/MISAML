using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MISAML.Core.Data;

namespace MISAML.Core;

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
            object?, /* instance */
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
    public static void Register(string typeName, string methodName, Func<object?, object?[], HookResult> handler)
    {
        var key = Key(typeName, methodName);

        lock (PatchLock)
        {
            if (!Overrides.TryAdd(key, handler))
            {
                Log($"{key} already has a registered override, refusing duplicate.");
                return;
            }
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
    // one of 4 methods are used for instance patching support.
    private static void ApplyPatch(MethodInfo method, string key)
    {
        try
        {
            // check for void return & static
            bool isVoid = method.ReturnType == typeof(void);
            bool isStatic = method.IsStatic;

            // pick the matching prefix based on return type & whether it's static
            var prefixName = (isVoid, isStatic) switch
            {
                (true, true) => nameof(GenericPrefixVoidStatic),
                (true, false) => nameof(GenericPrefixVoidInstance),
                (false, true) => nameof(GenericPrefixResultStatic),
                (false, false) => nameof(GenericPrefixResultInstance),
            };

            // get prefix method
            var prefix = new HarmonyMethod(
                typeof(Hooks).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static)
            );

            // patch the target
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

    // prefix for void instance methods.
    private static bool GenericPrefixVoidInstance(MethodBase originalMethod, object instance, object[] args)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(originalMethod, out var handler)) return true;

        // call the registered handler with the instance & arguments.
        var result = InvokeSafely(handler!, instance, args, originalMethod);

        // if SkipOriginal is true, prevent the original method from running.
        return !result.SkipOriginal;
    }

    // prefix for void static methods.
    private static bool GenericPrefixVoidStatic(MethodBase originalMethod, object[] args)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(originalMethod, out var handler)) return true;

        // static methods have no instance, so pass null.
        var result = InvokeSafely(handler!, null, args, originalMethod);

        // if SkipOriginal is true, prevent the original method from running.
        return !result.SkipOriginal;
    }

    // prefix for instance methods that return a value.
    private static bool GenericPrefixResultInstance(MethodBase originalMethod, object instance, object[] args, ref object result)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(originalMethod, out var handler)) return true;

        // call the registered handler with the instance & arguments.
        var hookResult = InvokeSafely(handler!, instance, args, originalMethod);

        // if SkipOriginal is true, use Value as the method's return value.
        if (hookResult.SkipOriginal) result = hookResult.Value!;

        // true = run the original method.
        // false = skip the original method.
        return !hookResult.SkipOriginal;
    }

    // prefix for static methods that return a value.
    private static bool GenericPrefixResultStatic(MethodBase originalMethod, object[] args, ref object result)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(originalMethod, out var handler)) return true;

        // static methods have no instance, so pass null.
        var hookResult = InvokeSafely(handler!, null, args, originalMethod);

        // if SkipOriginal is true, use Value as the method's return value.
        if (hookResult.SkipOriginal) result = hookResult.Value!;

        // true = run the original method.
        // false = skip the original method.
        return !hookResult.SkipOriginal;
    }

    // finds the handler registered for the original method.
    // out gives us the handler as an extra result from the function.
    private static bool TryGetHandler(MethodBase original, out Func<object?, object?[], HookResult>? handler)
    {
        // make the same key that was used when the handler was registered.
        var key = Key(original.DeclaringType?.FullName ?? "", original.Name);

        // try to find the handler using the key, bool return based on if found.
        return Overrides.TryGetValue(key, out handler);
    }

    // calls a registered handler and catches any errors.
    // if the handler throws, continue with the original method instead.
    private static HookResult InvokeSafely(
        Func<object?, object?[], HookResult> handler,
        object? instance, 
        object[] args, 
        MethodBase original
    )
    {
        try
        {   
            // call the handler with the original method's args and instance
            return handler(instance, args);
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
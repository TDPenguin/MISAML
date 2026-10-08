using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MISAML.structs;

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
    private static readonly System.Diagnostics.Stopwatch Stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // just check env for debug logging
    internal static readonly bool DebugEnabled 
        = Environment.GetEnvironmentVariable("MISAML_DEBUG") != null;

    internal static void Log(string @class, string message)
    {
        var line = $"{Stopwatch.Elapsed.TotalSeconds:F6} [MISAML.Core.{@class}] {message}";
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
            {
                GodotBridgePatch.TryPatch(Hooks.Harmony, assembly);
            }

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
    private static void RunSearch(string label, SearchResult result)
    {
        Log(
            label,
            $"complete: {result.TypesScanned} types, {result.Patchable} patchable, " +
            $"{result.Skipped} skipped, {result.Failed} failed. " +
            "Full list at misaml-debug.log"
        );
    }
}
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MISAML.Core;

public static class StartupHook
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!,
        "misaml.log"
    );

    // syscall id -> handler, filled in by mods once mod loading exists,
    // empty for now, so every syscall just falls through to the original
    private static readonly ConcurrentDictionary<int, Action> SyscallHandlers = new();

    internal static void Log(string message)
    {
        File.AppendAllText(LogPath, message + "\n");
    }

    public static void Initialize()
    {
        File.WriteAllText(LogPath, ""); // comment out if you want persistent log
        Log("MISAML.Core starting...");

        var harmony = new Harmony("misaml.core");

        try
        {
            harmony.PatchAll(typeof(StartupHook).Assembly);
        }
        catch (Exception ex)
        {
            Log("PatchAll failed: " + ex);
        }

        AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
        {
            if (args.LoadedAssembly.GetName().Name != "Asm") return;

            try
            {
                var vmType = args.LoadedAssembly.GetType("Asm.Running.VirtualMachine")
                    ?? throw new Exception("VirtualMachine type not found");

                var target = vmType.GetMethod("HandleSystemCall",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("HandleSystemCall method not found");

                if (harmony.GetPatchedMethods().Contains(target))
                {
                    Log("VirtualMachine.HandleSystemCall already patched, skipping.");
                    return;
                }

                var prefix = typeof(StartupHook).GetMethod(
                    nameof(SyscallPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new Exception("SyscallPrefix method not found");

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log("Patched VirtualMachine.HandleSystemCall successfully!");
            }
            catch (Exception ex)
            {
                Log("Failed to patch VirtualMachine: " + ex);
            }
        };

        Log("MISAML.Core startup complete.");
    }

    // Harmony prefix for VirtualMachine.HandleSystemCall(ScCode)
    // __0 since we have no compile time ref to ScCode
    private static bool SyscallPrefix(object __0)
    {
        int id = (int)__0;

        if (SyscallHandlers.TryGetValue(id, out var handler))
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log($"Mod syscall handler for id {id} threw: {ex}");
            }
            return false; // handled (or attempted) by a mod, skip the stock switch
        }

        return true; // not ours, let the original run
    }

    // Mods will call this once mod loading exists. Public so it's callable from outside this assembly later
    public static void RegisterSyscall(int id, Action handler)
    {
        if (!SyscallHandlers.TryAdd(id, handler))
        {
            Log($"Syscall id {id} already registered, refusing duplicate.");
        }
    }
}
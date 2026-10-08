using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MISAML.structs;

namespace MISAML.Debug;

// Every type in GodotSharp.dll, public and private, it's the whole engine API,
// not entirely needed and very large. Docs on this will be limited.
public static class GodotSharp
{
    /*
     Found via: monodis --output=X.il <dll>, then
       rg -o '\[GodotSharp\][\w.]+::\w+' mnemonimov.il asm.il --no-filename | sort -u
     against the live Mnemonimov.dll/Asm.dll. Redo this after a game
     update, same idea as Bridge.TypeNames. 6 of the 26 raw matches
     are already in Search's SkipNames (Godot's own interop overrides,
     always InvalidProgramException) and are dropped from this list, however
     they can still be included.
     
     enforced readonly by making it ImmutableArray
    */
    private static readonly ImmutableArray<(string TypeName, string MethodName)> UsedMethods =
    [
        ("Godot.Bridge.GodotSerializationInfo", "AddProperty"),
        ("Godot.Bridge.GodotSerializationInfo", "TryGetProperty"),
        ("Godot.Collections.Dictionary", "Add"),
        ("Godot.GodotObject", "Dispose"),
        ("Godot.Image", "CreateFromData"),
        ("Godot.Image", "SetData"),
        ("Godot.ImageTexture", "CreateFromImage"),
        ("Godot.ImageTexture", "Update"),
        ("Godot.NativeInterop.NativeVariantPtrArgs", "get_Count"),
        ("Godot.NativeInterop.NativeVariantPtrArgs", "get_Item"),
        ("Godot.NativeInterop.VariantUtils", "ConvertTo"),
        ("Godot.NativeInterop.VariantUtils", "ConvertToDictionary"),
        ("Godot.NativeInterop.VariantUtils", "CreateFrom"),
        ("Godot.NativeInterop.VariantUtils", "CreateFromArray"),
        ("Godot.NativeInterop.VariantUtils", "CreateFromDictionary"),
        ("Godot.StringName", "op_Equality"),
        ("Godot.StringName", "op_Implicit"),
        ("Godot.Variant", "As"),
        ("Godot.Variant", "From"),
        ("Godot.Variant", "op_Implicit"),
    ];

    public static SearchResult Run(Harmony harmony, Assembly godotSharpAssembly)
        => Search.RunMethods(harmony, ResolveMethods(godotSharpAssembly), "GodotSharp");

    // multiple overloads can share a name, funcs with same name diff params...
    // (e.g. VarianUtils.CreateFrom<T>), the IL match only gave the name,
    // so resolve every overload instead of guessing which one
    private static IEnumerable<MethodInfo> ResolveMethods(Assembly godotSharpAssembly)
    {
        // go through every type + method pair we want to find!!!
        foreach (var (typeName, methodName) in UsedMethods)
        {
            var type = godotSharpAssembly.GetType(typeName);
            if (type == null)
            {
                Log.Write($"SKIP|GodotSharp|{typeName}.{methodName}|type not found, game update likely renamed/removed it");
                continue;
            }

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            // go through every method and only keep methods with the name we want!!!
            foreach (var method in methods.Where(m => m.Name == methodName))
                /* gives this method back to whoever called ResolveMethods() 
                yield pauses here and continues from here when the next
                method is requested. it allows the function to produce 
                multiple results, per each method. as we can find multiple 
                matching methods per type. */
                yield return method;
        }
    }
}
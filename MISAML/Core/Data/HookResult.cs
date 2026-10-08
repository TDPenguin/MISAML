namespace MISAML.Core.Data;

public readonly struct HookResult
{
    /// <summary>
    /// tells the prefix wether the original method should be skipped.
    /// </summary>
    public bool SkipOriginal { get; private init; }

    /// <summary>
    /// value to use if the original method is being replaced.
    /// </summary>
    public object? Value { get; private init; }
    /// <summary>
    /// Creates a result that lets the original method run normally.
    /// </summary>
    public static HookResult Continue() => new() { SkipOriginal = false };

    /// <summary>
    /// creates a result that skips the original method and uses this value.
    /// </summary>
    public static HookResult Replace(object? value) => new() { SkipOriginal = true, Value = value };
}
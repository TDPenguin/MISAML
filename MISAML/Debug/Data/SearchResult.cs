namespace MISAML.structs;

// struct for results of search, readonly means immutable, enforced by init;. record means data focused. Made immutable by using {get;init;}
public readonly record struct SearchResult
{
    public int TypesScanned { get; init; }
    public int Patchable { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
}
using System.Collections.Generic;

namespace DepotDumper.Models;

/// <summary>What LuaParser.Parse pulls out of a pooled lua file: depot keys, tokens, manifest pins, DLC names.</summary>
public sealed class ParsedLuaResult
{
    public Dictionary<string, string> KeyedDepots { get; set; } = new();
    public Dictionary<string, string> BareDepots { get; set; } = new();
    public List<string> BareAppIds { get; set; } = new();
    public Dictionary<string, string> Tokens { get; set; } = new();
    public Dictionary<string, string> Pins { get; set; } = new();
    public Dictionary<string, string> DlcNames { get; set; } = new();
    public string? HeaderName { get; set; }
}

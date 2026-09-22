using System.Collections.Generic;
using System.Text.RegularExpressions;
using DepotDumper.Models;

namespace DepotDumper.Parsers;

public static class LuaRegexPatterns
{
    // addappid(depot_id, type, "<64-hex-key>")
    public static readonly Regex AddAppIdKeyed = new(
        @"addappid\(\s*(\d+)\s*,\s*\d+\s*,\s*""([0-9a-fA-F]{64})""\s*\)",
        RegexOptions.Compiled);

    // addappid(depot_id, type) -- no key
    public static readonly Regex AddAppIdDepot = new(
        @"addappid\(\s*(\d+)\s*,\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    // addappid(id) -- just marks id as owned (DLC, etc.)
    public static readonly Regex AddAppIdBare = new(
        @"addappid\(\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    // addtoken(appid, "<decimal PICS token>")
    public static readonly Regex AddToken = new(
        @"addtoken\(\s*(\d+)\s*,\s*""(\d+)""\s*\)",
        RegexOptions.Compiled);

    // setManifestid(depot_id, "<manifest_id>"[, "<legacy request code>"])
    public static readonly Regex SetManifestId = new(
        @"setManifestid\(\s*(\d+)\s*,\s*""(\d+)""\s*(?:,\s*""?\d+""?\s*)?\)",
        RegexOptions.Compiled);

    // Header: "-- <appid>'s Lua and Manifest Created by Hubcap Manifest\n-- <game name>"
    public static readonly Regex HeaderName = new(
        @"--\s*\d+'s Lua and Manifest Created by Hubcap Manifest\r?\n--\s*(.+?)\s*\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // addappid(id) -- Name, OR addappid(id, type[, "key"]) -- Name. Matches ANY
    // addappid call's trailing comment, keyed by its first (id) argument.
    public static readonly Regex DlcName = new(
        @"addappid\(\s*(\d+)(?:\s*,[^)]*)?\)\s*--\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
}

public static class LuaParser
{
    public static ParsedLuaResult Parse(string luaContent)
    {
        var result = new ParsedLuaResult();

        // Keyed depots: addappid(depot_id, type, "key")
        foreach (Match m in LuaRegexPatterns.AddAppIdKeyed.Matches(luaContent))
        {
            result.KeyedDepots[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // Bare depots: addappid(depot_id, type)
        foreach (Match m in LuaRegexPatterns.AddAppIdDepot.Matches(luaContent))
        {
            result.BareDepots[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // Bare appids: addappid(id)
        foreach (Match m in LuaRegexPatterns.AddAppIdBare.Matches(luaContent))
        {
            result.BareAppIds.Add(m.Groups[1].Value);
        }

        // Tokens: addtoken(appid, "token")
        foreach (Match m in LuaRegexPatterns.AddToken.Matches(luaContent))
        {
            result.Tokens[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // Pins: setManifestid(depot_id, "manifest_id")
        foreach (Match m in LuaRegexPatterns.SetManifestId.Matches(luaContent))
        {
            result.Pins[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // DLC names
        foreach (Match m in LuaRegexPatterns.DlcName.Matches(luaContent))
        {
            result.DlcNames[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // Header name
        var headerMatch = LuaRegexPatterns.HeaderName.Match(luaContent);
        if (headerMatch.Success)
        {
            result.HeaderName = headerMatch.Groups[1].Value.Trim();
        }

        return result;
    }

    public static Dictionary<string, string> ExtractKeyedDepots(string luaContent)
    {
        var dict = new Dictionary<string, string>();
        foreach (Match m in LuaRegexPatterns.AddAppIdKeyed.Matches(luaContent))
        {
            dict[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return dict;
    }
}
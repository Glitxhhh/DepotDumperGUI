using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using DepotDumper.Models;
using DepotDumper.Parsers;

namespace DepotDumper
{
    public sealed record IndexStats(long Games, long Dlc, long Depots, long KeyedDepots, long Manifests, long ManifestsDownloaded, long Luas, long Tokens, string BuiltAt);

    public sealed record IndexQueryResult(List<string> Columns, List<object[]> Rows, bool Truncated);

    public sealed record IndexPreset(string Name, string Description, string Sql);

    /// <summary>
    /// A SQLite index of everything in your own dumps folder, so it can be questioned with SQL: games and their DLC, depots and keys,
    /// every manifest with its date, the pooled luas, and the tokens. It is derived data, rebuilt from the files on demand
    /// (dumps\.DepotDumper\game_index.db); nothing here changes your dumps, and it never talks to Steam.
    /// </summary>
    public static class GameIndex
    {
        public static string PathFor(string dumpDir) => Path.Combine(dumpDir, DepotDumper.CONFIG_DIR, "game_index.db");

        private const string Schema = @"
            CREATE TABLE games (appid INTEGER PRIMARY KEY, name TEXT, is_dlc INTEGER NOT NULL DEFAULT 0, parent_appid INTEGER);
            CREATE TABLE depots (depot_id INTEGER PRIMARY KEY, appid INTEGER, key TEXT);
            CREATE INDEX idx_depots_app ON depots(appid);
            CREATE TABLE manifests (
                depot_id INTEGER NOT NULL, manifest_id TEXT NOT NULL, appid INTEGER, dlc_appid INTEGER, owner_appid INTEGER, created_utc TEXT,
                downloaded INTEGER NOT NULL, unavailable INTEGER NOT NULL, branches TEXT, first_seen_utc TEXT, size INTEGER,
                PRIMARY KEY (depot_id, manifest_id));
            CREATE INDEX idx_manifests_app ON manifests(appid);
            CREATE INDEX idx_manifests_owner ON manifests(owner_appid);
            CREATE TABLE luas (file TEXT PRIMARY KEY, appid INTEGER, variant TEXT, keys INTEGER NOT NULL, depots INTEGER NOT NULL, pins INTEGER NOT NULL, newest_utc TEXT);
            CREATE INDEX idx_luas_app ON luas(appid);
            CREATE TABLE tokens (appid INTEGER PRIMARY KEY, token TEXT NOT NULL);
            CREATE TABLE package_tokens (packageid INTEGER PRIMARY KEY, token TEXT NOT NULL);
            CREATE TABLE dlc (dlc_appid INTEGER PRIMARY KEY, parent_appid INTEGER NOT NULL);
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);

            CREATE VIEW game_summary AS
            SELECT g.appid, g.name, g.is_dlc,
                (SELECT COUNT(*) FROM depots d WHERE d.appid = g.appid) AS depots,
                (SELECT COUNT(*) FROM depots d WHERE d.appid = g.appid AND d.key IS NOT NULL) AS keyed_depots,
                (SELECT COUNT(*) FROM manifests m WHERE m.owner_appid = g.appid) AS manifests,
                (SELECT COUNT(*) FROM manifests m WHERE m.owner_appid = g.appid AND m.downloaded = 1) AS downloaded,
                (SELECT MAX(m.created_utc) FROM manifests m WHERE m.owner_appid = g.appid) AS newest_manifest,
                (SELECT COUNT(*) FROM dlc c WHERE c.parent_appid = g.appid) AS dlc_count,
                (SELECT COUNT(*) FROM luas l WHERE l.appid = g.appid) AS luas
            FROM games g;

            CREATE VIEW dlc_status AS
            SELECT c.dlc_appid, dg.name AS dlc_name, c.parent_appid, pg.name AS parent_name,
                (SELECT COUNT(*) FROM depots d WHERE d.appid = c.dlc_appid) AS depots,
                (SELECT COUNT(*) FROM manifests m WHERE m.owner_appid = c.dlc_appid) AS manifests
            FROM dlc c
            LEFT JOIN games dg ON dg.appid = c.dlc_appid
            LEFT JOIN games pg ON pg.appid = c.parent_appid;";

        public static readonly IndexPreset[] Presets =
        {
            new("Games, newest first", "Every game with its depots, manifests, DLC and newest manifest date.",
                "SELECT appid, name, depots, keyed_depots, manifests, downloaded, dlc_count, luas, newest_manifest FROM game_summary WHERE is_dlc = 0 ORDER BY newest_manifest DESC"),
            new("Games with the most versions", "Games ranked by how many manifests you hold.",
                "SELECT appid, name, manifests, downloaded, newest_manifest FROM game_summary WHERE is_dlc = 0 ORDER BY manifests DESC LIMIT 200"),
            new("Depots without a key", "Depots that appear in your dumps but have no key saved, with how many manifests they have.",
                "SELECT d.depot_id, d.appid, g.name, COUNT(m.manifest_id) AS manifests FROM depots d LEFT JOIN games g ON g.appid = d.appid LEFT JOIN manifests m ON m.depot_id = d.depot_id WHERE d.key IS NULL GROUP BY d.depot_id ORDER BY manifests DESC"),
            new("Keyed games with no manifest file", "Games that have depot keys but no downloaded manifest.",
                "SELECT appid, name, depots, keyed_depots, manifests FROM game_summary WHERE is_dlc = 0 AND keyed_depots > 0 AND downloaded = 0 ORDER BY appid"),
            new("DLC and their depots", "Each DLC with the depots and manifests that belong to it (0 depots = baked into the base game).",
                "SELECT dlc_appid, dlc_name, parent_appid, parent_name, depots, manifests FROM dlc_status ORDER BY parent_appid, dlc_appid"),
            new("Manifests Steam refused", "Manifests that were seen but could not be downloaded.",
                "SELECT m.depot_id, m.manifest_id, g.name, m.branches, m.first_seen_utc FROM manifests m LEFT JOIN games g ON g.appid = m.owner_appid WHERE m.downloaded = 0 AND m.unavailable = 1 ORDER BY m.first_seen_utc DESC"),
            new("Luas with no keys", "Pooled luas that contain no depot keys.",
                "SELECT l.file, l.appid, g.name, l.depots, l.pins FROM luas l LEFT JOIN games g ON g.appid = l.appid WHERE l.keys = 0 ORDER BY l.appid"),
        };

        // ---- build ---------------------------------------------------------------------------------------------------------

        /// <summary>Rebuilds the index from the dumps folder. Returns what it now holds.</summary>
        public static IndexStats Rebuild(string dumpDir, Action<string> log = null)
        {
            log ??= _ => { };
            Directory.CreateDirectory(Path.Combine(dumpDir, DepotDumper.CONFIG_DIR));
            var path = PathFor(dumpDir);
            var temp = path + ".building";
            foreach (var f in new[] { temp, temp + "-wal", temp + "-shm" }) if (File.Exists(f)) File.Delete(f);

            ManifestLedger.UseDirectory(dumpDir);
            var ledger = ManifestLedger.Snapshot();
            // a depot that belongs to a DLC: the ledger knows it from the manifests recorded for it
            var depotOwnerDlc = new Dictionary<uint, uint>();
            foreach (var e in ledger.Where(e => e.ContentAppId != 0 && e.ContentAppId != e.AppId)) depotOwnerDlc[e.DepotId] = e.ContentAppId;

            using (var db = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                db.Open();
                Exec(db, Schema);
                using var tx = db.BeginTransaction();

                var names = new Dictionary<uint, string>();
                var isDlc = new Dictionary<uint, uint>();          // dlc -> parent
                var seenGames = new HashSet<uint>();
                var depots = new Dictionary<uint, (uint? App, string Key)>();

                void AddDepot(uint depot, uint? app, string key)
                {
                    if (depots.TryGetValue(depot, out var cur))
                        depots[depot] = (cur.App ?? app, cur.Key ?? key);
                    else depots[depot] = (app, key);
                }
                uint OwnerOf(uint depot, uint baseApp) => depotOwnerDlc.TryGetValue(depot, out var dlc) ? dlc : baseApp;

                // 1. the per-game folders: name, DLC list, depot keys
                foreach (var appDir in Directory.EnumerateDirectories(dumpDir))
                {
                    if (!uint.TryParse(Path.GetFileName(appDir), out var app)) continue;
                    seenGames.Add(app);
                    var info = Path.Combine(appDir, app + ".info");
                    if (File.Exists(info))
                    {
                        var parts = (File.ReadLines(info).FirstOrDefault() ?? "").Split(';');
                        if (parts.Length >= 2 && parts[1].Trim().Length > 0) names[app] = parts[1].Trim();
                    }
                    foreach (var f in Directory.EnumerateFiles(appDir, "*.dlcinfo"))
                    {
                        var p = (File.ReadLines(f).FirstOrDefault() ?? "").Split(';');
                        if (p.Length >= 2 && uint.TryParse(p[0], out var dlc) && dlc != app)
                        {
                            isDlc[dlc] = app;
                            if (p[1].Trim().Length > 0) names.TryAdd(dlc, p[1].Trim());
                        }
                    }
                    foreach (var f in Directory.EnumerateFiles(appDir, "*.key"))
                        foreach (var line in File.ReadLines(f))
                        {
                            var p = line.Trim().Split(';');
                            if (p.Length >= 2 && uint.TryParse(p[0], out var depot) && p[1].Length > 0) AddDepot(depot, OwnerOf(depot, app), p[1].ToLowerInvariant());
                        }
                }

                // 2. the ledger: every manifest, with its date
                foreach (var e in ledger)
                {
                    if (e.AppId != 0) seenGames.Add(e.AppId);
                    AddDepot(e.DepotId, e.AppId == 0 ? null : OwnerOf(e.DepotId, e.AppId), null);
                }

                // 3. the pooled luas: keys, depots, pins, tokens and names they mention
                var luaRows = new List<(string File, uint App, string Variant, int Keys, int DepotCount, int Pins, DateTime? Newest)>();
                var luaDir = Collector.LuaDir(dumpDir);
                var fileName = new Regex(@"^(\d+)(?:\.(.+))?$");
                if (Directory.Exists(luaDir))
                    foreach (var file in Directory.EnumerateFiles(luaDir, "*.lua"))
                    {
                        var m = fileName.Match(Path.GetFileNameWithoutExtension(file));
                        if (!m.Success || !uint.TryParse(m.Groups[1].Value, out var app)) continue;
                        ParsedLuaResult parsed;
                        try { parsed = LuaParser.Parse(File.ReadAllText(file)); }
                        catch (IOException) { continue; }
                        seenGames.Add(app);
                        if (parsed.HeaderName != null) names.TryAdd(app, parsed.HeaderName);
                        foreach (var (id, n) in parsed.DlcNames)
                            if (uint.TryParse(id, out var parsedId)) names.TryAdd(parsedId, n);
                        foreach (var dlcStr in parsed.BareAppIds.Where(id => uint.TryParse(id, out var parsedId) && parsedId != app && !parsed.KeyedDepots.ContainsKey(id) && !parsed.BareDepots.ContainsKey(id)))
                            isDlc.TryAdd(uint.Parse(dlcStr), app);
                        foreach (var (depotStr, key) in parsed.KeyedDepots)
                            if (uint.TryParse(depotStr, out var depot))
                                AddDepot(depot, OwnerOf(depot, app), key);
                        foreach (var depotStr in parsed.BareDepots.Keys)
                            if (uint.TryParse(depotStr, out var depot))
                                AddDepot(depot, OwnerOf(depot, app), null);
                        DateTime? newest = null;
                        foreach (var (depotStr, manifestStr) in parsed.Pins)
                            if (uint.TryParse(depotStr, out var depot) && ulong.TryParse(manifestStr, out var manifest))
                                if (ManifestLedger.TryGet(depot, manifest, out var known) && known.CreatedUtc is { } made && (newest == null || made > newest)) newest = made;
                        luaRows.Add((Path.GetFileName(file), app, m.Groups[2].Success ? m.Groups[2].Value : "", parsed.KeyedDepots.Count, parsed.KeyedDepots.Count + parsed.BareDepots.Count, parsed.Pins.Count, newest));
                    }

                // write everything
                foreach (var dlc in isDlc.Keys) seenGames.Add(dlc);
                using (var cmd = Prepare(db, tx, "INSERT INTO games (appid, name, is_dlc, parent_appid) VALUES ($a, $n, $d, $p)"))
                    foreach (var app in seenGames.OrderBy(x => x))
                        Run(cmd, ("$a", (long)app), ("$n", names.TryGetValue(app, out var n) ? n : null), ("$d", isDlc.ContainsKey(app) ? 1 : 0), ("$p", isDlc.TryGetValue(app, out var parent) ? (long)parent : null));
                using (var cmd = Prepare(db, tx, "INSERT INTO dlc (dlc_appid, parent_appid) VALUES ($d, $p)"))
                    foreach (var (dlc, parent) in isDlc) Run(cmd, ("$d", (long)dlc), ("$p", (long)parent));
                using (var cmd = Prepare(db, tx, "INSERT INTO depots (depot_id, appid, key) VALUES ($d, $a, $k)"))
                    foreach (var (depot, (app, key)) in depots.OrderBy(x => x.Key)) Run(cmd, ("$d", (long)depot), ("$a", app == null ? null : (long)app.Value), ("$k", key));
                using (var cmd = Prepare(db, tx, @"INSERT INTO manifests (depot_id, manifest_id, appid, dlc_appid, owner_appid, created_utc, downloaded, unavailable, branches, first_seen_utc, size)
                                                   VALUES ($d, $m, $a, $c, $o, $t, $dl, $u, $b, $f, $s)"))
                    foreach (var e in ledger)
                        Run(cmd, ("$d", (long)e.DepotId), ("$m", e.ManifestId.ToString()), ("$a", e.AppId == 0 ? null : (long)e.AppId),
                            ("$c", e.ContentAppId != 0 && e.ContentAppId != e.AppId ? (long)e.ContentAppId : null),
                            ("$o", e.ContentAppId != 0 && e.ContentAppId != e.AppId ? (long)e.ContentAppId : (e.AppId == 0 ? null : (long)e.AppId)), ("$t", e.CreatedUtc?.ToString("yyyy-MM-dd HH:mm:ss")),
                            ("$dl", e.Downloaded ? 1 : 0), ("$u", e.Unavailable ? 1 : 0), ("$b", string.Join(", ", e.Branches)),
                            ("$f", e.FirstSeenUtc.ToString("yyyy-MM-dd HH:mm:ss")), ("$s", e.Size));
                using (var cmd = Prepare(db, tx, "INSERT OR REPLACE INTO luas (file, appid, variant, keys, depots, pins, newest_utc) VALUES ($f, $a, $v, $k, $d, $p, $n)"))
                    foreach (var l in luaRows) Run(cmd, ("$f", l.File), ("$a", (long)l.App), ("$v", l.Variant), ("$k", l.Keys), ("$d", l.DepotCount), ("$p", l.Pins), ("$n", l.Newest?.ToString("yyyy-MM-dd HH:mm:ss")));

                // 4. tokens saved during dumps
                ReadPairs(Path.Combine(dumpDir, DepotDumper.CONFIG_DIR, AccessTokens.AppFile), db, tx, "INSERT OR REPLACE INTO tokens (appid, token) VALUES ($i, $t)");
                ReadPairs(Path.Combine(dumpDir, DepotDumper.CONFIG_DIR, AccessTokens.PackageFile), db, tx, "INSERT OR REPLACE INTO package_tokens (packageid, token) VALUES ($i, $t)");

                using (var cmd = Prepare(db, tx, "INSERT INTO meta (key, value) VALUES ($k, $v)"))
                {
                    Run(cmd, ("$k", "built_at"), ("$v", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                    Run(cmd, ("$k", "dump_dir"), ("$v", dumpDir));
                }
                tx.Commit();
            }

            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            var stats = Stats(dumpDir);
            log($"Index built: {stats.Games:N0} games ({stats.Dlc:N0} DLC), {stats.Depots:N0} depots ({stats.KeyedDepots:N0} with a key), {stats.Manifests:N0} manifests, {stats.Luas:N0} luas, {stats.Tokens:N0} tokens.");
            return stats;
        }

        private static void ReadPairs(string file, SqliteConnection db, SqliteTransaction tx, string sql)
        {
            if (!File.Exists(file)) return;
            using var cmd = Prepare(db, tx, sql);
            foreach (var line in File.ReadLines(file))
            {
                var p = line.Trim().Split(';');
                if (p.Length == 2 && long.TryParse(p[0], out var id) && p[1].Length > 0) Run(cmd, ("$i", id), ("$t", p[1]));
            }
        }

        // ---- reading ----------------------------------------------------------------------------------------------------------

        public static bool Exists(string dumpDir) => File.Exists(PathFor(dumpDir));

        public static IndexStats Stats(string dumpDir)
        {
            using var db = OpenReadOnly(dumpDir);
            long One(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar() ?? 0L); }
            string built = ""; using (var c = db.CreateCommand()) { c.CommandText = "SELECT value FROM meta WHERE key = 'built_at'"; built = c.ExecuteScalar() as string ?? ""; }
            return new IndexStats(One("SELECT COUNT(*) FROM games WHERE is_dlc = 0"), One("SELECT COUNT(*) FROM games WHERE is_dlc = 1"), One("SELECT COUNT(*) FROM depots"),
                One("SELECT COUNT(*) FROM depots WHERE key IS NOT NULL"), One("SELECT COUNT(*) FROM manifests"), One("SELECT COUNT(*) FROM manifests WHERE downloaded = 1"),
                One("SELECT COUNT(*) FROM luas"), One("SELECT COUNT(*) FROM tokens"), built);
        }

        /// <summary>Runs a query on a read-only connection: it can look at anything and change nothing.</summary>
        public static IndexQueryResult Query(string dumpDir, string sql, int maxRows = 5000)
        {
            using var db = OpenReadOnly(dumpDir);
            using (var pragma = db.CreateCommand()) { pragma.CommandText = "PRAGMA query_only = ON"; pragma.ExecuteNonQuery(); }
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object[]>();
            var truncated = false;
            while (reader.Read())
            {
                if (rows.Count >= maxRows) { truncated = true; break; }
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row);
            }
            return new IndexQueryResult(columns, rows, truncated);
        }

        private static SqliteConnection OpenReadOnly(string dumpDir)
        {
            var path = PathFor(dumpDir);
            if (!File.Exists(path)) throw new FileNotFoundException("The index hasn't been built yet.", path);
            var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            db.Open();
            return db;
        }

        // ---- small helpers ----------------------------------------------------------------------------------------------------

        private static void Exec(SqliteConnection db, string sql) { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

        private static SqliteCommand Prepare(SqliteConnection db, SqliteTransaction tx, string sql)
        {
            var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = sql; return c;
        }

        private static void Run(SqliteCommand cmd, params (string Name, object Value)[] values)
        {
            cmd.Parameters.Clear();
            foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }
}

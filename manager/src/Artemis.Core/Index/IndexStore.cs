using System.Buffers;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Artemis.Core.Index;

/// <summary>One file of a mod or of the game. The rest is set only for files whose names are indexed: their folder, and their names packed by <see cref="NamePack"/>.</summary>
public sealed record FileEntry(string Path, long Size, long MTime, string? Domain = null, byte[]? Names = null, int NameCount = 0);

public sealed record SourceStats(int Files, int Parsed, int Defs);

/// <summary>A name defined by a file of one source: a script definition, an event id or a localisation key.</summary>
public sealed record NameRow(string Path, string Domain, string Name, int Line);

/// <summary>
/// The names of one file as a compressed blob. A file's names are only ever read together with its source's other files
/// (to find who else defines them), so one row per file is far cheaper to write than one row per name.
/// </summary>
public static class NamePack
{
    public static byte[] Pack(IReadOnlyList<Def> defs)
    {
        using var raw = new MemoryStream();
        using (var deflate = new DeflateStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var text = new StreamWriter(deflate, new UTF8Encoding(false)) { NewLine = "\n" })
            foreach (var d in defs) text.WriteLine($"{d.Line}\t{d.Name.Replace('\t', ' ')}");
        return raw.ToArray();
    }

    public static List<Def> Unpack(byte[] blob)
    {
        var defs = new List<Def>();
        Visit(blob, (name, line) => defs.Add(new Def(name.ToString(), line)));
        return defs;
    }

    /// <summary>Hands each name to <paramref name="visit"/> as text that is only valid during the call, so a caller that wants few of them allocates for few.</summary>
    public static void Visit(byte[] blob, NameVisitor visit)
    {
        using var raw = new MemoryStream(blob.Length * 6);
        using (var deflate = new DeflateStream(new MemoryStream(blob), CompressionMode.Decompress)) deflate.CopyTo(raw);
        var text = raw.GetBuffer().AsSpan(0, (int)raw.Length);
        var chars = ArrayPool<char>.Shared.Rent(256);
        try
        {
            while (text.Length > 0)
            {
                var eol = text.IndexOf((byte)'\n');
                var line = eol < 0 ? text : text[..eol];
                text = eol < 0 ? default : text[(eol + 1)..];
                var tab = line.IndexOf((byte)'\t');
                if (tab <= 0 || !int.TryParse(line[..tab], out var number)) continue;
                var name = line[(tab + 1)..];
                if (chars.Length < name.Length)
                {
                    ArrayPool<char>.Shared.Return(chars);
                    chars = ArrayPool<char>.Shared.Rent(name.Length);
                }
                visit(chars.AsSpan(0, Encoding.UTF8.GetChars(name, chars)), number);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}

public delegate void NameVisitor(ReadOnlySpan<char> name, int line);

public delegate void FileNameVisitor(string path, string domain, ReadOnlySpan<char> name, int line);

/// <summary>
/// The disposable index cache: for every mod (and for the game's own files, per build) its file list and the names defined
/// in its script and localisation files. Nothing here is the only copy of anything, so a file that is corrupt or from an
/// older version is simply deleted and built again.
/// </summary>
public sealed class IndexStore(string path)
{
    /// <summary>Bump when the tables or the way files are read change: every older cache is then dropped.</summary>
    public const int Version = 3;

    public string Path => path;

    private const string Schema = """
        CREATE TABLE source (id INTEGER PRIMARY KEY, kind TEXT NOT NULL, key TEXT NOT NULL, fingerprint TEXT NOT NULL,
                             files INTEGER NOT NULL DEFAULT 0, parsed INTEGER NOT NULL DEFAULT 0, defs INTEGER NOT NULL DEFAULT 0,
                             UNIQUE (kind, key));
        CREATE TABLE file (id INTEGER PRIMARY KEY, source INTEGER NOT NULL, path TEXT NOT NULL COLLATE NOCASE,
                           size INTEGER NOT NULL, mtime INTEGER NOT NULL, domain TEXT, names BLOB);
        CREATE INDEX file_source ON file (source);
        CREATE INDEX file_path ON file (path);
        """;

    public SqliteConnection Open()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try { return OpenCore(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // corrupt, or not a database at all
        {
            Logging.AppLog.Warn($"The index cache was damaged ({ex.Message}) and is being built again.");
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
            return OpenCore();
        }
    }

    private SqliteConnection OpenCore()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            db.Open();
            Run(db, "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
            if ((long)Scalar(db, "PRAGMA user_version")! != Version)
            {
                Run(db, "DROP TABLE IF EXISTS file; DROP TABLE IF EXISTS source;");
                Run(db, Schema + $"; PRAGMA user_version = {Version};");
            }
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public string? Fingerprint(string kind, string key)
    {
        using var db = Open();
        return Scalar(db, "SELECT fingerprint FROM source WHERE kind = $k AND key = $key", ("$k", kind), ("$key", key)) as string;
    }

    public Dictionary<(string Kind, string Key), SourceStats> AllStats()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT kind, key, files, parsed, defs FROM source";
        using var rows = cmd.ExecuteReader();
        var result = new Dictionary<(string, string), SourceStats>();
        while (rows.Read()) result[(rows.GetString(0), rows.GetString(1))] = new SourceStats(rows.GetInt32(2), rows.GetInt32(3), rows.GetInt32(4));
        return result;
    }

    /// <summary>Stores the whole index of one source in one transaction, replacing what was there. The entries are read lazily, as they are written.</summary>
    public SourceStats Replace(string kind, string key, string fingerprint, IEnumerable<FileEntry> entries)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        DeleteSource(db, kind, key);
        Run(db, "INSERT INTO source (kind, key, fingerprint) VALUES ($k, $key, $f)", ("$k", kind), ("$key", key), ("$f", fingerprint));
        var source = (long)Scalar(db, "SELECT last_insert_rowid()")!;
        var fileId = (long)Scalar(db, "SELECT COALESCE(MAX(id), 0) FROM file")!;

        using var addFile = db.CreateCommand();
        addFile.CommandText = "INSERT INTO file (id, source, path, size, mtime, domain, names) VALUES ($id, $s, $p, $size, $mtime, $d, $n)";
        var pId = addFile.Parameters.Add("$id", SqliteType.Integer);
        var pPath = addFile.Parameters.Add("$p", SqliteType.Text);
        var pSize = addFile.Parameters.Add("$size", SqliteType.Integer);
        var pTime = addFile.Parameters.Add("$mtime", SqliteType.Integer);
        var pDomain = addFile.Parameters.Add("$d", SqliteType.Text);
        var pNames = addFile.Parameters.Add("$n", SqliteType.Blob);
        addFile.Parameters.AddWithValue("$s", source);

        int files = 0, parsed = 0, defs = 0;
        foreach (var e in entries)
        {
            pId.Value = ++fileId;
            pPath.Value = e.Path;
            pSize.Value = e.Size;
            pTime.Value = e.MTime;
            pDomain.Value = (object?)e.Domain ?? DBNull.Value;
            pNames.Value = (object?)e.Names ?? DBNull.Value;
            addFile.ExecuteNonQuery();
            files++;
            if (e.Names is null) continue;
            parsed++;
            defs += e.NameCount;
        }
        Run(db, "UPDATE source SET files = $a, parsed = $b, defs = $c WHERE id = $id", ("$a", files), ("$b", parsed), ("$c", defs), ("$id", source));
        tx.Commit();
        return new SourceStats(files, parsed, defs);
    }

    /// <summary>Every name the source's files define, with the file and folder it is in. Files in folders <paramref name="wantDomain"/> refuses are not even unpacked.</summary>
    public List<NameRow> Names(string kind, string key, Func<string, bool>? wantDomain = null)
    {
        var rows = new List<NameRow>();
        VisitNames(kind, key, wantDomain, (path, domain, name, line) => rows.Add(new NameRow(path, domain, name.ToString(), line)));
        return rows;
    }

    /// <summary>Like <see cref="Names"/> but without a string per name: the visitor gets each name as text that is only valid during the call.</summary>
    public void VisitNames(string kind, string key, Func<string, bool>? wantDomain, FileNameVisitor visit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT f.path, f.domain, f.names FROM file f JOIN source s ON s.id = f.source WHERE s.kind = $k AND s.key = $key AND f.names IS NOT NULL";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var domain = reader.GetString(1);
            if (wantDomain is not null && !wantDomain(domain)) continue;
            var path = reader.GetString(0);
            NamePack.Visit((byte[])reader["names"], (name, line) => visit(path, domain, name, line));
        }
    }

    /// <summary>The path of every file in the source, as it is spelled on disk.</summary>
    public List<string> Paths(string kind, string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT f.path FROM file f JOIN source s ON s.id = f.source WHERE s.kind = $k AND s.key = $key";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        var paths = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    /// <summary>Forgets every source of this kind whose key is not in <paramref name="keep"/>.</summary>
    public int Remove(string kind, IReadOnlyCollection<string> keep)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var stale = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT key FROM source WHERE kind = $k";
            cmd.Parameters.AddWithValue("$k", kind);
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) if (!keep.Contains(rows.GetString(0), StringComparer.OrdinalIgnoreCase)) stale.Add(rows.GetString(0));
        }
        foreach (var key in stale) DeleteSource(db, kind, key);
        tx.Commit();
        return stale.Count;
    }

    private static void DeleteSource(SqliteConnection db, string kind, string key)
    {
        if (Scalar(db, "SELECT id FROM source WHERE kind = $k AND key = $key", ("$k", kind), ("$key", key)) is not long id) return;
        Run(db, "DELETE FROM file WHERE source = $id", ("$id", id));
        Run(db, "DELETE FROM source WHERE id = $id", ("$id", id));
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }
}

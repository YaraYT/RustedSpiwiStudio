namespace RustedShpizhionStudio.Core;

public sealed class IndexDatabase
{
    public const int IndexFormatVersion = 6;
    public const int ParserVersion = 7;
    private readonly string _path;

    public IndexDatabase(string path)
    {
        _path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var c = Open();
        c.Open();
        Configure(c);
        EnsureSchema(c);
        EnsureCompatibleFormat(c);
    }

    public string Path => _path;
    public SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Private;Pooling=True")
        {
            DefaultTimeout = 30
        };
        return c;
    }

    private SqliteConnection OpenReadOnly()
    {
        var c = new SqliteConnection($"Data Source={_path};Mode=ReadOnly;Cache=Private;Pooling=True")
        {
            DefaultTimeout = 30
        };
        return c;
    }

    private static void Configure(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=30000;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureSchema(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS mods (id INTEGER PRIMARY KEY, name TEXT NOT NULL, root_path TEXT NOT NULL, relative_path TEXT NOT NULL, source_type TEXT NOT NULL DEFAULT 'folder', package_path TEXT, package_hash TEXT, last_scan_at TEXT, UNIQUE(root_path, relative_path));
CREATE TABLE IF NOT EXISTS files (id INTEGER PRIMARY KEY, mod_id INTEGER NOT NULL REFERENCES mods(id) ON DELETE CASCADE, relative_path TEXT NOT NULL, absolute_path TEXT NOT NULL, kind TEXT NOT NULL, size INTEGER NOT NULL, mtime_ms INTEGER NOT NULL, sha256 TEXT, UNIQUE(mod_id, relative_path));
CREATE TABLE IF NOT EXISTS sections (id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE, name TEXT NOT NULL, line_start INTEGER NOT NULL, order_index INTEGER NOT NULL, is_core INTEGER NOT NULL DEFAULT 0, entity_name TEXT, UNIQUE(file_id, name));
CREATE TABLE IF NOT EXISTS keys (id INTEGER PRIMARY KEY, section_id INTEGER NOT NULL REFERENCES sections(id) ON DELETE CASCADE, key_name TEXT NOT NULL, value TEXT NOT NULL, line_no INTEGER NOT NULL, is_directive INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS units (id INTEGER PRIMARY KEY, mod_id INTEGER NOT NULL REFERENCES mods(id) ON DELETE CASCADE, file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE, section_id INTEGER NOT NULL REFERENCES sections(id) ON DELETE CASCADE, technical_name TEXT NOT NULL, display_name TEXT NOT NULL, display_name_source TEXT NOT NULL DEFAULT 'technical', UNIQUE(file_id, section_id));
CREATE TABLE IF NOT EXISTS images (id INTEGER PRIMARY KEY, mod_id INTEGER NOT NULL REFERENCES mods(id) ON DELETE CASCADE, relative_path TEXT NOT NULL, filename TEXT NOT NULL, extension TEXT NOT NULL, size INTEGER NOT NULL DEFAULT 0, sha256 TEXT, width INTEGER, height INTEGER, mtime_ms INTEGER NOT NULL DEFAULT 0, usage_count INTEGER NOT NULL DEFAULT 0, missing_count INTEGER NOT NULL DEFAULT 0, is_used INTEGER NOT NULL DEFAULT 0, resource_type TEXT NOT NULL DEFAULT 'other', resource_subtype TEXT NOT NULL DEFAULT 'other', is_animation INTEGER NOT NULL DEFAULT 0, UNIQUE(mod_id, relative_path));
CREATE TABLE IF NOT EXISTS image_references (id INTEGER PRIMARY KEY, image_id INTEGER REFERENCES images(id) ON DELETE SET NULL, mod_id INTEGER NOT NULL REFERENCES mods(id) ON DELETE CASCADE, file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE, section_id INTEGER REFERENCES sections(id) ON DELETE SET NULL, unit_id INTEGER REFERENCES units(id) ON DELETE SET NULL, key_name TEXT NOT NULL, raw_value TEXT NOT NULL, resolved_path TEXT, relation_type TEXT NOT NULL, source_file_id INTEGER REFERENCES files(id) ON DELETE SET NULL, source_section_id INTEGER REFERENCES sections(id) ON DELETE SET NULL, inherited_from TEXT, status TEXT NOT NULL DEFAULT 'ok', is_animation INTEGER NOT NULL DEFAULT 0, UNIQUE(mod_id, file_id, section_id, unit_id, key_name, raw_value, relation_type, resolved_path));
CREATE TABLE IF NOT EXISTS inheritance_edges (id INTEGER PRIMARY KEY, mod_id INTEGER NOT NULL REFERENCES mods(id) ON DELETE CASCADE, child_section_id INTEGER NOT NULL REFERENCES sections(id) ON DELETE CASCADE, parent_section_id INTEGER REFERENCES sections(id) ON DELETE CASCADE, relation_type TEXT NOT NULL, relation_text TEXT NOT NULL, UNIQUE(child_section_id, parent_section_id, relation_type, relation_text));
CREATE TABLE IF NOT EXISTS scan_runs (id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, finished_at TEXT, mods_count INTEGER NOT NULL DEFAULT 0, files_count INTEGER NOT NULL DEFAULT 0, images_count INTEGER NOT NULL DEFAULT 0, references_count INTEGER NOT NULL DEFAULT 0, missing_count INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS maps (id INTEGER PRIMARY KEY, mod_id INTEGER NOT NULL REFERENCES mods(id) ON DELETE CASCADE, image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE, image_relative_path TEXT NOT NULL, tmx_relative_path TEXT NOT NULL, UNIQUE(mod_id, image_id));
CREATE TABLE IF NOT EXISTS translations (text_key TEXT NOT NULL, target_lang TEXT NOT NULL, translated_text TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(text_key, target_lang));
CREATE TABLE IF NOT EXISTS rwmod_decisions (archive_path TEXT PRIMARY KEY, archive_hash TEXT NOT NULL, decision TEXT NOT NULL CHECK(decision IN ('allow','deny')), decided_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_images_filename ON images(filename);
CREATE INDEX IF NOT EXISTS idx_images_path ON images(relative_path);
CREATE INDEX IF NOT EXISTS idx_refs_image ON image_references(image_id);
CREATE INDEX IF NOT EXISTS idx_refs_image_status ON image_references(image_id, status);
CREATE INDEX IF NOT EXISTS idx_refs_unit ON image_references(unit_id);
CREATE INDEX IF NOT EXISTS idx_refs_status ON image_references(status);
CREATE INDEX IF NOT EXISTS idx_units_name ON units(technical_name);
CREATE INDEX IF NOT EXISTS idx_units_display_name ON units(display_name);
CREATE INDEX IF NOT EXISTS idx_files_mod ON files(mod_id);
CREATE INDEX IF NOT EXISTS idx_sections_file ON sections(file_id);
CREATE INDEX IF NOT EXISTS idx_keys_section ON keys(section_id);
CREATE INDEX IF NOT EXISTS idx_refs_animation ON image_references(is_animation, image_id);
CREATE INDEX IF NOT EXISTS idx_images_animation ON images(is_animation, id);
CREATE INDEX IF NOT EXISTS idx_maps_mod ON maps(mod_id);
CREATE INDEX IF NOT EXISTS idx_maps_image ON maps(image_id);
";
        cmd.ExecuteNonQuery();
        try
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE images ADD COLUMN resource_subtype TEXT NOT NULL DEFAULT 'other';";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) { }
        using (var subtypeIndex = c.CreateCommand())
        {
            subtypeIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_images_type_subtype ON images(resource_type, resource_subtype, id);";
            subtypeIndex.ExecuteNonQuery();
        }
        using (var hotFilterIndex = c.CreateCommand())
        {
            hotFilterIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_images_hot_filters ON images(mod_id, resource_type, resource_subtype, is_used, filename COLLATE NOCASE, id);";
            hotFilterIndex.ExecuteNonQuery();
        }
        using var metaCmd = c.CreateCommand();
        metaCmd.CommandText = "SELECT COUNT(*) FROM meta WHERE key IN ('index_format_version','parser_version');";
        var metaCount = Convert.ToInt32(metaCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (metaCount == 0)
        {
            SetMeta(c, "index_format_version", IndexFormatVersion.ToString(CultureInfo.InvariantCulture));
            SetMeta(c, "parser_version", ParserVersion.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void EnsureCompatibleFormat(SqliteConnection c)
    {
        string format;
        string parser;
        using (var read = c.CreateCommand())
        {
            read.CommandText = "SELECT COALESCE((SELECT value FROM meta WHERE key='index_format_version'),''), COALESCE((SELECT value FROM meta WHERE key='parser_version'),'');";
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return;
            format = reader.GetString(0);
            parser = reader.GetString(1);
        }
        var currentFormat = IndexFormatVersion.ToString(CultureInfo.InvariantCulture);
        var currentParser = ParserVersion.ToString(CultureInfo.InvariantCulture);
        if ((string.IsNullOrEmpty(format) && string.IsNullOrEmpty(parser)) ||
            (format == currentFormat && parser == currentParser))
            return;

        AppLog.Warn($"Incompatible index detected at startup: format={format}, parser={parser}; expected format={currentFormat}, parser={currentParser}. Rebuilding index data.");

        using var tx = c.BeginTransaction();
        foreach (var table in new[] { "image_references", "inheritance_edges", "maps", "units", "keys", "sections", "images", "files", "mods", "scan_runs" })
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table};";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO meta(key,value) VALUES('index_format_version',$f) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            cmd.Parameters.AddWithValue("$f", currentFormat);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO meta(key,value) VALUES('parser_version',$p) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            cmd.Parameters.AddWithValue("$p", currentParser);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        AppLog.Info("Incompatible index data was cleared successfully; the next scan will rebuild it.");
    }

    private static void SetMeta(SqliteConnection c, string key, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery();
    }

    public void ResetForIncompatibleFormat()
    {
        using var c = Open(); c.Open(); Configure(c);
        var tables = new[] { "image_references", "inheritance_edges", "maps", "units", "keys", "sections", "images", "files", "mods", "scan_runs" };
        using var tx = c.BeginTransaction();
        foreach (var table in tables)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = $"DELETE FROM {table};"; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool HasIndexedData()
    {
        using var c = OpenReadOnly(); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM images LIMIT 1);";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public Stats GetStats()
    {
        using var c = OpenReadOnly(); c.Open();
        int Get(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); }
        return new(Get("SELECT COUNT(*) FROM mods"), Get("SELECT COUNT(*) FROM files"), Get("SELECT COUNT(*) FROM images"), Get("SELECT COUNT(*) FROM image_references WHERE status='ok'"), Get("SELECT COUNT(*) FROM images WHERE NOT EXISTS (SELECT 1 FROM image_references r WHERE r.image_id=images.id AND r.status='ok') AND images.resource_type <> 'map'"), Get("SELECT COUNT(*) FROM image_references WHERE status='missing'"), Get("SELECT COUNT(*) FROM maps"), HasIndexedDataInternal(c));
    }

    private static bool HasIndexedDataInternal(SqliteConnection c)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM images LIMIT 1);"; return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public List<ModInfo> GetMods()
    {
        using var c = OpenReadOnly(); c.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,root_path,relative_path,source_type FROM mods ORDER BY name COLLATE NOCASE;";
        using var r = cmd.ExecuteReader(); var result = new List<ModInfo>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return result;
    }

    public SearchResult SearchImages(string query, IReadOnlyList<string> resourceTypes, IReadOnlyList<string> resourceSubtypes,
        int limit, int offset, IReadOnlyList<long> modIds, bool hideBroken, ResourceUsageFilter usageFilter, bool brokenOnly = false)
    {
        limit = Math.Clamp(limit, 50, 1000); offset = Math.Max(offset, 0);
        using var c = OpenReadOnly(); c.Open();
        var where = new List<string>();
        var p = new List<SqliteParameter>();

        // Пустой список выбора означает «ничего не показывать», а не «убрать фильтр».
        // Иначе кнопка «Сброс» фактически делала вид, что работает, возвращая всю базу.
        if (!brokenOnly && (resourceTypes.Count == 0 || resourceSubtypes.Count == 0))
            return new SearchResult([], false);

        if (resourceTypes.Count == 0 || resourceSubtypes.Count == 0)
        {
            // Для режима битых ссылок всё равно сохраняем согласованность фильтров.
            return new SearchResult([], false);
        }

        {
            var names = new List<string>(resourceTypes.Count);
            for (var idx = 0; idx < resourceTypes.Count; idx++)
            {
                names.Add($"$rt{idx}");
                p.Add(new SqliteParameter($"$rt{idx}", resourceTypes[idx]));
            }
            where.Add($"i.resource_type IN ({string.Join(',', names)})");
        }
        {
            var names = new List<string>(resourceSubtypes.Count);
            for (var idx = 0; idx < resourceSubtypes.Count; idx++)
            {
                names.Add($"$rs{idx}");
                p.Add(new SqliteParameter($"$rs{idx}", resourceSubtypes[idx]));
            }
            where.Add($"i.resource_subtype IN ({string.Join(',', names)})");
        }

        if (brokenOnly)
            where.Add("EXISTS (SELECT 1 FROM image_references br WHERE br.image_id=i.id AND br.status='missing')");

        if (hideBroken && !brokenOnly) where.Add("NOT EXISTS (SELECT 1 FROM image_references br WHERE br.image_id=i.id AND br.status='missing')");
        if (modIds.Count > 0)
        {
            where.Add($"i.mod_id IN ({string.Join(',', modIds.Select((_, idx) => $"$m{idx}"))})");
            for (var idx = 0; idx < modIds.Count; idx++) p.Add(new SqliteParameter($"$m{idx}", modIds[idx]));
        }
        if (usageFilter == ResourceUsageFilter.Used) where.Add("i.is_used=1");
        else if (usageFilter == ResourceUsageFilter.Unused) where.Add("i.is_used=0");

        var tokens = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens.Select((x, i) => (x, i)))
        {
            var q = $"$q{token.i}";
            where.Add($"(i.filename LIKE {q} ESCAPE '\\' OR i.relative_path LIKE {q} ESCAPE '\\' OR m.name LIKE {q} ESCAPE '\\' OR EXISTS (SELECT 1 FROM image_references rr JOIN units uu ON uu.id=rr.unit_id WHERE rr.image_id=i.id AND (uu.technical_name LIKE {q} ESCAPE '\\' OR uu.display_name LIKE {q} ESCAPE '\\')))" );
            p.Add(new SqliteParameter(q, $"%{token.x.Replace("%", "\\%").Replace("_", "\\_")}%"));
        }

        var whereSql = where.Count == 0 ? "1=1" : string.Join(" AND ", where);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT i.id,i.mod_id,m.name,i.filename,i.relative_path,i.size,i.width,i.height,COALESCE(i.sha256,''),i.resource_type,i.resource_subtype,i.is_used,i.is_animation,m.root_path,i.usage_count FROM images i JOIN mods m ON m.id=i.mod_id WHERE {whereSql} ORDER BY i.filename COLLATE NOCASE,i.id LIMIT $limit OFFSET $offset;";
        foreach (var x in p) cmd.Parameters.Add(x);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var r = cmd.ExecuteReader();
        var rows = new List<ImageRow>();
        while (r.Read())
        {
            rows.Add(new ImageRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetString(8), r.GetString(9),
                r.GetInt64(11) != 0, r.GetInt64(12) != 0, r.GetString(13), r.GetInt32(14)) { ResourceSubtype = r.GetString(10) });
        }

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore);
    }

    public ImageDetails? GetImage(long id)
    {
        using var c = OpenReadOnly(); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT i.id,i.mod_id,i.relative_path,i.filename,i.extension,i.size,i.sha256,i.width,i.height,i.mtime_ms,i.usage_count,i.missing_count,i.is_used,i.resource_type,i.resource_subtype,i.is_animation,m.name,m.root_path FROM images i JOIN mods m ON m.id=i.mod_id WHERE i.id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var image = new ImageRow(r.GetInt64(0), r.GetInt64(1), r.GetString(16), r.GetString(3), r.GetString(2), r.GetInt64(5), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(6) ? "" : r.GetString(6), r.GetString(13), r.GetInt64(12) != 0, r.GetInt64(15) != 0, r.GetString(17), r.GetInt32(10)) { ResourceSubtype = r.GetString(14) };
        var refs = new List<ReferenceRow>();
        using var rc = c.CreateCommand(); rc.CommandText = "SELECT id,key_name,raw_value,resolved_path,relation_type,inherited_from,status,is_animation,unit_id,image_id FROM image_references WHERE image_id=$id ORDER BY id"; rc.Parameters.AddWithValue("$id", id);
        using var rr = rc.ExecuteReader(); while (rr.Read()) refs.Add(new(rr.GetInt64(0),rr.GetString(1),rr.GetString(2),rr.IsDBNull(3)?null:rr.GetString(3),rr.GetString(4),rr.IsDBNull(5)?null:rr.GetString(5),rr.GetString(6),rr.GetInt64(7)!=0,rr.IsDBNull(8)?null:rr.GetInt64(8),rr.IsDBNull(9)?null:rr.GetInt64(9)));
        var names = new List<string>(); using var uc = c.CreateCommand(); uc.CommandText = "SELECT DISTINCT u.display_name FROM units u JOIN image_references r ON r.unit_id=u.id WHERE r.image_id=$id ORDER BY u.display_name"; uc.Parameters.AddWithValue("$id", id); using var ur=uc.ExecuteReader(); while(ur.Read()) names.Add(ur.GetString(0));
        var missing = new List<MissingReference>();
        using var mc = c.CreateCommand();
        mc.CommandText = @"SELECT r.id,m.name,f.relative_path,COALESCE(s.name,''),r.key_name,r.raw_value,r.resolved_path,r.status FROM image_references r JOIN mods m ON m.id=r.mod_id JOIN files f ON f.id=r.file_id LEFT JOIN sections s ON s.id=r.section_id WHERE r.status='missing' AND r.mod_id=$mod AND lower(COALESCE(r.raw_value,''))=lower($path) ORDER BY r.id";
        mc.Parameters.AddWithValue("$mod", image.ModId); mc.Parameters.AddWithValue("$path", image.RelativePath);
        using var mr=mc.ExecuteReader(); while(mr.Read()) missing.Add(new(mr.GetInt64(0),mr.GetString(1),mr.GetString(2),mr.GetString(3),mr.GetString(4),mr.GetString(5),mr.IsDBNull(6)?null:mr.GetString(6),mr.GetString(7)));
        return new(image, refs, names, missing);
    }

    public UnitDetails? GetUnit(long id)
    {
        using var c=OpenReadOnly();c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT u.id,u.technical_name,u.display_name,u.display_name_source,m.name,m.root_path,f.relative_path FROM units u JOIN mods m ON m.id=u.mod_id JOIN files f ON f.id=u.file_id WHERE u.id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var resources=new List<UnitResource>();using var rc=c.CreateCommand();rc.CommandText="SELECT id,key_name,raw_value,resolved_path,image_id,status,is_animation,relation_type,inherited_from FROM image_references WHERE unit_id=$id ORDER BY id";rc.Parameters.AddWithValue("$id",id);using var rr=rc.ExecuteReader();while(rr.Read())resources.Add(new(rr.GetInt64(0),rr.GetString(1),rr.GetString(2),rr.IsDBNull(3)?null:rr.GetString(3),rr.IsDBNull(4)?null:rr.GetInt64(4),rr.GetString(5),rr.GetInt64(6)!=0,rr.GetString(7),rr.IsDBNull(8)?null:rr.GetString(8)));
        return new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),resources);
    }

    public MissingReference? GetMissingReference(long id)
    {
        using var c=OpenReadOnly();c.Open();using var cmd=c.CreateCommand();cmd.CommandText=@"SELECT r.id,m.name,f.relative_path,COALESCE(s.name,''),r.key_name,r.raw_value,r.resolved_path,r.status FROM image_references r JOIN mods m ON m.id=r.mod_id JOIN files f ON f.id=r.file_id LEFT JOIN sections s ON s.id=r.section_id WHERE r.id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();return !r.Read()?null:new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetString(7));
    }

    public List<MissingReference> GetMissingReferences(string query = "", int limit = 1000)
    {
        using var c=OpenReadOnly(); c.Open(); using var cmd=c.CreateCommand();
        cmd.CommandText=@"SELECT r.id,m.name,f.relative_path,COALESCE(s.name,''),r.key_name,r.raw_value,r.resolved_path,r.status FROM image_references r JOIN mods m ON m.id=r.mod_id JOIN files f ON f.id=r.file_id LEFT JOIN sections s ON s.id=r.section_id WHERE r.status='missing' AND ($q='' OR r.raw_value LIKE $like OR f.relative_path LIKE $like OR m.name LIKE $like) ORDER BY r.id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", query ?? string.Empty); cmd.Parameters.AddWithValue("$like", $"%{query}%"); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit,1,5000));
        using var r=cmd.ExecuteReader(); var list=new List<MissingReference>(); while(r.Read()) list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetString(7))); return list;
    }


    public List<MapInfo> GetMaps(long? modId = null)
    {
        using var c = OpenReadOnly(); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = modId.HasValue
            ? "SELECT id,mod_id,image_id,image_relative_path,tmx_relative_path FROM maps WHERE mod_id=$m ORDER BY image_relative_path COLLATE NOCASE;"
            : "SELECT id,mod_id,image_id,image_relative_path,tmx_relative_path FROM maps ORDER BY image_relative_path COLLATE NOCASE;";
        if (modId.HasValue) cmd.Parameters.AddWithValue("$m", modId.Value);
        using var r = cmd.ExecuteReader();
        var result = new List<MapInfo>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)));
        return result;
    }

    public string? GetTranslation(string text,string target)
    { using var c=Open();c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT translated_text FROM translations WHERE text_key=$t AND target_lang=$l";cmd.Parameters.AddWithValue("$t",text);cmd.Parameters.AddWithValue("$l",target);return cmd.ExecuteScalar() as string; }
    public void SaveTranslation(string text,string target,string translated){using var c=Open();c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO translations(text_key,target_lang,translated_text,created_at) VALUES($t,$l,$v,$d) ON CONFLICT(text_key,target_lang) DO UPDATE SET translated_text=excluded.translated_text,created_at=excluded.created_at";cmd.Parameters.AddWithValue("$t",text);cmd.Parameters.AddWithValue("$l",target);cmd.Parameters.AddWithValue("$v",translated);cmd.Parameters.AddWithValue("$d",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}

    public (string Decision,string Hash)? GetRwmodDecision(string path){using var c=Open();c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT decision,archive_hash FROM rwmod_decisions WHERE archive_path=$p";cmd.Parameters.AddWithValue("$p",path);using var r=cmd.ExecuteReader();return r.Read()?(r.GetString(0),r.GetString(1)):null;}
    public void SaveRwmodDecision(string path,string hash,string decision){using var c=Open();c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO rwmod_decisions(archive_path,archive_hash,decision,decided_at) VALUES($p,$h,$d,$at) ON CONFLICT(archive_path) DO UPDATE SET archive_hash=excluded.archive_hash,decision=excluded.decision,decided_at=excluded.decided_at";cmd.Parameters.AddWithValue("$p",path);cmd.Parameters.AddWithValue("$h",hash);cmd.Parameters.AddWithValue("$d",decision);cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}

    public static void DeletePortableDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Не удалось удалить файл индекса '{file}': {ex.Message}");
            }
        }
    }

}

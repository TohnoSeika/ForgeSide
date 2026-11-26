using Microsoft.Data.Sqlite;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace ForgeSide;

/// <summary>
/// 缩略图缓存——SQLite BLOB 单文件存储，大/中/小三档，返回 Base64 Data URI。
/// 桃华为 Minagi 重写的精致版本 ✿
/// </summary>
public class ThumbnailCache : IDisposable
{
    private readonly string _cacheDir;
    private readonly SqliteConnection _conn;

    /// <summary>
    /// _conn 只有一个，而它会被两拨人同时用：后台的 PreGenerateAll 跑在 Task.Run 里，
    /// 前台的 GetBase64 走同步桥跑在 UI 线程上。SqliteConnection 不是线程安全的，
    /// 撞上了会在它内部的语句缓存里抛 ArgumentOutOfRange / IndexOutOfRange，
    /// 一路逃到页面 JS —— 那个渲染函数当场中断，图库停在半截。
    /// 现象是「偶发、没提示、刷新一下又好了」，属于最难查的那类
    /// </summary>
    private readonly object _db = new();

    // 三档缩略图的目标高度（生成时的长边尺寸，保持宽高比）
    public static readonly IReadOnlyDictionary<string, int> Sizes = new Dictionary<string, int>
    {
        ["small"]  = 280,
        ["medium"] = 400,
        ["large"]  = 560,
    };

    public ThumbnailCache(string baseDir)
    {
        _cacheDir = Path.Combine(baseDir, ".thumbcache");
        Directory.CreateDirectory(_cacheDir);
        try { File.SetAttributes(_cacheDir, FileAttributes.Hidden | FileAttributes.Directory); } catch { }
        _conn = new SqliteConnection($"Data Source={Path.Combine(_cacheDir, "thumbs.db")}");
        _conn.Open();
        InitDb();
        CleanOrphanJpgFiles();
    }

    private void InitDb()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS thumb_data (
                source_path     TEXT NOT NULL,
                size_key        TEXT NOT NULL,
                source_modified TEXT NOT NULL,
                thumb_blob      BLOB NOT NULL,
                created_at      TEXT NOT NULL,
                PRIMARY KEY (source_path, size_key)
            );
            CREATE TABLE IF NOT EXISTS gallery_files (
                file_path      TEXT PRIMARY KEY,
                file_name      TEXT NOT NULL,
                folder         TEXT NOT NULL,
                modified       TEXT NOT NULL,
                width          INTEGER NOT NULL,
                height         INTEGER NOT NULL,
                last_scanned   TEXT NOT NULL
            );
            -- 清理旧的基于文件的 thumbs 表
            DROP TABLE IF EXISTS thumbs;
            PRAGMA journal_mode=WAL;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>清理旧版本遗留的 .jpg 缩略图文件</summary>
    private void CleanOrphanJpgFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_cacheDir, "*.jpg"))
                File.Delete(f);
        }
        catch { }
    }

    /// <summary>
    /// 获取缩略图 Base64 Data URI。sizeKey: "small" / "medium" / "large"。
    /// 缓存命中直接返回，否则生成后存入 BLOB。
    /// </summary>
    public string GetBase64(string sourcePath, string sizeKey)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists) return "";

        int size = Sizes.TryGetValue(sizeKey, out var s) ? s : Sizes["medium"];
        string sourceModified = sourceInfo.LastWriteTimeUtc.ToString("O");

        string? cached = ReadCachedBlob(sourcePath, sizeKey, sourceModified);
        if (cached != null) return cached;

        // 解码、缩放、编码全在锁外做。它慢得肉眼可见，
        // 占着锁的话后台一跑预生成，UI 线程取缩略图就得排队
        byte[] jpeg;
        try { jpeg = GenerateJpegBlob(sourcePath, size); }
        catch { return ""; }

        SaveBlob(sourcePath, sizeKey, sourceModified, jpeg);
        return ToDataUri(jpeg);
    }

    /// <summary>查缓存。没有、或者原图已经改过，都返回 null</summary>
    private string? ReadCachedBlob(string sourcePath, string sizeKey, string sourceModified)
    {
        lock (_db)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT source_modified, thumb_blob FROM thumb_data WHERE source_path = @p AND size_key = @k";
            cmd.Parameters.AddWithValue("@p", sourcePath);
            cmd.Parameters.AddWithValue("@k", sizeKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != sourceModified) return null;

            byte[] blob = GetBlobBytes(reader, 1);
            return blob.Length > 0 ? ToDataUri(blob) : null;
        }
    }

    /// <summary>写回缓存。写不进去也不影响这次返回——下次再算一遍而已</summary>
    private void SaveBlob(string sourcePath, string sizeKey, string sourceModified, byte[] jpeg)
    {
        try
        {
            lock (_db)
            {
                using var upsert = _conn.CreateCommand();
                upsert.CommandText = """
                    INSERT OR REPLACE INTO thumb_data (source_path, size_key, source_modified, thumb_blob, created_at)
                    VALUES (@p, @k, @m, @b, @c)
                    """;
                upsert.Parameters.AddWithValue("@p", sourcePath);
                upsert.Parameters.AddWithValue("@k", sizeKey);
                upsert.Parameters.AddWithValue("@m", sourceModified);
                upsert.Parameters.AddWithValue("@b", jpeg);
                upsert.Parameters.AddWithValue("@c", DateTime.UtcNow.ToString("O"));
                upsert.ExecuteNonQuery();
            }
        }
        catch { }
    }

    private static string ToDataUri(byte[] jpeg) => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    private static byte[] GenerateJpegBlob(string sourcePath, int size)
    {
        using var img = Image.FromFile(sourcePath);
        int w, h;
        if (img.Width > img.Height) { w = size; h = (int)(img.Height * (double)size / img.Width); }
        else { h = size; w = (int)(img.Width * (double)size / img.Height); }
        if (w < 1) w = 1; if (h < 1) h = 1;

        using var thumb = new Bitmap(w, h);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(img, 0, 0, w, h);

        using var ms = new MemoryStream();
        var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)75);
        thumb.Save(ms, jpegEncoder, encParams);
        return ms.ToArray();
    }

    /// <summary>后台批量预生成三档缩略图</summary>
    public void PreGenerateAll(string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                foreach (var key in Sizes.Keys)
                    GetBase64(path, key);
            }
            catch { }
        }
    }

    /// <summary>清理孤立记录（原图已删除）</summary>
    public void CleanOrphans()
    {
        lock (_db)
        {
            var stale = new List<(string path, string key)>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT source_path, size_key FROM thumb_data";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string p = reader.GetString(0);
                    if (!File.Exists(p))
                        stale.Add((p, reader.GetString(1)));
                }
            }
            foreach (var (p, k) in stale)
            {
                using var del = _conn.CreateCommand();
                del.CommandText = "DELETE FROM thumb_data WHERE source_path = @p AND size_key = @k";
                del.Parameters.AddWithValue("@p", p);
                del.Parameters.AddWithValue("@k", k);
                del.ExecuteNonQuery();
            }
        }
    }

    /// <summary>从缓存加载画廊列表</summary>
    public string LoadCachedGallery(List<string> folders)
    {
        lock (_db)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT file_path, file_name, folder, modified, width, height FROM gallery_files WHERE folder = @f ORDER BY modified DESC";
                cmd.Parameters.AddWithValue("@f", folder);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string path = reader.GetString(0);
                    if (!File.Exists(path)) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        ["path"] = path,
                        ["name"] = reader.GetString(1),
                        ["folder"] = reader.GetString(2),
                        ["modified"] = DateTime.Parse(reader.GetString(3)).ToString("yyyy-MM-dd HH:mm"),
                        ["width"] = reader.GetInt32(4),
                        ["height"] = reader.GetInt32(5),
                    });
                }
            }
            return JsonSerializer.Serialize(new { images = list });
        }
    }

    /// <summary>扫描文件夹并缓存元数据</summary>
    public string ScanFolders(List<string> folders)
    {
        lock (_db)
        {
            var list = new List<Dictionary<string, object>>();
            var now = DateTime.UtcNow.ToString("O");
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in GetImageFiles(folder))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        string mod = fi.LastWriteTimeUtc.ToString("O");
                        int w = 0, h = 0;
                        bool cached = false;
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT modified, width, height FROM gallery_files WHERE file_path = @p";
                            cmd.Parameters.AddWithValue("@p", file);
                            using var reader = cmd.ExecuteReader();
                            if (reader.Read() && reader.GetString(0) == mod)
                            { w = reader.GetInt32(1); h = reader.GetInt32(2); cached = true; }
                        }
                        if (!cached)
                        {
                            try { using var img = Image.FromFile(file); w = img.Width; h = img.Height; } catch { }
                            using var upsert = _conn.CreateCommand();
                            upsert.CommandText = "INSERT OR REPLACE INTO gallery_files VALUES (@p,@n,@f,@m,@w,@h,@s)";
                            upsert.Parameters.AddWithValue("@p", file);
                            upsert.Parameters.AddWithValue("@n", fi.Name);
                            upsert.Parameters.AddWithValue("@f", folder);
                            upsert.Parameters.AddWithValue("@m", mod);
                            upsert.Parameters.AddWithValue("@w", w);
                            upsert.Parameters.AddWithValue("@h", h);
                            upsert.Parameters.AddWithValue("@s", now);
                            upsert.ExecuteNonQuery();
                        }
                        list.Add(new Dictionary<string, object>
                        {
                            ["path"] = file, ["name"] = fi.Name, ["folder"] = folder,
                            ["modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                            ["width"] = w, ["height"] = h,
                        });
                    }
                    catch { }
                }
            }
            CleanStaleFileEntries(folders);
            list.Sort((a, b) => string.CompareOrdinal((string)b["modified"], (string)a["modified"]));
            return JsonSerializer.Serialize(new { images = list });
        }
    }

    /// <summary>
    /// 单独收一张图进缓存（自动刷新用）。写法跟 ScanFolders 里那段一样，
    /// 只是不做全目录遍历——来一张新图没道理把几万张再摸一遍
    /// </summary>
    public GalleryInsertResult InsertFile(string filePath, string folder)
    {
        lock (_db)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) return GalleryInsertResult.NotReady;

                string mod = fi.LastWriteTimeUtc.ToString("O");
                using (var check = _conn.CreateCommand())
                {
                    check.CommandText = "SELECT modified FROM gallery_files WHERE file_path = @p";
                    check.Parameters.AddWithValue("@p", filePath);
                    if (check.ExecuteScalar() as string == mod) return GalleryInsertResult.Unchanged;
                }

                // 尺寸读不出来就说明文件还没写完（Created 比写入快一步）。
                // 这时候**不能**存一条 0×0 —— JS 那边 width>0 才排版，
                // 存进去就是一张永远不出现的幽灵图
                int w, h;
                try
                {
                    using var img = Image.FromFile(filePath);
                    w = img.Width; h = img.Height;
                }
                catch { return GalleryInsertResult.NotReady; }

                using var upsert = _conn.CreateCommand();
                upsert.CommandText = "INSERT OR REPLACE INTO gallery_files VALUES (@p,@n,@f,@m,@w,@h,@s)";
                upsert.Parameters.AddWithValue("@p", filePath);
                upsert.Parameters.AddWithValue("@n", fi.Name);
                upsert.Parameters.AddWithValue("@f", folder);
                upsert.Parameters.AddWithValue("@m", mod);
                upsert.Parameters.AddWithValue("@w", w);
                upsert.Parameters.AddWithValue("@h", h);
                upsert.Parameters.AddWithValue("@s", DateTime.UtcNow.ToString("O"));
                upsert.ExecuteNonQuery();
                return GalleryInsertResult.Added;
            }
            catch { return GalleryInsertResult.NotReady; }
        }
    }

    /// <summary>原图没了：画廊记录和它那几档缩略图一起丢掉，不留占位</summary>
    public void RemoveFile(string filePath)
    {
        lock (_db)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM gallery_files WHERE file_path = @p;
                    DELETE FROM thumb_data    WHERE source_path = @p;
                    """;
                cmd.Parameters.AddWithValue("@p", filePath);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // 删不掉就留着：LoadCachedGallery 自己会跳过不存在的文件，
                // 界面上看不见它，下次全量扫描再清
            }
        }
    }

    public void InvalidateFolder(string folder)
    {
        lock (_db)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM gallery_files WHERE folder = @f";
            cmd.Parameters.AddWithValue("@f", folder);
            cmd.ExecuteNonQuery();
            // 清理该文件夹的缩略图 BLOB。
            // 这里以前把反斜杠换成斜杠再去 LIKE，可 source_path 存的全是反斜杠——
            // 条件永远不成立，于是「清除这个文件夹的缓存」只清了记录、没清图。
            // 现在按原样拼前缀，并且把 LIKE 的通配符转义掉
            using var cmd2 = _conn.CreateCommand();
            cmd2.CommandText = "DELETE FROM thumb_data WHERE source_path LIKE @p ESCAPE '~'";
            cmd2.Parameters.AddWithValue("@p", EscapeLike(folder.TrimEnd('\\', '/')) + "\\%");
            cmd2.ExecuteNonQuery();
        }
    }

    /// <summary>路径里真出现 % 或 _ 的话，LIKE 会把它当通配符——先转义掉</summary>
    private static string EscapeLike(string text)
        => text.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");

    private void CleanStaleFileEntries(List<string> activeFolders)
    {
        lock (_db)
        {
            // 只清「当前还在的文件夹」底下的记录。以前是一律清——
            // 外接盘没挂载的时候，那一整个盘的图会被当成已删除，
            // 记录和缩略图一起没，盘回来还得一张张重新解码。
            // 反正不在 ActiveGalleryFolders 里的行也不会显示出来，留着不碍事
            var active = new HashSet<string>(activeFolders, StringComparer.OrdinalIgnoreCase);

            var stale = new List<string>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT file_path, folder FROM gallery_files";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!active.Contains(reader.GetString(1))) continue;
                    string p = reader.GetString(0);
                    if (!File.Exists(p)) stale.Add(p);
                }
            }
            if (stale.Count == 0) return;

            // 缩略图跟着记录一起走。反正 File.Exists 已经查过了，
            // 这里多出来的只是一条按主键删除，不花什么
            using var tx = _conn.BeginTransaction();
            foreach (var p in stale)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM gallery_files WHERE file_path = @p;
                    DELETE FROM thumb_data    WHERE source_path = @p;
                    """;
                del.Parameters.AddWithValue("@p", p);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// 清掉「原图早就不在、缩略图却还留着」的孤儿 BLOB——历史遗留和
    /// 手动删掉的文件夹都会留下这种。
    /// 一条 SQL 整体清，**不**逐行去查文件在不在：那才是会拖慢的做法。
    /// 返回删掉的 BLOB 数
    /// </summary>
    public int PurgeOrphanThumbnails()
    {
        lock (_db)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM thumb_data
                    WHERE NOT EXISTS (
                        SELECT 1 FROM gallery_files g
                        WHERE g.file_path = thumb_data.source_path
                    )
                    """;
                return cmd.ExecuteNonQuery();
            }
            catch { return 0; }
        }
    }

    private static byte[] GetBlobBytes(SqliteDataReader reader, int ordinal)
    {
        using var stream = reader.GetStream(ordinal);
        if (stream == null || stream.Length == 0) return Array.Empty<byte>();
        var buf = new byte[stream.Length];
        stream.ReadExactly(buf);
        return buf;
    }

    public void Dispose()
    {
        lock (_db)
        {
            _conn?.Close();
            _conn?.Dispose();
        }
    }

    /// <summary>获取文件夹中所有支持的 AI 图片文件</summary>
    private static string[] GetImageFiles(string folder)
    {
        return new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" }
            .SelectMany(ext => Directory.GetFiles(folder, ext, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>单张图收进缓存的结果。三种情况调用方的处理完全不一样，所以不能只回 bool</summary>
public enum GalleryInsertResult
{
    /// <summary>收进去了（新图，或者原图被覆盖过）</summary>
    Added,

    /// <summary>缓存里已经是最新的，什么都不用做</summary>
    Unchanged,

    /// <summary>文件没了 / 还没写完 / 读不出来，过一会儿再来</summary>
    NotReady,
}

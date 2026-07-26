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

        // 查缓存
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT source_modified, thumb_blob FROM thumb_data WHERE source_path = @p AND size_key = @k";
            cmd.Parameters.AddWithValue("@p", sourcePath);
            cmd.Parameters.AddWithValue("@k", sizeKey);
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && reader.GetString(0) == sourceModified)
            {
                byte[] blob = GetBlobBytes(reader, 1);
                if (blob != null && blob.Length > 0)
                    return "data:image/jpeg;base64," + Convert.ToBase64String(blob);
            }
        }

        // 生成新缩略图
        try
        {
            byte[] jpeg = GenerateJpegBlob(sourcePath, size);
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
            return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
        }
        catch { return ""; }
    }

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

    /// <summary>从缓存加载画廊列表</summary>
    public string LoadCachedGallery(List<string> folders)
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

    /// <summary>扫描文件夹并缓存元数据</summary>
    public string ScanFolders(List<string> folders)
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

    public void InvalidateFolder(string folder)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM gallery_files WHERE folder = @f";
        cmd.Parameters.AddWithValue("@f", folder);
        cmd.ExecuteNonQuery();
        // 清理该文件夹的缩略图 BLOB
        using var cmd2 = _conn.CreateCommand();
        cmd2.CommandText = "DELETE FROM thumb_data WHERE source_path LIKE @p";
        cmd2.Parameters.AddWithValue("@p", folder.Replace("\\", "/") + "/%");
        cmd2.ExecuteNonQuery();
    }

    private void CleanStaleFileEntries(List<string> activeFolders)
    {
        var stale = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_path FROM gallery_files";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { string p = reader.GetString(0); if (!File.Exists(p)) stale.Add(p); }
        }
        foreach (var p in stale)
        {
            using var del = _conn.CreateCommand();
            del.CommandText = "DELETE FROM gallery_files WHERE file_path = @p";
            del.Parameters.AddWithValue("@p", p);
            del.ExecuteNonQuery();
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
        _conn?.Close();
        _conn?.Dispose();
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

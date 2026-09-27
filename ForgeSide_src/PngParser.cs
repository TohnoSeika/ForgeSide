using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ForgeSide;

/// <summary>
/// 从 PNG/JPEG/WebP 中提取 AI 生成参数 —— 桃华帮 Minagi 完善的作品哦 ✿
/// 支援 WebUI / Forge / ComfyUI 格式，覆盖更多图片类型和节点结构～
/// </summary>
public static class PngParser
{
    public record ImageParams(
        string Prompt = "",
        string NegativePrompt = "",
        long   Seed = -1,
        int    Steps = 0,
        double CfgScale = 0,
        string Sampler = "",
        string Model = "",
        int    Width = 0,
        int    Height = 0,
        bool   IsComfyUI = false,
        string RawText = ""
    );

    /// <summary>从文件路径读取</summary>
    public static ImageParams? Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Read(fs);
        }
        catch { return null; }
    }

    /// <summary>从流读取（支持 MemoryStream 等）</summary>
    public static ImageParams? Read(Stream stream)
    {
        try
        {
            var sig = new byte[12];
            if (stream.Read(sig, 0, 12) < 12) return null;

            // 根据魔数检测格式，把流位置倒回正確的起點
            if (IsPng(sig))
            {
                stream.Seek(8, SeekOrigin.Begin);
                return ParsePng(stream);
            }
            if (IsJpeg(sig))
            {
                stream.Seek(2, SeekOrigin.Begin);
                return ParseJpeg(stream);
            }
            if (IsWebP(sig))
            {
                stream.Seek(12, SeekOrigin.Begin);
                return ParseWebP(stream);
            }

            return null;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════
    //  格式檢測
    // ═══════════════════════════════════════

    private static bool IsPng(byte[] h) =>
        h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47;

    private static bool IsJpeg(byte[] h) =>
        h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;

    private static bool IsWebP(byte[] h) =>
        h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46
        && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50;

    // ═══════════════════════════════════════
    //  PNG 解析
    // ═══════════════════════════════════════

    /// <summary>
    /// 单个 chunk 的长度上限。PNG 规范允许到 2^31-1，可真实的 AI 出图里
    /// 最大的也就 ComfyUI 那几 MB 的 workflow 块。卡在 64MB 是为了挡住
    /// 长度字段被写坏的文件 —— 那种文件会让解析器照着字段去要内存
    /// </summary>
    private const int MaxChunkLength = 64 * 1024 * 1024;

    /// <summary>
    /// 解一个 iTXt 块。结构是：
    ///   关键字 \0 压缩标志(1字节) 压缩方法(1字节) 语言标签 \0 翻译关键字 \0 正文
    /// 正文按压缩标志决定要不要 zlib 解压，编码一律是 UTF-8。
    /// 语言标签和翻译关键字都不关心内容，跳过就好
    /// </summary>
    private static bool TryReadITxt(byte[] data, int length, out string key, out string value)
    {
        key = "";
        value = "";

        int keyEnd = Array.IndexOf(data, (byte)0, 0, length);
        if (keyEnd < 0 || keyEnd + 3 > length) return false;

        int pos = keyEnd + 1;
        bool compressed = data[pos] != 0;
        pos += 2;   // 压缩标志 + 压缩方法（目前只有 0 = zlib）

        for (int skip = 0; skip < 2; skip++)
        {
            int end = Array.IndexOf(data, (byte)0, pos, length - pos);
            if (end < 0) return false;
            pos = end + 1;
        }
        if (pos > length) return false;

        key = Encoding.UTF8.GetString(data, 0, keyEnd);
        var body = data.AsSpan(pos, length - pos);

        if (!compressed)
        {
            value = Encoding.UTF8.GetString(body);
            return true;
        }

        try
        {
            using var input  = new MemoryStream(body.ToArray());
            using var zlib   = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            value = Encoding.UTF8.GetString(output.ToArray());
            return true;
        }
        catch
        {
            // 压缩数据坏了就当这块不存在。整张图的参数不值得为它陪葬
            return false;
        }
    }

    private static ImageParams? ParsePng(Stream stream)
    {
        var textChunks = new Dictionary<string, string>();
        int imgWidth = 0, imgHeight = 0;

        while (stream.Position < stream.Length - 4)
        {
            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) < 4) break;
            int length = ReadBigEndianInt32(lenBuf);

            // 先确认这个长度真拿得出来，再分配。以前是读完字段直接 new byte[length]：
            // 一个把长度改成 0x7FFFFFFF 的坏文件就能让这里试着要 2GB，而 ForgeSide
            // 天生是和 Forge NEO 并行跑的，提交内存本来就紧。
            // 长度字段是 int，负数是高位被置 1 的情况，一并挡掉
            if (length < 0 || length > MaxChunkLength || length > stream.Length - stream.Position)
                break;

            var typeBuf = new byte[4];
            if (stream.Read(typeBuf, 0, 4) < 4) break;
            string type = Encoding.ASCII.GetString(typeBuf);

            var data = new byte[length];
            if (stream.Read(data, 0, length) < length) break;
            stream.Seek(4, SeekOrigin.Current); // CRC

            switch (type)
            {
                case "IHDR":
                    imgWidth = ReadBigEndianInt32(data.AsSpan(0, 4));
                    imgHeight = ReadBigEndianInt32(data.AsSpan(4, 4));
                    break;

                case "tEXt":
                    int nullIdx = Array.IndexOf<byte>(data, 0);
                    if (nullIdx < 0) continue;
                    string key = Encoding.UTF8.GetString(data, 0, nullIdx);
                    string val = Encoding.UTF8.GetString(data, nullIdx + 1, length - nullIdx - 1);
                    textChunks[key] = val;
                    break;

                case "iTXt":
                    // Pillow 的 add_text 碰上没法用 latin-1 编码的文本会改用 iTXt，
                    // 而 A1111 / Forge / ComfyUI 存 PNG 走的就是它 ——
                    // 也就是说，中文提示词的图，参数全在这个块里。
                    // 以前这儿只认 tEXt，那些图的参数面板就永远是空的，
                    // 看起来和「这张图没存参数」一模一样
                    if (TryReadITxt(data, length, out var itxtKey, out var itxtVal))
                        textChunks[itxtKey] = itxtVal;
                    break;

                case "IEND":
                    goto done;
            }
        }

    done:
        // WebUI / Forge 格式
        if (textChunks.TryGetValue("parameters", out var rawParams))
        {
            var parsed = ParseWebUIParams(rawParams);
            return parsed with
            {
                Width = parsed.Width > 0 ? parsed.Width : imgWidth,
                Height = parsed.Height > 0 ? parsed.Height : imgHeight,
                RawText = rawParams,
            };
        }

        // ComfyUI 格式
        if (textChunks.TryGetValue("prompt", out var rawPrompt))
        {
            var parsed = ParseComfyUI(rawPrompt);
            return parsed with
            {
                Width = parsed.Width > 0 ? parsed.Width : imgWidth,
                Height = parsed.Height > 0 ? parsed.Height : imgHeight,
                RawText = rawPrompt,
            };
        }

        return null;
    }

    // ═══════════════════════════════════════
    //  WebUI / Forge 參數行解析
    // ═══════════════════════════════════════

    private static ImageParams ParseWebUIParams(string raw)
    {
        var result = new ImageParams();

        // 找到 "Negative prompt:" 邊界
        int negIdx = raw.IndexOf("\nNegative prompt:");
        string promptPart, rest;

        if (negIdx >= 0)
        {
            promptPart = raw[..negIdx].Trim();
            rest = raw[(negIdx + 1)..].Trim();
        }
        else
        {
            // A1111 在负面提示词为空的时候，连 "Negative prompt:" 这一行都不写，
            // 参数行就直接贴在提示词下面。以前这里把整段当提示词、rest 留空，
            // 于是 Seed / Steps / Sampler 一个都进不了 ParseParamLine ——
            // 参数面板上只剩一大段原文，看着跟「这张图没存参数」没两样。
            // 桃华是拿一个漏写了负面提示词的样本撞出来的呢，撞得挺巧
            //
            // 前面补一个 \n 是为了让「整段就是参数行」也能命中：
            // 那样 paramsIdx 会是 0，promptPart 自然是空的
            int paramsIdx = FindParamLineStart("\n" + raw);
            if (paramsIdx >= 0)
            {
                promptPart = raw[..paramsIdx].Trim();
                rest       = raw[paramsIdx..].Trim();
            }
            else
            {
                promptPart = raw.Trim();
                rest = "";
            }
        }

        if (!string.IsNullOrWhiteSpace(promptPart))
            result = result with { Prompt = promptPart };

        // 解析 Negative prompt + 參數行
        if (rest.StartsWith("Negative prompt:"))
        {
            string afterNeg = rest["Negative prompt:".Length..].TrimStart();
            int paramsLineIdx = FindParamLineStart(afterNeg);

            if (paramsLineIdx >= 0)
            {
                result = result with { NegativePrompt = afterNeg[..paramsLineIdx].Trim() };
                ParseParamLine(afterNeg[paramsLineIdx..].Trim(), ref result);
            }
            else
            {
                // 沒有參數行——整段可能就是 negative prompt 或直接是參數
                if (afterNeg.Contains("Steps:") || afterNeg.Contains("Seed:"))
                {
                    result = result with { NegativePrompt = "" };
                    ParseParamLine(afterNeg, ref result);
                }
                else
                {
                    result = result with { NegativePrompt = afterNeg.Trim() };
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(rest))
        {
            // 只有參數行，沒有 negative prompt
            ParseParamLine(rest, ref result);
        }

        return result;
    }

    private static int FindParamLineStart(string text)
    {
        var keys = new[] { "\nSteps:", "\nSeed:", "\nSampler:", "\nCFG scale:", "\nModel:", "\nSize:" };
        int best = -1;
        foreach (var key in keys)
        {
            int idx = text.IndexOf(key, StringComparison.Ordinal);
            if (idx >= 0 && (best < 0 || idx < best))
                best = idx;
        }
        return best;
    }

    private static void ParseParamLine(string line, ref ImageParams result)
    {
        var lines = line.Split('\n', StringSplitOptions.TrimEntries);
        string paramLine = line;
        foreach (var l in lines)
        {
            if (l.Contains("Steps:") || l.Contains("Seed:") || l.Contains("Sampler:"))
            {
                paramLine = l;
                break;
            }
        }

        var pairs = paramLine.Split(',', StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            int colon = pair.IndexOf(':');
            if (colon < 0) continue;
            string key = pair[..colon].Trim();
            string val = pair[(colon + 1)..].Trim();

            switch (key)
            {
                case "Seed":           result = result with { Seed    = long.TryParse(val, out var s) ? s : -1 }; break;
                case "Steps":          result = result with { Steps   = int.TryParse(val, out var st) ? st : 0 }; break;
                case "CFG scale":      result = result with { CfgScale = double.TryParse(val, out var c) ? c : 0 }; break;
                case "Sampler":        result = result with { Sampler = val }; break;
                case "Model":          result = result with { Model   = val }; break;
                case "Model hash":     break;
                case "Size":           ParseSize(val, out var w, out var h);
                                       result = result with { Width = w, Height = h }; break;
                case "Hires resize":   ParseSize(val.Split('(')[0], out var hw, out var hh);
                                       result = result with { Width = hw, Height = hh }; break;
            }
        }
    }

    private static void ParseSize(string val, out int w, out int h)
    {
        w = h = 0;
        var parts = val.Split('x');
        if (parts.Length == 2)
        {
            int.TryParse(parts[0].Trim(), out w);
            int.TryParse(parts[1].Trim(), out h);
        }
    }

    // ═══════════════════════════════════════
    //  ComfyUI 解析
    // ═══════════════════════════════════════

    private static ImageParams ParseComfyUI(string json)
    {
        var result = new ImageParams { IsComfyUI = true };
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // 確定節點集合：有 "nodes" 屬性則用它，否則把整個根對象當作節點映射
            JsonElement nodes;
            if (root.TryGetProperty("nodes", out var n))
                nodes = n;
            else if (root.ValueKind == JsonValueKind.Object)
                nodes = root;
            else
                return result;

            string? posPrompt = null, negPrompt = null, model = null;
            long seed = -1;
            int steps = 0, width = 0, height = 0;
            double cfg = 0;
            string? sampler = null;

            ForEachNode(nodes, (nodeEl, nodeId) =>
            {
                // 跳過非對象節點
                if (nodeEl.ValueKind != JsonValueKind.Object) return;

                string? nodeType = GetStringProp(nodeEl, "class_type")
                                ?? GetStringProp(nodeEl, "type");

                var inputs = nodeEl.TryGetProperty("inputs", out var ins) && ins.ValueKind == JsonValueKind.Object
                    ? ins : default;
                if (inputs.ValueKind != JsonValueKind.Object) return;

                // ── 提示詞提取（不限於 CLIPTextEncode，任何有文本輸入的節點）──
                // 支援 text / prompt / string 三種字段名
                if ((inputs.TryGetProperty("text", out var txt)
                     || inputs.TryGetProperty("prompt", out txt)
                     || inputs.TryGetProperty("string", out txt))
                    && txt.ValueKind == JsonValueKind.String)
                {
                    string text = txt.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string? title = GetStringProp(nodeEl, "_meta.title")
                                     ?? GetStringProp(nodeEl, "title");
                        bool isNeg = title != null
                            && title.Contains("neg", StringComparison.OrdinalIgnoreCase);

                        if (isNeg && negPrompt == null)
                            negPrompt = text;
                        else if (posPrompt == null)
                            posPrompt = text;
                        else if (negPrompt == null)
                            negPrompt = text;
                    }
                }

                // ── 種子 / 步數 / CFG ──
                if (seed < 0 && inputs.TryGetProperty("seed", out var sd))
                {
                    if (sd.ValueKind == JsonValueKind.Number) seed = sd.GetInt64();
                    else if (sd.ValueKind == JsonValueKind.String && long.TryParse(sd.GetString(), out var sp)) seed = sp;
                }
                if (steps == 0 && inputs.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Number)
                    steps = st.GetInt32();
                if (cfg == 0 && inputs.TryGetProperty("cfg", out var cf) && cf.ValueKind == JsonValueKind.Number)
                    cfg = cf.GetDouble();
                if (sampler == null && inputs.TryGetProperty("sampler_name", out var sn) && sn.ValueKind == JsonValueKind.String)
                    sampler = sn.GetString();

                // ── 模型名稱 ──
                if (inputs.TryGetProperty("ckpt_name", out var ckpt) && ckpt.ValueKind == JsonValueKind.String)
                    model ??= ckpt.GetString();
                if (inputs.TryGetProperty("model_name", out var mn) && mn.ValueKind == JsonValueKind.String)
                    model ??= mn.GetString();
                if (inputs.TryGetProperty("unet_name", out var un) && un.ValueKind == JsonValueKind.String)
                    model ??= un.GetString();

                // ── 圖片尺寸 ──
                if (width == 0 && inputs.TryGetProperty("width", out var wd) && wd.ValueKind == JsonValueKind.Number)
                    width = wd.GetInt32();
                if (height == 0 && inputs.TryGetProperty("height", out var ht) && ht.ValueKind == JsonValueKind.Number)
                    height = ht.GetInt32();
            });

            result = result with
            {
                Prompt = posPrompt ?? "",
                NegativePrompt = negPrompt ?? "",
                Seed = seed,
                Steps = steps,
                CfgScale = cfg,
                Sampler = sampler ?? "",
                Model = model ?? "",
                Width = width,
                Height = height,
            };
        }
        catch { /* 解析失敗，返回已有字段 */ }

        return result;
    }

    private static string? GetStringProp(JsonElement el, string path)
    {
        // 支援點路徑： "meta.title" → el.meta.title
        var parts = path.Split('.');
        var current = el;
        foreach (var part in parts)
        {
            if (!current.TryGetProperty(part, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static void ForEachNode(JsonElement nodes, Action<JsonElement, string> callback)
    {
        if (nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                string id = "0";
                if (node.TryGetProperty("id", out var idEl))
                    id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString()
                       : idEl.GetString() ?? "0";
                callback(node, id);
            }
        }
        else if (nodes.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in nodes.EnumerateObject())
            {
                if (kv.Name is "nodes" or "links" or "last_node_id" or "last_link_id" or "version" or "id" or "revision")
                    continue;
                callback(kv.Value, kv.Name);
            }
        }
    }

    // ═══════════════════════════════════════
    //  JPEG 解析
    // ═══════════════════════════════════════

    private static ImageParams? ParseJpeg(Stream stream)
    {
        int imgWidth = 0, imgHeight = 0;

        try
        {
            while (stream.Position < stream.Length - 2)
            {
                var marker = new byte[2];
                if (stream.Read(marker, 0, 2) < 2) break;

                if (marker[0] != 0xFF) break;

                // SOS（Start of Scan）— 圖像數據開始，停止解析
                if (marker[1] == 0xDA) break;

                // SOI，跳過
                if (marker[1] == 0xD8) continue;

                // 讀取段長度
                var lenBuf = new byte[2];
                if (stream.Read(lenBuf, 0, 2) < 2) break;
                int segLen = (lenBuf[0] << 8) | lenBuf[1];
                if (segLen < 2) break;
                int dataLen = segLen - 2;

                switch (marker[1])
                {
                    case 0xE1: // APP1 — EXIF
                        if (dataLen > 6)
                        {
                            var appData = new byte[dataLen];
                            if (stream.Read(appData, 0, dataLen) < dataLen) break;

                            string exifHeader = Encoding.ASCII.GetString(appData, 0, 6);
                            if (exifHeader == "Exif\0\0")
                            {
                                if (ParseExifUserComment(appData.AsSpan(6), out var comment) && comment != null)
                                {
                                    var parsed = ParseWebUIParams(comment);
                                    return parsed with { RawText = comment };
                                }
                            }
                        }
                        else
                        {
                            stream.Seek(dataLen, SeekOrigin.Current);
                        }
                        break;

                    case 0xFE: // COM — 註釋
                        var comData = new byte[dataLen];
                        stream.ReadExactly(comData);
                        string comText = Encoding.UTF8.GetString(comData).Trim();
                        if (comText.Contains("Steps:") || comText.Contains("parameters"))
                        {
                            var parsed = ParseWebUIParams(comText);
                            return parsed with { RawText = comText };
                        }
                        break;

                    case 0xC0: // SOF0
                    case 0xC2: // SOF2（漸進式）
                        var sofData = new byte[dataLen];
                        stream.ReadExactly(sofData);
                        if (sofData.Length >= 5)
                        {
                            imgHeight = (sofData[1] << 8) | sofData[2];
                            imgWidth  = (sofData[3] << 8) | sofData[4];
                        }
                        break;

                    default:
                        stream.Seek(dataLen, SeekOrigin.Current);
                        break;
                }
            }
        }
        catch (EndOfStreamException) { }

        // 有尺寸但沒 metadata 的 JPEG 也返回（至少提供尺寸）
        if (imgWidth > 0 && imgHeight > 0)
            return new ImageParams(Width: imgWidth, Height: imgHeight);

        return null;
    }

    /// <summary>從 EXIF TIFF 結構中讀取 UserComment (tag 0x9286)</summary>
    private static bool ParseExifUserComment(ReadOnlySpan<byte> exifData, out string? comment)
    {
        comment = null;
        try
        {
            if (exifData.Length < 8) return false;

            bool isLittle = exifData[0] == 'I' && exifData[1] == 'I';
            bool isBig    = exifData[0] == 'M' && exifData[1] == 'M';
            if (!isLittle && !isBig) return false;

            // TIFF header 魔數 0x002A——小端寫成 2A 00，大端寫成 00 2A。
            // 這裡以前兩種都要求第 3 個 byte 是 0x2A，大端檔案永遠過不了
            if (isLittle ? (exifData[2] != 0x2A || exifData[3] != 0x00)
                         : (exifData[2] != 0x00 || exifData[3] != 0x2A))
                return false;

            // IFD0 偏移
            int ifdOffset = isLittle
                ? exifData[4] | (exifData[5] << 8) | (exifData[6] << 16) | (exifData[7] << 24)
                : (exifData[4] << 24) | (exifData[5] << 16) | (exifData[6] << 8) | exifData[7];

            if (ifdOffset <= 0 || ifdOffset >= exifData.Length - 2) return false;

            // Entry count
            int entryCount = isLittle
                ? exifData[ifdOffset] | (exifData[ifdOffset + 1] << 8)
                : (exifData[ifdOffset] << 8) | exifData[ifdOffset + 1];

            for (int i = 0; i < entryCount; i++)
            {
                int entryOff = ifdOffset + 2 + i * 12;
                if (entryOff + 12 > exifData.Length) break;

                int tag = isLittle
                    ? exifData[entryOff] | (exifData[entryOff + 1] << 8)
                    : (exifData[entryOff] << 8) | exifData[entryOff + 1];

                if (tag != 0x9286) continue; // UserComment

                // Data 的偏移/值（4 字節）
                int dataOffset = isLittle
                    ? exifData[entryOff + 8] | (exifData[entryOff + 9] << 8) | (exifData[entryOff + 10] << 16) | (exifData[entryOff + 11] << 24)
                    : (exifData[entryOff + 8] << 24) | (exifData[entryOff + 9] << 16) | (exifData[entryOff + 10] << 8) | exifData[entryOff + 11];

                if (dataOffset < 0 || dataOffset >= exifData.Length) break;

                var remaining = exifData[dataOffset..];
                if (remaining.Length < 8) break;

                // 前 8 字節是編碼格式標識
                string codec = Encoding.ASCII.GetString(remaining[..8].ToArray()).TrimEnd('\0');

                if (codec.StartsWith("ASCII") || codec.StartsWith("UNDEFINED"))
                    comment = Encoding.ASCII.GetString(remaining[8..].ToArray()).Trim('\0', ' ');
                else if (codec.StartsWith("UNICODE") || codec.StartsWith("JIS"))
                    comment = Encoding.Unicode.GetString(remaining[8..].ToArray()).Trim('\0', ' ');

                return comment != null;
            }
        }
        catch { }

        return false;
    }

    // ═══════════════════════════════════════
    //  WebP 解析
    // ═══════════════════════════════════════

    private static ImageParams? ParseWebP(Stream stream)
    {
        int imgWidth = 0, imgHeight = 0;

        while (stream.Position < stream.Length - 8)
        {
            var chunkHeader = new byte[8];
            if (stream.Read(chunkHeader, 0, 8) < 8) break;

            string chunkType = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            int chunkSize = chunkHeader[4] | (chunkHeader[5] << 8)
                          | (chunkHeader[6] << 16) | (chunkHeader[7] << 24);

            if (chunkSize <= 0 || chunkSize > stream.Length - stream.Position) break;

            if (chunkType == "VP8 " || chunkType == "VP8L")
            {
                var data = new byte[Math.Min(chunkSize, 32)];
                stream.ReadExactly(data);

                if (chunkType == "VP8 ")
                {
                    // VP8 keyframe：3 byte sync + 3 byte start code，然後寬高各 14 bit
                    // （低 8 位在前，高 6 位在後一個 byte 的 bit0-5）
                    if (data.Length >= 10)
                    {
                        imgWidth  = data[6] | ((data[7] & 0x3F) << 8);
                        imgHeight = data[8] | ((data[9] & 0x3F) << 8);
                    }
                }
                else // VP8L
                {
                    // VP8L：1 byte 簽名 + 小端 32 位，裡面塞著 (寬-1, 高-1)，各 14 bit
                    if (data.Length >= 5)
                    {
                        int packed = data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24);
                        imgWidth  = (packed & 0x3FFF) + 1;
                        imgHeight = ((packed >> 14) & 0x3FFF) + 1;
                    }
                }

                if (chunkSize > 32)
                    stream.Seek(chunkSize - 32, SeekOrigin.Current);

                continue;
            }

            // WebP 的 EXIF chunk 格式與 JPEG 相同
            if (chunkType == "EXIF")
            {
                var exifData = new byte[chunkSize];
                stream.ReadExactly(exifData);

                if (ParseExifUserComment(exifData, out var comment) && comment != null)
                {
                    var parsed = ParseWebUIParams(comment);
                    return parsed with
                    {
                        Width = parsed.Width > 0 ? parsed.Width : imgWidth,
                        Height = parsed.Height > 0 ? parsed.Height : imgHeight,
                        RawText = comment,
                    };
                }
                break;
            }

            stream.Seek(chunkSize, SeekOrigin.Current);
        }

        if (imgWidth > 0 && imgHeight > 0)
            return new ImageParams(Width: imgWidth, Height: imgHeight);

        return null;
    }

    // ═══════════════════════════════════════
    //  工具函數
    // ═══════════════════════════════════════

    private static int ReadBigEndianInt32(byte[] buf) =>
        (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> buf) =>
        (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
}

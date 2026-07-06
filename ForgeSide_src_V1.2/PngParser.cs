using System.Text;
using System.Text.Json;

namespace ForgeSide;

/// <summary>从 PNG tEXt chunk 中提取 AI 生成参数 —— 桃华帮 Minagi 写的哦</summary>
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
            var sig = new byte[8];
            stream.ReadExactly(sig);
            if (sig[0] != 0x89 || sig[1] != 'P' || sig[2] != 'N' || sig[3] != 'G')
                return null;

            var parameters = new Dictionary<string, string>();

            while (stream.Position < stream.Length)
            {
                var lenBytes = new byte[4];
                stream.ReadExactly(lenBytes);
                if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                int length = BitConverter.ToInt32(lenBytes);

                var typeBytes = new byte[4];
                stream.ReadExactly(typeBytes);
                string type = Encoding.ASCII.GetString(typeBytes);

                var data = new byte[length];
                stream.ReadExactly(data);
                stream.Seek(4, SeekOrigin.Current); // CRC

                if (type == "tEXt")
                {
                    int nullIdx = Array.IndexOf<byte>(data, 0);
                    if (nullIdx < 0) continue;
                    string key = Encoding.UTF8.GetString(data, 0, nullIdx);
                    string val = Encoding.UTF8.GetString(data, nullIdx + 1, length - nullIdx - 1);
                    parameters[key] = val;
                }
                else if (type == "IEND")
                {
                    break;
                }
            }

            // WebUI / Forge 格式
            if (parameters.TryGetValue("parameters", out var rawParams))
            {
                var parsed = ParseWebUI(rawParams);
                return parsed with { RawText = rawParams };
            }

            // ComfyUI 格式
            if (parameters.TryGetValue("prompt", out var rawPrompt))
            {
                var parsed = ParseComfyUI(rawPrompt);
                return parsed with { RawText = rawPrompt };
            }

            return null;
        }
        catch { return null; }
    }

    // ═══ WebUI / Forge 格式解析 ═══
    private static ImageParams ParseWebUI(string raw)
    {
        var result = new ImageParams();

        // 找到 "Negative prompt:" 边界，这样即使 prompt 有多行也能正确提取
        int negIdx = raw.IndexOf("\nNegative prompt:");
        string promptPart, rest;

        if (negIdx >= 0)
        {
            promptPart = raw[..negIdx].Trim();
            rest = raw[(negIdx + 1)..].Trim();
        }
        else
        {
            // 没有 Negative prompt 的情况（极少见）
            promptPart = raw.Trim();
            rest = "";
        }

        result = result with { Prompt = promptPart };

        // 解析剩余部分（Negative prompt + 参数行）
        if (rest.StartsWith("Negative prompt:"))
        {
            // 找到参数行起始（以 "Steps:" 或常见参数键开头的那一行）
            string afterNeg = rest["Negative prompt:".Length..].TrimStart();
            int paramsLineIdx = FindParamLineStart(afterNeg);

            if (paramsLineIdx >= 0)
            {
                result = result with { NegativePrompt = afterNeg[..paramsLineIdx].Trim() };
                ParseParamLine(afterNeg[paramsLineIdx..].Trim(), ref result);
            }
            else
            {
                // 可能整行就是参数，没有独立的 negative prompt
                // 尝试作为参数行解析
                string negText = afterNeg;
                // 如果 afterNeg 里有逗号分隔的参数键值对，可能是参数行
                if (afterNeg.Contains("Steps:") || afterNeg.Contains("Seed:"))
                {
                    result = result with { NegativePrompt = "" };
                    ParseParamLine(afterNeg, ref result);
                }
                else
                {
                    result = result with { NegativePrompt = negText };
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(rest))
        {
            ParseParamLine(rest, ref result);
        }

        return result;
    }

    /// <summary>在文本中找参数行开始位置（以 Steps: / Seed: 等键开头）</summary>
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

    /// <summary>解析参数行（逗号分隔的 key: value 对）</summary>
    private static void ParseParamLine(string line, ref ImageParams result)
    {
        // 如果有多行，只取包含参数键的第一行
        var lines = line.Split('\n', StringSplitOptions.TrimEntries);
        string paramLine = line; // 默认用整段
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
                case "Model hash":     break; // 跳过 hash
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

    // ═══ ComfyUI 格式解析 ═══
    // 兼容两种格式:
    //   旧格式 (prompt chunk): {"1":{class_type:"KSampler",inputs:{seed:...}}, "2":{...}}  ← 根即节点映射
    //   新格式 (API):          {"nodes":[{type:"KSampler",...}], "links":[...], ...}
    private static ImageParams ParseComfyUI(string json)
    {
        var result = new ImageParams { IsComfyUI = true };
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // 确定节点集合：有 "nodes" 属性则用它，否则把整个根对象当作节点映射
            JsonElement nodes;
            if (root.TryGetProperty("nodes", out var n))
            {
                nodes = n;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // 旧格式：根对象就是节点映射 {"1":{...}, "2":{...}}
                // 如果根对象的子元素看起来像节点（有 class_type 或 type），就用它
                nodes = root;
            }
            else
            {
                return result;
            }

            string? posPrompt = null, negPrompt = null, model = null;
            long seed = -1;
            int steps = 0, width = 0, height = 0;
            double cfg = 0;
            string? sampler = null;

            ForEachNode(nodes, (nodeEl, nodeId) =>
            {
                // class_type = 旧格式, type = 新格式
                string? nodeType = GetStringProp(nodeEl, "class_type")
                                ?? GetStringProp(nodeEl, "type");
                var inputs = nodeEl.TryGetProperty("inputs", out var i) ? i : default;

                // _meta.title = 旧格式, title = 新格式
                string? title = null;
                if (nodeEl.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("title", out var mt))
                    title = mt.GetString();
                title ??= GetStringProp(nodeEl, "title");

                switch (nodeType)
                {
                    case "CLIPTextEncode":
                        if (inputs.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        {
                            string text = txt.GetString() ?? "";
                            bool isNeg = (title != null && title.Contains("neg", StringComparison.OrdinalIgnoreCase))
                                      || (posPrompt != null && string.CompareOrdinal(nodeId, posPrompt) > 0);
                            if (isNeg && negPrompt == null)
                                negPrompt = text;
                            else if (posPrompt == null)
                                posPrompt = text;
                            else if (negPrompt == null)
                                negPrompt = text;
                        }
                        break;

                    case "CheckpointLoaderSimple":
                    case "CheckpointLoader":
                    case "LoadCheckpoint":
                    case "LoadDiffusionModel":
                    case "UNETLoader":
                        if (inputs.TryGetProperty("ckpt_name", out var ckpt) && ckpt.ValueKind == JsonValueKind.String)
                            model = ckpt.GetString(); // 扩散模型优先，覆盖之前的
                        else if (inputs.TryGetProperty("unet_name", out var un) && un.ValueKind == JsonValueKind.String)
                            model = un.GetString();
                        break;

                    case "CLIPLoader":
                        if (inputs.TryGetProperty("clip_name", out var cn) && cn.ValueKind == JsonValueKind.String)
                            model ??= cn.GetString(); // 次选
                        break;

                    case "VAELoader":
                        // VAE 不作为模型名
                        break;

                    case "KSampler":
                    case "KSamplerAdvanced":
                        if (inputs.TryGetProperty("seed", out var s))
                        {
                            if (s.ValueKind == JsonValueKind.Number) seed = s.GetInt64();
                            else if (s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), out var sp)) seed = sp;
                        }
                        if (inputs.TryGetProperty("steps", out var st)) steps = st.GetInt32();
                        if (inputs.TryGetProperty("cfg", out var c)) cfg = c.GetDouble();
                        if (inputs.TryGetProperty("sampler_name", out var sn)) sampler = sn.GetString();
                        break;

                    case "EmptyLatentImage":
                        if (inputs.TryGetProperty("width", out var ew)) width = ew.GetInt32();
                        if (inputs.TryGetProperty("height", out var eh)) height = eh.GetInt32();
                        break;
                }
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
        catch { /* 解析失败，返回已有字段 */ }

        return result;
    }

    private static string? GetStringProp(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    /// <summary>遍历 ComfyUI 节点，兼容数组 [{id,type,...}] 和对象 {"id":{type,...}} 两种格式</summary>
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
                // 跳过元数据属性（非节点对象）
                if (kv.Name is "nodes" or "links" or "last_node_id" or "last_link_id" or "version" or "id" or "revision")
                    continue;
                callback(kv.Value, kv.Name);
            }
        }
    }
}

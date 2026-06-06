using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ukg.Core;

/// <summary>
/// テキストを固定次元ベクトルへ写像する埋め込み器。設定で差し替え可能なプラグイン（ADR-001/ADR-010）。
/// </summary>
public interface IEmbedder
{
    int Dimension { get; }

    /// <summary>索引署名。変化したらベクトル索引を作り直し全再埋め込みする（次元安全）。</summary>
    string Id { get; }

    float[] Embed(string text);

    /// <summary>バッチ埋め込み。既定は逐次。HTTP系は1リクエストにまとめて上書きする。</summary>
    IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) result[i] = Embed(texts[i]);
        return result;
    }
}

/// <summary>環境変数から埋め込み器を生成するファクトリ（ADR-010）。</summary>
public static class Embedders
{
    /// <summary>
    /// <c>UKG_EMBEDDER</c> = hashing(既定) | http(=openai)。
    /// http時: <c>UKG_EMBED_URL</c>(既定 OpenAI) / <c>UKG_EMBED_MODEL</c> / <c>UKG_EMBED_API_KEY</c> /
    /// <c>UKG_EMBED_DIM</c>(必須=モデルの出力次元)。
    /// </summary>
    public static IEmbedder FromEnvironment()
    {
        var kind = (Env("UKG_EMBEDDER") ?? "hashing").ToLowerInvariant();
        switch (kind)
        {
            case "hashing":
                return new HashingEmbedder(EnvInt("UKG_EMBED_DIM") ?? 256);
            case "http":
            case "openai":
                var url = Env("UKG_EMBED_URL") ?? "https://api.openai.com/v1/embeddings";
                var model = Env("UKG_EMBED_MODEL") ?? "text-embedding-3-small";
                var dim = EnvInt("UKG_EMBED_DIM")
                          ?? throw new InvalidOperationException("UKG_EMBED_DIM（モデルの出力次元）を設定してください");
                return new HttpEmbedder(url, model, Env("UKG_EMBED_API_KEY"), dim);
            default:
                throw new InvalidOperationException($"未知の UKG_EMBEDDER: {kind}（hashing|http）");
        }
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static int? EnvInt(string k) => int.TryParse(Env(k), out var v) ? v : null;
}

/// <summary>
/// 外部依存なしの決定的埋め込み（フィーチャーハッシュ法）。
/// トークンをハッシュで次元へ割り当て、TF重みを置いて L2 正規化する。
/// 同入力＝同ベクトルなのでキャッシュ・ゴールデンテストが成立する（ADR-001）。
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public int Dimension { get; }
    public string Id => $"hashing-{Dimension}";

    public HashingEmbedder(int dimension = 256) => Dimension = dimension;

    public float[] Embed(string text)
    {
        var vec = new float[Dimension];
        foreach (var tok in Tokenize(text))
        {
            // 符号付きハッシュで衝突の偏りを緩和（feature hashing の符号トリック）
            uint h = Fnv1a(tok);
            int idx = (int)(h % (uint)Dimension);
            float sign = (h & 0x80000000u) != 0 ? -1f : 1f;
            vec[idx] += sign;

            // バイグラム（部分的な語順情報）
            foreach (var bg in Bigrams(tok))
            {
                uint h2 = Fnv1a(bg);
                vec[(int)(h2 % (uint)Dimension)] += (h2 & 1u) != 0 ? -0.5f : 0.5f;
            }
        }

        // L2 正規化（cosine 用）
        double norm = 0;
        foreach (var x in vec) norm += (double)x * x;
        norm = Math.Sqrt(norm);
        if (norm > 1e-9)
            for (int i = 0; i < vec.Length; i++) vec[i] = (float)(vec[i] / norm);
        return vec;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } }
        }
        if (sb.Length > 0) yield return sb.ToString();

        // CamelCase / PascalCase の分割語も語彙として加える（識別子の意味を拾う）
        foreach (var sub in SplitCamel(text))
            yield return sub;
    }

    private static IEnumerable<string> SplitCamel(string text)
    {
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch)) { if (sb.Length > 1) yield return sb.ToString().ToLowerInvariant(); sb.Clear(); prev = ch; continue; }
            if (char.IsUpper(ch) && char.IsLower(prev) && sb.Length > 1) { yield return sb.ToString().ToLowerInvariant(); sb.Clear(); }
            sb.Append(ch);
            prev = ch;
        }
        if (sb.Length > 1) yield return sb.ToString().ToLowerInvariant();
    }

    private static IEnumerable<string> Bigrams(string tok)
    {
        for (int i = 0; i + 1 < tok.Length; i++) yield return tok.Substring(i, 2);
    }

    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261;
        foreach (var ch in s) { hash ^= ch; hash *= 16777619; }
        return hash;
    }
}

/// <summary>
/// OpenAI互換の <c>/v1/embeddings</c> を叩く埋め込み器（ADR-010）。URL を変えるだけで
/// OpenAI / Azure / Ollama / text-embeddings-inference / LM Studio 等を1実装で賄う。
/// </summary>
public sealed class HttpEmbedder : IEmbedder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _url;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly bool _sendDimensions;

    public int Dimension { get; }
    public string Id => $"http:{_model}:{Dimension}";

    public HttpEmbedder(string url, string model, string? apiKey, int dimension)
    {
        _url = url; _model = model; _apiKey = apiKey; Dimension = dimension;
        // OpenAI v3 は dimensions で次元を固定できる。互換サーバには送らない（既定）。
        _sendDimensions = url.Contains("openai.com", StringComparison.OrdinalIgnoreCase);
    }

    public float[] Embed(string text) => EmbedBatch(new[] { text })[0];

    public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        var payload = new Dictionary<string, object> { ["model"] = _model, ["input"] = texts };
        if (_sendDimensions) payload["dimensions"] = Dimension;

        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = Http.Send(req);
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"埋め込みAPIが {(int)resp.StatusCode} を返しました: {Truncate(body, 300)}");

        var vectors = ParseEmbeddings(body, texts.Count);
        for (int i = 0; i < vectors.Length; i++)
            if (vectors[i].Length != Dimension)
                throw new InvalidOperationException(
                    $"埋め込み次元が UKG_EMBED_DIM={Dimension} と一致しません（実際 {vectors[i].Length}）。モデルの出力次元に合わせてください。");
        return vectors;
    }

    private static float[][] ParseEmbeddings(string json, int expected)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var result = new float[data.GetArrayLength()][];
        foreach (var item in data.EnumerateArray())
        {
            int idx = item.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
            var arr = item.GetProperty("embedding");
            var vec = new float[arr.GetArrayLength()];
            int j = 0;
            foreach (var f in arr.EnumerateArray()) vec[j++] = f.GetSingle();
            result[idx] = vec;
        }
        return result;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>ハッシュユーティリティ。</summary>
public static class Hashing
{
    public static string Sha1(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

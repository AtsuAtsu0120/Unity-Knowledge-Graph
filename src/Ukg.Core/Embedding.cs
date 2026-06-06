using System.Security.Cryptography;
using System.Text;

namespace Ukg.Core;

/// <summary>テキストを固定次元ベクトルへ写像する埋め込み器。実APIに差し替え可能（ADR-001）。</summary>
public interface IEmbedder
{
    int Dimension { get; }
    float[] Embed(string text);
}

/// <summary>
/// 外部依存なしの決定的埋め込み（フィーチャーハッシュ法）。
/// トークンをハッシュで次元へ割り当て、TF重みを置いて L2 正規化する。
/// 同入力＝同ベクトルなのでキャッシュ・ゴールデンテストが成立する（ADR-001）。
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public int Dimension { get; }

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

/// <summary>ハッシュユーティリティ。</summary>
public static class Hashing
{
    public static string Sha1(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

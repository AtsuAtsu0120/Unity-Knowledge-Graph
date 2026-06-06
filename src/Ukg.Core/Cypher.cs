using System.Globalization;
using System.Text;

namespace Ukg.Core;

/// <summary>32bit 浮動小数ベクトル。FalkorDB の <c>vecf32([...])</c> リテラルとして出力される。</summary>
public readonly record struct Vec(float[] Values);

/// <summary>
/// Cypher リテラルを安全に組み立てるためのヘルパ。
/// 値はパラメータ前置（<c>CYPHER k=&lt;literal&gt;</c>）で渡し、ボディは静的に保つ（ADR-002）。
/// リテラル生成はここに集約し、エスケープ漏れを防ぐ。
/// </summary>
public static class Cypher
{
    /// <summary>文字列を単一引用符で囲んだ Cypher リテラルにエスケープする。</summary>
    public static string Str(string? value)
    {
        if (value is null) return "null";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': break; // ヌルは捨てる
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>任意の値を Cypher リテラルへ変換する（list/map/vecf32 を含む）。</summary>
    public static string Value(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int or long or short or byte => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        string s => Str(s),
        Vec vec => Vecf32(vec.Values),
        IReadOnlyDictionary<string, object?> map => Map(map),
        System.Collections.IEnumerable seq => List(seq),
        _ => Str(value.ToString())
    };

    /// <summary>vecf32 ベクトルリテラル。</summary>
    public static string Vecf32(IReadOnlyList<float> v)
    {
        var sb = new StringBuilder("vecf32([");
        for (int i = 0; i < v.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append("])");
        return sb.ToString();
    }

    /// <summary>リストリテラル <c>[a, b, ...]</c>。</summary>
    public static string List(System.Collections.IEnumerable seq)
    {
        var parts = new List<string>();
        foreach (var item in seq) parts.Add(Value(item));
        return "[" + string.Join(", ", parts) + "]";
    }

    /// <summary>プロパティ辞書を <c>{k: v, ...}</c> のマップリテラルにする。</summary>
    public static string Map(IReadOnlyDictionary<string, object?> props)
    {
        if (props.Count == 0) return "{}";
        var parts = props.Select(kv => $"{Ident(kv.Key)}: {Value(kv.Value)}");
        return "{" + string.Join(", ", parts) + "}";
    }

    /// <summary>プロパティ名/ラベルを検証する（英数字とアンダースコアのみ許可）。</summary>
    public static string Ident(string ident)
    {
        if (string.IsNullOrEmpty(ident)) throw new ArgumentException("Empty identifier");
        foreach (var c in ident)
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"Invalid identifier: {ident}");
        return ident;
    }

    /// <summary>
    /// パラメータをボディ前置の <c>CYPHER k=&lt;literal&gt; ...</c> 文へ展開する。
    /// ボディ側は <c>$k</c> で参照され構造インジェクションを受けない（ADR-002）。
    /// </summary>
    public static string WithParams(string body, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return body;
        var sb = new StringBuilder("CYPHER ");
        foreach (var (k, v) in parameters)
            sb.Append(Ident(k)).Append('=').Append(Value(v)).Append(' ');
        sb.Append(body);
        return sb.ToString();
    }
}

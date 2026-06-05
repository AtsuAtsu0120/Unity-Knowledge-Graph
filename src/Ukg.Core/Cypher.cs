using System.Globalization;
using System.Text;

namespace Ukg.Core;

/// <summary>
/// Cypher リテラルを安全に組み立てるためのヘルパ。
/// FalkorDB のパラメータ前置構文は StackExchange.Redis 経由だと扱いにくいため、
/// 値をエスケープしてインライン化する方針を採る。
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
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>任意の値を Cypher リテラルへ変換する。</summary>
    public static string Value(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int or long or short or byte => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        string s => Str(s),
        _ => Str(value.ToString())
    };

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
        foreach (var c in ident)
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"Invalid identifier: {ident}");
        return ident;
    }
}

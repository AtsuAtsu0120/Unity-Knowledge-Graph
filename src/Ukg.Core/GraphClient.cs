using StackExchange.Redis;

namespace Ukg.Core;

/// <summary>
/// FalkorDB（Redis プロトコル）への接続と <c>GRAPH.QUERY</c> / <c>GRAPH.RO_QUERY</c> 実行を担う。
/// 結果は JSON にシリアライズしやすい素朴なオブジェクト（string/long/double/object[]）へ変換して返す。
/// </summary>
public sealed class GraphClient : IDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly string _graph;

    public string GraphName => _graph;

    private GraphClient(ConnectionMultiplexer redis, string graph)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _graph = graph;
    }

    /// <summary>
    /// 接続を確立する。connection は "host:port"、env で上書き可能。
    /// </summary>
    public static GraphClient Connect(string? connection = null, string? graph = null)
    {
        connection ??= Environment.GetEnvironmentVariable("UKG_REDIS") ?? "localhost:6379";
        graph ??= Environment.GetEnvironmentVariable("UKG_GRAPH") ?? "unity";
        var options = ConfigurationOptions.Parse(connection);
        options.AbortOnConnectFail = false;
        var redis = ConnectionMultiplexer.Connect(options);
        return new GraphClient(redis, graph);
    }

    /// <summary>グラフ全体（ノード・エッジ・索引）を破棄する。存在しなければ無視。</summary>
    public void DeleteGraph()
    {
        try { _db.Execute("GRAPH.DELETE", _graph); } catch { /* 未作成は無視 */ }
    }

    /// <summary>書き込みクエリを実行する。</summary>
    public QueryResult Query(string cypher, IReadOnlyDictionary<string, object?>? parameters = null)
        => Run("GRAPH.QUERY", cypher, parameters);

    /// <summary>読み取り専用クエリを実行する（書き込みは拒否される）。</summary>
    public QueryResult ReadOnlyQuery(string cypher, IReadOnlyDictionary<string, object?>? parameters = null)
        => Run("GRAPH.RO_QUERY", cypher, parameters);

    private QueryResult Run(string command, string cypher, IReadOnlyDictionary<string, object?>? parameters)
    {
        // 値はパラメータ前置（CYPHER k=v）で渡し、ボディは静的に保つ（ADR-002）。
        var full = Cypher.WithParams(cypher, parameters);
        // --compact 形式: 各スカラーが [typeCode, value] で返るため型を正しく判定できる
        // （verbose だと全桁数字の文字列 guid が数値に化けるなどの誤変換が起きる）。
        try
        {
            var reply = _db.Execute(command, _graph, full, "--compact");
            return ParseReply(reply);
        }
        catch (Exception ex) when (command == "GRAPH.RO_QUERY" && ex.Message.Contains("empty key"))
        {
            // 未作成グラフへの読み取りは「空」とみなす（鮮度判定などが初回でも落ちないように）。
            return new QueryResult(Array.Empty<string>(), new List<object?[]>(), Array.Empty<string>());
        }
    }

    // FalkorDB compact value type codes (ValueType enum)
    // UNKNOWN=0, NULL=1, STRING=2, INTEGER=3, BOOLEAN=4, DOUBLE=5, ARRAY=6, EDGE=7, NODE=8, PATH=9, MAP=10, POINT=11
    private const int TNull = 1, TString = 2, TInteger = 3, TBoolean = 4, TDouble = 5, TArray = 6;

    /// <summary>
    /// GRAPH.QUERY の応答は [header, rows, statistics] の3要素配列。
    /// compact では header は各カラムの [colType, name]、各値は [valueType, value]。
    /// </summary>
    private static QueryResult ParseReply(RedisResult reply)
    {
        if (reply.Resp2Type != ResultType.Array)
            return new QueryResult(Array.Empty<string>(), new List<object?[]>(), Array.Empty<string>());

        var top = (RedisResult[])reply!;
        // 書き込みのみ（RETURN 無し）の場合は [statistics] の1要素になる
        if (top.Length == 1)
            return new QueryResult(Array.Empty<string>(), new List<object?[]>(), ToStringArray(top[0]));

        var columns = ParseHeader(top[0]);
        var rows = new List<object?[]>();
        if (top[1].Resp2Type == ResultType.Array)
        {
            foreach (var row in (RedisResult[])top[1]!)
            {
                var cells = (RedisResult[])row!;
                var values = new object?[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                    values[i] = ParseValue(cells[i]);
                rows.Add(values);
            }
        }
        var stats = top.Length > 2 ? ToStringArray(top[2]) : Array.Empty<string>();
        return new QueryResult(columns, rows, stats);
    }

    private static string[] ParseHeader(RedisResult header)
    {
        if (header.Resp2Type != ResultType.Array) return Array.Empty<string>();
        var cols = (RedisResult[])header!;
        var names = new string[cols.Length];
        for (int i = 0; i < cols.Length; i++)
        {
            // compact モードでは [columnType, columnName]
            if (cols[i].Resp2Type == ResultType.Array)
            {
                var pair = (RedisResult[])cols[i]!;
                names[i] = pair.Length > 1 ? pair[1].ToString()! : pair[0].ToString()!;
            }
            else
            {
                names[i] = cols[i].ToString()!;
            }
        }
        return names;
    }

    private static string[] ToStringArray(RedisResult r)
    {
        if (r.Resp2Type != ResultType.Array) return new[] { r.ToString()! };
        return ((RedisResult[])r!).Select(x => x.ToString()!).ToArray();
    }

    /// <summary>
    /// compact 形式の [typeCode, value] セルを JSON 親和的なオブジェクトへ変換する。
    /// 型コードで判定するため、数字だけの文字列（guid 等）が数値に化けない。
    /// node/edge/map/path は ID 参照を含み解決が重いため、本 CLI では生構造のまま返す。
    /// </summary>
    private static object? ParseValue(RedisResult cell)
    {
        if (cell.Resp2Type != ResultType.Array)
            return cell.ToString(); // 念のためのフォールバック

        var pair = (RedisResult[])cell!;
        if (pair.Length < 2) return null;
        var type = (int)(long)pair[0];
        var v = pair[1];
        return type switch
        {
            TNull => null,
            TString => v.ToString(),
            TInteger => (long)v,
            TBoolean => v.ToString() == "true",
            TDouble => double.Parse(v.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
            TArray => v.Resp2Type == ResultType.Array
                ? ((RedisResult[])v!).Select(ParseValue).ToArray()
                : Array.Empty<object?>(),
            _ => RawToObject(v) // node/edge/map/path など: 生構造をそのまま
        };
    }

    private static object? RawToObject(RedisResult r) => r.Resp2Type switch
    {
        ResultType.None => null,
        ResultType.Integer => (long)r,
        ResultType.Array => ((RedisResult[])r!).Select(RawToObject).ToArray(),
        _ => r.ToString()
    };

    public void Dispose() => _redis.Dispose();
}

/// <summary>クエリ結果（カラム名・行・統計）。</summary>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<string> Statistics);

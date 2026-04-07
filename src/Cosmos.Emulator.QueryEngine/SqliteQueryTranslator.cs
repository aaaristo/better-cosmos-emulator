using System.Globalization;
using System.Text;
using Cosmos.Emulator.QueryEngine.Ast;

namespace Cosmos.Emulator.QueryEngine;

public class TranslatedQuery
{
    public required string Sql { get; set; }
    public required Dictionary<string, object> Parameters { get; set; }
    public bool IsValue { get; set; }
}

public class SqliteQueryTranslator
{
    private readonly string _containerName;
    private readonly string _fromAlias;
    private readonly HashSet<string> _knownColumns;
    private readonly Dictionary<string, object> _parameters = new();
    private readonly Dictionary<string, int> _joinAliases = new(StringComparer.OrdinalIgnoreCase);
    private int _paramCounter;

    // Cosmos system properties (_rid, _etag, _ts) map to SQLite columns without underscore prefix
    private static readonly Dictionary<string, string> SystemPropertyToColumn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_rid"] = "rid",
        ["_etag"] = "etag",
        ["_ts"] = "ts",
        ["_self"] = null!, // stays in body JSON
        ["_attachments"] = null!, // stays in body JSON
    };

    public SqliteQueryTranslator(string containerName, string fromAlias, HashSet<string> knownColumns)
    {
        _containerName = containerName;
        _fromAlias = fromAlias;
        _knownColumns = knownColumns;
    }

    public TranslatedQuery Translate(SelectStatement stmt, Dictionary<string, object>? userParams = null)
    {
        if (userParams is not null)
        {
            foreach (var (k, v) in userParams)
                _parameters[k] = v;
        }

        // Pre-register JOIN aliases so they're available during SELECT translation
        if (stmt.Joins is not null)
        {
            for (int i = 0; i < stmt.Joins.Count; i++)
                _joinAliases[stmt.Joins[i].Alias] = i;
        }

        var sb = new StringBuilder();

        // SELECT clause
        sb.Append("SELECT ");
        if (stmt.IsDistinct) sb.Append("DISTINCT ");

        if (stmt.IsValue)
        {
            // SELECT VALUE expr → return just that value
            if (stmt.SelectItems.Count != 1)
                throw new InvalidOperationException("SELECT VALUE must have exactly one expression");
            var valueExpr = TranslateSelectValueExpression(stmt.SelectItems[0].Expr);
            sb.Append(valueExpr);
        }
        else if (stmt.SelectItems.Count == 1 && stmt.SelectItems[0].Expr is SelectStar)
        {
            // SELECT * → return full body
            sb.Append("body");
        }
        else
        {
            // SELECT c.name, c.age → build a JSON object
            sb.Append("json_object(");
            for (int i = 0; i < stmt.SelectItems.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var item = stmt.SelectItems[i];
                var alias = item.Alias ?? GetExpressionAlias(item.Expr);
                var translated = TranslateExpression(item.Expr);
                // Wrap body references with json() for proper sub-object embedding
                if (translated == "body") translated = "json(body)";
                sb.Append($"'{alias}', {translated}");
            }
            sb.Append(')');
        }

        // FROM clause
        sb.Append($" FROM [{_containerName.Replace("]", "]]")}]");

        // JOIN clauses → CROSS JOIN json_each(...)
        if (stmt.Joins is not null)
        {
            for (int i = 0; i < stmt.Joins.Count; i++)
            {
                var join = stmt.Joins[i];
                _joinAliases[join.Alias] = i;
                var arrayExpr = TranslateExpression(join.InExpression);
                var tableAlias = $"__j{i}";
                sb.Append($" CROSS JOIN json_each({arrayExpr}) AS {tableAlias}");
            }
        }

        // WHERE clause (always exclude deleted + add user conditions)
        sb.Append(" WHERE is_deleted = 0");
        if (stmt.Where is not null)
        {
            sb.Append(" AND (");
            sb.Append(TranslateExpression(stmt.Where));
            sb.Append(')');
        }

        // GROUP BY
        if (stmt.GroupBy is not null)
        {
            sb.Append(" GROUP BY ");
            sb.Append(string.Join(", ", stmt.GroupBy.Select(TranslateExpression)));
        }

        // HAVING
        if (stmt.Having is not null)
        {
            sb.Append(" HAVING ");
            sb.Append(TranslateExpression(stmt.Having));
        }

        // ORDER BY
        if (stmt.OrderBy is not null)
        {
            sb.Append(" ORDER BY ");
            for (int i = 0; i < stmt.OrderBy.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TranslateExpression(stmt.OrderBy[i].Expr));
                sb.Append(stmt.OrderBy[i].Descending ? " DESC" : " ASC");
            }
        }

        // LIMIT / OFFSET
        if (stmt.Top.HasValue)
        {
            sb.Append($" LIMIT {stmt.Top.Value}");
        }
        else if (stmt.Limit is not null)
        {
            sb.Append($" LIMIT {ResolveIntExpression(stmt.Limit)}");
            if (stmt.Offset is not null)
                sb.Append($" OFFSET {ResolveIntExpression(stmt.Offset)}");
        }

        return new TranslatedQuery
        {
            Sql = sb.ToString(),
            Parameters = _parameters,
            IsValue = stmt.IsValue
        };
    }

    /// <summary>
    /// For SELECT VALUE, we need to wrap scalar results as JSON values.
    /// But if the expression is the full document (the FROM alias), return body directly.
    /// </summary>
    private string TranslateSelectValueExpression(Expression expr)
    {
        var translated = TranslateExpression(expr);
        // If the expression is the full document body, return it directly — no json_quote wrapping.
        // json_quote would turn the JSON object into an escaped string.
        if (translated == "body")
            return "body";
        return $"json_quote({translated})";
    }

    private string TranslateExpression(Expression expr) => expr switch
    {
        PropertyAccess pa => TranslatePropertyAccess(pa),
        LiteralExpression lit => TranslateLiteral(lit),
        ParameterExpression param => param.Name,
        BinaryExpression bin => TranslateBinary(bin),
        UnaryExpression un => TranslateUnary(un),
        FunctionCall fn => TranslateFunction(fn),
        InExpression ine => TranslateIn(ine),
        BetweenExpression be => TranslateBetween(be),
        CoalesceExpression ce => $"COALESCE({TranslateExpression(ce.Left)}, {TranslateExpression(ce.Right)})",
        ArrayIndexAccess aia => TranslateArrayIndex(aia),
        ObjectExpression obj => TranslateObject(obj),
        ArrayExpression arr => TranslateArray(arr),
        SelectStar => "body",
        _ => throw new NotSupportedException($"Expression type {expr.GetType().Name} not supported")
    };

    private string TranslatePropertyAccess(PropertyAccess pa)
    {
        // Check if the first path segment is a JOIN alias (e.g., "t" in "t.name" from JOIN t IN c.tags)
        var firstSegment = pa.Path.Count > 0 ? pa.Path[0] : pa.Source;
        if (firstSegment is not null && _joinAliases.TryGetValue(firstSegment, out var joinIdx))
        {
            var tableAlias = $"__j{joinIdx}";
            var remaining = pa.Path.Count > 0 && pa.Path[0].Equals(firstSegment, StringComparison.OrdinalIgnoreCase)
                ? pa.Path.Skip(1).ToList()
                : pa.Path;

            if (remaining.Count == 0)
            {
                // Just the join alias → the element value (could be scalar or object)
                return $"{tableAlias}.value";
            }

            // Nested property on joined element: t.name → json_extract(__j0.value, '$.name')
            var joinJsonPath = "$." + string.Join(".", remaining.Select(EscapeJsonPath));
            return $"json_extract({tableAlias}.value, '{joinJsonPath}')";
        }

        // Strip the FROM alias if present (e.g., "c" in "c.name" → just "name")
        var path = pa.Path;
        if (pa.Source is not null && pa.Source.Equals(_fromAlias, StringComparison.OrdinalIgnoreCase))
        {
            // Already stripped, path is correct
        }
        else if (pa.Path.Count > 0 && pa.Path[0].Equals(_fromAlias, StringComparison.OrdinalIgnoreCase))
        {
            path = pa.Path.Skip(1).ToList();
        }

        if (path.Count == 0)
        {
            // Just the alias itself → return body
            return "body";
        }

        // Map Cosmos system properties to SQLite column names: _rid → rid, _etag → etag, _ts → ts
        if (path.Count == 1 && SystemPropertyToColumn.TryGetValue(path[0], out var sqliteCol))
        {
            if (sqliteCol is not null)
                return $"[{sqliteCol}]";
            // null means stays in body JSON — fall through to json_extract
        }

        // Flatten dotted path with __ separator: address.city → address__city
        var columnName = string.Join("__", path);

        // If column exists, use it directly (covers both top-level and nested)
        if (_knownColumns.Contains(columnName))
            return $"[{columnName.Replace("]", "]]")}]";

        // Fallback to json_extract (for arrays or columns not yet seen)
        var jsonPath = "$." + string.Join(".", path.Select(EscapeJsonPath));
        return $"json_extract(body, '{jsonPath}')";
    }

    private string TranslateArrayIndex(ArrayIndexAccess aia)
    {
        // c["Deleted"] → treat as property access c.Deleted (bracket notation for property access)
        // EF Core Cosmos provider uses this syntax: c["PropertyName"]
        // Double-quoted identifiers are tokenized as Identifier → parsed as PropertyAccess
        if (aia.Array is PropertyAccess pa)
        {
            string? propName = null;
            if (aia.Index is LiteralExpression { Type: LiteralType.String } strIdx)
                propName = strIdx.Value?.ToString();
            else if (aia.Index is PropertyAccess indexPa && indexPa.Source is null && indexPa.Path.Count == 1)
                propName = indexPa.Path[0]; // double-quoted identifier like c["Deleted"]

            if (propName is not null)
            {
                var syntheticPa = new PropertyAccess(pa.Source, [.. pa.Path, propName]);
                return TranslatePropertyAccess(syntheticPa);
            }
        }

        // c.tags[0] → json_extract(body, '$.tags[0]')
        if (aia.Array is PropertyAccess pa2 && aia.Index is LiteralExpression { Type: LiteralType.Number } idx)
        {
            var path = GetPropertyPath(pa2);
            return $"json_extract(body, '$.{path}[{idx.Value}]')";
        }

        // Fallback: use json_extract on the translated array expression
        var arrayExpr = TranslateExpression(aia.Array);
        var indexExpr = TranslateExpression(aia.Index);
        return $"json_extract({arrayExpr}, '$[' || {indexExpr} || ']')";
    }

    private static string TranslateLiteral(LiteralExpression lit) => lit.Type switch
    {
        LiteralType.String => $"'{EscapeSqlString(lit.Value?.ToString() ?? "")}'",
        LiteralType.Number when lit.Value is double d => d.ToString(CultureInfo.InvariantCulture),
        LiteralType.Number => lit.Value?.ToString() ?? "0",
        LiteralType.Boolean => (bool)lit.Value! ? "1" : "0",
        LiteralType.Null => "NULL",
        LiteralType.Undefined => "NULL",
        _ => "NULL"
    };

    private string TranslateBinary(BinaryExpression bin)
    {
        var left = TranslateExpression(bin.Left);
        var right = TranslateExpression(bin.Right);
        var op = bin.Operator switch
        {
            "||" => "||", // string concatenation in both SQL dialects
            _ => bin.Operator
        };
        return $"({left} {op} {right})";
    }

    private string TranslateUnary(UnaryExpression un)
    {
        var operand = TranslateExpression(un.Operand);
        return un.Operator switch
        {
            "NOT" => $"(NOT {operand})",
            "-" => $"(-{operand})",
            "IS NULL" => $"({operand} IS NULL)",
            "IS NOT NULL" => $"({operand} IS NOT NULL)",
            _ => throw new NotSupportedException($"Unary operator '{un.Operator}' not supported")
        };
    }

    private string TranslateFunction(FunctionCall fn)
    {
        var args = fn.Arguments.Select(TranslateExpression).ToList();

        return fn.Name switch
        {
            // String functions
            "CONTAINS" when args.Count >= 2 => $"({args[0]} LIKE '%' || {args[1]} || '%')",
            "STARTSWITH" => $"({args[0]} LIKE {args[1]} || '%')",
            "ENDSWITH" => $"({args[0]} LIKE '%' || {args[1]})",
            "UPPER" => $"UPPER({args[0]})",
            "LOWER" => $"LOWER({args[0]})",
            "LENGTH" => $"LENGTH({args[0]})",
            "LTRIM" or "TRIM" => $"TRIM({args[0]})",
            "RTRIM" => $"RTRIM({args[0]})",
            "LEFT" => $"SUBSTR({args[0]}, 1, {args[1]})",
            "RIGHT" => $"SUBSTR({args[0]}, -CAST({args[1]} AS INTEGER))",
            "SUBSTRING" when args.Count == 3 => $"SUBSTR({args[0]}, {args[1]} + 1, {args[2]})", // Cosmos is 0-based
            "SUBSTRING" when args.Count == 2 => $"SUBSTR({args[0]}, {args[1]} + 1)",
            "REPLACE" => $"REPLACE({args[0]}, {args[1]}, {args[2]})",
            "CONCAT" => string.Join(" || ", args),
            "TOSTRING" => $"CAST({args[0]} AS TEXT)",
            "INDEX_OF" => $"(INSTR({args[0]}, {args[1]}) - 1)", // Cosmos returns -1 if not found, 0-based

            // Math functions
            "ABS" => $"ABS({args[0]})",
            "CEILING" => $"CAST(CASE WHEN {args[0]} = CAST({args[0]} AS INTEGER) THEN {args[0]} ELSE CAST({args[0]} AS INTEGER) + 1 END AS INTEGER)",
            "FLOOR" => $"CAST({args[0]} AS INTEGER)",
            "ROUND" => args.Count == 2 ? $"ROUND({args[0]}, {args[1]})" : $"ROUND({args[0]})",
            "POWER" => $"POWER({args[0]}, {args[1]})",
            "SQRT" => $"SQRT({args[0]})",
            "SQUARE" => $"({args[0]} * {args[0]})",

            // Type-checking functions
            "IS_DEFINED" => $"({args[0]} IS NOT NULL)",
            "IS_NULL" => TranslateIsNull(fn.Arguments[0]),
            "IS_NUMBER" => $"(typeof({args[0]}) IN ('integer', 'real'))",
            "IS_STRING" => $"(typeof({args[0]}) = 'text')",
            "IS_BOOL" => $"(typeof({args[0]}) = 'integer' AND {args[0]} IN (0, 1))",
            "IS_ARRAY" => $"(json_type(json_extract(body, '$.{GetPropertyJsonPath(fn.Arguments[0])}')) = 'array')",
            "IS_OBJECT" => $"(json_type(json_extract(body, '$.{GetPropertyJsonPath(fn.Arguments[0])}')) = 'object')",

            // Array functions
            "ARRAY_CONTAINS" when args.Count >= 2 => TranslateArrayContains(fn.Arguments),
            "ARRAY_LENGTH" => $"json_array_length({args[0]})",

            // Aggregate functions
            "COUNT" when args.Count == 1 => $"COUNT({args[0]})",
            "COUNT" when args.Count == 0 => "COUNT(*)",
            "SUM" => $"SUM({args[0]})",
            "AVG" => $"AVG({args[0]})",
            "MIN" => $"MIN({args[0]})",
            "MAX" => $"MAX({args[0]})",

            _ => throw new NotSupportedException($"Function '{fn.Name}' is not supported")
        };
    }

    private string TranslateIsNull(Expression arg)
    {
        // For IS_NULL we need to distinguish between "column is NULL" (undefined)
        // and "JSON value is null". Use json_extract on body to check json null.
        var jsonPath = GetPropertyJsonPath(arg);
        return $"(json_type(json_extract(body, '$.{jsonPath}')) = 'null')";
    }

    private string TranslateArrayContains(List<Expression> arguments)
    {
        var arrayExpr = arguments[0];
        var valueExpr = TranslateExpression(arguments[1]);

        // When the array is a parameter (e.g., ARRAY_CONTAINS(@pathsToLookup, c["Path"])),
        // SQLite's json_each() doesn't work with bound parameters. Instead, resolve the
        // parameter value and expand it to an IN clause with individual parameters.
        if (arrayExpr is ParameterExpression paramExpr && _parameters.TryGetValue(paramExpr.Name, out var paramValue))
        {
            var jsonStr = paramValue?.ToString() ?? "[]";
            try
            {
                var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonStr);
                var inParams = new List<string>();
                int idx = 0;
                foreach (var elem in jsonDoc.RootElement.EnumerateArray())
                {
                    var pName = $"@__ac_{_paramCounter++}";
                    _parameters[pName] = elem.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => elem.GetString()!,
                        System.Text.Json.JsonValueKind.Number when elem.TryGetInt64(out var l) => l,
                        System.Text.Json.JsonValueKind.Number => elem.GetDouble(),
                        _ => elem.GetRawText()
                    };
                    inParams.Add(pName);
                    idx++;
                }
                // Remove the original array parameter — it's no longer needed
                _parameters.Remove(paramExpr.Name);
                if (inParams.Count == 0)
                    return "0"; // empty array → always false
                return $"({valueExpr} IN ({string.Join(", ", inParams)}))";
            }
            catch
            {
                // Fall through to json_each approach if parsing fails
            }
        }

        // For document-level arrays (e.g., ARRAY_CONTAINS(c.tags, "value")),
        // use json_each to search within the array
        if (arrayExpr is PropertyAccess pa)
        {
            var path = GetPropertyPath(pa);
            var jsonPath = "$." + path;

            // Check if it's a top-level column
            var parts = path.Split('.');
            string arraySource;
            if (parts.Length == 1 && _knownColumns.Contains(parts[0]))
                arraySource = $"[{parts[0].Replace("]", "]]")}]";
            else
                arraySource = $"json_extract(body, '{jsonPath}')";

            return $"EXISTS (SELECT 1 FROM json_each({arraySource}) WHERE json_each.value = {valueExpr})";
        }

        var translated = TranslateExpression(arrayExpr);
        return $"EXISTS (SELECT 1 FROM json_each({translated}) WHERE json_each.value = {valueExpr})";
    }

    /// <summary>
    /// {"key": expr, ...} → json_object('key', expr, ...)
    /// When a value resolves to 'body' (full document column), wraps in json()
    /// so SQLite embeds it as a JSON sub-object, not a string.
    /// </summary>
    private string TranslateObject(ObjectExpression obj)
    {
        var parts = obj.Properties.Select(p =>
        {
            var val = TranslateExpression(p.Value);
            if (val == "body") val = "json(body)";
            return $"'{EscapeSqlString(p.Key)}', {val}";
        });
        return $"json_object({string.Join(", ", parts)})";
    }

    /// <summary>
    /// [expr, ...] → json_array(expr, ...)
    /// </summary>
    private string TranslateArray(ArrayExpression arr)
    {
        var parts = arr.Elements.Select(e =>
        {
            var val = TranslateExpression(e);
            return val == "body" ? "json(body)" : val;
        });
        return $"json_array({string.Join(", ", parts)})";
    }

    private string TranslateIn(InExpression ine)
    {
        var value = TranslateExpression(ine.Value);
        var items = string.Join(", ", ine.List.Select(TranslateExpression));
        var not = ine.Negated ? "NOT " : "";
        return $"({value} {not}IN ({items}))";
    }

    private string TranslateBetween(BetweenExpression be)
    {
        var value = TranslateExpression(be.Value);
        var low = TranslateExpression(be.Low);
        var high = TranslateExpression(be.High);
        var not = be.Negated ? "NOT " : "";
        return $"({value} {not}BETWEEN {low} AND {high})";
    }

    // --- Helpers ---

    private string GetPropertyPath(PropertyAccess pa)
    {
        var path = pa.Path;
        if (path.Count > 0 && path[0].Equals(_fromAlias, StringComparison.OrdinalIgnoreCase))
            path = path.Skip(1).ToList();
        return string.Join(".", path);
    }

    private string GetPropertyJsonPath(Expression expr)
    {
        if (expr is PropertyAccess pa)
            return GetPropertyPath(pa);
        return TranslateExpression(expr);
    }

    private static string GetExpressionAlias(Expression expr) => expr switch
    {
        PropertyAccess pa => pa.Path.Last(),
        FunctionCall fn => fn.Name.ToLowerInvariant(),
        _ => "$1"
    };

    /// <summary>
    /// Resolves an expression to an integer for OFFSET/LIMIT.
    /// Handles literal numbers and parameter references.
    /// </summary>
    private string ResolveIntExpression(Expression expr)
    {
        if (expr is LiteralExpression lit)
            return lit.Value?.ToString() ?? "0";
        if (expr is ParameterExpression param)
        {
            // Resolve parameter to its value and inline it
            if (_parameters.TryGetValue(param.Name, out var val))
                return val.ToString() ?? "0";
            return param.Name; // pass through as SQLite parameter
        }
        return TranslateExpression(expr);
    }

    private static string EscapeJsonPath(string segment) => segment.Replace("'", "\\'");
    private static string EscapeSqlString(string value) => value.Replace("'", "''");
}

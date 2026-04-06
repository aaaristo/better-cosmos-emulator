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
    private int _paramCounter;

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
                sb.Append($"'{alias}', {translated}");
            }
            sb.Append(')');
        }

        // FROM clause
        sb.Append($" FROM [{_containerName.Replace("]", "]]")}]");

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
        else if (stmt.Limit.HasValue)
        {
            sb.Append($" LIMIT {stmt.Limit.Value}");
            if (stmt.Offset.HasValue)
                sb.Append($" OFFSET {stmt.Offset.Value}");
        }

        return new TranslatedQuery
        {
            Sql = sb.ToString(),
            Parameters = _parameters,
            IsValue = stmt.IsValue
        };
    }

    /// <summary>
    /// For SELECT VALUE, we need to wrap the result as a JSON value so it can be returned properly.
    /// </summary>
    private string TranslateSelectValueExpression(Expression expr)
    {
        var translated = TranslateExpression(expr);
        // Wrap in json() to ensure it's returned as valid JSON
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
        SelectStar => "body",
        _ => throw new NotSupportedException($"Expression type {expr.GetType().Name} not supported")
    };

    private string TranslatePropertyAccess(PropertyAccess pa)
    {
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
        // c.tags[0] → json_extract(body, '$.tags[0]')
        if (aia.Array is PropertyAccess pa && aia.Index is LiteralExpression { Type: LiteralType.Number } idx)
        {
            var path = GetPropertyPath(pa);
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

        // We need json_each to search within the array
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

    private static string EscapeJsonPath(string segment) => segment.Replace("'", "\\'");
    private static string EscapeSqlString(string value) => value.Replace("'", "''");
}

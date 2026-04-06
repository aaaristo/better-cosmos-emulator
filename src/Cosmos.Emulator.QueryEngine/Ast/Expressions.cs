namespace Cosmos.Emulator.QueryEngine.Ast;

public abstract record Expression;

public record SelectStatement(
    bool IsDistinct,
    int? Top,
    bool IsValue,
    List<SelectItem> SelectItems,
    string FromAlias,
    List<JoinClause>? Joins,
    Expression? Where,
    List<OrderByItem>? OrderBy,
    int? Offset,
    int? Limit,
    List<Expression>? GroupBy,
    Expression? Having) : Expression;

public record JoinClause(string Alias, Expression InExpression);

public record SelectItem(Expression Expr, string? Alias);

public record SelectStar() : Expression;

public record PropertyAccess(string? Source, List<string> Path) : Expression
{
    public string FullPath => string.Join(".", Source is not null ? [Source, .. Path] : Path);
    public string JsonPath => "$." + string.Join(".", Path);
}

public record ArrayIndexAccess(Expression Array, Expression Index) : Expression;

public record BinaryExpression(Expression Left, string Operator, Expression Right) : Expression;

public record UnaryExpression(string Operator, Expression Operand) : Expression;

public record LiteralExpression(object? Value, LiteralType Type) : Expression;

public enum LiteralType { String, Number, Boolean, Null, Undefined }

public record FunctionCall(string Name, List<Expression> Arguments) : Expression;

public record InExpression(Expression Value, List<Expression> List, bool Negated) : Expression;

public record BetweenExpression(Expression Value, Expression Low, Expression High, bool Negated) : Expression;

public record ParameterExpression(string Name) : Expression;

public record ArrayExpression(List<Expression> Elements) : Expression;

public record ObjectExpression(List<(string Key, Expression Value)> Properties) : Expression;

public record SubqueryExpression(SelectStatement Subquery) : Expression;

public record ExistsExpression(SelectStatement Subquery) : Expression;

public record CoalesceExpression(Expression Left, Expression Right) : Expression;

public record OrderByItem(Expression Expr, bool Descending);

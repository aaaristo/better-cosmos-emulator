using Cosmos.Emulator.QueryEngine.Ast;

namespace Cosmos.Emulator.QueryEngine;

public class CosmosSqlParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public CosmosSqlParser(List<Token> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    private Token Current => _tokens[_pos];
    private Token Peek(int offset = 1) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

    private Token Expect(TokenType type)
    {
        if (Current.Type != type)
            throw new FormatException($"Expected {type} but got {Current.Type} ('{Current.Value}') at position {Current.Position}");
        return Advance();
    }

    private Token Advance()
    {
        var token = Current;
        _pos++;
        return token;
    }

    private bool Match(TokenType type)
    {
        if (Current.Type != type) return false;
        _pos++;
        return true;
    }

    private bool Check(TokenType type) => Current.Type == type;

    public SelectStatement Parse()
    {
        return ParseSelect();
    }

    private SelectStatement ParseSelect()
    {
        Expect(TokenType.Select);

        bool isDistinct = Match(TokenType.Distinct);

        int? top = null;
        if (Match(TokenType.Top))
        {
            top = int.Parse(Expect(TokenType.NumberLiteral).Value);
        }

        bool isValue = Match(TokenType.Value);

        var selectItems = ParseSelectList();

        Expect(TokenType.From);
        var fromAlias = Expect(TokenType.Identifier).Value;
        Expression? fromSource = null;

        // Handle "FROM root c", "FROM root AS c", or just "FROM c"
        // In Cosmos SQL, "root" is a keyword meaning the container.
        if (fromAlias.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            if (Match(TokenType.As))
                fromAlias = Expect(TokenType.Identifier).Value;
            else if (Current.Type == TokenType.Identifier)
                fromAlias = Expect(TokenType.Identifier).Value;
            // else: "FROM root" alone — "root" IS the alias
        }
        else if (Match(TokenType.In))
        {
            // "FROM x IN <collection>" — array iteration over an embedded collection
            // or a parameter array. Used inside subqueries, e.g.
            // EXISTS (SELECT VALUE 1 FROM o IN c.items WHERE o = c.x).
            fromSource = ParsePostfix();
        }
        else if (Match(TokenType.As))
        {
            // "FROM someAlias AS anotherAlias" — unusual but handle it
            fromAlias = Expect(TokenType.Identifier).Value;
        }

        // Optional JOIN clauses: JOIN t IN c.tags
        List<JoinClause>? joins = null;
        while (Match(TokenType.Join))
        {
            joins ??= new List<JoinClause>();
            var joinAlias = Expect(TokenType.Identifier).Value;
            Expect(TokenType.In);
            var inExpr = ParsePostfix();
            joins.Add(new JoinClause(joinAlias, inExpr));
        }

        Expression? where = null;
        if (Match(TokenType.Where))
        {
            where = ParseExpression();
        }

        List<Expression>? groupBy = null;
        if (Match(TokenType.Group))
        {
            Expect(TokenType.By);
            groupBy = new List<Expression>();
            groupBy.Add(ParseExpression());
            while (Match(TokenType.Comma))
            {
                groupBy.Add(ParseExpression());
            }
        }

        Expression? having = null;
        if (Match(TokenType.Having))
        {
            having = ParseExpression();
        }

        List<OrderByItem>? orderBy = null;
        if (Match(TokenType.Order))
        {
            Expect(TokenType.By);
            orderBy = ParseOrderByList();
        }

        Expression? offset = null;
        Expression? limit = null;
        if (Match(TokenType.Offset))
        {
            offset = ParsePrimary();
            Expect(TokenType.Limit);
            limit = ParsePrimary();
        }

        return new SelectStatement(isDistinct, top, isValue, selectItems, fromAlias, joins, where, orderBy, offset, limit, groupBy, having, fromSource);
    }

    private List<SelectItem> ParseSelectList()
    {
        var items = new List<SelectItem>();

        if (Check(TokenType.Star))
        {
            Advance();
            items.Add(new SelectItem(new SelectStar(), null));
            return items;
        }

        items.Add(ParseSelectItem());
        while (Match(TokenType.Comma))
        {
            items.Add(ParseSelectItem());
        }
        return items;
    }

    private SelectItem ParseSelectItem()
    {
        var expr = ParseExpression();
        string? alias = null;

        if (Match(TokenType.As))
        {
            alias = Expect(TokenType.Identifier).Value;
        }

        return new SelectItem(expr, alias);
    }

    private List<OrderByItem> ParseOrderByList()
    {
        var items = new List<OrderByItem>();
        items.Add(ParseOrderByItem());
        while (Match(TokenType.Comma))
        {
            items.Add(ParseOrderByItem());
        }
        return items;
    }

    private OrderByItem ParseOrderByItem()
    {
        var expr = ParseExpression();
        var desc = false;
        if (Match(TokenType.Desc))
            desc = true;
        else
            Match(TokenType.Asc); // optional, ascending is default
        return new OrderByItem(expr, desc);
    }

    // --- Expression parsing (precedence climbing) ---

    private Expression ParseExpression() => ParseCoalesce();

    private Expression ParseCoalesce()
    {
        var left = ParseOr();
        if (Match(TokenType.QuestionQuestion))
        {
            var right = ParseCoalesce();
            return new CoalesceExpression(left, right);
        }
        return left;
    }

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenType.Or))
        {
            var right = ParseAnd();
            left = new BinaryExpression(left, "OR", right);
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseNot();
        while (Match(TokenType.And))
        {
            var right = ParseNot();
            left = new BinaryExpression(left, "AND", right);
        }
        return left;
    }

    private Expression ParseNot()
    {
        if (Match(TokenType.Not))
        {
            var operand = ParseNot();
            return new UnaryExpression("NOT", operand);
        }
        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        var left = ParseAdditive();

        // IN expression
        if (Current.Type == TokenType.Not && Peek().Type == TokenType.In)
        {
            Advance(); Advance();
            Expect(TokenType.LeftParen);
            var list = ParseExpressionList();
            Expect(TokenType.RightParen);
            return new InExpression(left, list, true);
        }
        if (Match(TokenType.In))
        {
            Expect(TokenType.LeftParen);
            var list = ParseExpressionList();
            Expect(TokenType.RightParen);
            return new InExpression(left, list, false);
        }

        // BETWEEN expression
        if (Current.Type == TokenType.Not && Peek().Type == TokenType.Between)
        {
            Advance(); Advance();
            var low = ParseAdditive();
            Expect(TokenType.And);
            var high = ParseAdditive();
            return new BetweenExpression(left, low, high, true);
        }
        if (Match(TokenType.Between))
        {
            var low = ParseAdditive();
            Expect(TokenType.And);
            var high = ParseAdditive();
            return new BetweenExpression(left, low, high, false);
        }

        // IS NULL / IS NOT NULL / IS DEFINED etc. handled via function calls in Cosmos
        // but some queries use: c.prop IS NULL
        if (Match(TokenType.Is))
        {
            bool negated = Match(TokenType.Not);
            if (Match(TokenType.Null))
            {
                return negated
                    ? new UnaryExpression("IS NOT NULL", left)
                    : new UnaryExpression("IS NULL", left);
            }
            throw new FormatException($"Expected NULL after IS at position {Current.Position}");
        }

        // Standard comparison operators
        var op = Current.Type switch
        {
            TokenType.Equal => "=",
            TokenType.NotEqual => "!=",
            TokenType.LessThan => "<",
            TokenType.GreaterThan => ">",
            TokenType.LessThanOrEqual => "<=",
            TokenType.GreaterThanOrEqual => ">=",
            TokenType.Like => "LIKE",
            _ => null
        };

        if (op is not null)
        {
            Advance();
            var right = ParseAdditive();
            return new BinaryExpression(left, op, right);
        }

        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Type is TokenType.Plus or TokenType.Minus or TokenType.Pipe)
        {
            var op = Advance().Value;
            var right = ParseMultiplicative();
            left = new BinaryExpression(left, op, right);
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Type is TokenType.Star or TokenType.Slash or TokenType.Percent)
        {
            var op = Advance().Value;
            var right = ParseUnary();
            left = new BinaryExpression(left, op, right);
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (Match(TokenType.Minus))
        {
            var operand = ParseUnary();
            return new UnaryExpression("-", operand);
        }
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Match(TokenType.Dot))
            {
                var prop = Expect(TokenType.Identifier).Value;
                if (expr is PropertyAccess pa)
                {
                    expr = new PropertyAccess(pa.Source, [.. pa.Path, prop]);
                }
                else
                {
                    // Shouldn't happen in well-formed Cosmos SQL, but handle gracefully
                    expr = new PropertyAccess(null, [prop]);
                }
            }
            else if (Match(TokenType.LeftBracket))
            {
                var index = ParseExpression();
                Expect(TokenType.RightBracket);
                expr = new ArrayIndexAccess(expr, index);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expression ParsePrimary()
    {
        switch (Current.Type)
        {
            case TokenType.StringLiteral:
                return new LiteralExpression(Advance().Value, LiteralType.String);

            case TokenType.NumberLiteral:
                var numVal = Advance().Value;
                if (numVal.Contains('.') || numVal.Contains('e') || numVal.Contains('E'))
                    return new LiteralExpression(double.Parse(numVal, System.Globalization.CultureInfo.InvariantCulture), LiteralType.Number);
                return new LiteralExpression(long.Parse(numVal), LiteralType.Number);

            case TokenType.True:
                Advance();
                return new LiteralExpression(true, LiteralType.Boolean);

            case TokenType.False:
                Advance();
                return new LiteralExpression(false, LiteralType.Boolean);

            case TokenType.Null:
                Advance();
                return new LiteralExpression(null, LiteralType.Null);

            case TokenType.Undefined:
                Advance();
                return new LiteralExpression(null, LiteralType.Undefined);

            case TokenType.Parameter:
                return new ParameterExpression(Advance().Value);

            case TokenType.LeftParen:
                Advance();
                var inner = ParseExpression();
                Expect(TokenType.RightParen);
                return inner;

            case TokenType.LeftBrace:
                return ParseObjectLiteral();

            case TokenType.LeftBracket:
                return ParseArrayLiteral();

            case TokenType.Exists:
                Advance();
                Expect(TokenType.LeftParen);
                Expect(TokenType.Select); _pos--; // put SELECT back
                var subquery = ParseSelect();
                Expect(TokenType.RightParen);
                return new ExistsExpression(subquery);

            case TokenType.Identifier:
                var name = Advance().Value;

                // Check if it's a function call
                if (Check(TokenType.LeftParen))
                {
                    Advance(); // consume (
                    var args = new List<Expression>();
                    if (!Check(TokenType.RightParen))
                    {
                        args.Add(ParseExpression());
                        while (Match(TokenType.Comma))
                        {
                            args.Add(ParseExpression());
                        }
                    }
                    Expect(TokenType.RightParen);
                    return new FunctionCall(name.ToUpperInvariant(), args);
                }

                // It's a property access (start of dotted path)
                // If next is dot, the postfix handler will extend it
                return new PropertyAccess(null, [name]);

            default:
                throw new FormatException($"Unexpected token {Current.Type} ('{Current.Value}') at position {Current.Position}");
        }
    }

    private Expression ParseObjectLiteral()
    {
        Expect(TokenType.LeftBrace);
        var properties = new List<(string Key, Expression Value)>();
        if (!Check(TokenType.RightBrace))
        {
            properties.Add(ParseObjectProperty());
            while (Match(TokenType.Comma))
                properties.Add(ParseObjectProperty());
        }
        Expect(TokenType.RightBrace);
        return new ObjectExpression(properties);
    }

    private (string Key, Expression Value) ParseObjectProperty()
    {
        string key;
        if (Check(TokenType.StringLiteral))
            key = Advance().Value;
        else if (Check(TokenType.Identifier))
            key = Advance().Value;
        else
            throw new FormatException($"Expected property name but got {Current.Type} at position {Current.Position}");

        Expect(TokenType.Colon);
        var value = ParseExpression();
        return (key, value);
    }

    private Expression ParseArrayLiteral()
    {
        Expect(TokenType.LeftBracket);
        var elements = new List<Expression>();
        if (!Check(TokenType.RightBracket))
        {
            elements.Add(ParseExpression());
            while (Match(TokenType.Comma))
            {
                elements.Add(ParseExpression());
            }
        }
        Expect(TokenType.RightBracket);
        return new ArrayExpression(elements);
    }

    private List<Expression> ParseExpressionList()
    {
        var list = new List<Expression>();
        list.Add(ParseExpression());
        while (Match(TokenType.Comma))
        {
            list.Add(ParseExpression());
        }
        return list;
    }
}

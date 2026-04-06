namespace Cosmos.Emulator.QueryEngine;

public enum TokenType
{
    // Literals
    StringLiteral, NumberLiteral, True, False, Null, Undefined,

    // Identifiers & parameters
    Identifier, Parameter,

    // Operators
    Equal, NotEqual, LessThan, GreaterThan, LessThanOrEqual, GreaterThanOrEqual,
    Plus, Minus, Star, Slash, Percent, Pipe, QuestionQuestion,

    // Punctuation
    Dot, Comma, Colon, LeftParen, RightParen, LeftBracket, RightBracket, LeftBrace, RightBrace,

    // Keywords
    Select, From, Where, And, Or, Not, In, Between, Like,
    Order, By, Asc, Desc, Top, Distinct, Value, Join,
    Offset, Limit, Group, Having, As, Exists,
    Is,

    // End
    Eof
}

public record Token(TokenType Type, string Value, int Position);

public class CosmosSqlLexer
{
    private readonly string _input;
    private int _pos;

    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = TokenType.Select,
        ["FROM"] = TokenType.From,
        ["WHERE"] = TokenType.Where,
        ["AND"] = TokenType.And,
        ["OR"] = TokenType.Or,
        ["NOT"] = TokenType.Not,
        ["IN"] = TokenType.In,
        ["BETWEEN"] = TokenType.Between,
        ["LIKE"] = TokenType.Like,
        ["ORDER"] = TokenType.Order,
        ["BY"] = TokenType.By,
        ["ASC"] = TokenType.Asc,
        ["DESC"] = TokenType.Desc,
        ["TOP"] = TokenType.Top,
        ["DISTINCT"] = TokenType.Distinct,
        ["VALUE"] = TokenType.Value,
        ["JOIN"] = TokenType.Join,
        ["OFFSET"] = TokenType.Offset,
        ["LIMIT"] = TokenType.Limit,
        ["GROUP"] = TokenType.Group,
        ["HAVING"] = TokenType.Having,
        ["AS"] = TokenType.As,
        ["EXISTS"] = TokenType.Exists,
        ["IS"] = TokenType.Is,
        ["TRUE"] = TokenType.True,
        ["FALSE"] = TokenType.False,
        ["NULL"] = TokenType.Null,
        ["UNDEFINED"] = TokenType.Undefined,
    };

    public CosmosSqlLexer(string input)
    {
        _input = input;
        _pos = 0;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_pos < _input.Length)
        {
            SkipWhitespace();
            if (_pos >= _input.Length)
                break;

            var c = _input[_pos];

            if (c == '-' && _pos + 1 < _input.Length && c == '-' && _input[_pos + 1] == '-')
            {
                // Line comment
                while (_pos < _input.Length && _input[_pos] != '\n')
                    _pos++;
                continue;
            }

            var token = c switch
            {
                '\'' => ReadString(),
                '"' => ReadQuotedIdentifier(),
                '@' => ReadParameter(),
                '.' => MakeToken(TokenType.Dot, "."),
                ',' => MakeToken(TokenType.Comma, ","),
                '(' => MakeToken(TokenType.LeftParen, "("),
                ')' => MakeToken(TokenType.RightParen, ")"),
                '[' => MakeToken(TokenType.LeftBracket, "["),
                ']' => MakeToken(TokenType.RightBracket, "]"),
                '{' => MakeToken(TokenType.LeftBrace, "{"),
                '}' => MakeToken(TokenType.RightBrace, "}"),
                ':' => MakeToken(TokenType.Colon, ":"),
                '+' => MakeToken(TokenType.Plus, "+"),
                '-' => ReadMinusOrNumber(),
                '*' => MakeToken(TokenType.Star, "*"),
                '/' => MakeToken(TokenType.Slash, "/"),
                '%' => MakeToken(TokenType.Percent, "%"),
                '=' => MakeToken(TokenType.Equal, "="),
                '!' when Peek() == '=' => MakeToken2(TokenType.NotEqual, "!="),
                '<' when Peek() == '>' => MakeToken2(TokenType.NotEqual, "<>"),
                '<' when Peek() == '=' => MakeToken2(TokenType.LessThanOrEqual, "<="),
                '<' => MakeToken(TokenType.LessThan, "<"),
                '>' when Peek() == '=' => MakeToken2(TokenType.GreaterThanOrEqual, ">="),
                '>' => MakeToken(TokenType.GreaterThan, ">"),
                '|' when Peek() == '|' => MakeToken2(TokenType.Pipe, "||"),
                '?' when Peek() == '?' => MakeToken2(TokenType.QuestionQuestion, "??"),
                _ when char.IsDigit(c) => ReadNumber(),
                _ when char.IsLetter(c) || c == '_' => ReadIdentifierOrKeyword(),
                _ => throw new FormatException($"Unexpected character '{c}' at position {_pos}")
            };

            tokens.Add(token);
        }

        tokens.Add(new Token(TokenType.Eof, "", _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
            _pos++;
    }

    private char Peek() => _pos + 1 < _input.Length ? _input[_pos + 1] : '\0';

    private Token MakeToken(TokenType type, string value)
    {
        var pos = _pos;
        _pos++;
        return new Token(type, value, pos);
    }

    private Token MakeToken2(TokenType type, string value)
    {
        var pos = _pos;
        _pos += 2;
        return new Token(type, value, pos);
    }

    private Token ReadString()
    {
        var start = _pos;
        _pos++; // skip opening quote
        var sb = new System.Text.StringBuilder();

        while (_pos < _input.Length)
        {
            if (_input[_pos] == '\'' && _pos + 1 < _input.Length && _input[_pos + 1] == '\'')
            {
                sb.Append('\'');
                _pos += 2;
            }
            else if (_input[_pos] == '\'')
            {
                _pos++;
                return new Token(TokenType.StringLiteral, sb.ToString(), start);
            }
            else
            {
                sb.Append(_input[_pos]);
                _pos++;
            }
        }

        throw new FormatException($"Unterminated string starting at position {start}");
    }

    private Token ReadQuotedIdentifier()
    {
        var start = _pos;
        _pos++; // skip opening quote
        var sb = new System.Text.StringBuilder();

        while (_pos < _input.Length && _input[_pos] != '"')
        {
            sb.Append(_input[_pos]);
            _pos++;
        }

        if (_pos < _input.Length)
            _pos++; // skip closing quote

        return new Token(TokenType.Identifier, sb.ToString(), start);
    }

    private Token ReadParameter()
    {
        var start = _pos;
        _pos++; // skip @
        var sb = new System.Text.StringBuilder("@");

        while (_pos < _input.Length && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'))
        {
            sb.Append(_input[_pos]);
            _pos++;
        }

        return new Token(TokenType.Parameter, sb.ToString(), start);
    }

    private Token ReadNumber()
    {
        var start = _pos;
        var sb = new System.Text.StringBuilder();

        while (_pos < _input.Length && (char.IsDigit(_input[_pos]) || _input[_pos] == '.' || _input[_pos] == 'e' || _input[_pos] == 'E' || _input[_pos] == '+' || _input[_pos] == '-'))
        {
            // Handle + and - only after e/E
            if ((_input[_pos] == '+' || _input[_pos] == '-') && sb.Length > 0 && sb[^1] != 'e' && sb[^1] != 'E')
                break;
            sb.Append(_input[_pos]);
            _pos++;
        }

        return new Token(TokenType.NumberLiteral, sb.ToString(), start);
    }

    private Token ReadMinusOrNumber()
    {
        // Check if this is a negative number (minus followed by digit with no preceding identifier/literal)
        var pos = _pos;
        _pos++;
        return new Token(TokenType.Minus, "-", pos);
    }

    private Token ReadIdentifierOrKeyword()
    {
        var start = _pos;
        var sb = new System.Text.StringBuilder();

        while (_pos < _input.Length && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'))
        {
            sb.Append(_input[_pos]);
            _pos++;
        }

        var value = sb.ToString();
        if (Keywords.TryGetValue(value, out var keyword))
            return new Token(keyword, value, start);

        return new Token(TokenType.Identifier, value, start);
    }
}

namespace Cosmos.Emulator.QueryEngine;

public class CosmosSqlQueryEngine
{
    public TranslatedQuery Translate(
        string cosmosQuery,
        string containerName,
        HashSet<string> knownColumns,
        Dictionary<string, object>? parameters = null)
    {
        var lexer = new CosmosSqlLexer(cosmosQuery);
        var tokens = lexer.Tokenize();

        var parser = new CosmosSqlParser(tokens);
        var ast = parser.Parse();

        var translator = new SqliteQueryTranslator(containerName, ast.FromAlias, knownColumns);
        return translator.Translate(ast, parameters);
    }
}

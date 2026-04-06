using Microsoft.Data.Sqlite;

var conn = new SqliteConnection("Data Source=:memory:");
conn.Open();
using var cmd = conn.CreateCommand();

cmd.CommandText = "SELECT sqlite_version()";
Console.WriteLine($"SQLite version: {cmd.ExecuteScalar()}");

cmd.CommandText = "SELECT json_object('a', json('{\"b\":1}'))";
Console.WriteLine($"json_object with json(): {cmd.ExecuteScalar()}");

cmd.CommandText = "SELECT json_object('a', '{\"b\":1}')";
Console.WriteLine($"json_object without json(): {cmd.ExecuteScalar()}");

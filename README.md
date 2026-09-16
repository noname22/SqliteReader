# SqliteReader
A simple sqlite3 database reader in pure, managed .NET without dependencies to SQLite native binaries.

Reads tables row by row straight from disk with asynchronous IO, so databases much larger than available memory
can be iterated. Only table iteration is supported: there is no SQL, no index lookups and no writing.

## Usage

```csharp
using SqliteReader;

await using var db = await SqliteDatabase.OpenAsync("data.db");

foreach (SqliteTable table in db.Tables)
    Console.WriteLine($"{table.Name}({string.Join(", ", table.Columns)})");

await foreach (SqliteRow row in db.ReadTableAsync("FILE"))
{
    long? rowId = row.RowId;        // null for WITHOUT ROWID tables
    object? first = row[0];         // null, long, double, string or byte[]
    object? name = row["file_name"];
}
```

- Rows are returned in rowid order, or in primary key order for WITHOUT ROWID tables.
- Supported: all page sizes, UTF-8 and UTF-16 databases, overflow pages, auto-vacuum, INTEGER PRIMARY KEY
  rowid aliases, columns added with `ALTER TABLE ADD COLUMN` (their constant defaults are used), STRICT tables and
  generated columns. VIRTUAL generated columns are not stored in the file and are returned as `null`.
- The file is opened read-only and no locks are taken, so it must not be written to while it is being read.
  A database with a non-empty write-ahead log (`-wal`) or a hot rollback journal throws `NotSupportedException`.
  Open it once with SQLite to checkpoint or recover it first.

## Building and testing

Requires the .NET 8 SDK.

```sh
dotnet test Source/SqliteReader.sln
```

The test databases in `Source/SqliteReader.Tests/TestData` are stored with git-lfs.
`create-test-databases.sh` regenerates them and needs the `sqlite3` command line shell.
Tests against the large databases in `TestDatabases/` (not in git) are marked explicit. Run them with:

```sh
dotnet test Source/SqliteReader.sln --filter "FullyQualifiedName~RdsExplicitTests"
```

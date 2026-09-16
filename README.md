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

Pass `useWalFile: false` to `OpenAsync` to read only the database file and ignore a `-wal` file next to it.
Databases can also be read from any readable, seekable streams, for example in memory:

```csharp
await using var db = await SqliteDatabase.OpenStreamAsync(databaseStream, walStream /* or null */);
```

The streams are disposed together with the database unless `leaveOpen: true` is passed. `FileStream`s are read
through their file handle, so several tables can be read at the same time; reads from other streams take turns.

- Rows are returned in rowid order, or in primary key order for WITHOUT ROWID tables.
- Supported: all page sizes, UTF-8 and UTF-16 databases, overflow pages, auto-vacuum, INTEGER PRIMARY KEY
  rowid aliases, columns added with `ALTER TABLE ADD COLUMN` (their constant defaults are used), STRICT tables and
  generated columns. VIRTUAL generated columns are not stored in the file and are returned as `null`.
- Databases in WAL mode are supported: committed transactions in the `-wal` file are included. The log is indexed
  when the database is opened (the `-shm` file is not used), and later changes to it are not seen.
- Corrupt or malicious files fail with `SqliteFormatException`. Like SQLite, values larger than 1,000,000,000
  bytes are rejected.
- The files are opened read-only and no locks are taken, so the database must not be written to or checkpointed
  while it is being read. When opening a file, a hot rollback journal next to it throws `NotSupportedException`;
  open the database once with SQLite to recover it first. `OpenStreamAsync` only sees the streams it is given, so
  it can't detect a hot journal.

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

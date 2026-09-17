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

Databases can also be read from any readable, seekable streams, for example in memory:

```csharp
await using var db = await SqliteDatabase.OpenStreamAsync(databaseStream, walStream /* or null */);
```

The streams are disposed together with the database unless `leaveOpen: true` is passed. `FileStream`s are read
through their file handle, so several tables can be read at the same time; reads from other streams take turns.

Both methods take an optional `SqliteDatabaseOptions`. The defaults behave like SQLite; the limits are useful when
reading files from untrusted sources:

```csharp
var options = new SqliteDatabaseOptions
{
    UseWalFile = true,          // OpenAsync: include a -wal file next to the database
    CheckHotJournal = true,     // OpenAsync: refuse a database with a hot -journal file next to it
    MaxWalSize = null,          // largest -wal file to read, in bytes (null: no limit)
    MaxRowSize = 1_000_000_000, // largest row to read, in bytes; also limits single strings and blobs
};
await using var db = await SqliteDatabase.OpenAsync("data.db", options);
```

Exceeding a limit throws `NotSupportedException`. Anyone who can create files next to a database can change what is
read by placing a `-wal` file there, or prevent opening with a `-journal` file; turn the first two options off if
that matters.

- Rows are returned in rowid order, or in primary key order for WITHOUT ROWID tables.
- Supported: all page sizes, UTF-8 and UTF-16 databases, overflow pages, auto-vacuum, INTEGER PRIMARY KEY
  rowid aliases, columns added with `ALTER TABLE ADD COLUMN` (their constant defaults are used), STRICT tables and
  generated columns. VIRTUAL generated columns are not stored in the file and are returned as `null`.
- Databases in WAL mode are supported: committed transactions in the `-wal` file are included. The log is indexed
  when the database is opened (the `-shm` file is not used), and later changes to it are not seen.
- Corrupt or malicious files fail with `SqliteFormatException`.
- The files are opened read-only and no locks are taken, so the database must not be written to or checkpointed
  while it is being read. When opening a file, a hot rollback journal next to it throws `NotSupportedException`;
  open the database once with SQLite to recover it first. `OpenStreamAsync` only sees the streams it is given, so
  it can't detect a hot journal.

## Types

Row values are `object?` with one of five .NET types, one per SQLite storage class: `null`, `long`, `double`,
`string` and `byte[]`. Apart from the `REAL` conversion below, the type of a value is determined by how it is stored
in the file, not by the column's declared type, so cast it to what you need:

```csharp
long id = (long)row["id"];
int count = (int)(long)row["count"]; // all integers are long; unbox first, then narrow
double price = (double)row["price"];
string name = (string)row["name"];
byte[] data = (byte[])row["data"];
```

- **All integers are `long`**, regardless of declared column type or how many bytes are stored, so a value from a
  32-bit column must be unboxed as `long` first: `(int)(long)row["count"]`.
- **`REAL`-affinity columns return integers as `double`**: a value stored as an integer in a `REAL`, `FLOAT` or
  `DOUBLE PRECISION` column is converted to `double`, so a numeric value there is never a `long`. `NULL` is still
  `null`, and in non-STRICT tables text or blobs stored there are returned unchanged.
- No other conversions are made. A `BOOLEAN` column yields `long`, a date column yields `string` (or `long`/`double`,
  depending on how it was stored), and there are no `DateTime`, `Guid` or similar types.

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

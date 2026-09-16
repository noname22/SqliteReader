#!/usr/bin/env bash
# Regenerates the small test databases in this directory using the sqlite3 command line shell.
#
# For every database except the overflow ones, it also writes <name>.expected.jsonl, one JSON object per row:
#   {"t": <table>, "r": [<rowid or null>, [<typeof>, <value>] or null, ...]}
# Reals are printed as the hex of their big-endian IEEE-754 bits, and blobs as hex. VIRTUAL generated columns are null.
# The overflow databases contain deterministic values that the tests regenerate instead (see OverflowTests).
set -euo pipefail
cd "$(dirname "$0")"

rm -f ./*.db ./*.db-wal ./*.db-shm ./*.db-journal ./*.expected.jsonl

# dump <db> <table> <expected file>
dump() {
    local db=$1 table=$2 out=$3
    local without_rowid cols rowid order
    without_rowid=$(sqlite3 "$db" "SELECT count(*) FROM pragma_index_list('$table') WHERE origin = 'pk' AND
        (SELECT sql FROM sqlite_schema WHERE name = '$table') LIKE '%WITHOUT ROWID%'")
    cols=$(sqlite3 "$db" "SELECT group_concat(
            CASE WHEN hidden = 2 THEN 'NULL'
            ELSE replace('json_array(typeof(@), CASE typeof(@) WHEN ''blob'' THEN hex(@) WHEN ''real'' THEN hex(ieee754_to_blob(@)) ELSE @ END)',
                         '@', '\"' || replace(name, '\"', '\"\"') || '\"')
            END, ', ' ORDER BY cid)
        FROM pragma_table_xinfo('$table')")
    if [ "$without_rowid" -gt 0 ]; then
        rowid="NULL"; order=""
    else
        rowid="rowid"; order="ORDER BY rowid"
    fi
    local quoted=${table//\"/\"\"}
    sqlite3 "$db" "SELECT json_object('t', '${table//\'/\'\'}', 'r', json_array($rowid, $cols)) FROM \"$quoted\" NOT INDEXED $order" >> "$out"
}

# dump_all <db>: dumps every user table in schema order.
dump_all() {
    local db=$1 out=${1%.db}.expected.jsonl
    : > "$out"
    sqlite3 "$db" "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\\' ORDER BY rowid" |
        while IFS= read -r table; do dump "$db" "$table" "$out"; done
}

values_sql() {
    cat <<'SQL'
CREATE TABLE vals(id INTEGER PRIMARY KEY, v);
INSERT INTO vals(v) VALUES
    (NULL), (0), (1), (2), (-1), (127), (128), (-128), (-129),
    (32767), (32768), (-32768), (-32769), (8388607), (8388608), (-8388608), (-8388609),
    (2147483647), (2147483648), (-2147483648), (-2147483649),
    (140737488355327), (140737488355328), (-140737488355328), (-140737488355329),
    (9223372036854775807), (-9223372036854775808),
    (0.0), (-0.0), (1.5), (-2.25), (3.141592653589793), (0.1), (1e308), (-1e308), (5e-324), (2.5e-308), (1e999), (-1e999),
    (''), ('hello'), ('räksmörgås 日本語 🎉'), ('text with ''quotes'' and "double"'), (char(0) || 'nul'),
    (x''), (x'00'), (x'0102fffe'), (zeroblob(100)), (printf('%.200c', 'z'));
CREATE TABLE typed(i INTEGER, t TEXT, r REAL, b BLOB, n NUMERIC, f FLOAT, d DOUBLE PRECISION);
INSERT INTO typed(rowid, i, t, r, b, n, f, d) VALUES
    (-5, 1, 'one', 1.0, x'01', '1', 2, 3),
    (0, NULL, NULL, 2.5, NULL, '2.5', -7, 0),
    (9223372036854775807, 9223372036854775807, '', -0.0, x'', 'abc', 1e20, 123456789012);
SQL
}

# Basic value types, UTF-8 and both UTF-16 encodings.
for enc in "UTF-8:types" "UTF-16le:utf16le" "UTF-16be:utf16be"; do
    db=${enc#*:}.db
    { echo "PRAGMA encoding = '${enc%%:*}';"; values_sql; } | sqlite3 "$db"
    dump_all "$db"
done

# Multi-level b-trees: small pages and many rows, in both table and index b-trees.
sqlite3 multipage.db <<'SQL'
PRAGMA page_size = 512;
CREATE TABLE t(a INTEGER, b TEXT, c REAL);
INSERT INTO t SELECT value * 7 % 1000, 'row ' || value || ' ' || hex(value * 31), value / 3.0 FROM generate_series(1, 8000);
DELETE FROM t WHERE a % 13 = 0;
CREATE TABLE w(k TEXT, n INTEGER, payload TEXT, PRIMARY KEY (n, k)) WITHOUT ROWID;
INSERT INTO w SELECT 'key' || (value % 97), value, printf('%.*c', value % 50, 'p') FROM generate_series(1, 4000);
CREATE INDEX t_b ON t(b);
CREATE TABLE empty(x, y);
SQL
dump_all multipage.db

# Largest page size.
sqlite3 pagesize65536.db <<'SQL'
PRAGMA page_size = 65536;
CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT, data BLOB);
INSERT INTO t(name, data) SELECT 'name' || value, CAST(printf('%.*c', value % 300, char(65 + value % 26)) AS BLOB) FROM generate_series(1, 3000);
SQL
dump_all pagesize65536.db

# Auto-vacuum adds pointer-map pages, which a table scan must never visit.
sqlite3 autovacuum.db <<'SQL'
PRAGMA page_size = 1024;
PRAGMA auto_vacuum = FULL;
CREATE TABLE a(id INTEGER PRIMARY KEY, v TEXT);
CREATE TABLE b(id INTEGER PRIMARY KEY, v TEXT);
INSERT INTO a(v) SELECT printf('%.*c', value % 200, char(97 + value % 26)) FROM generate_series(1, 2000);
INSERT INTO b(v) SELECT printf('%.*c', value % 100, char(65 + value % 26)) FROM generate_series(1, 2000);
DELETE FROM a WHERE id % 3 = 0;
SQL
dump_all autovacuum.db

# Schema features that affect how records map to columns.
sqlite3 schema.db <<'SQL'
CREATE TABLE ipk(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
INSERT INTO ipk(name) VALUES ('a'), ('b'), ('c');
INSERT INTO ipk(id, name) VALUES (100, 'd'), (-3, 'e');
CREATE TABLE ipk_table_constraint(name TEXT, "Id" integer, CONSTRAINT pk PRIMARY KEY ("Id" DESC));
INSERT INTO ipk_table_constraint VALUES ('x', 10), ('y', 20);
CREATE TABLE not_alias_desc(id INTEGER PRIMARY KEY DESC, v);
INSERT INTO not_alias_desc VALUES (5, 'five'), (1, 'one');
CREATE TABLE not_alias_int(id INT PRIMARY KEY, v);
INSERT INTO not_alias_int VALUES (5, 'five'), (1, 'one');
CREATE TABLE "odd ""name""" ([select] TEXT, `from` INTEGER, "a,b" /* comment, with comma */ VARCHAR(10, 2), 'quoted' -- comment
    , [x y] DEFAULT (1 + 2), CHECK ("from" > 0), UNIQUE ([select], [from]));
INSERT INTO "odd ""name""" VALUES ('s', 1, 'ab', 'q', 3);
CREATE TABLE altered(id INTEGER PRIMARY KEY, a TEXT);
INSERT INTO altered(a) VALUES ('before1'), ('before2');
ALTER TABLE altered ADD COLUMN b INTEGER DEFAULT -42;
ALTER TABLE altered ADD COLUMN c TEXT DEFAULT 'it''s';
ALTER TABLE altered ADD COLUMN d REAL DEFAULT 1;
ALTER TABLE altered ADD COLUMN e DEFAULT NULL;
ALTER TABLE altered ADD COLUMN f TEXT DEFAULT 12.5;
ALTER TABLE altered ADD COLUMN g BLOB DEFAULT x'CAFE';
ALTER TABLE altered ADD COLUMN h DEFAULT TRUE;
ALTER TABLE altered ADD COLUMN i NUMERIC DEFAULT '0x10';
ALTER TABLE altered ADD COLUMN j INTEGER DEFAULT +7.0;
INSERT INTO altered(a, b, c, d, e, f, g, h, i, j) VALUES ('after', 1, 'c', 2.5, 'e', 'f', x'00', 0, 5, 6);
CREATE TABLE generated(a INTEGER, b INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL, c INTEGER AS (a * 3) STORED, d TEXT, e AS (d || '!'));
INSERT INTO generated(a, d) VALUES (1, 'x'), (2, 'y');
CREATE TABLE wr(a TEXT, b INTEGER, c TEXT COLLATE NOCASE, d BLOB, PRIMARY KEY (c, a, c COLLATE BINARY, c)) WITHOUT ROWID;
INSERT INTO wr VALUES ('a1', 1, 'C1', x'01'), ('a2', 2, 'c0', NULL), ('a0', 3, 'c1', x'03');
CREATE TABLE wr_column_pk(v REAL, id TEXT PRIMARY KEY) WITHOUT ROWID;
INSERT INTO wr_column_pk VALUES (1, 'b'), (2.5, 'a');
CREATE TABLE wr_generated(x INTEGER, y AS (x + 1), z TEXT, PRIMARY KEY (z)) WITHOUT ROWID;
INSERT INTO wr_generated(x, z) VALUES (10, 'k2'), (20, 'k1');
CREATE TABLE strict_t(id INTEGER PRIMARY KEY, a ANY, r REAL, t TEXT) STRICT;
INSERT INTO strict_t VALUES (1, '1', 1, 'x'), (2, 1.5, 3.25, 'y');
CREATE TABLE empty_table(a, b, c);
CREATE TABLE removed(x);
DROP TABLE removed;
CREATE VIEW v AS SELECT * FROM ipk;
CREATE INDEX ipk_name ON ipk(name);
CREATE TRIGGER trg AFTER INSERT ON ipk BEGIN SELECT 1; END;
CREATE TABLE "CREATE TABLE fake(x)" (real_column);
INSERT INTO "CREATE TABLE fake(x)" VALUES ('ok');
SQL
dump_all schema.db

# A small database that CorruptionTests modifies: 'big' is a single row whose payload spills onto an overflow
# chain, and 'tree' is deep enough to have interior pages below the root.
sqlite3 corruptible.db <<'SQL'
PRAGMA page_size = 512;
CREATE TABLE big(id INTEGER PRIMARY KEY, v BLOB);
INSERT INTO big VALUES (1, zeroblob(20000));
CREATE TABLE tree(id INTEGER PRIMARY KEY, v TEXT);
INSERT INTO tree(v) SELECT printf('%.*c', 40, 'x') FROM generate_series(1, 2000);
SQL
dump_all corruptible.db

# wal_db <name> <page size> <sql>: creates <name>.db with an un-checkpointed <name>.db-wal holding the effects of
# <sql>. The copies are taken while the connection is open, since sqlite3 checkpoints and deletes the log when it
# closes. Never open the resulting database with sqlite3, for the same reason. The expected data comes from running
# the same SQL on a separate database in rollback journal mode.
wal_db() {
    local name=$1 pagesize=$2 sql=$3
    rm -f "$name-source.db"* "$name-reference.db"
    sqlite3 "$name-source.db" <<SQL
.output /dev/null
PRAGMA page_size = $pagesize;
PRAGMA journal_mode = WAL;
PRAGMA wal_autocheckpoint = 0;
$sql
.shell cp $name-source.db $name.db && cp $name-source.db-wal $name.db-wal
SQL
    rm -f "$name-source.db"*
    { echo "PRAGMA page_size = $pagesize;"; echo "$sql"; } | sqlite3 "$name-reference.db" > /dev/null
    dump_all "$name-reference.db"
    mv "$name-reference.expected.jsonl" "$name.expected.jsonl"
    rm -f "$name-reference.db"
}

# All tables exist only in the log, and the database grows beyond the one page in the main file.
# The last transaction must stay a single insert of 'last' (WalTests relies on it).
wal_db wal 1024 "
CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT, n REAL);
INSERT INTO t(v, n) SELECT 'first ' || value, value / 4.0 FROM generate_series(1, 300);
BEGIN;
UPDATE t SET v = 'updated ' || id WHERE id % 5 = 0;
DELETE FROM t WHERE id % 7 = 0;
COMMIT;
CREATE TABLE w(k TEXT PRIMARY KEY, big TEXT) WITHOUT ROWID;
INSERT INTO w SELECT 'k' || value, printf('%.*c', 3000, char(65 + value % 26)) FROM generate_series(1, 20);
INSERT INTO t(v, n) SELECT 'second ' || value, -value FROM generate_series(1, 200);
INSERT INTO t(v) VALUES ('last');
"

# After a RESTART checkpoint the next transaction rewrites the start of the log with new salts. Frames from before
# the checkpoint remain after it and must be ignored, or the deleted rows would reappear.
wal_db wal-restarted 1024 "
CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);
INSERT INTO t(v) SELECT printf('%.*c', 100, char(97 + value % 26)) FROM generate_series(1, 2000);
UPDATE t SET v = upper(v) WHERE id % 2 = 0;
PRAGMA wal_checkpoint(RESTART);
DELETE FROM t WHERE id BETWEEN 100 AND 300;
"

# A database in WAL mode that has been checkpointed (no log file remains).
sqlite3 wal-checkpointed.db <<'SQL'
.output /dev/null
PRAGMA journal_mode = WAL;
CREATE TABLE t(x);
INSERT INTO t VALUES (1), (2), (3);
SQL
dump_all wal-checkpointed.db

# Payloads that spill onto overflow pages, including sizes around the local/overflow thresholds.
# Values are substr(group_concat(i * seed, ','), 1, length) over i = 1..length (OverflowTests regenerates them).
for pagesize in 512 4096; do
    sqlite3 "overflow$pagesize.db" <<SQL
PRAGMA page_size = $pagesize;
CREATE TABLE sizes(len INTEGER PRIMARY KEY);
INSERT OR IGNORE INTO sizes SELECT value FROM generate_series(0, 1200, 1) WHERE $pagesize = 512;
INSERT OR IGNORE INTO sizes SELECT value FROM generate_series(0, 9000, 29) WHERE $pagesize = 4096;
INSERT OR IGNORE INTO sizes SELECT value FROM generate_series(4020, 4100) WHERE $pagesize = 4096;
INSERT OR IGNORE INTO sizes VALUES (100000), (1000000);
CREATE TABLE t(id INTEGER PRIMARY KEY, len INTEGER, txt TEXT, blb BLOB);
INSERT INTO t(id, len, txt, blb)
    SELECT len, len,
        (SELECT coalesce(substr(group_concat(value * 3, ','), 1, len), '') FROM generate_series(1, len)),
        CAST((SELECT coalesce(substr(group_concat(value * 7, ','), 1, len), '') FROM generate_series(1, len)) AS BLOB)
    FROM sizes;
CREATE TABLE w(k TEXT PRIMARY KEY, len INTEGER) WITHOUT ROWID;
INSERT INTO w(k, len)
    SELECT (SELECT printf('%08d:', len) || coalesce(substr(group_concat(value * 5, ','), 1, len), '') FROM generate_series(1, len)), len
    FROM sizes WHERE len <= 20000;
DROP TABLE sizes;
VACUUM;
SQL
done

echo "Done."

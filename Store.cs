using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

internal sealed class Note
{
    public long Id, Revision;
    public string Title = "", Username = "", Password = "", Body = "";
}

internal sealed class Store : IDisposable
{
    private const int Row = 100, Done = 101;
    private const int FormatId = 0x514E5631; // QNV1
    private const int KdfIterations = 600000;
    private readonly string path;
    private IntPtr db;

    internal string FilePath { get { return path; } }

    public Store(string path, string password, bool create)
    {
        if (String.IsNullOrEmpty(password)) throw new ArgumentException("请输入主密码。");
        path = Path.GetFullPath(path);
        this.path = path;
        byte[] key = Encoding.UTF8.GetBytes(password);
        try
        {
            if (create) CreateVault(path, key);
            Open(path, key);
            try
            {
                using (Statement check = Prepare("PRAGMA application_id"))
                    if (!check.Read() || check.Number(0) != FormatId)
                        throw new InvalidDataException("文件不是本应用支持的保险库。");
                using (Statement version = Prepare("PRAGMA user_version"))
                    if (!version.Read() || version.Number(0) != 1)
                        throw new InvalidDataException("保险库版本不受支持。");
            }
            catch (IOException ex)
            {
                throw new InvalidDataException("无法打开保险库，请检查主密码和文件是否完好。", ex);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally { Array.Clear(key, 0, key.Length); }
    }

    private void CreateVault(string path, byte[] key)
    {
        if (File.Exists(path)) throw new IOException("目标文件已存在，请选择新文件。");
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (FileStream reserved = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            Open(staging, key);
            Execute("BEGIN IMMEDIATE");
            Execute("PRAGMA application_id=" + FormatId);
            Execute("PRAGMA user_version=1");
            Execute("CREATE TABLE clock (value INTEGER NOT NULL)");
            Execute("INSERT INTO clock VALUES (0)");
            Execute("CREATE TABLE notes (id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, title TEXT NOT NULL, username TEXT NOT NULL, search_title TEXT NOT NULL, search_user TEXT NOT NULL)");
            Execute("CREATE INDEX notes_recent ON notes(revision DESC)");
            Execute("CREATE TABLE contents (id INTEGER PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE, password TEXT NOT NULL, body TEXT NOT NULL)");
            Execute("COMMIT");
            Dispose();
            File.Move(staging, path);
        }
        finally
        {
            Dispose(); // Closing also rolls back incomplete schema creation.
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    internal static void CloneVerified(string sourcePath, string targetPath, string password)
    {
        if (String.IsNullOrEmpty(password)) throw new ArgumentException("请输入主密码。");
        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        if (String.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("新位置必须与当前资料库不同。");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("当前资料库不存在。", sourcePath);
        if (File.Exists(targetPath))
        {
            if (!FilesEqual(sourcePath, targetPath))
                throw new IOException("目标位置已有其他资料库，请选择其他位置。");
            VerifyClone(targetPath, password);
            return;
        }

        string directory = Path.GetDirectoryName(targetPath);
        Directory.CreateDirectory(directory);
        string staging = targetPath + ".moving";
        try
        {
            if (File.Exists(staging)) File.Delete(staging);
            File.Copy(sourcePath, staging, false);
            using (FileStream stream = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                stream.Flush(true);

            VerifyClone(staging, password);
            File.Move(staging, targetPath);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static void VerifyClone(string path, string password)
    {
        using (Store verified = new Store(path, password, false))
        {
            List<Note> sample = verified.Search("", 0, 1);
            if (sample.Count > 0 && verified.Get(sample[0].Id) == null)
                throw new InvalidDataException("目标资料库校验失败。");
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        using (var first = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var second = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] a = new byte[65536];
            byte[] b = new byte[65536];
            int read;
            while ((read = first.Read(a, 0, a.Length)) > 0)
            {
                int offset = 0;
                while (offset < read)
                {
                    int count = second.Read(b, offset, read - offset);
                    if (count == 0) return false;
                    offset += count;
                }
                for (int i = 0; i < read; i++)
                    if (a[i] != b[i]) return false;
            }
            return second.ReadByte() == -1;
        }
    }

    private void Open(string path, byte[] key)
    {
        Check(Sqlite.sqlite3_open_v2(Utf8Z(path), out db, 2, IntPtr.Zero));
        ConfigureCipher();
        Check(Sqlite.sqlite3_key(db, key, key.Length));
        Check(Sqlite.sqlite3_busy_timeout(db, 700));
        Execute("PRAGMA temp_store=MEMORY");
        Execute("PRAGMA foreign_keys=ON");
    }

    private void ConfigureCipher()
    {
        int cipher = Sqlite.sqlite3mc_cipher_index("chacha20");
        if (cipher <= 0 || Sqlite.sqlite3mc_config(db, "cipher", cipher) != cipher ||
            Sqlite.sqlite3mc_config_cipher(db, "chacha20", "kdf_iter", KdfIterations) != KdfIterations)
            throw new InvalidOperationException("加密引擎无法应用此保险库所需的算法参数。");
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (db == IntPtr.Zero) throw new ObjectDisposedException("Store");
        if (String.IsNullOrEmpty(newPassword)) throw new ArgumentException("请输入新主密码。");
        using (var verified = new Store(path, currentPassword, false))
        {
            if (newPassword == currentPassword) return;
            byte[] key = Encoding.UTF8.GetBytes(newPassword);
            try
            {
                verified.ConfigureCipher();
                verified.Check(Sqlite.sqlite3_rekey(verified.db, key, key.Length));
                // Keep the connection that now holds the new key.
                Dispose();
                db = verified.db;
                verified.db = IntPtr.Zero;
            }
            finally { Array.Clear(key, 0, key.Length); }
        }
    }

    public List<Note> Search(string query, int offset, int limit)
    {
        if (offset < 0 || limit <= 0) throw new ArgumentOutOfRangeException("分页参数无效");
        query = (query ?? "").ToLowerInvariant();
        string filter = query.Length == 0 ? "" : " WHERE instr(search_title,?3)>0 OR instr(search_user,?3)>0";
        using (Statement stmt = Prepare("SELECT id,revision,title,username FROM notes" + filter + " ORDER BY revision DESC LIMIT ?1 OFFSET ?2"))
        {
            stmt.Bind(1, limit);
            stmt.Bind(2, offset);
            if (query.Length != 0) stmt.Bind(3, query);
            List<Note> result = new List<Note>();
            while (stmt.Read())
                result.Add(new Note { Id = stmt.Number(0), Revision = stmt.Number(1), Title = stmt.Text(2), Username = stmt.Text(3) });
            return result;
        }
    }

    public Note Get(long id)
    {
        using (Statement stmt = Prepare("SELECT n.revision,n.title,n.username,c.password,c.body FROM notes n JOIN contents c ON c.id=n.id WHERE n.id=?1"))
        {
            stmt.Bind(1, id);
            if (!stmt.Read()) return null;
            return new Note { Id = id, Revision = stmt.Number(0), Title = stmt.Text(1),
                              Username = stmt.Text(2), Password = stmt.Text(3), Body = stmt.Text(4) };
        }
    }

    public void Save(Note note)
    {
        if (note == null) throw new ArgumentNullException("note");
        long id = note.Id;
        long revision;
        Execute("BEGIN IMMEDIATE");
        try
        {
            Execute("UPDATE clock SET value=value+1");
            using (Statement seq = Prepare("SELECT value FROM clock"))
            {
                if (!seq.Read()) throw new InvalidDataException("保险库记录版本信息缺失。");
                revision = seq.Number(0);
            }
            string sql = id == 0
                ? "INSERT INTO notes(title,username,search_title,search_user,revision) VALUES(?1,?2,?3,?4,?5)"
                : "UPDATE notes SET title=?1,username=?2,search_title=?3,search_user=?4,revision=?5 WHERE id=?6 AND revision=?7";
            using (Statement stmt = Prepare(sql))
            {
                string title = note.Title ?? "", user = note.Username ?? "";
                stmt.Bind(1, title);
                stmt.Bind(2, user);
                stmt.Bind(3, title.ToLowerInvariant());
                stmt.Bind(4, user.ToLowerInvariant());
                stmt.Bind(5, revision);
                if (id != 0)
                {
                    stmt.Bind(6, id);
                    stmt.Bind(7, note.Revision);
                }
                stmt.Run();
                RequireChange();
            }
            if (id == 0) id = Sqlite.sqlite3_last_insert_rowid(db);
            string contentSql = note.Id == 0
                ? "INSERT INTO contents(password,body,id) VALUES(?1,?2,?3)"
                : "UPDATE contents SET password=?1,body=?2 WHERE id=?3";
            using (Statement stmt = Prepare(contentSql))
            {
                stmt.Bind(1, note.Password ?? "");
                stmt.Bind(2, note.Body ?? "");
                stmt.Bind(3, id);
                stmt.Run();
                RequireChange();
            }
            Execute("COMMIT");
        }
        catch
        {
            if (Sqlite.sqlite3_get_autocommit(db) == 0) Execute("ROLLBACK");
            throw;
        }
        note.Id = id;
        note.Revision = revision;
    }

    public void Delete(long id, long revision)
    {
        using (Statement stmt = Prepare("DELETE FROM notes WHERE id=?1 AND revision=?2"))
        {
            stmt.Bind(1, id);
            stmt.Bind(2, revision);
            stmt.Run();
            RequireChange();
        }
    }

    private void RequireChange()
    {
        if (Sqlite.sqlite3_changes(db) != 1)
            throw new InvalidOperationException("这条记录已被修改或删除，请重新载入后再试。");
    }

    private void Check(int code)
    {
        if (code == 0) return;
        if (code == 5 || code == 6) throw new IOException("保险库正在被其他操作使用，请稍后重试。");
        if (code == 26 || code == 11) throw new InvalidDataException("主密码错误或保险库文件已损坏。");
        throw new IOException("保险库读写失败（SQLite " + code + "）。");
    }

    private static byte[] Utf8Z(string value)
    {
        int size = Encoding.UTF8.GetByteCount(value);
        byte[] buffer = new byte[size + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        return buffer;
    }

    private Statement Prepare(string sql)
    {
        if (db == IntPtr.Zero) throw new ObjectDisposedException("Store");
        IntPtr stmt;
        Check(Sqlite.sqlite3_prepare_v2(db, Utf8Z(sql), -1, out stmt, IntPtr.Zero));
        return new Statement(this, stmt);
    }

    private void Execute(string sql)
    {
        using (Statement stmt = Prepare(sql)) stmt.Run();
    }

    public void Dispose()
    {
        if (db != IntPtr.Zero)
        {
            Sqlite.sqlite3_close_v2(db);
            db = IntPtr.Zero;
        }
    }

    private sealed class Statement : IDisposable
    {
        private readonly Store store;
        private IntPtr stmt;
        public Statement(Store store, IntPtr stmt) { this.store = store; this.stmt = stmt; }

        public void Bind(int index, long value) { store.Check(Sqlite.sqlite3_bind_int64(stmt, index, value)); }

        public void Bind(int index, string value)
        {
            byte[] bytes = Utf8Z(value);
            try { store.Check(Sqlite.sqlite3_bind_text(stmt, index, bytes, bytes.Length - 1, new IntPtr(-1))); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        public long Number(int column) { return Sqlite.sqlite3_column_int64(stmt, column); }

        public string Text(int column)
        {
            IntPtr ptr = Sqlite.sqlite3_column_text(stmt, column);
            int size = Sqlite.sqlite3_column_bytes(stmt, column);
            if (size == 0) return "";
            byte[] bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes, 0, size); }
        }

        public bool Read()
        {
            int code = Sqlite.sqlite3_step(stmt);
            if (code == Row) return true;
            if (code != Done) store.Check(code);
            return false;
        }

        public void Run()
        {
            int code = Sqlite.sqlite3_step(stmt);
            if (code != Done) store.Check(code);
        }

        public void Dispose()
        {
            if (stmt != IntPtr.Zero) Sqlite.sqlite3_finalize(stmt);
            stmt = IntPtr.Zero;
        }
    }

    private static class Sqlite
    {
        private const string Dll = "sqlite3mc.dll";
        private const CallingConvention C = CallingConvention.Cdecl;
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_key(IntPtr db, byte[] key, int size);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_rekey(IntPtr db, byte[] key, int size);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3mc_cipher_index(string name);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3mc_config(IntPtr db, string name, int value);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3mc_config_cipher(IntPtr db, string cipher, string name, int value);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int size, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] text, int size, IntPtr destructor);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention=C)] internal static extern long sqlite3_column_int64(IntPtr stmt, int column);
        [DllImport(Dll, CallingConvention=C)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_changes(IntPtr db);
        [DllImport(Dll, CallingConvention=C)] internal static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Dll, CallingConvention=C)] internal static extern int sqlite3_get_autocommit(IntPtr db);
    }
}

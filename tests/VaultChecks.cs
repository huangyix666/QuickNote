using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

internal static class VaultChecks
{
    private const string Passphrase = "test-only-临时主密码";
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void MustFail(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new Exception(message);
    }
    private static double TimeMs(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }
    private static double P95(List<double> samples)
    {
        samples.Sort();
        return samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    }
    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName + " " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
    private static void Run()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "vaultcheck-" + Guid.NewGuid().ToString("N"))).FullName;
        string file = Path.Combine(folder, "test.qnv");
        const string marker = "unique-disk-plaintext-marker";
        try
        {
            long id;
            using (Store store = new Store(file, Passphrase, true))
            {
                Note note = new Note { Title = "网站 100%_", Username = "someone@example.net",
                    Password = marker + "-password", Body = "一行\r\n第二行 " + marker };
                store.Save(note);
                id = note.Id;
                Require(id > 0 && store.Get(id).Body == note.Body, "Round trip");
                Require(store.Search("100%_", 0, 10).Single().Id == id, "Literal search");
                Require(store.Search("EXAMPLE.NET", 0, 10).Single().Id == id, "Account search");
                using (Store other = new Store(file, Passphrase, false))
                {
                    Note stale = other.Get(id);
                    note.Title = "updated-" + marker;
                    store.Save(note);
                    stale.Title = "must fail";
                    MustFail(() => other.Save(stale), "Concurrent update lost");
                    MustFail(() => other.Delete(id, stale.Revision), "Stale delete succeeded");
                }
                var writes = new List<double>();
                for (int i = 0; i < 10000; i++)
                {
                    Note item = new Note { Title = "便签 " + i.ToString("D5"), Username = "sample" + i,
                        Password = "sample-secret", Body = new string('文', 512) };
                    writes.Add(TimeMs(() => store.Save(item)));
                }
                var searches = new List<double>();
                for (int i = 0; i < 100; i++)
                {
                    string query = "便签 " + (i * 97).ToString("D5");
                    searches.Add(TimeMs(() => Require(store.Search(query, 0, 100).Count == 1, "Search miss")));
                }
                Console.WriteLine("10000 single-row commits p95 {0:F2} ms; 100 selective searches p95 {1:F2} ms",
                    P95(writes), P95(searches));
                var first = store.Search("", 0, 100);
                var second = store.Search("", 100, 100);
                Require(first.Count == 100 && second.Count == 100 &&
                    !first.Select(n => n.Id).Intersect(second.Select(n => n.Id)).Any(), "Bad pages");
                Require(first.All(n => String.IsNullOrEmpty(n.Password) && String.IsNullOrEmpty(n.Body)),
                    "Secret fields present in summaries");
            }
            MustFail(() => { using (new Store(file, "incorrect-key", false)) {} }, "Wrong key accepted");
            MustFail(() => { using (new Store(file, Passphrase, true)) {} }, "Overwrite accepted");
            Require(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(marker), "Plaintext in file");
            using (var store = new Store(file, Passphrase, false))
            {
                var note = store.Get(id);
                Require(note.Password == marker + "-password", "Persisted secret mismatch");
                store.Delete(id, note.Revision);
                Require(store.Search(marker, 0, 10).Count == 0, "Delete did not persist");
            }
            CheckPasswordChange(Path.Combine(folder, "rekey.qnv"), marker);
            Console.WriteLine("PASS persistence, wrong password, ciphertext, conflicts, pagination, deletion, password change");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void CheckPasswordChange(string file, string marker)
    {
        const string newPassword = "短";
        var note = new Note { Title = "Change password", Password = marker, Body = "Original" };
        using (var store = new Store(file, Passphrase, true))
        {
            store.Save(note);
            MustFail(() => store.ChangePassword("wrong password", newPassword), "Wrong current password accepted");
            MustFail(() => store.ChangePassword(Passphrase, ""), "Empty replacement password accepted");
            Require(store.Get(note.Id).Body == note.Body, "Failed password change invalidated the active session");
            using (var original = new Store(file, Passphrase, false))
                Require(original.Get(note.Id).Password == marker, "Failed password change altered the existing vault");

            using (var oldConnection = new Store(file, Passphrase, false))
            {
                var stale = oldConnection.Get(note.Id);
                store.ChangePassword(Passphrase, newPassword);
                stale.Body = "Old session write";
                MustFail(() => oldConnection.Save(stale), "Old connection can still write after password change");
            }
            Require(store.Get(note.Id).Password == marker, "Password change lost existing credentials");
            note.Body = "Updated after password change";
            store.Save(note);
        }
        MustFail(() => { using (new Store(file, Passphrase, false)) {} }, "Former password still opens the vault");
        using (var store = new Store(file, newPassword, false))
        {
            var reopened = store.Get(note.Id);
            Require(reopened.Password == marker && reopened.Body == note.Body, "Password change did not preserve persisted data");
        }
        Require(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(marker), "Password change exposed plaintext");
    }
}

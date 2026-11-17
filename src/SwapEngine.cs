using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace HorizonLanguage
{
    [DataContract]
    public sealed class Original
    {
        [DataMember] public string Name;
        [DataMember] public string Hash;
    }

    [DataContract]
    public sealed class Manifest
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Directory;
        [DataMember] public string Generation;
        [DataMember] public string ActiveLanguage;
        [DataMember] public List<Original> Originals = new List<Original>();
    }

    [DataContract]
    public sealed class Change
    {
        [DataMember] public string Name;
        [DataMember] public string SourceName;
        [DataMember] public string BeforeHash;
        [DataMember] public string AfterHash;
    }

    [DataContract]
    public sealed class Journal
    {
        [DataMember] public string Id;
        [DataMember] public Manifest Before;
        [DataMember] public Manifest After;
        [DataMember] public List<Change> Changes = new List<Change>();
    }

    public sealed class Inspection
    {
        public string[] Languages;
        public string ActiveLanguage;
        public int BackupCount;
        public bool Pending;
        public string Problem;
    }

    // Normal operations rename local ZIPs. Backups are only for failure recovery.
    public sealed class SwapEngine
    {
        public const string StoreName = ".fh6-language-switcher";
        public readonly string TablesDirectory;
        private readonly string store;
        // Fault injection is internal and is compiled into the test harness only as a caller.
        internal Action<int> AfterReplace = null;

        public SwapEngine(string path)
        {
            TablesDirectory = ResolveDirectory(path);
            store = Path.Combine(TablesDirectory, StoreName);
        }

        public static string ResolveDirectory(string input)
        {
            if (String.IsNullOrWhiteSpace(input)) throw new IOException("请选择游戏根目录或 StringTables 文件夹。");
            string path = Path.GetFullPath(input.Trim().Trim('"'));
            string nested = Path.Combine(path, @"media\Stripped\StringTables");
            if (System.IO.Directory.Exists(nested)) path = nested;
            if (!System.IO.Directory.Exists(path) || (!File.Exists(Path.Combine(path, "CHS.zip")) &&
                !File.Exists(Path.Combine(path, StoreName, "pending.json"))))
                throw new IOException("没有找到 CHS.zip。请选择包含 media\\Stripped\\StringTables 的游戏目录，或直接选择 StringTables。");
            return path.TrimEnd(Path.DirectorySeparatorChar);
        }

        public Inspection Inspect()
        {
            using (Acquire())
            {
                GuardStore();
                var result = new Inspection();
                result.Languages = System.IO.Directory.GetFiles(TablesDirectory, "*.zip")
                    .Select(Path.GetFileName).Where(ValidName)
                    .Where(n => !Same(n, "CHS.zip")).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                result.Pending = File.Exists(Path.Combine(store, "pending.json"));
                if (result.Pending)
                {
                    result.Problem = "发现未完成的操作，请先恢复中断操作。";
                    return result;
                }
                try
                {
                    Manifest m = LoadManifest();
                    if (m != null)
                    {
                        result.ActiveLanguage = m.ActiveLanguage;
                        result.BackupCount = m.Originals.Count;
                        VerifyBackups(m);
                        VerifyCurrent(m);
                    }
                }
                catch (Exception ex) { result.Problem = ex.Message; }
                return result;
            }
        }

        public void Apply(string target)
        {
            if (!ValidName(target) || Same(target, "CHS.zip")) throw new IOException("请选择 CHS 以外的语言包。");
            using (Acquire())
            {
                EnsureGameClosed();
                GuardStore();
                RequireNoPending();
                Manifest before = LoadManifest();
                if (before != null) { VerifyBackups(before); VerifyCurrent(before); }
                if (before != null && Same(before.ActiveLanguage, target)) return;
                ValidateZip("CHS.zip");
                ValidateZip(target);
                Manifest after = before == null ? NewManifest() : Clone(before);
                EnsureOriginal(after, "CHS.zip");
                EnsureOriginal(after, target);
                after.ActiveLanguage = target;
                Execute(before, after);
            }
        }

        public void Restore()
        {
            using (Acquire())
            {
                EnsureGameClosed();
                GuardStore();
                RequireNoPending();
                Manifest before = LoadManifest();
                if (before == null) return;
                VerifyBackups(before);
                VerifyCurrent(before);
                if (before.ActiveLanguage == null) return;
                Manifest after = Clone(before);
                after.ActiveLanguage = null;
                Execute(before, after);
            }
        }

        // Explicit opt-in after Steam has restored the correct files for the current build.
        // Old generations remain on disk; this never uses old backups to overwrite updated ZIPs.
        public void Rebaseline()
        {
            using (Acquire())
            {
                EnsureGameClosed();
                GuardStore();
                RequireNoPending();
                ValidateZip("CHS.zip");
                System.IO.Directory.CreateDirectory(store);
                string manifest = Path.Combine(store, "manifest.json");
                if (File.Exists(manifest))
                {
                    string archive = Path.Combine(store, "retired-" + Guid.NewGuid().ToString("N") + ".json");
                    CopyDurable(manifest, archive);
                }
                SaveAtomic(manifest, NewManifest());
            }
        }

        public void Recover()
        {
            using (Acquire())
            {
                EnsureGameClosed();
                GuardStore();
                string pending = Path.Combine(store, "pending.json");
                if (!File.Exists(pending)) return;
                Rollback(Read<Journal>(pending));
            }
        }

        private void Execute(Manifest before, Manifest after)
        {
            VerifyBackups(after);
            var journal = new Journal { Id = Guid.NewGuid().ToString("N"), Before = before, After = after };
            string tx = TransactionDirectory(journal.Id);
            System.IO.Directory.CreateDirectory(Path.Combine(tx, "before"));
            System.IO.Directory.CreateDirectory(Path.Combine(tx, "renamed"));
            foreach (Original original in after.Originals)
            {
                string sourceName = ExpectedSource(after, original.Name);
                string currentSource = after.Originals.Single(o => Same(
                    before == null ? o.Name : ExpectedSource(before, o.Name), sourceName)).Name;
                if (Same(original.Name, currentSource)) continue;
                string source = BackupPath(after, sourceName);
                string current = GameFile(original.Name);
                string beforeHash = Hash(current);
                string afterHash = Hash(source);
                CopyDurable(current, Path.Combine(tx, "before", original.Name));
                CheckHash(Path.Combine(tx, "before", original.Name), beforeHash);
                journal.Changes.Add(new Change { Name = original.Name, SourceName = currentSource, BeforeHash = beforeHash, AfterHash = afterHash });
            }
            // Re-check all managed files after staging and before publishing the recovery journal.
            if (before != null) VerifyCurrent(before);
            foreach (Original original in after.Originals)
                if (before == null || !before.Originals.Any(o => Same(o.Name, original.Name)))
                    CheckHash(GameFile(original.Name), original.Hash);
            string pending = Path.Combine(store, "pending.json");
            SaveAtomic(pending, journal);
            try
            {
                int count = 0;
                foreach (Change c in journal.Changes)
                {
                    CheckHash(GameFile(c.Name), c.BeforeHash);
                    File.Move(GameFile(c.Name), Path.Combine(tx, "renamed", c.Name));
                    if (AfterReplace != null) AfterReplace(++count);
                }
                foreach (Change c in journal.Changes)
                {
                    File.Move(Path.Combine(tx, "renamed", c.SourceName), GameFile(c.Name));
                    CheckHash(GameFile(c.Name), c.AfterHash);
                    if (AfterReplace != null) AfterReplace(++count);
                }
                VerifyCurrent(after);
                SaveAtomic(Path.Combine(store, "manifest.json"), after);
            }
            catch (Exception operationError)
            {
                try { Rollback(journal); }
                catch (Exception recoveryError)
                {
                    throw new IOException("操作中断，自动回滚未完成。备份和恢复记录已保留。请关闭游戏后点击“恢复中断操作”。\r\n" +
                        operationError.Message + "\r\n" + recoveryError.Message, operationError);
                }
                throw new IOException("操作失败，已回滚到本次操作前：" + operationError.Message, operationError);
            }
            // If cleanup fails, the journal intentionally remains so Recover can roll back safely.
            File.Delete(pending);
            CleanTransaction(journal.Id);
        }

        private void Rollback(Journal journal)
        {
            ValidateJournal(journal);
            string tx = TransactionDirectory(journal.Id);
            // Validate every file before changing any of them; never clobber an external edit.
            foreach (Change c in journal.Changes)
            {
                GuardManagedPath(Path.Combine(tx, "before", c.Name));
                CheckHash(Path.Combine(tx, "before", c.Name), c.BeforeHash);
                if (!File.Exists(GameFile(c.Name))) continue; // A crash can occur between the two rename phases.
                string current = Hash(GameFile(c.Name));
                if (current != c.BeforeHash && current != c.AfterHash)
                    throw new IOException(c.Name + " 在中断后被其他程序修改。已停止恢复并保留备份。");
            }
            foreach (Change c in journal.Changes)
                ReplaceFrom(Path.Combine(tx, "before", c.Name), GameFile(c.Name), c.BeforeHash);
            string manifest = Path.Combine(store, "manifest.json");
            if (journal.Before == null) File.Delete(manifest);
            else SaveAtomic(manifest, journal.Before);
            File.Delete(Path.Combine(store, "pending.json"));
            CleanTransaction(journal.Id);
        }

        private void ValidateJournal(Journal journal)
        {
            if (journal == null || !ValidId(journal.Id) || journal.Changes == null || journal.Changes.Count > 64)
                throw new IOException("恢复记录损坏。请保留备份目录并通过 Steam 验证文件完整性。");
            ValidateManifest(journal.After);
            if (journal.Before != null) ValidateManifest(journal.Before);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Change c in journal.Changes)
                if (c == null || !ValidName(c.Name) || !ValidName(c.SourceName) || !ValidHash(c.BeforeHash) || !ValidHash(c.AfterHash) || !names.Add(c.Name))
                    throw new IOException("恢复记录包含无效的文件信息。");
            if (journal.Changes.Select(c => c.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count ||
                journal.Changes.Any(c => !names.Contains(c.SourceName)))
                throw new IOException("恢复记录包含无效的重命名顺序。");
        }

        private void EnsureOriginal(Manifest m, string name)
        {
            if (m.Originals.Any(o => Same(o.Name, name))) return;
            string source = GameFile(name);
            ValidateZip(name);
            string hash = Hash(source);
            string backup = BackupPath(m, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backup));
            // An orphaned backup from a failed pre-journal operation may be reused only if identical.
            if (!File.Exists(backup)) CopyDurable(source, backup);
            CheckHash(backup, hash);
            m.Originals.Add(new Original { Name = name, Hash = hash });
        }

        private Manifest NewManifest()
        {
            return new Manifest { Directory = TablesDirectory, Generation = Guid.NewGuid().ToString("N") };
        }

        private Manifest LoadManifest()
        {
            string path = Path.Combine(store, "manifest.json");
            if (!File.Exists(path)) return null;
            Manifest m = Read<Manifest>(path);
            ValidateManifest(m);
            return m;
        }

        private void ValidateManifest(Manifest m)
        {
            if (m == null || m.Version != 1 || !Same(m.Directory, TablesDirectory) || !ValidId(m.Generation) ||
                m.Originals == null || m.Originals.Count > 64)
                throw new IOException("备份记录无效或目录已移动。请保留备份，验证游戏文件后重建基线。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Original o in m.Originals)
                if (o == null || !ValidName(o.Name) || !ValidHash(o.Hash) || !names.Add(o.Name))
                    throw new IOException("备份记录包含无效的文件信息。");
            if (m.ActiveLanguage != null && (Same(m.ActiveLanguage, "CHS.zip") || !names.Contains(m.ActiveLanguage) || !names.Contains("CHS.zip")))
                throw new IOException("备份记录中的当前语言无效。");
        }

        private void VerifyBackups(Manifest m)
        {
            foreach (Original o in m.Originals) CheckHash(BackupPath(m, o.Name), o.Hash);
        }

        private void VerifyCurrent(Manifest m)
        {
            foreach (Original o in m.Originals)
            {
                Original source = m.Originals.Single(x => Same(x.Name, ExpectedSource(m, o.Name)));
                try { CheckHash(GameFile(o.Name), source.Hash); }
                catch (IOException ex)
                {
                    throw new IOException(o.Name + " 与记录不一致，可能由游戏更新、Steam 验证或手动修改导致。为避免覆盖新文件，已停止。请先通过 Steam 验证文件完整性，再重建备份基线。\r\n" + ex.Message, ex);
                }
            }
        }

        private static string ExpectedSource(Manifest m, string name)
        {
            if (m.ActiveLanguage == null) return name;
            if (Same(name, "CHS.zip")) return m.ActiveLanguage;
            if (Same(name, m.ActiveLanguage)) return "CHS.zip";
            return name;
        }

        private string GameFile(string name)
        {
            if (!ValidName(name)) throw new IOException("无效的语言包文件名。");
            string path = Path.Combine(TablesDirectory, name);
            NoLinks(path);
            return path;
        }

        private string BackupPath(Manifest m, string name)
        {
            if (!ValidId(m.Generation) || !ValidName(name)) throw new IOException("无效的备份路径。");
            string path = Path.Combine(store, "generations", m.Generation, name);
            GuardManagedPath(path);
            return path;
        }

        private string TransactionDirectory(string id)
        {
            if (!ValidId(id)) throw new IOException("无效的事务编号。");
            string path = Path.Combine(store, "transactions", id);
            GuardManagedPath(path);
            return path;
        }

        private void CleanTransaction(string id)
        {
            string path = TransactionDirectory(id);
            if (!System.IO.Directory.Exists(path)) return;
            // Only a validated, tool-owned GUID directory can be removed recursively.
            // Check descendants too, so a directory link cannot redirect cleanup.
            CheckTree(path);
            try { System.IO.Directory.Delete(path, true); }
            catch (IOException) { /* A harmless orphan is preferable to a failed completed operation. */ }
            catch (UnauthorizedAccessException) { }
        }

        private static void CheckTree(string path)
        {
            NoLinks(path);
            foreach (string file in System.IO.Directory.GetFiles(path)) NoLinks(file);
            foreach (string child in System.IO.Directory.GetDirectories(path)) CheckTree(child);
        }

        private void GuardStore()
        {
            GuardManagedPath(Path.Combine(store, "manifest.json"));
            GuardManagedPath(Path.Combine(store, "pending.json"));
        }

        private void GuardManagedPath(string path)
        {
            string current = path;
            while (!Same(current, TablesDirectory))
            {
                if (String.IsNullOrEmpty(current) || !current.StartsWith(store, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("备份路径超出工具目录。");
                NoLinks(current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static void NoLinks(string path)
        {
            if ((File.Exists(path) || System.IO.Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("为保护文件，工具不处理符号链接或目录联接：" + path);
        }

        private void ValidateZip(string name)
        {
            using (var stream = new FileStream(GameFile(name), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                if (zip.Entries.Count == 0) throw new IOException(name + " 是空的 ZIP 文件。");
        }

        private void RequireNoPending()
        {
            if (File.Exists(Path.Combine(store, "pending.json"))) throw new IOException("请先恢复中断操作。");
        }

        private static void EnsureGameClosed()
        {
            foreach (Process process in Process.GetProcesses())
                using (process)
                    if (process.ProcessName.IndexOf("ForzaHorizon6", StringComparison.OrdinalIgnoreCase) >= 0 || Same(process.ProcessName, "FH6"))
                        throw new IOException("请先完全退出 Forza Horizon 6，再修改语言包。");
        }

        private IDisposable Acquire()
        {
            string key;
            using (var sha = SHA256.Create())
                key = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(TablesDirectory.ToUpperInvariant()))).Replace("-", "");
            var mutex = new Mutex(false, @"Local\FH6Language_" + key);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new IOException("另一个窗口正在处理此目录，请稍后重试。"); }
            return new HeldMutex(mutex);
        }

        private sealed class HeldMutex : IDisposable
        {
            private readonly Mutex mutex;
            public HeldMutex(Mutex value) { mutex = value; }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }

        public static string Hash(string path)
        {
            NoLinks(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void CheckHash(string path, string expected)
        {
            if (Hash(path) != expected) throw new IOException("SHA-256 校验失败：" + Path.GetFileName(path));
        }

        private static void CopyDurable(string source, string destination)
        {
            NoLinks(source);
            NoLinks(destination);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
        }

        private static void ReplaceFrom(string source, string destination, string expectedHash)
        {
            NoLinks(destination);
            string temp = destination + ".fh6-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                CopyDurable(source, temp);
                CheckHash(temp, expectedHash);
                if (File.Exists(destination)) ReplaceWithRetry(temp, destination);
                else File.Move(temp, destination);
                CheckHash(destination, expectedHash);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static void SaveAtomic<T>(string path, T value)
        {
            NoLinks(path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                    stream.Flush(true);
                }
                if (File.Exists(path)) ReplaceWithRetry(temp, path);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static T Read<T>(string path)
        {
            NoLinks(path);
            if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("备份记录过大，拒绝读取。");
            using (var stream = File.OpenRead(path))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static void ReplaceWithRetry(string source, string destination)
        {
            // Antivirus/indexers (including OneDrive) can briefly prevent atomic replacement.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(source, destination, null); return; }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xFFFF;
                    if (attempt >= 3 || (code != 32 && code != 33 && code != 1175)) throw;
                    Thread.Sleep(60 * (attempt + 1));
                }
            }
        }

        private static Manifest Clone(Manifest m)
        {
            return new Manifest { Version = m.Version, Directory = m.Directory, Generation = m.Generation,
                ActiveLanguage = m.ActiveLanguage, Originals = m.Originals.Select(o => new Original { Name = o.Name, Hash = o.Hash }).ToList() };
        }

        private static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool ValidName(string name) { return name != null && Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_-]{1,15}\.zip\z", RegexOptions.IgnoreCase); }
        private static bool ValidId(string id) { return id != null && Regex.IsMatch(id, @"\A[a-f0-9]{32}\z"); }
        private static bool ValidHash(string hash) { return hash != null && Regex.IsMatch(hash, @"\A[a-f0-9]{64}\z"); }
    }
}

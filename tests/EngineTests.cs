using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HorizonLanguage;
using Microsoft.Win32.SafeHandles;

internal static class EngineTests
{
    private static int passed;
    private static int failed;

    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--crash")
        {
            var engine = new SwapEngine(args[1]);
            int point = Int32.Parse(args[2]);
            engine.AfterReplace = count => { if (count == point) Environment.Exit(73); };
            engine.Apply(args[3]);
            return 74;
        }
        Test("Inspect is read-only and discovers language ZIPs", f => {
            var state = f.Engine.Inspect();
            Assert(state.Languages.Length == 3 && state.Languages.Contains("EN.zip"), "discovery");
            Assert(!Directory.Exists(f.Store), "inspection wrote state");
            f.Engine.Restore();
            Assert(!Directory.Exists(f.Store), "empty restore wrote state");
        });
        Test("Apply really renames files, preserving Windows file identities", f => {
            string chineseId = FileId(f.File("CHS")), englishId = FileId(f.File("EN"));
            f.Engine.Apply("EN.zip");
            f.AssertActive("EN");
            Assert(FileId(f.File("EN")) == chineseId && FileId(f.File("CHS")) == englishId, "ZIPs were copied instead of renamed");
            f.Engine.Restore();
            f.AssertOriginal();
            Assert(FileId(f.File("CHS")) == chineseId && FileId(f.File("EN")) == englishId, "restore did not rename");
        });
        Test("Repeat apply is idempotent", f => {
            f.Engine.Apply("EN.zip");
            string id = FileId(f.File("CHS"));
            f.Engine.Apply("EN.zip");
            f.AssertActive("EN");
            Assert(FileId(f.File("CHS")) == id, "same language changed file");
        });
        Test("Switch EN to JP restores previous pair in one transaction", f => {
            f.Engine.Apply("EN.zip");
            f.Engine.Apply("JP.zip");
            f.AssertActive("JP");
            Assert(f.Hash("EN") == f.English, "previous target was not restored");
            f.Engine.Restore();
            f.AssertOriginal();
        });
        Test("Identical ZIP bytes still exchange file identities", f => {
            string chs = FileId(f.File("CHS")), cht = FileId(f.File("CHT"));
            f.Engine.Apply("CHT.zip");
            Assert(FileId(f.File("CHS")) == cht && FileId(f.File("CHT")) == chs, "identical hashes skipped rename");
            f.Engine.Restore();
            f.AssertOriginal();
        });
        Test("Reject CHS and traversal targets without writing", f => {
            Reject(() => f.Engine.Apply("CHS.zip"));
            Reject(() => f.Engine.Apply("..\\EN.zip"));
            Assert(!Directory.Exists(f.Store), "invalid targets created state");
            f.AssertOriginal();
        });
        Test("Invalid ZIP rejected before backup", f => {
            File.WriteAllText(f.File("EN"), "not a ZIP");
            Reject(() => f.Engine.Apply("EN.zip"));
            Assert(!Directory.Exists(f.Store), "invalid ZIP created state");
            Assert(f.Hash("CHS") == f.Chinese, "CHS modified");
        });
        Test("Locked ZIP fails without changing either file", f => {
            using (var locked = new FileStream(f.File("EN"), FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => f.Engine.Apply("EN.zip"));
            f.AssertOriginal();
        });
        Test("External update blocks apply and restore", f => {
            f.Engine.Apply("EN.zip");
            Fixture.WriteZip(f.File("EN"), "updated chinese");
            string updated = f.Hash("EN");
            Assert(f.Engine.Inspect().Problem != null, "missed external change");
            Reject(() => f.Engine.Restore());
            Reject(() => f.Engine.Apply("JP.zip"));
            Assert(f.Hash("EN") == updated, "overwrote game update");
        });
        Test("Corrupt backup blocks mutation", f => {
            f.Engine.Apply("EN.zip");
            string backup = Directory.GetFiles(Path.Combine(f.Store, "generations"), "CHS.zip", SearchOption.AllDirectories).Single();
            File.WriteAllText(backup, "corrupt");
            Reject(() => f.Engine.Restore());
            f.AssertActive("EN");
        });
        Test("New baseline archives old records and uses current game version", f => {
            f.Engine.Apply("EN.zip");
            Fixture.WriteZip(f.File("CHS"), "new Chinese");
            Fixture.WriteZip(f.File("EN"), "new English");
            string chinese = f.Hash("CHS"), english = f.Hash("EN");
            f.Engine.Rebaseline();
            Assert(Directory.GetFiles(f.Store, "retired-*.json").Length == 1, "missing archive");
            f.Engine.Apply("EN.zip");
            Assert(f.Hash("CHS") == english && f.Hash("EN") == chinese, "old baseline reused");
            f.Engine.Restore();
            Assert(f.Hash("CHS") == chinese && f.Hash("EN") == english, "new baseline restore failed");
        });
        Test("Damaged manifest is blocked, then explicitly archived", f => {
            Directory.CreateDirectory(f.Store);
            File.WriteAllText(Path.Combine(f.Store, "manifest.json"), "{}");
            Assert(f.Engine.Inspect().Problem != null, "bad manifest accepted");
            Reject(() => f.Engine.Apply("EN.zip"));
            f.Engine.Rebaseline();
            f.Engine.Apply("EN.zip");
            f.AssertActive("EN");
        });
        Test("Concurrent operation is rejected by directory mutex", f => {
            using (var reached = new ManualResetEvent(false))
            using (var resume = new ManualResetEvent(false))
            {
                f.Engine.AfterReplace = count => { if (count == 1) { reached.Set(); if (!resume.WaitOne(10000)) throw new Exception("timeout"); } };
                Task work = Task.Run(() => f.Engine.Apply("EN.zip"));
                Assert(reached.WaitOne(10000), "worker did not reach rename");
                try { Reject(() => new SwapEngine(f.Root).Inspect()); }
                finally { resume.Set(); }
                work.GetAwaiter().GetResult();
                f.AssertActive("EN");
            }
        });
        for (int i = 1; i <= 4; i++)
        {
            int point = i;
            Test("Automatic rollback after rename " + point, f => {
                f.Engine.AfterReplace = count => { if (count == point) throw new IOException("simulated failure"); };
                Reject(() => f.Engine.Apply("EN.zip"));
                f.AssertOriginal();
                Assert(!f.Engine.Inspect().Pending, "journal left after successful rollback");
            });
            Test("Process crash recovery after rename " + point, f => {
                Crash(f, point, "EN.zip");
                Assert(f.Engine.Inspect().Pending, "missing crash journal");
                Reject(() => f.Engine.Apply("JP.zip"));
                new SwapEngine(f.Root).Recover(); // Restart must work even while CHS.zip is temporarily absent.
                f.AssertOriginal();
                Assert(!f.Engine.Inspect().Pending, "journal left after recovery");
                f.Engine.Apply("JP.zip");
                f.AssertActive("JP");
            });
        }
        for (int i = 1; i <= 6; i++)
        {
            int point = i;
            Test("Switch crash preserves previous EN state at rename " + point, f => {
                f.Engine.Apply("EN.zip");
                Crash(f, point, "JP.zip");
                f.Engine.Recover();
                f.AssertActive("EN");
                Assert(f.Engine.Inspect().ActiveLanguage == "EN.zip", "lost previous state");
                f.Engine.Restore();
                f.AssertOriginal();
            });
        }
        Test("Recovery refuses external edits without overwriting them", f => {
            Crash(f, 3, "EN.zip");
            Fixture.WriteZip(f.File("CHS"), "external modification after crash");
            string external = f.Hash("CHS");
            Reject(() => f.Engine.Recover());
            Assert(f.Hash("CHS") == external && f.Engine.Inspect().Pending, "external edit overwritten or journal lost");
        });
        Test("Forged journal path is rejected", f => {
            Directory.CreateDirectory(f.Store);
            File.WriteAllText(Path.Combine(f.Store, "pending.json"), "{\"Id\":\"../../escape\",\"Changes\":[]}");
            Reject(() => f.Engine.Recover());
            f.AssertOriginal();
        });
        Console.WriteLine("\n{0} passed; {1} failed.", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static void Crash(Fixture f, int point, string target)
    {
        string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var info = new ProcessStartInfo(executable, "--crash \"" + f.Root + "\" " + point + " " + target) { UseShellExecute = false, CreateNoWindow = true };
        using (Process process = Process.Start(info))
        {
            if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("crash subprocess timed out"); }
            Assert(process.ExitCode == 73, "crash subprocess failed: " + process.ExitCode);
        }
    }

    private static void Test(string name, Action<Fixture> test)
    {
        using (var fixture = new Fixture())
        {
            try { test(fixture); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException || ex is InvalidDataException) return; throw; }
        throw new Exception("Expected operation to be rejected");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly SwapEngine Engine;
        internal readonly string Chinese, English, Japanese;
        internal string Store { get { return Path.Combine(Root, SwapEngine.StoreName); } }
        internal Fixture()
        {
            Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            WriteZip(File("CHS"), "Chinese 中文");
            WriteZip(File("EN"), "English");
            WriteZip(File("JP"), "Japanese 日本語");
            System.IO.File.Copy(File("CHS"), File("CHT"));
            Chinese = Hash("CHS"); English = Hash("EN"); Japanese = Hash("JP");
            Engine = new SwapEngine(Root);
        }
        internal string File(string code) { return Path.Combine(Root, code + ".zip"); }
        internal string Hash(string code) { return SwapEngine.Hash(File(code)); }
        internal void AssertOriginal() { Assert(Hash("CHS") == Chinese && Hash("EN") == English && Hash("JP") == Japanese && Hash("CHT") == Chinese, "original hashes differ"); }
        internal void AssertActive(string target)
        {
            Assert(Hash(target) == Chinese, "target is not Chinese");
            Assert(Hash("CHS") == (target == "EN" ? English : target == "JP" ? Japanese : Chinese), "CHS is not target language");
            Assert(target == "EN" || Hash("EN") == English, "EN was changed unexpectedly");
            Assert(target == "JP" || Hash("JP") == Japanese, "JP was changed unexpectedly");
        }
        internal static void WriteZip(string path, string text)
        {
            using (var stream = new FileStream(path, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("strings.str").Open())) writer.Write(text);
        }
        public void Dispose()
        {
            string boundary = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(Root, true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    private static string FileId(string path)
    {
        using (var stream = System.IO.File.OpenRead(path))
        {
            FileInformation information;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out information)) throw new IOException("Cannot read Windows file identity");
            return information.Volume + ":" + information.IndexHigh + ":" + information.IndexLow;
        }
    }
}

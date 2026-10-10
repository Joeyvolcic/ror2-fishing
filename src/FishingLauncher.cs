// FISHING.exe - loads the fishing visuals (FishingClient.dll), the F2 request panel (ReqPanelClient.dll)
// the fish (FishFxClient.dll), the fishing rod (NoWeaponsClient.dll) and the fish items (FishItemsClient.dll),
// both embedded in this exe, into the running
// Risk of Rain 2 on THIS PC. Nothing is installed: closing the game removes it.
// Technique: the game runs on Mono, so we start a tiny thread in the game that calls Mono's own
// mono_domain_assembly_open -> mono_class_from_name -> mono_runtime_invoke(<Class>.Run), once per payload.
// Self-updating: on start it reads latest.json from the newest GitHub release of Joeyvolcic/ror2-fishing and, if that
// release is newer, downloads it, checks its SHA-256, swaps itself for it and restarts (see Updater below).
// Flags: --nopause (don't wait for a key), --update-only (only check/update, then exit), --no-update (skip the check).
// Build: see build.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

static class FishingLauncher
{
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32")] static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, IntPtr stack, IntPtr start, IntPtr param, uint flags, IntPtr id);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);

    static IntPtr proc;
    static Dictionary<string, long> ex;
    static bool pause = true;

    static byte[] Read(long addr, int n)
    {
        var buf = new byte[n]; IntPtr got;
        if (!ReadProcessMemory(proc, (IntPtr)addr, buf, (IntPtr)n, out got)) throw new Exception("could not read game memory at 0x" + addr.ToString("x"));
        return buf;
    }

    static void Write(long addr, byte[] data)
    {
        IntPtr got;
        if (!WriteProcessMemory(proc, (IntPtr)addr, data, (IntPtr)data.Length, out got)) throw new Exception("could not write game memory");
    }

    static Dictionary<string, long> Exports(long b)
    {
        long nt = b + BitConverter.ToInt32(Read(b + 0x3C, 4), 0);
        int rva = BitConverter.ToInt32(Read(nt + 24 + 112, 4), 0);
        byte[] d = Read(b + rva, 40);
        int n = BitConverter.ToInt32(d, 0x18), funcs = BitConverter.ToInt32(d, 0x1C), names = BitConverter.ToInt32(d, 0x20), ords = BitConverter.ToInt32(d, 0x24);
        byte[] nameRvas = Read(b + names, 4 * n), ordTable = Read(b + ords, 2 * n);
        var result = new Dictionary<string, long>();
        for (int i = 0; i < n; i++)
        {
            byte[] raw = Read(b + BitConverter.ToInt32(nameRvas, 4 * i), 128);
            int len = Array.IndexOf(raw, (byte)0);
            string name = Encoding.ASCII.GetString(raw, 0, len < 0 ? raw.Length : len);
            int ord = BitConverter.ToUInt16(ordTable, 2 * i);
            result[name] = b + BitConverter.ToInt32(Read(b + funcs + 4 * ord, 4), 0);
        }
        return result;
    }

    static void Say(string s) { Console.WriteLine(" " + s); }

    static int Done(int code)
    {
        if (pause)
        {
            Console.WriteLine();
            Console.Write(" Press any key to close this window...");
            try { Console.ReadKey(true); } catch { }
        }
        return code;
    }

    static int Main(string[] args)
    {
        bool updateOnly = false, noUpdate = false;
        foreach (string a in args)
        {
            if (a == "--nopause") pause = false;
            if (a == "--update-only") { updateOnly = true; pause = false; }
            if (a == "--no-update") noUpdate = true;
        }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.Title = "Risk of Rain 2 - Fishing mod v" + Updater.Version; } catch { }
        Console.WriteLine();
        Say("Risk of Rain 2 - fishing mod v" + Updater.Version + " (guest client)");
        Updater.CleanupOld();
        if (!noUpdate && Updater.CheckAndUpdate(args)) return 0;   // a newer FISHING.exe was started in our place
        if (updateOnly) { Say("--update-only: done."); return 0; }
        Console.WriteLine();
        Say("Draws the fishing rods, lines, bobbers and water that the host's fishing mod announces in chat,");
        Say("and adds the F2 'Requests for Claude' panel.");
        Console.WriteLine();
        try { return Done(Inject()); }
        catch (Exception e)
        {
            Say("ERROR: " + e.Message);
            return Done(1);
        }
    }

    static int Inject()
    {
        Process[] ps = Process.GetProcessesByName("Risk of Rain 2");
        if (ps.Length == 0)
        {
            Say("Risk of Rain 2 is not running.");
            Say("Start the game (joining the lobby first is fine), then run FISHING.exe again.");
            return 1;
        }
        Process game = ps[0];
        Say("Found Risk of Rain 2 (process " + game.Id + ").");

        proc = OpenProcess(0x1F0FFF, false, game.Id);
        if (proc == IntPtr.Zero) throw new Exception("could not open the game process (error " + Marshal.GetLastWin32Error() + "). If the game runs as administrator, run this as administrator too.");

        long monoBase = 0;
        foreach (ProcessModule m in game.Modules)
            if (m.ModuleName.ToLower() == "mono-2.0-bdwgc.dll") monoBase = m.BaseAddress.ToInt64();
        if (monoBase == 0) { Say("The game's Mono runtime wasn't found - is the game still starting up? Wait for the main menu and try again."); return 1; }
        ex = Exports(monoBase);

        // 1) fishing visuals
        string logPath = Path.Combine(Path.GetTempPath(), "ror2_fishing_client.log");
        string news;
        Say("Loading the fishing visuals into the game...");
        int rc = LoadPayload("FishingClient.dll", "FishingClient", "ror2_fishing_client_", logPath, out news);
        Console.WriteLine();
        if (rc != 0) { }
        else if (news.Contains("host detected"))
        {
            Say("Loaded, but this game is the HOST - the host already draws the fishing itself,");
            Say("so the guest visuals stay switched off here. (Run this on the guests' PCs.)");
        }
        else if (news.Contains("already running") || news.Contains("installed"))
        {
            Say("Fishing visuals loaded into Risk of Rain 2 ✔" + (news.Contains("already running") ? "  (they were already loaded - nothing changed)" : ""));
            Say("Type 'fish' in chat or left-click as usual - you'll now see everyone's rods, lines,");
            Say("bobbers and the water.");
        }
        else
        {
            Say("Loaded, but the fishing visuals couldn't start. Details: " + logPath);
            if (news.Length > 0) Console.WriteLine(news);
            rc = 1;
        }

        // 2) "Requests for Claude" panel
        Console.WriteLine();
        string reqLog = Path.Combine(Path.GetTempPath(), "ror2_requests.log");
        string reqNews;
        Say("Loading the request panel into the game...");
        int rc2 = LoadPayload("ReqPanelClient.dll", "ReqPanelClient", "ror2_reqpanel_client_", reqLog, out reqNews);
        if (rc2 != 0) { }
        else if (reqNews.Contains("host detected"))
            Say("This game is the HOST - the host runs its own request panel, nothing changed here.");
        else if (reqNews.Contains("already running") || reqNews.Contains("installed"))
        {
            Say("Request panel loaded ✔" + (reqNews.Contains("already running") ? "  (it was already loaded - nothing changed)" : ""));
            Say("Press F2 in the game, type a feature request for Claude, press Enter. F2 or Esc closes it.");
        }
        else
        {
            Say("Loaded, but the request panel couldn't start. Details: " + reqLog);
            if (reqNews.Length > 0) Console.WriteLine(reqNews);
            rc2 = 1;
        }
        // 3) fish in the lake + trophy fish on catches
        Console.WriteLine();
        string fishLog = Path.Combine(Path.GetTempPath(), "ror2_fishfx.log");
        string fishNews;
        Say("Loading the fish into the game...");
        int rc3 = LoadPayload("FishFxClient.dll", "FishFxClient", "ror2_fishfx_client_", fishLog, out fishNews);
        if (rc3 != 0) { }
        else if (fishNews.Contains("already running") || fishNews.Contains("installed"))
        {
            Say("Fish loaded ✔" + (fishNews.Contains("already running") ? "  (they were already loaded - nothing changed)" : ""));
            Say("Fish swim in the lake around you, and every catch pops up as a flopping trophy fish.");
        }
        else
        {
            Say("Loaded, but the fish couldn't start. Details: " + fishLog);
            if (fishNews.Length > 0) Console.WriteLine(fishNews);
            rc3 = 1;
        }
        // 4) no weapons: all skills removed, left click (M1) = fishing rod
        Console.WriteLine();
        string nwLog = Path.Combine(Path.GetTempPath(), "ror2_noweapons.log");
        string nwNews;
        Say("Swapping your weapons for the fishing rod...");
        int rc4 = LoadPayload("NoWeaponsClient.dll", "NoWeaponsClient", "ror2_noweapons_client_", nwLog, out nwNews);
        if (rc4 != 0) { }
        else if (nwNews.Contains("already running") || nwNews.Contains("installed"))
        {
            Say("Fishing rod equipped ✔" + (nwNews.Contains("already running") ? "  (it was already loaded - nothing changed)" : ""));
            Say("Weapons are removed: LEFT CLICK = fishing rod (cast, and click again when it bites).");
            Say("RIGHT CLICK = Rod Whip: hit the player in front of you (1 damage) and snap their line back.");
            Say("Shift / R do nothing. Equipment (Q), jump and sprint still work.");
        }
        else
        {
            Say("Loaded, but the fishing rod couldn't start. Details: " + nwLog);
            if (nwNews.Length > 0) Console.WriteLine(nwNews);
            rc4 = 1;
        }
        // 5) fish items: caught fish show as fish in the item bar + small movement perks for your own character
        Console.WriteLine();
        string fiLog = Path.Combine(Path.GetTempPath(), "ror2_fishitems.log");
        string fiNews;
        Say("Turning your item bar into a fish bar...");
        int rc5 = LoadPayload("FishItemsClient.dll", "FishItemsClient", "ror2_fishitems_client_", fiLog, out fiNews);
        if (rc5 != 0) { }
        else if (fiNews.Contains("host detected"))
            Say("This game is the HOST - the host runs its own fish-items mod, nothing changed here.");
        else if (fiNews.Contains("already running") || fiNews.Contains("installed"))
        {
            Say("Fish items loaded ✔" + (fiNews.Contains("already running") ? "  (they were already loaded - nothing changed)" : ""));
            Say("Every fish you catch now shows in your item bar as that fish, and gives a small movement perk.");
        }
        else
        {
            Say("Loaded, but the fish items couldn't start. Details: " + fiLog);
            if (fiNews.Length > 0) Console.WriteLine(fiNews);
            rc5 = 1;
        }
        Console.WriteLine();
        Say("You can close this window; closing the game removes everything.");
        return rc != 0 ? rc : rc2 != 0 ? rc2 : rc3 != 0 ? rc3 : rc4 != 0 ? rc4 : rc5;
    }

    // Writes the embedded payload to a unique temp file and runs <cls>.Run() inside the game.
    // Returns 0 if Run() returned normally; 'news' = what Run() appended to its log file.
    static int LoadPayload(string resource, string cls, string tempPrefix, string logPath, out string news)
    {
        news = "";
        string dll = Path.Combine(Path.GetTempPath(), tempPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dll");
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (s == null) throw new Exception("the payload " + resource + " is missing from this exe");
            byte[] bytes = new byte[s.Length];
            int off0 = 0; while (off0 < bytes.Length) { int r = s.Read(bytes, off0, bytes.Length - off0); if (r <= 0) break; off0 += r; }
            File.WriteAllBytes(dll, bytes);
        }
        long logStart = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

        long mem = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)0x2000, 0x3000, 0x40).ToInt64();
        if (mem == 0) throw new Exception("could not allocate memory in the game");
        long data = mem + 0x1000;
        string[] slotNames = { "root", "thread", "asm", "img", "klass", "method", "exc", "ret" };
        var slots = new Dictionary<string, long>();
        for (int i = 0; i < slotNames.Length; i++) slots[slotNames[i]] = data + 8 * i;
        Write(data, new byte[0x40]);

        var strs = new Dictionary<string, long>();
        long off = data + 0x100;
        foreach (var kv in new[] { new[] { "path", dll }, new[] { "ns", "" }, new[] { "cls", cls }, new[] { "run", "Run" } })
        {
            byte[] b = Encoding.UTF8.GetBytes(kv[1] + "\0");
            Write(off, b); strs[kv[0]] = off; off += (b.Length + 7) & ~7;
        }

        // x64 shellcode: attach a Mono thread, open the assembly, find <cls>.Run, invoke it, detach
        var code = new List<byte>();
        var fix = new List<int>();
        Action<byte[], long> imm = (op, v) => { code.AddRange(op); code.AddRange(BitConverter.GetBytes(v)); };
        Action<string> call = fn => { imm(new byte[] { 0x48, 0xB8 }, ex[fn]); code.AddRange(new byte[] { 0xFF, 0xD0 }); };
        Action<string> store = s => { imm(new byte[] { 0x49, 0xBA }, slots[s]); code.AddRange(new byte[] { 0x49, 0x89, 0x02 }); };
        Action<string> loadRcx = s => { imm(new byte[] { 0x49, 0xBA }, slots[s]); code.AddRange(new byte[] { 0x49, 0x8B, 0x0A }); };
        Action jzEnd = () => { code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x84 }); fix.Add(code.Count); code.AddRange(new byte[4]); };
        byte[] movRcxRax = { 0x48, 0x89, 0xC1 };

        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x48 });
        call("mono_get_root_domain"); store("root");
        code.AddRange(movRcxRax); call("mono_thread_attach"); store("thread");
        loadRcx("root"); imm(new byte[] { 0x48, 0xBA }, strs["path"]); call("mono_domain_assembly_open"); store("asm"); jzEnd();
        code.AddRange(movRcxRax); call("mono_assembly_get_image"); store("img"); jzEnd();
        code.AddRange(movRcxRax); imm(new byte[] { 0x48, 0xBA }, strs["ns"]); imm(new byte[] { 0x49, 0xB8 }, strs["cls"]);
        call("mono_class_from_name"); store("klass"); jzEnd();
        code.AddRange(movRcxRax); imm(new byte[] { 0x48, 0xBA }, strs["run"]); code.AddRange(new byte[] { 0x45, 0x31, 0xC0 });
        call("mono_class_get_method_from_name"); store("method"); jzEnd();
        code.AddRange(movRcxRax); code.AddRange(new byte[] { 0x31, 0xD2, 0x45, 0x31, 0xC0 }); imm(new byte[] { 0x49, 0xB9 }, slots["exc"]);
        call("mono_runtime_invoke"); store("ret");
        int end = code.Count;
        foreach (int p in fix)
        {
            byte[] rel = BitConverter.GetBytes(end - (p + 4));
            for (int i = 0; i < 4; i++) code[p + i] = rel[i];
        }
        loadRcx("thread"); code.AddRange(new byte[] { 0x48, 0x85, 0xC9, 0x74, 0x0C }); call("mono_thread_detach");
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x48, 0x31, 0xC0, 0xC3 });
        Write(mem, code.ToArray());

        IntPtr t = CreateRemoteThread(proc, IntPtr.Zero, IntPtr.Zero, (IntPtr)mem, IntPtr.Zero, 0, IntPtr.Zero);
        if (t == IntPtr.Zero) throw new Exception("could not start the loader thread in the game");
        uint wait = WaitForSingleObject(t, 15000);
        CloseHandle(t);
        if (wait != 0) { Say("The game didn't respond within 15 seconds (is it frozen or on a loading screen?). Try again in a moment."); return 1; }
        byte[] res = Read(data, 64);
        VirtualFreeEx(proc, (IntPtr)mem, IntPtr.Zero, 0x8000);
        try { File.Delete(dll); } catch { }   // usually still in use by the game; it's just a temp file

        if (BitConverter.ToInt64(res, 16) == 0) { Say("ERROR: the game could not load " + resource + "."); return 1; }
        if (BitConverter.ToInt64(res, 40) == 0) { Say("ERROR: " + resource + " loaded but its entry point was not found."); return 1; }
        if (BitConverter.ToInt64(res, 48) != 0) { Say("ERROR: " + cls + " hit an error while starting. Details: " + logPath); return 1; }

        // Run() logs what it decided; return the new lines
        try
        {
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(logStart, SeekOrigin.Begin);
                news = new StreamReader(fs).ReadToEnd();
            }
        }
        catch { }
        return 0;
    }
}

// ---------------------------------------------------------------- self-updater
static class Updater
{
    public const string Version = "2.0";
    const string Repo = "Joeyvolcic/ror2-fishing";
    const string LatestJson = "https://github.com/" + Repo + "/releases/latest/download/latest.json";
    const string AllowedPrefix = "https://github.com/" + Repo + "/releases/download/";

    static void Say(string s) { Console.WriteLine(" " + s); }

    class Web : WebClient
    {
        public int TimeoutMs = 5000;
        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest r = base.GetWebRequest(address);
            r.Timeout = TimeoutMs;
            HttpWebRequest h = r as HttpWebRequest;
            if (h != null) { h.ReadWriteTimeout = TimeoutMs; h.UserAgent = "ror2-fishing-updater/" + Version; h.AllowAutoRedirect = true; }
            return r;
        }
    }

    static string ExePath() { return Process.GetCurrentProcess().MainModule.FileName; }
    static string Sibling(string exe, string suffix) { return Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + suffix); }

    // Deletes FISHING.old.exe left by the previous update (that process may still be exiting: retry quietly).
    public static void CleanupOld()
    {
        string old;
        try { old = Sibling(ExePath(), ".old.exe"); } catch { return; }
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); } catch { System.Threading.Thread.Sleep(300); }
        }
    }

    static string JsonField(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\/", "/").Replace("\\\"", "\"") : null;
    }

    // major.minor[.patch] -> 3 numbers; null if it can't be parsed
    static int[] Parse(string v)
    {
        if (v == null) return null;
        string[] p = v.Trim().TrimStart('v', 'V').Split('.');
        if (p.Length < 1 || p.Length > 3) return null;
        int[] r = new int[3];
        for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i], out r[i]) || r[i] < 0) return null;
        return r;
    }

    public static int Compare(string a, string b)
    {
        int[] x = Parse(a), y = Parse(b);
        if (x == null || y == null) return 0;
        for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    static string Sha256(string file)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(file))
        {
            var sb = new StringBuilder();
            foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    // true = a newer version was installed and started; the caller should exit
    public static bool CheckAndUpdate(string[] args)
    {
        string json;
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            using (var w = new Web { TimeoutMs = 5000 }) json = w.DownloadString(LatestJson);
        }
        catch { Say("Couldn't check for updates - continuing with v" + Version + "."); return false; }

        string ver = JsonField(json, "version"), url = JsonField(json, "url"), sha = JsonField(json, "sha256"), notes = JsonField(json, "notes");
        if (Parse(ver) == null || url == null || sha == null) { Say("Couldn't check for updates (unexpected latest.json) - continuing with v" + Version + "."); return false; }
        if (Compare(ver, Version) <= 0) { Say("You have the latest version (v" + Version + ")."); return false; }
        if (!url.StartsWith(AllowedPrefix, StringComparison.Ordinal) || url.Contains("..") || url.Contains("?") || url.Contains("#"))
        { Say("Update v" + ver + " ignored: its download link isn't a release of github.com/" + Repo + "."); return false; }

        Say("Updating to v" + ver + (string.IsNullOrEmpty(notes) ? "" : " (" + notes + ")") + "...");
        string exe, neu, old;
        try { exe = ExePath(); neu = Sibling(exe, ".new.exe"); old = Sibling(exe, ".old.exe"); }
        catch (Exception e) { Say("Couldn't update (" + e.Message + ") - continuing with v" + Version + "."); return false; }
        try
        {
            if (File.Exists(neu)) File.Delete(neu);
            using (var w = new Web { TimeoutMs = 120000 }) w.DownloadFile(url, neu);
        }
        catch (Exception e)
        {
            try { File.Delete(neu); } catch { }
            Say("Couldn't download the update (" + e.Message + ") - continuing with v" + Version + ".");
            return false;
        }
        string got;
        try { got = Sha256(neu); } catch { got = ""; }
        if (!string.Equals(got, sha.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(neu); } catch { }
            Say("WARNING: the downloaded update failed its SHA-256 check and was deleted. Continuing with v" + Version + ".");
            return false;
        }
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);                       // Windows allows renaming a running exe
            try { File.Move(neu, exe); }
            catch { File.Move(old, exe); throw; }      // put the current version back
        }
        catch (Exception e)
        {
            try { File.Delete(neu); } catch { }
            Say("Couldn't replace FISHING.exe (" + e.Message + ") - continuing with v" + Version + ".");
            return false;
        }
        try
        {
            var sb = new StringBuilder();
            foreach (string a in args) sb.Append(sb.Length > 0 ? " " : "").Append(Quote(a));
            Say("Updated to v" + ver + " - starting it...");
            Process.Start(new ProcessStartInfo(exe, sb.ToString()) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory });
            return true;
        }
        catch (Exception e)
        {
            Say("Updated to v" + ver + ", but couldn't start it (" + e.Message + "). Please run FISHING.exe again.");
            return true;
        }
    }
}
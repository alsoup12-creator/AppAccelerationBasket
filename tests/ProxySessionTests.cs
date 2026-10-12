using System;
using System.IO;
class ProxySessionTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static int Main(string[] args)
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (args.Length > 0) { Console.WriteLine("Read-only WinINet flags=" + WinInetProxy.Read().Flags); return 0; }
            string path = Path.Combine(directory, "lease.json");
            ProxySnapshot current = new ProxySnapshot { Flags = 11, Server = "local", Bypass = "local", Script = "" };
            int writes = 0;
            var session = new SystemProxySession(path, () => current, f => {
                writes++;
                current = new ProxySnapshot { Flags = f, Server = current.Server, Bypass = current.Bypass, Script = current.Script };
            }, (a, d) => { });
            Check(session.Enter() && current.Flags == 1 && session.IsDedicated(current), "enter");
            Check(!session.Enter() && writes == 1, "reuse should not overwrite original backup");
            Check(session.Restore("user-click") == ProxyRestoreResult.Restored && current.Flags == 11 && !session.Active, "restore");
            for (int i = 0; i < 3; i++)
            {
                Check(session.Enter() && session.IsDedicated(current), "resume cycle");
                Check(session.Restore("user-click") == ProxyRestoreResult.Restored && current.Flags == 11, "restore cycle");
            }
            Console.WriteLine("enter / restore / resume repeated cycles PASS");

            current = new ProxySnapshot { Flags = 5, Server = "new-local", Bypass = "new-bypass", Script = "http://local/proxy.pac" };
            var latest = session.ReadCurrent();
            Check(session.Enter() && current.Flags == 1, "PAC enter");
            Check(session.Restore("user-click") == ProxyRestoreResult.Restored && current.Same(latest), "must restore latest settings including PAC");
            Console.WriteLine("fresh backup after external edits and PAC restoration PASS");

            session.Enter();
            var recovered = new SystemProxySession(path, () => current, f => { current.Flags = f; }, (a, d) => { });
            Check(recovered.IsDedicated(current), "lease-backed dedicated status");
            Check(recovered.Restore("crash-recovery") == ProxyRestoreResult.Restored && current.Same(latest), "crash recovery");
            Console.WriteLine("crash recovery PASS");

            session.Enter();
            current.Server = "user-new-proxy";
            Check(!session.IsDedicated(current), "external edits must not display dedicated state");
            Check(session.Restore("user-click") == ProxyRestoreResult.ExternalChangePreserved && current.Flags == 1 && current.Server == "user-new-proxy" && !session.Active, "external change");
            Console.WriteLine("external change preserved and reported PASS");

            int before = writes;
            Check(!session.Enter() && !session.Active && session.IsDedicated(current), "direct no-op should still show requested mode");
            Check(session.Restore("user-click") == ProxyRestoreResult.NoSession && !session.IsDedicated(current) && writes == before, "direct restore must not invent proxy settings");
            Console.WriteLine("already direct mode selection without system writes PASS");

            current.Flags = 3;
            var broken = new SystemProxySession(path, () => current, f => {
                if (f == 1) { current = current.Direct(); throw new Exception("simulated partial write"); }
                current.Flags = f;
            }, (a, d) => { });
            try { broken.Enter(); throw new Exception("missing failure"); }
            catch (Exception e) { if (e.Message != "simulated partial write") throw; }
            Check(current.Flags == 3 && !broken.Active && !broken.IsDedicated(current), "partial rollback");
            Console.WriteLine("partial failure rollback PASS");

            session.Enter();
            var restoreFailure = new SystemProxySession(path, () => current, f => { throw new IOException("simulated restore failure"); }, (a, d) => { });
            try { restoreFailure.Restore("user-click"); throw new Exception("missing restore failure"); }
            catch (IOException) { }
            Check(restoreFailure.Active && restoreFailure.IsDedicated(current), "failed restore must retain recovery record and current status");
            session.Restore("cleanup");
            Console.WriteLine("failed restore retains recovery record PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
        finally
        {
            if (Directory.Exists(directory))
            {
                Check(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), StringComparison.OrdinalIgnoreCase), "cleanup outside test output directory");
                Directory.Delete(directory, true);
            }
        }
    }
}

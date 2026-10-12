using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class BasketUiTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static T Field<T>(BasketForm form, string name)
    {
        return (T)typeof(BasketForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
    }
    static void Set(BasketForm form, string name, object value)
    {
        typeof(BasketForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value);
    }
    static void Refresh(BasketForm form)
    {
        typeof(BasketForm).GetMethod("RefreshEverything", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
    }
    static void Click(Button button)
    {
        Check(button.Enabled, "button unexpectedly disabled: " + button.Text);
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
    }

    [STAThread]
    static int Main()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-" + Guid.NewGuid().ToString("N"));
        TcpListener listener = null;
        try
        {
            var realBefore = WinInetProxy.Read();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            bool acceptHandshake = true;
            var server = new Thread(() => {
                try
                {
                    while (true)
                    {
                        using (var client = listener.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        {
                            stream.ReadTimeout = 3000;
                            stream.Read(new byte[1024], 0, 1024);
                            string response = Volatile.Read(ref acceptHandshake) ? "HTTP/1.1 200 Connection established\r\n\r\n" : "HTTP/1.1 503 Unavailable\r\n\r\n";
                            byte[] bytes = Encoding.ASCII.GetBytes(response);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            server.IsBackground = true;
            server.Start();

            ProxySnapshot current = new ProxySnapshot { Flags = 11, Server = "local", Bypass = "local", Script = "" };
            int writes = 0;
            var session = new SystemProxySession(Path.Combine(directory, "lease.json"), () => current, f => {
                writes++;
                current = new ProxySnapshot { Flags = f, Server = current.Server, Bypass = current.Bypass, Script = current.Script };
            }, (a, d) => { });
            using (var form = new BasketForm(directory, session, (a, d) => { }))
            {
                Field<System.Windows.Forms.Timer>(form, "refreshTimer").Stop();
                Field<NumericUpDown>(form, "portBox").Value = port;
                var resume = Field<Button>(form, "resumeButton");
                var restore = Field<Button>(form, "restoreButton");
                var system = Field<Label>(form, "systemValue");
                var feedback = Field<Label>(form, "feedback");
                Check(resume.Enabled && !restore.Enabled && system.Text == "系统代理已开启", "initial system state");
                Check(Field<List<BasketApp>>(form, "apps").Count == 0, "test must need no selected app");
                Volatile.Write(ref acceptHandshake, false);
                Click(resume);
                Check(current.Flags == 11 && writes == 0 && !session.Active && resume.Enabled, "failed handshake must not change system mode");
                Console.WriteLine("UI proxy unavailable keeps original network PASS");
                Volatile.Write(ref acceptHandshake, true);
                for (int i = 0; i < 3; i++)
                {
                    Click(resume);
                    Check(current.Flags == 1 && restore.Enabled && !resume.Enabled && system.Text.StartsWith("专用加速模式"), "dedicated UI state");
                    Click(restore);
                    Check(current.Flags == 11 && !restore.Enabled && resume.Enabled && system.Text == "系统代理已开启", "restored UI state");
                }
                Check(Field<Dictionary<string, int>>(form, "launchedProcessIds").Count == 0 && Field<Dictionary<string, string>>(form, "launchedModes").Count == 0, "mode buttons must not launch apps");
                Console.WriteLine("UI two-button cycles with no selected app or app launches PASS");

                Click(resume);
                current.Server = "external-vpn-change";
                Refresh(form);
                Check(!system.Text.StartsWith("专用加速模式") && resume.Enabled && restore.Enabled, "external change must update status");
                Click(restore);
                Check(current.Server == "external-vpn-change" && feedback.Text.Contains("保留当前系统设置"), "external change feedback");
                Console.WriteLine("UI external change preservation and feedback PASS");

                int before = writes;
                Click(resume);
                Check(!session.Active && restore.Enabled && !resume.Enabled, "already direct requested mode UI");
                Click(restore);
                Check(current.Flags == 1 && writes == before && system.Text == "系统直连" && feedback.Text.Contains("原本就是直连"), "direct mode restoration UI");
                Console.WriteLine("UI direct mode without false proxy restoration PASS");

                Set(form, "launchReceiptPending", true);
                Refresh(form);
                Check(resume.Enabled, "independent mode switch must remain available after pending launch receipt");
                Set(form, "networkSwitchInProgress", true);
                Refresh(form);
                Check(!resume.Enabled && !restore.Enabled && !Field<Button>(form, "launchButton").Enabled && !Field<Button>(form, "normalLaunchButton").Enabled, "lock all mode and launch buttons while switching");
                Set(form, "networkSwitchInProgress", false);
                Refresh(form);
                Console.WriteLine("UI switching lock and pending launch recovery PASS");

                foreach (string name in new[] { "launchButton", "normalLaunchButton", "restoreButton", "resumeButton" })
                {
                    var button = Field<Button>(form, name);
                    Check(form.ClientRectangle.Contains(button.Bounds), "button out of bounds: " + name);
                    var measured = TextRenderer.MeasureText(button.Text, button.Font);
                    Check(measured.Width + 12 < button.Width && measured.Height + 10 < button.Height, "button text clipping: " + name);
                }
                Check(restore.Right < resume.Left, "mode buttons overlap");
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-16000, -16000);
                form.Show();
                Application.DoEvents();
                using (var preview = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(preview, new Rectangle(0, 0, form.Width, form.Height));
                    preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-v0.1.3.png"), ImageFormat.Png);
                }
                Console.WriteLine("UI layout and text fit PASS");
            }
            Check(realBefore.Same(WinInetProxy.Read()), "tests must not modify real system proxy");
            Console.WriteLine("Real Windows proxy unchanged PASS");
            listener.Stop();
            server.Join(3000);
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
        finally
        {
            if (listener != null) listener.Stop();
            if (Directory.Exists(directory))
            {
                Check(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), StringComparison.OrdinalIgnoreCase), "cleanup outside test output directory");
                Directory.Delete(directory, true);
            }
        }
    }
}

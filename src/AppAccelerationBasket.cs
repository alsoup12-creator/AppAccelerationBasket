using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("应用加速箩筐")]
[assembly: AssemblyDescription("Windows 应用级代理启动与分流辅助工具")]
[assembly: AssemblyCompany("alsoup12-creator")]
[assembly: AssemblyProduct("应用加速箩筐")]
[assembly: AssemblyCopyright("Copyright © 2026 alsoup12-creator")]
[assembly: AssemblyVersion("0.1.1.0")]
[assembly: AssemblyFileVersion("0.1.1.0")]
[assembly: AssemblyInformationalVersion("0.1.1")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, @"Local\AppAccelerationBasket.SingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("应用加速箩筐已经打开，请使用现有窗口。", "箩筐已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BasketForm());
        }
    }
}

internal sealed class ProxyProfile
{
    public string Preset { get; set; }
    public string Type { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
}

internal sealed class BasketApp
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public string Arguments { get; set; }
    public string WorkingDirectory { get; set; }
    public string Adapter { get; set; }
    public bool Enabled { get; set; }
}

internal sealed class SavedState
{
    public ProxyProfile Proxy { get; set; }
    public List<BasketApp> Apps { get; set; }
    public List<VpnProgram> VpnPrograms { get; set; }
    public string SelectedVpnId { get; set; }
}

internal sealed class VpnProgram
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public string Arguments { get; set; }
    public string WorkingDirectory { get; set; }
    public override string ToString() { return Name; }
}

internal sealed class AppCandidate
{
    public string Name { get; set; }
    public string Path { get; set; }
    public int ProcessId { get; set; }
    public override string ToString() { return Name + "  ·  PID " + ProcessId; }
}

internal sealed class DesktopAppCandidate
{
    public string Name { get; set; }
    public string ShortcutPath { get; set; }
    public string DesktopLocation { get; set; }
}

internal sealed class InstalledAppCandidate
{
    public string Key { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public string Arguments { get; set; }
    public string WorkingDirectory { get; set; }
    public string Source { get; set; }
    public string Compatibility { get; set; }
    public bool CanAdd { get; set; }
}

[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager { }

internal sealed class BasketForm : Form
{
    private const string CodexAppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private const string ProxyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly string stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppAccelerationBasket");
    private readonly string stateFile;
    private readonly string backupFile;
    private readonly string browserProfilesDirectory;

    private readonly ComboBox presetBox = new ComboBox();
    private readonly ComboBox vpnBox = new ComboBox();
    private readonly ComboBox typeBox = new ComboBox();
    private readonly TextBox hostBox = new TextBox();
    private readonly NumericUpDown portBox = new NumericUpDown();
    private readonly Label systemValue = new Label();
    private readonly Label proxyValue = new Label();
    private readonly Label countValue = new Label();
    private readonly Label feedback = new Label();
    private readonly ListView appList = new ListView();
    private readonly ImageList icons = new ImageList();
    private readonly Button launchButton = new Button();
    private readonly Button normalLaunchButton = new Button();
    private readonly Button restoreButton = new Button();
    private readonly Button openVpnButton = new Button();
    private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
    private readonly List<BasketApp> apps = new List<BasketApp>();
    private readonly List<VpnProgram> vpnPrograms = new List<VpnProgram>();
    private readonly Dictionary<string, int> launchedProcessIds = new Dictionary<string, int>();
    private readonly Dictionary<string, string> launchedModes = new Dictionary<string, string>();
    private string currentCodexExecutable = "";
    private bool launchInProgress;
    private bool loading;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    public BasketForm()
    {
        stateFile = Path.Combine(stateDirectory, "basket.json");
        backupFile = Path.Combine(stateDirectory, "windows-proxy-backup.json");
        browserProfilesDirectory = Path.Combine(stateDirectory, "browser-profiles");
        BuildInterface();
        LoadState();
        RefreshEverything();
        refreshTimer.Interval = 2500;
        refreshTimer.Tick += delegate { RefreshEverything(); };
        refreshTimer.Start();
    }

    private void BuildInterface()
    {
        Text = "应用加速箩筐 v0.1.1 · 应用库版";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 728);
        MinimumSize = new Size(820, 678);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        Label title = MakeLabel("应用加速箩筐", new Point(28, 20), new Size(380, 42));
        title.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold, GraphicsUnit.Point);
        title.ForeColor = Color.FromArgb(24, 31, 42);
        Controls.Add(title);

        Label subtitle = MakeLabel("单击选择一个应用，再选择专用或普通启动；运行中的非浏览器应用不会被重复启动", new Point(31, 62), new Size(820, 24));
        subtitle.ForeColor = Color.FromArgb(93, 103, 116);
        Controls.Add(subtitle);

        Panel statusPanel = new Panel();
        statusPanel.Location = new Point(28, 94);
        statusPanel.Size = new Size(844, 76);
        statusPanel.BackColor = Color.White;
        statusPanel.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(statusPanel);
        AddStatusCell(statusPanel, "系统网络", systemValue, 18, 250);
        AddStatusCell(statusPanel, "代理线路", proxyValue, 298, 310);
        AddStatusCell(statusPanel, "当前选择（一次一个）", countValue, 648, 180);

        Panel vpnPanel = new Panel();
        vpnPanel.Location = new Point(28, 184);
        vpnPanel.Size = new Size(844, 64);
        vpnPanel.BackColor = Color.White;
        vpnPanel.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(vpnPanel);

        vpnPanel.Controls.Add(MakeLabel("VPN 程序", new Point(16, 12), new Size(70, 24)));
        vpnBox.Location = new Point(88, 9);
        vpnBox.Size = new Size(286, 26);
        vpnBox.DropDownStyle = ComboBoxStyle.DropDownList;
        vpnBox.SelectedIndexChanged += delegate { if (!loading) { SaveState(); RefreshVpnButton(); } };
        vpnPanel.Controls.Add(vpnBox);

        Button addVpnButton = new Button();
        addVpnButton.Text = "添加其他 VPN…";
        addVpnButton.Location = new Point(386, 7);
        addVpnButton.Size = new Size(142, 30);
        StyleLightButton(addVpnButton);
        addVpnButton.Click += delegate { AddVpnProgram(); };
        vpnPanel.Controls.Add(addVpnButton);

        openVpnButton.Text = "打开所选 VPN";
        openVpnButton.Location = new Point(540, 7);
        openVpnButton.Size = new Size(294, 30);
        StyleLightButton(openVpnButton);
        openVpnButton.Click += delegate { OpenSelectedVpn(); };
        vpnPanel.Controls.Add(openVpnButton);

        Label vpnHint = MakeLabel("这里只负责启动或切回 VPN；节点和模式仍在 VPN 自己的界面选择。", new Point(88, 39), new Size(730, 21));
        vpnHint.ForeColor = Color.FromArgb(105, 114, 126);
        vpnPanel.Controls.Add(vpnHint);

        Panel proxyPanel = new Panel();
        proxyPanel.Location = new Point(28, 262);
        proxyPanel.Size = new Size(844, 76);
        proxyPanel.BackColor = Color.White;
        proxyPanel.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(proxyPanel);

        proxyPanel.Controls.Add(MakeLabel("代理", new Point(16, 13), new Size(46, 24)));
        presetBox.Location = new Point(62, 10);
        presetBox.Size = new Size(170, 26);
        presetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        presetBox.Items.AddRange(new object[] { "Veee", "自定义代理" });
        presetBox.SelectedIndexChanged += delegate { ApplyPreset(); };
        proxyPanel.Controls.Add(presetBox);

        typeBox.Location = new Point(244, 10);
        typeBox.Size = new Size(95, 26);
        typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        typeBox.Items.AddRange(new object[] { "HTTP", "SOCKS5" });
        typeBox.SelectedIndexChanged += delegate { MarkCustom(); RefreshEverything(); };
        proxyPanel.Controls.Add(typeBox);

        hostBox.Location = new Point(351, 10);
        hostBox.Size = new Size(165, 26);
        hostBox.TextChanged += delegate { MarkCustom(); RefreshEverything(); };
        proxyPanel.Controls.Add(hostBox);

        portBox.Location = new Point(528, 10);
        portBox.Size = new Size(86, 26);
        portBox.Minimum = 1;
        portBox.Maximum = 65535;
        portBox.ValueChanged += delegate { MarkCustom(); RefreshEverything(); };
        proxyPanel.Controls.Add(portBox);

        Button testButton = new Button();
        testButton.Text = "测试线路";
        testButton.Location = new Point(628, 8);
        testButton.Size = new Size(92, 30);
        StyleLightButton(testButton);
        testButton.Click += delegate { TestSelectedProxy(true); };
        proxyPanel.Controls.Add(testButton);

        Button detectButton = new Button();
        detectButton.Text = "读取系统代理";
        detectButton.Location = new Point(730, 8);
        detectButton.Size = new Size(104, 30);
        StyleLightButton(detectButton);
        detectButton.Click += delegate { DetectSystemProxy(); };
        proxyPanel.Controls.Add(detectButton);

        Label proxyHint = MakeLabel("地址示例：127.0.0.1　端口示例：Veee HTTP 15236 / SOCKS5 15235", new Point(62, 45), new Size(650, 22));
        proxyHint.ForeColor = Color.FromArgb(105, 114, 126);
        proxyPanel.Controls.Add(proxyHint);

        Label basketTitle = MakeLabel("本次加速箩筐", new Point(30, 354), new Size(160, 25));
        basketTitle.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        Controls.Add(basketTitle);

        Button addCodexButton = MakeSmallButton("添加 Codex", 174);
        addCodexButton.Click += delegate { AddCodex(); };
        Controls.Add(addCodexButton);
        Button installedAppsButton = MakeSmallButton("已安装应用库", 278);
        installedAppsButton.Size = new Size(142, 31);
        installedAppsButton.Click += delegate { AddFromInstalledApps(); };
        Controls.Add(installedAppsButton);
        Button addFileButton = MakeSmallButton("桌面 / 文件", 430);
        addFileButton.Size = new Size(112, 31);
        addFileButton.Click += delegate { AddFromFiles(); };
        Controls.Add(addFileButton);
        Button addRunningButton = MakeSmallButton("正在运行", 552);
        addRunningButton.Size = new Size(130, 31);
        addRunningButton.Click += delegate { AddFromRunning(); };
        Controls.Add(addRunningButton);
        Button removeButton = MakeSmallButton("移除", 692);
        removeButton.Size = new Size(52, 31);
        removeButton.Click += delegate { RemoveSelected(); };
        Controls.Add(removeButton);
        Button clearButton = MakeSmallButton("清空", 754);
        clearButton.Size = new Size(52, 31);
        clearButton.Click += delegate { ClearBasket(); };
        Controls.Add(clearButton);
        Button refreshButton = MakeSmallButton("刷新", 816);
        refreshButton.Size = new Size(52, 31);
        refreshButton.Click += delegate { RefreshEverything(); };
        Controls.Add(refreshButton);

        icons.ColorDepth = ColorDepth.Depth32Bit;
        icons.ImageSize = new Size(24, 24);
        appList.Location = new Point(28, 392);
        appList.Size = new Size(844, 220);
        appList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        appList.View = View.Details;
        appList.FullRowSelect = true;
        appList.GridLines = true;
        appList.CheckBoxes = false;
        appList.MultiSelect = false;
        appList.HideSelection = false;
        appList.SmallImageList = icons;
        appList.Columns.Add("应用（单击选择）", 180);
        appList.Columns.Add("适配能力", 175);
        appList.Columns.Add("当前状态", 235);
        appList.Columns.Add("程序位置", 230);
        appList.SelectedIndexChanged += delegate { if (!loading) RefreshEverything(); };
        Controls.Add(appList);

        launchButton.Text = "专用加速启动选中应用";
        launchButton.Location = new Point(28, 646);
        launchButton.Size = new Size(310, 48);
        launchButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        launchButton.FlatStyle = FlatStyle.Flat;
        launchButton.FlatAppearance.BorderSize = 0;
        launchButton.BackColor = Color.FromArgb(29, 111, 237);
        launchButton.ForeColor = Color.White;
        launchButton.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        launchButton.Cursor = Cursors.Hand;
        launchButton.Click += delegate { LaunchBasket(); };
        Controls.Add(launchButton);

        normalLaunchButton.Text = "普通方式启动选中应用";
        normalLaunchButton.Location = new Point(348, 646);
        normalLaunchButton.Size = new Size(250, 48);
        normalLaunchButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleLightButton(normalLaunchButton);
        normalLaunchButton.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        normalLaunchButton.Click += delegate { LaunchNormally(); };
        Controls.Add(normalLaunchButton);

        restoreButton.Text = "仅恢复 Windows 系统代理";
        restoreButton.Location = new Point(608, 646);
        restoreButton.Size = new Size(264, 48);
        restoreButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        StyleLightButton(restoreButton);
        restoreButton.Click += delegate { RestoreSystemProxy(true); };
        Controls.Add(restoreButton);

        feedback.Location = new Point(30, 618);
        feedback.Size = new Size(840, 24);
        feedback.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        feedback.ForeColor = Color.FromArgb(93, 103, 116);
        feedback.Text = "先单击选择一个应用，再选择专用或普通启动；已运行的非浏览器应用不会重复启动。";
        Controls.Add(feedback);
    }

    private static Label MakeLabel(string text, Point location, Size size)
    {
        Label label = new Label();
        label.Text = text;
        label.Location = location;
        label.Size = size;
        return label;
    }

    private Button MakeSmallButton(string text, int left)
    {
        Button button = new Button();
        button.Text = text;
        button.Location = new Point(left, 351);
        button.Size = new Size(94, 31);
        StyleLightButton(button);
        return button;
    }

    private static void StyleLightButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(194, 201, 210);
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(39, 48, 61);
        button.Cursor = Cursors.Hand;
    }

    private static void AddStatusCell(Control parent, string name, Label value, int left, int width)
    {
        Label nameLabel = MakeLabel(name, new Point(left, 12), new Size(width, 22));
        nameLabel.ForeColor = Color.FromArgb(105, 114, 126);
        parent.Controls.Add(nameLabel);
        value.Location = new Point(left, 37);
        value.Size = new Size(width, 27);
        value.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        parent.Controls.Add(value);
    }

    private void LoadState()
    {
        loading = true;
        SavedState saved = null;
        try
        {
            if (File.Exists(stateFile)) saved = new JavaScriptSerializer().Deserialize<SavedState>(File.ReadAllText(stateFile, Encoding.UTF8));
        }
        catch { }
        if (saved == null) saved = new SavedState();
        if (saved.Proxy == null) saved.Proxy = new ProxyProfile { Preset = "Veee", Type = "HTTP", Host = "127.0.0.1", Port = 15236 };
        presetBox.SelectedItem = saved.Proxy.Preset == "自定义代理" ? "自定义代理" : "Veee";
        typeBox.SelectedItem = saved.Proxy.Type == "SOCKS5" ? "SOCKS5" : "HTTP";
        hostBox.Text = String.IsNullOrWhiteSpace(saved.Proxy.Host) ? "127.0.0.1" : saved.Proxy.Host;
        portBox.Value = Math.Max(1, Math.Min(65535, saved.Proxy.Port));
        if (saved.VpnPrograms != null) vpnPrograms.AddRange(saved.VpnPrograms.Where(v => v != null && !String.IsNullOrWhiteSpace(v.Path)));
        string veeePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Veee", "Veee.exe");
        if (File.Exists(veeePath) && !vpnPrograms.Any(v => String.Equals(v.Path, veeePath, StringComparison.OrdinalIgnoreCase)))
            vpnPrograms.Insert(0, new VpnProgram { Id = Guid.NewGuid().ToString("N"), Name = "Veee", Path = veeePath, Arguments = "", WorkingDirectory = Path.GetDirectoryName(veeePath) });
        vpnBox.Items.Clear();
        foreach (VpnProgram vpn in vpnPrograms) vpnBox.Items.Add(vpn);
        VpnProgram selectedVpn = vpnPrograms.FirstOrDefault(v => String.Equals(v.Id, saved.SelectedVpnId, StringComparison.OrdinalIgnoreCase));
        if (selectedVpn != null) vpnBox.SelectedItem = selectedVpn;
        else if (vpnBox.Items.Count > 0) vpnBox.SelectedIndex = 0;
        if (saved.Apps != null) apps.AddRange(saved.Apps.Where(a => a != null && !String.IsNullOrWhiteSpace(a.Path)));
        bool repairedCodex = false;
        if (apps.Any(a => a.Adapter == "Codex"))
        {
            try
            {
                currentCodexExecutable = FindCodexExecutable();
                foreach (BasketApp app in apps.Where(a => a.Adapter == "Codex"))
                {
                    if (!String.Equals(app.Path, currentCodexExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        app.Path = currentCodexExecutable;
                        app.WorkingDirectory = Path.GetDirectoryName(currentCodexExecutable);
                        repairedCodex = true;
                    }
                }
            }
            catch { }
        }
        loading = false;
        RebuildList();
        if (repairedCodex) SaveState();
    }

    private void SaveState()
    {
        if (loading) return;
        Directory.CreateDirectory(stateDirectory);
        VpnProgram selectedVpn = vpnBox.SelectedItem as VpnProgram;
        SavedState state = new SavedState { Proxy = GetProxy(false), Apps = apps, VpnPrograms = vpnPrograms, SelectedVpnId = selectedVpn == null ? "" : selectedVpn.Id };
        File.WriteAllText(stateFile, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
    }

    private void AddVpnProgram()
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title = "选择 VPN 程序或快捷方式";
            dialog.Filter = "应用和快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*";
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string path = dialog.FileName;
                string arguments = "";
                string workingDirectory = "";
                if (String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
                    ResolveShortcut(dialog.FileName, out path, out arguments, out workingDirectory);
                if (!File.Exists(path) || !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("该文件不是可启动的 VPN 程序。");
                VpnProgram existing = vpnPrograms.FirstOrDefault(v => String.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase) && String.Equals(v.Arguments ?? "", arguments ?? "", StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = new VpnProgram { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileNameWithoutExtension(dialog.FileName), Path = path, Arguments = arguments ?? "", WorkingDirectory = String.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(path) : workingDirectory };
                    vpnPrograms.Add(existing);
                    vpnBox.Items.Add(existing);
                }
                vpnBox.SelectedItem = existing;
                SaveState();
                RefreshVpnButton();
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法添加 VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    private void OpenSelectedVpn()
    {
        VpnProgram vpn = vpnBox.SelectedItem as VpnProgram;
        if (vpn == null) { MessageBox.Show(this, "请先选择或添加 VPN 程序。", "尚未选择 VPN", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (!File.Exists(vpn.Path)) { MessageBox.Show(this, "VPN 程序文件不存在：\n" + vpn.Path, "找不到 VPN", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try
        {
            using (Process running = FindRunningProcess(vpn.Path))
            {
                if (running != null && running.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(running.MainWindowHandle, 9);
                    SetForegroundWindow(running.MainWindowHandle);
                    feedback.Text = "已切回 " + vpn.Name + "；请在 VPN 界面选择节点和模式。";
                }
                else
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = vpn.Path;
                    info.Arguments = vpn.Arguments ?? "";
                    info.WorkingDirectory = Directory.Exists(vpn.WorkingDirectory) ? vpn.WorkingDirectory : Path.GetDirectoryName(vpn.Path);
                    info.UseShellExecute = true;
                    Process.Start(info);
                    feedback.Text = "已打开 " + vpn.Name + "；连接完成后回到箩筐启动应用。";
                }
            }
            feedback.ForeColor = Color.FromArgb(25, 135, 84);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开 VPN", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        RefreshVpnButton();
    }

    private void RefreshVpnButton()
    {
        VpnProgram vpn = vpnBox.SelectedItem as VpnProgram;
        openVpnButton.Enabled = vpn != null && File.Exists(vpn.Path);
        bool running = false;
        if (vpn != null)
        {
            using (Process process = FindRunningProcess(vpn.Path)) running = process != null;
        }
        openVpnButton.Text = running ? "切回 VPN 界面（正在运行）" : "打开所选 VPN";
        openVpnButton.ForeColor = running ? Color.FromArgb(25, 135, 84) : Color.FromArgb(39, 48, 61);
    }

    private static Process FindRunningProcess(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) return null;
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase)) return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    private void ApplyPreset()
    {
        if (loading) return;
        if (presetBox.Text == "Veee")
        {
            loading = true;
            typeBox.SelectedItem = "HTTP";
            hostBox.Text = "127.0.0.1";
            portBox.Value = 15236;
            loading = false;
        }
        SaveState();
        RefreshEverything();
    }

    private void MarkCustom()
    {
        if (loading) return;
        bool isVeee = typeBox.Text == "HTTP" && IsLoopback(hostBox.Text.Trim()) && portBox.Value == 15236;
        if (!isVeee && presetBox.Text != "自定义代理")
        {
            loading = true;
            presetBox.SelectedItem = "自定义代理";
            loading = false;
        }
        SaveState();
    }

    private ProxyProfile GetProxy(bool validate)
    {
        string host = hostBox.Text.Trim();
        if (validate && (String.IsNullOrWhiteSpace(host) || host.IndexOfAny(new char[] { ' ', '/', '\\' }) >= 0))
            throw new InvalidOperationException("代理地址格式不正确。");
        return new ProxyProfile { Preset = presetBox.Text, Type = typeBox.Text == "SOCKS5" ? "SOCKS5" : "HTTP", Host = host, Port = Decimal.ToInt32(portBox.Value) };
    }

    private void RefreshEverything()
    {
        ProxyProfile proxy = null;
        try { proxy = GetProxy(true); } catch { }
        int enabled = ReadProxyEnabled();
        bool ownBackup = File.Exists(backupFile);
        bool codexBackup = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexVeeeLauncher", "windows-proxy-backup.json"));
        if (ownBackup && enabled == 0)
            SetValue(systemValue, "箩筐模式：其他应用直连", Color.FromArgb(25, 135, 84));
        else if (enabled == 1)
            SetValue(systemValue, "系统代理已开启", Color.FromArgb(29, 111, 237));
        else if (codexBackup)
            SetValue(systemValue, "系统代理已关闭（Codex 工具管理）", Color.FromArgb(184, 116, 17));
        else
            SetValue(systemValue, "系统直连", Color.FromArgb(105, 114, 126));

        if (proxy == null)
            SetValue(proxyValue, "代理配置无效", Color.FromArgb(190, 56, 45));
        else
            SetValue(proxyValue, proxy.Type + " · " + proxy.Host + ":" + proxy.Port, Color.FromArgb(39, 48, 61));
        BasketApp selected = GetSelectedApp();
        bool selectedRunning = selected != null && selected.Adapter != "Browser" && IsAppRunning(selected);
        SetValue(countValue, selected == null ? "未选择应用" : "已选择：" + selected.Name,
            selected == null ? Color.FromArgb(105, 114, 126) : Color.FromArgb(25, 135, 84));

        if (launchInProgress)
        {
            launchButton.Text = "正在启动，请稍候…";
            normalLaunchButton.Text = "正在启动，请稍候…";
        }
        else if (selected == null)
        {
            launchButton.Text = "请先选择一个应用";
            normalLaunchButton.Text = "请先选择一个应用";
        }
        else if (selectedRunning)
        {
            launchButton.Text = "已在运行，不会重复启动";
            normalLaunchButton.Text = "已在运行，不会重复启动";
        }
        else if (selected.Adapter == "Codex")
        {
            launchButton.Text = "专用代理启动 Codex（验证参数）";
            normalLaunchButton.Text = "普通方式启动选中应用";
        }
        else
        {
            launchButton.Text = selected.Adapter == "Browser" && IsTrackedLaunchRunning(selected) && GetTrackedMode(selected) == "Special"
                ? "切回专用浏览器窗口" : "专用加速启动选中应用";
            normalLaunchButton.Text = "普通方式启动选中应用";
        }

        launchButton.Enabled = !launchInProgress && proxy != null && selected != null && !selectedRunning;
        launchButton.BackColor = launchButton.Enabled ? Color.FromArgb(29, 111, 237) : Color.FromArgb(169, 183, 201);
        normalLaunchButton.Enabled = !launchInProgress && selected != null && !selectedRunning;
        restoreButton.Enabled = ownBackup;
        RefreshVpnButton();
        UpdateListStatuses();
    }

    private static void SetValue(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    private void RebuildList()
    {
        BasketApp selected = GetSelectedApp();
        string selectedId = selected == null ? "" : selected.Id;
        loading = true;
        appList.Items.Clear();
        icons.Images.Clear();
        foreach (BasketApp app in apps)
        {
            string imageKey = app.Id;
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(app.Path);
                icons.Images.Add(imageKey, icon ?? SystemIcons.Application);
            }
            catch { icons.Images.Add(imageKey, SystemIcons.Application); }
            ListViewItem item = new ListViewItem(app.Name, imageKey);
            item.Tag = app;
            item.SubItems.Add(GetAdapterText(app.Adapter));
            item.SubItems.Add(DescribeState(app));
            item.SubItems.Add(app.Path);
            appList.Items.Add(item);
            if (String.Equals(app.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
                item.Focused = true;
            }
        }
        loading = false;
    }

    private void UpdateListStatuses()
    {
        foreach (ListViewItem item in appList.Items)
        {
            BasketApp app = item.Tag as BasketApp;
            if (app == null) continue;
            item.SubItems[1].Text = GetAdapterText(app.Adapter);
            item.SubItems[2].Text = DescribeState(app);
            item.SubItems[3].Text = app.Path;
        }
    }

    private string DescribeState(BasketApp app)
    {
        string executable = ResolveAppExecutable(app, false);
        if (!File.Exists(executable)) return "程序文件不存在";
        if (IsTrackedLaunchRunning(app))
        {
            string mode = GetTrackedMode(app);
            if (mode == "Normal") return "普通方式运行中；不会重复启动";
            if (app.Adapter == "Browser") return "专用浏览器窗口运行中；可点击切回";
            return "专用启动中；代理效果待验证";
        }
        bool running = IsPathRunning(executable);
        if (app.Adapter == "Browser") return running ? "已有普通窗口；仍可另开加速窗口" : "未运行；可开独立加速窗口";
        if (running) return "正在运行；不会重复启动，退出后可切换";
        return "未运行；单击选中后选择启动方式";
    }

    private bool IsTrackedLaunchRunning(BasketApp app)
    {
        int processId;
        if (!launchedProcessIds.TryGetValue(app.Id, out processId)) return false;
        try
        {
            using (Process process = Process.GetProcessById(processId)) return !process.HasExited;
        }
        catch
        {
            launchedProcessIds.Remove(app.Id);
            launchedModes.Remove(app.Id);
            return false;
        }
    }

    private string GetTrackedMode(BasketApp app)
    {
        string mode;
        return launchedModes.TryGetValue(app.Id, out mode) ? mode : "";
    }

    private BasketApp GetSelectedApp()
    {
        if (appList.SelectedItems.Count != 1) return null;
        return appList.SelectedItems[0].Tag as BasketApp;
    }

    private void SelectApp(BasketApp app)
    {
        foreach (ListViewItem item in appList.Items)
        {
            BasketApp candidate = item.Tag as BasketApp;
            if (candidate == null || !String.Equals(candidate.Id, app.Id, StringComparison.OrdinalIgnoreCase)) continue;
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            appList.Select();
            break;
        }
    }

    private static string GetAdapterText(string adapter)
    {
        if (adapter == "Codex") return "可验证代理启动 · Codex";
        if (adapter == "Browser") return "已适配 · 独立浏览器窗口";
        return "通用代理 · 生效情况需实测";
    }

    private void AddCodex()
    {
        try
        {
            string path = FindCodexExecutable();
            AddApp(new BasketApp { Id = Guid.NewGuid().ToString("N"), Name = "Codex", Path = path, WorkingDirectory = Path.GetDirectoryName(path), Arguments = "", Adapter = "Codex", Enabled = true });
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "未找到 Codex", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddFromInstalledApps()
    {
        Cursor = Cursors.WaitCursor;
        feedback.ForeColor = Color.FromArgb(93, 103, 116);
        feedback.Text = "正在扫描开始菜单、安装记录和 Microsoft Store 应用……";
        Application.DoEvents();
        try
        {
            List<InstalledAppCandidate> candidates = GetInstalledAppCandidates();
            using (InstalledAppsDialog dialog = new InstalledAppsDialog(candidates))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (InstalledAppCandidate candidate in dialog.SelectedCandidates)
                    AddApp(CreateApp(candidate.Name, candidate.Path, candidate.Arguments, candidate.WorkingDirectory));
                feedback.ForeColor = Color.FromArgb(25, 135, 84);
                feedback.Text = "已加入 " + dialog.SelectedCandidates.Count + " 个应用；通用尝试项目仍需实际验证代理效果。";
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法读取已安装应用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    private List<InstalledAppCandidate> GetInstalledAppCandidates()
    {
        Dictionary<string, InstalledAppCandidate> result = new Dictionary<string, InstalledAppCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> location in new Dictionary<string, string>
        {
            { "开始菜单（当前用户）", Environment.GetFolderPath(Environment.SpecialFolder.Programs) },
            { "开始菜单（所有用户）", Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }
        })
        {
            if (String.IsNullOrWhiteSpace(location.Value) || !Directory.Exists(location.Value)) continue;
            foreach (string shortcut in Directory.GetFiles(location.Value, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    string path, arguments, workingDirectory;
                    ResolveShortcut(shortcut, out path, out arguments, out workingDirectory);
                    AddInstalledCandidate(result, Path.GetFileNameWithoutExtension(shortcut), path, arguments, workingDirectory, location.Key);
                }
                catch { }
            }
        }

        ScanUninstallRegistry(result, RegistryHive.CurrentUser, RegistryView.Default, "安装记录（当前用户）");
        ScanUninstallRegistry(result, RegistryHive.LocalMachine, RegistryView.Registry64, "安装记录（64 位）");
        ScanUninstallRegistry(result, RegistryHive.LocalMachine, RegistryView.Registry32, "安装记录（32 位）");
        ScanStartApps(result);
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ScanUninstallRegistry(Dictionary<string, InstalledAppCandidate> result, RegistryHive hive, RegistryView view, string source)
    {
        try
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
            using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
            {
                if (uninstall == null) return;
                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey entry = uninstall.OpenSubKey(subKeyName, false))
                        {
                            if (entry == null || Convert.ToInt32(entry.GetValue("SystemComponent", 0)) == 1) continue;
                            string name = Convert.ToString(entry.GetValue("DisplayName", "")).Trim();
                            string icon = Convert.ToString(entry.GetValue("DisplayIcon", "")).Trim();
                            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(icon)) continue;
                            string path = ExtractExecutableFromDisplayIcon(icon);
                            AddInstalledCandidate(result, name, path, "", Path.GetDirectoryName(path), source);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static string ExtractExecutableFromDisplayIcon(string value)
    {
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        string path;
        if (expanded.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = expanded.IndexOf('"', 1);
            path = end > 1 ? expanded.Substring(1, end - 1) : expanded.Trim('"');
        }
        else
        {
            int comma = expanded.LastIndexOf(',');
            path = comma > 0 ? expanded.Substring(0, comma) : expanded;
        }
        return path.Trim().Trim('"');
    }

    private void AddInstalledCandidate(Dictionary<string, InstalledAppCandidate> result, string name, string path, string arguments, string workingDirectory, string source)
    {
        if (String.IsNullOrWhiteSpace(path)) return;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(path) || !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return;
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!String.IsNullOrWhiteSpace(windowsDirectory) && path.StartsWith(windowsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        string lowerName = (name ?? "").ToLowerInvariant();
        if (file.StartsWith("unins") || file.Contains("uninstall") || file == "setup" || file.Contains("updater") || lowerName.Contains("卸载") || lowerName.Contains("uninstall")) return;
        string key = path + "|" + (arguments ?? "");
        if (result.ContainsKey(key)) return;
        result[key] = new InstalledAppCandidate
        {
            Key = key,
            Name = String.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim(),
            Path = path,
            Arguments = arguments ?? "",
            WorkingDirectory = String.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(path) : workingDirectory,
            Source = source,
            Compatibility = GetInstalledCompatibility(path),
            CanAdd = true
        };
    }

    private static string GetInstalledCompatibility(string path)
    {
        string file = Path.GetFileName(path).ToLowerInvariant();
        if (file == "chatgpt.exe" && path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0) return "可验证代理启动 · Codex";
        if (new string[] { "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe" }.Contains(file)) return "已适配 · 独立浏览器窗口";
        if (file.Contains("launcher") || file.Contains("bootstrap")) return "通用尝试 · 启动器可能另开进程";
        return "通用尝试 · 是否采用代理需实测";
    }

    private void ScanStartApps(Dictionary<string, InstalledAppCandidate> result)
    {
        try
        {
            string command = "$items=Get-StartApps | Select-Object Name,AppID; [Console]::Out.Write(($items | ConvertTo-Json -Compress))";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded);
            info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using (Process process = Process.Start(info))
            {
                string json = process.StandardOutput.ReadToEnd().Trim();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || String.IsNullOrWhiteSpace(json)) return;
                object parsed = new JavaScriptSerializer().DeserializeObject(json);
                IEnumerable<object> rows = parsed as object[] ?? new object[] { parsed };
                foreach (object raw in rows)
                {
                    Dictionary<string, object> row = raw as Dictionary<string, object>;
                    if (row == null) continue;
                    string name = row.ContainsKey("Name") ? Convert.ToString(row["Name"]).Trim() : "";
                    string appId = row.ContainsKey("AppID") ? Convert.ToString(row["AppID"]).Trim() : "";
                    if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(appId)) continue;
                    if (appId.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            string codexPath = FindCodexExecutable();
                            AddInstalledCandidate(result, "Codex", codexPath, "", Path.GetDirectoryName(codexPath), "Microsoft Store · 已解析桌面入口");
                        }
                        catch { }
                        continue;
                    }
                    if (result.Values.Any(a => String.Equals(a.Name, name, StringComparison.CurrentCultureIgnoreCase))) continue;
                    string key = "APPID|" + appId;
                    result[key] = new InstalledAppCandidate
                    {
                        Key = key, Name = name, Path = "", Arguments = "", WorkingDirectory = "", Source = "Microsoft Store / 系统应用",
                        Compatibility = "仅识别 · 当前不能注入代理", CanAdd = false
                    };
                }
            }
        }
        catch { }
    }

    private void AddFromFiles()
    {
        List<DesktopAppCandidate> desktopItems = GetDesktopCandidates();
        using (DesktopAppsDialog desktopDialog = new DesktopAppsDialog(desktopItems))
        {
            DialogResult result = desktopDialog.ShowDialog(this);
            if (result == DialogResult.OK)
            {
                foreach (DesktopAppCandidate item in desktopDialog.SelectedCandidates) AddSelectedFile(item.ShortcutPath);
                return;
            }
            if (!desktopDialog.BrowseOtherFile) return;
        }

        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title = "从其他位置选择应用或快捷方式";
            dialog.Filter = "应用和快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*";
            dialog.Multiselect = true;
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            foreach (string selected in dialog.FileNames) AddSelectedFile(selected);
        }
    }

    private static List<DesktopAppCandidate> GetDesktopCandidates()
    {
        List<DesktopAppCandidate> result = new List<DesktopAppCandidate>();
        string personal = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        foreach (KeyValuePair<string, string> desktop in new Dictionary<string, string> { { "个人桌面", personal }, { "公共桌面", common } })
        {
            if (String.IsNullOrWhiteSpace(desktop.Value) || !Directory.Exists(desktop.Value)) continue;
            foreach (string path in Directory.GetFiles(desktop.Value).Where(p => String.Equals(Path.GetExtension(p), ".lnk", StringComparison.OrdinalIgnoreCase) || String.Equals(Path.GetExtension(p), ".exe", StringComparison.OrdinalIgnoreCase)))
                result.Add(new DesktopAppCandidate { Name = Path.GetFileNameWithoutExtension(path), ShortcutPath = path, DesktopLocation = desktop.Key });
        }
        return result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void AddSelectedFile(string selected)
    {
        try
        {
            string path = selected;
            string arguments = "";
            string workingDirectory = "";
            if (String.Equals(Path.GetExtension(selected), ".lnk", StringComparison.OrdinalIgnoreCase))
                ResolveShortcut(selected, out path, out arguments, out workingDirectory);
            if (!File.Exists(path) || !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该快捷方式不是可启动的桌面应用：" + selected);
            AddApp(CreateApp(Path.GetFileNameWithoutExtension(selected), path, arguments, workingDirectory));
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法添加应用", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void AddFromRunning()
    {
        List<AppCandidate> candidates = new List<AppCandidate>();
        foreach (Process process in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero || process.Id == Process.GetCurrentProcess().Id) continue;
                string path = process.MainModule.FileName;
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                if (candidates.Any(c => String.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                candidates.Add(new AppCandidate { Name = String.IsNullOrWhiteSpace(process.MainWindowTitle) ? process.ProcessName : process.MainWindowTitle, Path = path, ProcessId = process.Id });
            }
            catch { }
        }
        using (RunningAppsDialog dialog = new RunningAppsDialog(candidates))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            foreach (AppCandidate candidate in dialog.SelectedCandidates)
                AddApp(CreateApp(candidate.Name, candidate.Path, "", Path.GetDirectoryName(candidate.Path)));
        }
    }

    private BasketApp CreateApp(string name, string path, string arguments, string workingDirectory)
    {
        string file = Path.GetFileName(path).ToLowerInvariant();
        string adapter = "Generic";
        if (file == "chatgpt.exe" && path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0) { adapter = "Codex"; name = "Codex"; }
        else if (new string[] { "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe" }.Contains(file)) adapter = "Browser";
        return new BasketApp { Id = Guid.NewGuid().ToString("N"), Name = name, Path = path, Arguments = arguments ?? "", WorkingDirectory = String.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(path) : workingDirectory, Adapter = adapter, Enabled = true };
    }

    private void AddApp(BasketApp app)
    {
        BasketApp existing = app.Adapter == "Codex"
            ? apps.FirstOrDefault(a => a.Adapter == "Codex")
            : apps.FirstOrDefault(a => String.Equals(a.Path, app.Path, StringComparison.OrdinalIgnoreCase) && String.Equals(a.Arguments ?? "", app.Arguments ?? "", StringComparison.Ordinal));
        if (existing != null)
        {
            existing.Name = app.Name;
            existing.Path = app.Path;
            existing.Arguments = app.Arguments;
            existing.WorkingDirectory = app.WorkingDirectory;
            existing.Adapter = app.Adapter;
            app = existing;
        }
        else apps.Add(app);
        SaveState();
        RebuildList();
        SelectApp(app);
        RefreshEverything();
    }

    private void RemoveSelected()
    {
        List<BasketApp> selected = appList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as BasketApp).Where(a => a != null).ToList();
        if (selected.Count == 0) return;
        foreach (BasketApp app in selected) apps.Remove(app);
        SaveState(); RebuildList(); RefreshEverything();
    }

    private void ClearBasket()
    {
        if (apps.Count == 0) return;
        if (MessageBox.Show(this, "清空箩筐中的应用列表？\n\n不会卸载或删除这些应用。", "清空箩筐", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        apps.Clear(); SaveState(); RebuildList(); RefreshEverything();
    }

    private void LaunchBasket()
    {
        if (launchInProgress) return;
        BasketApp app = GetSelectedApp();
        if (app == null) return;
        if (app.Adapter != "Browser" && IsAppRunning(app))
        {
            MessageBox.Show(this, app.Name + " 已经在运行，箩筐不会再次发起启动。\n\n如需切换线路，请先正常退出该应用，等状态变为“未运行”后再启动。", "已阻止重复启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { ResolveAppExecutable(app, true); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法定位当前程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (app.Adapter == "Browser" && IsTrackedLaunchRunning(app) && GetTrackedMode(app) == "Special" && TryActivateTrackedProcess(app))
        {
            feedback.ForeColor = Color.FromArgb(25, 135, 84);
            feedback.Text = "已切回 " + app.Name + " 的专用浏览器窗口。";
            return;
        }

        launchInProgress = true;
        RefreshEverything();
        try
        {
            if (!TestSelectedProxy(false)) return;
            EnsureOutsideAppsDirect();
            ProxyProfile proxy = GetProxy(true);
            if (app.Adapter == "Codex") LaunchCodexWithProxy(app, proxy);
            else LaunchApp(app, proxy);
            launchedModes[app.Id] = "Special";
            feedback.ForeColor = Color.FromArgb(25, 135, 84);
            feedback.Text = app.Adapter == "Codex"
                ? "Codex 已收到代理启动参数；登录与请求是否走该线路仍需验证。"
                : app.Name + " 已按专用方式启动；通用应用是否真正采用代理仍需实测。";
        }
        catch (Exception error)
        {
            try { RestoreSystemProxy(false); } catch { }
            if (app.Adapter == "Codex")
            {
                launchedProcessIds.Remove(app.Id);
                launchedModes.Remove(app.Id);
            }
            MessageBox.Show(this, error.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            launchInProgress = false;
            RefreshEverything();
        }
    }

    private void LaunchNormally()
    {
        if (launchInProgress) return;
        BasketApp app = GetSelectedApp();
        if (app == null) return;
        if (app.Adapter != "Browser" && IsAppRunning(app))
        {
            MessageBox.Show(this, app.Name + " 已经在运行，箩筐不会再次发起启动。\n\n如需切换为普通方式，请先正常退出该应用，等状态变为“未运行”后再启动。", "已阻止重复启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { ResolveAppExecutable(app, true); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法定位当前程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        launchInProgress = true;
        RefreshEverything();
        try
        {
            if (File.Exists(backupFile)) RestoreSystemProxy(false);
            launchedProcessIds[app.Id] = LaunchAppNormally(app);
            launchedModes[app.Id] = "Normal";
            feedback.ForeColor = Color.FromArgb(39, 48, 61);
            feedback.Text = app.Name + " 已按普通方式启动，将跟随 Windows 和 VPN 当前网络设置。";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "普通启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            launchInProgress = false;
            RefreshEverything();
        }
    }

    private int LaunchAppNormally(BasketApp app)
    {
        string executable = ResolveAppExecutable(app, true);
        if (!File.Exists(executable)) throw new FileNotFoundException("程序文件不存在", executable);
        if (app.Adapter == "Codex") return ActivateCodex(app.Arguments ?? "");
        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = executable;
        info.Arguments = app.Arguments ?? "";
        info.WorkingDirectory = Directory.Exists(app.WorkingDirectory) ? app.WorkingDirectory : Path.GetDirectoryName(executable);
        info.UseShellExecute = true;
        Process process = Process.Start(info);
        if (process == null) throw new InvalidOperationException("系统没有返回启动进程。");
        return process.Id;
    }

    private void LaunchApp(BasketApp app, ProxyProfile profile)
    {
        string executable = ResolveAppExecutable(app, true);
        if (!File.Exists(executable)) throw new FileNotFoundException("程序文件不存在", executable);
        string chromiumProxy = (profile.Type == "SOCKS5" ? "socks5://" : "http://") + profile.Host + ":" + profile.Port;
        string environmentProxy = (profile.Type == "SOCKS5" ? "socks5h://" : "http://") + profile.Host + ":" + profile.Port;
        List<string> args = new List<string>();
        if (!String.IsNullOrWhiteSpace(app.Arguments)) args.Add(app.Arguments);
        if (app.Adapter == "Browser")
        {
            string profileDirectory = Path.Combine(browserProfilesDirectory, app.Id);
            Directory.CreateDirectory(profileDirectory);
            args.Add("--user-data-dir=" + Quote(profileDirectory));
            args.Add("--proxy-server=" + QuoteIfNeeded(chromiumProxy));
            args.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
            args.Add("--disable-quic");
        }

        ProcessStartInfo info = new ProcessStartInfo();
        info.FileName = executable;
        info.Arguments = String.Join(" ", args.ToArray());
        info.WorkingDirectory = Directory.Exists(app.WorkingDirectory) ? app.WorkingDirectory : Path.GetDirectoryName(executable);
        info.UseShellExecute = false;
        foreach (string key in new string[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) info.EnvironmentVariables[key] = environmentProxy;
        info.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
        info.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1,::1";
        Process process = Process.Start(info);
        if (process == null) throw new InvalidOperationException("系统没有返回启动进程。");
        launchedProcessIds[app.Id] = process.Id;
    }

    private void LaunchCodexWithProxy(BasketApp app, ProxyProfile profile)
    {
        string proxy = (profile.Type == "SOCKS5" ? "socks5://" : "http://") + profile.Host + ":" + profile.Port;
        string environmentProxy = (profile.Type == "SOCKS5" ? "socks5h://" : "http://") + profile.Host + ":" + profile.Port;
        List<string> arguments = new List<string>();
        if (!String.IsNullOrWhiteSpace(app.Arguments)) arguments.Add(app.Arguments);
        arguments.Add("--proxy-server=" + QuoteIfNeeded(proxy));
        arguments.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
        arguments.Add("--disable-quic");
        string startedExecutable = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string executable = ResolveAppExecutable(app, true);
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = executable;
            info.Arguments = String.Join(" ", arguments.ToArray());
            info.WorkingDirectory = Path.GetDirectoryName(executable);
            info.UseShellExecute = false;
            foreach (string key in new string[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                info.EnvironmentVariables[key] = environmentProxy;
            info.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
            info.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1,::1";
            try
            {
                using (Process process = Process.Start(info))
                {
                    if (process == null) throw new InvalidOperationException("Windows 没有返回 Codex 启动进程。");
                }
                startedExecutable = executable;
                break;
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                if (error.NativeErrorCode != 5 && error.NativeErrorCode != 2 && error.NativeErrorCode != 3) throw;
                if (attempt == 2)
                    throw new InvalidOperationException("Windows 暂时无法直接启动 Codex（错误码 " + error.NativeErrorCode + "）；箩筐已重新识别安装版本并重试。请等 Codex 更新完成后再试。", error);
                feedback.Text = "Codex 安装包暂时无法启动，正在重新识别并重试…";
                Application.DoEvents();
                Thread.Sleep(1000);
            }
        }

        // Electron can forward a second launch to an existing instance. Require
        // the main process to contain our switch before recording a special launch.
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        bool foundMainProcess = false;
        do
        {
            int mainProcessId;
            string commandLine = FindCodexMainCommandLine(startedExecutable, out mainProcessId);
            if (!String.IsNullOrEmpty(commandLine))
            {
                foundMainProcess = true;
                if (commandLine.IndexOf("--proxy-server=" + proxy, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    launchedProcessIds[app.Id] = mainProcessId;
                    return;
                }
            }
            Application.DoEvents();
            Thread.Sleep(200);
        } while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException(foundMainProcess
            ? "Codex 已打开，但没有收到所选代理启动参数。箩筐未把这次启动标记为加速；请正常退出 Codex 后重试。"
            : "Codex 启动后未能读取主进程的启动参数，无法确认代理是否生效。箩筐未把这次启动标记为加速。");
    }

    private static string FindCodexMainCommandLine(string expectedExecutable, out int processId)
    {
        processId = 0;
        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'ChatGPT.exe'"))
        using (ManagementObjectCollection processes = searcher.Get())
        {
            foreach (ManagementObject process in processes)
            {
                using (process)
                {
                    string path = Convert.ToString(process["ExecutablePath"]);
                    string commandLine = Convert.ToString(process["CommandLine"]);
                    if (String.Equals(path, expectedExecutable, StringComparison.OrdinalIgnoreCase) &&
                        commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        processId = Convert.ToInt32(process["ProcessId"]);
                        return commandLine;
                    }
                }
            }
        }
        return "";
    }

    private static int ActivateCodex(string arguments)
    {
        IApplicationActivationManager manager = null;
        try
        {
            manager = (IApplicationActivationManager)new ApplicationActivationManager();
            uint processId;
            int result = manager.ActivateApplication(CodexAppUserModelId, arguments, 0, out processId);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            if (processId == 0) throw new InvalidOperationException("Windows 没有返回 Codex 的进程编号。");
            return checked((int)processId);
        }
        catch (COMException error)
        {
            throw new InvalidOperationException("Windows 应用入口无法启动 Codex（错误码 0x" + error.ErrorCode.ToString("X8") + "）。", error);
        }
        finally
        {
            if (manager != null) Marshal.FinalReleaseComObject(manager);
        }
    }

    private bool TestSelectedProxy(bool showMessage)
    {
        ProxyProfile profile;
        try { profile = GetProxy(true); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "代理配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        Cursor = Cursors.WaitCursor;
        feedback.ForeColor = Color.FromArgb(93, 103, 116);
        feedback.Text = "正在进行真实代理握手测试……";
        Application.DoEvents();
        try
        {
            if (profile.Type == "SOCKS5") TestSocks5(profile); else TestHttpConnect(profile);
            feedback.ForeColor = Color.FromArgb(25, 135, 84);
            feedback.Text = "代理线路可用。";
            if (showMessage) MessageBox.Show(this, "代理握手成功，可以使用。", "测试成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SaveState();
            return true;
        }
        catch (Exception error)
        {
            feedback.ForeColor = Color.FromArgb(190, 56, 45);
            feedback.Text = "代理测试失败。";
            if (showMessage) MessageBox.Show(this, error.Message, "代理测试失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally { Cursor = Cursors.Default; }
    }

    private static TcpClient OpenProxy(ProxyProfile profile)
    {
        TcpClient client = new TcpClient();
        IAsyncResult result = client.BeginConnect(profile.Host, profile.Port, null, null);
        if (!result.AsyncWaitHandle.WaitOne(3000)) { client.Close(); throw new IOException("连接代理端口超时。"); }
        client.EndConnect(result); client.ReceiveTimeout = 4000; client.SendTimeout = 4000; return client;
    }

    private static void TestHttpConnect(ProxyProfile profile)
    {
        using (TcpClient client = OpenProxy(profile)) using (NetworkStream stream = client.GetStream())
        {
            byte[] request = Encoding.ASCII.GetBytes("CONNECT api.openai.com:443 HTTP/1.1\r\nHost: api.openai.com:443\r\nProxy-Connection: close\r\n\r\n");
            stream.Write(request, 0, request.Length);
            byte[] buffer = new byte[512]; int count = stream.Read(buffer, 0, buffer.Length);
            string line = Encoding.ASCII.GetString(buffer, 0, count).Split('\n')[0].Trim();
            if (!line.StartsWith("HTTP/1.1 200") && !line.StartsWith("HTTP/1.0 200")) throw new IOException("HTTP 代理拒绝请求：" + line);
        }
    }

    private static void TestSocks5(ProxyProfile profile)
    {
        using (TcpClient client = OpenProxy(profile)) using (NetworkStream stream = client.GetStream())
        {
            stream.Write(new byte[] { 5, 1, 0 }, 0, 3);
            byte[] response = ReadExact(stream, 2);
            if (response[0] != 5 || response[1] != 0) throw new IOException("SOCKS5 不支持免密码连接。");
            byte[] domain = Encoding.ASCII.GetBytes("api.openai.com");
            byte[] request = new byte[7 + domain.Length];
            request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)domain.Length;
            Buffer.BlockCopy(domain, 0, request, 5, domain.Length); request[5 + domain.Length] = 1; request[6 + domain.Length] = 187;
            stream.Write(request, 0, request.Length); response = ReadExact(stream, 4);
            if (response[0] != 5 || response[1] != 0) throw new IOException("SOCKS5 无法连接 OpenAI，错误码：" + response[1]);
        }
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        byte[] data = new byte[count]; int offset = 0;
        while (offset < count) { int read = stream.Read(data, offset, count - offset); if (read <= 0) throw new EndOfStreamException("代理提前断开。"); offset += read; }
        return data;
    }

    private void EnsureOutsideAppsDirect()
    {
        int enabled = ReadProxyEnabled();
        if (enabled == 0) return;
        if (!File.Exists(backupFile)) SaveSystemProxyBackup();
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProxyRegistryPath, true))
        {
            if (key == null) throw new InvalidOperationException("无法修改 Windows 代理设置。");
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", false);
        }
        RefreshInternetSettings();
    }

    private void SaveSystemProxyBackup()
    {
        Directory.CreateDirectory(stateDirectory);
        Dictionary<string, object> root = new Dictionary<string, object>();
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProxyRegistryPath, false))
        {
            if (key == null) throw new InvalidOperationException("无法读取 Windows 代理设置。");
            foreach (string name in new string[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" })
            {
                bool exists = Array.IndexOf(key.GetValueNames(), name) >= 0;
                root[name] = new Dictionary<string, object> { { "Exists", exists }, { "Value", exists ? key.GetValue(name) : null } };
            }
        }
        File.WriteAllText(backupFile, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
    }

    private void RestoreSystemProxy(bool showMessage)
    {
        if (!File.Exists(backupFile))
        {
            if (showMessage) MessageBox.Show(this, "本工具没有保存过系统代理，因此没有修改任何设置。", "没有可恢复的备份", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Dictionary<string, object> root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(backupFile, Encoding.UTF8));
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProxyRegistryPath, true))
        {
            if (key == null) throw new InvalidOperationException("无法修改 Windows 代理设置。");
            foreach (string name in new string[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" })
            {
                Dictionary<string, object> entry = (Dictionary<string, object>)root[name];
                if (!Convert.ToBoolean(entry["Exists"])) key.DeleteValue(name, false);
                else key.SetValue(name, name == "ProxyEnable" ? (object)Convert.ToInt32(entry["Value"]) : Convert.ToString(entry["Value"]), name == "ProxyEnable" ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
        }
        File.Delete(backupFile); RefreshInternetSettings(); RefreshEverything();
        if (showMessage) MessageBox.Show(this, "已恢复启动箩筐前保存的系统代理。\n\n已经启动的应用会保持原来的代理路线直到退出。", "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DetectSystemProxy()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProxyRegistryPath, false))
        {
            string value = key == null ? null : Convert.ToString(key.GetValue("ProxyServer", null));
            if (String.IsNullOrWhiteSpace(value)) { MessageBox.Show(this, "没有读取到明确的系统代理地址。", "读取系统代理", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try
            {
                ProxyProfile parsed = ParseProxy(value);
                loading = true; presetBox.SelectedItem = "自定义代理"; typeBox.SelectedItem = parsed.Type; hostBox.Text = parsed.Host; portBox.Value = parsed.Port; loading = false;
                SaveState(); RefreshEverything();
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法识别系统代理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    private static ProxyProfile ParseProxy(string value)
    {
        string selected = value.Trim();
        if (selected.Contains(";") || selected.Contains("="))
        {
            string first = null;
            foreach (string raw in selected.Split(';'))
            {
                string part = raw.Trim();
                if (part.StartsWith("https=", StringComparison.OrdinalIgnoreCase)) { first = part.Substring(6); break; }
                if (part.StartsWith("http=", StringComparison.OrdinalIgnoreCase) && first == null) first = part.Substring(5);
                if (part.StartsWith("socks=", StringComparison.OrdinalIgnoreCase) && first == null) first = "socks5://" + part.Substring(6);
            }
            selected = first;
        }
        if (String.IsNullOrWhiteSpace(selected)) throw new InvalidOperationException("系统代理格式无法识别。");
        string type = selected.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? "SOCKS5" : "HTTP";
        Uri uri; if (!Uri.TryCreate(selected.Contains("://") ? selected : "http://" + selected, UriKind.Absolute, out uri)) throw new InvalidOperationException("系统代理格式无法识别。");
        return new ProxyProfile { Preset = "自定义代理", Type = type, Host = uri.Host, Port = uri.Port };
    }

    private static void ResolveShortcut(string shortcutPath, out string targetPath, out string arguments, out string workingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("无法读取 Windows 快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        targetPath = Convert.ToString(shortcut.TargetPath);
        arguments = Convert.ToString(shortcut.Arguments);
        workingDirectory = Convert.ToString(shortcut.WorkingDirectory);
        Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
        if (String.IsNullOrWhiteSpace(targetPath))
        {
            string appId = GetShortcutAppId(shortcutPath);
            if (!String.IsNullOrWhiteSpace(appId) && appId.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = FindCodexExecutable();
                arguments = "";
                workingDirectory = Path.GetDirectoryName(targetPath);
            }
        }
    }

    private static string GetShortcutAppId(string shortcutPath)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return "";
        dynamic shell = null;
        dynamic folder = null;
        dynamic item = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            folder = shell.Namespace(Path.GetDirectoryName(shortcutPath));
            if (folder == null) return "";
            item = folder.ParseName(Path.GetFileName(shortcutPath));
            return item == null ? "" : Convert.ToString(item.ExtendedProperty("System.Link.TargetParsingPath"));
        }
        catch { return ""; }
        finally
        {
            if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (folder != null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string FindCodexExecutable()
    {
        string command = "$p=Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1; if($p){[Console]::Out.Write($p.InstallLocation)}";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded);
        info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using (Process process = Process.Start(info))
        {
            string location = process.StandardOutput.ReadToEnd().Trim(); string error = process.StandardError.ReadToEnd().Trim(); process.WaitForExit();
            if (process.ExitCode != 0 || String.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("没有找到 Windows 桌面版 Codex。" + (String.IsNullOrWhiteSpace(error) ? "" : "\n" + error));
            string executable = Path.Combine(location, "app", "ChatGPT.exe"); if (!File.Exists(executable)) throw new FileNotFoundException("找不到 Codex 程序。", executable); return executable;
        }
    }

    private static string FindCodexExecutableWithRetry()
    {
        Exception lastError = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { return FindCodexExecutable(); }
            catch (FileNotFoundException error) { lastError = error; }
            catch (InvalidOperationException error) { lastError = error; }
            if (attempt < 3) Thread.Sleep(800);
        }
        throw new InvalidOperationException("Codex 安装位置暂时不可用；箩筐已重新检查。请等应用更新完成后再试。", lastError);
    }

    private string ResolveAppExecutable(BasketApp app, bool refreshCodex)
    {
        if (app.Adapter != "Codex") return app.Path;
        if (!refreshCodex && !String.IsNullOrWhiteSpace(currentCodexExecutable) && File.Exists(currentCodexExecutable))
            return currentCodexExecutable;
        try
        {
            string resolved = refreshCodex ? FindCodexExecutableWithRetry() : FindCodexExecutable();
            currentCodexExecutable = resolved;
            if (!String.Equals(app.Path, resolved, StringComparison.OrdinalIgnoreCase))
            {
                app.Path = resolved;
                app.WorkingDirectory = Path.GetDirectoryName(resolved);
                SaveState();
            }
            return resolved;
        }
        catch
        {
            if (refreshCodex) throw;
            return app.Path;
        }
    }

    private bool IsAppRunning(BasketApp app)
    {
        if (app.Adapter == "Codex")
        {
            foreach (Process process in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string path = process.MainModule.FileName;
                    if (path.IndexOf("\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        path.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }
        return IsPathRunning(ResolveAppExecutable(app, false));
    }

    private bool TryActivateTrackedProcess(BasketApp app)
    {
        int processId;
        if (!launchedProcessIds.TryGetValue(app.Id, out processId)) return false;
        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                if (process.HasExited || process.MainWindowHandle == IntPtr.Zero) return false;
                ShowWindow(process.MainWindowHandle, 9);
                return SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch { return false; }
    }

    private static bool IsPathRunning(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) return false;
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try { if (String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static bool IsLoopback(string host)
    {
        if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1") return true;
        IPAddress address; return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
    }

    private static int ReadProxyEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProxyRegistryPath, false)) return key == null ? -1 : Convert.ToInt32(key.GetValue("ProxyEnable", 0));
    }

    private static void RefreshInternetSettings()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    private static string QuoteIfNeeded(string value) { return value.IndexOf(' ') >= 0 ? Quote(value) : value; }
}

internal sealed class RunningAppsDialog : Form
{
    private readonly ListView list = new ListView();
    private readonly List<AppCandidate> candidates;
    public List<AppCandidate> SelectedCandidates { get; private set; }

    public RunningAppsDialog(List<AppCandidate> items)
    {
        candidates = items;
        SelectedCandidates = new List<AppCandidate>();
        Text = "选择正在运行的应用";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 430);
        Font = new Font("Microsoft YaHei UI", 9F);
        list.Location = new Point(16, 16); list.Size = new Size(618, 350); list.View = View.Details; list.CheckBoxes = true; list.FullRowSelect = true; list.GridLines = true;
        list.Columns.Add("应用", 280); list.Columns.Add("PID", 70); list.Columns.Add("程序位置", 250);
        foreach (AppCandidate item in items) { ListViewItem row = new ListViewItem(item.Name); row.Tag = item; row.SubItems.Add(item.ProcessId.ToString()); row.SubItems.Add(item.Path); list.Items.Add(row); }
        Controls.Add(list);
        Button ok = new Button(); ok.Text = "加入箩筐"; ok.Location = new Point(438, 382); ok.Size = new Size(94, 32); ok.Click += delegate { SelectedCandidates = list.CheckedItems.Cast<ListViewItem>().Select(i => (AppCandidate)i.Tag).ToList(); if (SelectedCandidates.Count == 0) return; DialogResult = DialogResult.OK; Close(); }; Controls.Add(ok);
        Button cancel = new Button(); cancel.Text = "取消"; cancel.Location = new Point(540, 382); cancel.Size = new Size(94, 32); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}

internal sealed class DesktopAppsDialog : Form
{
    private readonly ListView list = new ListView();
    public List<DesktopAppCandidate> SelectedCandidates { get; private set; }
    public bool BrowseOtherFile { get; private set; }

    public DesktopAppsDialog(List<DesktopAppCandidate> items)
    {
        SelectedCandidates = new List<DesktopAppCandidate>();
        Text = "从全部桌面选择应用";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(690, 480);
        MinimumSize = new Size(620, 400);
        Font = new Font("Microsoft YaHei UI", 9F);

        Label hint = new Label();
        hint.Text = "这里同时显示个人桌面和公共桌面的应用快捷方式。文件夹和文档不会显示。";
        hint.Location = new Point(16, 14); hint.Size = new Size(650, 24); hint.ForeColor = Color.FromArgb(93, 103, 116);
        Controls.Add(hint);

        list.Location = new Point(16, 44); list.Size = new Size(658, 368); list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.View = View.Details; list.CheckBoxes = true; list.FullRowSelect = true; list.GridLines = true; list.HideSelection = false;
        list.Columns.Add("应用", 250); list.Columns.Add("所在桌面", 105); list.Columns.Add("快捷方式位置", 285);
        foreach (DesktopAppCandidate item in items)
        {
            ListViewItem row = new ListViewItem(item.Name); row.Tag = item; row.SubItems.Add(item.DesktopLocation); row.SubItems.Add(item.ShortcutPath); list.Items.Add(row);
        }
        Controls.Add(list);

        Button browse = new Button(); browse.Text = "从其他位置选择…"; browse.Location = new Point(16, 428); browse.Size = new Size(140, 34); browse.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        browse.Click += delegate { BrowseOtherFile = true; DialogResult = DialogResult.Retry; Close(); }; Controls.Add(browse);
        Button ok = new Button(); ok.Text = "加入箩筐"; ok.Location = new Point(474, 428); ok.Size = new Size(94, 34); ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        ok.Click += delegate { SelectedCandidates = list.CheckedItems.Cast<ListViewItem>().Select(i => (DesktopAppCandidate)i.Tag).ToList(); if (SelectedCandidates.Count == 0) return; DialogResult = DialogResult.OK; Close(); }; Controls.Add(ok);
        Button cancel = new Button(); cancel.Text = "取消"; cancel.Location = new Point(580, 428); cancel.Size = new Size(94, 34); cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}

internal sealed class InstalledAppsDialog : Form
{
    private readonly TextBox searchBox = new TextBox();
    private readonly ListView list = new ListView();
    private readonly Label countLabel = new Label();
    private readonly Label hintLabel = new Label();
    private readonly List<InstalledAppCandidate> candidates;
    private readonly HashSet<string> selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool rebuilding;
    public List<InstalledAppCandidate> SelectedCandidates { get; private set; }

    public InstalledAppsDialog(List<InstalledAppCandidate> items)
    {
        candidates = items ?? new List<InstalledAppCandidate>();
        SelectedCandidates = new List<InstalledAppCandidate>();
        Text = "已安装应用库";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(720, 460);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);

        Label title = new Label();
        title.Text = "搜索已安装应用"; title.Location = new Point(18, 16); title.Size = new Size(130, 24);
        title.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold); Controls.Add(title);
        searchBox.Location = new Point(150, 13); searchBox.Size = new Size(430, 26); searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        searchBox.TextChanged += delegate { RebuildList(); }; Controls.Add(searchBox);
        countLabel.Location = new Point(594, 16); countLabel.Size = new Size(206, 23); countLabel.TextAlign = ContentAlignment.TopRight;
        countLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right; countLabel.ForeColor = Color.FromArgb(93, 103, 116); Controls.Add(countLabel);

        hintLabel.Text = "“已适配”可直接使用；“通用尝试”不保证采用代理；灰色项目只识别、不允许加入。";
        hintLabel.Location = new Point(18, 48); hintLabel.Size = new Size(780, 24); hintLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        hintLabel.ForeColor = Color.FromArgb(93, 103, 116); Controls.Add(hintLabel);

        list.Location = new Point(18, 78); list.Size = new Size(782, 414); list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.View = View.Details; list.CheckBoxes = true; list.FullRowSelect = true; list.GridLines = true; list.HideSelection = false;
        list.Columns.Add("应用", 220); list.Columns.Add("兼容性", 235); list.Columns.Add("来源", 150); list.Columns.Add("程序位置", 250);
        list.ItemCheck += OnItemCheck;
        Controls.Add(list);

        Button ok = new Button(); ok.Text = "加入箩筐"; ok.Location = new Point(594, 510); ok.Size = new Size(98, 34);
        ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        ok.Click += delegate
        {
            SelectedCandidates = candidates.Where(c => c.CanAdd && selectedKeys.Contains(c.Key)).ToList();
            if (SelectedCandidates.Count == 0)
            {
                MessageBox.Show(this, "请先勾选至少一个可以加入的应用。", "尚未选择应用", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK; Close();
        };
        Controls.Add(ok);
        Button cancel = new Button(); cancel.Text = "取消"; cancel.Location = new Point(702, 510); cancel.Size = new Size(98, 34);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        RebuildList();
    }

    private void OnItemCheck(object sender, ItemCheckEventArgs e)
    {
        if (rebuilding || e.Index < 0 || e.Index >= list.Items.Count) return;
        InstalledAppCandidate candidate = list.Items[e.Index].Tag as InstalledAppCandidate;
        if (candidate == null) return;
        if (!candidate.CanAdd)
        {
            e.NewValue = CheckState.Unchecked;
            hintLabel.ForeColor = Color.FromArgb(184, 116, 17);
            hintLabel.Text = candidate.Name + " 目前只有应用标识，没有可注入代理的启动程序，因此不能加入。";
            return;
        }
        if (e.NewValue == CheckState.Checked) selectedKeys.Add(candidate.Key);
        else selectedKeys.Remove(candidate.Key);
    }

    private void RebuildList()
    {
        string keyword = searchBox.Text.Trim();
        IEnumerable<InstalledAppCandidate> filtered = candidates;
        if (!String.IsNullOrWhiteSpace(keyword))
            filtered = filtered.Where(c => (c.Name ?? "").IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (c.Source ?? "").IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (c.Compatibility ?? "").IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0);
        List<InstalledAppCandidate> visible = filtered.ToList();
        rebuilding = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (InstalledAppCandidate candidate in visible)
            {
                ListViewItem row = new ListViewItem(candidate.Name); row.Tag = candidate;
                row.SubItems.Add(candidate.Compatibility); row.SubItems.Add(candidate.Source); row.SubItems.Add(candidate.CanAdd ? candidate.Path : "—");
                row.Checked = candidate.CanAdd && selectedKeys.Contains(candidate.Key);
                if (!candidate.CanAdd) row.ForeColor = Color.FromArgb(140, 147, 157);
                else if (candidate.Compatibility.StartsWith("已适配", StringComparison.Ordinal)) row.ForeColor = Color.FromArgb(25, 135, 84);
                list.Items.Add(row);
            }
        }
        finally { list.EndUpdate(); rebuilding = false; }
        int addable = visible.Count(c => c.CanAdd);
        countLabel.Text = visible.Count + " 项 · " + addable + " 项可加入";
    }
}

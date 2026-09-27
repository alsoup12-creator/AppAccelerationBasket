using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[ComImport, Guid("F158268A-D5A5-45CE-99CF-00D6C3F3FC0A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBasketDesktopActivator {
 [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPWStr)] string id,[MarshalAs(UnmanagedType.LPWStr)] string exe,[MarshalAs(UnmanagedType.LPWStr)] string args,out IntPtr process);
 [PreserveSig] int ActivateWithOptions([MarshalAs(UnmanagedType.LPWStr)] string id,[MarshalAs(UnmanagedType.LPWStr)] string exe,[MarshalAs(UnmanagedType.LPWStr)] string args,uint options,uint parent,out IntPtr process);
}
[ComImport, Guid("168EB462-775F-42AE-9111-D714B2306C2E")] internal class BasketDesktopActivator { }
internal sealed class ProxyLaunchRequest { public string Id; public string Host; public int Port; public string Type; public string Arguments; public bool Probe; }
internal sealed class LaunchReceiptPendingException : Exception { public LaunchReceiptPendingException() : base("启动回报尚未确认。请观察 Codex；当前网络模式暂时保留，未自动判定失败。关闭箩筐可恢复原网络。") { } }
internal sealed class ProxyLaunchResult { public string Id; public int Pid; public string Error; public string Identity; }
internal static class PackagedProxy {
 internal static readonly string State = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppAccelerationBasket");
 internal static readonly string Logs = Path.Combine(State,"logs");
 static string ResultPath(string id) { Guid parsed; if(!Guid.TryParseExact(id,"N",out parsed)) throw new ArgumentException("Invalid request id"); return Path.Combine(State,"launch-results",id+".json"); }
 internal static void Log(string id,string action,string detail) {
  try {Directory.CreateDirectory(Logs); string path=Path.Combine(Logs,"basket-"+DateTime.Now.ToString("yyyy-MM-dd")+"-"+Process.GetCurrentProcess().Id+".log");
   if(File.Exists(path)&&new FileInfo(path).Length>4*1024*1024)return;
   File.AppendAllText(path,DateTimeOffset.Now.ToString("o")+" request="+id+" "+action+" "+detail.Replace("\r"," ").Replace("\n"," ")+Environment.NewLine,new UTF8Encoding(false));
  } catch { }
 }
 internal static bool HandleMode(string[] args) {
  if(args.Length==1&&args[0]=="--basket-identity-probe") {Log("probe","child.identity",Identity(Process.GetCurrentProcess().Handle)+" proxyMarker="+(Environment.GetEnvironmentVariable("HTTP_PROXY")=="http://127.0.0.1:15236")); try { LaunchVerified(Path.Combine(CurrentPackagePath(),"app","ChatGPT.exe"),"","http://127.0.0.1:15236",Identity(Process.GetCurrentProcess().Handle),"probe-image",false); Log("probe-image","suspended_codex_test","PASS; applicationCodeNeverResumed=true"); } catch(Exception e) { Log("probe-image","suspended_codex_test","FAIL "+e.GetType().Name+" "+e.HResult.ToString("X8")); Environment.ExitCode=1; } return true;}
  if(args.Length!=2||args[0]!="--basket-codex-helper")return false;
  ProxyLaunchRequest r=null;
  try {
   r=new JavaScriptSerializer().Deserialize<ProxyLaunchRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
   string resultPath=ResultPath(r.Id); Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
   if(Uri.CheckHostName(r.Host.Trim('[',']'))==UriHostNameType.Unknown||r.Port<1||r.Port>65535||(r.Type!="HTTP"&&r.Type!="SOCKS5"))throw new ArgumentException("代理配置无效");
   string identity=Identity(Process.GetCurrentProcess().Handle);
   if(!identity.StartsWith("OpenAI.Codex_",StringComparison.Ordinal))throw new InvalidOperationException("辅助进程没有 Codex 包身份");
   Log(r.Id,"helper.identity",identity);
   string exe=r.Probe?typeof(PackagedProxy).Assembly.Location:Path.Combine(CurrentPackagePath(),"app","ChatGPT.exe");
   string host=r.Host.Trim('[',']'); if(host.Contains(":"))host="["+host+"]";
   string proxy=(r.Type=="SOCKS5"?"socks5://":"http://")+host+":"+r.Port;
   string envProxy=(r.Type=="SOCKS5"?"socks5h://":"http://")+host+":"+r.Port;
   string arguments=r.Probe?"--basket-identity-probe":(r.Arguments??"")+" --proxy-server="+proxy+" --proxy-bypass-list=localhost;127.0.0.1;[::1] --disable-quic";
   int pid=LaunchVerified(exe,arguments,envProxy,identity,r.Id);
   SendResult(r,new ProxyLaunchResult{Id=r.Id,Pid=pid,Identity=identity});
   if(!r.Probe)Monitor(pid,r);
  } catch(Exception e) {
   string detail=e.GetType().Name+" hresult="+e.HResult.ToString("X8");var w=e as Win32Exception;if(w!=null)detail+=" win32="+w.NativeErrorCode;
   Log(r==null?"unknown":r.Id,"helper.error",detail);
   if(r!=null)SendResult(r,new ProxyLaunchResult{Id=r.Id,Error="专用启动失败："+detail+"。详见本地日志。"});
  }
  return true;
 }
 static string PipeName(string id) { ResultPath(id); return "BasketReceipt-"+id; }
 static void SendResult(ProxyLaunchRequest r,ProxyLaunchResult value) {
  byte[] payload=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
  try {
   using(var pipe=new NamedPipeClientStream(".",PipeName(r.Id),PipeDirection.InOut,PipeOptions.Asynchronous)) {
    pipe.Connect(10000);
    byte[] length=BitConverter.GetBytes(payload.Length);
    pipe.Write(length,0,length.Length);pipe.Write(payload,0,payload.Length);pipe.Flush();
    Log(r.Id,"receipt.sent","transport=named-pipe; pid="+value.Pid+" error="+(!String.IsNullOrEmpty(value.Error)));
    byte[] ack=ReadPipe(pipe,1,DateTime.UtcNow.AddSeconds(5));
    Log(r.Id,"receipt.acknowledged","received="+(ack[0]==1));
   }
  } catch(Exception e) { Log(r.Id,"receipt.send_unconfirmed",e.GetType().Name+" hresult="+e.HResult.ToString("X8")); }
  // Diagnostic copy only. It is no longer the communication channel.
  try {string path=ResultPath(r.Id);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,new JavaScriptSerializer().Serialize(value));}catch(Exception e){Log(r.Id,"receipt.file_error",e.GetType().Name);}
 }
 static byte[] ReadPipe(Stream pipe,int count,DateTime deadline) {
  byte[] data=new byte[count];int offset=0;
  while(offset<count){int remaining=(int)Math.Max(0,(deadline-DateTime.UtcNow).TotalMilliseconds);if(remaining==0)throw new TimeoutException();var read=pipe.ReadAsync(data,offset,count-offset);if(!read.Wait(remaining))throw new TimeoutException();int n=read.Result;if(n==0)throw new EndOfStreamException();offset+=n;}return data;
 }
 internal static int Start(string host,int port,string type,string arguments,bool probe) {
  var r=new ProxyLaunchRequest{Id=Guid.NewGuid().ToString("N"),Host=host,Port=port,Type=type,Arguments=arguments,Probe=probe};
  string result=ResultPath(r.Id);string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(r)));
  IBasketDesktopActivator manager=null;IntPtr process=IntPtr.Zero;
  Log(r.Id,"activation.request","method=DesktopActivator2; endpoint="+type+":"+host+":"+port+"; systemProxyUnchanged=true; probe="+probe);
  var security=new PipeSecurity();security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
  using(var receipt=new NamedPipeServerStream(PipeName(r.Id),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security))
  try {
   var connected=receipt.BeginWaitForConnection(null,null);
   Log(r.Id,"receipt.listening","transport=named-pipe");
   manager=(IBasketDesktopActivator)new BasketDesktopActivator();
   int hr=manager.ActivateWithOptions("OpenAI.Codex_2p2nqsd0c76g0!App",typeof(PackagedProxy).Assembly.Location,"--basket-codex-helper "+encoded,6|8,(uint)Process.GetCurrentProcess().Id,out process);
   if(hr<0)Marshal.ThrowExceptionForHR(hr);
   ProxyLaunchResult status;
   try {
    if(!connected.AsyncWaitHandle.WaitOne(15000))throw new TimeoutException();
    receipt.EndWaitForConnection(connected);
    DateTime deadline=DateTime.UtcNow.AddSeconds(5);
    int length=BitConverter.ToInt32(ReadPipe(receipt,4,deadline),0);
    if(length<1||length>16384)throw new InvalidDataException("Invalid receipt size");
    status=new JavaScriptSerializer().Deserialize<ProxyLaunchResult>(Encoding.UTF8.GetString(ReadPipe(receipt,length,deadline)));
    if(status==null||status.Id!=r.Id||(String.IsNullOrEmpty(status.Error)&&(status.Pid<=0||String.IsNullOrEmpty(status.Identity)||!status.Identity.StartsWith("OpenAI.Codex_",StringComparison.Ordinal))))throw new InvalidDataException("Invalid receipt");
    Log(r.Id,"receipt.received","pid="+status.Pid+" error="+(!String.IsNullOrEmpty(status.Error)));
    try { receipt.WriteByte(1);receipt.Flush(); } catch(Exception ackError) { Log(r.Id,"receipt.ack_error",ackError.GetType().Name+"; validatedReceiptRetained=true"); }
   } catch(Exception e) {
    Log(r.Id,"receipt.pending",e.GetType().Name+"; launchOutcome=unknown; automaticRollback=false");
    throw new LaunchReceiptPendingException();
   }
   if(!String.IsNullOrEmpty(status.Error))throw new InvalidOperationException(status.Error);
   Log(r.Id,"activation.accepted","pid="+status.Pid+" identity="+status.Identity+"; appReady=unverified; routing=unverified");return status.Pid;
  } finally {if(process!=IntPtr.Zero)CloseHandle(process);if(manager!=null)Marshal.FinalReleaseComObject(manager);}
 }
 static string CurrentPackagePath(){int n=0;int rc=GetCurrentPackagePath(ref n,null);if(rc!=122)throw new Win32Exception(rc);var b=new StringBuilder(n);rc=GetCurrentPackagePath(ref n,b);if(rc!=0)throw new Win32Exception(rc);return b.ToString();}
 internal static string Identity(IntPtr process){int n=0;int rc=GetPackageFullName(process,ref n,null);if(rc!=122)throw new Win32Exception(rc);var b=new StringBuilder(n);rc=GetPackageFullName(process,ref n,b);if(rc!=0)throw new Win32Exception(rc);return b.ToString();}
 static int LaunchVerified(string exe,string args,string proxy,string expected,string request,bool run = true) {
  var values=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(DictionaryEntry pair in Environment.GetEnvironmentVariables())values[(string)pair.Key]=(string)pair.Value;
  foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy"})values[key]=proxy;
  values["NO_PROXY"]="localhost,127.0.0.1,::1";values["NODE_USE_ENV_PROXY"]="1";
  var block=new StringBuilder();foreach(var pair in values)block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');block.Append('\0');
  IntPtr environment=Marshal.StringToHGlobalUni(block.ToString()); PROCESS_INFORMATION pi=new PROCESS_INFORMATION();bool resumed=false;
  try {STARTUPINFO si=new STARTUPINFO();si.cb=Marshal.SizeOf(typeof(STARTUPINFO));
   if(!CreateProcessW(exe,new StringBuilder("\""+exe+"\" "+args),IntPtr.Zero,IntPtr.Zero,false,0x400|4,environment,Path.GetDirectoryName(exe),ref si,out pi))throw new Win32Exception();
   string actual=Identity(pi.process);if(actual!=expected)throw new InvalidOperationException("包身份不匹配，已阻止启动");
   Log(request,"child.identity_verified","pid="+pi.pid+" package="+actual+"; proxyEnvironmentSupplied=true; proxySwitchSupplied=true");
   if(!run)return pi.pid;
   if(ResumeThread(pi.thread)==UInt32.MaxValue)throw new Win32Exception();resumed=true;return pi.pid;
  }finally{if(pi.process!=IntPtr.Zero){if(!resumed)TerminateProcess(pi.process,1);CloseHandle(pi.process);}if(pi.thread!=IntPtr.Zero)CloseHandle(pi.thread);Marshal.FreeHGlobal(environment);}
 }
 static void Monitor(int root,ProxyLaunchRequest r) {
  try {
   var known=new Dictionary<int,DateTime>();using(var p=Process.GetProcessById(root))known[root]=p.StartTime.ToUniversalTime();
   var endpoint=new HashSet<string>(Dns.GetHostAddresses(r.Host.Trim('[',']')).Select(a=>a.ToString()));bool everProxy=false;bool everExternal=false;
   for(int tick=0;tick<30;tick++) {
    var live=new HashSet<int>();foreach(var pair in known.ToArray())try{using(var p=Process.GetProcessById(pair.Key)){if(p.StartTime.ToUniversalTime()==pair.Value&&!p.HasExited)live.Add(pair.Key);}}catch{}
    using(var query=new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))using(var rows=query.Get()) {
     var links=new List<Tuple<int,int>>();foreach(ManagementObject row in rows){links.Add(Tuple.Create(Convert.ToInt32(row["ProcessId"]),Convert.ToInt32(row["ParentProcessId"])));row.Dispose();}
     bool more;do{more=false;foreach(var link in links)if(live.Contains(link.Item2)&&!live.Contains(link.Item1))try{using(var p=Process.GetProcessById(link.Item1)){var started=p.StartTime.ToUniversalTime();if(started>=known[link.Item2]){known[link.Item1]=started;live.Add(link.Item1);more=true;}}}catch{}}while(more);
    }
    if(live.Count==0){Log(r.Id,"process_tree.exited","root="+root);break;}
    int proxied=0,external=0;foreach(var c in Connections())if(live.Contains(c.Pid)){if(c.Port==r.Port&&endpoint.Contains(c.Address))proxied++;else if(!IPAddress.IsLoopback(IPAddress.Parse(c.Address)))external++;}
    everProxy|=proxied>0;everExternal|=external>0;
    Log(r.Id,"network.sample","root="+root+" processes="+live.Count+" establishedToProxy="+proxied+" otherRemoteTcp="+external+"; coverage=TCPv4+TCPv6; udp=unverified");
    Thread.Sleep(3000);
   }
   Log(r.Id,"network.summary","proxyObserved="+everProxy+" otherRemoteObserved="+everExternal+"; allTrafficCoverage=unverified");
  }catch(Exception e){Log(r.Id,"monitor.error",e.GetType().Name+" hresult="+e.HResult.ToString("X8"));}
 }
 sealed class Connection {public int Pid,Port;public string Address;}
 static List<Connection> Connections(){var result=new List<Connection>();foreach(int family in new[]{2,23}){int size=0;uint rc=GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,5,0);if(rc!=122&&rc!=0)throw new Win32Exception((int)rc);IntPtr b=Marshal.AllocHGlobal(size);try{rc=GetExtendedTcpTable(b,ref size,false,family,5,0);if(rc!=0)throw new Win32Exception((int)rc);int count=Marshal.ReadInt32(b);int stride=family==2?24:56;for(int i=0;i<count;i++){IntPtr row=IntPtr.Add(b,4+i*stride);int state=Marshal.ReadInt32(row,family==2?0:48);if(state!=5)continue;byte[] address=new byte[family==2?4:16];Marshal.Copy(IntPtr.Add(row,family==2?12:24),address,0,address.Length);int off=family==2?16:44;int port=Marshal.ReadByte(row,off)*256+Marshal.ReadByte(row,off+1);int pid=Marshal.ReadInt32(row,family==2?20:52);result.Add(new Connection{Pid=pid,Port=port,Address=new IPAddress(address).ToString()});}}finally{Marshal.FreeHGlobal(b);}}return result;}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct STARTUPINFO {public int cb;public string reserved,desktop,title;public int x,y,xsize,ysize,xc,yc,fill,flags;public short show,cbreserved;public IntPtr reserved2,input,output,error;}
 [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION {public IntPtr process,thread;public int pid,tid;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcessW(string file,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref STARTUPINFO si,out PROCESS_INFORMATION pi);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetCurrentPackagePath(ref int n,StringBuilder path);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetPackageFullName(IntPtr process,ref int n,StringBuilder name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
 [DllImport("kernel32.dll")]static extern bool TerminateProcess(IntPtr h,uint code);
 [DllImport("kernel32.dll")]static extern uint WaitForSingleObject(IntPtr h,uint timeout);
 [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
}




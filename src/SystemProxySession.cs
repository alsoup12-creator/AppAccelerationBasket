using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

internal sealed class ProxySnapshot {
 public int Flags; public string Server; public string Bypass; public string Script;
 public ProxySnapshot Direct(){return new ProxySnapshot{Flags=1,Server=Server,Bypass=Bypass,Script=Script};}
 public bool Same(ProxySnapshot other){return other!=null&&Flags==other.Flags&&Server==other.Server&&Bypass==other.Bypass&&Script==other.Script;}
}
internal sealed class ProxyLease { public ProxySnapshot Before; public ProxySnapshot Applied; }
internal sealed class SystemProxySession {
 readonly string file; readonly Func<ProxySnapshot> read; readonly Action<int> write; readonly Action<string,string> log;
 public bool Active {get{return File.Exists(file);}}
 public SystemProxySession(string file,Func<ProxySnapshot> read,Action<int> write,Action<string,string> log){this.file=file;this.read=read;this.write=write;this.log=log;}
 public bool Enter() {
  var current=read();log("proxy.before","flags="+current.Flags);
  if(Active){var saved=Load();if(current.Same(saved.Applied))return false;Restore("external-change");current=read();}
  if(current.Flags==1){log("proxy.unchanged","alreadyDirect=true");return false;}
  var lease=new ProxyLease{Before=current,Applied=current.Direct()};Directory.CreateDirectory(Path.GetDirectoryName(file));
  string temp=file+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(lease));File.Move(temp,file);
  try{write(1);var after=read();if(!after.Same(lease.Applied))throw new InvalidOperationException("网络设置已被其他程序改变，已停止启动。请查看日志。");log("proxy.enter","beforeFlags="+current.Flags+" afterFlags="+after.Flags);return true;}
  catch{try{Restore("enter-failed");}catch(Exception e){log("proxy.rollback_error",e.GetType().Name+" "+e.HResult.ToString("X8"));}throw;}
 }
 ProxyLease Load(){var lease=new JavaScriptSerializer().Deserialize<ProxyLease>(File.ReadAllText(file));if(lease==null||lease.Before==null||lease.Applied==null)throw new InvalidOperationException("网络恢复记录无效；未覆盖当前设置。");return lease;}
 public void Restore(string reason) {
  if(!Active)return;var lease=Load();var current=read();
  if(current.Same(lease.Before)){File.Delete(file);log("proxy.restore","alreadyRestored=true; reason="+reason);return;}
  if(!current.Same(lease.Applied)){File.Delete(file);log("proxy.restore_skipped","externalChange=true; currentFlags="+current.Flags+"; reason="+reason);return;}
  write(lease.Before.Flags);if(!read().Same(lease.Before))throw new InvalidOperationException("恢复网络设置后核对不一致；恢复记录已保留。");File.Delete(file);log("proxy.restore","flags="+lease.Before.Flags+"; reason="+reason);
 }
}
internal static class WinInetProxy {
 [StructLayout(LayoutKind.Explicit)]struct Value { [FieldOffset(0)]public int Number;[FieldOffset(0)]public IntPtr Text;[FieldOffset(0)]public long FileTime; }
 [StructLayout(LayoutKind.Sequential)]struct Option {public int Kind;public Value Data;}
 [StructLayout(LayoutKind.Sequential)]struct OptionList {public int Size;public IntPtr Connection;public int Count,Error;public IntPtr Options;}
 [DllImport("wininet.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool InternetQueryOptionW(IntPtr h,int option,ref OptionList list,ref int size);
 [DllImport("wininet.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool InternetSetOptionW(IntPtr h,int option,ref OptionList list,int size);
 [DllImport("wininet.dll",EntryPoint="InternetSetOptionW",SetLastError=true)]static extern bool Notify(IntPtr h,int option,IntPtr buffer,int size);
 [DllImport("kernel32.dll")]static extern IntPtr GlobalFree(IntPtr h);
 public static ProxySnapshot Read(){int stride=Marshal.SizeOf(typeof(Option));IntPtr memory=Marshal.AllocHGlobal(stride*4);bool obtained=false;
  try{int[] kinds={10,2,3,4};for(int i=0;i<4;i++)Marshal.StructureToPtr(new Option{Kind=kinds[i]},IntPtr.Add(memory,i*stride),false);
   var list=new OptionList{Size=Marshal.SizeOf(typeof(OptionList)),Count=4,Options=memory};int size=list.Size;
   if(!InternetQueryOptionW(IntPtr.Zero,75,ref list,ref size))throw new Win32Exception();obtained=true;
   var result=new ProxySnapshot();for(int i=0;i<4;i++){var item=(Option)Marshal.PtrToStructure(IntPtr.Add(memory,i*stride),typeof(Option));if(i==0)result.Flags=item.Data.Number;else{string text=Marshal.PtrToStringUni(item.Data.Text)??"";if(i==1)result.Server=text;if(i==2)result.Bypass=text;if(i==3)result.Script=text;}}return result;
  }finally{if(obtained)for(int i=1;i<4;i++){var item=(Option)Marshal.PtrToStructure(IntPtr.Add(memory,i*stride),typeof(Option));if(item.Data.Text!=IntPtr.Zero)GlobalFree(item.Data.Text);}Marshal.FreeHGlobal(memory);}
 }
 public static void WriteFlags(int flags){var item=new Option{Kind=1,Data=new Value{Number=flags}};IntPtr memory=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Option)));
  try{Marshal.StructureToPtr(item,memory,false);var list=new OptionList{Size=Marshal.SizeOf(typeof(OptionList)),Count=1,Options=memory};if(!InternetSetOptionW(IntPtr.Zero,75,ref list,list.Size))throw new Win32Exception();
   Notify(IntPtr.Zero,39,IntPtr.Zero,0);Notify(IntPtr.Zero,37,IntPtr.Zero,0);
  }finally{Marshal.FreeHGlobal(memory);}
 }
}

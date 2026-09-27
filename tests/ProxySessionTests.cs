using System;
using System.IO;
class ProxySessionTests {
 static int Main(string[] args){try{if(args.Length>0){var live=WinInetProxy.Read();Console.WriteLine("Read-only WinINet flags="+live.Flags+" manual="+((live.Flags&2)!=0)+" script="+((live.Flags&4)!=0)+" autoDetect="+((live.Flags&8)!=0));return 0;}
 string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-lease.json");ProxySnapshot current=new ProxySnapshot{Flags=11,Server="local",Bypass="local",Script=""};int writes=0;
 var session=new SystemProxySession(path,()=>current,f=>{writes++;current=new ProxySnapshot{Flags=f,Server=current.Server,Bypass=current.Bypass,Script=current.Script};},(a,d)=>{});
 if(!session.Enter()||current.Flags!=1||!session.Active)throw new Exception("enter");if(session.Enter()||writes!=1)throw new Exception("reuse");session.Restore("failure");if(current.Flags!=11||session.Active)throw new Exception("rollback");Console.WriteLine("enter/reuse/rollback PASS");
 session.Enter();var recovered=new SystemProxySession(path,()=>current,f=>{current=new ProxySnapshot{Flags=f,Server=current.Server,Bypass=current.Bypass,Script=current.Script};},(a,d)=>{});recovered.Restore("crash-recovery");if(current.Flags!=11)throw new Exception("recovery");Console.WriteLine("crash recovery PASS");
 session.Enter();current.Server="user-new-proxy";session.Restore("close");if(current.Flags!=1||current.Server!="user-new-proxy"||session.Active)throw new Exception("conflict");Console.WriteLine("external change preserved PASS");
 if(session.Enter()||session.Active)throw new Exception("direct no-op");Console.WriteLine("already direct no-op PASS");
 current.Flags=3;var broken=new SystemProxySession(path,()=>current,f=>{if(f==1){current=current.Direct();throw new Exception("simulated partial write");}current.Flags=f;},(a,d)=>{});try{broken.Enter();throw new Exception("missing failure");}catch(Exception e){if(e.Message!="simulated partial write")throw;}if(current.Flags!=3||broken.Active)throw new Exception("partial rollback");Console.WriteLine("partial failure rollback PASS");return 0;
 }catch(Exception e){Console.WriteLine(e);return 1;}}
}

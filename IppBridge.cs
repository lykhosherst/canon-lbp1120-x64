using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class IppRequest {
    public byte Major,Minor; public int Op,Id,DocumentOffset;
    public Dictionary<string,List<byte[]>> Attrs=new Dictionary<string,List<byte[]>>();
    public static int U16(byte[] b,int p) {return (b[p]<<8)|b[p+1];}
    public static int I32(byte[] b,int p) {return (b[p]<<24)|(b[p+1]<<16)|(b[p+2]<<8)|b[p+3];}
    public string Text(string name,string fallback) {return Attrs.ContainsKey(name)?Encoding.UTF8.GetString(Attrs[name][0]):fallback;}
    public int Number(string name,int fallback) {return Attrs.ContainsKey(name)&&Attrs[name][0].Length==4?I32(Attrs[name][0],0):fallback;}
    public bool Flag(string name,bool fallback) {return Attrs.ContainsKey(name)&&Attrs[name][0].Length==1?Attrs[name][0][0]!=0:fallback;}
    public static IppRequest Parse(byte[] b) {
        if(b.Length<9) throw new InvalidDataException("Short IPP request");
        IppRequest r=new IppRequest{Major=b[0],Minor=b[1],Op=U16(b,2),Id=I32(b,4)};
        int p=8,collection=0;string name=null;
        while(p<b.Length) {
            int tag=b[p++];
            if(tag==3) {r.DocumentOffset=p;return r;}
            if(tag<16) {name=null;continue;}
            if(p+2>b.Length) break;
            int nl=U16(b,p);p+=2;
            if(p+nl+2>b.Length) break;
            if(nl>0) name=Encoding.UTF8.GetString(b,p,nl);
            p+=nl; int vl=U16(b,p);p+=2;
            if(p+vl>b.Length) break;
            if(tag==0x37) collection--;
            if(collection==0 && tag!=0x37 && tag!=0x34 && name!=null) {
                byte[] value=new byte[vl];Buffer.BlockCopy(b,p,value,0,vl);
                if(!r.Attrs.ContainsKey(name)) r.Attrs[name]=new List<byte[]>();
                r.Attrs[name].Add(value);
            }
            if(tag==0x34) collection++;
            if(collection<0||collection>16) throw new InvalidDataException("Invalid IPP collection");
            p+=vl;
        }
        throw new InvalidDataException("IPP end-of-attributes missing");
    }
}
class IppReply {
    MemoryStream m=new MemoryStream();
    public IppReply(IppRequest r,int status) {m.WriteByte(r.Major);m.WriteByte(r.Minor);Short(status);Int(r.Id);Group(1);Text(0x47,"attributes-charset","utf-8");Text(0x48,"attributes-natural-language","en");}
    void Short(int v){m.WriteByte((byte)(v>>8));m.WriteByte((byte)v);}
    void Int(int v){m.WriteByte((byte)(v>>24));m.WriteByte((byte)(v>>16));m.WriteByte((byte)(v>>8));m.WriteByte((byte)v);}
    public void Group(int v){m.WriteByte((byte)v);}
    public void Value(int tag,string name,byte[] data){m.WriteByte((byte)tag);byte[] n=Encoding.UTF8.GetBytes(name);Short(n.Length);m.Write(n,0,n.Length);Short(data.Length);m.Write(data,0,data.Length);}
    public void Text(int tag,string name,params string[] values){foreach(string v in values){Value(tag,name,Encoding.UTF8.GetBytes(v));name="";}}
    public void Number(int tag,string name,params int[] values){foreach(int v in values){Value(tag,name,new byte[]{(byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v});name="";}}
    public void Bool(string name,bool value){Value(0x22,name,new byte[]{(byte)(value?1:0)});}
    public void Range(string name,int lo,int hi){Value(0x33,name,new byte[]{(byte)(lo>>24),(byte)(lo>>16),(byte)(lo>>8),(byte)lo,(byte)(hi>>24),(byte)(hi>>16),(byte)(hi>>8),(byte)hi});}
    public void Resolution(string name){Value(0x32,name,new byte[]{0,0,2,88,0,0,2,88,3});}
    public void Begin(string name){Value(0x34,name,new byte[0]);}
    public void Member(string name){Text(0x4a,"",name);}
    public void End(){Value(0x37,"",new byte[0]);}
    public byte[] Finish(){Group(3);return m.ToArray();}
}
class BridgeJob {
    public int Id,State=4,Pages=0,Copies=1; public string Name,Format,Document,Error="",Directory; public bool Cancel;
    public Process Worker;public DateTime Created=DateTime.UtcNow;
}
class IppBridge {
    const int Port=8631,MaxBody=128*1024*1024;
    const string PrinterUri="ipp://127.0.0.1:8631/ipp/print";
    const string PrinterName="Canon LBP-1120 x64";
    readonly string root=AppDomain.CurrentDomain.BaseDirectory;
    readonly object gate=new object(),logGate=new object();
    readonly Dictionary<int,BridgeJob> jobs=new Dictionary<int,BridgeJob>();
    readonly Queue<BridgeJob> pending=new Queue<BridgeJob>();
    readonly AutoResetEvent jobReady=new AutoResetEvent(false);
    readonly DateTime started=DateTime.UtcNow;
    bool dryRun;int lastId;BridgeJob active;
    void Log(string text){lock(logGate){string line=DateTime.Now.ToString("s")+" "+text;Console.WriteLine(line);File.AppendAllText(Path.Combine(root,"bridge.log"),line+Environment.NewLine,Encoding.UTF8);}}
    int UpTime(){return (int)(DateTime.UtcNow-started).TotalSeconds;}
    static string ReadLine(Stream stream,int max){using(MemoryStream m=new MemoryStream()){while(m.Length<max){int c=stream.ReadByte();if(c<0)throw new EndOfStreamException();if(c==10)return Encoding.ASCII.GetString(m.ToArray()).TrimEnd('\r');m.WriteByte((byte)c);}throw new InvalidDataException("HTTP line too long");}}
    static void CopyExact(Stream from,Stream to,int count){byte[] b=new byte[65536];while(count>0){int n=from.Read(b,0,Math.Min(count,b.Length));if(n<=0)throw new EndOfStreamException();to.Write(b,0,n);count-=n;}}
    static void Http(Stream s,int status,string contentType,byte[] body){byte[] h=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+(status==200?" OK":" Error")+"\r\nContent-Type: "+contentType+"\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");s.Write(h,0,h.Length);s.Write(body,0,body.Length);}
    void Client(object obj){using(TcpClient client=(TcpClient)obj){client.ReceiveTimeout=60000;client.SendTimeout=60000;using(NetworkStream stream=client.GetStream()){try{
        string requestLine=ReadLine(stream,8192);string[] first=requestLine.Split(' ');
        Dictionary<string,string> h=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        int headerSize=requestLine.Length;
        for(;;){string line=ReadLine(stream,8192);headerSize+=line.Length;if(headerSize>65536)throw new InvalidDataException("HTTP headers too large");if(line.Length==0)break;int split=line.IndexOf(':');if(split>0)h[line.Substring(0,split)]=line.Substring(split+1).Trim();}
        if(first.Length<2)throw new InvalidDataException("Invalid HTTP request");
        if(first[0]=="GET"){Http(stream,200,"text/plain; charset=utf-8",Encoding.UTF8.GetBytes(PrinterName+"\nCAPT bridge is running.\n"+(dryRun?"DRY RUN: no physical printing\n":"")));return;}
        if(first[0]!="POST"||first[1]!="/ipp/print"){Http(stream,404,"text/plain",Encoding.ASCII.GetBytes("Not found"));return;}
        if(!h.ContainsKey("Content-Type")||!h["Content-Type"].StartsWith("application/ipp",StringComparison.OrdinalIgnoreCase)){Http(stream,415,"text/plain",new byte[0]);return;}
        if(h.ContainsKey("Expect")&&h["Expect"].IndexOf("100-continue",StringComparison.OrdinalIgnoreCase)>=0){byte[] cont=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");stream.Write(cont,0,cont.Length);}
        byte[] body;
        using(MemoryStream m=new MemoryStream()){
            if(h.ContainsKey("Transfer-Encoding")&&h["Transfer-Encoding"].Equals("chunked",StringComparison.OrdinalIgnoreCase)){
                for(;;){string line=ReadLine(stream,100);int semi=line.IndexOf(';');if(semi>=0)line=line.Substring(0,semi);int size=int.Parse(line,System.Globalization.NumberStyles.HexNumber);if(size<0||m.Length+size>MaxBody)throw new InvalidDataException("IPP document too large");if(size==0){while(ReadLine(stream,8192)!=""){}break;}CopyExact(stream,m,size);if(ReadLine(stream,2)!="")throw new InvalidDataException("Invalid chunk ending");}
            } else {long size=h.ContainsKey("Content-Length")?long.Parse(h["Content-Length"]):0;if(size<0||size>MaxBody)throw new InvalidDataException("IPP document too large");CopyExact(stream,m,(int)size);}
            body=m.ToArray();
        }
        IppRequest r=IppRequest.Parse(body);Log("IPP op=0x"+r.Op.ToString("X4")+" request="+r.Id+" bytes="+body.Length);
        byte[] response=Handle(r,body);Http(stream,200,"application/ipp",response);
    }catch(Exception ex){Log("HTTP/IPP error: "+ex.Message);try{Http(stream,400,"text/plain",Encoding.UTF8.GetBytes("Invalid print request"));}catch{}}}}}
    void Media(IppReply p,string name){
        p.Begin(name);p.Member("media-size");p.Begin("");p.Member("x-dimension");p.Number(0x21,"",21000);p.Member("y-dimension");p.Number(0x21,"",29700);p.End();
        p.Member("media-size-name");p.Text(0x44,"","iso_a4_210x297mm");
        foreach(string edge in new string[]{"bottom","left","right","top"}){p.Member("media-"+edge+"-margin");p.Number(0x21,"",500);}
        p.Member("media-source");p.Text(0x44,"","main");p.Member("media-type");p.Text(0x44,"","stationery");p.End();
    }
    void Capabilities(IppReply p){
        int queued;lock(gate){queued=pending.Count+(active==null?0:1);}
        p.Group(4);p.Text(0x45,"printer-uri-supported",PrinterUri);p.Text(0x44,"uri-authentication-supported","none");p.Text(0x44,"uri-security-supported","none");
        p.Text(0x42,"printer-name",PrinterName);p.Text(0x41,"printer-info","Canon LBP-1120 native CAPT bridge");p.Text(0x41,"printer-location","This computer (USB)");p.Text(0x41,"printer-make-and-model","Canon LBP-1120 CAPT Bridge");
        p.Text(0x41,"printer-device-id","MFG:Canon;MDL:LBP-1120 CAPT Bridge;CMD:PWGRaster,PDF;CLS:PRINTER;");
        p.Text(0x45,"printer-uuid","urn:uuid:92876a20-d54b-4b02-85f9-04a9262b1120");
        p.Text(0x45,"printer-more-info","http://127.0.0.1:8631/");p.Number(0x23,"printer-state",queued>0?4:3);p.Text(0x44,"printer-state-reasons","none");p.Bool("printer-is-accepting-jobs",true);
        p.Number(0x21,"queued-job-count",queued);p.Number(0x21,"printer-up-time",UpTime());p.Number(0x21,"printer-config-change-time",0);p.Number(0x21,"printer-state-change-time",0);
        p.Text(0x44,"ipp-versions-supported","1.1","2.0");p.Number(0x23,"operations-supported",2,4,5,6,8,9,10,11);p.Text(0x47,"charset-configured","utf-8");p.Text(0x47,"charset-supported","utf-8");p.Text(0x48,"natural-language-configured","en");p.Text(0x48,"generated-natural-language-supported","en");
        p.Text(0x49,"document-format-default","image/pwg-raster");p.Text(0x49,"document-format-supported","image/pwg-raster","application/pdf","image/jpeg");p.Text(0x44,"compression-supported","none");
        p.Bool("color-supported",false);p.Text(0x44,"print-color-mode-supported","monochrome");p.Text(0x44,"print-color-mode-default","monochrome");
        p.Text(0x44,"sides-supported","one-sided");p.Text(0x44,"sides-default","one-sided");p.Resolution("printer-resolution-supported");p.Resolution("printer-resolution-default");p.Resolution("pwg-raster-document-resolution-supported");
        p.Text(0x44,"pwg-raster-document-type-supported","black_1","sgray_8");p.Text(0x44,"pwg-raster-document-sheet-back","normal");
        p.Range("copies-supported",1,1);p.Number(0x21,"copies-default",1);p.Number(0x23,"print-quality-supported",4);p.Number(0x23,"print-quality-default",4);
        p.Number(0x23,"finishings-supported",3);p.Number(0x23,"finishings-default",3);p.Number(0x23,"orientation-requested-supported",3,4);p.Number(0x23,"orientation-requested-default",3);
        p.Text(0x44,"media-supported","iso_a4_210x297mm");p.Text(0x44,"media-default","iso_a4_210x297mm");p.Text(0x44,"media-ready","iso_a4_210x297mm");p.Text(0x44,"media-source-supported","main");p.Text(0x44,"media-source-default","main");p.Text(0x44,"media-type-supported","stationery");p.Text(0x44,"media-type-default","stationery");
        foreach(string edge in new string[]{"bottom","left","right","top"})p.Number(0x21,"media-"+edge+"-margin-supported",500);
        p.Text(0x44,"media-col-supported","media-size","media-size-name","media-source","media-type","media-bottom-margin","media-left-margin","media-right-margin","media-top-margin");
        Media(p,"media-col-database");Media(p,"media-col-default");Media(p,"media-col-ready");
        p.Begin("media-size-supported");p.Member("x-dimension");p.Number(0x21,"",21000);p.Member("y-dimension");p.Number(0x21,"",29700);p.End();
        p.Text(0x44,"multiple-document-handling-supported","single-document");p.Text(0x44,"multiple-document-handling-default","single-document");p.Bool("multiple-document-jobs-supported",false);
        p.Text(0x44,"job-creation-attributes-supported","job-name","media","media-col","copies","sides","orientation-requested","print-color-mode","printer-resolution","print-quality");
        p.Text(0x44,"job-hold-until-supported","no-hold");p.Text(0x44,"job-hold-until-default","no-hold");p.Text(0x44,"job-sheets-supported","none");p.Text(0x44,"job-sheets-default","none");
        p.Number(0x21,"pages-per-minute",10);p.Number(0x21,"number-up-supported",1);p.Number(0x21,"number-up-default",1);p.Bool("page-ranges-supported",false);
    }
    void JobAttrs(IppReply p,BridgeJob j){p.Group(2);p.Number(0x21,"job-id",j.Id);p.Text(0x45,"job-uri",PrinterUri+"/jobs/"+j.Id);p.Text(0x45,"job-printer-uri",PrinterUri);p.Text(0x42,"job-name",j.Name);p.Number(0x23,"job-state",j.State);p.Text(0x44,"job-state-reasons",j.State==9?"job-completed-successfully":j.State==8?"aborted-by-system":j.State==7?"job-canceled-by-user":j.State==4?"job-data-insufficient":"none");p.Text(0x41,"job-state-message",j.Error);p.Number(0x21,"job-impressions-completed",j.Pages);p.Number(0x21,"job-media-sheets-completed",j.Pages);p.Number(0x21,"time-at-creation",(int)(j.Created-started).TotalSeconds);p.Number(0x21,"job-printer-up-time",UpTime());}
    IppReply Reply(IppRequest r,int code,string message){IppReply p=new IppReply(r,code);if(message!=null)p.Text(0x41,"status-message",message);return p;}
    int Validate(IppRequest r){string format=r.Text("document-format","image/pwg-raster");if(format!="image/pwg-raster"&&format!="application/pdf"&&format!="image/jpeg")return 0x040a;if(r.Number("copies",1)!=1||r.Text("sides","one-sided")!="one-sided"||r.Text("media","iso_a4_210x297mm")!="iso_a4_210x297mm")return 0x040b;return 0;}
    BridgeJob NewJob(IppRequest r){
        lock(gate){
            int unfinished=0;List<int> old=new List<int>();
            foreach(BridgeJob existing in jobs.Values){if(existing.State<7)unfinished++;else if(jobs.Count>=256)old.Add(existing.Id);}
            foreach(int oldId in old)jobs.Remove(oldId);
            if(unfinished>=20)throw new InvalidOperationException("Print queue is full");
            int id=++lastId;string dir=Path.Combine(root,"queue",id.ToString("D8")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);BridgeJob j=new BridgeJob{Id=id,Name=r.Text("job-name","Document"),Format=r.Text("document-format","image/pwg-raster"),Directory=dir};jobs.Add(id,j);return j;
        }
    }
    byte[] Handle(IppRequest r,byte[] body){
        if(r.Major!=1&&r.Major!=2)return Reply(r,0x0503,"Unsupported IPP version").Finish();
        if(r.Op==11){IppReply p=Reply(r,0,null);Capabilities(p);return p.Finish();}
        if(r.Op==4)return Reply(r,Validate(r),null).Finish();
        if(r.Op==2||r.Op==5){int error=Validate(r);if(error!=0)return Reply(r,error,"Unsupported print settings").Finish();if(r.Op==2&&r.DocumentOffset==body.Length)return Reply(r,0x0400,"Document missing").Finish();BridgeJob j=NewJob(r);if(r.Op==2)Save(j,body,r.DocumentOffset);IppReply p=Reply(r,0,null);lock(gate)JobAttrs(p,j);return p.Finish();}
        if(r.Op==6||r.Op==8||r.Op==9){int id=r.Number("job-id",0);if(id==0){string uri=r.Text("job-uri","");int.TryParse(uri.Substring(uri.LastIndexOf('/')+1),out id);}BridgeJob j;lock(gate){if(!jobs.TryGetValue(id,out j))return Reply(r,0x0406,"Job not found").Finish();}
            if(r.Op==6){lock(gate){if(j.State!=4)return Reply(r,0x0404,"Job already has a document").Finish();if(!r.Flag("last-document",true))return Reply(r,0x040b,"Only a single document is supported").Finish();j.Format=r.Text("document-format",j.Format);int err=Validate(r);if(err!=0)return Reply(r,err,"Unsupported document").Finish();Save(j,body,r.DocumentOffset);}}
            if(r.Op==8){bool empty=false;lock(gate){if(j.State>=7)return Reply(r,0x0404,"Job already finished").Finish();empty=j.State==4;j.Cancel=true;if(j.State!=5)j.State=7;}if(empty)try{Directory.Delete(j.Directory);}catch{}Log("Cancel requested for job "+id);}
            IppReply p=Reply(r,0,null);lock(gate)JobAttrs(p,j);return p.Finish();}
        if(r.Op==10){IppReply p=Reply(r,0,null);string which=r.Text("which-jobs","not-completed");lock(gate){foreach(BridgeJob j in jobs.Values){if(which=="not-completed"&&j.State>=7)continue;if(which=="completed"&&j.State<7)continue;JobAttrs(p,j);}}return p.Finish();}
        return Reply(r,0x0501,"Operation not supported").Finish();
    }
    void Save(BridgeJob j,byte[] body,int offset){
        if(offset>=body.Length)throw new InvalidDataException("Empty document");
        string name=j.Format=="application/pdf"?"document.pdf":j.Format=="image/jpeg"?"document.jpg":"document.pwg";
        j.Document=Path.Combine(j.Directory,name);using(FileStream f=new FileStream(j.Document,FileMode.CreateNew))f.Write(body,offset,body.Length-offset);
        lock(gate){j.State=3;pending.Enqueue(j);}jobReady.Set();Log("Queued job "+j.Id+" format="+j.Format);
    }
    int RunProcess(BridgeJob j,string exe,string args){
        using(Process p=new Process()){
            p.StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            p.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null){
                if(e.Data.StartsWith("Printer reports page ")&&e.Data.EndsWith(" completed.")) {
                    int page;string[] words=e.Data.Split(' ');if(words.Length>3&&int.TryParse(words[3],out page))lock(gate)j.Pages=page;
                }
                Log("Job "+j.Id+": "+e.Data);
            }};p.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)Log("Job "+j.Id+" error: "+e.Data);};
            p.Start();lock(gate)j.Worker=p;p.BeginOutputReadLine();p.BeginErrorReadLine();
            DateTime deadline=DateTime.UtcNow.AddMinutes(20);
            while(!p.WaitForExit(200)){if(j.Cancel){ // Native CAPT checks this file and releases the printer itself.
                    File.WriteAllText(Path.Combine(j.Directory,"cancel"),"cancel");
                }if(DateTime.UtcNow>deadline){p.Kill();throw new TimeoutException("Worker exceeded 20 minute limit");}}
            p.WaitForExit();lock(gate)j.Worker=null;return p.ExitCode;
        }
    }
    static string Quote(string path){return "\""+path+"\"";}
    void Work(){for(;;){jobReady.WaitOne(1000);BridgeJob j=null;lock(gate){if(pending.Count>0){j=pending.Dequeue();if(j.Cancel)continue;j.State=5;active=j;}}if(j==null)continue;try{
        string raster=Path.Combine(j.Directory,"page.pbm");
        if(j.Format=="image/pwg-raster")PwgRaster.Convert(j.Document,raster);
        else {string args="-NoProfile -ExecutionPolicy RemoteSigned -File "+Quote(Path.Combine(root,"Convert-ToPbm.ps1"))+" -InputFile "+Quote(j.Document)+" -OutputFile "+Quote(raster);if(RunProcess(j,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),args)!=0)throw new InvalidOperationException("Document rendering failed");}
        if(j.Cancel){lock(gate)j.State=7;continue;}
        int code=RunProcess(j,Path.Combine(root,"capt-print.exe"),(dryRun?"--verify-pbm ":"--print-pbm ")+Quote(raster)+(dryRun?"":" --cancel-file "+Quote(Path.Combine(j.Directory,"cancel"))));
        if(code!=0&&!j.Cancel)throw new InvalidOperationException("CAPT printing failed; see bridge.log");
        lock(gate){j.State=j.Cancel?7:9;j.Error=dryRun?"Dry-run validation; nothing printed":"";}
        Log("Job "+j.Id+" finished state="+j.State);
    }catch(Exception e){lock(gate){j.State=j.Cancel?7:8;j.Error=e.Message;}Log("Job "+j.Id+" failed: "+e.Message);
    }finally{lock(gate)active=null;try{foreach(string f in Directory.GetFiles(j.Directory))File.Delete(f);Directory.Delete(j.Directory);}catch(Exception e){Log("Job cleanup: "+e.Message);}}}}
    public void Start(bool dry){dryRun=dry;Directory.CreateDirectory(Path.Combine(root,"queue"));
        // Never replay files left by a crash: that could print a physical page twice.
        if(Directory.GetDirectories(Path.Combine(root,"queue")).Length>0)Log("Old queue files found; they will NOT be automatically replayed.");
        TcpListener listener=new TcpListener(IPAddress.Loopback,Port);listener.Start(16);
        Thread worker=new Thread(Work);worker.IsBackground=true;worker.Start();Log("Listening "+PrinterUri+(dry?" [DRY RUN]":""));
        for(;;){TcpClient client=listener.AcceptTcpClient();ThreadPool.QueueUserWorkItem(Client,client);}
    }
    static int Main(string[] args){try{using(Mutex mutex=new Mutex(false,"Local\\CanonLBP1120IppBridge")){if(!mutex.WaitOne(0)){Console.Error.WriteLine("Bridge already running");return 2;}try{new IppBridge().Start(args.Length>0&&args[0]=="--dry-run");}finally{mutex.ReleaseMutex();}}return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}

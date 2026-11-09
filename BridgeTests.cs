using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

class BridgeTests {
    static int id=100;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
    static IppRequest Send(int op,Action<IppReply> attrs,byte[] document,bool chunked){
        IppReply builder=new IppReply(new IppRequest{Major=2,Minor=0,Id=++id},op);
        builder.Text(0x45,"printer-uri","ipp://127.0.0.1:8631/ipp/print");
        if(attrs!=null)attrs(builder);
        byte[] header=builder.Finish();
        HttpWebRequest web=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:8631/ipp/print");
        web.Proxy=null;web.Method="POST";web.ContentType="application/ipp";web.KeepAlive=false;web.Timeout=30000;
        web.SendChunked=chunked;if(!chunked)web.ContentLength=header.Length+(document==null?0:document.Length);
        using(Stream output=web.GetRequestStream()){output.Write(header,0,header.Length);if(document!=null)output.Write(document,0,document.Length);}
        using(WebResponse response=web.GetResponse())using(Stream input=response.GetResponseStream())using(MemoryStream result=new MemoryStream()){input.CopyTo(result);return IppRequest.Parse(result.ToArray());}
    }
    static void Put(byte[] h,int pos,int val){h[pos]=(byte)(val>>24);h[pos+1]=(byte)(val>>16);h[pos+2]=(byte)(val>>8);h[pos+3]=(byte)val;}
    static byte[] Raster(){
        byte[] h=new byte[1796];Encoding.ASCII.GetBytes("PwgRaster").CopyTo(h,0);
        Put(h,276,600);Put(h,280,600);Put(h,372,8);Put(h,376,8);Put(h,384,1);Put(h,388,1);Put(h,392,1);Put(h,400,3);Put(h,420,1);
        using(MemoryStream m=new MemoryStream()){byte[] magic=Encoding.ASCII.GetBytes("RaS2");m.Write(magic,0,4);m.Write(h,0,h.Length);
            // First four rows black (black_1=1 is black), last four white.
            m.WriteByte(3);m.WriteByte(0);m.WriteByte(255);m.WriteByte(3);m.WriteByte(0);m.WriteByte(0);return m.ToArray();}
    }
    static int State(int job){IppRequest r=Send(9,delegate(IppReply p){p.Number(0x21,"job-id",job);},null,false);if(r.Op!=0)throw new Exception("Get-Job failed");return r.Number("job-state",0);}
    static void Complete(int job){for(int i=0;i<100;i++){int state=State(job);if(state==9)return;if(state==8)throw new Exception("Job aborted");Thread.Sleep(100);}throw new Exception("Job completion timed out");}
    public static int Main(string[] args){
        string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bridge-tests");Directory.CreateDirectory(dir);
        try {
            using(WebClient client=new WebClient()) {
                client.Proxy=null;
                if(!client.DownloadString("http://127.0.0.1:8631/").Contains("DRY RUN")) throw new Exception("Tests require bridge --dry-run; physical printing is prohibited in this suite");
            }
            byte[] raster=Raster();string pwg=Path.Combine(dir,"sample.pwg"),pbm=Path.Combine(dir,"sample.pbm");File.WriteAllBytes(pwg,raster);if(File.Exists(pbm))File.Delete(pbm);
            Check(PwgRaster.Convert(pwg,pbm)==1,"PWG decoder accepts valid compressed black_1");
            byte[] b=File.ReadAllBytes(pbm);int start=0,newlines=0;while(newlines<2){if(b[start++]==10)newlines++;}
            Check((b[start+1284*600+300]&128)!=0,"PWG black_1 polarity preserved");
            Check((b[start+4284*600+300]&128)==0,"PWG white rows preserved");
            string bad=Path.Combine(dir,"bad.pwg"),badPbm=Path.Combine(dir,"bad.pbm");File.WriteAllBytes(bad,new byte[]{82,97,83,50,0});if(File.Exists(badPbm))File.Delete(badPbm);bool rejected=false;try{PwgRaster.Convert(bad,badPbm);}catch(InvalidDataException){rejected=true;}Check(rejected,"Truncated PWG rejected before USB use");
            IppRequest caps=Send(11,null,null,false);Check(caps.Op==0&&caps.Text("printer-name","")=="Canon LBP-1120 x64","IPP printer capabilities handshake");
            Check(Send(4,delegate(IppReply p){p.Text(0x49,"document-format","application/postscript");},null,false).Op==0x040a,"Unsupported document format rejected");
            Check(Send(4,delegate(IppReply p){p.Number(0x21,"copies",2);},null,false).Op==0x040b,"Unimplemented copy count rejected");
            IppRequest job=Send(2,delegate(IppReply p){p.Text(0x49,"document-format","image/pwg-raster");p.Text(0x42,"job-name","Protocol dry-run test");},raster,true);
            Check(job.Op==0,"Chunked HTTP Print-Job accepted");int jobId=job.Number("job-id",0);Complete(jobId);Check(State(jobId)==9,"Dry-run job reaches completed state");
            IppRequest created=Send(5,null,null,false);int createdId=created.Number("job-id",0);Check(created.Op==0&&State(createdId)==4,"Create-Job waits for document");
            IppRequest sent=Send(6,delegate(IppReply p){p.Number(0x21,"job-id",createdId);p.Text(0x49,"document-format","image/pwg-raster");p.Bool("last-document",true);},raster,false);Check(sent.Op==0,"Send-Document accepted");Complete(createdId);
            IppRequest held=Send(5,null,null,false);int heldId=held.Number("job-id",0);Check(Send(8,delegate(IppReply p){p.Number(0x21,"job-id",heldId);},null,false).Op==0&&State(heldId)==7,"Pending job cancellation");
            Console.WriteLine("All tests passed. No physical page was requested (requires bridge --dry-run).");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}

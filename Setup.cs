using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Canon LBP-1120 x64 Setup")]
[assembly: AssemblyDescription("Installs the tested CAPT bridge and a Windows IPP printer queue")]
[assembly: AssemblyVersion("0.1.0.0")]
class SetupForm : Form {
    public Label Status=new Label(); public Button Install=new Button();
    public SetupForm(bool remove) {
        Text=remove?"Видалення Canon LBP-1120 x64":"Canon LBP-1120 x64 — встановлення";
        Width=590;Height=330;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        Label description=new Label{Left=24,Top=24,Width=525,Height=125,Text=remove?
            "Буде видалено системний принтер та автозапуск моста.\r\nЖурнали й файли незавершених завдань залишаться.\r\nWindows попросить підтвердити права адміністратора.":
            "Друк на Canon LBP-1120 у Windows 11 x64.\r\nПідключіть принтер через USB та ввімкніть його.\r\nПісля встановлення вибирайте Canon LBP-1120 x64 через Ctrl+P.\r\n\r\nA4 · 600 dpi · односторонній друк · одна копія.\r\nВикористовує Microsoft IPP Class Driver та локальний міст CAPT."};
        Status.SetBounds(24,157,525,58);Status.Text="Готово до початку.";
        Install.SetBounds(365,230,180,34);Install.Text=remove?"Видалити":"Встановити";
        Controls.Add(description);Controls.Add(Status);Controls.Add(Install);
    }
    public void Report(string message){if(IsHandleCreated)BeginInvoke(new Action(delegate{Status.Text=message;}));}
}
static class Setup {
    const string Product="CanonLBP1120CaptBridge",Version="0.1.0",Marker="installation.json";
    const string UninstallKey="Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\CanonLBP1120CaptBridge";
    static string Target{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CanonLBP1120");}}
    static JavaScriptSerializer json=new JavaScriptSerializer();
    [DllImport("kernel32.dll")] static extern void GetNativeSystemInfo(IntPtr info);
    static string Quote(string s){if(s.Contains("\""))throw new IOException("Invalid path");return "\""+s+"\"";}
    static Dictionary<string,object> ReadJson(string path){return json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));}
    static void WriteJson(string path,object value){File.WriteAllText(path,json.Serialize(value),new UTF8Encoding(false));}
    static void CheckPlatform(){
        if(!Environment.Is64BitOperatingSystem)throw new Exception("Потрібна 64-бітна Windows 11.");
        IntPtr system=Marshal.AllocHGlobal(64);
        try{GetNativeSystemInfo(system);if(Marshal.ReadInt16(system)!=9)throw new Exception("Цей пакет перевірено лише для процесорів Intel/AMD x64. ARM64 не підтримується цим інсталятором.");}
        finally{Marshal.FreeHGlobal(system);}
        using(RegistryKey key=Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion")){
            int build;if(key==null||!int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")),out build)||build<22000)throw new Exception("Цей пакет призначений для Windows 11 x64. Windows 10 ще не перевірено.");
        }
    }
    static string Hash(Stream s){using(SHA256 hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
    static Dictionary<string,object> Manifest(){using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream("manifest.json"))using(StreamReader r=new StreamReader(s))return json.Deserialize<Dictionary<string,object>>(r.ReadToEnd());}
    static void Extract(string folder){
        Dictionary<string,object> manifest=Manifest();
        using(Stream payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using(ZipArchive zip=new ZipArchive(payload,ZipArchiveMode.Read)){
            if(zip.Entries.Count!=manifest.Count)throw new Exception("Package manifest mismatch");
            foreach(ZipArchiveEntry entry in zip.Entries){
                if(!manifest.ContainsKey(entry.FullName)||entry.FullName!=Path.GetFileName(entry.FullName)||entry.Length>20000000)throw new Exception("Invalid package entry");
                using(Stream source=entry.Open()){string actual=Hash(source);if(actual!=Convert.ToString(manifest[entry.FullName]))throw new Exception("Package checksum failed: "+entry.FullName);}
                string dest=Path.Combine(folder,entry.FullName);
                using(Stream source=entry.Open())using(FileStream output=new FileStream(dest,FileMode.CreateNew))source.CopyTo(output);
            }
        }
    }
    static bool PortInUse(){try{using(TcpClient c=new TcpClient()){var task=c.ConnectAsync(IPAddress.Loopback,8631);if(!task.Wait(1000))return false;return c.Connected;}}catch{return false;}}
    static void StopOwnedBridge(string target){
        foreach(Process p in Process.GetProcessesByName("capt-ipp-bridge"))using(p){
            try{if(string.Equals(p.MainModule.FileName,Path.Combine(target,"capt-ipp-bridge.exe"),StringComparison.OrdinalIgnoreCase)){p.Kill();p.WaitForExit(10000);}}catch(System.ComponentModel.Win32Exception){}
        }
    }
    static Dictionary<string,object> PrinterAction(string target,string action,string port){
        string result=Path.Combine(target,"setup-result-"+Guid.NewGuid().ToString("N")+".json");
        string args="-NoProfile -ExecutionPolicy RemoteSigned -File "+Quote(Path.Combine(target,"Package-Printer.ps1"))+" -Action "+action+" -ResultFile "+Quote(result);
        if(port!=null)args+=" -ExpectedPort "+Quote(port);
        try{
            ProcessStartInfo info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),args){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            using(Process p=Process.Start(info)){p.WaitForExit();if(!File.Exists(result))throw new Exception("Інсталятор Windows не повернув результат. Перевірте підтвердження UAC і політику запуску скриптів.");}
            var response=ReadJson(result);if(!Convert.ToBoolean(response["Success"]))throw new Exception(Convert.ToString(response["Error"]));return response;
        }finally{if(File.Exists(result))File.Delete(result);}
    }
    static void Install(Action<string> report){
        CheckPlatform();string target=Target;
        if(Directory.Exists(target))throw new Exception("Папка встановлення вже існує: "+target+"\r\nВидаліть попередню версію або перенесіть залишені журнали перед новим встановленням.");
        if(PortInUse())throw new Exception("Порт 8631 уже зайнятий. Можливо, інша версія моста Canon уже працює. Ця версія її не замінює.");
        bool queueAdded=false,folderCreated=false;string port=null;
        try{
            report("Розпакування й перевірка файлів…");Directory.CreateDirectory(target);folderCreated=true;Extract(target);
            File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(target,"Uninstall.exe"));
            report("Запуск локального моста…");
            using(Process p=Process.Start(new ProcessStartInfo(Path.Combine(target,"capt-ipp-bridge.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){
                bool ready=false;
                for(int i=0;i<50;i++){if(p.HasExited)throw new Exception("Не вдалося запустити міст. Перевірте bridge.log у папці встановлення.");try{using(WebClient web=new WebClient()){web.Proxy=null;ready=web.DownloadString("http://127.0.0.1:8631/").Contains("CAPT bridge is running.");}}catch{}if(ready)break;Thread.Sleep(200);}
                if(!ready)throw new Exception("Міст не відповідає на локальному порту 8631.");
            }
            report("Додавання принтера. Підтвердьте запит UAC Windows…");
            var result=PrinterAction(target,"Install",null);port=Convert.ToString(result["Port"]);queueAdded=true;
            WriteJson(Path.Combine(target,Marker),new Dictionary<string,object>{{"Product",Product},{"Version",Version},{"Port",port},{"Files",new List<string>(Manifest().Keys).ToArray()}});
            using(RegistryKey run=Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))run.SetValue(Product,Quote(Path.Combine(target,"capt-ipp-bridge.exe")));
            using(RegistryKey key=Registry.CurrentUser.CreateSubKey(UninstallKey)){
                key.SetValue("DisplayName","Canon LBP-1120 x64 (CAPT Bridge)");key.SetValue("DisplayVersion",Version);key.SetValue("Publisher","Community CAPT Bridge");key.SetValue("InstallLocation",target);
                key.SetValue("UninstallString",Quote(Path.Combine(target,"Uninstall.exe"))+" --uninstall");key.SetValue("DisplayIcon",Path.Combine(target,"Uninstall.exe"));key.SetValue("NoModify",1);key.SetValue("NoRepair",1);
            }
            report("Готово. У меню Ctrl+P вибирайте Canon LBP-1120 x64.");
        }catch{
            if(queueAdded){ // Keep a recoverable installation if queue removal requires another approval.
                WriteJson(Path.Combine(target,Marker),new Dictionary<string,object>{{"Product",Product},{"Version",Version},{"Port",port},{"Files",new List<string>(Manifest().Keys).ToArray()}});
            }else if(folderCreated){
                StopOwnedBridge(target);
                foreach(string file in Manifest().Keys){string path=Path.Combine(target,file);if(File.Exists(path))File.Delete(path);}
                string uninstaller=Path.Combine(target,"Uninstall.exe");if(File.Exists(uninstaller))File.Delete(uninstaller);
                if(Directory.GetFileSystemEntries(target).Length==0)Directory.Delete(target);
            }
            throw;
        }
    }
    static void Remove(Action<string> report){
        string target=Path.GetFullPath(Target);
        if(!File.Exists(Path.Combine(target,Marker)))throw new Exception("Не знайдено запис встановлення.");
        if((File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)throw new Exception("Папка встановлення є посиланням. Автоматичне видалення зупинено.");
        var marker=ReadJson(Path.Combine(target,Marker));if(Convert.ToString(marker["Product"])!=Product)throw new Exception("Ідентифікатор встановлення не збігається.");
        report("Видалення системного принтера. Підтвердьте UAC Windows…");PrinterAction(target,"Remove",Convert.ToString(marker["Port"]));
        StopOwnedBridge(target);
        using(RegistryKey run=Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run",true)){if(run!=null)run.DeleteValue(Product,false);}
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey,false);
        // Delete only package-owned files; retain documents/logs and never recurse.
        foreach(string file in Manifest().Keys){string path=Path.Combine(target,file);if(File.Exists(path))File.Delete(path);}
        File.Delete(Path.Combine(target,Marker));File.Delete(Path.Combine(target,"Uninstall.exe"));
        if(Directory.GetFileSystemEntries(target).Length==0)Directory.Delete(target);
        report("Принтер і автозапуск видалено. Збережені журнали: "+target);
    }
    [STAThread] public static int Main(string[] args){
        if(args.Length==2&&args[0]=="--verify-package"){
            try{string folder=Path.GetFullPath(args[1]);if(Directory.Exists(folder))throw new IOException("Verification directory already exists");Directory.CreateDirectory(folder);Extract(folder);File.WriteAllText(Path.Combine(folder,"verification.txt"),"All embedded payload hashes verified. No printer or registry settings changed.");return 0;}catch{return 1;}
        }
        bool remove=args.Length==1&&(args[0]=="--uninstall"||args[0]=="--remove-install");
        if(remove&&args[0]=="--uninstall"){
            string copy=Path.Combine(Path.GetTempPath(),"CanonLBP1120-Uninstall-"+Guid.NewGuid().ToString("N")+".exe");
            try{File.Copy(Assembly.GetExecutingAssembly().Location,copy);Process.Start(new ProcessStartInfo(copy,"--remove-install"){UseShellExecute=false});return 0;}catch(Exception e){MessageBox.Show(e.Message);return 1;}
        }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(SetupForm form=new SetupForm(remove)){
            bool working=false,done=false;
            form.FormClosing+=delegate(object sender,FormClosingEventArgs e){if(working)e.Cancel=true;};
            form.Install.Click+=delegate{if(done){form.Close();return;}working=true;form.Install.Enabled=false;ThreadPool.QueueUserWorkItem(delegate{
                try{if(remove)Remove(form.Report);else Install(form.Report);form.BeginInvoke(new Action(delegate{done=true;form.Install.Text="Закрити";form.Install.Enabled=true;}));}
                catch(Exception e){form.Report("Помилка встановлення або видалення.");form.BeginInvoke(new Action(delegate{MessageBox.Show(form,e.Message,"Canon LBP-1120",MessageBoxButtons.OK,MessageBoxIcon.Error);form.Install.Enabled=true;}));}
                finally{working=false;}
            });};
            Application.Run(form);
        }
        return 0;
    }
}

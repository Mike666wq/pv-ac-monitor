using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ExperimentMonitor {
    /// <summary>Export work is built in a user-writable location and copied into a verified destination before atomic publication.</summary>
    internal static class ExportNaming {
        internal static Action<string> BeforePublishForTests;
        internal static string CreateWorkDirectory() {
            var roots=new List<string>();string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(!String.IsNullOrEmpty(local))roots.Add(Path.Combine(local,"ExperimentMonitorDemo","ExportWork"));
            if(!String.IsNullOrEmpty(Path.GetTempPath()))roots.Add(Path.GetTempPath());
            roots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"export-work"));Exception last=null;
            foreach(string root in roots){string path=Path.Combine(root,"ExperimentExport-"+Guid.NewGuid().ToString("N"));try{Directory.CreateDirectory(path);string probe=Path.Combine(path,"write-check.tmp");File.WriteAllText(probe,"ok");File.Delete(probe);return path;}catch(Exception ex){if(!(ex is IOException)&&!(ex is UnauthorizedAccessException))throw;last=ex;TryDelete(path);}}
            throw new IOException("无法在当前用户的临时目录创建导出工作区。",last);
        }
        internal static void VerifyDestination(string parent) {
            if(String.IsNullOrWhiteSpace(parent))throw new ArgumentException("请选择导出保存目录", "parent");string full=Path.GetFullPath(parent),probe=null;
            try{Directory.CreateDirectory(full);probe=Path.Combine(full,".~w"+Guid.NewGuid().ToString("N").Substring(0,6));using(var f=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.WriteByte(0);f.Flush(true);}File.Delete(probe);probe=null;}
            catch(UnauthorizedAccessException ex){throw new IOException("当前进程无法写入所选导出目录："+full+"。请选择当前用户可写的目录。若从开发工作区运行，请将发布包解压到普通目录后再启动。",ex);}
            catch(IOException ex){throw new IOException("无法在所选导出目录创建并清理测试文件："+full+"。请检查权限、磁盘空间和文件占用。",ex);}
            finally{if(probe!=null)try{if(File.Exists(probe))File.Delete(probe);}catch{}}
        }
        internal static void CheckDestinationSpace(string parent){string full=Path.GetFullPath(parent);if(new DriveInfo(Path.GetPathRoot(full)).AvailableFreeSpace<100L*1024*1024)throw new IOException("导出目标卷可用空间低于100 MiB："+full);}
        internal static string PublishDirectory(string work,string parent,string stem,CancellationToken token){VerifyDestination(parent);if(Directory.Exists(Path.Combine(parent,stem))||File.Exists(Path.Combine(parent,stem)))stem+="_"+Guid.NewGuid().ToString("N").Substring(0,8);string stage=CreateStage(parent);try{Copy(work,stage,token);token.ThrowIfCancellationRequested();Action<string> test=BeforePublishForTests;if(test!=null)test(stage);token.ThrowIfCancellationRequested();string final=Path.Combine(Path.GetFullPath(parent),stem);if(Directory.Exists(final)||File.Exists(final))throw new IOException("目标结果已存在，未覆盖："+final);Directory.Move(stage,final);return final;}catch{TryDelete(stage);throw;}}
        static string CreateStage(string parent){for(int i=0;i<10;i++){// Keep the publish path short for Windows installations that still enforce MAX_PATH.
            string path=Path.Combine(Path.GetFullPath(parent),".~e"+Guid.NewGuid().ToString("N").Substring(0,8));try{Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"w"),"ok");File.Delete(Path.Combine(path,"w"));return path;}catch(IOException){TryDelete(path);}}throw new IOException("无法在目标目录创建导出提交暂存目录。");}
        static void Copy(string source,string target,CancellationToken token){foreach(string file in Directory.GetFiles(source,"*",SearchOption.TopDirectoryOnly)){token.ThrowIfCancellationRequested();using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(Path.Combine(target,Path.GetFileName(file)),FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] b=new byte[64*1024];int n;while((n=input.Read(b,0,b.Length))>0){token.ThrowIfCancellationRequested();output.Write(b,0,n);}output.Flush(true);}}foreach(string dir in Directory.GetDirectories(source)){token.ThrowIfCancellationRequested();string child=Path.Combine(target,Path.GetFileName(dir));Directory.CreateDirectory(child);Copy(dir,child,token);}}
        internal static void TryDelete(string path){try{if(Directory.Exists(path))Directory.Delete(path,true);}catch{}}
    }
}

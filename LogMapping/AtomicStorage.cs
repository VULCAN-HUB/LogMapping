using System.IO;
using System.Text;
namespace LogMapping;
internal static class AtomicStorage
{
    public static void Write(string path,string content)
    {
        path=System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { var bytes=new UTF8Encoding(false).GetBytes(content);f.Write(bytes);f.Flush(true); }
            if(File.Exists(path))File.Replace(temp,path,path+".bak");
            else File.Move(temp,path);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
}

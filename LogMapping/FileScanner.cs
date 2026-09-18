using System.IO;
namespace LogMapping;
internal static class FileScanner
{
    // Intentional exclusions only. An unreadable ordinary directory makes the scan fail.
    public static int Scan(string root,Action<string,string,bool,long,string> emit,CancellationToken token)
    {
        root=Path.GetFullPath(root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("스캔 경로가 없거나 접근할 수 없습니다: "+root);
        int excluded=0;
        var stack=new Stack<(string path,string relative)>();stack.Push((root,""));
        while(stack.Count>0)
        {
            token.ThrowIfCancellationRequested();
            var (path,relative)=stack.Pop();
            foreach(var entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();var name=Path.GetFileName(entry);
                if(relative.Length==0 && (name.Equals("System Volume Information",StringComparison.OrdinalIgnoreCase)||name.Equals("$RECYCLE.BIN",StringComparison.OrdinalIgnoreCase))) {excluded++;continue;}
                var attributes=File.GetAttributes(entry);bool directory=attributes.HasFlag(FileAttributes.Directory);
                bool link=attributes.HasFlag(FileAttributes.ReparsePoint);
                if(directory)
                {
                    emit(relative,name,true,0,"");
                    if(link){excluded++;continue;} // record junction itself; never recursively follow it
                    stack.Push((entry,relative.Length==0?name:relative+"/"+name));
                }
                else
                {
                    if(link){excluded++;continue;}
                    var f=new FileInfo(entry);long bytes=f.Length;
                    emit(relative,name,false,bytes==0?0:Math.Max(1,bytes/1024),f.LastWriteTime.ToString("yyyy-MM-dd"));
                }
            }
        }
        token.ThrowIfCancellationRequested();return excluded;
    }
}

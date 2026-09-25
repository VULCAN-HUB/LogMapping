using System.IO;
namespace LogMapping;
internal static class FileScanner
{
    // Intentional exclusions, plus items that vanish between listing and stat (temp files, AV quarantine) -
    // both counted in the returned number. An unreadable ordinary directory still makes the scan fail,
    // and a root that disappears (drive unplugged) fails the whole scan instead of saving a partial list.
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
            IEnumerable<string> entries;
            try{entries=Directory.EnumerateFileSystemEntries(path);}
            catch(DirectoryNotFoundException) when(relative.Length>0 && Directory.Exists(root)){excluded++;continue;}
            foreach(var entry in entries)
            {
                token.ThrowIfCancellationRequested();var name=Path.GetFileName(entry);
                if(relative.Length==0 && (name.Equals("System Volume Information",StringComparison.OrdinalIgnoreCase)||name.Equals("$RECYCLE.BIN",StringComparison.OrdinalIgnoreCase))) {excluded++;continue;}
                FileAttributes attributes;
                try{attributes=File.GetAttributes(entry);}
                catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){excluded++;continue;}
                bool directory=attributes.HasFlag(FileAttributes.Directory);
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
                    var f=new FileInfo(entry);long bytes;
                    try{bytes=f.Length;}catch(FileNotFoundException){excluded++;continue;}
                    emit(relative,name,false,bytes==0?0:Math.Max(1,bytes/1024),f.LastWriteTime.ToString("yyyy-MM-dd"));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("스캔 중 경로가 사라졌습니다(연결 해제?): "+root);
        return excluded;
    }
}

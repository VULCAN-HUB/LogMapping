#nullable enable
using System.IO;
namespace LogMapping;
internal static class FileScanner
{
    internal sealed class Exclusions
    {
        public int SystemFolders, Links, Missing;
        private readonly List<string> samples = new();
        public void Add(string reason, string path)
        {
            if(reason == "시스템 폴더") SystemFolders++;
            else if(reason == "연결 항목") Links++;
            else Missing++;
            if(samples.Count < 10) samples.Add(reason + ": " + path);
        }
        public string? Summary => SystemFolders + Links + Missing == 0 ? null :
            $"스캔 완료 — 제외 내역\n시스템 폴더 {SystemFolders}개 · 연결 항목 {Links}개 · 스캔 중 사라진 항목 {Missing}개\n연결 폴더 자체는 폴더로 기록하며 하위 내용은 따라가지 않습니다.\n\n" + string.Join("\n", samples) +
            (SystemFolders + Links + Missing > samples.Count ? "\n(내역은 최대 10개 표시)" : "");
    }
    // Intentional exclusions, plus items that vanish between listing and stat (temp files, AV quarantine) -
    // both counted in the returned number. An unreadable ordinary directory still makes the scan fail,
    // and a root that disappears (drive unplugged) fails the whole scan instead of saving a partial list.
    public static int Scan(string root,Action<string,string,bool,long,string> emit,CancellationToken token,Exclusions? details = null)
    {
        root=Path.GetFullPath(root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("스캔 경로가 없거나 접근할 수 없습니다: "+root);
        int excluded=0;
        void Exclude(string reason,string path) { excluded++; details?.Add(reason,Path.GetRelativePath(root,path)); }
        var stack=new Stack<(string path,string relative)>();stack.Push((root,""));
        while(stack.Count>0)
        {
            token.ThrowIfCancellationRequested();
            var (path,relative)=stack.Pop();
            IEnumerable<string> entries;
            try{entries=Directory.EnumerateFileSystemEntries(path);}
            catch(DirectoryNotFoundException) when(relative.Length>0 && Directory.Exists(root)){Exclude("사라진 항목",path);continue;}
            foreach(var entry in entries)
            {
                token.ThrowIfCancellationRequested();var name=Path.GetFileName(entry);
                if(relative.Length==0 && (name.Equals("System Volume Information",StringComparison.OrdinalIgnoreCase)||name.Equals("$RECYCLE.BIN",StringComparison.OrdinalIgnoreCase))) {Exclude("시스템 폴더",entry);continue;}
                FileAttributes attributes;
                try{attributes=File.GetAttributes(entry);}
                catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){Exclude("사라진 항목",entry);continue;}
                bool directory=attributes.HasFlag(FileAttributes.Directory);
                bool link=attributes.HasFlag(FileAttributes.ReparsePoint);
                if(directory)
                {
                    emit(relative,name,true,0,"");
                    if(link){Exclude("연결 항목",entry);continue;} // record junction itself; never recursively follow it
                    stack.Push((entry,relative.Length==0?name:relative+"/"+name));
                }
                else
                {
                    if(link){Exclude("연결 항목",entry);continue;}
                    var f=new FileInfo(entry);long bytes;
                    try{bytes=f.Length;}catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){Exclude("사라진 항목",entry);continue;}
                    emit(relative,name,false,bytes==0?0:Math.Max(1,bytes/1024),f.LastWriteTime.ToString("yyyy-MM-dd"));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("스캔 중 경로가 사라졌습니다(연결 해제?): "+root);
        return excluded;
    }
}

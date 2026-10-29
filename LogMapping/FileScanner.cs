#nullable enable
using System.IO;
using System.Buffers.Binary;
namespace LogMapping;
internal static class FileScanner
{
    internal sealed class Exclusions
    {
        public int SystemFolders, Links, Missing, MacMetadata, HiddenSystem;
        private readonly List<string> samples = new();
        public void Add(string reason, string path)
        {
            if(reason == "시스템 폴더") SystemFolders++;
            else if(reason == "연결 항목") Links++;
            else if(reason == "Mac 보조 파일") MacMetadata++;
            else if(reason == "숨김·시스템 항목") HiddenSystem++;
            else Missing++;
            if(samples.Count < 10) samples.Add(reason + ": " + path);
        }
        public string? Summary => SystemFolders + Links + Missing + MacMetadata + HiddenSystem == 0 ? null :
            $"스캔 완료 — 제외 내역\n시스템 폴더 {SystemFolders}개 · 연결 항목 {Links}개 · 스캔 중 사라진 항목 {Missing}개\n연결 폴더 자체는 폴더로 기록하며 하위 내용은 따라가지 않습니다.\n\n" + string.Join("\n", samples) +
            $"\nMac 보조 파일 {MacMetadata}개 (파일 내용으로 확인, 원본은 디스크에 유지)" +
            $"\n숨김·시스템 항목 {HiddenSystem}개 (숨김 폴더 하위 내용 포함 제외)" +
            (SystemFolders + Links + Missing + MacMetadata + HiddenSystem > samples.Count ? "\n(내역은 최대 10개 표시)" : "");
    }
    // AppleDouble v1/v2 header and entry bounds (RFC 1740). A filename alone is not proof.
    private static bool IsAppleDouble(string path)
    {
        try
        {
            using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[26];
            if(stream.Length < header.Length) return false;
            stream.ReadExactly(header);
            if(BinaryPrimitives.ReadUInt32BigEndian(header) != 0x00051607) return false;
            uint version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if(version != 0x00010000 && version != 0x00020000) return false;
            int count = BinaryPrimitives.ReadUInt16BigEndian(header[24..]);
            long tableEnd = 26L + 12L * count;
            if(count == 0 || tableEnd > stream.Length) return false;
            Span<byte> entry = stackalloc byte[12];
            for(int i=0;i<count;i++)
            {
                stream.ReadExactly(entry);
                uint id = BinaryPrimitives.ReadUInt32BigEndian(entry);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                long length = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                if(id == 0 || id == 1 || offset < tableEnd || offset + length > stream.Length) return false;
            }
            return true;
        }
        catch(IOException) { return false; }
        catch(UnauthorizedAccessException) { return false; }
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
                if((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) {Exclude("숨김·시스템 항목",entry);continue;}
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
                    if(name.StartsWith("._",StringComparison.Ordinal) && IsAppleDouble(entry)) {Exclude("Mac 보조 파일",entry);continue;}
                    emit(relative,name,false,bytes==0?0:Math.Max(1,bytes/1024),f.LastWriteTime.ToString("yyyy-MM-dd"));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("스캔 중 경로가 사라졌습니다(연결 해제?): "+root);
        return excluded;
    }
}

using LogMapping;
using System.Diagnostics;
var root=Path.GetFullPath(args[0]);int passed=0,failed=0;
void Check(string name,Action a){var sw=Stopwatch.StartNew();try{a();passed++;Console.WriteLine($"PASS {name}: {sw.Elapsed.TotalSeconds:F3}s");}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
void Assert(bool ok){if(!ok)throw new Exception("assertion failed");}
var tree=Path.Combine(root,"tree");Directory.CreateDirectory(tree);
if(OperatingSystem.IsWindows()) Check("Explorer hidden/system items and hidden folder descendants are omitted",()=>{
 var dir=Path.Combine(root,"visibility");Directory.CreateDirectory(dir);
 foreach(var name in new[]{"hidden.txt","system.txt","visible.txt"})File.WriteAllText(Path.Combine(dir,name),"keep on disk");
 Directory.CreateDirectory(Path.Combine(dir,"hidden-folder"));File.WriteAllText(Path.Combine(dir,"hidden-folder","nested.txt"),"keep on disk");
 File.SetAttributes(Path.Combine(dir,"hidden.txt"),FileAttributes.Hidden);
 File.SetAttributes(Path.Combine(dir,"system.txt"),FileAttributes.System);
 File.SetAttributes(Path.Combine(dir,"hidden-folder"),FileAttributes.Hidden | FileAttributes.Directory);
 var rows=new List<string>();var report=new FileScanner.Exclusions();
 int skipped=FileScanner.Scan(dir,(p,n,d,s,m)=>rows.Add(p+"/"+n),default,report);
 Assert(skipped==3&&report.HiddenSystem==3&&report.Missing==0);
 Assert(rows.Count==1&&rows[0]=="/visible.txt");
 Assert(File.ReadAllText(Path.Combine(dir,"hidden-folder","nested.txt"))=="keep on disk");
});
Check("verified AppleDouble excluded while original folders and similarly named user files survive",()=>{
 var dir=Path.Combine(root,"appledouble");Directory.CreateDirectory(Path.Combine(dir,"사진"));
 File.WriteAllText(Path.Combine(dir,"사진","keep.txt"),"original");
 byte[] data=new byte[4096];
 System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data,0x00051607);
 System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4),0x00020000);
 System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(24),1);
 System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(26),9);
 System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(30),38);
 System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(34),32);
 File.WriteAllBytes(Path.Combine(dir,"._사진"),data);
 File.WriteAllBytes(Path.Combine(dir,"ordinary.bin"),data);
 File.WriteAllText(Path.Combine(dir,"._user.txt"),"user content");
 data[30]=255;File.WriteAllBytes(Path.Combine(dir,"._invalid"),data);
 File.WriteAllBytes(Path.Combine(dir,"._truncated"),data[..8]);
 Directory.CreateDirectory(Path.Combine(dir,"._directory"));
 var rows=new HashSet<string>();var details=new FileScanner.Exclusions();
 int excluded=FileScanner.Scan(dir,(p,n,d,s,m)=>rows.Add(p+"/"+n),default,details);
 Assert(excluded==1&&details.MacMetadata==1&&details.Missing==0);
 Assert(!rows.Contains("/._사진")&&rows.Contains("/사진")&&rows.Contains("사진/keep.txt"));
 Assert(rows.Contains("/ordinary.bin")&&rows.Contains("/._user.txt")&&rows.Contains("/._invalid")&&rows.Contains("/._truncated")&&rows.Contains("/._directory"));
 Assert(File.ReadAllBytes(Path.Combine(dir,"._사진")).Length==4096);
});
Check("directory types remain distinct from 4KB files and metadata companions",()=>{
 var dir=Path.Combine(root,"types");Directory.CreateDirectory(dir);
 foreach(var name in new[]{"상위 폴더","project.ext",".hidden"}){
  Directory.CreateDirectory(Path.Combine(dir,name,"child"));
  File.WriteAllBytes(Path.Combine(dir,name,"child","file.bin"),new byte[4096]);
  File.WriteAllBytes(Path.Combine(dir,"._"+name),new byte[4096]);
 }
 var rows=new Dictionary<string,(bool folder,long size)>();
 FileScanner.Scan(dir,(p,n,d,s,m)=>rows[p+"/"+n]=(d,s),default);
 foreach(var name in new[]{"상위 폴더","project.ext",".hidden"}){
  Assert(rows["/"+name]==(true,0));Assert(rows[name+"/child"]==(true,0));
  Assert(rows[name+"/child/file.bin"]==(false,4));Assert(rows["/._"+name]==(false,4));
 }
});
Check("exclusion summary distinguishes system directories from missing entries",()=>{
 var dir=Path.Combine(root,"excluded");Directory.CreateDirectory(dir);
 Directory.CreateDirectory(Path.Combine(dir,"System Volume Information"));Directory.CreateDirectory(Path.Combine(dir,"$RECYCLE.BIN"));
 Directory.CreateDirectory(Path.Combine(dir,"vanish"));var report=new FileScanner.Exclusions();
 int count=FileScanner.Scan(dir,(p,n,d,s,m)=>{if(d&&n=="vanish")Directory.Delete(Path.Combine(dir,n));},default,report);
 Assert(count==3&&report.SystemFolders==2&&report.Links==0&&report.Missing==1);
 Assert(report.Summary!.Contains("시스템 폴더 2개")&&report.Summary.Contains("사라진 항목 1개"));
});
Check("scan 30000 physical fixture files with a junction loop",()=>{
 for(int d=0;d<30;d++){var folder=Path.Combine(tree,"사진.2026-"+d);Directory.CreateDirectory(folder);for(int i=0;i<1000;i++)File.WriteAllText(Path.Combine(folder,".사진-"+i+".txt"),i==0?"":"fixture");}
 int files=0,zeros=0;var seen=new HashSet<string>();int skipped=FileScanner.Scan(tree,(p,n,d,s,m)=>{if(!d){files++;Assert(seen.Add(p+"/"+n));if(s==0)zeros++;}},default);
 Assert(files==30000&&zeros==30&&skipped==1);
});
Check("deep Unicode Windows path beyond 260 characters",()=>{
 var deep=Path.Combine(root,"deep");for(int i=0;i<12;i++)deep=Path.Combine(deep,"한글공백 폴더123456789-"+i);Directory.CreateDirectory(deep);File.WriteAllText(Path.Combine(deep,"자료.txt"),"x");
 int files=0;FileScanner.Scan(Path.Combine(root,"deep"),(p,n,d,s,m)=>{if(!d)files++;},default);Assert(files==1);
});
Check("subfolder disappears during scan: skipped and counted, rest kept",()=>{
 var dir=Path.Combine(root,"removed-during-scan");Directory.CreateDirectory(Path.Combine(dir,"child"));File.WriteAllText(Path.Combine(dir,"keep.txt"),"x");int files=0;
 int skipped=FileScanner.Scan(dir,(p,n,d,s,m)=>{if(d&&n=="child")Directory.Delete(Path.Combine(dir,n));if(!d)files++;},default);Assert(skipped==1&&files==1);
});
Check("scan root disappears (unplugged): explicit failure",()=>{
 var dir=Path.Combine(root,"root-removed");Directory.CreateDirectory(Path.Combine(dir,"child"));bool threw=false;
 try{FileScanner.Scan(dir,(p,n,d,s,m)=>{if(d&&n=="child")Directory.Delete(dir,true);},default);}catch(DirectoryNotFoundException){threw=true;}Assert(threw);
});
Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;

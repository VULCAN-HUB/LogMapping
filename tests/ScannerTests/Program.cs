using LogMapping;
using System.Diagnostics;
var root=Path.GetFullPath(args[0]);int passed=0,failed=0;
void Check(string name,Action a){var sw=Stopwatch.StartNew();try{a();passed++;Console.WriteLine($"PASS {name}: {sw.Elapsed.TotalSeconds:F3}s");}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
void Assert(bool ok){if(!ok)throw new Exception("assertion failed");}
var tree=Path.Combine(root,"tree");Directory.CreateDirectory(tree);
Check("scan 30000 physical fixture files with a junction loop",()=>{
 for(int d=0;d<30;d++){var folder=Path.Combine(tree,"사진.2026-"+d);Directory.CreateDirectory(folder);for(int i=0;i<1000;i++)File.WriteAllText(Path.Combine(folder,".사진-"+i+".txt"),i==0?"":"fixture");}
 int files=0,zeros=0;var seen=new HashSet<string>();int skipped=FileScanner.Scan(tree,(p,n,d,s,m)=>{if(!d){files++;Assert(seen.Add(p+"/"+n));if(s==0)zeros++;}},default);
 Assert(files==30000&&zeros==30&&skipped==1);
});
Check("deep Unicode Windows path beyond 260 characters",()=>{
 var deep=Path.Combine(root,"deep");for(int i=0;i<12;i++)deep=Path.Combine(deep,"한글공백 폴더123456789-"+i);Directory.CreateDirectory(deep);File.WriteAllText(Path.Combine(deep,"자료.txt"),"x");
 int files=0;FileScanner.Scan(Path.Combine(root,"deep"),(p,n,d,s,m)=>{if(!d)files++;},default);Assert(files==1);
});
Check("directory disappears during scan: explicit failure",()=>{
 var dir=Path.Combine(root,"removed-during-scan");Directory.CreateDirectory(Path.Combine(dir,"child"));bool threw=false;
 try{FileScanner.Scan(dir,(p,n,d,s,m)=>{if(d&&n=="child")Directory.Delete(Path.Combine(dir,n));},default);}catch(DirectoryNotFoundException){threw=true;}Assert(threw);
});
Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;

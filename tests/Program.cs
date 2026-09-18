using LogMapping;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text;
using System.Text.Json;
var root=System.IO.Path.GetFullPath(args.Length>0?args[0]:System.IO.Path.Combine(System.IO.Path.GetTempPath(),"LogMapping-tests",Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);int passed=0,failed=0;
void Check(string name,Action test){try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
JsonElement Rows(string s)=>JsonDocument.Parse(s).RootElement;
long Drive(CatalogDb db,int num,string path){var id=db.CreateDrive(path);db.UpsertDrive(JsonSerializer.SerializeToElement(new{dbId=id,num,name="Test "+num,cap=1024,used=1,color="lime",scannedPath=path}));return id;}
var dbPath=System.IO.Path.Combine(root,"catalog.db");
using(var db=new CatalogDb(dbPath))
{
 var a=Drive(db,1,"E:\\");var b=Drive(db,2,"F:\\");
 db.InsertStreaming(a,emit=>{emit("","project.2026",true,0,"");emit("project.2026","photo.jpg",false,30,"");emit("","small.JPG",false,2,"");emit("","literal_%.txt",false,4,"");emit("","constructor",true,0,"");emit("constructor","child.txt",false,1,"");emit("","line\nbreak.txt",false,1,"");});
 db.InsertStreaming(b,emit=>emit("","second.jpg",false,10,""));
 Check("catalog-open prune preserves other snapshots",()=>{db.PruneDrives(new[]{a});Assert(Rows(db.DriveStatsJson(b))[0].GetProperty("files").GetInt32()==1);});
 Check("search only selected catalog scope",()=>{Assert(Rows(db.SearchJson("jpg",20,new[]{a})).GetArrayLength()==2);Assert(Rows(db.SearchJson("second",20,new[]{a})).GetArrayLength()==0);Assert(Rows(db.SearchJson("second",20,new[]{b})).GetArrayLength()==1);});
 Check("full path and backslash search",()=>{Assert(Rows(db.SearchJson("project.2026/photo",20,new[]{a})).GetArrayLength()==1);Assert(Rows(db.SearchJson("project.2026\\photo",20,new[]{a})).GetArrayLength()==1);});
 Check("literal SQL wildcard chars",()=>Assert(Rows(db.SearchJson("_%",20,new[]{a})).GetArrayLength()==1));
 Check("color filter before limit",()=>{db.SetItemColor(a,"small.JPG","#red");var r=Rows(db.SearchJson("jpg",1,new[]{a},"#red"));Assert(r.GetArrayLength()==1&&r[0].GetProperty("name").GetString()=="small.JPG");});
 Check("missing tag target reports failure",()=>{bool threw=false;try{db.SetItemColor(a,"missing.jpg","#red");}catch(InvalidOperationException){threw=true;}Assert(threw);});
 Check("category and size ordering",()=>{var r=Rows(db.FilesByExtsJson(a,"jpg",20,"size-desc"));Assert(r.GetArrayLength()==2&&r[0].GetProperty("size").GetInt32()==30);});
 Check("rescan retains old generation and copies colors",()=>{var n=Drive(db,3,"E:\\");db.InsertStreaming(n,e=>e("","small.JPG",false,2,""));db.CopyColors(a,n);Assert(Rows(db.FilesByColorJson(n,"#red",20)).GetArrayLength()==1);Assert(Rows(db.DriveStatsJson(a))[0].GetProperty("files").GetInt32()==5);});
 Check("row carries owning drive ID",()=>Assert(Rows(db.GetChildrenJson(a,""))[0].GetProperty("drive_id").GetInt64()==a));
 Check("scoped viewer and newline payload",()=>{db.ExportViewer(System.IO.Path.Combine(root,"viewer.html"),new[]{a});var html=File.ReadAllText(System.IO.Path.Combine(root,"viewer.html"));Assert(!html.Contains("Test 2"));Assert(html.Contains("Object.create(null)"));});
 Check("scoped CSV",()=>{var p=System.IO.Path.Combine(root,"out.csv");db.ExportCsv(p,new[]{b});var text=File.ReadAllText(p);Assert(text.Contains("second.jpg")&&!text.Contains("small.JPG"));});
 Check("backup opens with same database identity",()=>{var info=Rows(db.DescribeJson());var bp=System.IO.Path.Combine(root,"backup.db");db.BackupTo(bp);using var backup=new CatalogDb(bp);Assert(Rows(backup.DescribeJson()).GetProperty("databaseId").GetString()==info.GetProperty("databaseId").GetString());Assert(Rows(backup.DriveStatsJson(a))[0].GetProperty("files").GetInt32()==5);});
}
Check("legacy name-only FTS migration preserves rows",()=>{
 using(var conn=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=dbPath,Pooling=false}.ToString())){conn.Open();using var c=conn.CreateCommand();c.CommandText="DROP TRIGGER files_fts_ai; DROP TRIGGER files_fts_ad; DROP TABLE files_fts; CREATE VIRTUAL TABLE files_fts USING fts5(name,content='files',content_rowid='id',tokenize='trigram');DELETE FROM meta WHERE k='fts_schema';";c.ExecuteNonQuery();}
 using var db=new CatalogDb(dbPath);Assert(Rows(db.SearchJson("project.2026/photo",20,new long[]{1})).GetArrayLength()==1);
});
Check("corrupt DB preserved byte-for-byte",()=>{var path=System.IO.Path.Combine(root,"corrupt.db");var bytes=Encoding.UTF8.GetBytes("not a database, preserve me");File.WriteAllBytes(path,bytes);bool threw=false;try{using var db=new CatalogDb(path);}catch(IOException){threw=true;}Assert(threw);Assert(File.ReadAllBytes(path).SequenceEqual(bytes));});
Check("atomic .hcat retains previous version",()=>{var p=System.IO.Path.Combine(root,"snapshot.hcat");AtomicStorage.Write(p,"old");AtomicStorage.Write(p,"new");Assert(File.ReadAllText(p)=="new"&&File.ReadAllText(p+".bak")=="old");});
Check("failed atomic save preserves original",()=>{var p=System.IO.Path.Combine(root,"locked.hcat");File.WriteAllText(p,"original");using(var held=new FileStream(p,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){bool threw=false;try{AtomicStorage.Write(p,"bad");}catch(IOException){threw=true;}Assert(threw);}Assert(File.ReadAllText(p)=="original");});
Check("scanner includes dot files and dotted directories",()=>{var folder=System.IO.Path.Combine(root,"scan");Directory.CreateDirectory(System.IO.Path.Combine(folder,"photos.2026"));File.WriteAllText(System.IO.Path.Combine(folder,".hidden"),"data");File.WriteAllText(System.IO.Path.Combine(folder,"photos.2026","empty.txt"),"");var found=new List<(string path,long size)>();FileScanner.Scan(folder,(p,n,d,s,m)=>{if(!d)found.Add((p+"/"+n,s));},default);Assert(found.Any(x=>x.path=="/.hidden"));Assert(found.Any(x=>x.path=="photos.2026/empty.txt"&&x.size==0));});
Check("missing root fails instead of empty success",()=>{bool threw=false;try{FileScanner.Scan(System.IO.Path.Combine(root,"missing"),(p,n,d,s,m)=>{},default);}catch(DirectoryNotFoundException){threw=true;}Assert(threw);});
Check("cancelled scan fails explicitly",()=>{using var ct=new CancellationTokenSource();ct.Cancel();bool threw=false;try{FileScanner.Scan(root,(p,n,d,s,m)=>{},ct.Token);}catch(OperationCanceledException){threw=true;}Assert(threw);});
Check("cancellation during traversal fails explicitly",()=>{var dir=System.IO.Path.Combine(root,"scan");using var ct=new CancellationTokenSource();bool threw=false;try{FileScanner.Scan(dir,(p,n,d,s,m)=>ct.Cancel(),ct.Token);}catch(OperationCanceledException){threw=true;}Assert(threw);});
Check("APFS dotted folder includes children",()=>{
 var records=new Dictionary<ulong,List<(string,ulong,bool)>>{{2,new(){("photos.2026",3,true)}},{3,new(){("child.jpg",4,false)}}};var emitted=new List<string>();
 typeof(ApfsReader).GetMethod("EmitTree",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{(ulong)2,"",records,new Dictionary<ulong,long>{{4,25}},new Dictionary<ulong,bool>{{3,true},{4,false}},new Dictionary<ulong,string>(),new HashSet<ulong>(),(Action<string,string,bool,long,string>)((p,n,d,s,m)=>emitted.Add($"{p}/{n}:{d}"))});Assert(emitted.Contains("/photos.2026:True")&&emitted.Contains("photos.2026/child.jpg:False"));
});
Check("APFS volume identity and name offsets",()=>{
 var bytes=new byte[8192];BitConverter.GetBytes((uint)0x42535041).CopyTo(bytes,4096+32);BitConverter.GetBytes((ulong)1).CopyTo(bytes,4096+264);Encoding.UTF8.GetBytes("TestVolume").CopyTo(bytes,4096+704);for(int i=0;i<16;i++)bytes[4096+240+i]=(byte)(i+1);
 using var stream=new MemoryStream(bytes);using var reader=new ApfsReader(stream);var list=new List<ApfsVolumeInfo>();typeof(ApfsReader).GetMethod("AddVolumeFromBlock",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(reader,new object[]{(ulong)1,list});Assert(list.Count==1&&list[0].Name=="TestVolume"&&list[0].VolumeId=="APFS-0102030405060708090A0B0C0D0E0F10");
});
Check("failed auto backup is surfaced and remains dirty",()=>{
 var path=System.IO.Path.Combine(root,"auto-fail.db");using var db=new CatalogDb(path);var id=db.CreateDrive("test");db.InsertStreaming(id,e=>e("","a.txt",false,1,""));db.BackupSelf();db.SetItemColor(id,"a.txt","#red");
 bool threw=false;using(var held=new FileStream(path+".bak",FileMode.Open,FileAccess.ReadWrite,FileShare.None)){try{db.BackupIfDirty();}catch(Exception e) when(e is IOException or UnauthorizedAccessException){threw=true;}}Assert(threw);Assert(db.BackupIfDirty());
 using var restored=new CatalogDb(path+".bak");Assert(Rows(restored.FilesByColorJson(id,"#red",10)).GetArrayLength()==1);
});
Check("schema initialization failure releases database handle",()=>{
 var path=System.IO.Path.Combine(root,"bad-schema.db");using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString())){c.Open();using var q=c.CreateCommand();q.CommandText="CREATE TABLE files(foo TEXT)";q.ExecuteNonQuery();}
 bool threw=false;try{using var db=new CatalogDb(path);}catch(SqliteException){threw=true;}Assert(threw);using var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert(exclusive.Length>0);
});
Console.WriteLine($"RESULT passed={passed} failed={failed} fixtures={root}");return failed==0?0:1;

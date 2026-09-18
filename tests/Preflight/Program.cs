using LogMapping;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

if(args[0]=="--migration") {
    var sourceRoot=Path.GetFullPath(args[1]);var migrationPath=Path.Combine(sourceRoot,"migration.db");
    if(File.Exists(migrationPath))throw new IOException("Use a fresh migration destination");
    Console.WriteLine("PROGRESS cloning synthetic DB for migration");
    using(var source=new CatalogDb(Path.Combine(sourceRoot,"large.db")))source.BackupTo(migrationPath);
    Console.WriteLine("PROGRESS preparing legacy name-only FTS schema");
    using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=migrationPath,Pooling=false}.ToString())) {
        c.Open();using var q=c.CreateCommand();q.CommandText="DROP TRIGGER files_fts_ai;DROP TRIGGER files_fts_ad;DROP TABLE files_fts;CREATE VIRTUAL TABLE files_fts USING fts5(name,content='files',content_rowid='id',tokenize='trigram');DELETE FROM meta WHERE k='fts_schema';";q.ExecuteNonQuery();
    }
    Console.WriteLine("PROGRESS rebuilding path index from legacy schema");var timer=Stopwatch.StartNew();
    using(var migrated=new CatalogDb(migrationPath)) {
        var migrationSeconds=timer.Elapsed.TotalSeconds;
        var driveRows=JsonDocument.Parse(migrated.DescribeJson()).RootElement.GetProperty("drives");var selected=driveRows.EnumerateArray().Select(x=>x.GetProperty("id").GetInt64()).ToArray();
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=migrationPath,Pooling=false}.ToString());c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT count(*) FROM files";var records=Convert.ToInt64(q.ExecuteScalar());
        q.CommandText="SELECT v FROM meta WHERE k='fts_schema'";if((string?)q.ExecuteScalar()!="2")throw new Exception("Migration flag missing");
        var query=$"set-{(records-1)/1000}/asset-{records-1:D7}";var result=JsonDocument.Parse(migrated.SearchJson(query,5,selected)).RootElement;
        if(records!=6400000||selected.Length!=40||result.GetArrayLength()!=1||migrated.IndexWarning!=null)throw new Exception("Migration verification failed");
        File.WriteAllText(Path.Combine(sourceRoot,"migration-results.json"),JsonSerializer.Serialize(new{passed=true,records,drives=selected.Length,migrationSeconds,peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64}));
        Console.WriteLine($"PASS legacy FTS migration: {records} records, {selected.Length} drives, {migrationSeconds:F3}s");
    }
    return 0;
}
if(args[0]=="--backup-policy") {
    using var source=new CatalogDb(Path.Combine(Path.GetFullPath(args[1]),"large.db"));
    var description=JsonDocument.Parse(source.DescribeJson()).RootElement;
    var firstId=description.GetProperty("drives")[0].GetProperty("id").GetInt64();
    source.SetItemColor(firstId,"projects-2026/set-0/asset-0000000.jpg","#red");
    if(!description.GetProperty("autoBackupSkipped").GetBoolean()||source.BackupIfDirty())throw new Exception("Large DB backup policy failed");
    Console.WriteLine("PASS >1GB dirty database explicitly skips exit backup; manual backup/restore tested separately");return 0;
}
if(args[0]=="--export-existing") {
    using var source=new CatalogDb(Path.Combine(Path.GetFullPath(args[1]),"large.db"));
    var exportIds=JsonDocument.Parse(source.DescribeJson()).RootElement.GetProperty("drives").EnumerateArray().Select(x=>x.GetProperty("id").GetInt64()).ToArray();
    source.ExportViewer(args[2],exportIds);Console.WriteLine("PASS final viewer regenerated from existing database");return 0;
}
if(args[0]=="--crash-child") {
    var child=new CatalogDb(args[1]); var id=child.CreateDrive("fixture");
    child.InsertStreaming(id,e=> {for(int i=0;i<9000;i++)e("","durable-"+i+".txt",false,1,"");});
    Console.WriteLine("READY");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);return 0;
}
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
int count=args.Length>1?int.Parse(args[1]):2_000_000,passed=0,failed=0;
int driveCount=args.Length>2&&int.TryParse(args[2],out var requestedDrives)?requestedDrives:20;
if(driveCount<1||count<driveCount)throw new ArgumentException("Invalid fixture size");
var metrics=new List<object>();
void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
void Test(string name,Action action){var sw=Stopwatch.StartNew();try{action();passed++;Console.WriteLine($"PASS {name}: {sw.Elapsed.TotalSeconds:F3}s");metrics.Add(new{name,seconds=sw.Elapsed.TotalSeconds,ok=true});}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);metrics.Add(new{name,seconds=sw.Elapsed.TotalSeconds,ok=false,error=e.Message});}Console.Out.Flush();}
long SqlCount(string path,string sql){using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());c.Open();using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(q.ExecuteScalar());}
string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
int Rows(string json)=>JsonDocument.Parse(json).RootElement.GetArrayLength();
Test("committed WAL survives owned process termination",()=>{
    var path=Path.Combine(root,"crash.db");var start=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,UseShellExecute=false,CreateNoWindow=true};
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--crash-child");start.ArgumentList.Add(path);
    using var child=Process.Start(start)!;
    try{var line=child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();Assert(line=="READY");child.Kill();child.WaitForExit();}
    finally{if(!child.HasExited){child.Kill();child.WaitForExit();}}
    using var reopened=new CatalogDb(path);Assert(SqlCount(path,"SELECT count(*) FROM files")==9000);
});
Test("failed backup preserves previous backup and removes temp",()=>{
    using var db=new CatalogDb(Path.Combine(root,"backup-failure.db"));db.DescribeJson();var p=Path.Combine(root,"locked.db");db.BackupTo(p);var before=Hash(p);
    bool threw=false;using(var held=new FileStream(p,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){try{db.BackupTo(p);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){threw=true;}}
    Assert(threw&&Hash(p)==before);Assert(!Directory.GetFiles(root,"locked.db.tmp-*").Any());
});
Test("read-only catalog save preserves original",()=>{
    var p=Path.Combine(root,"readonly.hcat");File.WriteAllText(p,"original");File.SetAttributes(p,FileAttributes.ReadOnly);bool threw=false;
    try{try{AtomicStorage.Write(p,"replacement");}catch(UnauthorizedAccessException){threw=true;}catch(Exception e) when(e is IOException or UnauthorizedAccessException){threw=true;}}
    finally{File.SetAttributes(p,FileAttributes.Normal);}
    Assert(threw&&File.ReadAllText(p)=="original");
});
Test("SQLite full failure preserves prior generation",()=>{
    var path=Path.Combine(root,"full.db");using var db=new CatalogDb(path);var old=db.CreateDrive("old");db.InsertStreaming(old,e=>e("","original.txt",false,1,""));
    var connection=(SqliteConnection)typeof(CatalogDb).GetField("_conn",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(db)!;
    using(var q=connection.CreateCommand()){q.CommandText="PRAGMA page_count";var pages=Convert.ToInt64(q.ExecuteScalar());q.CommandText="PRAGMA max_page_count="+(pages+2);q.ExecuteScalar();}
    var next=db.CreateDrive("new");bool threw=false;try{db.InsertStreaming(next,e=>{for(int i=0;i<10000;i++)e("",new string('x',500)+i+".txt",false,1,"");});}catch(SqliteException e){threw=e.SqliteErrorCode==13;}
    Assert(threw);Assert(Rows(db.GetChildrenJson(old,""))==1);
});
if(args.Contains("--fault-only")){Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;}
var dbPath=Path.Combine(root,"large.db");var ids=new List<long>();
using(var db=new CatalogDb(dbPath)) {
    Test("generate "+count+" synthetic files across "+driveCount+" drives",()=>{
        for(int d=0;d<driveCount;d++){
            var id=db.CreateDrive("fixture-"+d);ids.Add(id);
            db.UpsertDrive(JsonSerializer.SerializeToElement(new{dbId=id,num=d+1,name="Synthetic "+d,cap=1048576,used=500000,scannedPath="fixture-"+d}));
            int start=d*count/driveCount,end=(d+1)*count/driveCount;
            db.InsertStreaming(id,e=>{for(int i=start;i<end;i++)e("projects-2026/set-"+(i/1000),$"asset-{i:D7}.jpg",false,i%100000,"2026-09-18");});
            Console.WriteLine($"PROGRESS synthetic drives={d+1}/{driveCount} rows={end}");Console.Out.Flush();
        }
        Assert(SqlCount(dbPath,"SELECT count(*) FROM files")==count);
    });
    Test("unique match on last drive",()=>Assert(Rows(db.SearchJson($"asset-{count-1:D7}",50,ids))==1));
    Test("missing query",()=>Assert(Rows(db.SearchJson("nonexistent-xyz",50,ids))==0));
    Test("common query bounded and ordered",()=>Assert(Rows(db.SearchJson("asset",50001,ids,sort:"size-desc"))==Math.Min(50001,count)));
    Test("two-character query",()=>Assert(Rows(db.SearchJson("26",100,ids))==100));
    Test("concurrent search and tag writes",()=>{
        var tasks=Enumerable.Range(0,8).Select(i=>Task.Run(()=>{
            for(int n=0;n<5;n++){
                db.SetItemColor(ids[0],"projects-2026/set-0/asset-0000000.jpg","#red");
                Assert(Rows(db.SearchJson("asset-0000000",10,ids,"#red"))==1);
            }
        })).ToArray();Task.WaitAll(tasks);
    });
    Test("CSV export all synthetic records",()=>{db.ExportCsv(Path.Combine(root,"large.csv"),ids);Assert(File.ReadLines(Path.Combine(root,"large.csv")).Count()==count+1);});
    Test("HTML viewer cold export",()=>db.ExportViewer(Path.Combine(root,"large-viewer.html"),ids));
    Test("HTML viewer cached export",()=>db.ExportViewer(Path.Combine(root,"cached-viewer.html"),ids));
    Test("backup set and relocation to clean directory",()=>{
        var info=JsonDocument.Parse(db.DescribeJson()).RootElement;var guid=info.GetProperty("databaseId").GetString();
        var moved=Path.Combine(root,"relocated","data");Directory.CreateDirectory(moved);
        db.BackupTo(Path.Combine(moved,"catalog.db"));AtomicStorage.Write(Path.Combine(moved,"catalog.hcat"),JsonSerializer.Serialize(new{databaseId=guid,drives=ids.Select((id,i)=>new{dbId=id,num=i+1,name="Synthetic "+i,scannedPath="fixture-"+i})}));
        using var restored=new CatalogDb(Path.Combine(moved,"catalog.db"));
        Assert(JsonDocument.Parse(restored.DescribeJson()).RootElement.GetProperty("databaseId").GetString()==guid);
        Assert(SqlCount(Path.Combine(moved,"catalog.db"),"SELECT count(*) FROM files")==count);
        Assert(Rows(restored.SearchJson("asset-0000000",10,ids,"#red"))==1);
    });
}
Test("large database reopen and search",()=>{using var db=new CatalogDb(dbPath);Assert(Rows(db.SearchJson($"asset-{count-1:D7}",10,ids))==1);});
Test("database integrity_check",()=>{using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=dbPath,Pooling=false}.ToString());c.Open();using var q=c.CreateCommand();q.CommandText="PRAGMA integrity_check";Assert((string?)q.ExecuteScalar()=="ok");});
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{passed,failed,count,driveCount,peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64,dbBytes=new FileInfo(dbPath).Length,htmlBytes=new FileInfo(Path.Combine(root,"large-viewer.html")).Length,metrics},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"RESULT passed={passed} failed={failed} root={root}");return failed==0?0:1;

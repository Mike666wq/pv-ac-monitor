using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExperimentMonitor {
 public sealed class HistoryFilter {
  public DateTime FromUtc=DateTime.UtcNow.AddDays(-1), ToUtc=DateTime.UtcNow.AddSeconds(1);
  public string Source="serial", ExperimentId="";
  public string[] Devices=new string[0], Points=new string[0];
  public HistoryFilter Copy(){return new HistoryFilter{FromUtc=FromUtc.ToUniversalTime(),ToUtc=ToUtc.ToUniversalTime(),Source=Source,ExperimentId=ExperimentId,Devices=(string[])Devices.Clone(),Points=(string[])Points.Clone()};}
 }
 public sealed class StoredExperiment {public string Id,Name,Note;public DateTime StartedUtc;public DateTime? EndedUtc;public override string ToString(){return Name;}}
 public sealed class StoredObservation {
  public long Id,Round; public string Database,Session,ExperimentId,Source,Point,Description,Device,Unit,Mode,Status,Value,Tx,Rx,Payload,ConfigVersion;
  public int Slave,Address,Count; public DateTime Utc; public DateTime? RoundStartedUtc; public long Milliseconds; public double? Number,RawNumber;public string Quality;
 }
 public sealed class ExperimentStoreStatus {public string DatabasePath,LastError;public long Committed;public int Pending,QueuedBytes;public bool Recording,DiskWarning;public long AvailableDiskBytes;}
 public sealed class ExperimentStore:IDisposable {
  sealed class Work {public Observation O;public long Round;public string Unit,Experiment,DatabasePath;public TaskCompletionSource<bool> Barrier;public string Event,Note;public int Bytes;}
  readonly BlockingCollection<Work> queue=new BlockingCollection<Work>(2048);readonly Thread worker;readonly string root,source,config;readonly int days;readonly Func<DateTime,string> pathResolver;
  const int BytesLimit=16*1024*1024;int queuedBytes;long diskBytes;volatile bool diskWarning;
  volatile bool closed;string error="",path="",experiment="";long committed;
  public Task Ready{get{return ready.Task;}}readonly TaskCompletionSource<bool> ready=new TaskCompletionSource<bool>();
  public ExperimentStore(string root,string source,string configJson,int periodDays=30):this(root,source,configJson,periodDays,null,null){}
  public ExperimentStore(string root,string source,string configJson,int periodDays,Func<DateTime,string> resolver):this(root,source,configJson,periodDays,resolver,null){}
  public ExperimentStore(string root,string source,string configJson,int periodDays,Func<DateTime,string> resolver,string sessionId){if(source!="serial"&&source!="simulation")throw new ArgumentException("来源必须为serial或simulation");if(periodDays<1||periodDays>3650)throw new ArgumentOutOfRangeException("periodDays");this.root=Path.GetFullPath(root);this.source=source;config=configJson??"";days=periodDays;pathResolver=resolver;SessionId=String.IsNullOrWhiteSpace(sessionId)?Guid.NewGuid().ToString("N"):sessionId;Directory.CreateDirectory(this.root);worker=new Thread(Loop){IsBackground=true,Name="Experiment SQLite writer"};worker.Start();}
  public string SessionId{get;private set;}
  public string CatalogPath{get{return Path.Combine(root,source+".catalog.db");}}
  public string DatabasePath{get{return path;}}public long Committed{get{return Interlocked.Read(ref committed);}}public string LastError{get{return error;}}
  public ExperimentStoreStatus Status{get{return new ExperimentStoreStatus{DatabasePath=path,LastError=error,Committed=Committed,Pending=queue.Count,QueuedBytes=Volatile.Read(ref queuedBytes),AvailableDiskBytes=Interlocked.Read(ref diskBytes),DiskWarning=diskWarning,Recording=!closed&&error.Length==0};}}
  public void Record(Observation o,long round,string unit){Record(o,round,unit,null);}
  public void Record(Observation o,long round,string unit,string databasePath){if(o==null)throw new ArgumentNullException("o");if(o.Source!=source)throw new ArgumentException("观测来源与数据库不一致");Observation copy=new Observation{Point=o.Point,Description=o.Description,Device=o.Device,Mode=o.Mode,Source=o.Source,Status=o.Status,Value=o.Value,Slave=o.Slave,Address=o.Address,Count=o.Count,Utc=o.Utc.ToUniversalTime(),RoundStartedUtc=o.RoundStartedUtc==DateTime.MinValue?DateTime.MinValue:o.RoundStartedUtc.ToUniversalTime(),Milliseconds=o.Milliseconds,Number=o.Number,RawNumber=o.RawNumber,Unit=o.Unit,Quality=o.Quality,ConfigVersion=o.ConfigVersion,SessionId=o.SessionId,Round=o.Round,Tx=(byte[])o.Tx.Clone(),Rx=(byte[])o.Rx.Clone(),Payload=(byte[])o.Payload.Clone()};if(copy.Number.HasValue&&(double.IsNaN(copy.Number.Value)||double.IsInfinity(copy.Number.Value)))copy.Number=null;Add(new Work{O=copy,Round=round,Unit=unit??"",Experiment=experiment,DatabasePath=databasePath});}
  public void RecordPolicy(string type,string detail,long round,DateTime utc){if(String.IsNullOrWhiteSpace(type))throw new ArgumentException("type");Add(new Work{Event="policy",Unit=type,Note=detail??"",Round=round,DatabasePath=pathResolver==null?null:pathResolver(utc.ToUniversalTime())});}
  static int Estimate(Work w){
   long bytes=256+(w.Unit??"").Length*2L+(w.Note??"").Length*2L;
   if(w.O!=null){var o=w.O;bytes+=o.Tx.Length+(long)o.Rx.Length+o.Payload.Length;foreach(string text in new[]{o.Point,o.Description,o.Device,o.Mode,o.Source,o.Status,o.Value,o.Unit,o.Quality,o.ConfigVersion,o.SessionId})bytes+=(text??"").Length*2L;}
   if(bytes>BytesLimit)throw new IOException("单条记录超过16 MiB载荷预算");return (int)bytes;
  }
  void Add(Work w){
   if(closed)throw new ObjectDisposedException("ExperimentStore");if(error.Length>0)throw new IOException(error);
   try{w.Bytes=Estimate(w);}catch(Exception e){error="记录已停止："+e.Message;throw;}
   int budget=Interlocked.Add(ref queuedBytes,w.Bytes);
   if(budget>BytesLimit){Interlocked.Add(ref queuedBytes,-w.Bytes);error="记录队列超过16 MiB预算；记录已停止，请检查磁盘";throw new IOException(error);}
   bool added=false;try{added=queue.TryAdd(w);}finally{if(!added)Interlocked.Add(ref queuedBytes,-w.Bytes);}
   if(!added){error="记录队列已满；记录已停止，请检查磁盘";throw new IOException(error);}
  }
  public Task FlushAsync(){if(!worker.IsAlive&&ready.Task.IsCompleted)throw new IOException(error.Length==0?"记录线程已停止":error);var t=new TaskCompletionSource<bool>();Add(new Work{Barrier=t});return t.Task;}
  public string BeginExperiment(string name,string note){if(!string.IsNullOrEmpty(experiment))throw new InvalidOperationException("请先结束当前实验");string id=Guid.NewGuid().ToString("N");Add(new Work{Event="begin",Experiment=id,Unit=name??"",Note=note??""});experiment=id;return id;}
  public void EndExperiment(){if(experiment.Length==0)return;Add(new Work{Event="end",Experiment=experiment});experiment="";}
  string FileFor(DateTime utc){if(pathResolver!=null)return pathResolver(utc.ToUniversalTime());long day=(long)(utc.Date-new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc)).TotalDays;long bucket=(long)Math.Floor(day/(double)days);DateTime start=new DateTime(2020,1,1).AddDays(bucket*days);return Path.Combine(root,source+"-"+start.ToString("yyyyMMdd")+"-"+days+"d.db");}
  static void Execute(SQLiteConnection c,SQLiteTransaction t,string sql,params object[] a){using(var q=new SQLiteCommand(sql,c,t)){for(int i=0;i<a.Length;i++)q.Parameters.AddWithValue("@p"+i,a[i]??DBNull.Value);q.ExecuteNonQuery();}}
  SQLiteConnection Open(DateTime utc){string file=FileFor(utc);Directory.CreateDirectory(Path.GetDirectoryName(file));var b=new SQLiteConnectionStringBuilder{DataSource=file,Version=3};var c=new SQLiteConnection(b.ConnectionString+";BusyTimeout=5000;Synchronous=Full;");c.Open();using(var t=c.BeginTransaction()){Execute(c,t,"CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,source TEXT,started_utc TEXT,config_json TEXT);CREATE TABLE IF NOT EXISTS experiments(id TEXT PRIMARY KEY,name TEXT,note TEXT,started_utc TEXT,ended_utc TEXT);CREATE TABLE IF NOT EXISTS observations(id INTEGER PRIMARY KEY AUTOINCREMENT,utc_ticks INTEGER NOT NULL,source TEXT,session TEXT,experiment TEXT,round INTEGER,point TEXT,description TEXT,device TEXT,slave INTEGER,address INTEGER,count INTEGER,unit TEXT,mode TEXT,status TEXT,value TEXT,number REAL,elapsed_ms INTEGER,tx TEXT,rx TEXT,payload TEXT,raw_number REAL,quality TEXT,config_version TEXT,round_started_utc_ticks INTEGER);CREATE INDEX IF NOT EXISTS ix_obs_time ON observations(utc_ticks,id);CREATE INDEX IF NOT EXISTS ix_obs_point_time ON observations(point,utc_ticks);CREATE INDEX IF NOT EXISTS ix_obs_experiment ON observations(experiment,utc_ticks)");
    using(var q=new SQLiteCommand("PRAGMA table_info(observations)",c,t))using(var r=q.ExecuteReader()){bool found=false;while(r.Read())if(String.Equals(Convert.ToString(r["name"]),"round_started_utc_ticks",StringComparison.OrdinalIgnoreCase))found=true;if(!found)Execute(c,t,"ALTER TABLE observations ADD COLUMN round_started_utc_ticks INTEGER");}
    Execute(c,t,"INSERT OR IGNORE INTO sessions VALUES(@p0,@p1,@p2,@p3)",SessionId,source,DateTime.UtcNow.ToString("o"),config);t.Commit();}path=file;return c;}
  void CheckDisk(){var d=new DriveInfo(Path.GetPathRoot(root));long free=d.AvailableFreeSpace;Interlocked.Exchange(ref diskBytes,free);diskWarning=free<1024L*1024*1024;if(free<100L*1024*1024)throw new IOException("记录磁盘可用空间低于100 MiB，停止记录");}
  void SaveExperimentEvent(Work w){
   if(w.Event==null)return;
   var b=new SQLiteConnectionStringBuilder{DataSource=CatalogPath,Version=3};
   using(var c=new SQLiteConnection(b.ConnectionString)){c.Open();using(var t=c.BeginTransaction()){
    Execute(c,t,"CREATE TABLE IF NOT EXISTS experiments(id TEXT PRIMARY KEY,name TEXT,note TEXT,started_utc TEXT,ended_utc TEXT)");
    if(w.Event=="begin")Execute(c,t,"INSERT INTO experiments VALUES(@p0,@p1,@p2,@p3,NULL)",w.Experiment,w.Unit,w.Note,DateTime.UtcNow.ToString("o"));
    else Execute(c,t,"UPDATE experiments SET ended_utc=@p0 WHERE id=@p1",DateTime.UtcNow.ToString("o"),w.Experiment);
    t.Commit();
   }}
  }
  void Loop(){
   SQLiteConnection c=null;
   try {
    CheckDisk();c=Open(DateTime.UtcNow);ready.TrySetResult(true);DateTime last=DateTime.UtcNow;
    foreach(Work first in queue.GetConsumingEnumerable()) {
     Interlocked.Add(ref queuedBytes,-first.Bytes);var batch=new List<Work>{first};Work next;
     while(batch.Count<128&&queue.TryTake(out next)){Interlocked.Add(ref queuedBytes,-next.Bytes);batch.Add(next);}
     try {
      int offset=0;
      while(offset<batch.Count) {
       if((DateTime.UtcNow-last).TotalSeconds>30){CheckDisk();last=DateTime.UtcNow;}
       Work lead=batch[offset];DateTime at=lead.O==null?DateTime.UtcNow:lead.O.Utc;string target=!String.IsNullOrEmpty(lead.DatabasePath)?lead.DatabasePath:FileFor(at);
       if(target!=path){c.Dispose();c=Open(at);}
       int end=offset+1;
       while(end<batch.Count&&(String.IsNullOrEmpty(batch[end].DatabasePath)?FileFor(batch[end].O==null?DateTime.UtcNow:batch[end].O.Utc):batch[end].DatabasePath)==target)end++;
       using(var t=c.BeginTransaction()) {
        for(int i=offset;i<end;i++){Work w=batch[i];if(w.O!=null){var o=w.O;Execute(c,t,"INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23)",o.Utc.Ticks,source,SessionId,w.Experiment,w.Round,o.Point,o.Description,o.Device,o.Slave,o.Address,o.Count,w.Unit,o.Mode,o.Status,o.Value,o.Number.HasValue?(object)o.Number.Value:DBNull.Value,o.Milliseconds,Modbus.Hex(o.Tx),Modbus.Hex(o.Rx),Modbus.Hex(o.Payload),o.RawNumber.HasValue?(object)o.RawNumber.Value:DBNull.Value,o.Quality??"unknown",o.ConfigVersion??"",o.RoundStartedUtc==DateTime.MinValue?(object)DBNull.Value:o.RoundStartedUtc.Ticks);}else if(w.Event=="begin")Execute(c,t,"INSERT INTO experiments VALUES(@p0,@p1,@p2,@p3,NULL)",w.Experiment,w.Unit,w.Note,DateTime.UtcNow.ToString("o"));else if(w.Event=="end")Execute(c,t,"UPDATE experiments SET ended_utc=@p0 WHERE id=@p1",DateTime.UtcNow.ToString("o"),w.Experiment);else if(w.Event=="policy"){Execute(c,t,"CREATE TABLE IF NOT EXISTS recording_policy(utc_ticks INTEGER NOT NULL,type TEXT,detail TEXT,round INTEGER)");Execute(c,t,"INSERT INTO recording_policy VALUES(@p0,@p1,@p2,@p3)",DateTime.UtcNow.Ticks,w.Unit,w.Note,w.Round);}}
        t.Commit();
       }
       for(int i=offset;i<end;i++){Work w=batch[i];SaveExperimentEvent(w);if(w.O!=null)Interlocked.Increment(ref committed);if(w.Barrier!=null)w.Barrier.TrySetResult(true);}
       offset=end;
      }
     }catch(Exception e){foreach(var w in batch)if(w.Barrier!=null)w.Barrier.TrySetException(e);throw;}
    }
   }catch(Exception e){error="本地记录失败："+e.Message;ready.TrySetException(e);Work w;while(queue.TryTake(out w)){Interlocked.Add(ref queuedBytes,-w.Bytes);if(w.Barrier!=null)w.Barrier.TrySetException(e);}}
   finally{if(c!=null)c.Dispose();}
  }
  public void Dispose(){if(closed)return;closed=true;queue.CompleteAdding();worker.Join();queue.Dispose();}
 }
 public static class ExperimentHistory {
  public static IEnumerable<string> Databases(string root,string source){if(source!="serial"&&source!="simulation")throw new ArgumentException("来源无效");if(!Directory.Exists(root))return new string[0];var files=Directory.GetFiles(root,source+"-*.db").ToList();string nested=Path.Combine(root,source);if(Directory.Exists(nested))files.AddRange(Directory.GetFiles(nested,"*.db",SearchOption.TopDirectoryOnly));return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();}
  internal static SQLiteConnection OpenRead(string path){var b=new SQLiteConnectionStringBuilder{DataSource=path,Version=3,ReadOnly=true};var c=new SQLiteConnection(b.ConnectionString+";BusyTimeout=5000;");c.Open();return c;}
  internal static SQLiteCommand Command(SQLiteConnection c,HistoryFilter f,string select,long upper){if(f.ToUtc<=f.FromUtc)throw new ArgumentException("结束时间必须晚于开始时间");string sql=select+" FROM observations WHERE utc_ticks>=@f AND utc_ticks<@t AND id<=@u";var q=new SQLiteCommand(c);q.Parameters.AddWithValue("@f",f.FromUtc.ToUniversalTime().Ticks);q.Parameters.AddWithValue("@t",f.ToUtc.ToUniversalTime().Ticks);q.Parameters.AddWithValue("@u",upper);if(!string.IsNullOrEmpty(f.ExperimentId)){sql+=" AND experiment=@e";q.Parameters.AddWithValue("@e",f.ExperimentId);}string[][] filters={f.Devices,f.Points};string[] columns={"device","point"};for(int a=0;a<2;a++)if(filters[a]!=null&&filters[a].Length>0){var n=new List<string>();for(int i=0;i<filters[a].Length;i++){string p="@k"+a+"_"+i;n.Add(p);q.Parameters.AddWithValue(p,filters[a][i]);}sql+=" AND "+columns[a]+" IN ("+string.Join(",",n.ToArray())+")";}q.CommandText=sql;return q;}
  static string Text(SQLiteDataReader r,int n){return r.IsDBNull(n)?"":r.GetString(n);}
  static int Ordinal(SQLiteDataReader r,string name){try{return r.GetOrdinal(name);}catch(IndexOutOfRangeException){return -1;}}
  internal static StoredObservation Read(SQLiteDataReader r,string file){int roundStart=Ordinal(r,"round_started_utc_ticks");return new StoredObservation{Database=file,Id=r.GetInt64(0),Utc=new DateTime(r.GetInt64(1),DateTimeKind.Utc),Source=Text(r,2),Session=Text(r,3),ExperimentId=Text(r,4),Round=r.GetInt64(5),Point=Text(r,6),Description=Text(r,7),Device=Text(r,8),Slave=r.GetInt32(9),Address=r.GetInt32(10),Count=r.GetInt32(11),Unit=Text(r,12),Mode=Text(r,13),Status=Text(r,14),Value=Text(r,15),Number=r.IsDBNull(16)?(double?)null:r.GetDouble(16),Milliseconds=r.GetInt64(17),Tx=Text(r,18),Rx=Text(r,19),Payload=Text(r,20),RawNumber=r.IsDBNull(21)?(double?)null:r.GetDouble(21),Quality=Text(r,22),ConfigVersion=Text(r,23),RoundStartedUtc=roundStart<0||r.IsDBNull(roundStart)?(DateTime?)null:new DateTime(Convert.ToInt64(r.GetValue(roundStart)),DateTimeKind.Utc)};}
  sealed class Cursor {
   public string File;readonly HistoryFilter filter;readonly long upper;readonly bool ascending;
   List<StoredObservation> batch=new List<StoredObservation>();int index;bool ended;long time,id;
   public Cursor(string file,HistoryFilter f,bool ascending=false){File=file;filter=f;this.ascending=ascending;time=ascending?long.MinValue:long.MaxValue;id=ascending?long.MinValue:long.MaxValue;using(var c=OpenRead(file))using(var q=new SQLiteCommand("SELECT COALESCE(MAX(id),0) FROM observations",c))upper=Convert.ToInt64(q.ExecuteScalar());}
   public StoredObservation Peek(){
    if(index<batch.Count)return batch[index];if(ended)return null;
    batch.Clear();index=0;
    using(var c=OpenRead(File))using(var q=Command(c,filter,"SELECT *",upper)){
     q.CommandText+=ascending?" AND (utc_ticks>@cursorTime OR (utc_ticks=@cursorTime AND id>@cursorId)) ORDER BY utc_ticks ASC,id ASC LIMIT 128":" AND (utc_ticks<@cursorTime OR (utc_ticks=@cursorTime AND id<@cursorId)) ORDER BY utc_ticks DESC,id DESC LIMIT 128";
     q.Parameters.AddWithValue("@cursorTime",time);q.Parameters.AddWithValue("@cursorId",id);
     using(var r=q.ExecuteReader())while(r.Read())batch.Add(Read(r,File));
    }
    if(batch.Count==0){ended=true;return null;}return batch[0];
   }
   public void Advance(){var o=Peek();if(o!=null){time=o.Utc.Ticks;id=o.Id;index++;}}
  }
  public static List<StoredObservation> Query(string root,HistoryFilter filter,int limit=500){return QueryPage(root,filter,0,limit);}
  public static List<StoredObservation> QueryPage(string root,HistoryFilter filter,int offset,int limit=500){
   if(offset<0)throw new ArgumentOutOfRangeException("offset");if(limit<1||limit>10000)throw new ArgumentOutOfRangeException("limit");
   var result=new List<StoredObservation>();long skipped=0;Merge(root,filter,false,CancellationToken.None,o=>{if(skipped++<offset)return true;result.Add(o);return result.Count<limit;});
   return result;
  }
  sealed class RoundPart { public string File,Session; public long Round,Upper; }
  sealed class RoundRef { public string Key,Session; public long Round; public DateTime StartedUtc,FirstSampleUtc; public List<RoundPart> Parts=new List<RoundPart>(); }
  static bool HasColumn(SQLiteConnection c,string name){using(var q=new SQLiteCommand("PRAGMA table_info(observations)",c))using(var r=q.ExecuteReader())while(r.Read())if(String.Equals(Convert.ToString(r["name"]),name,StringComparison.OrdinalIgnoreCase))return true;return false;}
  static long MaxId(SQLiteConnection c){using(var q=new SQLiteCommand("SELECT COALESCE(MAX(id),0) FROM observations",c))return Convert.ToInt64(q.ExecuteScalar());}
  static string RoundKey(string file,string session,long round){return (String.IsNullOrEmpty(session)?"legacy:"+file:session)+"\u001f"+round.ToString(CultureInfo.InvariantCulture);}
  /// <summary>Pages whole capture rounds for wide-table previews; offset/total count rounds, not point rows.</summary>
  public static List<List<StoredObservation>> QueryRoundPage(string root,HistoryFilter filter,int offset,int limit,out long totalRounds,bool ascending=true){
   if(offset<0)throw new ArgumentOutOfRangeException("offset");if(limit<1||limit>10000)throw new ArgumentOutOfRangeException("limit");if(filter==null)throw new ArgumentNullException("filter");
   var f=filter.Copy();var roundsByKey=new Dictionary<string,RoundRef>(StringComparer.Ordinal);var files=Databases(root,f.Source).ToArray();
   // Freeze each database's maximum row ID before any grouped preview query.
   foreach(string file in files){using(var c=OpenRead(file)){long upper=MaxId(c);bool hasStart=HasColumn(c,"round_started_utc_ticks");string startExpr=hasStart?"MIN(COALESCE(round_started_utc_ticks,utc_ticks))":"MIN(utc_ticks)";
    using(var q=Command(c,f,"SELECT session,round,"+startExpr+",MIN(utc_ticks),COUNT(*)",upper)){q.CommandText+=" GROUP BY session,round";using(var r=q.ExecuteReader())while(r.Read()){
     string session=Text(r,0);long round=Convert.ToInt64(r.GetValue(1));string key=RoundKey(file,session,round);long startTicks=Convert.ToInt64(r.GetValue(2));long firstTicks=Convert.ToInt64(r.GetValue(3));RoundRef rr;
     if(!roundsByKey.TryGetValue(key,out rr)){rr=new RoundRef{Key=key,Session=session,Round=round,StartedUtc=new DateTime(startTicks,DateTimeKind.Utc),FirstSampleUtc=new DateTime(firstTicks,DateTimeKind.Utc)};roundsByKey.Add(key,rr);}else{DateTime s=new DateTime(startTicks,DateTimeKind.Utc),a=new DateTime(firstTicks,DateTimeKind.Utc);if(s<rr.StartedUtc)rr.StartedUtc=s;if(a<rr.FirstSampleUtc)rr.FirstSampleUtc=a;}
     rr.Parts.Add(new RoundPart{File=file,Session=session,Round=round,Upper=upper});
    }}
   }}
   var ordered=roundsByKey.Values.OrderBy(x=>x.StartedUtc).ThenBy(x=>x.FirstSampleUtc).ThenBy(x=>x.Session,StringComparer.Ordinal).ThenBy(x=>x.Round).ToList();if(!ascending)ordered.Reverse();totalRounds=ordered.Count;
   var selected=ordered.Skip(offset).Take(limit).ToList();var rowsByKey=selected.ToDictionary(x=>x.Key,x=>new List<StoredObservation>(),StringComparer.Ordinal);
   // Fetch only the visible page, in bounded parameter chunks per database.
   foreach(string file in files){var parts=selected.SelectMany(x=>x.Parts).Where(x=>String.Equals(x.File,file,StringComparison.OrdinalIgnoreCase)).ToList();if(parts.Count==0)continue;using(var c=OpenRead(file))for(int at=0;at<parts.Count;at+=200){var chunk=parts.Skip(at).Take(200).ToList();using(var q=Command(c,f,"SELECT *",chunk.Max(x=>x.Upper))){var clauses=new List<string>();for(int i=0;i<chunk.Count;i++){string s="@rs"+i,r="@rr"+i;clauses.Add("(session="+s+" AND round="+r+")");q.Parameters.AddWithValue(s,chunk[i].Session??"");q.Parameters.AddWithValue(r,chunk[i].Round);}q.CommandText+=" AND ("+String.Join(" OR ",clauses.ToArray())+") ORDER BY utc_ticks "+(ascending?"ASC":"DESC")+",id "+(ascending?"ASC":"DESC");using(var reader=q.ExecuteReader())while(reader.Read()){var o=Read(reader,file);string key=RoundKey(file,o.Session,o.Round);List<StoredObservation> list;if(rowsByKey.TryGetValue(key,out list))list.Add(o);}}}}
   return selected.Select(x=>rowsByKey[x.Key]).ToList();
  }
  public static long Stream(string root,HistoryFilter filter,Action<StoredObservation> visitor,bool ascending=true,CancellationToken cancellationToken=default(CancellationToken)){
   if(visitor==null)throw new ArgumentNullException("visitor");long count=0;Merge(root,filter,ascending,cancellationToken,o=>{visitor(o);count++;return true;});return count;
  }
  static void Merge(string root,HistoryFilter filter,bool ascending,CancellationToken cancellationToken,Func<StoredObservation,bool> visit){
   if(filter==null)throw new ArgumentNullException("filter");var f=filter.Copy();var cursors=Databases(root,f.Source).Select(file=>new Cursor(file,f,ascending)).ToList();
   while(true){cancellationToken.ThrowIfCancellationRequested();Cursor chosen=null;StoredObservation best=null;foreach(var cursor in cursors){var o=cursor.Peek();if(o==null)continue;bool before=best==null||(ascending?o.Utc<best.Utc:o.Utc>best.Utc);if(!before&&best!=null&&o.Utc==best.Utc){int db=String.CompareOrdinal(o.Database,best.Database);before=ascending?(db<0||(db==0&&o.Id<best.Id)):(db>0||(db==0&&o.Id>best.Id));}if(before){chosen=cursor;best=o;}}
    if(chosen==null)break;if(!visit(best))break;chosen.Advance();
   }
  }
  public static List<StoredExperiment> ListExperiments(string root,string source){
   if(source!="serial"&&source!="simulation")throw new ArgumentException("来源无效");string file=Path.Combine(root,source+".catalog.db");var result=new List<StoredExperiment>();if(!File.Exists(file))return result;
   using(var c=OpenRead(file))using(var q=new SQLiteCommand("SELECT id,name,note,started_utc,ended_utc FROM experiments ORDER BY started_utc DESC,id DESC",c))using(var r=q.ExecuteReader())while(r.Read())result.Add(new StoredExperiment{Id=Text(r,0),Name=Text(r,1),Note=Text(r,2),StartedUtc=DateTime.Parse(Text(r,3),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime(),EndedUtc=r.IsDBNull(4)?(DateTime?)null:DateTime.Parse(Text(r,4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime()});return result;
  }
  public static long Count(string root,HistoryFilter f){long n=0;foreach(string file in Databases(root,f.Source))using(var c=OpenRead(file))using(var q=Command(c,f,"SELECT COUNT(*)",long.MaxValue))n+=Convert.ToInt64(q.ExecuteScalar());return n;}
 }
}



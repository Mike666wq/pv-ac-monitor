using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
  sealed class Work {public Observation O;public ControlEvent Control;public long Round;public string Unit,Experiment,DatabasePath;public TaskCompletionSource<bool> Barrier;public string Event,Note;public int Bytes;}
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
  /// <summary>控制操作审计。写入库时带分区路径；未采集时由引擎只写 JSONL。</summary>
  public void RecordControl(ControlEvent e,string databasePath){if(e==null)throw new ArgumentNullException("e");e.Source=source;e.Session=SessionId;if(e.UtcTicks==0)e.UtcTicks=DateTime.UtcNow.Ticks;Add(new Work{Control=e,DatabasePath=databasePath});}
  static int Estimate(Work w){
   long bytes=256+(w.Unit??"").Length*2L+(w.Note??"").Length*2L;
   if(w.O!=null){var o=w.O;bytes+=o.Tx.Length+(long)o.Rx.Length+o.Payload.Length;foreach(string text in new[]{o.Point,o.Description,o.Device,o.Mode,o.Source,o.Status,o.Value,o.Unit,o.Quality,o.ConfigVersion,o.SessionId})bytes+=Utf8Bytes(text);}
   if(w.Control!=null){var c=w.Control;foreach(string text in new[]{c.Source,c.Session,c.Operator,c.Point,c.Label,c.Status,c.Detail,c.Tx,c.Rx,c.ConfigVersion})bytes+=Utf8Bytes(text);}
   if(bytes>BytesLimit)throw new IOException("单条记录超过16 MiB载荷预算");return (int)bytes;
  }
  /// <summary>文本按 UTF-8 的真实字节数计。旧实现一律按"字符数×2"估，
  /// 对纯 ASCII 高估整整一倍 —— 队列预算是按这个值算的，于是实际只用掉一半预算就被判超限。</summary>
  static long Utf8Bytes(string text){return text==null?0L:Encoding.UTF8.GetByteCount(text);}
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
  SQLiteConnection Open(DateTime utc){return OpenAt(FileFor(utc));}
  /// <summary>
  /// 打开指定文件并确保 schema 就绪。
  /// 旧实现里只有 Open(DateTime)，而写入路径要的是"引擎传进来的那个分区文件"，
  /// 不是"按时间算出来的那个文件" —— 两者不一致时，行会静默落进另一个分区库，
  /// "一轮一库"就不成立了。这里把"按时间推导"和"按文件打开"拆成两件事。
  /// </summary>
  SQLiteConnection OpenAt(string file){Directory.CreateDirectory(Path.GetDirectoryName(file));var b=new SQLiteConnectionStringBuilder{DataSource=file,Version=3};var c=new SQLiteConnection(b.ConnectionString+";BusyTimeout=5000;Synchronous=Full;");c.Open();
   try{using(SQLiteCommand q=new SQLiteCommand("PRAGMA journal_mode=WAL",c))q.ExecuteNonQuery();}catch{}// WAL：读写不再互相阻塞。失败时退回原来的回滚日志模式，不影响正确性。
   using(var t=c.BeginTransaction()){Execute(c,t,"CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,source TEXT,started_utc TEXT,config_json TEXT);CREATE TABLE IF NOT EXISTS experiments(id TEXT PRIMARY KEY,name TEXT,note TEXT,started_utc TEXT,ended_utc TEXT);CREATE TABLE IF NOT EXISTS observations(id INTEGER PRIMARY KEY AUTOINCREMENT,utc_ticks INTEGER NOT NULL,source TEXT,session TEXT,experiment TEXT,round INTEGER,point TEXT,description TEXT,device TEXT,slave INTEGER,address INTEGER,count INTEGER,unit TEXT,mode TEXT,status TEXT,value TEXT,number REAL,elapsed_ms INTEGER,tx TEXT,rx TEXT,payload TEXT,raw_number REAL,quality TEXT,config_version TEXT,round_started_utc_ticks INTEGER);CREATE INDEX IF NOT EXISTS ix_obs_time ON observations(utc_ticks,id);CREATE INDEX IF NOT EXISTS ix_obs_point_time ON observations(point,utc_ticks);CREATE INDEX IF NOT EXISTS ix_obs_experiment ON observations(experiment,utc_ticks);CREATE INDEX IF NOT EXISTS ix_obs_round ON observations(session,round,utc_ticks);CREATE TABLE IF NOT EXISTS control_events(id INTEGER PRIMARY KEY AUTOINCREMENT,utc_ticks INTEGER NOT NULL,source TEXT,session TEXT,operator TEXT,point TEXT,label TEXT,slave INTEGER,address INTEGER,function INTEGER,old_value INTEGER,new_value INTEGER,readback INTEGER,status TEXT,detail TEXT,tx TEXT,rx TEXT,elapsed_ms INTEGER,config_version TEXT);CREATE INDEX IF NOT EXISTS ix_control_time ON control_events(utc_ticks,id)");
    // 迁移一律"只加可空列"，不动历史行、不删索引、不 VACUUM —— UPGRADE.md 对旧库的承诺。
    EnsureColumn(c,t,"observations","round_started_utc_ticks","ALTER TABLE observations ADD COLUMN round_started_utc_ticks INTEGER");
    EnsureColumn(c,t,"control_events","write_id","ALTER TABLE control_events ADD COLUMN write_id TEXT");
    EnsureColumn(c,t,"control_events","target","ALTER TABLE control_events ADD COLUMN target TEXT");
    EnsureColumn(c,t,"control_events","evidence","ALTER TABLE control_events ADD COLUMN evidence TEXT");
    Execute(c,t,"INSERT OR IGNORE INTO sessions VALUES(@p0,@p1,@p2,@p3)",SessionId,source,DateTime.UtcNow.ToString("o"),config);t.Commit();}path=file;return c;}
  /// <summary>加列前先查列是否存在。老库反复 ALTER 会踩到 SQLite 不支持 DROP COLUMN 的坑，
  /// 所以这里只做"加"，并且只在列真的缺失时才执行。</summary>
  static void EnsureColumn(SQLiteConnection c,SQLiteTransaction t,string table,string column,string ddl){
   using(var q=new SQLiteCommand("PRAGMA table_info("+table+")",c,t))using(var r=q.ExecuteReader()){
    while(r.Read())if(String.Equals(Convert.ToString(r["name"]),column,StringComparison.OrdinalIgnoreCase))return;
   }
   Execute(c,t,ddl);
  }
  void CheckDisk(){var d=new DriveInfo(Path.GetPathRoot(root));long free=d.AvailableFreeSpace;Interlocked.Exchange(ref diskBytes,free);diskWarning=free<1024L*1024*1024;if(free<100L*1024*1024)throw new IOException("记录磁盘可用空间低于100 MiB，停止记录");}
  void SaveExperimentEvent(Work w){
   if(w.Event==null)return;
   var b=new SQLiteConnectionStringBuilder{DataSource=CatalogPath,Version=3};
   using(var c=new SQLiteConnection(b.ConnectionString+";BusyTimeout=5000;")){c.Open();using(var t=c.BeginTransaction()){
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
       if(!String.Equals(target,path,StringComparison.OrdinalIgnoreCase)){c.Dispose();c=OpenAt(target);}
       int end=offset+1;
       while(end<batch.Count&&(String.IsNullOrEmpty(batch[end].DatabasePath)?FileFor(batch[end].O==null?DateTime.UtcNow:batch[end].O.Utc):batch[end].DatabasePath)==target)end++;
       using(var t=c.BeginTransaction()) {
        for(int i=offset;i<end;i++){Work w=batch[i];if(w.O!=null){var o=w.O;Execute(c,t,"INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23)",o.Utc.Ticks,source,SessionId,w.Experiment,w.Round,o.Point,o.Description,o.Device,o.Slave,o.Address,o.Count,w.Unit,o.Mode,o.Status,o.Value,o.Number.HasValue?(object)o.Number.Value:DBNull.Value,o.Milliseconds,Modbus.Hex(o.Tx),Modbus.Hex(o.Rx),Modbus.Hex(o.Payload),o.RawNumber.HasValue?(object)o.RawNumber.Value:DBNull.Value,o.Quality??"unknown",o.ConfigVersion??"",o.RoundStartedUtc==DateTime.MinValue?(object)DBNull.Value:o.RoundStartedUtc.Ticks);}else if(w.Event=="begin")Execute(c,t,"INSERT INTO experiments VALUES(@p0,@p1,@p2,@p3,NULL)",w.Experiment,w.Unit,w.Note,DateTime.UtcNow.ToString("o"));else if(w.Event=="end")Execute(c,t,"UPDATE experiments SET ended_utc=@p0 WHERE id=@p1",DateTime.UtcNow.ToString("o"),w.Experiment);else if(w.Event=="policy"){Execute(c,t,"CREATE TABLE IF NOT EXISTS recording_policy(utc_ticks INTEGER NOT NULL,type TEXT,detail TEXT,round INTEGER)");Execute(c,t,"INSERT INTO recording_policy VALUES(@p0,@p1,@p2,@p3)",DateTime.UtcNow.Ticks,w.Unit,w.Note,w.Round);}else if(w.Control!=null){var k=w.Control;Execute(c,t,"INSERT INTO control_events(utc_ticks,source,session,operator,point,label,slave,address,function,old_value,new_value,readback,status,detail,tx,rx,elapsed_ms,config_version,write_id,target,evidence) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20)",k.UtcTicks,source,SessionId,k.Operator,k.Point,k.Label,k.Slave,k.Address,k.Function,k.OldValue.HasValue?(object)k.OldValue.Value:DBNull.Value,k.NewValue.HasValue?(object)k.NewValue.Value:DBNull.Value,k.Readback.HasValue?(object)k.Readback.Value:DBNull.Value,k.Status,k.Detail??"",k.Tx??"",k.Rx??"",k.ElapsedMs,k.ConfigVersion??"",k.WriteId??"",k.Target??"",k.Evidence??"");}}
        t.Commit();
       }
       for(int i=offset;i<end;i++){Work w=batch[i];SaveExperimentEvent(w);if(w.O!=null)Interlocked.Increment(ref committed);if(w.Barrier!=null)w.Barrier.TrySetResult(true);}
       offset=end;
      }
      }catch(Exception e){
       // 瞬时错误（库被占用、磁盘忙）不该杀死写线程 —— 一旦写线程退出，
       // 队列很快写满，采集随即被迫停止，几十小时的实验数据从这一刻起全部丢失。
       // 真正的致命错误（磁盘满、文件损坏）才向上抛。
       if(IsTransient(e)){
        Log("记录暂时失败，已跳过本批（"+e.Message+"）");
        foreach(var w in batch)if(w.Barrier!=null)w.Barrier.TrySetException(e);
        continue;
       }
       foreach(var w in batch)if(w.Barrier!=null)w.Barrier.TrySetException(e);throw;
      }
    }
   }catch(Exception e){error="本地记录失败："+e.Message;ready.TrySetException(e);Work w;while(queue.TryTake(out w)){Interlocked.Add(ref queuedBytes,-w.Bytes);if(w.Barrier!=null)w.Barrier.TrySetException(e);}}
    finally{if(c!=null){try{using(SQLiteCommand q=new SQLiteCommand("PRAGMA wal_checkpoint(TRUNCATE)",c))q.ExecuteNonQuery();}catch{}c.Dispose();}}
  }
  /// <summary>库被占用/忙这类瞬时错误：可以重试，不该终止记录。</summary>
  static bool IsTransient(Exception e){
   var sqlite=e as SQLiteException;
   if(sqlite!=null){
    string message=sqlite.Message??"";
    int code=sqlite.ErrorCode;
    if(code==5||code==6)return true;                        // SQLITE_BUSY / SQLITE_LOCKED
    if(message.IndexOf("locked",StringComparison.OrdinalIgnoreCase)>=0)return true;
    if(message.IndexOf("busy",StringComparison.OrdinalIgnoreCase)>=0)return true;
   }
   var io=e as IOException;
   if(io!=null&&io.Message!=null&&io.Message.IndexOf("being used by another process",StringComparison.OrdinalIgnoreCase)>=0)return true;
   return false;
  }
  /// <summary>记录线程的诊断信息（瞬时失败、写线程退出超时等），由引擎转写到会话日志。</summary>
  public event Action<string> Logged;
  void Log(string message){var handler=Logged;if(handler!=null)foreach(Action<string> f in handler.GetInvocationList())try{f(message);}catch{}}
  public void Dispose(){if(closed)return;closed=true;queue.CompleteAdding();if(!worker.Join(5000))Log("记录线程未在 5 秒内结束，已放弃等待（队列数据可能未落盘）");queue.Dispose();}
 }
 public static class ExperimentHistory {
  public static IEnumerable<string> Databases(string root,string source){if(source!="serial"&&source!="simulation")throw new ArgumentException("来源无效");if(!Directory.Exists(root))return new string[0];var files=Directory.GetFiles(root,source+"-*.db").ToList();string nested=Path.Combine(root,source);if(Directory.Exists(nested))files.AddRange(Directory.GetFiles(nested,"*.db",SearchOption.TopDirectoryOnly));return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();}
  internal static SQLiteConnection OpenRead(string path){
   var b=new SQLiteConnectionStringBuilder{DataSource=path,Version=3};SQLiteConnection c=null;
   try{
    // WAL 库需要一个可写的 -shm 索引文件，纯 ReadOnly 连接打不开它。
    // 因此优先以"能连接但 query_only"的方式打开：SQL 层仍然只读，但 SQLite 能自己维护 WAL 索引；
    // 目录确实不可写时再退回纯 ReadOnly。
    c=new SQLiteConnection(b.ConnectionString+";BusyTimeout=5000;");c.Open();
    using(SQLiteCommand q=new SQLiteCommand("PRAGMA query_only=1",c))q.ExecuteNonQuery();
   }catch{
    if(c!=null)try{c.Dispose();}catch{}
    c=new SQLiteConnection(b.ConnectionString+";BusyTimeout=5000;ReadOnly=true;");c.Open();
   }
   try{
    // 大范围 GROUP BY(轮次分组)需要 SQLite 临时 B 树;系统临时目录对该进程不可用时SQLite 抛 SQLITE_CANTOPEN("unable to open database file"),而导出的 spool 因带 (session,round) 索引不受影响。
    // 连接级把查询排序/分组临时存储放进内存:不写库、不改 schema、不依赖系统 TEMP,对旧迁移库同样生效。
    using(SQLiteCommand q=new SQLiteCommand("PRAGMA temp_store=MEMORY",c))q.ExecuteNonQuery();
    return c;
   }catch{c.Dispose();throw;}
  }
  internal static SQLiteCommand Command(SQLiteConnection c,HistoryFilter f,string select,long upper){if(f.ToUtc<=f.FromUtc)throw new ArgumentException("结束时间必须晚于开始时间");string sql=select+" FROM observations WHERE utc_ticks>=@f AND utc_ticks<@t AND id<=@u";var q=new SQLiteCommand(c);q.Parameters.AddWithValue("@f",f.FromUtc.ToUniversalTime().Ticks);q.Parameters.AddWithValue("@t",f.ToUtc.ToUniversalTime().Ticks);q.Parameters.AddWithValue("@u",upper);if(!string.IsNullOrEmpty(f.ExperimentId)){sql+=" AND experiment=@e";q.Parameters.AddWithValue("@e",f.ExperimentId);}q.CommandText=AppendFilters(q,f,sql);return q;}
   /// <summary>设备与测点过滤条件。命令构造与范围统计共用同一段逻辑,避免两处漂移。</summary>
   static string AppendFilters(SQLiteCommand q,HistoryFilter f,string sql){string[][] filters={f.Devices,f.Points};string[] columns={"device","point"};for(int a=0;a<2;a++)if(filters[a]!=null&&filters[a].Length>0){var n=new List<string>();for(int i=0;i<filters[a].Length;i++){string p="@k"+a+"_"+i;n.Add(p);q.Parameters.AddWithValue(p,filters[a][i]);}sql+=" AND "+columns[a]+" IN ("+string.Join(",",n.ToArray())+")";}return sql;}
  static string Text(SQLiteDataReader r,int n){return r.IsDBNull(n)?"":r.GetString(n);}
  static int Ordinal(SQLiteDataReader r,string name){try{return r.GetOrdinal(name);}catch(IndexOutOfRangeException){return -1;}}
  internal static StoredObservation Read(SQLiteDataReader r,string file){return Read(r,file,Ordinal(r,"round_started_utc_ticks"));}
   /// <summary>逐行读取时复用已解析的列序:按列名查序是线性扫描,放在循环内会成为大范围扫描的主要开销。</summary>
   internal static StoredObservation Read(SQLiteDataReader r,string file,int roundStart){return new StoredObservation{Database=file,Id=r.GetInt64(0),Utc=new DateTime(r.GetInt64(1),DateTimeKind.Utc),Source=Text(r,2),Session=Text(r,3),ExperimentId=Text(r,4),Round=r.GetInt64(5),Point=Text(r,6),Description=Text(r,7),Device=Text(r,8),Slave=r.GetInt32(9),Address=r.GetInt32(10),Count=r.GetInt32(11),Unit=Text(r,12),Mode=Text(r,13),Status=Text(r,14),Value=Text(r,15),Number=r.IsDBNull(16)?(double?)null:r.GetDouble(16),Milliseconds=r.GetInt64(17),Tx=Text(r,18),Rx=Text(r,19),Payload=Text(r,20),RawNumber=r.IsDBNull(21)?(double?)null:r.GetDouble(21),Quality=Text(r,22),ConfigVersion=Text(r,23),RoundStartedUtc=roundStart<0||r.IsDBNull(roundStart)?(DateTime?)null:new DateTime(Convert.ToInt64(r.GetValue(roundStart)),DateTimeKind.Utc)};}
  /// <summary>轻量投影:趋势只需要时间、测点与数值。SELECT * 会把 tx/rx/payload 三个 hex 大列一起读,大范围时是主要开销。</summary>
  const string LightColumns="SELECT id,utc_ticks,point,number,raw_number,quality";
  internal static StoredObservation ReadLight(SQLiteDataReader r,string file){return new StoredObservation{Database=file,Id=r.GetInt64(0),Utc=new DateTime(r.GetInt64(1),DateTimeKind.Utc),Point=Text(r,2),Number=r.IsDBNull(3)?(double?)null:r.GetDouble(3),RawNumber=r.IsDBNull(4)?(double?)null:r.GetDouble(4),Quality=Text(r,5)};}
  sealed class Cursor {
   public string File;readonly HistoryFilter filter;readonly long upper;readonly bool ascending;
   List<StoredObservation> batch=new List<StoredObservation>();int index;bool ended;long time,id;
   // 游标起点:0=从头 1=严格(时间+id) 2=仅时间。翻页只按时间续读,同一库内再用 id 兜底去重。
   int seedMode;
   // 单批取行数。批量过小会让每个批次都重开一次只读连接,大范围扫描时连接建立次数成为主要开销。
   const int BatchSize=2048;
   readonly bool light;
   public Cursor(string file,HistoryFilter f,bool ascending=false):this(file,f,ascending,0,0,0,false){}
   public Cursor(string file,HistoryFilter f,bool ascending,long seedTime,long seedId,int seedMode):this(file,f,ascending,seedTime,seedId,seedMode,false){}
   public Cursor(string file,HistoryFilter f,bool ascending,long seedTime,long seedId,int seedMode,bool light){
    File=file;filter=f;this.ascending=ascending;this.seedMode=seedMode;this.light=light;
    time=ascending?long.MinValue:long.MaxValue;id=ascending?long.MinValue:long.MaxValue;
    using(var c=OpenRead(file))using(var q=new SQLiteCommand("SELECT COALESCE(MAX(id),0) FROM observations",c))upper=Convert.ToInt64(q.ExecuteScalar());
    if(seedMode==1){time=seedTime;id=seedId;}
    else if(seedMode==2){time=seedTime;id=ascending?long.MinValue:long.MaxValue;}
    // 3=时间锚点(含该时刻):用于"跳到该时刻",先取到包含锚点的那一轮,再由 QueryRoundWindowFrom 补齐整轮。
    else if(seedMode==3){time=seedTime;id=ascending?long.MinValue:long.MaxValue;}
   }
   public StoredObservation Peek(){
    if(index<batch.Count)return batch[index];if(ended)return null;
    batch.Clear();index=0;
    using(var c=OpenRead(File))using(var q=Command(c,filter,light?LightColumns:"SELECT *",upper)){
     if(seedMode==2)q.CommandText+=ascending?" AND (utc_ticks>@cursorTime)":" AND (utc_ticks<@cursorTime)";
     else if(seedMode==3)q.CommandText+=ascending?" AND (utc_ticks>=@cursorTime)":" AND (utc_ticks<=@cursorTime)";
     else q.CommandText+=ascending?" AND (utc_ticks>@cursorTime OR (utc_ticks=@cursorTime AND id>@cursorId))":" AND (utc_ticks<@cursorTime OR (utc_ticks=@cursorTime AND id<@cursorId))";
     q.CommandText+=ascending?" ORDER BY utc_ticks ASC,id ASC LIMIT "+BatchSize.ToString(CultureInfo.InvariantCulture):" ORDER BY utc_ticks DESC,id DESC LIMIT "+BatchSize.ToString(CultureInfo.InvariantCulture);
     q.Parameters.AddWithValue("@cursorTime",time);q.Parameters.AddWithValue("@cursorId",id);
     using(var r=q.ExecuteReader()){if(light){while(r.Read())batch.Add(ReadLight(r,File));}else{int roundStart=Ordinal(r,"round_started_utc_ticks");while(r.Read())batch.Add(Read(r,File,roundStart));}}
    }
    seedMode=0;// 首批取完后统一按严格(时间+id)游标继续推进。
    if(batch.Count==0){ended=true;return null;}return batch[0];
   }
   public void Advance(){var o=Peek();if(o!=null){time=o.Utc.Ticks;id=o.Id;index++;}}
  }
  public static List<StoredObservation> Query(string root,HistoryFilter filter,int limit=500){return QueryPage(root,filter,0,limit);}
  public static List<StoredObservation> QueryPage(string root,HistoryFilter filter,int offset,int limit=500){
   if(offset<0)throw new ArgumentOutOfRangeException("offset");if(limit<1||limit>10000)throw new ArgumentOutOfRangeException("limit");
   var result=new List<StoredObservation>();long skipped=0;Merge(root,filter,false,CancellationToken.None,o=>{if(skipped++<offset)return true;result.Add(o);return result.Count<limit;},false);
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
   foreach(string file in files){var parts=selected.SelectMany(x=>x.Parts).Where(x=>String.Equals(x.File,file,StringComparison.OrdinalIgnoreCase)).ToList();if(parts.Count==0)continue;using(var c=OpenRead(file))for(int at=0;at<parts.Count;at+=200){var chunk=parts.Skip(at).Take(200).ToList();using(var q=Command(c,f,"SELECT *",chunk.Max(x=>x.Upper))){var clauses=new List<string>();for(int i=0;i<chunk.Count;i++){string s="@rs"+i,r="@rr"+i;clauses.Add("(session="+s+" AND round="+r+")");q.Parameters.AddWithValue(s,chunk[i].Session??"");q.Parameters.AddWithValue(r,chunk[i].Round);}q.CommandText+=" AND ("+String.Join(" OR ",clauses.ToArray())+") ORDER BY utc_ticks "+(ascending?"ASC":"DESC")+",id "+(ascending?"ASC":"DESC");using(var reader=q.ExecuteReader()){int readRoundStart=Ordinal(reader,"round_started_utc_ticks");while(reader.Read()){var o=Read(reader,file,readRoundStart);string key=RoundKey(file,o.Session,o.Round);List<StoredObservation> list;if(rowsByKey.TryGetValue(key,out list))list.Add(o);}}}}}
   return selected.Select(x=>rowsByKey[x.Key]).ToList();
  }
  /// <summary>采集轮分页的键集位置:本页已消费到的最后一条记录。</summary>
  public sealed class RoundCursor {
   public long UtcTicks;public long Id;public string Database;
  }
  /// <summary>一页完整采集轮。每轮含该轮全部测点记录,并给出继续翻页的键集。</summary>
  public sealed class RoundPage {
   public List<List<StoredObservation>> Rounds=new List<List<StoredObservation>>();
   public bool HasMore;public RoundCursor Next;public DateTime FromUtc,ToUtc;
  }
  /// <summary>采集轮分组键:同一会话内同一轮号只算一轮,跨分期库的同轮记录归入同一组;无会话的旧记录按库区分。</summary>
  struct RoundIdentity:IEquatable<RoundIdentity> {
   public readonly string Group;public readonly long Round;public readonly bool Legacy;
   public RoundIdentity(string file,string session,long round){Legacy=String.IsNullOrEmpty(session);Group=Legacy?(file??""):session;Round=round;}
   public bool Equals(RoundIdentity other){return Round==other.Round&&Legacy==other.Legacy&&String.Equals(Group,other.Group,StringComparison.Ordinal);}
   public override bool Equals(object obj){return obj is RoundIdentity&&Equals((RoundIdentity)obj);}
   public override int GetHashCode(){unchecked{return (Group==null?0:Group.GetHashCode())*397^(Legacy?1:0)^Round.GetHashCode();}}
  }
  static RoundIdentity RoundGroupKey(StoredObservation o){return new RoundIdentity(o.Database,o.Session,o.Round);}
  /// <summary>按采集轮翻页:从最新(或最早)一端沿索引顺序读取,凑够 limit 轮即停,不做全范围 GROUP BY。</summary>
  public static RoundPage QueryRoundWindow(string root,HistoryFilter filter,RoundCursor cursor,int limit,bool descending,CancellationToken cancellationToken=default(CancellationToken)){
   if(filter==null)throw new ArgumentNullException("filter");
   if(limit<1||limit>10000)throw new ArgumentOutOfRangeException("limit");
   var page=new RoundPage();
   var files=Databases(root,filter.Source).ToArray();
   if(files.Length==0)return page;
   var cursors=new List<Cursor>();
   foreach(string file in files){
    long seedTime=0,seedId=0;int mode=0;
    if(cursor!=null&&!String.IsNullOrEmpty(cursor.Database)){
     if(String.Equals(file,cursor.Database,StringComparison.Ordinal)){seedTime=cursor.UtcTicks;seedId=cursor.Id;mode=1;}
     else{seedTime=cursor.UtcTicks;mode=2;}
    }
    cursors.Add(new Cursor(file,filter,!descending,seedTime,seedId,mode));
   }
   return Collect(cursors,limit,descending,cancellationToken);
  }
  static RoundPage Collect(List<Cursor> cursors,int limit,bool descending,CancellationToken cancellationToken){
   var page=new RoundPage();
   var groups=new List<List<StoredObservation>>();var seen=new Dictionary<RoundIdentity,int>();
   long lastTicks=0,lastId=0;string lastFile=null;DateTime minUtc=DateTime.MaxValue,maxUtc=DateTime.MinValue;
   while(true){
    cancellationToken.ThrowIfCancellationRequested();
    Cursor chosen=null;StoredObservation best=null;
    foreach(Cursor c in cursors){
     StoredObservation o=c.Peek();if(o==null)continue;
     bool before=best==null||(descending?o.Utc>best.Utc:o.Utc<best.Utc);
     if(!before&&best!=null&&o.Utc==best.Utc){int db=String.CompareOrdinal(o.Database,best.Database);before=descending?(db>0||(db==0&&o.Id>best.Id)):(db<0||(db==0&&o.Id<best.Id));}
     if(before){chosen=c;best=o;}
    }
    if(chosen==null)break;
    RoundIdentity key=RoundGroupKey(best);int gi;
    if(!seen.TryGetValue(key,out gi)){
     if(groups.Count>=limit){page.HasMore=true;break;}
     gi=groups.Count;groups.Add(new List<StoredObservation>());seen.Add(key,gi);
    }
    groups[gi].Add(best);
    if(best.Utc<minUtc)minUtc=best.Utc;
    if(best.Utc>maxUtc)maxUtc=best.Utc;
    lastTicks=best.Utc.Ticks;lastId=best.Id;lastFile=best.Database;
    chosen.Advance();
   }
   page.Rounds=groups;
   if(lastFile!=null)page.Next=new RoundCursor{UtcTicks=lastTicks,Id=lastId,Database=lastFile};
   if(minUtc<=maxUtc){page.FromUtc=minUtc;page.ToUtc=maxUtc;}
   return page;
  }
  /// <summary>按时间锚点定位一页采集轮:返回包含该时刻的整轮起算(锚点落在空档期时取其后的第一轮)。</summary>
  public static RoundPage QueryRoundWindowFrom(string root,HistoryFilter filter,DateTime anchorUtc,int limit,bool ascending,CancellationToken cancellationToken=default(CancellationToken)){
   if(filter==null)throw new ArgumentNullException("filter");
   if(limit<1||limit>10000)throw new ArgumentOutOfRangeException("limit");
   var files=Databases(root,filter.Source).ToArray();
   if(files.Length==0)return new RoundPage();
   long ticks=anchorUtc.ToUniversalTime().Ticks;
   // 升序锚点可能把一轮从中间切开。先用"只要 1 轮"的探测拿到这一轮真实的起始时刻,再用起始时刻
   // 取完整一页:探测那一步只读一轮,比先取一整页再重取便宜得多,结果也一定是一整轮。
   if(ascending){
    RoundPage probe=WindowFrom(files,filter,ticks,1,true,cancellationToken);
    if(probe.Rounds.Count>0&&probe.Rounds[0].Count>0){
     DateTime? start=null;
     foreach(StoredObservation row in probe.Rounds[0])if(row.RoundStartedUtc.HasValue&&(!start.HasValue||row.RoundStartedUtc.Value<start.Value))start=row.RoundStartedUtc.Value;
     if(start.HasValue&&start.Value.Ticks<ticks)ticks=start.Value.Ticks;
    }
   }
   return WindowFrom(files,filter,ticks,limit,ascending,cancellationToken);
  }
  static RoundPage WindowFrom(string[] files,HistoryFilter filter,long seedTicks,int limit,bool ascending,CancellationToken cancellationToken){
   var cursors=files.Select(file=>new Cursor(file,filter,ascending,seedTicks,0,3)).ToList();
   return Collect(cursors,limit,!ascending,cancellationToken);
  }
  /// <summary>范围内是否存在严格早于该时刻的采集轮。用一次最小窗口查询回答,不依赖点击意图。</summary>
  public static bool HasRoundBefore(string root,HistoryFilter f,DateTime utc){
   if(f==null)throw new ArgumentNullException("filter");
   var files=Databases(root,f.Source).ToArray();
   if(files.Length==0)return false;
   return WindowFrom(files,f,utc.ToUniversalTime().Ticks-1,1,false,CancellationToken.None).Rounds.Count>0;
  }
  /// <summary>范围内采集轮总数(精确值)。跨分期库的同一轮只算一次。用于摘要展示,应放在后台线程执行。</summary>
  public static long CountRounds(string root,HistoryFilter f){
   if(f==null)throw new ArgumentNullException("filter");
   var keys=new HashSet<RoundIdentity>();
   foreach(string file in Databases(root,f.Source)){
    using(var c=OpenRead(file)){
     var q=new SQLiteCommand(c);
     string sql="SELECT session,round FROM observations WHERE utc_ticks>=@f AND utc_ticks<@t";
     q.Parameters.AddWithValue("@f",f.FromUtc.ToUniversalTime().Ticks);
     q.Parameters.AddWithValue("@t",f.ToUtc.ToUniversalTime().Ticks);
     if(!string.IsNullOrEmpty(f.ExperimentId)){sql+=" AND experiment=@e";q.Parameters.AddWithValue("@e",f.ExperimentId);}
     q.CommandText=AppendFilters(q,f,sql)+" GROUP BY session,round";
     using(var r=q.ExecuteReader())while(r.Read())keys.Add(new RoundIdentity(file,Text(r,0),r.GetInt64(1)));
    }
   }
   return keys.Count;
  }
  public static long Stream(string root,HistoryFilter filter,Action<StoredObservation> visitor,bool ascending=true,CancellationToken cancellationToken=default(CancellationToken)){
   if(visitor==null)throw new ArgumentNullException("visitor");long count=0;Merge(root,filter,ascending,cancellationToken,o=>{visitor(o);count++;return true;},false);return count;
  }
  /// <summary>只读取趋势所需列的流式读取:避免把 tx/rx/payload 三个 hex 大列一并读出。</summary>
  public static long StreamLight(string root,HistoryFilter filter,Action<StoredObservation> visitor,bool ascending=true,CancellationToken cancellationToken=default(CancellationToken)){
   if(visitor==null)throw new ArgumentNullException("visitor");long count=0;Merge(root,filter,ascending,cancellationToken,o=>{visitor(o);count++;return true;},true);return count;
  }
  static void Merge(string root,HistoryFilter filter,bool ascending,CancellationToken cancellationToken,Func<StoredObservation,bool> visit,bool light){
   if(filter==null)throw new ArgumentNullException("filter");var f=filter.Copy();var cursors=Databases(root,f.Source).Select(file=>new Cursor(file,f,ascending,0,0,0,light)).ToList();
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





using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
namespace ExperimentMonitor {
 public sealed class StorageTestHarness:MarshalByRefObject {public int Execute(){return StorageTests.Run();}}
 public static class StorageTests {
  static void Check(bool ok,string name){if(!ok)throw new Exception("存储测试失败："+name);}
 public static int Run(){string root=Path.Combine(Path.GetTempPath(),"experiment-storage-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);int tests=0;try{DateTime utc=DateTime.UtcNow;var filter=new HistoryFilter{FromUtc=utc.AddDays(-90),ToUtc=utc.AddMinutes(1),Source="simulation"};using(var store=new ExperimentStore(root,"simulation","{config:test}")){store.Ready.GetAwaiter().GetResult();string id=store.BeginExperiment("test","=formula");for(int i=0;i<3;i++)store.Record(new Observation{Point="T0",Description="=unsafe",Device="PLC",Source="simulation",Status=i==2?"超时":"正常",Quality=i==2?"timeout":"good",Value=i==2?"等待超时":"-1.25",Number=i==2?(double?)null:-1.25,RawNumber=i==2?(double?)null:-1.25,Unit="℃",ConfigVersion="sha256-test",Utc=utc.AddDays(i==0?-60:0),Tx=new byte[]{1,3},Rx=new byte[0],Payload=new byte[0]},i,"℃");store.EndExperiment();store.FlushAsync().GetAwaiter().GetResult();Check(store.Committed==3,"flush提交全部队列");tests++;filter.ExperimentId=id;Check(ExperimentHistory.Count(root,filter)==3,"跨分期实验过滤");tests++;}Check(ExperimentHistory.Databases(root,"simulation").Count()==2,"30天跨库");tests++;Check(ExperimentHistory.Count(root,new HistoryFilter{FromUtc=filter.FromUtc,ToUtc=filter.ToUtc,Source="serial"})==0,"模拟实机隔离");tests++;var fullPage=ExperimentHistory.Query(root,filter);var page1=ExperimentHistory.QueryPage(root,filter,0,2);var page2=ExperimentHistory.QueryPage(root,filter,2,2);Check(page1.Count==2&&page2.Count==1&&page1.Concat(page2).Select(x=>x.Database+"|"+x.Id).SequenceEqual(fullPage.Select(x=>x.Database+"|"+x.Id)),"跨分期分页无重漏");tests++;var experiments=ExperimentHistory.ListExperiments(root,"simulation");Check(experiments.Count==1&&experiments[0].Name=="test"&&experiments[0].EndedUtc.HasValue,"跨分期实验目录及结束时间");tests++;var rows=ExperimentHistory.Query(root,filter);Check(rows.Count==3&&rows.Count(x=>x.Number==null)==1&&rows.Any(x=>x.Number==-1.25),"负数及失败空值保留");tests++;Check(rows.All(x=>x.ConfigVersion=="sha256-test")&&rows.Any(x=>x.Quality=="timeout"),"质量和配置版本");tests++;filter.Points=new[]{"missing"};Check(ExperimentHistory.Count(root,filter)==0,"测点过滤");tests++;filter.Points=new string[0];var xlsx=ExperimentExport.ExportAsync(root,filter,root,"xlsx",null,CancellationToken.None).GetAwaiter().GetResult();Check(xlsx.Rows==3&&xlsx.Files.Length==1,"Excel单工作簿完整导出");tests++;using(var z=ZipFile.OpenRead(Path.Combine(xlsx.OutputDirectory,xlsx.Files[0]))){var nsDoc=new XmlDocument();using(var stream=z.GetEntry("xl/workbook.xml").Open())nsDoc.Load(stream);var ns=new XmlNamespaceManager(nsDoc.NameTable);ns.AddNamespace("s","http://schemas.openxmlformats.org/spreadsheetml/2006/main");Check(nsDoc.SelectNodes("//s:sheet",ns).Count==5,"五个语义工作表（含统计摘要）");tests++;var wide=new XmlDocument();using(var stream=z.GetEntry("xl/worksheets/sheet1.xml").Open())wide.Load(stream);Check(wide.SelectNodes("//s:sheetData/s:row",ns).Count==4&&wide.OuterXml.Contains("-1.25"),"宽表逐轮且负数为数值");tests++;var detail=new XmlDocument();using(var stream=z.GetEntry("xl/worksheets/sheet2.xml").Open())detail.Load(stream);Check(detail.SelectNodes("//s:sheetData/s:row",ns).Count==4&&detail.OuterXml.Contains("timeout"),"明细包含全部记录质量");tests++;var quality=new XmlDocument();using(var stream=z.GetEntry("xl/worksheets/sheet3.xml").Open())quality.Load(stream);Check(quality.SelectNodes("//s:sheetData/s:row",ns).Count==4&&quality.OuterXml.Contains("timeout")&&quality.OuterXml.Contains("超时"),"质量表包含枚举值和状态说明");tests++;var info=new XmlDocument();using(var stream=z.GetEntry("xl/worksheets/sheet4.xml").Open())info.Load(stream);Check(info.SelectNodes("//s:sheetData/s:row",ns).Count>8&&info.OuterXml.Contains("记录条数"),"说明表包含可审阅的筛选和导出元数据");tests++;}var csv=ExperimentExport.ExportAsync(root,filter,root,"csv",null,CancellationToken.None).GetAwaiter().GetResult();string wideCsv=File.ReadAllText(Path.Combine(csv.OutputDirectory,csv.Files.First(f=>f.Contains("宽表"))));string detailCsv=File.ReadAllText(Path.Combine(csv.OutputDirectory,csv.Files.First(f=>f.Contains("明细"))));string qualityCsv=File.ReadAllText(Path.Combine(csv.OutputDirectory,csv.Files.First(f=>f.Contains("质量"))));Check(csv.Rows==3&&csv.Files.Count(f=>f.EndsWith(".csv"))==3,"CSV生成宽表、明细和质量三套文件");tests++;Check(wideCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==4&&wideCsv.Contains("-1.25"),"CSV宽表输出全部真实轮次和值");tests++;Check(detailCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==4&&!detailCsv.Contains("=unsafe")&&detailCsv.Contains(PointCatalog.Get("T0").Label),"CSV明细用当前名称映射且旧描述不外泄");tests++;Check(qualityCsv.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==4&&qualityCsv.Contains("timeout"),"CSV质量文件真实记录");tests++;var next=ExperimentExport.ExportAsync(root,filter,root,"xlsx",null,CancellationToken.None).GetAwaiter().GetResult();Check(next.OutputDirectory!=xlsx.OutputDirectory&&File.Exists(Path.Combine(xlsx.OutputDirectory,xlsx.Files[0])),"不覆盖先前导出");tests++;ExperimentExport.RowsPerVolume=2;try{var split=ExperimentExport.ExportAsync(root,filter,root,"xlsx",null,CancellationToken.None).GetAwaiter().GetResult();Check(split.Files.Length==2&&split.Rows==3,"分卷不拆轮次（文件数="+split.Files.Length+"，记录="+split.Rows+"）");tests++;}finally{ExperimentExport.RowsPerVolume=100000;}var cancel=new CancellationTokenSource();cancel.Cancel();try{ExperimentExport.ExportAsync(root,filter,root,"xlsx",null,cancel.Token).GetAwaiter().GetResult();throw new Exception("应取消");}catch(OperationCanceledException){tests++;}Check(Directory.GetDirectories(root,".experiment-export-*",SearchOption.AllDirectories).Length==0,"取消无半成品发布");tests++;using(var budgetStore=new ExperimentStore(Path.Combine(root,"budget"),"simulation","")){budgetStore.Ready.GetAwaiter().GetResult();try{budgetStore.Record(new Observation{Source="simulation",Value=new string('x',9*1024*1024),Utc=utc},1,"");throw new Exception("预算应拒绝");}catch(IOException){Check(budgetStore.LastError.Length>0&&!budgetStore.Status.Recording,"16MiB预算超限显式停止");tests++;}}// 回归:大范围分组查询不得依赖系统临时目录(TEMP 对进程不可用时 QueryRoundPage 曾抛 SQLITE_CANTOPEN)
string tempDbDir=Path.Combine(root,"tempblocked"),bigDb=Path.Combine(tempDbDir,"simulation","simulation_cycle_20261005T000000000_30d_000001_001.db");Directory.CreateDirectory(Path.GetDirectoryName(bigDb));
using(var bc=new SQLiteConnection("Data Source="+bigDb+";Version=3;")){bc.Open();using(var t=bc.BeginTransaction()){using(var q=new SQLiteCommand("CREATE TABLE observations(id INTEGER PRIMARY KEY AUTOINCREMENT,utc_ticks INTEGER NOT NULL,source TEXT,session TEXT,experiment TEXT,round INTEGER,point TEXT,description TEXT,device TEXT,slave INTEGER,address INTEGER,count INTEGER,unit TEXT,mode TEXT,status TEXT,value TEXT,number REAL,elapsed_ms INTEGER,tx TEXT,rx TEXT,payload TEXT,raw_number REAL,quality TEXT,config_version TEXT,round_started_utc_ticks INTEGER);CREATE INDEX ix_obs_time ON observations(utc_ticks,id);CREATE INDEX ix_obs_point_time ON observations(point,utc_ticks);",bc,t))q.ExecuteNonQuery();DateTime baseUtc=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);using(var ins=new SQLiteCommand("INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@u,'simulation',@se,'',@r,'T0','水箱','PLC',1,125,2,'℃','FLOAT ABCD','正常','1',1.0,3,'','','',1.0,'good','test',@rs)",bc,t)){for(int round=1;round<=400;round++)for(int i=0;i<150;i++){ins.Parameters.Clear();long ru=baseUtc.AddMinutes(round).Ticks;ins.Parameters.AddWithValue("@u",ru+i);ins.Parameters.AddWithValue("@se",round%2==0?"s-even":"s-odd");ins.Parameters.AddWithValue("@r",round);ins.Parameters.AddWithValue("@rs",ru);ins.ExecuteNonQuery();}}t.Commit();}}
string oldTemp=Environment.GetEnvironmentVariable("TEMP"),oldTmp=Environment.GetEnvironmentVariable("TMP"),blockedTemp=Path.Combine(root,"blocked-temp-file");File.WriteAllText(blockedTemp,"x");
Environment.SetEnvironmentVariable("TEMP",blockedTemp);Environment.SetEnvironmentVariable("TMP",blockedTemp);
try{long roundTotal;List<List<StoredObservation>> bigRounds=ExperimentHistory.QueryRoundPage(tempDbDir,new HistoryFilter{FromUtc=new DateTime(2026,10,4,0,0,0,DateTimeKind.Utc),ToUtc=new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc),Source="simulation"},0,5,out roundTotal,false);Check(roundTotal==400&&bigRounds.Count==5&&bigRounds.All(g=>g.Count==150),"大范围分组查询不依赖系统临时目录(60000行/400轮)");tests++;}catch(SQLiteException e){Check(false,"大范围分组查询不依赖系统临时目录(实际: "+e.Message+")");}finally{Environment.SetEnvironmentVariable("TEMP",oldTemp);Environment.SetEnvironmentVariable("TMP",oldTmp);}
using(SQLiteConnection probe=ExperimentHistory.OpenRead(bigDb)){using(var q=new SQLiteCommand("PRAGMA temp_store",probe))Check(Convert.ToInt32(q.ExecuteScalar())==2,"历史只读连接分组临时存储在内存");tests++;}
 // 记录页新取数路径:键集翻页首屏不扫全范围,翻遍全库的轮次键与旧分组实现完全一致。
 string pagingRoot=Path.Combine(Path.GetTempPath(),"experiment-paging-tests-"+Guid.NewGuid().ToString("N"));
 Directory.CreateDirectory(Path.Combine(pagingRoot,"simulation"));
 string pagingDb=Path.Combine(pagingRoot,"simulation","simulation_cycle_20261005T000000000_30d_000001_001.db");
 using(var bc=new SQLiteConnection("Data Source="+pagingDb+";Version=3;")){bc.Open();using(var t=bc.BeginTransaction()){using(var q=new SQLiteCommand("CREATE TABLE observations(id INTEGER PRIMARY KEY AUTOINCREMENT,utc_ticks INTEGER NOT NULL,source TEXT,session TEXT,experiment TEXT,round INTEGER,point TEXT,description TEXT,device TEXT,slave INTEGER,address INTEGER,count INTEGER,unit TEXT,mode TEXT,status TEXT,value TEXT,number REAL,elapsed_ms INTEGER,tx TEXT,rx TEXT,payload TEXT,raw_number REAL,quality TEXT,config_version TEXT,round_started_utc_ticks INTEGER);CREATE INDEX ix_obs_time ON observations(utc_ticks,id);CREATE INDEX ix_obs_point_time ON observations(point,utc_ticks);",bc,t))q.ExecuteNonQuery();DateTime baseUtc=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);using(var ins=new SQLiteCommand("INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@u,'simulation',@se,'',@r,@p,'水箱','PLC',1,125,2,'℃','FLOAT ABCD','正常','1',1.0,3,'','','',1.0,'good','test',@rs)",bc,t)){for(int round=1;round<=400;round++)for(int i=0;i<3;i++){ins.Parameters.Clear();long ru=baseUtc.AddMinutes(round).Ticks;ins.Parameters.AddWithValue("@u",ru+i);ins.Parameters.AddWithValue("@se",round%2==0?"s-even":"s-odd");ins.Parameters.AddWithValue("@r",round);ins.Parameters.AddWithValue("@p","T"+i);ins.Parameters.AddWithValue("@rs",ru);ins.ExecuteNonQuery();}}t.Commit();}}
 var winFilter=new HistoryFilter{FromUtc=new DateTime(2026,10,4,0,0,0,DateTimeKind.Utc),ToUtc=new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc),Source="simulation"};
 var windowWatch=System.Diagnostics.Stopwatch.StartNew();
 ExperimentHistory.RoundPage firstPage=ExperimentHistory.QueryRoundWindow(pagingRoot,winFilter,null,5,false,CancellationToken.None);
 windowWatch.Stop();
 Check(firstPage.Rounds.Count==5&&firstPage.HasMore&&firstPage.Rounds.All(g=>g.Count==3),"键集翻页首屏返回完整采集轮");tests++;
 Check(windowWatch.ElapsedMilliseconds<3000,"键集翻页首屏不做全范围分组("+windowWatch.ElapsedMilliseconds+" ms)");tests++;
 var walked=new List<string>();var seenKeys=new HashSet<string>(StringComparer.Ordinal);bool duplicateKey=false;
 ExperimentHistory.RoundCursor cursor=null;
 while(true){ExperimentHistory.RoundPage page=ExperimentHistory.QueryRoundWindow(pagingRoot,winFilter,cursor,7,false,CancellationToken.None);foreach(List<StoredObservation> group in page.Rounds){string key=group[0].Session+"|"+group[0].Round;if(!seenKeys.Add(key))duplicateKey=true;walked.Add(key);}if(!page.HasMore||page.Next==null)break;cursor=page.Next;}
 Check(!duplicateKey&&walked.Count==400,"键集翻页遍历全部采集轮且无重复");tests++;
 var oldKeys=new List<string>();
 for(int offset=0;offset<400;offset+=50){long chunkTotal;List<List<StoredObservation>> chunk=ExperimentHistory.QueryRoundPage(pagingRoot,winFilter,offset,50,out chunkTotal,false);foreach(List<StoredObservation> group in chunk)oldKeys.Add(group[0].Session+"|"+group[0].Round);}
 Check(oldKeys.Count==400&&walked.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(oldKeys.OrderBy(x=>x,StringComparer.Ordinal)),"键集翻页与旧分组实现轮次键完全一致");tests++;
 Check(ExperimentHistory.CountRounds(pagingRoot,winFilter)==400,"范围采集轮总数精确");tests++;
 // 锚点定位:落在一轮采样的中途时必须返回完整的一整轮(锚点之前的记录不能丢),且首条记录早于锚点。
 DateTime baseAnchor=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);
 DateTime midRound=baseAnchor.AddMinutes(100).AddTicks(1);
 ExperimentHistory.RoundPage midPage=ExperimentHistory.QueryRoundWindowFrom(pagingRoot,winFilter,midRound,5,true,CancellationToken.None);
 Check(midPage.Rounds.Count==5&&midPage.Rounds[0].Count==3&&midPage.Rounds[0][0].Utc.Ticks<midRound.Ticks,"锚点落在轮次中间时仍返回完整的一整轮");tests++;
 // 锚点落在无数据空档期时,落到该时刻之后最近的真实轮次,不能空白。
 DateTime gapAnchor=baseAnchor.AddMinutes(100).AddSeconds(30);
 ExperimentHistory.RoundPage gapPage=ExperimentHistory.QueryRoundWindowFrom(pagingRoot,winFilter,gapAnchor,5,true,CancellationToken.None);
 Check(gapPage.Rounds.Count==5&&gapPage.Rounds[0].Count==3&&gapPage.Rounds[0][0].Round==101&&gapPage.Rounds[0][0].Utc>=gapAnchor,"锚点落在空档期时落到该时刻之后最近的真实轮次");tests++;
 // 锚点=范围开始时刻:与旧分组实现的升序首页轮次键完全一致。
 ExperimentHistory.RoundPage anchorPage=ExperimentHistory.QueryRoundWindowFrom(pagingRoot,winFilter,winFilter.FromUtc,7,true,CancellationToken.None);
 long anchorOldTotal;List<List<StoredObservation>> anchorOld=ExperimentHistory.QueryRoundPage(pagingRoot,winFilter,0,7,out anchorOldTotal,true);
 Check(anchorPage.Rounds.Count==7&&anchorPage.Rounds.Select(g=>g[0].Session+"|"+g[0].Round).SequenceEqual(anchorOld.Select(g=>g[0].Session+"|"+g[0].Round)),"锚点定位与旧分组实现的升序首页轮次键一致");tests++;
 Check(ExperimentHistory.QueryRoundWindowFrom(pagingRoot,winFilter,winFilter.FromUtc,500,true,CancellationToken.None).Rounds.Count==400,"锚点定位按指定每页轮数返回完整轮次");tests++;
 // "还能不能更早"由一次最小窗口查询回答,不依赖点击意图。
 Check(!ExperimentHistory.HasRoundBefore(pagingRoot,winFilter,baseAnchor.AddMinutes(1)),"范围最早端之前没有更早的采集轮");tests++;
 Check(ExperimentHistory.HasRoundBefore(pagingRoot,winFilter,baseAnchor.AddMinutes(2)),"范围中间存在更早的采集轮");tests++;
 // 跨分期库的一轮只算一轮:同一 session+round 的记录分片落在两个库文件里。
 string splitRoot=Path.Combine(Path.GetTempPath(),"experiment-split-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(splitRoot,"serial"));
 string splitA=Path.Combine(splitRoot,"serial-split-a.db"),splitB=Path.Combine(splitRoot,"serial","split-b.db");
 string createObservations="CREATE TABLE observations(id INTEGER PRIMARY KEY AUTOINCREMENT,utc_ticks INTEGER NOT NULL,source TEXT,session TEXT,experiment TEXT,round INTEGER,point TEXT,description TEXT,device TEXT,slave INTEGER,address INTEGER,count INTEGER,unit TEXT,mode TEXT,status TEXT,value TEXT,number REAL,elapsed_ms INTEGER,tx TEXT,rx TEXT,payload TEXT,raw_number REAL,quality TEXT,config_version TEXT,round_started_utc_ticks INTEGER);CREATE INDEX ix_obs_time ON observations(utc_ticks,id);";
 foreach(string splitFile in new[]{splitA,splitB})using(var c=new SQLiteConnection("Data Source="+splitFile+";Version=3;")){c.Open();using(var q=new SQLiteCommand(createObservations,c))q.ExecuteNonQuery();}
 DateTime splitBase=new DateTime(2026,10,9,0,0,0,DateTimeKind.Utc);
 Action<string,int,string,long> insertSplit=(splitFile,round,point,offsetTicks)=>{
  using(var c=new SQLiteConnection("Data Source="+splitFile+";Version=3;")){c.Open();using(var q=new SQLiteCommand("INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@u,'serial','split-session','',@r,@p,'测点','DS666',1,125,2,'','FLOAT ABCD','正常','1',1.0,3,'','','',1.0,'good','test',@rs)",c)){q.Parameters.AddWithValue("@u",splitBase.AddTicks(offsetTicks).Ticks);q.Parameters.AddWithValue("@r",round);q.Parameters.AddWithValue("@p",point);q.Parameters.AddWithValue("@rs",splitBase.Ticks);q.ExecuteNonQuery();}}
 };
 for(int i=0;i<5;i++)insertSplit(splitA,1,"T"+i,i);
 for(int i=5;i<10;i++)insertSplit(splitB,1,"T"+i,i);
 for(int i=0;i<3;i++)insertSplit(splitB,2,"T"+i,1000+i*10);
 var splitFilter=new HistoryFilter{FromUtc=splitBase.AddMinutes(-1),ToUtc=splitBase.AddMinutes(10),Source="serial"};
 Check(ExperimentHistory.CountRounds(splitRoot,splitFilter)==2,"跨分期库的同一采集轮只算一轮");tests++;
 ExperimentHistory.RoundPage splitPage=ExperimentHistory.QueryRoundWindow(splitRoot,splitFilter,null,10,false,CancellationToken.None);
 Check(splitPage.Rounds.Count==2&&splitPage.Rounds.Sum(g=>g.Count)==13&&splitPage.Rounds.Any(g=>g.Count==10)&&splitPage.Rounds.Any(g=>g.Count==3),"跨分期库的采集轮被完整合并取回");tests++;
 long splitTotal;List<List<StoredObservation>> splitOld=ExperimentHistory.QueryRoundPage(splitRoot,splitFilter,0,10,out splitTotal,false);
 Check(splitTotal==2&&splitOld.Count==2&&splitOld.Sum(g=>g.Count)==13,"旧分组实现对跨库轮次结论一致");tests++;
 // 实验段与测点统计:会话分段、缺口、超时轮、越限计数。
 string statsRoot=Path.Combine(Path.GetTempPath(),"experiment-stats-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(statsRoot,"serial"));
 string statsDb=Path.Combine(statsRoot,"serial-stats.db");
 using(var c=new SQLiteConnection("Data Source="+statsDb+";Version=3;")){c.Open();using(var t=c.BeginTransaction()){
  using(var q=new SQLiteCommand(createObservations,c))q.ExecuteNonQuery();
  DateTime statsBase=new DateTime(2026,11,1,0,0,0,DateTimeKind.Utc);
  using(var ins=new SQLiteCommand("INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@u,'serial',@se,'',@r,@p,'测点','DS666',1,125,2,'℃','FLOAT ABCD','正常','1',@n,3,'','','',@n,'good','test',@rs)",c)){
   for(int round=1;round<=10;round++){if(round==6)continue;long ru=statsBase.AddMinutes(round).Ticks;for(int i=0;i<3;i++){ins.Parameters.Clear();ins.Parameters.AddWithValue("@u",ru+i);ins.Parameters.AddWithValue("@se","sessA");ins.Parameters.AddWithValue("@r",round);ins.Parameters.AddWithValue("@p","T"+i);ins.Parameters.AddWithValue("@n",(double)round*(i+1));ins.Parameters.AddWithValue("@rs",ru);ins.ExecuteNonQuery();}}
   using(var timeout=new SQLiteCommand("INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@u,'serial','sessB','',@r,'T0','测点','DS666',1,125,2,'℃','FLOAT ABCD','超时','timeout',NULL,3,'','','',NULL,'timeout','test',@rs)",c)){
    for(int round=20;round<=21;round++){long ru=statsBase.AddMinutes(round).Ticks;timeout.Parameters.Clear();timeout.Parameters.AddWithValue("@u",ru);timeout.Parameters.AddWithValue("@r",round);timeout.Parameters.AddWithValue("@rs",ru);timeout.ExecuteNonQuery();}}
   using(var legacy=new SQLiteCommand("INSERT INTO observations(utc_ticks,source,session,experiment,round,point,description,device,slave,address,count,unit,mode,status,value,number,elapsed_ms,tx,rx,payload,raw_number,quality,config_version,round_started_utc_ticks) VALUES(@u,'serial',NULL,'',30,'T0','测点','DS666',1,125,2,'℃','FLOAT ABCD','正常','5',5.0,3,'','','',5.0,'good','test',@rs)",c)){
    long ru=statsBase.AddMinutes(30).Ticks;legacy.Parameters.AddWithValue("@u",ru);legacy.Parameters.AddWithValue("@rs",ru);legacy.ExecuteNonQuery();}
  }t.Commit();}}
 var statsFilter=new HistoryFilter{FromUtc=new DateTime(2026,10,31,0,0,0,DateTimeKind.Utc),ToUtc=new DateTime(2026,11,2,0,0,0,DateTimeKind.Utc),Source="serial"};
 List<SessionSummary> sessions=HistoryStats.Sessions(statsRoot,statsFilter,null,CancellationToken.None);
 Check(sessions.Count==3,"实验段按会话分成三段(含无会话旧数据)");tests++;
 SessionSummary sessA=sessions.Single(x=>x.Session=="sessA");
 Check(sessA.Rounds==9&&sessA.Records==27&&sessA.GoodRounds==9&&sessA.TimeoutRounds==0,"实验段的轮数与记录数正确");tests++;
 Check(sessA.ExpectedRounds==10&&sessA.MissingRounds==1&&sessA.MaxGapTicks==TimeSpan.FromMinutes(2).Ticks,"实验段识别出缺 1 轮与最大间隔 2 分钟");tests++;
 SessionSummary sessB=sessions.Single(x=>x.Session=="sessB");
 Check(sessB.TimeoutRounds==2&&sessB.GoodRounds==0,"整轮超时被计为超时轮");tests++;
 Check(sessions.Any(x=>x.Legacy&&x.Session==""),"无会话旧数据单独成段");tests++;
 var statsLimits=new Dictionary<string,Limit>(StringComparer.Ordinal){ {"T0",new Limit{Hi=5}} };
 List<SessionSummary> limitedSessions=HistoryStats.Sessions(statsRoot,statsFilter,statsLimits,CancellationToken.None);
 Check(limitedSessions.Single(x=>x.Session=="sessA").OverLimitRounds==4,"实验段的越限轮数按阈值统计");tests++;
 List<PointSummary> points=HistoryStats.Points(statsRoot,statsFilter,statsLimits,CancellationToken.None);
 PointSummary t0=points.Single(x=>x.Point=="T0");
 Check(t0.Good==10&&t0.Timeout==2&&t0.OverLimit==4,"测点统计的有效点与越限点正确");tests++;
 Check(t0.Min==1&&t0.Max==10&&Math.Abs(t0.Mean.Value-5.4)<1e-9,"测点统计的最值与均值正确");tests++;
 Check(points.Single(x=>x.Point=="T1").OverLimit==-1,"未配置阈值的测点不做越限判定");tests++;
 string limitRoot=Path.Combine(Path.GetTempPath(),"experiment-limits-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(limitRoot);
 LimitStore.Save(limitRoot,new Dictionary<string,Limit>(StringComparer.Ordinal){ {"T1",new Limit{Lo=5,Hi=40}} });
 var reloaded=LimitStore.Load(limitRoot);
 Check(reloaded.Count==1&&reloaded["T1"].Lo==5&&reloaded["T1"].Hi==40,"阈值表落盘后可重新读回");tests++;
 bool rejected=false;try{LimitStore.Save(limitRoot,new Dictionary<string,Limit>(StringComparer.Ordinal){ {"T1",new Limit{Lo=40,Hi=5}} });}catch(ArgumentException){rejected=true;}
 Check(rejected&&LimitStore.Load(limitRoot)["T1"].Lo==5,"下限大于等于上限的阈值被拒绝且保留原文件");tests++;
 for(int attempt=0;attempt<20;attempt++){try{if(Directory.Exists(pagingRoot))Directory.Delete(pagingRoot,true);if(Directory.Exists(splitRoot))Directory.Delete(splitRoot,true);if(Directory.Exists(statsRoot))Directory.Delete(statsRoot,true);if(Directory.Exists(limitRoot))Directory.Delete(limitRoot,true);}catch{}if(!Directory.Exists(pagingRoot)&&!Directory.Exists(splitRoot)&&!Directory.Exists(statsRoot)&&!Directory.Exists(limitRoot))break;System.Threading.Thread.Sleep(50);}
 return tests;}finally{ExperimentExport.RowsPerVolume=100000;Directory.Delete(root,true);}}
 }
}



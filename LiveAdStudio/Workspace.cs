using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveAdDesk;
namespace LiveAdStudio;
sealed class Workspace
{
    public int Version {get;set;}=3;
    public string Selected {get;set;}="";
    public List<Room> Rooms {get;set;}=[];
    public string Model {get;set;}="";
    public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveAdStudio");
    public static string DataPath=>Path.Combine(Root,"workspace.json");
    [JsonIgnore] public Room Current=>Rooms.First(x=>x.Finance.Id==Selected);
    public static Workspace Load(string? path=null)
    {
        path??=DataPath;
        if(!File.Exists(path)){var w=new Workspace();var room=new Room();w.Rooms.Add(room);w.Selected=room.Finance.Id;return w;}
        if(new FileInfo(path).Length>50*1024*1024)throw new InvalidDataException("工作区文件大于50MB，请缩小备份范围。");
        var data=JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path))??throw new InvalidDataException("工作区文件为空");
        if(data.Version==2&&Path.GetFullPath(path)==Path.GetFullPath(DataPath))File.Copy(path,Path.Combine(Root,"before-v21-"+DateTime.Now.ToString("yyyyMMddHHmmssfff")+".json"));
        data.Upgrade();
        data.Validate();
        if(!data.Rooms.Any(r=>r.Finance.Id==data.Selected))data.Selected=data.Rooms[0].Finance.Id;return data;
    }
    public void Validate()
    {
        if(Version!=3||Model==null||Rooms==null||Rooms.Count==0||Rooms.Count>1000||Rooms.Any(r=>r==null||r.Finance==null||r.Messages==null||r.Calls==null||r.Events==null))throw new InvalidDataException("工作区格式不完整，请恢复正确备份。");
        if(Rooms.Select(r=>r.Finance.Id).Distinct().Count()!=Rooms.Count)throw new InvalidDataException("备份有重复场次标识。");
        foreach(var r in Rooms)
        {
            var s=r.Finance;
            if(string.IsNullOrWhiteSpace(s.Id)||s.Name==null||s.Product==null||s.Account==null||s.Notes==null||s.Snapshots==null||s.Orders==null||s.Changes==null||s.Changes.Any(c=>c==null||c.Text==null)||r.Messages.Any(m=>m==null||m.Images==null||m.Text==null||m.Role is not ("user" or "assistant" or "notice"))||r.Calls.Any(c=>c==null)||r.Events.Any(e=>e==null))throw new InvalidDataException("备份中的记录不完整，未替换工作区。");
            Rules.ValidateSettings(r);foreach(var q in s.Snapshots){if(q==null)throw new InvalidDataException("快照为空。");Rules.ValidateSnapshot(q,false);}
            Decision.Validate(r);
            foreach(var o in s.Orders)if(o==null||string.IsNullOrEmpty(o.Id)||o.Paid<0||o.Refund<0||o.Refund>o.Paid||o.Channel==null||o.Status==null||o.Product==null||o.Buyer==null||o.Reason==null||o.Account==null)throw new InvalidDataException("备份中的订单无效。");
            if(s.Orders.Select(o=>o.Id).Distinct().Count()!=s.Orders.Count)throw new InvalidDataException("备份中存在重复订单号。");
            foreach(var m in r.Messages){if(m.Images.Count>2||m.Text.Length>200000)throw new InvalidDataException("对话记录超出限制。");foreach(var image in m.Images)ImageStore.PathOf(image);}
        }
        if(!Rooms.Any(r=>r.Finance.Id==Selected))Selected=Rooms[0].Finance.Id;
    }
    public void Upgrade(){if(Version==2)Version=3;}
    public void Save(string? path=null)
    {
        path??=DataPath;Directory.CreateDirectory(Path.GetDirectoryName(path)!);var tmp=path+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
        if(File.Exists(path))File.Replace(tmp,path,path+".bak",true);else File.Move(tmp,path);
    }
}
sealed class Room
{
    public Session Finance {get;set;}=new(){Name="今晚的直播"};
    public BusinessGoal Business {get;set;}=new();
    public DeliveryFacts Facts {get;set;}=new();
    public List<MaterialBatch> MaterialBatches {get;set;}=[];
    public List<MaterialReview> MaterialReviews {get;set;}=[];
    public List<FollowUp> FollowUps {get;set;}=[];
    public DateTime? OrdersImportedAt {get;set;}
    public List<ChatItem> Messages {get;set;}=[];
    public List<CallLog> Calls {get;set;}=[];
    public int CaptureSeconds {get;set;}=60;
    public int AnalysisMinutes {get;set;}=3;
    public int RequestLimit {get;set;}=20;
    public List<string> Events {get;set;}=[];
    public string ExportText()
    {
        var b=new StringBuilder(DecisionContext.Build(this));b.AppendLine("\n【AI请求用量】");
        b.AppendLine($"尝试{Calls.Count}次，完成{Calls.Count(c=>c.Status=="completed")}次；有usage记录{Calls.Count(c=>c.InputTokens.HasValue)}次，输入{Calls.Sum(c=>c.InputTokens??0)}，输出{Calls.Sum(c=>c.OutputTokens??0)}Token。未报告的请求用量未知；不代表套餐总余额。");
        foreach(var item in Messages.TakeLast(16))b.AppendLine($"\n{item.At:MM-dd HH:mm} {item.Role}：{item.Text}"+(item.Images.Count>0?$" [含{item.Images.Count}张本地图片，未随文字复制]":""));
        b.AppendLine("\n【盯盘记录】");foreach(var e in Events.TakeLast(15))b.AppendLine(e);
        return b.ToString();
    }
}
sealed class ChatItem
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Role {get;set;}="user";
    public string Text {get;set;}="";
    public DateTime At {get;set;}=DateTime.Now;
    public List<string> Images {get;set;}=[];
}
sealed class CallLog
{
    public DateTime At {get;set;}=DateTime.Now;
    public string Model {get;set;}="";
    public string Kind {get;set;}="手动";
    public string Status {get;set;}="pending";
    public long? InputTokens {get;set;}
    public long? OutputTokens {get;set;}
    public string Note {get;set;}="";
}
static class Rules
{
    public static void ValidateSettings(Room r)
    {
        var s=r.Finance;
        if(s.Name.Trim().Length==0||s.Name.Length>100)throw new InvalidDataException("场次名称须为1—100字。");
        if(s.Goal is not ("成交金额" or "成单人数"))throw new InvalidDataException("请选择有效投放目标。");
        foreach(var value in new[]{s.Price,s.FeeRate,s.DirectCost,s.Retention,s.DesiredProfit,s.Budget,s.Target,s.LossLimit})if(value<0)throw new InvalidDataException("金额、费率和目标不可为负。");
        if(s.FeeRate>100||s.Retention>100)throw new InvalidDataException("费率与保留率须为0—100%。");
        if(s.Start.HasValue!=s.End.HasValue || (s.Start.HasValue&&s.End<=s.Start))throw new InvalidDataException("统计起止时间需同时填写，结束晚于开始。");
        if(r.CaptureSeconds<15||r.CaptureSeconds>3600||r.AnalysisMinutes<1||r.AnalysisMinutes>60||r.RequestLimit<1||r.RequestLimit>500)throw new InvalidDataException("采集间隔15—3600秒、分析间隔1—60分钟、每场请求上限1—500。");
    }
    public static void ValidateSnapshot(Snapshot q,bool checkClock=true,Session? scope=null)
    {
        if(checkClock&&q.At>DateTime.Now.AddMinutes(5))throw new InvalidDataException("快照不能记录未来数据；本机时钟容差5分钟。");
        if(scope!=null&&(q.At<(scope.Start??scope.Day.Date)||q.At>(scope.End??scope.Day.Date.AddDays(1))))throw new InvalidDataException("快照时间不在本场统计范围内，请核对日期；跨零点直播先设置完整范围。");
        if(q.Spend<0||q.Gmv<0||q.Buyers<0||q.Orders<0||q.Refund<0||q.Visits<0||q.Clicks<0||q.Appointments<0)throw new InvalidDataException("快照数值不可为负。");
        if(q.Refund>q.Gmv)throw new InvalidDataException("退款超过GMV，请核对是否为同批订单。");
    }
}

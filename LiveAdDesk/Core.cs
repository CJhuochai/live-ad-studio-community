using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LiveAdDesk;
public class AppData { public int Version {get;set;}=1; public List<Session> Sessions {get;set;}=[]; }
public class Session
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Name {get;set;}="新场次";
    public string Account {get;set;}="";
    public DateTime Day {get;set;}=DateTime.Today;
    public DateTime? Start {get;set;}
    public DateTime? End {get;set;}
    public string Goal {get;set;}="成交金额";
    public string Product {get;set;}="";
    public decimal? Price {get;set;}
    public decimal? FeeRate {get;set;}
    public decimal? DirectCost {get;set;}
    public decimal? Retention {get;set;}
    public decimal DesiredProfit {get;set;}
    public decimal? Budget {get;set;}
    public decimal? Target {get;set;}
    public decimal? LossLimit {get;set;}
    public int SampleMin {get;set;}=10;
    public string Notes {get;set;}="";
    public List<Snapshot> Snapshots {get;set;}=[];
    public List<Order> Orders {get;set;}=[];
    public List<Change> Changes {get;set;}=[];
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<Snapshot> UsableSnapshots=>Snapshots.Where(x=>x.At<=DateTime.Now.AddMinutes(5)&&x.At>=(Start??Day.Date)&&x.At<=(End??Day.Date.AddDays(1)));
    public IncrementReport? Report {get;set;}
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<Order> LiveOrders => Orders.Where(x=>x.Channel=="直播" && x.OrderTime>=(Start??Day.Date) && x.OrderTime<(End??Day.Date.AddDays(1)));
    public void Merge(IEnumerable<Order> incoming)
    {
        var map=Orders.ToDictionary(x=>x.Id);
        foreach(var o in incoming) map[o.Id]=o;
        Orders=map.Values.OrderBy(x=>x.OrderTime).ToList();
    }
    public decimal? BreakEvenCpa() => Price is >0 && FeeRate.HasValue && DirectCost.HasValue && Retention.HasValue
        ? (Price*(1-FeeRate/100)-DirectCost)*Retention/100-DesiredProfit : null;
    public override string ToString()=>$"{Day:MM-dd}  {Name}";
}
public class Snapshot
{
    public DateTime At {get;set;}=DateTime.Now;
    public decimal Spend {get;set;}
    public decimal Gmv {get;set;}
    public int Buyers {get;set;}
    public int? Orders {get;set;}
    public decimal? Refund {get;set;}
    public int? Visits {get;set;}
    public int? Clicks {get;set;}
    public int? Appointments {get;set;}
    public string Source {get;set;}="手动";
    public string Note {get;set;}="";
}
public class Change { public DateTime At {get;set;}=DateTime.Now; public string Text {get;set;}=""; }
public class Order
{
    public string Id {get;set;}="";
    public string Buyer {get;set;}="";
    public string Product {get;set;}="";
    public string Channel {get;set;}="";
    public string Account {get;set;}="";
    public string Status {get;set;}="";
    public DateTime OrderTime {get;set;}
    public DateTime? PaidTime {get;set;}
    public decimal Paid {get;set;}
    public decimal Refund {get;set;}
    public int Quantity {get;set;}=1;
    public decimal? ExpectedIncome {get;set;}
    public string Reason {get;set;}="";
    public bool Redeemed {get;set;}
}
public class IncrementReport
{
    public string Account {get;set;}="";
    public DateTime Day {get;set;}
    public decimal Gmv {get;set;}
    public decimal Natural {get;set;}
    public decimal Increment {get;set;}
}
public record Metrics(int Count,int Buyers,int KeptBuyers,int RefundCount,int Redeemed,decimal Gross,decimal Refund,decimal Retained)
{
    public static Metrics From(Session s)
    {
        var a=s.LiveOrders.Where(o=>o.PaidTime.HasValue && !o.Status.Contains("未支付")).ToArray();
        string Key(Order o)=>string.IsNullOrWhiteSpace(o.Buyer)?o.Id:o.Buyer;
        return new(a.Length,a.Select(Key).Distinct().Count(),a.Where(o=>o.Paid>o.Refund).Select(Key).Distinct().Count(),
            a.Count(o=>o.Refund>0),a.Count(o=>o.Redeemed),a.Sum(o=>o.Paid),a.Sum(o=>o.Refund),a.Sum(o=>o.Paid-o.Refund));
    }
}
public static class Calc
{
    public static decimal? Ratio(decimal a,decimal b)=>b>0?a/b:null;
    public static string N(decimal? n)=>n?.ToString("N2",CultureInfo.CurrentCulture)??"待补充";
    public static string P(decimal? n)=>n.HasValue?(n.Value*100).ToString("0.0")+"%":"待补充";
    public static string Delta(Snapshot a,Snapshot b)
    {
        if(a.At.Date!=b.At.Date) return "跨日：后台可能重置累计值，不直接计算区间增量。";
        if(b.Spend<a.Spend || b.Gmv<a.Gmv || b.Buyers<a.Buyers) return "累计值下降：可能为回补、重置或口径改变，请核对，暂不计算区间投产。";
        var cost=b.Spend-a.Spend; var gmv=b.Gmv-a.Gmv; var buyers=b.Buyers-a.Buyers;
        return $"{a.At:HH:mm} → {b.At:HH:mm}：新增消耗 ¥{N(cost)}；新增GMV ¥{N(gmv)}；新增买家 {buyers}；阶段支付ROI {N(Ratio(gmv,cost))}；阶段人数成本 ¥{N(Ratio(cost,buyers))}。\n新增买家为累计差值，不是独立广告归因；留意回传延迟。";
    }
    public static List<string> Advice(Session s)
    {
        var list=new List<string>(); var x=s.UsableSnapshots.OrderBy(x=>x.At).LastOrDefault(); var m=Metrics.From(s);
        var cap=s.BreakEvenCpa();
        if(x==null) list.Add("【待录入】尚无投放快照，无法判断消耗和投产；订单金额不能代替广告消耗。");
        if(!s.Start.HasValue && s.Orders.Any(o=>o.Channel=="直播" && o.OrderTime.Date!=s.Day.Date)) list.Add("【统计范围】当前按自然日汇总，文件中有其他日期的直播订单；若跨零点，请在场次设置中填写统计起止时间。");
        if(cap==null) list.Add("【缺经营参数】请补齐主推售价、平台费率、直接成本、预计订单保留率；暂不推荐具体出价或认定盈利。");
        else if(cap<=0) list.Add("【检查商品经济账】当前成本、保留率及利润要求下，可承受获客成本≤0；应调整商品或经营假设，而不是加预算。");
        else list.Add($"【经营上限·情景估算】按主推品假设，可承受每位支付用户广告费 ¥{N(cap)}；仅适用于该商品/单客一单，不代表低价混合订单同样可承受。");
        if(m.Count>0)
        {
            if(m.Count<s.SampleMin) list.Add($"【样本不足·经验规则】直播支付订单 {m.Count} 笔，少于设置的 {s.SampleMin} 笔；避免据此断言人群或素材有效，也不要由单笔大单决定放量。");
            if(m.Refund>0) list.Add($"【退款检查·数据事实】{m.RefundCount}/{m.Count} 笔有退款，金额退款率 {P(Ratio(m.Refund,m.Gross))}。支付ROI不能代替退款后产出；先看退款原因。");
            if(m.Redeemed==0) list.Add("【核销未验证】导入记录中尚无已核销订单（也可能未导出券级状态）。新订单未核销不等于无效；需后续补导核销结果。");
            var groups=s.LiveOrders.Where(o=>o.PaidTime.HasValue).GroupBy(o=>o.Product).OrderByDescending(g=>g.Count()).ToArray();
            if(groups.Length>1) list.Add("【成交结构】多商品混合成交，请分商品查看。人数目标不保证主推高价品成交；主播主推品与用户自主下单品可能不同。");
            if(s.Product.Length>0 && !s.LiveOrders.Any(o=>o.Product.Contains(s.Product,StringComparison.OrdinalIgnoreCase))) list.Add("【主推品匹配待核对】商品名称关键词未匹配订单，不能据此断言主推品没有成交；请检查设置关键词。");
        }
        if(x!=null)
        {
            if(s.Budget is >0 && x.Spend>=s.Budget*.9m) list.Add("【接近预算·本地提醒】当前消耗已达所设预算90%；这是可控投入提醒，不是官方阈值。本工具不会自动暂停后台。");
            if(s.LossLimit is >0 && x.Spend>=s.LossLimit) list.Add("【试错额度】消耗达到所设最大可承受试错投入；建议先停止追加并核对后台。此处以全部投入暴露提醒，不将待核销GMV当利润。");
            if(s.Goal=="成单人数" && cap is >0 && Calc.Ratio(x.Spend,x.Buyers)>cap) list.Add("【成本约束】全域平均人数成本高于主推品估算上限。先核对成交结构与口径，不因不跑量继续抬价。");
            if(s.Budget is >0 && x.Spend<s.Budget*.8m) list.Add("【预算未用满】单纯加预算通常不能解决不消耗；先查审核、余额、直播状态、定向、素材与目标约束。");
            if(x.Refund>x.Gmv) list.Add("【口径异常】快照退款大于GMV，请检查是否来自不同订单批次。");
        }
        list.Add("【调控】未验证稳定保留成交与履约前，不叠加起量任务。预算共用和互斥以当前后台规则为准，不能照搬旧教程。");
        list.Add("【规则边界】建议为本地透明规则，不是抖音内部推荐算法或收益承诺；缺数据时给出核查方向，不自动改出价。规则核验日期：2026-11-05。");
        return list;
    }
    public static string Summary(Session s)
    {
        var b=new StringBuilder(); var m=Metrics.From(s); var x=s.UsableSnapshots.OrderBy(x=>x.At).LastOrDefault();
        b.AppendLine("请依据以下本地推直播数据进行深度分析，区分事实、假设与待补项，给出预算/出价/商品/承接建议；不要将全域成交当作广告独立增量。");
        b.AppendLine($"场次：{s.Name}；账号：{s.Account}；下单归属日期：{s.Day:yyyy-MM-dd}；生成：{DateTime.Now:yyyy-MM-dd HH:mm}");
        b.AppendLine($"主推：{s.Product}；售价¥{N(s.Price)}；目标：{s.Goal}；日预算¥{N(s.Budget)}；目标值{N(s.Target)}（人数目标单位元/人，金额目标为ROI倍数，具体按当前后台口径）");
        b.AppendLine($"成本假设：平台费{N(s.FeeRate)}%、直接成本¥{N(s.DirectCost)}、最终订单保留率{N(s.Retention)}%、希望每笔支付订单留下¥{N(s.DesiredProfit)}；可承受获客成本¥{N(s.BreakEvenCpa())}。");
        b.AppendLine("\n【累计快照：来源口径为用户核对后录入】");
        foreach(var q in s.Snapshots.OrderBy(q=>q.At)) b.AppendLine($"{q.At:MM-dd HH:mm} 消耗{N(q.Spend)} GMV{N(q.Gmv)} 买家{q.Buyers} 订单{q.Orders?.ToString()??"未知"} 退款{N(q.Refund)} 进房{q.Visits?.ToString()??"未知"} 商品点击{q.Clicks?.ToString()??"未知"} 有效预约{q.Appointments?.ToString()??"未知"} 来源{q.Source} 备注{q.Note}");
        if(s.Snapshots.Count>1) {var a=s.Snapshots.OrderBy(q=>q.At).ToArray(); b.AppendLine(Delta(a[^2],a[^1]));}
        if(x!=null) b.AppendLine($"最新全域支付ROI {N(Ratio(x.Gmv,x.Spend))}；人数成本¥{N(Ratio(x.Spend,x.Buyers))}；订单成本¥{N(Ratio(x.Spend,x.Orders??0))}");
        b.AppendLine($"\n【导入订单：直播渠道，下单时间范围{(s.Start??s.Day.Date):yyyy-MM-dd HH:mm} 至 {(s.End??s.Day.Date.AddDays(1)):yyyy-MM-dd HH:mm}（不含结束时刻）】支付{m.Count}笔/买家{m.Buyers}人，实收¥{N(m.Gross)}，退款¥{N(m.Refund)}，保留¥{N(m.Retained)}，未全退买家{m.KeptBuyers}人，有核销标记订单{m.Redeemed}笔；排除其他范围/渠道{s.Orders.Count-s.LiveOrders.Count()}条。");
        if(x!=null && m.Count>0) b.AppendLine($"按同场假设粗分摊：保留金额/消耗{N(Ratio(m.Retained,x.Spend))}；未全退买家成本¥{N(Ratio(x.Spend,m.KeptBuyers))}。请核对快照与订单时间范围一致性。");
        foreach(var g in s.LiveOrders.Where(o=>o.PaidTime.HasValue).GroupBy(o=>o.Product)) b.AppendLine($"商品 {g.Key}：{g.Count()}笔，实收{N(g.Sum(o=>o.Paid))}，退款{N(g.Sum(o=>o.Refund))}，保留{N(g.Sum(o=>o.Paid-o.Refund))}。");
        foreach(var g in s.LiveOrders.Where(o=>o.Refund>0).GroupBy(o=>o.Reason)) b.AppendLine($"退款标签：{g.Key} {g.Count()}笔/{N(g.Sum(o=>o.Refund))}元（选择标签非确证根因）。");
        if(s.Report is {} r) b.AppendLine($"增量报表：账号{r.Account} 日期{r.Day:yyyy-MM-dd} 总GMV{N(r.Gmv)} 自然{N(r.Natural)} 增量{N(r.Increment)}；平台拆分非实验因果结论。");
        b.AppendLine("\n【设置变更】"); foreach(var c in s.Changes) b.AppendLine($"{c.At:MM-dd HH:mm} {c.Text}");
        b.AppendLine("备注："+s.Notes); b.AppendLine("\n【本地规则提示】"); foreach(var a in Advice(s)) b.AppendLine(a);
        b.AppendLine("\n未包含订单编号、买家UID、券码。自由填写的账号/商品/备注仍需本人检查后分享。\n规则核验日期2026-11-05；此工具不联网自动抓取后台。\n官方全域协议："+Knowledge.Official+"\n官方保障规则："+Knowledge.Guarantee);
        return b.ToString();
    }
}
public static class Store
{
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveAdDesk","data.json");
    static readonly JsonSerializerOptions Options=new(){WriteIndented=true};
    public static AppData Load(string? path=null)
    {
        path??=DefaultPath; if(!File.Exists(path)) return new();
        var d=JsonSerializer.Deserialize<AppData>(File.ReadAllText(path),Options)??throw new InvalidDataException("数据文件为空");
        if(d.Version!=1 || d.Sessions==null || d.Sessions.Any(s=>s.Orders==null||s.Snapshots==null||s.Changes==null)) throw new InvalidDataException("不支持的数据格式");
        return d;
    }
    public static void Save(AppData d,string? path=null)
    {
        path??=DefaultPath; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=path+".tmp"; File.WriteAllText(temp,JsonSerializer.Serialize(d,Options),Encoding.UTF8);
        if(File.Exists(path)) File.Replace(temp,path,path+".bak",true); else File.Move(temp,path);
    }
}

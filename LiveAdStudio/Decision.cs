using System.Text;
using System.Text.RegularExpressions;
using LiveAdDesk;
namespace LiveAdStudio;

sealed class BusinessGoal
{
    public string PlanId {get;set;}="";
    public string SellingMode {get;set;}="单主推";
    public decimal? MaxCost {get;set;}
    public string CostBasis {get;set;}="支付订单";
    public bool HardCeiling {get;set;}=true;
    public bool AssumeOtherCostZero {get;set;}
    public string Constraints {get;set;}="";
    public List<SkuGoal> Products {get;set;}=[];
}
sealed class SkuGoal
{
    public string Name {get;set;}="";
    public decimal Price {get;set;}
    public decimal AllowedCost {get;set;}
    public int ExpectedOrders {get;set;}=1;
    public decimal? Retention {get;set;}
}
sealed class DeliveryFacts
{
    public DateTime? CheckedAt {get;set;}
    public bool ScopeConfirmed {get;set;}
    public string DuringLive {get;set;}="未知";
    public string Approval {get;set;}="未知";
    public string Balance {get;set;}="未知";
    public string Account {get;set;}="未知";
    public string Materials {get;set;}="未知";
    public string Targeting {get;set;}="未知";
    public string Conflict {get;set;}="未知";
    public DateTime? LiveStart {get;set;}
    public DateTime? LiveEnd {get;set;}
    public DateTime? EnabledStart {get;set;}
    public DateTime? EnabledEnd {get;set;}
    public string Notes {get;set;}="";
}
sealed class MaterialBatch
{
    public EvidenceScope? EvidenceScope {get;set;}
    public DateTime? ComparedSnapshotAt {get;set;}
    public string? ComparedSnapshotId {get;set;}
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string FileName {get;set;}="";
    public string Kind {get;set;}="视频";
    public string PlanId {get;set;}="";
    public DateTime Start {get;set;}
    public DateTime End {get;set;}
    public DateTime ImportedAt {get;set;}=DateTime.Now;
    public bool ScopeConfirmed {get;set;}
    public List<MaterialRow> Rows {get;set;}=[];
}
sealed class MaterialRow
{
    public string Id {get;set;}="";
    public string Title {get;set;}="";
    public string Source {get;set;}="";
    public string CreatedRaw {get;set;}="";
    public DateTime? Created {get;set;}
    public decimal Spend {get;set;}
    public decimal Gmv {get;set;}
    public int Orders {get;set;}
}
sealed class MaterialReview
{
    public string Kind {get;set;}="视频";
    public string Id {get;set;}="";
    public string Relevance {get;set;}="未核实";
    public string Resolution {get;set;}="未处理";
    public string Notes {get;set;}="";
    public DateTime At {get;set;}=DateTime.Now;
}
sealed class FollowUp
{
    public bool FixedWindow {get;set;}
    public decimal? PreviousTarget {get;set;}
    public decimal? NewTarget {get;set;}
    public string? TargetGoal {get;set;}
    public Snapshot? Baseline {get;set;}
    public Snapshot? ReviewStart {get;set;}
    public FollowUpResult? LockedResult {get;set;}
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Title {get;set;}="";
    public string Reason {get;set;}="";
    public string Expected {get;set;}="";
    public string StopCondition {get;set;}="";
    public string Confounders {get;set;}="";
    public DateTime CreatedAt {get;set;}=DateTime.Now;
    public DateTime? ExecutedAt {get;set;}
    public int ReviewMinutes {get;set;}=30;
    public string Metric {get;set;}="支付ROI";
    public string Assessment {get;set;}="";
    public DateTime? ReviewedAt {get;set;}
    public bool Archived {get;set;}
}
record DecisionCard(string Id,string Level,string Title,string Evidence,string Action,string Tradeoff,string Review);
record EconomyView(decimal? PaymentCostCeiling,decimal? ReferenceRoi,decimal? MixRoi,string Note,object[] Scenarios);
record IntervalResult(DateTime Start,DateTime End,decimal Spend,decimal Gmv,int Buyers,decimal? Roi,decimal? Cpa,decimal SpendPerMinute,int? Visits=null,int? Clicks=null);
record FollowUpResult(string Status,string Note,IntervalResult? Before=null,IntervalResult? After=null,decimal? BeforeValue=null,decimal? AfterValue=null,string? Recommendation=null);

static class Decision
{
    public static readonly string[] Metrics=["支付ROI","人数成本","消耗速度"];
    static void Require(bool condition,string error){if(!condition)throw new InvalidDataException(error);}
    static void Text(string? s,int max,string name)=>Require(s!=null&&s.Length<=max,name+"缺失或过长。");
    static void Choice(string? value,string name,params string[] values)=>Require(values.Contains(value),name+"取值无效。");
    public static void Validate(Room r)
    {
        var b=r.Business;var f=r.Facts;
        Require(b!=null&&f!=null&&r.MaterialBatches!=null&&r.MaterialReviews!=null&&r.FollowUps!=null,"新版记录不完整。");
        Text(b!.PlanId,40,"计划ID");Require(b.PlanId.Length==0||Regex.IsMatch(b.PlanId,@"^\d{1,40}$"),"计划ID只能填写数字。");
        Text(b.Constraints,4000,"固定经营约束");Choice(b.SellingMode,"商品模式","单主推","多商品");Choice(b.CostBasis,"成本口径","支付订单","未退款订单");
        Require(!b.MaxCost.HasValue||b.MaxCost>0,"可接受获客成本应大于0；未知请留空。");
        Require(b.Products!=null&&b.Products.Count<=20,"最多设置20个商品目标。");
        foreach(var p in b.Products!)Require(p!=null&&!string.IsNullOrWhiteSpace(p.Name)&&p.Name.Length<=200&&p.Price>0&&p.AllowedCost>0&&p.ExpectedOrders is >0 and <=100000&&(!p.Retention.HasValue||p.Retention is >=0 and <=100),"商品目标需填写名称、正数价格/广告费、正整数预期订单；保留率为0—100%。");
        Require(b.Products.Select(p=>p.Name.Trim()).Distinct().Count()==b.Products.Count,"商品目标名称不能重复。");
        Choice(f!.DuringLive,"直播期间开关","未知","已开启","未开启");Choice(f.Approval,"审核","未知","通过","待审核","不通过");Choice(f.Balance,"余额","未知","充足","不足");Choice(f.Account,"账户","未知","正常","受限");Choice(f.Materials,"素材可投","未知","可投","待审核","无可投素材");Choice(f.Targeting,"地域与定向","未知","已核对","过窄或不符");Choice(f.Conflict,"互斥限制","未知","无","有");Text(f.Notes,4000,"核查备注");
        Pair(f.LiveStart,f.LiveEnd,"直播时间");Pair(f.EnabledStart,f.EnabledEnd,"计划开启时间");
        Require(!f.ScopeConfirmed||b.PlanId.Length>0,"确认范围前请先填写计划ID。");
        Require(r.MaterialBatches!.Count<=40&&r.MaterialReviews!.Count<=20000&&r.FollowUps!.Count<=500,"素材批次或行动记录超过限制，请另建场次。");
        foreach(var batch in r.MaterialBatches)
        {
            Require(batch!=null&&Guid.TryParseExact(batch.Id,"N",out _),"素材批次标识无效。");Choice(batch!.Kind,"素材类型","视频","图文");Text(batch.FileName,260,"报表文件名");Text(batch.PlanId,40,"报表计划ID");
            Require(batch.End>batch.Start&&batch.Rows!=null&&batch.Rows.Count<=10000,"素材报表范围或行数无效。");
            foreach(var row in batch.Rows!)Require(row!=null&&Regex.IsMatch(row.Id??"",@"^\d{1,40}$")&&row.Title!=null&&row.Title.Length<=10000&&row.Source!=null&&row.Source.Length<=200&&row.CreatedRaw!=null&&row.CreatedRaw.Length<=100&&row.Spend>=0&&row.Gmv>=0&&row.Orders>=0,"素材报表包含无效字段。");
            Require(batch.Rows.Select(x=>x.Id).Distinct().Count()==batch.Rows.Count,"同一报表有重复素材ID，请核对导出维度。");
        }
        Require(r.MaterialBatches.Select(x=>x.Id).Distinct().Count()==r.MaterialBatches.Count,"素材批次ID重复。");
        foreach(var m in r.MaterialReviews!)
        {
            Require(m!=null&&Regex.IsMatch(m.Id??"",@"^\d{1,40}$"),"素材确认ID无效。");Choice(m!.Kind,"素材确认类型","视频","图文");Choice(m.Relevance,"相关性","未核实","相关","不相关");Choice(m.Resolution,"处理状态","未处理","已排除","已停用","保留观察");Text(m.Notes,2000,"素材备注");
        }
        Require(r.MaterialReviews.Select(m=>m.Kind+":"+m.Id).Distinct().Count()==r.MaterialReviews.Count,"素材人工确认记录重复。");
        foreach(var a in r.FollowUps!)ValidateAction(a);
        Require(r.FollowUps.Select(a=>a.Id).Distinct().Count()==r.FollowUps.Count,"行动ID重复。");
        Require(r.FollowUps.Count(a=>!a.ReviewedAt.HasValue&&!a.Archived)<=20,"最多保留20项未复查行动，请先复查或归档已有行动。");
    }
    static void Pair(DateTime? a,DateTime? b,string name)=>Require(a.HasValue==b.HasValue&&(!a.HasValue||b>a),name+"需要同时填写起止时间，结束晚于开始。");
    public static void ValidateAction(FollowUp a)
    {
        Require(a!=null&&Guid.TryParseExact(a.Id,"N",out _),"行动标识无效。");Require(!string.IsNullOrWhiteSpace(a!.Title)&&a.Title.Length<=300,"行动内容为1—300字。");
        Text(a.Reason,1500,"行动依据");Text(a.Expected,1000,"预期变化");Text(a.StopCondition,1000,"退出条件");Text(a.Confounders,1500,"同期变化");Text(a.Assessment,2000,"复查结论");
        Require(a.ReviewMinutes is >=5 and <=1440,"复查间隔为5—1440分钟。");Require(Metrics.Contains(a.Metric),"行动指标无效。");Require(!a.ExecutedAt.HasValue||a.ExecutedAt<=DateTime.Now.AddMinutes(5),"执行时间不能在未来。");
        Require(a.PreviousTarget is null or >0 && a.NewTarget is null or >0,"调价目标必须大于零。");
        if(a.PreviousTarget.HasValue||a.NewTarget.HasValue||a.TargetGoal is not null)
            Require(a.FixedWindow&&a.PreviousTarget is >0&&a.NewTarget is >0&&a.PreviousTarget!=a.NewTarget&&
                a.TargetGoal is "成交金额" or "成单人数","调价需要同一目标口径、两个不同的正数和固定复查窗口。");
        Require(!a.ReviewedAt.HasValue||a.ExecutedAt.HasValue,"未执行行动不能标记已复查。");
        Require(!a.Archived||!string.IsNullOrWhiteSpace(a.Assessment),"归档行动需要填写原因，归档不代表有效。");
    }
    public static EconomyView Economy(Room r)
    {
        var b=r.Business;var s=r.Finance;
        decimal? allowed=b.MaxCost;
        string note="经营要求按用户填写计算，不是系统逐单成本承诺，也不是利润结论。";
        if(b.CostBasis=="未退款订单")
        {
            allowed=b.MaxCost.HasValue&&s.Retention.HasValue?b.MaxCost*s.Retention/100:null;
            note+=" 未退款口径换算支付目标需填写预估保留率；该比例是经营假设。";
        }
        decimal? roi=s.Price is >0&&allowed is >0?s.Price/allowed:null;
        decimal? mixed=null;
        if(b.SellingMode=="多商品")
        {
            note+=" 多款混卖不能把单款参考ROI直接当整场目标。";
            if(b.Products.Count>=2)
            {
                decimal cost=0,gmv=0;bool known=true;
                foreach(var p in b.Products)
                {
                    if(b.CostBasis=="未退款订单"&&!p.Retention.HasValue){known=false;break;}
                    cost+=p.AllowedCost*p.ExpectedOrders*(b.CostBasis=="未退款订单"?p.Retention!.Value/100:1);gmv+=p.Price*p.ExpectedOrders;
                }
                if(known&&cost>0)mixed=gmv/cost;
            }
            note+=" 成交结构参考值来自预期订单数，不代表已验证的最佳出价。";
        }
        var scenarios=new[]{4m,5m,6m,6.4m,8m}.Select(x=>(object)new{roi=x,averageCost=s.Price is >0?s.Price/x:null,requiredGmv=s.Budget.HasValue?s.Budget*x:null}).ToArray();
        return new(allowed,roi,mixed,note,scenarios);
    }
    public static double? OverlapMinutes(DeliveryFacts f)
    {
        if(!f.LiveStart.HasValue||!f.LiveEnd.HasValue||!f.EnabledStart.HasValue||!f.EnabledEnd.HasValue)return null;
        return Math.Max(0,(new[]{f.LiveEnd.Value,f.EnabledEnd.Value}.Min()-new[]{f.LiveStart.Value,f.EnabledStart.Value}.Max()).TotalMinutes);
    }
    public static List<MaterialBatch> CurrentReports(Room r)
    {
        var start=r.Finance.Start??r.Finance.Day.Date;var end=r.Finance.End??r.Finance.Day.Date.AddDays(1);
        return r.MaterialBatches.Where(b=>b.ScopeConfirmed&&b.PlanId==r.Business.PlanId&&b.Start==start&&b.End==end).GroupBy(b=>b.Kind).Select(g=>g.OrderBy(b=>b.ImportedAt).Last()).ToList();
    }
    public static MaterialReview? Review(Room r,string kind,string id)=>r.MaterialReviews.FirstOrDefault(m=>m.Kind==kind&&m.Id==id);
    public static void AddReport(Room r,MaterialBatch batch)
    {
        // ponytail: 保存报表快照便于追溯，40批次后另建场次，不引入数据库。
        Require(r.MaterialBatches.Count<40,"本场已保留40个报表批次，请新建场次或先备份。");
        var candidate=new Room{Finance=r.Finance,Business=r.Business,Facts=r.Facts,MaterialBatches=[batch]};Validate(candidate);r.MaterialBatches.Add(batch);
    }
    public static List<DecisionCard> Cards(Room r)
    {
        var list=new List<DecisionCard>();var f=r.Facts;var b=r.Business;var s=r.Finance;var x=s.Snapshots.OrderBy(z=>z.At).LastOrDefault();
        void Add(string id,string level,string title,string evidence,string action,string tradeoff,string review)=>list.Add(new(id,level,title,evidence,action,tradeoff,review));
        if(!f.ScopeConfirmed||b.PlanId.Length==0)Add("scope","待核查","先核对计划与统计范围","计划ID或统计范围尚未由你确认。","在投放核查中填写计划ID，并核对账号、日期与投放范围。","范围混用会导致成本与成交无法对应。","确认后再比较报表或调整目标。");
        var failures=new List<string>();
        if(f.DuringLive=="未开启")failures.Add("直播期间计划未开启");if(f.Approval=="不通过")failures.Add("计划审核不通过");if(f.Balance=="不足")failures.Add("余额不足");if(f.Account=="受限")failures.Add("账户受限");if(f.Materials=="无可投素材")failures.Add("无可投素材");if(f.Conflict=="有")failures.Add("存在互斥限制");
        var overlap=OverlapMinutes(f);if(overlap==0)failures.Add("计划开启时段与直播没有重合");
        if(failures.Count>0)Add("eligibility","需处理","存在明确的投放前置问题",string.Join("；",failures)+"。证据来自你填写的后台核查。","先处理这些前置问题，再核验投放状态。","调低ROI或加预算不替代资格、排期和账户修复。","处理后重新核查审核、时段与可投状态。");
        var unknown=new List<string>();if(f.Approval=="未知"||f.Approval=="待审核")unknown.Add("审核结果");if(f.Balance=="未知")unknown.Add("余额");if(f.Account=="未知")unknown.Add("账户状态");if(f.Materials is "未知" or "待审核")unknown.Add("素材可投状态");if(f.DuringLive=="未知")unknown.Add("直播期间开关");if(!overlap.HasValue)unknown.Add("直播/开启时间重合");if(f.Targeting=="未知")unknown.Add("地域与定向");if(f.Conflict=="未知")unknown.Add("互斥限制");
        if(unknown.Count>0)Add("unknown","待核查","还不能认定是ROI导致不起量","尚缺："+string.Join("、",unknown)+"。当前暂停不代表直播时未开启。","对照后台详情、日志与素材状态补齐核查。","不能把未知当作正常，也不能保证换自动出价即可起量。","前置条件确认后，再验证目标约束。");
        if(f.Targeting=="过窄或不符")Add("targeting","需处理","地域或定向需要核对","你标记了地域/定向过窄或与服务范围不符。","按真实门店服务范围核对定向，不仅为起量盲目放大地域。","覆盖变广不等于有效到店客户变多。","查看进房、商品点击及有效成交是否同步变化。");
        var reports=CurrentReports(r);var bad=reports.SelectMany(batch=>batch.Rows.Select(row=>new{batch.Kind,Row=row,Review=Review(r,batch.Kind,row.Id)})).Where(v=>v.Review?.Relevance=="不相关"&&v.Review.Resolution is not ("已排除" or "已停用")).ToArray();
        if(bad.Length>0)Add("irrelevant","需处理","有人工确认的不相关素材待处理",$"{bad.Length}条；对应报表历史消耗{bad.Sum(v=>v.Row.Spend):N2}元。相关性由你确认，非标题自动定性。","在后台核对并停用/排除相应素材，然后在此记录处理状态。","小额消耗不能证明它导致整场不起量；不自动删除主页内容。","清理后核对剩余素材可投状态，并记录后续表现。");
        if(r.MaterialBatches.Count>reports.Count)Add("material-scope","观察","部分素材报表未纳入当前汇总","只汇总当前计划、统计范围下最新的视频/图文批次。较早或未确认范围的报表仍保留。","在素材体检页核对所用批次与范围。","不同日期或计划的消耗不能直接相加。","确认范围后再看消耗集中度。");
        if(x==null)Add("snapshot","待核查","还缺已确认投放快照","截图候选与素材报表不自动进入财务账本。","核对后台累计消耗、GMV和成单人数，保存一笔快照。","无快照不能判定整场ROI；素材创建时间也不是审核通过时间。","补录后检查数据时点与订单范围。");
        else if(x.At>DateTime.Now.AddMinutes(5))Add("future","需处理","最新快照时间在未来",$"快照时间{x.At:yyyy-MM-dd HH:mm}。","在数据页核对并删除/重录错误时间的快照。","时间错误时暂停依此提出调价建议。","时间校正后重新分析。");
        else if(x.At<(s.Start??s.Day.Date)||x.At>(s.End??s.Day.Date.AddDays(1)))Add("snapshot-scope","需处理","最新快照与本场日期范围不符",$"快照时间{x.At:yyyy-MM-dd HH:mm}，已从当前指标和行动复查排除。","核对快照日期与场次范围，跨零点先设置完整起止。","其他场次数字不能用于当前调价。","范围校正后补录同口径快照。");
        else
        {
            if(s.Day.Date==DateTime.Today&&DateTime.Now-x.At>TimeSpan.FromMinutes(30))Add("stale","待核查","已确认数字可能已过期",$"最后确认{x.At:HH:mm}，距今超过30分钟（本地提醒策略）。","核对最新截图并补快照，保留旧记录。","未确认截图不是最新账本；过期数据不适合实时加价判断。","更新快照后再看建议。");
            if(x.Spend<=1&&overlap is >0&&failures.Count==0)Add("low-delivery","待验证","已开启但报告消耗极少",$"快照消耗{x.Spend:N2}元；核对的开启与直播重合约{overlap:N1}分钟。","先完成前置核查；均正常后，再把ROI/定向约束作为候选原因验证。","小额数据不能评价素材转化率，也不能据此计算最佳ROI。","记录一个改动及复查条件，保留总投入边界。");
            if(s.LossLimit is >0&&x.Spend>=s.LossLimit)Add("loss","需处理","达到你设置的试错投入边界",$"累计消耗{x.Spend:N2}元，试错边界{s.LossLimit:N2}元。","先核对后台总消耗并停止继续追加；工具不会自动暂停广告。","待核销GMV不等于可用于继续试投的利润。","确认退款与有效成交后再决定后续投入。");
        }
        var e=Economy(r);var relevantRoi=b.SellingMode=="多商品"?e.MixRoi:e.ReferenceRoi;
        if(b.MaxCost==null)Add("goal","待核查","先明确经营要求","还未填写可接受获客成本及口径。","在经营目标中区分支付成交、未退款成交与试验投入。","日预算不是单笔获客成本保证。","填写后查看ROI换算与成本放宽代价。");
        else if(b.SellingMode=="多商品"&&e.MixRoi==null)Add("mix","观察","单款参考ROI不能代表混卖整场",$"本场为多商品；单款参考ROI{Calc.N(e.ReferenceRoi)}，预期成交结构尚不完整。","填写各商品价格、可接受广告费和预期订单，或先保留未知。","预期结构是情景估算，不能从一两笔订单当作稳定比例。","导入实际订单，核对商品与退款结构。");
        if(b.HardCeiling&&s.Goal=="成交金额"&&s.Target.HasValue&&relevantRoi is >0&&s.Target<relevantRoi)Add("ceiling","需处理","后台目标放宽了你的经营成本要求",$"当前ROI目标{s.Target:N2}，按所填假设计算的参考下限{relevantRoi:N2}。","先确认是否接受更高的成交广告成本，再考虑放宽目标。","系统目标本身也不是逐单硬上限；自动出价不保证200元成交。","按同范围支付与未退款结果分别复查。");
        if(b.HardCeiling&&s.Goal=="成单人数"&&b.SellingMode=="单主推"&&e.PaymentCostCeiling is >0&&s.Target>e.PaymentCostCeiling)
            Add("people-ceiling","需处理","人数成本目标高于经营参考上限",$"后台目标{s.Target:N2}元，单主推支付口径参考{e.PaymentCostCeiling:N2}元。","先核对人数成本与每单成本的口径，再决定试错边界。","参考值按一位买家一单估算。","复查成交结构和同口径的实际人数成本。");
        var m=LiveAdDesk.Metrics.From(s);
        if(m.Refund>0)Add("refund","观察","支付成交需要继续看退款",$"订单范围内实收{m.Gross:N2}元、退款{m.Refund:N2}元、保留{m.Retained:N2}元。","查看分商品退款、原因和后续核销，避免由一笔高价单决定放量。","保留金额不是已核销收入；全域成交也不是逐单广告归因。","补导最新退款/核销结果并核对快照范围。");
        if(list.Count==0)Add("observe","观察","前置核查已记录，继续验证成交","当前没有命中明确阻断；这不证明必然能起量。","保持单次可追踪的改动，记录各商品成交与退款。","不因花出预算就判定投放有效。","创建行动复查，并等待足够的已确认区间数据。");
        return list.OrderBy(c=>c.Level=="需处理"?0:c.Level=="待核查"?1:c.Level=="待验证"?2:3).ToList();
    }
    internal static IntervalResult? Interval(Snapshot a,Snapshot b)
    {
        if(a.At.Date!=b.At.Date||b.At<=a.At||b.Spend<a.Spend||b.Gmv<a.Gmv||b.Buyers<a.Buyers)return null;
        decimal spend=b.Spend-a.Spend,gmv=b.Gmv-a.Gmv;int buyers=b.Buyers-a.Buyers;
        static int? Delta(int? first,int? last)=>first.HasValue&&last>=first?last-first:null;
        return new(a.At,b.At,spend,gmv,buyers,Calc.Ratio(gmv,spend),Calc.Ratio(spend,buyers),spend/(decimal)(b.At-a.At).TotalMinutes,
            Delta(a.Visits,b.Visits),Delta(a.Clicks,b.Clicks));
    }
    public static FollowUpResult Evaluate(Room r,FollowUp action)
    {
        if(action.Archived)return new("已归档","用户归档，不代表动作有效。原因："+action.Assessment);
        if(!action.ExecutedAt.HasValue)return new("待执行","记录实际执行时间后，才开始匹配前后区间。");
        if(action.FixedWindow)return ReviewEvidence.Evaluate(r,action);
        var at=action.ExecutedAt.Value;var all=r.Finance.UsableSnapshots.OrderBy(x=>x.At).ToArray();
        var before=all.Where(x=>x.At<=at).TakeLast(2).ToArray();var after=all.Where(x=>x.At>=at).ToArray();
        if(before.Length<2||after.Length<2)return new("等待快照","需要执行前后各两条已确认累计快照；聊天描述不替代区间数据。");
        if(after[^1].At<at.AddMinutes(action.ReviewMinutes))return new("等待观察窗","执行后的已确认快照尚未覆盖设置的复查间隔。");
        var pre=Interval(before[0],before[1]);var post=Interval(after[0],after[^1]);
        if(pre==null||post==null)return new("口径待核对","区间包含跨日、累计回补或时间异常，暂不比较；不能自动归因。");
        decimal? Value(IntervalResult x)=>action.Metric=="支付ROI"?x.Roi:action.Metric=="人数成本"?x.Cpa:x.SpendPerMinute;
        var p=Value(pre);var q=Value(post);
        if(!p.HasValue||!q.HasValue)return new("分母不足","区间没有相应消耗或新增买家，指标为未知，不按0比较。",pre,post,p,q);
        var note="这是前后区间观察，不是因果证明；直播内容、自然流量与回传延迟均可能影响结果。";
        if(post.Buyers<r.Finance.SampleMin)note+=" 新增买家较少，勿据此认定稳定有效。";
        if(action.Confounders.Length>0)note+=" 已记录同期其他变化。";
        return new(action.ReviewedAt.HasValue?"已人工复查":"可复查",note,pre,post,p,q);
    }
}

using System.Text.Json;
using LiveAdDesk;

namespace LiveAdStudio;

record Reconciliation(string Status,string Note,decimal? PlanSpend,decimal? MaterialSpend,decimal? Difference,DateTime? At);

static class ReviewEvidence
{
    static readonly TimeSpan Tolerance=TimeSpan.FromMinutes(2);

    internal static EvidenceScope Scope(Room room)
    {
        var s=room.Finance;
        return new(s.Id,(s.Account??"").Trim(),room.Business.PlanId,s.Start??s.Day.Date,s.End??s.Day.Date.AddDays(1));
    }

    internal static bool Matches(Room room,EvidenceScope? recorded)
    {
        var current=Scope(room);
        return recorded==current && current.Account.Length>0 && current.PlanId.Length>0;
    }

    internal static Reconciliation Reconcile(Room room)
    {
        var snapshot=room.Finance.UsableSnapshots.OrderBy(s=>s.At).LastOrDefault();
        var batches=Decision.CurrentReports(room);
        var materialSpend=batches.Count==0?(decimal?)null:batches.Sum(b=>b.Rows.Sum(row=>row.Spend));
        Reconciliation Wait(string reason)=>new("待核对",reason,snapshot?.Spend,materialSpend,null,snapshot?.At);
        if(snapshot is null||batches.Count==0)return Wait("先保存一笔累计快照，并导入当前范围的素材报表。");
        if(!Matches(room,snapshot.Scope))return Wait("请保存或确认同账号、同计划、同统计范围的快照。");
        if(batches.Any(b=>!Matches(room,b.EvidenceScope)||b.ComparedSnapshotAt!=snapshot.At||b.ComparedSnapshotId!=snapshot.Id))
            return Wait("核对各份素材报表与这笔快照的账号、计划、统计范围和数据截止点；当前只分别展示。");
        var difference=snapshot.Spend-materialSpend;
        return new(difference==0?"金额对齐":"差额待对账",
            "仅比较已导入的素材批次。差额需核对报表覆盖范围和数据回传。",snapshot.Spend,materialSpend,difference,snapshot.At);
    }

    static Snapshot Copy(Snapshot s)=>JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(s))!;

    internal static void Refresh(Room room,FollowUp action)
    {
        if(!action.FixedWindow||action.Archived||action.ReviewedAt.HasValue||action.LockedResult is not null||action.ExecutedAt is not DateTime at)return;
        var all=room.Finance.UsableSnapshots.Where(s=>Matches(room,s.Scope)).OrderBy(s=>s.At).ToArray();
        if(action.Baseline is null)
        {
            var baseline=all.LastOrDefault(s=>s.At<=at&&at-s.At<=Tolerance);
            if(baseline is not null)action.Baseline=Copy(baseline);
        }
        if(action.Baseline is not { } b||!Matches(room,b.Scope))return;
        var start=b.At.AddMinutes(-action.ReviewMinutes);
        if(action.ReviewStart is null)
        {
            var prior=all.Where(s=>s.At<b.At&&(s.At-start).Duration()<=Tolerance)
                .OrderBy(s=>(s.At-start).Duration()).FirstOrDefault();
            if(prior is not null)action.ReviewStart=Copy(prior);
        }
        if(action.ReviewStart is not { } first)return;
        var end=at.AddMinutes(action.ReviewMinutes);
        var last=all.FirstOrDefault(s=>s.At>=end&&s.At-end<=Tolerance);
        if(last is null)return;
        if(((last.At-b.At)-(b.At-first.At)).Duration()>Tolerance)
        {
            action.LockedResult=new("区间不可比","前后实际时长相差超过2分钟，请重新记录可比窗口。");
            return;
        }
        var before=Decision.Interval(first,b);
        var after=Decision.Interval(b,last);
        var between=new[]{first,b,last}.Concat(all.Where(s=>s.At>first.At&&s.At<last.At))
            .DistinctBy(s=>s.At).OrderBy(s=>s.At).ToArray();
        if(before is null||after is null||between.Zip(between.Skip(1)).Any(pair=>Decision.Interval(pair.First,pair.Second) is null))
        {
            action.LockedResult=new("口径待核对","数据跨日或累计值回退，请核对原始数据。");
            return;
        }
        decimal? Metric(IntervalResult i)=>action.Metric switch{"支付ROI"=>i.Roi,"人数成本"=>i.Cpa,_=>i.SpendPerMinute};
        action.LockedResult=new("可复查","窗口已锁定。消耗速度、进房和点击仅表示同期变化。",before,after,Metric(before),Metric(after));
    }

    internal static FollowUpResult Evaluate(Room room,FollowUp action)
    {
        if(action.Baseline is null)return new("缺调整基线","请在调整前保存同口径累计快照；仅接收执行时间前2分钟内的记录。");
        if(!Matches(room,action.Baseline.Scope))return new("口径待核对","当前账号、计划或统计范围与调整基线不一致。");
        if(action.ReviewStart is null)return new("缺调整前窗口","请补同口径、接近观察时长的调整前累计快照。");
        if(action.LockedResult is not { } result)
            return DateTime.Now<action.ExecutedAt!.Value.AddMinutes(action.ReviewMinutes)
                ?new("等待观察窗","观察到点后保存一笔同口径累计快照，允许2分钟记录偏差。")
                :new("缺到点快照","请补到点后2分钟内的原始数据；更晚的快照不纳入这次复查。");
        if(result.Before is not { } before||result.After is not { } after)return result;
        var changed=!string.IsNullOrWhiteSpace(action.Confounders)
            ||room.Finance.Changes.Any(c=>c.At>before.Start&&c.At<=after.End)
            ||room.FollowUps.Any(a=>a.Id!=action.Id&&a.ExecutedAt>before.Start&&a.ExecutedAt<=after.End);
        var note=result.Note;
        if(after.Visits is null||after.Clicks is null||before.Visits is null||before.Clicks is null)
            note+=" 缺同口径进房或点击，需补两段窗口对应的累计数据页。";
        if(result.BeforeValue is null||result.AfterValue is null)note+=" ROI或人数成本分母不足时保留未知。";
        if(after.Buyers<room.Finance.SampleMin)note+=" 新增买家样本较小。";
        string recommendation="继续观察成交和退款。";
        if(before.Visits is not int preVisits||after.Visits is not int postVisits||
            before.Clicks is not int preClicks||after.Clicks is not int postClicks)
            recommendation="先补同口径的累计进房和商品点击。";
        else
        {
            decimal preMinutes=(decimal)(before.End-before.Start).TotalMinutes;
            decimal postMinutes=(decimal)(after.End-after.Start).TotalMinutes;
            if(after.SpendPerMinute>before.SpendPerMinute && postVisits/postMinutes<=preVisits/preMinutes)
                recommendation="消耗加快，进房未同步增加。先核查曝光和进房。";
            else if(postVisits/postMinutes>preVisits/preMinutes && postClicks/postMinutes<=preClicks/preMinutes)
                recommendation="进房增加，商品点击未同步增加。先核查讲品和挂车。";
        }
        if(changed){note+=" 同期还有其他动作，不能单独评价这次调整。";recommendation="核对同期变更，再单独观察一个动作。";}
        if(room.Finance.LossLimit is >0&&action.Baseline.Spend+after.Spend>=room.Finance.LossLimit)
            recommendation="到点累计消耗已触及试错投入上限，核对后台后停止追加。";
        return result with {Status=changed?"同期有变更":action.ReviewedAt.HasValue?"已人工复查":"可复查",Note=note,Recommendation=recommendation};
    }
}

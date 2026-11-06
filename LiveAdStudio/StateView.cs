using LiveAdDesk;
namespace LiveAdStudio;
static class StateView
{
    public static object Build(Workspace w,bool busy=false,bool monitoring=false,List<ModelItem>? models=null,string email="",bool connected=false,object? capture=null,object? pending=null,string materialQuery="")
    {
        var r=w.Current;var s=r.Finance;var x=s.UsableSnapshots.OrderBy(q=>q.At).LastOrDefault();var m=Metrics.From(s);var calls=r.Calls;var reports=Decision.CurrentReports(r);
        var all=reports.SelectMany(batch=>batch.Rows.Select(row=>new{batch.Kind,Row=row,Review=Decision.Review(r,batch.Kind,row.Id)})).OrderByDescending(v=>v.Row.Spend).ToArray();
        var found=all.Where(v=>materialQuery.Length==0||(v.Row.Id+" "+v.Row.Title+" "+v.Row.Source).Contains(materialQuery,StringComparison.OrdinalIgnoreCase)).ToArray();
        return new{
            type="state",version=AppUpdate.CurrentVersion.ToString(3),busy,monitoring,model=w.Model,models=(models??[]).Select(a=>new{slug=a.Slug,name=a.Name}),connected,email,
            rooms=w.Rooms.Select(room=>new{id=room.Finance.Id,name=room.Finance.Name,date=room.Finance.Day.ToString("yyyy-MM-dd")}),selected=w.Selected,
            finance=new{s.Id,s.Name,s.Account,s.Day,s.Start,s.End,s.Goal,s.Product,s.Price,s.FeeRate,s.DirectCost,s.Retention,s.DesiredProfit,s.Budget,s.Target,s.LossLimit,s.Notes,s.SampleMin},
            config=new{r.CaptureSeconds,r.AnalysisMinutes,r.RequestLimit},snapshots=s.Snapshots.OrderBy(q=>q.At).ToList(),chartSnapshots=s.UsableSnapshots.OrderBy(q=>q.At).ToList(),
            metrics=new{spend=x?.Spend,gmv=x?.Gmv,roi=x==null?null:Calc.Ratio(x.Gmv,x.Spend),retained=s.Orders.Count>0?(decimal?)m.Retained:x?.Refund.HasValue==true?x.Gmv-x.Refund:null,refund=s.Orders.Count>0?(decimal?)m.Refund:x?.Refund,orders=m.Count,buyers=m.Buyers,redeemed=m.Redeemed,latestAt=x?.At,r.OrdersImportedAt},
            products=s.LiveOrders.Where(o=>o.PaidTime.HasValue&&!o.Status.Contains("未支付")).GroupBy(o=>o.Product).Select(g=>new{name=g.Key,count=g.Count(),gross=g.Sum(o=>o.Paid),refund=g.Sum(o=>o.Refund),kept=g.Sum(o=>o.Paid-o.Refund),reasons=string.Join("；",g.Select(o=>o.Reason).Where(t=>t.Length>0).Distinct())}),
            advice=Calc.Advice(s),changes=s.Changes.OrderByDescending(c=>c.At).Take(30),events=r.Events.TakeLast(30).Reverse(),
            messages=r.Messages.TakeLast(40).Select(c=>new{c.Id,c.Role,c.Text,c.At,images=c.Images.Select(i=>new{id=i,thumb=ImageStore.Thumb(i)})}),
            pending=pending??Array.Empty<object>(),capture=capture??new{selected=false,revision=0,title="",size="",thumb="",raw="",candidates=new Dictionary<string,decimal>(),trigger="未采集"},
            usage=new{attempts=calls.Count,completed=calls.Count(c=>c.Status=="completed"),failed=calls.Count(c=>c.Status is "failed" or "cancelled"),input=calls.Sum(c=>c.InputTokens??0),output=calls.Sum(c=>c.OutputTokens??0),reported=calls.Count(c=>c.InputTokens.HasValue),last=calls.LastOrDefault()?.At,logs=calls.TakeLast(30).Reverse()},
            business=r.Business,facts=r.Facts,economy=Decision.Economy(r),decisions=Decision.Cards(r),overlapMinutes=Decision.OverlapMinutes(r.Facts),
            materials=new{query=materialQuery,totalCount=all.Length,matchedCount=found.Length,spend=all.Sum(v=>v.Row.Spend),gmv=all.Sum(v=>v.Row.Gmv),paidCount=all.Count(v=>v.Row.Spend>0),unreviewed=all.Count(v=>v.Review==null||v.Review.Relevance=="未核实"),reports=r.MaterialBatches.OrderByDescending(a=>a.ImportedAt).Take(40).Select(a=>new{a.Id,a.Kind,a.FileName,a.PlanId,a.Start,a.End,a.ImportedAt,a.ScopeConfirmed,count=a.Rows.Count,active=reports.Any(t=>t.Id==a.Id),invalidDates=a.Rows.Count(x=>!x.Created.HasValue),blankTitles=a.Rows.Count(x=>x.Title.Length==0)}),
                rows=found.Take(300).Select(v=>new{kind=v.Kind,v.Row.Id,title=v.Row.Title.Length<=500?v.Row.Title:v.Row.Title[..500]+"（标题节选）",v.Row.Source,v.Row.Created,v.Row.CreatedRaw,v.Row.Spend,v.Row.Gmv,v.Row.Orders,roi=Calc.Ratio(v.Row.Gmv,v.Row.Spend),share=Calc.Ratio(v.Row.Spend,all.Sum(a=>a.Row.Spend)),relevance=v.Review?.Relevance??"未核实",resolution=v.Review?.Resolution??"未处理",notes=v.Review?.Notes??""})},
            followUps=r.FollowUps.OrderByDescending(a=>a.CreatedAt).Select(a=>new{item=a,result=Decision.Evaluate(r,a)}),
            knowledge=Knowledge.Text
        };
    }
}

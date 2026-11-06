using System.Text.Json;
namespace LiveAdStudio;
static class DecisionCommands
{
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    public static string Apply(Room r,string action,JsonElement p)
    {
        var b=r.Business;var f=r.Facts;var reviews=r.MaterialReviews;var actions=r.FollowUps;
        var message="";
        decimal? price=r.Finance.Price,retention=r.Finance.Retention;
        switch(action)
        {
            case "business":
                b=(p.TryGetProperty("business",out var goal)?goal:p).Deserialize<BusinessGoal>(Json)??throw new InvalidDataException("经营目标为空。");
                if(p.TryGetProperty("refPrice",out var rp))price=rp.ValueKind==JsonValueKind.Null?null:rp.GetDecimal();
                if(p.TryGetProperty("refRetention",out var rr))retention=rr.ValueKind==JsonValueKind.Null?null:rr.GetDecimal();
                if(price<0||retention<0||retention>100)throw new InvalidDataException("参考价格不可为负，预估保留率为0—100%。");
                if(r.Business.PlanId.Length>0&&b.PlanId!=r.Business.PlanId&&(r.Finance.Snapshots.Count>0||r.MaterialBatches.Count>0||r.Finance.Orders.Count>0))throw new InvalidDataException("已有数据的场次不能更换计划ID；请为其他计划新建场次。");
                if(b.PlanId!=r.Business.PlanId)f=new();
                message="经营目标已保存；不会修改广告后台。";break;
            case "facts":
                f=p.Deserialize<DeliveryFacts>(Json)??throw new InvalidDataException("核查记录为空。");f.CheckedAt=DateTime.Now;message="投放核查已记录，未知项仍保持未知。";break;
            case "materialReview":
                var review=p.Deserialize<MaterialReview>(Json)??throw new InvalidDataException("素材确认记录为空。");
                if(!r.MaterialBatches.Any(x=>x.Kind==review.Kind&&x.Rows.Any(row=>row.Id==review.Id)))throw new InvalidDataException("素材不在本场导入记录中。");
                review.At=DateTime.Now;reviews=r.MaterialReviews.Where(x=>!(x.Kind==review.Kind&&x.Id==review.Id)).Append(review).ToList();message="已记录人工判断；没有操作后台素材。";break;
            case "followUp":
                var next=p.GetProperty("item").Deserialize<FollowUp>(Json)??throw new InvalidDataException("行动为空。");
                var old=r.FollowUps.FirstOrDefault(x=>x.Id==next.Id);
                next.CreatedAt=old?.CreatedAt??DateTime.Now;next.ReviewedAt=old?.ReviewedAt;
                if(p.TryGetProperty("markReviewed",out var mark)&&mark.GetBoolean())
                {
                    if(Decision.Evaluate(r,next).Status!="可复查"||string.IsNullOrWhiteSpace(next.Assessment))throw new InvalidDataException("需有完整前后区间并填写人工结论，才能标记已复查；数据不足可填写原因后归档。");
                    next.ReviewedAt=DateTime.Now;
                }
                if(old?.ReviewedAt!=null&&(old.ExecutedAt!=next.ExecutedAt||old.Metric!=next.Metric||old.ReviewMinutes!=next.ReviewMinutes||old.Title!=next.Title||old.Reason!=next.Reason||old.Expected!=next.Expected||old.StopCondition!=next.StopCondition||old.Confounders!=next.Confounders||old.Assessment!=next.Assessment||old.Archived!=next.Archived))throw new InvalidDataException("已人工复查的行动已锁定，请新建行动记录后续调整。");
                actions=r.FollowUps.Where(x=>x.Id!=next.Id).Append(next).ToList();message="行动已保存；只有实际执行时间和已确认数据才参与复查。";break;
            default:throw new InvalidDataException("不支持的决策记录操作。");
        }
        var candidate=new Room{Finance=r.Finance,Business=b,Facts=f,MaterialBatches=r.MaterialBatches,MaterialReviews=reviews,FollowUps=actions};
        Decision.Validate(candidate);
        r.Business=b;r.Facts=f;r.MaterialReviews=reviews;r.FollowUps=actions;r.Finance.Price=price;r.Finance.Retention=retention;
        return message;
    }
    public static string ObservationKey(string imageHash,IReadOnlyDictionary<string,decimal> candidates)
        =>candidates.ContainsKey("消耗")&&(candidates.ContainsKey("GMV")||candidates.ContainsKey("人数"))
        ?string.Join("|",candidates.OrderBy(x=>x.Key).Select(x=>x.Key+"="+x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        :imageHash;
}

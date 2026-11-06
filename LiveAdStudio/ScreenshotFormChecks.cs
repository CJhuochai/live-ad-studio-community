using LiveAdDesk;

namespace LiveAdStudio;

static class ScreenshotFormChecks
{
    public static void Run()
    {
        static void Check(bool ok,string name)
        {
            if(!ok)throw new InvalidDataException("Screenshot form: "+name);
        }
        static bool Reject(Action action)
        {
            try{action();return false;}catch(InvalidDataException){return true;}
        }
        var preview=ScreenshotForm.ReadPlan(["计划 ] D：000123456 日预算(元)：1,200.50 全域支付RO ] 目 标：3．8 编辑",
            "计划ID：000123456\n全域支付ROI目标\n3.8"]);
        Check(preview.Fields==new PlanFields("000123456","成交金额",1200.50m,3.8m),"label and decimal extraction");
        var cost=ScreenshotForm.ReadPlan(["全域成单人数成本目标（元）\n76.25\n日预算\n950"]);
        Check(cost.Fields==new PlanFields(null,"成单人数",950m,76.25m),"cost goal");
        var uncertain=ScreenshotForm.ReadPlan(["日预算：2丽\n全域支付ROI目标：6.0 → 4.0\n建议日预算：900"]);
        Check(uncertain.Fields.Budget is null&&uncertain.Fields.Target is null,"no guessed digits or historical targets");
        Check(ScreenshotForm.ReadPlan(["日预算","999"]).Fields.Budget is null,"no cross-image label match");
        var mixed=ScreenshotForm.ReadPlan(["全域支付ROI目标：4.0","全域成单人数成本目标：62"]);
        Check(mixed.Fields.Goal is null&&mixed.Fields.Target is null&&mixed.Review.Length>0,"conflicting goals");
        Check(Reject(()=>ScreenshotForm.ReadPlan(["计划ID:123456","计划ID:345678"])),"different plan images");
        Check(Reject(()=>ScreenshotForm.CheckSnapshotPlan("计划ID:345678","123456")),"different snapshot plan");
        Check(!OcrReader.Candidates("消耗\n2,8").ContainsKey("消耗"),"malformed decimal comma");
        Check(OcrReader.Candidates("消耗\n1,290.50")["消耗"]==1290.50m,"valid thousands grouping");
        var room=new Room();
        room.Finance.Price=750;room.Business.MaxCost=120;
        ScreenshotForm.ApplyPlan(room,preview.Fields);
        Check(room.Finance.Price==750&&room.Business.MaxCost==120&&room.Finance.Budget==1200.50m,"business settings preserved");
        ScreenshotForm.ApplyPlan(room,new PlanFields(null,"成单人数",null,null));
        Check(room.Finance.Target is null&&room.Finance.Budget==1200.50m,"old target cleared on unit change");
        room.Finance.Snapshots.Add(new());
        Check(Reject(()=>ScreenshotForm.ApplyPlan(room,new PlanFields("654321","成交金额",200,4)))&&
            room.Business.PlanId=="000123456"&&room.Finance.Goal=="成单人数"&&room.Finance.Budget==1200.50m,"wrong plan cannot modify historical room");
    }
}

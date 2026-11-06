using LiveAdDesk;

namespace LiveAdStudio;

static class ReviewEvidenceChecks
{
    public static void Run()
    {
        static void Check(bool passed,string name)
        {
            if(!passed)throw new InvalidDataException("Review evidence: "+name);
        }

        var room=new Room();
        room.Finance.Day=DateTime.Today.AddDays(-1);
        room.Finance.Account="演示账号";
        room.Business.PlanId="10001";
        var scope=ReviewEvidence.Scope(room);
        var at=scope.Start.AddHours(20);
        var first=new Snapshot{At=at.AddMinutes(-15),Scope=scope,Spend=2,Gmv=0,Buyers=0,Visits=10,Clicks=2};
        var baseline=new Snapshot{At=at,Scope=scope,Spend=4,Gmv=20,Buyers=1,Visits=30,Clicks=8};
        var last=new Snapshot{At=at.AddMinutes(15),Scope=scope,Spend=7.20m,Gmv=40,Buyers=2,Visits=40,Clicks=11};
        room.Finance.Snapshots=[first,baseline];
        var action=new FollowUp{FixedWindow=true,ExecutedAt=at,ReviewMinutes=15,Metric="消耗速度"};
        ReviewEvidence.Refresh(room,action);
        Check(action.Baseline?.Id==baseline.Id&&action.LockedResult is null,"baseline and missing cutoff");
        room.Finance.Snapshots.Add(last);
        ReviewEvidence.Refresh(room,action);
        var result=Decision.Evaluate(room,action);
        Check(result is {Before.Visits:20,After.Visits:10,After.Clicks:3}&&
            result.Recommendation?.Contains("进房未同步") == true,"fixed window funnel and advice");

        var batch=new MaterialBatch{PlanId=scope.PlanId,Start=scope.Start,End=scope.End,ScopeConfirmed=true,
            Rows=[new MaterialRow{Id="10002",Spend=1.45m}]};
        room.MaterialBatches.Add(batch);
        Check(ReviewEvidence.Reconcile(room) is {PlanSpend:7.20m,MaterialSpend:1.45m,Difference:null},"unconfirmed cutoff");
        batch.EvidenceScope=scope;batch.ComparedSnapshotAt=last.At;batch.ComparedSnapshotId=baseline.Id;
        Check(ReviewEvidence.Reconcile(room).Difference is null,"same timestamp and mismatched record");
        batch.ComparedSnapshotId=last.Id;
        Check(ReviewEvidence.Reconcile(room).Difference==5.75m,"confirmed difference");
        room.Finance.Account="另一账号";
        Check(ReviewEvidence.Reconcile(room).Difference is null&&Decision.Evaluate(room,action).After is null,"account mismatch");
        room.Finance.Account=scope.Account;
        room.Finance.Report=new IncrementReport{Account=scope.Account,Day=scope.Start,Gmv=100,Natural=80,Increment=20};
        Check(DecisionContext.Build(room).Contains("账号日增量报表"),"standalone account increment");
        room.Finance.Snapshots.Add(new Snapshot{At=at.AddMinutes(30),Scope=scope,Spend=90,Gmv=200,Buyers=5});
        ReviewEvidence.Refresh(room,action);
        Check(Decision.Evaluate(room,action).After==result.After&&ReviewEvidence.Reconcile(room).Difference is null,
            "later data leaves locked window and invalidates old reconciliation");
        room.Finance.Changes.Add(new(){At=at.AddMinutes(5),Text="换讲品"});
        Check(Decision.Evaluate(room,action).Status=="同期有变更","concurrent change");
    }
}

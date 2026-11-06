using System.Text.Json;
using LiveAdDesk;
namespace LiveAdStudio;
static class StudioChecks
{
    public static void Run(string[] args)
    {
        int n=0;void Check(bool b,string name){if(!b)throw new Exception(name);n++;}
        LiveAdConnect.Checks.Run(args[1]+".auth");
        UpgradeChecks.Run(args[1]+".upgrade");
        UpdateChecks.Run();
        ScreenshotFormChecks.Run();
        var financeArgs = args.Length>=4
            ? new[]{"--self-test",args[2],args[3],args[1]+".finance"}
            : new[]{"--self-test",args[1]+".finance"};
        Check(SelfTest.Run(financeArgs)==0,"合成财务回归");
        var r=new Room();r.Messages.Add(new(){Text="第一轮问题"});r.Messages.Add(new(){Role="assistant",Text="第一轮回答"});r.Messages.Add(new(){Text="第二轮跟进"});
        var request=JsonSerializer.Serialize(AiClient.BuildRequest(r,"model-from-account",[]));
        Check(request.Contains("第一轮问题")||request.Contains("\\u7B2C"),"历史消息存在");using(var doc=JsonDocument.Parse(request)){Check(doc.RootElement.GetProperty("input").GetArrayLength()==4,"单独数据上下文及多轮对话");Check(!doc.RootElement.GetProperty("store").GetBoolean()&&doc.RootElement.GetProperty("stream").GetBoolean(),"套餐请求契约");}
        var sse="data: {\"type\":\"response.output_text.delta\",\"delta\":\"完成\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":123,\"output_tokens\":9}}}\n\n";
        var result=AiClient.Read(new StringReader(sse),null,CancellationToken.None).GetAwaiter().GetResult();Check(result.Input==123&&result.Output==9,"官方usage计量");
        try{Rules.ValidateSnapshot(new(){Spend=-1});throw new Exception("负数未拒绝");}catch(InvalidDataException){n++;}
        try{Rules.ValidateSettings(new(){RequestLimit=0});throw new Exception("无上限未拒绝");}catch(InvalidDataException){n++;}
        var temp=Path.Combine(Path.GetTempPath(),"Studio-"+Guid.NewGuid()+".json");var w=new Workspace{Rooms=[r],Selected=r.Finance.Id};w.Save(temp);Check(Workspace.Load(temp).Current.Messages.Count==3,"工作区回读");File.Delete(temp);
        try{ImageStore.PathOf("../credentials.bin");throw new Exception("图片路径越界未拒绝");}catch(InvalidDataException){n++;}
        r.Calls.Add(new(){Status="failed"});r.Calls.Add(new(){Status="completed",InputTokens=12,OutputTokens=5});Check(r.ExportText().Contains("尝试2次"),"失败尝试计量");
        var broken=new Workspace{Rooms=[new Room()]};broken.Rooms[0].Finance.Snapshots=null!;
        try{broken.Validate();throw new Exception("损坏备份未拒绝");}catch(InvalidDataException){n++;}
        w.Selected="不存在";w.Validate();Check(w.Current==r,"恢复无效选中场次");
        var archive=temp+".zip";WorkspaceArchive.Save(w,archive);var restored=WorkspaceArchive.Read(archive);Check(restored.Data.Current.Messages.Count==3,"ZIP备份恢复");File.Delete(archive);
        var capture=new CaptureService();var revision=capture.Revision;capture.Clear();capture.RevokeConsent();Check(capture.Revision==revision+2,"区域授权版本失效");
        using(var bitmap=new Bitmap(16,16))using(var ms=new MemoryStream())
        {
            bitmap.Save(ms,System.Drawing.Imaging.ImageFormat.Png);var id=ImageStore.Add(ms.ToArray());
            try{r.Messages[^1].Images.Add(id);using var imgRequest=JsonDocument.Parse(JsonSerializer.Serialize(AiClient.BuildRequest(r,"test",[id])));Check(imgRequest.RootElement.GetProperty("input")[3].GetProperty("content")[1].GetProperty("image_url").GetString()!.StartsWith("data:image/jpeg;base64,"),"图片请求契约");WorkspaceArchive.Save(w,archive);Check(WorkspaceArchive.Read(archive).Images.ContainsKey(id),"备份含图片");}finally{r.Messages[^1].Images.Clear();File.Delete(ImageStore.PathOf(id));if(File.Exists(archive))File.Delete(archive);}
        }
        try{AiClient.Read(new StringReader("data: {\"type\":\"response.output_text.delta\",\"delta\":\"半截\"}\n\n"),null,CancellationToken.None).GetAwaiter().GetResult();throw new Exception("中断流误记成功");}catch(LiveAdConnect.AuthError){n++;}
        File.WriteAllText(args[1],$"PASS: {n} studio checks + 17 auth checks + upgrade and update checks. Synthetic finance regression passed. Workbook read: {(args.Length>=4?"executed":"not supplied")}.");
    }
}

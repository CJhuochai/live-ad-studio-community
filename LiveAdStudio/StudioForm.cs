using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LiveAdConnect;
using LiveAdDesk;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LiveAdStudio;
sealed class StudioForm : Form
{
    readonly WebView2 web=new(){Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(245,247,252)};
    readonly AuthClient auth=new();
    Workspace workspace=Workspace.Load();
    readonly CaptureService capture=new();
    readonly NotifyIcon tray=new(){Text="Live Ad Studio"};
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly System.Windows.Forms.Timer updateTimer=new(){Interval=15*60*1000};
    UpdateOffer? availableUpdate;
    bool checkingUpdate;
    readonly List<string> pending=[];
    readonly JsonSerializerOptions json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    List<ModelItem> models=[];
    CancellationTokenSource? operation;
    bool ready,busy,monitoring,closing,limitNotified;
    DateTime nextCapture,lastAnalysis=DateTime.MinValue;
    string lastImage="",lastHash="",lastSentObservation="",rawOcr="",materialQuery="";
    Dictionary<string,decimal> candidates=[];
    Room R=>workspace.Current;
    public StudioForm()
    {
        using(var iconStream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveAdStudio.Assets.app.ico")??throw new InvalidDataException("缺少程序图标。"))Icon=new Icon(iconStream,32,32);
        tray.Icon=Icon;tray.Visible=true;FormClosed+=(_,_)=>Icon?.Dispose();
        AutoScaleMode=AutoScaleMode.Dpi;Text=$"Live Ad Studio {AppUpdate.CurrentVersion.ToString(3)} · 直播决策工作台";Width=1440;Height=940;MinimumSize=new Size(1140,780);StartPosition=FormStartPosition.CenterScreen;WindowState=FormWindowState.Maximized;
        Controls.Add(web);tray.DoubleClick+=(_,_)=>{Show();WindowState=FormWindowState.Normal;Activate();};Shown+=async(_,_)=>await Initialize();
        timer.Tick+=async(_,_)=>await Tick();
        updateTimer.Tick+=async(_,_)=>await CheckForUpdate();
        FormClosed+=(_,_)=>updateTimer.Dispose();
        SystemEvents.SessionSwitch+=SessionSwitch;SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        FormClosing+=(_,_)=>{closing=true;Stop("工具关闭");operation?.Cancel();timer.Dispose();tray.Dispose();SystemEvents.SessionSwitch-=SessionSwitch;SystemEvents.DisplaySettingsChanged-=DisplayChanged;auth.Dispose();};
    }
    async Task Tick()
    {
        if(!monitoring||busy||DateTime.Now<nextCapture)return;
        await Run(async()=>{
            await CaptureFrame();if(!monitoring)return;nextCapture=DateTime.Now.AddSeconds(R.CaptureSeconds);
            if(R.Calls.Count>=R.RequestLimit)
            {
                if(!limitNotified){limitNotified=true;Event("AI次数达到上限：继续本地采集，不再上传或自动分析。");Save();tray.ShowBalloonTip(5000,"AI次数上限已到","仍在本地采集；不会继续上传。需要时在设置中调整次数。",ToolTipIcon.Info);}return;
            }
            var key=DecisionCommands.ObservationKey(lastHash,candidates);
            if(DateTime.Now-lastAnalysis>=TimeSpan.FromMinutes(R.AnalysisMinutes)&&key!=lastSentObservation)
            {
                lastAnalysis=DateTime.Now;await Analyze("请结合本次区域截图、最新已确认数字、经营约束与未复查行动，给出当前一个主要判断、依据、建议动作、成本代价及复查条件。OCR尚未确认，不能当成账本事实。",[lastImage],"自动盯盘");lastSentObservation=key;
            }
        });
    }
    void SessionSwitch(object sender,SessionSwitchEventArgs e){if(e.Reason==SessionSwitchReason.SessionLock && !IsDisposed)BeginInvoke(()=>{Stop("锁屏已暂停");Publish();});}
    void DisplayChanged(object? sender,EventArgs e){if(!IsDisposed)BeginInvoke(()=>{Stop("显示器布局变化，请重新选区");capture.Clear();Publish();});}
    async Task Initialize()
    {
        try
        {
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Workspace.Root,"webview"),new CoreWebView2EnvironmentOptions("--disable-gpu"));await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.AreDevToolsEnabled=false;web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            const string appUrl="https://livead.local/index.html";
            web.CoreWebView2.NavigationStarting+=(_,e)=>{if(e.Uri!=appUrl)e.Cancel=true;};web.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
            web.CoreWebView2.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived+=async(_,e)=>
            {
                if(e.Source!=appUrl)return;
                try
                {
                    var text=e.WebMessageAsJson;if(text.Length>18*1024*1024)throw new InvalidDataException("消息过大。");
                    using var doc=JsonDocument.Parse(text);var message=doc.RootElement.Clone();var action=message.GetProperty("action").GetString()??"";
                    if(action=="ready"){ready=true;Publish();updateTimer.Start();_ = CheckForUpdate();if(auth.StartupError.Length>0)Toast(auth.StartupError+" 本地记录与计算仍可使用，请重新连接。",true);await Run(LoadModelsIfPossible);return;}
                    if(action=="pause"){Stop("手动暂停");Publish();return;}
                    if(action=="cancel"){Stop("取消当前操作");operation?.Cancel();Publish();return;}
                    if(action=="update"&&monitoring){Toast("请先暂停盯盘，再更新程序。",true);return;}
                    await Run(()=>Command(action,message.TryGetProperty("data",out var p)?p.Clone():default));
                }
                catch(Exception ex){Toast(SafeError(ex),true);}
            };
            var uiDir=Path.Combine(Workspace.Root,"ui-v21");Directory.CreateDirectory(uiDir);
            foreach(var asset in new[]{"index.html","decision.js","decision.css","app-logo.png"}){using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveAdStudio.Ui."+asset)??throw new Exception("缺少界面资源");using var output=File.Create(Path.Combine(uiDir,asset));await stream.CopyToAsync(output);}
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("livead.local",uiDir,CoreWebView2HostResourceAccessKind.DenyCors);web.CoreWebView2.Navigate(appUrl);
        }
        catch
        {
            if(MessageBox.Show("界面运行时未就绪。请确认已安装Microsoft Edge WebView2 Runtime。是否打开微软官方安装页？","运行环境",MessageBoxButtons.YesNo)==DialogResult.Yes)Open("https://developer.microsoft.com/microsoft-edge/webview2/");Close();
        }
    }
    static string SafeError(Exception e)=>e is AuthError or InvalidDataException or InvalidOperationException?e.Message:e is OperationCanceledException?"操作已取消或超时；没有自动重试。":"操作未完成，请检查文件、网络或运行环境；未输出凭据或原始网络错误。";
    async Task Run(Func<Task> fn)
    {
        if(busy){Toast("当前操作尚未结束，请稍候或点击取消。");return;}busy=true;operation=new();Publish();
        try{await fn();}
        catch(Exception e){if(monitoring)Stop("遇到错误，已暂停："+SafeError(e));Toast(SafeError(e),true);}
        finally{busy=false;operation?.Dispose();operation=null;if(!closing)Publish();}
    }
    CancellationToken Ct=>operation?.Token??CancellationToken.None;
    void Post(object value){if(ready&&!closing&&!web.IsDisposed)web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value,json));}
    void Toast(string text,bool error=false)=>Post(new{type="toast",text,error});
    void Save(){workspace.Save();}
    async Task CheckForUpdate()
    {
        if(checkingUpdate||closing)return;
        checkingUpdate=true;
        try
        {
            availableUpdate=await AppUpdate.CheckAsync();
            Post(new{type="update",version=availableUpdate?.Version.ToString(3)});
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A transient lookup failure leaves the last confirmed offer in place.
        }
        finally{checkingUpdate=false;}
    }

    async Task UpdateAndRestart()
    {
        var offer=availableUpdate??throw new InvalidOperationException("目前没有可用更新。");
        var exe=Application.ExecutablePath;
        var staged=exe+"."+Guid.NewGuid().ToString("N")+".update";
        var handedOff=false;
        try
        {
            Toast($"正在下载并校验 {offer.Version}，完成后将重启。");
            await AppUpdate.PrepareAsync(offer,staged,Ct);
            Ct.ThrowIfCancellationRequested();
            Save();
            WorkspaceArchive.Save(workspace,Path.Combine(Workspace.Root,"before-update.zip"));
            AppUpdate.InstallAfterExit(exe,staged);
            handedOff=true;
            Close();
        }
        finally
        {
            if(!handedOff)File.Delete(staged);
        }
    }
    void Event(string text){R.Events.Add($"{DateTime.Now:MM-dd HH:mm:ss} {text}");if(R.Events.Count>500)R.Events.RemoveRange(0,R.Events.Count-500);}
    void Stop(string reason){var was=monitoring;monitoring=false;timer.Stop();if(ready){Event(reason);Save();}if(was&&!closing)tray.ShowBalloonTip(5000,"盯盘已暂停",reason,ToolTipIcon.Info);}
    async Task LoadModelsIfPossible()
    {
        if(auth.Session?.AccessToken.Length>0 && (auth.Session.CanUse||auth.Session.RefreshToken.Length>0)){var session=await auth.Authorized(Ct);models=await LiveModels(session,Ct);if(!models.Any(m=>m.Slug==workspace.Model))workspace.Model=models.FirstOrDefault()?.Slug??"";Save();}
    }
    static async Task<List<ModelItem>> LiveModels(Credentials s,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,"https://api.openai.com/v1/models");req.Headers.Authorization=new("Bearer",s.AccessToken);using var response=await AuthClient.Http.SendAsync(req,ct);
        if(!response.IsSuccessStatusCode)throw new AuthError($"模型目录读取失败（HTTP {(int)response.StatusCode}），请核对授权和网络。");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var result=new List<ModelItem>();
        if(doc.RootElement.TryGetProperty("models",out var a))foreach(var m in a.EnumerateArray())if(m.TryGetProperty("visibility",out var v)&&v.GetString()=="list"&&m.TryGetProperty("slug",out var slug)&&slug.GetString() is {Length:>0} id)result.Add(new(id,m.TryGetProperty("display_name",out var name)?name.GetString()??id:id));
        if(result.Count==0)throw new AuthError("此账号没有返回可选模型，不能以登录成功代替推理权限。");return result;
    }
    async Task Command(string action,JsonElement p)
    {
        string Text(string key)=>p.TryGetProperty(key,out var v)?v.GetString()??"":"";
        switch(action)
        {
            case "business": case "facts": case "materialReview": case "followUp":
                var message=DecisionCommands.Apply(R,action,p);
                if(action is "business" or "facts"){capture.RevokeConsent();Stop("经营目标或核查记录已更新，请确认采集范围后再开始。");}
                Event(message);Save();Post(new{type="dialogSaved"});Toast(message);break;
            case "materialQuery":var query=Text("query");if(query.Length>100)throw new InvalidDataException("搜索条件最多100字。");materialQuery=query;break;
            case "materials":ImportMaterials();break;
            case "update":await UpdateAndRestart();break;
            case "login":Stop("连接账号");await auth.Login(t=>Post(new{type="progress",text=t}),Ct);await LoadModelsIfPossible();Toast("ChatGPT已连接，可以开始对话。");break;
            case "logout":Stop("退出账号");await auth.Disconnect(Ct);models=[];Toast("已退出本机授权。");break;
            case "models":await LoadModelsIfPossible();break;
            case "model":if(!models.Any(m=>m.Slug==Text("model")))throw new InvalidDataException("请选择账号目录中的模型。");workspace.Model=Text("model");Save();break;
            case "usage":Open("https://chatgpt.com/settings/usage");break;
            case "source":Open("https://support.oceanengine.com/support/content/root?spaceId=174");break;
            case "newRoom":Stop("新建场次");var room=new Room();workspace.Rooms.Add(room);workspace.Selected=room.Finance.Id;ClearCapture();Save();Publish();Post(new{type="settings"});break;
            case "switchRoom":Stop("切换场次");var id=Text("id");if(!workspace.Rooms.Any(r=>r.Finance.Id==id))throw new InvalidDataException("场次不存在。");workspace.Selected=id;ClearCapture();Save();break;
            case "settings":SaveSettings(p);Post(new{type="dialogSaved"});break;
            case "snapshot":
                var q=p.GetProperty("snapshot").Deserialize<Snapshot>(json)??throw new InvalidDataException("快照为空。");Rules.ValidateSnapshot(q,scope:R.Finance);
                if(q.Source=="截图核对"&&(!p.TryGetProperty("confirmed",out var confirmed)||!confirmed.GetBoolean()))throw new InvalidDataException("请先核对截图数字。");
                if(R.Finance.Snapshots.Any(s=>s.At==q.At))throw new InvalidDataException("该时间已有快照，请调整时间。");R.Finance.Snapshots.Add(q);Save();Post(new{type="dialogSaved"});Toast("累计快照已保存。");break;
            case "deleteSnapshot":
                var at=p.GetProperty("at").GetDateTime();if(MessageBox.Show("删除选中的快照？","删除确认",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;R.Finance.Snapshots.RemoveAll(s=>s.At==at);Save();break;
            case "change":var note=Text("text").Trim();if(note.Length==0||note.Length>2000)throw new InvalidDataException("请填写1—2000字变更内容。");R.Finance.Changes.Add(new(){Text=note});Event("记录调整："+note);Save();Post(new{type="dialogSaved"});break;
            case "chooseRegion":Stop("重新选择区域");await ChooseRegion();break;
            case "capture":await CaptureFrame();break;
            case "start":
                if(!p.TryGetProperty("consent",out var consent)||!consent.GetBoolean())throw new InvalidDataException("请明确同意定时采集并发送选定区域至OpenAI。");
                if(!p.TryGetProperty("revision",out var rev)||rev.GetInt32()!=capture.Revision||Text("roomId")!=R.Finance.Id)throw new InvalidDataException("截图区域或场次已变化，请重新核对并勾选授权。");
                if(capture.Area.IsEmpty)throw new InvalidDataException("请先选择数据区域并核对预览。");
                if(workspace.Model.Length==0)throw new InvalidDataException("请先连接并选择模型。");await auth.Authorized(Ct);
                if(R.Calls.Count>=R.RequestLimit)throw new InvalidDataException("本场请求次数已达上限，请先核对用量并调整设置。");
                monitoring=true;limitNotified=false;lastSentObservation="";nextCapture=DateTime.Now;lastAnalysis=DateTime.MinValue;Event($"开始盯盘：采集{R.CaptureSeconds}秒，分析{R.AnalysisMinutes}分钟；已明确同意区域自动发送。");Save();timer.Start();Toast("盯盘已开始，请保持目标数据区域可见。");
                if(Bounds.IntersectsWith(capture.Area)){WindowState=FormWindowState.Minimized;tray.ShowBalloonTip(5000,"盯盘已开始","工作台已最小化，避免遮挡数据区域。点击任务栏或双击托盘可恢复。",ToolTipIcon.Info);}break;
            case "addFiles":using(var d=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp",Multiselect=true})if(d.ShowDialog(this)==DialogResult.OK)foreach(var f in d.FileNames.Take(2-pending.Count))AddImage(File.ReadAllBytes(f));break;
            case "paste":if(!Clipboard.ContainsImage())throw new InvalidDataException("剪贴板没有图片，请先截图。");using(var image=Clipboard.GetImage()!)using(var ms=new MemoryStream()){image.Save(ms,System.Drawing.Imaging.ImageFormat.Png);AddImage(ms.ToArray());}break;
            case "imageData":var raw=Text("data");var comma=raw.IndexOf(',');if(!raw.StartsWith("data:image/",StringComparison.Ordinal)||comma<0)throw new InvalidDataException("图片格式无效。");AddImage(Convert.FromBase64String(raw[(comma+1)..]));break;
            case "removeImage":pending.Remove(Text("id"));break;
            case "useCapture":if(lastImage.Length==0)throw new InvalidDataException("请先采集区域截图。");if(!pending.Contains(lastImage)){if(pending.Count>=2)throw new InvalidDataException("每次最多附两张图片。");pending.Add(lastImage);}break;
            case "chat":var text=Text("text").Trim();if(text.Length==0&&pending.Count==0)throw new InvalidDataException("请输入问题或添加图片。");if(text.Length>10000)throw new InvalidDataException("单次问题请控制在10000字内。");await Analyze(text.Length==0?"请分析这张投流截图，指出最需要关注的问题。":text,pending.ToList(),"手动对话");pending.Clear();break;
            case "analyzeCapture":if(lastImage.Length==0)throw new InvalidDataException("请先选择区域并采集截图。");await Analyze("请结合当前区域截图、已确认快照和本场历史，给出简洁的盯盘建议。",[lastImage],"手动截图");break;
            case "orders":ImportOrders();break;
            case "increment":ImportReport();break;
            case "copy":Clipboard.SetText(R.ExportText());Toast("已复制本场分析摘要，不含买家UID和订单号。");break;
            case "backup":Backup();break;
            case "restore":Restore();break;
            case "exportCsv":ExportCsv();break;
            default:throw new InvalidDataException("不支持的操作。");
        }
    }
    void ClearCapture(){capture.Clear();lastImage="";lastHash="";lastSentObservation="";rawOcr="";candidates=[];pending.Clear();materialQuery="";}
    void AddImage(byte[] bytes){if(pending.Count>=2)throw new InvalidDataException("每次最多附两张图片，请先发送或移除。");pending.Add(ImageStore.Add(bytes));Toast("图片已添加，发送前可移除。");}
    async Task ChooseRegion()
    {
        Hide();try{await Task.Delay(200);using var picker=new RegionPicker();if(picker.ShowDialog()!=DialogResult.OK)return;await Task.Delay(120);capture.Set(picker.Selection);lastImage=capture.Capture();lastHash=ImageStore.Hash(lastImage);rawOcr="";candidates=[];}catch{ClearCapture();throw;}finally{Show();Activate();}Toast("区域预览已生成；开始前请确认画面不含无关隐私。");
    }
    async Task CaptureFrame()
    {
        var image=capture.Capture();var hash=ImageStore.Hash(image);
        if(hash==lastHash){File.Delete(ImageStore.PathOf(image));if(rawOcr.Length>0)return;image=lastImage;}
        var previous=lastImage;lastImage=image;lastHash=hash;rawOcr="";candidates=[];
        if(previous.Length>0&&previous!=lastImage&&!pending.Contains(previous)&&!workspace.Rooms.Any(room=>room.Messages.Any(m=>m.Images.Contains(previous))))File.Delete(ImageStore.PathOf(previous));
        try{rawOcr=await OcrReader.Read(ImageStore.PathOf(image));candidates=OcrReader.Candidates(rawOcr);}catch{rawOcr="本地OCR暂不可用；仍可发送图片给支持图像的模型。";}
        Event("已采集区域截图，识别候选尚未确认。");Save();Post(new{type="captured"});Publish();
    }
    async Task Analyze(string text,List<string> images,string kind)
    {
        if(R.Calls.Count>=R.RequestLimit)throw new InvalidDataException("本场请求次数已达设置上限，请先查看用量。");
        if(workspace.Model.Length==0)throw new InvalidDataException("请先连接ChatGPT并选择模型。");
        var room=R;var log=new CallLog{Model=workspace.Model,Kind=kind};room.Calls.Add(log);room.Messages.Add(new(){Text=text,Images=images});Save();Publish();Post(new{type="streamStart"});
        try
        {
            var result=await AiClient.Send(auth,room,workspace.Model,images,d=>Post(new{type="delta",text=d}),Ct);
            log.Status="completed";log.InputTokens=result.Input;log.OutputTokens=result.Output;room.Messages.Add(new(){Role="assistant",Text=result.Text});
            room.Events.Add($"{DateTime.Now:MM-dd HH:mm:ss} {kind}分析完成；usage{(result.Input.HasValue?"已返回":"未报告")}。");
            if(kind=="自动盯盘"&&WindowState==FormWindowState.Minimized)tray.ShowBalloonTip(5000,"新的盯盘分析",result.Text[..Math.Min(100,result.Text.Length)].Replace("**",""),ToolTipIcon.Info);
        }
        catch(Exception e){log.Status=e is OperationCanceledException?"cancelled":"failed";log.Note=SafeError(e);room.Messages.Add(new(){Role="notice",Text="这次分析未完成："+SafeError(e)});throw;}
        finally{Save();Post(new{type="streamEnd"});}
    }
    void SaveSettings(JsonElement p)
    {
        var old=R.Finance;var finance=p.GetProperty("finance").Deserialize<Session>(json)??throw new InvalidDataException("设置为空。");
        if(old.Orders.Count>0&&finance.Account!=old.Account)throw new InvalidDataException("此场次已有订单，更换账号请新建场次。");
        var check=new Room{Finance=finance,CaptureSeconds=p.GetProperty("captureSeconds").GetInt32(),AnalysisMinutes=p.GetProperty("analysisMinutes").GetInt32(),RequestLimit=p.GetProperty("requestLimit").GetInt32()};Rules.ValidateSettings(check);
        finance.Id=old.Id;finance.Orders=old.Orders;finance.Snapshots=old.Snapshots;finance.Changes=old.Changes;finance.Report=old.Report;
        finance.Changes.Add(new(){Text=$"设置调整：目标{old.Goal}→{finance.Goal}；预算{Calc.N(old.Budget)}→{Calc.N(finance.Budget)}；目标值{Calc.N(old.Target)}→{Calc.N(finance.Target)}；主推{old.Product}→{finance.Product}"});
        if(old.Account!=finance.Account||old.Day!=finance.Day||old.Start!=finance.Start||old.End!=finance.End)R.Facts=new();
        R.Finance=finance;R.CaptureSeconds=check.CaptureSeconds;R.AnalysisMinutes=check.AnalysisMinutes;R.RequestLimit=check.RequestLimit;capture.RevokeConsent();Stop("设置已更新，请确认后重新开始盯盘");Save();Toast("设置已保存。");
    }
    void ImportOrders()
    {
        using var d=new OpenFileDialog{Filter="来客成交明细|*.xlsx"};if(d.ShowDialog(this)!=DialogResult.OK)return;var list=ExcelImport.Orders(d.FileName);var accounts=list.Select(o=>o.Account).Distinct().ToArray();var account=R.Finance.Account;
        if(account.Length==0||!accounts.Contains(account))
        {
            if(accounts.Length==1)account=accounts[0];else {using var choose=new Form{Text="选择订单账号",Size=new Size(460,180),StartPosition=FormStartPosition.CenterParent};var cb=new ComboBox{DataSource=accounts,DropDownStyle=ComboBoxStyle.DropDownList,Left=20,Top=20,Width=390};var ok=new Button{Text="选择",Left=290,Top=70,DialogResult=DialogResult.OK};choose.Controls.Add(cb);choose.Controls.Add(ok);if(choose.ShowDialog(this)!=DialogResult.OK)return;account=cb.SelectedItem?.ToString()??"";}
            if(R.Finance.Orders.Count>0 && R.Finance.Account!=account)throw new InvalidDataException("请为其他账号新建场次。");
        }
        var incoming=list.Where(o=>o.Account==account).ToList();var demo=new Session{Day=R.Finance.Day,Start=R.Finance.Start,End=R.Finance.End};demo.Merge(incoming);var m=Metrics.From(demo);
        var regressions=incoming.Count(o=>R.Finance.Orders.Any(old=>old.Id==o.Id&&(old.Refund>o.Refund||(old.Redeemed&&!o.Redeemed))));
        var warning=regressions>0?$"\n注意：{regressions}笔退款或核销状态将回退，可能导入了旧文件，请核对。":"";
        if(MessageBox.Show($"账号：{account}\n当前统计范围直播支付{m.Count}笔，实收{m.Gross:N2}，退款{m.Refund:N2}，保留{m.Retained:N2}。\n按订单号更新，其他日期/渠道不纳入本场。{warning}\n确认导入？","订单预览",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        if(R.Finance.Account!=account)R.Facts=new();
        R.Finance.Account=account;R.Finance.Merge(incoming);R.OrdersImportedAt=DateTime.Now;Event($"导入订单{incoming.Count}笔，退款与核销以本次文件更新。");Save();Toast("订单已导入，查看数据复盘。");
    }
    void ImportMaterials()
    {
        if(R.Business.PlanId.Length==0)throw new InvalidDataException("请先在经营目标中填写当前计划ID，再导入素材报表。");
        using var d=new OpenFileDialog{Filter="本地推视频/图文效果报表|*.xlsx"};if(d.ShowDialog(this)!=DialogResult.OK)return;
        var batch=MaterialImport.Read(d.FileName);batch.PlanId=R.Business.PlanId;batch.Start=R.Finance.Start??R.Finance.Day.Date;batch.End=R.Finance.End??R.Finance.Day.Date.AddDays(1);
        Decision.Validate(new(){Finance=R.Finance,Business=R.Business,Facts=R.Facts,MaterialBatches=[batch]});
        var missing=batch.Rows.Count(r=>r.Title.Length==0||!r.Created.HasValue);
        if(MessageBox.Show($"{batch.Kind} {batch.Rows.Count}条，消耗{batch.Rows.Sum(r=>r.Spend):N2}元，GMV{batch.Rows.Sum(r=>r.Gmv):N2}元。\n{missing}条标题或创建时间缺失，将保留为未知。\n\n文件没有计划ID和统计日期：请核对它来自计划{batch.PlanId}，统计范围{batch.Start:yyyy-MM-dd HH:mm}至{batch.End:yyyy-MM-dd HH:mm}（不含结束）。\n范围一致才点“是”。此报表不能证明审核通过或曝光为0。","核对素材报表范围",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        batch.ScopeConfirmed=true;Decision.AddReport(R,batch);Event($"导入{batch.Kind}素材报表{batch.Rows.Count}条；统计范围由用户核对。");Save();Toast("已导入素材体检；相同范围仅取该类型最新批次，不重复累计。");
    }
    void ImportReport()
    {
        using var d=new OpenFileDialog{Filter="增量价值报表|*.xlsx"};if(d.ShowDialog(this)!=DialogResult.OK)return;var r=ExcelImport.Report(d.FileName);
        if(r.Day.Date!=R.Finance.Day.Date)throw new InvalidDataException("增量报表日期与场次日期不符。");
        if(MessageBox.Show($"账号/抖音号：{r.Account}\n总成交{r.Gmv:N2}，自然{r.Natural:N2}，增量{r.Increment:N2}。\n确认与当前场次同账号？","核对报表",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;R.Finance.Report=r;Save();
    }
    void Backup()
    {
        using var d=new SaveFileDialog{Filter="完整工作区备份|*.zip",FileName="LiveAdStudio-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip"};if(d.ShowDialog(this)!=DialogResult.OK)return;WorkspaceArchive.Save(workspace,d.FileName);Toast("工作区及对话图片已备份，不含授权凭据。");
    }
    void Restore()
    {
        using var d=new OpenFileDialog{Filter="工作区或旧版数据备份|*.zip;*.json"};if(d.ShowDialog(this)!=DialogResult.OK)return;
        Workspace incoming;var images=new Dictionary<string,byte[]>();
        if(Path.GetExtension(d.FileName).Equals(".zip",StringComparison.OrdinalIgnoreCase)){var archive=WorkspaceArchive.Read(d.FileName);incoming=archive.Data;images=archive.Images;}
        else {using var j=JsonDocument.Parse(File.ReadAllText(d.FileName));if(j.RootElement.TryGetProperty("Version",out var v)&&v.GetInt32()==1){var old=Store.Load(d.FileName);incoming=new(){Rooms=old.Sessions.Select(s=>new Room{Finance=s}).ToList()};if(incoming.Rooms.Count==0)throw new InvalidDataException("旧备份没有场次。");incoming.Selected=incoming.Rooms[0].Finance.Id;}else incoming=Workspace.Load(d.FileName);}
        incoming.Validate();
        if(MessageBox.Show("恢复该工作区？当前工作区会先保存一份恢复前备份。旧备份不含图片时，图片会显示缺失。","恢复",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        Stop("恢复工作区");workspace.Save(Path.Combine(Workspace.Root,"before-restore-"+DateTime.Now.ToString("yyyyMMddHHmmss")+".json"));
        foreach(var image in images){var path=ImageStore.PathOf(image.Key);if(File.Exists(path)&&!File.ReadAllBytes(path).SequenceEqual(image.Value))throw new InvalidDataException("备份图片标识与本地不同内容冲突，未替换工作区。");}
        Directory.CreateDirectory(Path.Combine(Workspace.Root,"images"));foreach(var image in images)if(!File.Exists(ImageStore.PathOf(image.Key)))File.WriteAllBytes(ImageStore.PathOf(image.Key),image.Value);
        workspace=incoming;ClearCapture();Save();
    }
    void ExportCsv()
    {
        using var d=new SaveFileDialog{Filter="商品汇总|*.csv",FileName="商品汇总.csv"};if(d.ShowDialog(this)!=DialogResult.OK)return;
        string Escape(string text)=>"\""+(text.TrimStart().StartsWith('=')||text.TrimStart().StartsWith('+')||text.TrimStart().StartsWith('-')||text.TrimStart().StartsWith('@')?"'":"")+text.Replace("\"","\"\"")+"\"";
        var lines=new List<string>{"商品,支付订单,实收,退款,保留"};lines.AddRange(R.Finance.LiveOrders.Where(o=>o.PaidTime.HasValue).GroupBy(o=>o.Product).Select(g=>string.Join(",",Escape(g.Key),g.Count(),g.Sum(o=>o.Paid).ToString(System.Globalization.CultureInfo.InvariantCulture),g.Sum(o=>o.Refund).ToString(System.Globalization.CultureInfo.InvariantCulture),g.Sum(o=>o.Paid-o.Refund).ToString(System.Globalization.CultureInfo.InvariantCulture))));File.WriteAllLines(d.FileName,lines,new UTF8Encoding(true));Toast("汇总已导出。");
    }
    void Publish()
    {
        if(!ready||closing)return;
        Post(StateView.Build(workspace,busy,monitoring,models,Mask(auth.Session?.Email??""),auth.Session?.CanUse==true,
            new{selected=!capture.Area.IsEmpty,revision=capture.Revision,title=capture.Title,size=$"{capture.Area.Width} × {capture.Area.Height}",thumb=lastImage.Length>0?ImageStore.Thumb(lastImage):"",raw=rawOcr,candidates,trigger=candidates.ContainsKey("消耗")&&(candidates.ContainsKey("GMV")||candidates.ContainsKey("人数"))?"按OCR候选变化触发（未入账）":"OCR不足，按画面变化触发"},
            pending.Select(i=>new{id=i,thumb=ImageStore.Thumb(i)}).ToArray(),materialQuery));
    }
    static string Mask(string value){var at=value.IndexOf('@');return at>0?value[..1]+"***"+value[at..]:value.Length>0?"已授权账号":"";}
    static void Open(string url)=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
}

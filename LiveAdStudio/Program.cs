using LiveAdConnect;
namespace LiveAdStudio;
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if(args.Length>=2&&args[0]=="--check")
        {
            try{StudioChecks.Run(args);Environment.ExitCode=0;}catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e.GetType().Name+" "+e.Message);Environment.ExitCode=1;}return;
        }
        ApplicationConfiguration.Initialize();using var mutex=new Mutex(true,"Local\\LiveAdStudio-"+Environment.UserName,out var first);
        if(!first){MessageBox.Show("直播工作台已经打开，请切换到已有窗口。");return;}
        try{Application.Run(new StudioForm());}catch(AuthError e){MessageBox.Show(e.Message,"授权文件提示");}catch{MessageBox.Show("工作区无法启动。原数据未被覆盖，请检查运行环境或恢复备份。","启动提示");}
    }
}

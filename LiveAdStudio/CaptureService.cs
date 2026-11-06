using System.Runtime.InteropServices;
using System.Text;
namespace LiveAdStudio;
sealed class CaptureService
{
    public Rectangle Area {get;private set;}
    public nint Target {get;private set;}
    public string Title {get;private set;}="";
    public int Revision {get;private set;}
    public void RevokeConsent()=>Revision++;
    [DllImport("user32.dll")]static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")]static extern nint GetAncestor(nint w,uint flags);
    [DllImport("user32.dll")]static extern bool IsWindow(nint h);
    [DllImport("user32.dll")]static extern bool IsIconic(nint h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(nint h,StringBuilder text,int length);
    public static string WindowTitle(nint hwnd){var b=new StringBuilder(512);GetWindowText(hwnd,b,512);return b.ToString();}
    static nint RootAt(Point point)=>GetAncestor(WindowFromPoint(point),2);
    public void Set(Rectangle area)
    {
        if(area.Width<160||area.Height<100)throw new InvalidDataException("区域太小，请至少框选160×100像素的数据面板。");
        var target=RootAt(new(area.Left+area.Width/2,area.Top+area.Height/2));var title=WindowTitle(target);
        if(target==0||title.Length==0)throw new InvalidDataException("未识别到目标窗口，请重新选择数据面板。");
        if(new[]{"登录","授权","Sign in","password"}.Any(s=>title.Contains(s,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("该窗口可能是登录/授权页面，请改选投放数据页。");
        Area=area;Target=target;Title=title;Revision++;
    }
    public void Clear(){Area=Rectangle.Empty;Target=0;Title="";Revision++;}
    public string Capture()
    {
        if(Area.IsEmpty||!IsWindow(Target)||IsIconic(Target))throw new InvalidOperationException("目标窗口已关闭、最小化或尚未选择区域。");
        if(WindowTitle(Target)!=Title)throw new InvalidOperationException("目标窗口标题已变化，为避免采集其他页面已暂停。请重新确认区域。");
        foreach(var p in new[]{new Point(Area.Left+3,Area.Top+3),new Point(Area.Right-3,Area.Top+3),new Point(Area.Left+3,Area.Bottom-3),new Point(Area.Right-3,Area.Bottom-3),new Point(Area.Left+Area.Width/2,Area.Top+Area.Height/2)})
            if(RootAt(p)!=Target)throw new InvalidOperationException("数据区域可能被其他窗口遮挡，已暂停采集。请保持区域可见后再开始。");
        using var bmp=new Bitmap(Area.Width,Area.Height);using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(Area.Location,Point.Empty,Area.Size);
        using var ms=new MemoryStream();bmp.Save(ms,System.Drawing.Imaging.ImageFormat.Png);return ImageStore.Add(ms.ToArray());
    }
}
sealed class RegionPicker : Form
{
    Point origin;Rectangle selected;bool dragging;
    public Rectangle Selection {get;private set;}
    public RegionPicker()
    {
        FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=SystemInformation.VirtualScreen;
        TopMost=true;BackColor=Color.FromArgb(12,20,40);Opacity=.35;Cursor=Cursors.Cross;DoubleBuffered=true;KeyPreview=true;
        KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}};
        MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){origin=e.Location;dragging=true;}};
        MouseMove+=(_,e)=>{if(dragging){selected=Rectangle.FromLTRB(Math.Min(origin.X,e.X),Math.Min(origin.Y,e.Y),Math.Max(origin.X,e.X),Math.Max(origin.Y,e.Y));Invalidate();}};
        MouseUp+=(_,e)=>{if(!dragging)return;dragging=false;Selection=new Rectangle(selected.X+Left,selected.Y+Top,selected.Width,selected.Height);DialogResult=DialogResult.OK;Close();};
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);using var font=new Font("Microsoft YaHei UI",18,FontStyle.Bold);e.Graphics.DrawString("拖动框选投流数据面板 · 按 Esc 取消",font,Brushes.White,30,35);
        using var pen=new Pen(Color.White,3);e.Graphics.DrawRectangle(pen,selected);
    }
}

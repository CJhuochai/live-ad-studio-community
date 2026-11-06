using System.Drawing.Imaging;
using System.Security.Cryptography;
namespace LiveAdStudio;
static class ImageStore
{
    public static string Add(byte[] bytes)
    {
        if(bytes.Length==0||bytes.Length>12*1024*1024)throw new InvalidDataException("图片须小于12MB。");
        using var stream=new MemoryStream(bytes);using var image=Image.FromStream(stream,true,true);
        if((long)image.Width*image.Height>40_000_000)throw new InvalidDataException("图片分辨率过大，请先裁剪。");
        var scale=Math.Min(1,2400d/Math.Max(image.Width,image.Height));using var bmp=new Bitmap(Math.Max(1,(int)(image.Width*scale)),Math.Max(1,(int)(image.Height*scale)));
        using(var g=Graphics.FromImage(bmp)){g.Clear(Color.White);g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;g.DrawImage(image,0,0,bmp.Width,bmp.Height);}
        Directory.CreateDirectory(Path.Combine(Workspace.Root,"images"));var relative=Guid.NewGuid().ToString("N")+".jpg";bmp.Save(PathOf(relative),ImageFormat.Jpeg);return relative;
    }
    public static string PathOf(string name)
    {
        if(name.Length!=36||!name.EndsWith(".jpg",StringComparison.Ordinal)||!Guid.TryParseExact(name[..32],"N",out _))throw new InvalidDataException("图片标识无效。");
        return Path.Combine(Workspace.Root,"images",name);
    }
    public static string DataUrl(string name)=>"data:image/jpeg;base64,"+Convert.ToBase64String(File.ReadAllBytes(PathOf(name)));
    public static string Thumb(string name)
    {
        try{using var src=Image.FromFile(PathOf(name));var scale=Math.Min(1,420d/Math.Max(src.Width,src.Height));using var bmp=new Bitmap(src,new Size(Math.Max(1,(int)(src.Width*scale)),Math.Max(1,(int)(src.Height*scale))));using var ms=new MemoryStream();bmp.Save(ms,ImageFormat.Jpeg);return "data:image/jpeg;base64,"+Convert.ToBase64String(ms.ToArray());}catch{return "";}
    }
    public static string Hash(string name)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathOf(name))));
}

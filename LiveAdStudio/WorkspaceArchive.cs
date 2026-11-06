using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace LiveAdStudio;
static class WorkspaceArchive
{
    public static void Save(Workspace data,string path)
    {
        data.Validate();var temp=path+".tmp";
        if(File.Exists(temp))File.Delete(temp);
        using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
        {
            using(var writer=new StreamWriter(zip.CreateEntry("workspace.json").Open(),new UTF8Encoding(false)))writer.Write(JsonSerializer.Serialize(data));
            foreach(var image in data.Rooms.SelectMany(r=>r.Messages.SelectMany(m=>m.Images)).Distinct())
                if(File.Exists(ImageStore.PathOf(image)))zip.CreateEntryFromFile(ImageStore.PathOf(image),"images/"+image,CompressionLevel.Optimal);
        }
        File.Move(temp,path,true);
    }
    public static (Workspace Data,Dictionary<string,byte[]> Images) Read(string path)
    {
        using var zip=ZipFile.OpenRead(path);var entry=zip.GetEntry("workspace.json")??throw new InvalidDataException("备份缺少workspace.json。");
        if(entry.Length>50*1024*1024)throw new InvalidDataException("备份数据过大。");
        using var reader=new StreamReader(entry.Open());var data=JsonSerializer.Deserialize<Workspace>(reader.ReadToEnd())??throw new InvalidDataException("备份为空。");data.Upgrade();data.Validate();
        var images=new Dictionary<string,byte[]>();long total=0;
        foreach(var id in data.Rooms.SelectMany(r=>r.Messages.SelectMany(m=>m.Images)).Distinct())
        {
            var image=zip.GetEntry("images/"+id);if(image==null)continue;
            total+=image.Length;if(image.Length>12*1024*1024||total>200*1024*1024)throw new InvalidDataException("备份图片过大。");
            using var ms=new MemoryStream();using(var source=image.Open())source.CopyTo(ms);var bytes=ms.ToArray();using var bitmapStream=new MemoryStream(bytes);using var check=Image.FromStream(bitmapStream,true,true);
            if((long)check.Width*check.Height>40_000_000)throw new InvalidDataException("备份图片分辨率过大。");images[id]=bytes;
        }
        return(data,images);
    }
}

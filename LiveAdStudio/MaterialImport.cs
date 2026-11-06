using System.Globalization;
using LiveAdDesk;
namespace LiveAdStudio;
static class MaterialImport
{
    public static MaterialBatch Read(string path)=>ReadRows(ExcelImport.Rows(path),Path.GetFileName(path));
    public static MaterialBatch ReadRows(List<Dictionary<string,string>> rows,string fileName)
    {
        if(rows.Count is 0 or >10000)throw new InvalidDataException("素材报表需要1—10000行数据。");
        var h=rows[0];string title=h.ContainsKey("视频标题")?"视频标题":h.ContainsKey("图文标题")?"图文标题":throw new InvalidDataException("不是视频/图文素材效果报表。");
        foreach(var key in new[]{"素材ID","素材添加来源","创建时间","消耗(元)","全域成交金额(元)","全域成交订单数"})if(!h.ContainsKey(key))throw new InvalidDataException("素材报表缺少字段："+key);
        var batch=new MaterialBatch{Kind=title=="视频标题"?"视频":"图文",FileName=fileName};
        foreach(var row in rows)
        {
            string V(string key)=>row.GetValueOrDefault(key,"");
            var count=ExcelImport.Number(V("全域成交订单数"))??throw new InvalidDataException("订单数缺失，未将未知按0处理。");
            if(count<0||count>int.MaxValue||decimal.Truncate(count)!=count)throw new InvalidDataException("素材订单数必须是非负整数。");
            DateTime? created=null;if(DateTime.TryParse(V("创建时间"),CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))created=date;
            batch.Rows.Add(new(){Id=V("素材ID"),Title=V(title),Source=V("素材添加来源"),CreatedRaw=V("创建时间"),Created=created,Spend=ExcelImport.Number(V("消耗(元)"))??throw new InvalidDataException("素材消耗缺失。"),Gmv=ExcelImport.Number(V("全域成交金额(元)"))??throw new InvalidDataException("素材GMV缺失。"),Orders=(int)count});
        }
        return batch;
    }
}

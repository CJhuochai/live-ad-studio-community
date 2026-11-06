using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace LiveAdDesk;
public static class ExcelImport
{
    static readonly XNamespace N="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static XDocument Xml(ZipArchiveEntry e)
    {
        if(e.Length>100_000_000) throw new InvalidDataException("工作表过大，请拆分导出。");
        using var stream=e.Open(); using var r=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=100_000_000});
        return XDocument.Load(r);
    }
    public static List<Dictionary<string,string>> Rows(string path)
    {
        if(new FileInfo(path).Length>100_000_000) throw new InvalidDataException("文件大于100MB，请缩小导出范围。");
        using var z=ZipFile.OpenRead(path);
        var ss=z.GetEntry("xl/sharedStrings.xml") is {} e?Xml(e).Descendants(N+"si").Select(si=>string.Concat(si.Descendants(N+"t").Select(t=>t.Value))).ToArray():[];
        var sheet=z.GetEntry("xl/worksheets/sheet1.xml")??throw new InvalidDataException("未找到第一张工作表，请使用来客原始xlsx导出。");
        var doc=Xml(sheet); var raw=new List<Dictionary<string,string>>();
        foreach(var row in doc.Descendants(N+"sheetData").Elements(N+"row"))
        {
            if(raw.Count>100000) throw new InvalidDataException("超过10万行，请拆分文件。");
            var cells=new Dictionary<string,string>();
            foreach(var c in row.Elements(N+"c"))
            {
                var col=Regex.Replace((string?)c.Attribute("r")??"","[0-9]","");
                var v=c.Element(N+"v")?.Value??string.Concat(c.Descendants(N+"t").Select(t=>t.Value));
                if((string?)c.Attribute("t")=="s") v=ss[int.Parse(v,CultureInfo.InvariantCulture)];
                cells[col]=v.Trim();
            }
            if(cells.Values.Any(v=>v.Length>0)) raw.Add(cells);
        }
        if(raw.Count<2) throw new InvalidDataException("表格没有数据行。");
        var header=raw[0];
        return raw.Skip(1).Select(r=>header.Where(h=>h.Value.Length>0).GroupBy(h=>h.Value).ToDictionary(g=>g.Key,g=>r.GetValueOrDefault(g.First().Key,""))).ToList();
    }
    public static decimal? Number(string s)
    {
        s=s.Normalize(System.Text.NormalizationForm.FormKC).Replace(",","").Replace("，","").Replace("￥","").Replace("¥","").Trim();
        if(s is "" or "-" or "--") return null;
        var match=Regex.Match(s,@"^([+-]?\d+(?:\.\d+)?)\s*([万亿千]?)\s*(?:元|人|次|单)?$");
        if(match.Success&&decimal.TryParse(match.Groups[1].Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n))
        {
            var scale=match.Groups[2].Value switch{"万"=>10000m,"亿"=>100000000m,"千"=>1000m,_=>1m};
            try{return checked(n*scale);}catch(OverflowException){throw new InvalidDataException("金额超出支持范围。");}
        }
        throw new InvalidDataException("金额不是有效数字："+s);
    }
    static DateTime Date(string s)
    {
        if(DateTime.TryParse(s,CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)) return d;
        if(double.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out var oa) && oa>1 && oa<100000) return DateTime.FromOADate(oa);
        throw new InvalidDataException("日期格式无法识别："+s);
    }
    public static List<Order> Orders(string path)
    {
        var rows=Rows(path); var first=rows[0];
        foreach(var h in new[]{"订单编号","下单时间","商品名称","成交渠道","订单实收","支付时间"})
            if(!first.ContainsKey(h)) throw new InvalidDataException("不是支持的成交明细，缺少字段："+h);
        var list=new List<Order>();
        foreach(var group in rows.Where(r=>r.GetValueOrDefault("订单编号","").Length>0).GroupBy(r=>r["订单编号"]))
        {
            var r=group.First(); string V(string h)=>r.GetValueOrDefault(h,"");
            if(group.Select(x=>x.GetValueOrDefault("商品名称")).Distinct().Count()>1) throw new InvalidDataException("发现同一订单多个商品明细，暂不自动合并，请按单商品导出后核对。");
            var paid=Number(V("订单实收"))??0;
            var totals=group.Select(x=>Number(x.GetValueOrDefault("退款金额总计",""))).Where(x=>x.HasValue).ToArray();
            // 订单退款总计与售后退款不能叠加；券级重复行按售后编号去重。
            var refund=totals.Length>0?totals.Max()!.Value:group.GroupBy(x=>x.GetValueOrDefault("售后编号","")).Sum(g=>g.Select(x=>Number(x.GetValueOrDefault("退款金额",""))??0).Max());
            if(paid<0||refund<0||refund>paid) throw new InvalidDataException("订单金额或退款范围异常，已停止导入，请核对源文件。");
            list.Add(new Order{Id=V("订单编号"),Buyer=V("消费者UID"),Product=V("商品名称"),Channel=V("成交渠道"),Account=V("订单归属人昵称"),Status=V("订单状态"),
                OrderTime=Date(V("下单时间")),PaidTime=V("支付时间") is "" or "-"?null:Date(V("支付时间")),Paid=paid,Refund=refund,
                Quantity=(int)(Number(V("购买数量"))??1),ExpectedIncome=Number(V("预计收入")),Reason=string.Join("；",group.Select(x=>x.GetValueOrDefault("售后原因","")).Where(x=>x.Length>0).Distinct()),
                Redeemed=group.Any(x=>x.GetValueOrDefault("核销时间","") is not ("" or "-"))||V("订单状态")=="已使用"});
        }
        if(list.Count==0) throw new InvalidDataException("未识别到订单。");
        return list;
    }
    public static IncrementReport Report(string path)
    {
        var rows=Rows(path); if(rows.Count!=1) throw new InvalidDataException("请导出单账号单日增量报表（当前不止一行）。");
        var r=rows[0];
        foreach(var h in new[]{"抖音号","日期","总成交金额(元)","自然成交金额(元)","增量成交金额(元)"})
            if(!r.ContainsKey(h)) throw new InvalidDataException("增量报表缺少字段："+h);
        return new(){Account=r["抖音号"],Day=Date(r["日期"]),Gmv=Number(r["总成交金额(元)"])??0,Natural=Number(r["自然成交金额(元)"])??0,Increment=Number(r["增量成交金额(元)"])??0};
    }
}
public static class OcrReader
{
    public static string Languages()=>string.Join("、",OcrEngine.AvailableRecognizerLanguages.Select(l=>l.LanguageTag));
    public static async Task<string> Read(string path)
    {
        var lang=OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l=>l.LanguageTag.StartsWith("zh"));
        if(lang==null) throw new InvalidOperationException("Windows未安装中文OCR。请在设置→时间和语言→语言和区域添加中文及文字识别组件。手动快照、Excel导入仍可使用。");
        var engine=OcrEngine.TryCreateFromLanguage(lang)??throw new InvalidOperationException("无法启动Windows OCR。");
        var file=await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)); using var stream=await file.OpenReadAsync();
        var decoder=await BitmapDecoder.CreateAsync(stream);
        var max=OcrEngine.MaxImageDimension;
        double scale=Math.Min(1,Math.Min((double)max/decoder.PixelWidth,(double)max/decoder.PixelHeight));
        var transform=new BitmapTransform{ScaledWidth=(uint)Math.Max(1,decoder.PixelWidth*scale),ScaledHeight=(uint)Math.Max(1,decoder.PixelHeight*scale)};
        using var bmp=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,transform,ExifOrientationMode.RespectExifOrientation,ColorManagementMode.DoNotColorManage);
        var result=await engine.RecognizeAsync(bmp);
        return string.Join(Environment.NewLine,result.Lines.Select(l=>l.Text));
    }
    public static Dictionary<string,decimal> Candidates(string text)
    {
        var result=new Dictionary<string,decimal>(); var lines=text.Normalize(System.Text.NormalizationForm.FormKC).Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(l=>Regex.Replace(l,@"\s+","")).ToArray();
        for(int i=0;i<lines.Length;i++)
        {
            var l=lines[i].Replace(" ","");
            if(l.Contains("成本")||l.Contains("目标")||l.Contains("预算")||l.Contains("占比")||l.Contains("上周期")) continue;
            var key=l.Contains("退款金额")?"退款":l.Contains("成单人数")?"人数":l.Contains("成交订单数")?"订单":l.Contains("成交金额")&&!l.Contains("券助力")?"GMV":l.Contains("消耗")?"消耗":l.Contains("进房人数")?"进房":l.Contains("商品点击人数")?"点击":null;
            if(key==null||l.Contains("券助力")) continue;
            var start=Regex.Match(l,@"[+-]?\d");var tail=start.Success?l[start.Index..]:"";
            if(tail.Length==0&&i+1<lines.Length)tail=lines[i+1];
            // ponytail: 仅接收完整数值候选，未知单位不截成一个看似正确的小数。
            try{var n=ExcelImport.Number(tail);if(n is >=0)result.TryAdd(key,n.Value);}catch(InvalidDataException){}
        }
        return result;
    }
}

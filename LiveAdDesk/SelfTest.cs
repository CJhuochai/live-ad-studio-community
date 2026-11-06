namespace LiveAdDesk;
static class SelfTest
{
    public static int Run(string[] args)
    {
        var output = args[^1];
        try
        {
            count = 0;
            if (args.Length >= 4)
            {
                Check(ExcelImport.Orders(args[1]).Count > 0, "订单表读取");
                Check(ExcelImport.Report(args[2]).Day != default, "增量表读取");
            }
            var s = new Session { Name = "虚构测试", Day = new DateTime(2025,1,1) };
            var orders = new[]
            {
                new Order { Id="demo-1", Buyer="demo-buyer-1", Paid=120, Channel="直播", OrderTime=s.Day, PaidTime=s.Day },
                new Order { Id="demo-2", Buyer="demo-buyer-1", Paid=75, Refund=25, Channel="直播", OrderTime=s.Day, PaidTime=s.Day, Redeemed=true },
                new Order { Id="demo-3", Buyer="demo-buyer-2", Paid=50, Refund=50, Channel="直播", OrderTime=s.Day, PaidTime=s.Day },
                new Order { Id="demo-4", Buyer="demo-buyer-3", Paid=40, Channel="短视频", OrderTime=s.Day, PaidTime=s.Day }
            };
            s.Merge(orders); s.Merge(orders);
            Check(s.Orders.Count == 4, "重复导入不重复计单");
            var m = Metrics.From(s);
            Check(m.Count == 3 && m.Gross == 245m && m.Refund == 75m && m.Retained == 170m, "合成直播对账");
            Check(m.Buyers == 2 && m.KeptBuyers == 1 && m.Redeemed == 1, "买家去重与核销");
            Check(s.BreakEvenCpa() == null, "缺少成本不算盈利");
            Check(!Calc.Advice(s).Any(a=>a.Contains("经营上限·情景估算")),"未知成本不输出上限");
            s.Price=1000; s.FeeRate=10; s.DirectCost=400; s.Retention=80; s.DesiredProfit=100;
            Check(s.BreakEvenCpa() == 300m, "可承受成本公式");
            var p = new Order { Id="partial", Paid=100, Refund=30, Quantity=2, Channel="直播", OrderTime=s.Day, PaidTime=s.Day };
            s.Merge([p]); Check(Metrics.From(s).Retained==240m, "部分退款");
            Check(Calc.Ratio(10,0)==null, "零分母");
            Check(Calc.Delta(new Snapshot{At=s.Day.AddHours(22),Spend=100,Gmv=20},new Snapshot{At=s.Day.AddHours(23),Spend=120,Gmv=10}).Contains("下降"), "累计回补");
            Check(Calc.Delta(new Snapshot{At=s.Day,Spend=100},new Snapshot{At=s.Day.AddDays(1),Spend=20}).Contains("跨日"), "跨日不作差");
            var fields=OcrReader.Candidates("消耗(元)\n123.45\n全域成单人数\n4\n全域成交金额(元)\n1,250.50\n全域成单人数转化成本\n30.86");
            Check(fields.GetValueOrDefault("消耗")==123.45m && fields.GetValueOrDefault("人数")==4 && fields.GetValueOrDefault("GMV")==1250.50m, "OCR候选与成本排除");
            var crossing=new Session{Day=s.Day,Start=s.Day.AddHours(21),End=s.Day.AddDays(1).AddHours(2)};
            crossing.Merge([new Order{Id="late",Buyer="private-UID-TEST",Channel="直播",OrderTime=s.Day.AddDays(1).AddHours(1),PaidTime=s.Day.AddDays(1).AddHours(1),Paid=1280},new Order{Id="outside",Channel="直播",OrderTime=s.Day.AddDays(1).AddHours(3),PaidTime=s.Day.AddDays(1).AddHours(3),Paid=100}]);
            Check(Metrics.From(crossing).Gross==1280,"跨零点订单范围");
            Check(!Calc.Summary(crossing).Contains("private-UID-TEST"),"分析摘要不含UID");
            var spaced=OcrReader.Candidates("累 计 消 耗\n123 ． 45\n全 域 成 单 人 数\n4");
            Check(spaced.GetValueOrDefault("消耗")==123.45m && spaced.GetValueOrDefault("人数")==4,"OCR全角和分词空格");
            var temp=Path.Combine(Path.GetTempPath(),"LiveAdDesk-test-"+Guid.NewGuid()+".json");
            Store.Save(new AppData{Sessions=[s]},temp); Check(Store.Load(temp).Sessions[0].Orders.Count==5,"存储回读");
            Store.Save(new AppData{Sessions=[crossing]},temp);Check(Store.Load(temp+".bak").Sessions[0].Orders.Count==5,"保存备份");File.Delete(temp);File.Delete(temp+".bak");
            File.WriteAllText(output, $"PASS: {count}项检查通过。使用虚构数据核对退款、去重、跨日、OCR、摘要脱敏和备份。",System.Text.Encoding.UTF8);
            return 0;
        }
        catch(Exception e) { File.WriteAllText(output,"FAIL: "+e.GetType().Name); return 1; }
    }
    static int count;
    static void Check(bool value,string name) { if(!value) throw new Exception("FAIL: "+name);count++; }
}

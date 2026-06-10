using System; using TradingStudio.Ct;

var md = new CtpMd("./ctp_flow");
md.OnConnected += () => Console.WriteLine("[Connected]");
md.OnDisconnected += r => Console.WriteLine($"[Disconnected 0x{r:X}]");

Console.Write("Connect... "); md.Connect("tcp://180.168.146.187:10131");
Console.WriteLine("OK");

Console.Write("Login... "); md.Login("9999","13961193449","Chenxy@20240218!");
Console.WriteLine("Waiting...");
await Task.Delay(3000);

Console.Write("Subscribe... ");
int ret = md.Subscribe("cu2607","ag2608");
Console.WriteLine(ret==0 ? "OK" : $"Err:{ret}");

Console.WriteLine("Receiving ticks (30s)...");
await Task.Delay(30000);

Console.WriteLine($"Done. {md.TickCount} ticks received, {md.Reader.Count} in channel.");
md.Dispose();

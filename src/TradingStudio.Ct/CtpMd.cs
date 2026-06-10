using System;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TradingStudio.Core.Models;

namespace TradingStudio.Ct
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtOnTick(double l, int v, double t, double oi, double bp, int bv, double ap, int av, long ets, long lts, double ul, double ll);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtOnConnected();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtOnDisconnected(int r);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtOnLogin(int eid, string em);

    internal static class C
    {
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_Create")]
        public static extern IntPtr Create(string fp);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_RegisterCallbacks")]
        public static extern void RegCB(IntPtr h, CtOnTick ot, CtOnConnected oc, CtOnDisconnected od, CtOnLogin ol);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_Connect")]
        public static extern int Connect(IntPtr h, string fa);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_Login")]
        public static extern int Login(IntPtr h, string b, string u, string p);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_Subscribe")]
        public static extern int Subscribe(IntPtr h, string[] ss, int n);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_Unsubscribe")]
        public static extern int Unsubscribe(IntPtr h, string[] ss, int n);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CtpMd_Release")]
        public static extern void Release(IntPtr h);
    }

    public class CtpMd : IDisposable
    {
        private IntPtr _h;
        private readonly Channel<TickRecord> _ch;
        private readonly CtOnTick _ot;
        private readonly CtOnConnected _oc;
        private readonly CtOnDisconnected _od;
        private readonly CtOnLogin _ol;
        private int _tickCount;

        public ChannelReader<TickRecord> Reader => _ch.Reader;
        public int TickCount => _tickCount;
        public event Action? OnConnected;
        public event Action<int>? OnDisconnected;

        public CtpMd(string fp = "./ctp_flow")
        {
            System.IO.Directory.CreateDirectory(fp);
            _ch = Channel.CreateBounded<TickRecord>(10000);

            _ot = (l, v, t, oi, bp, bv, ap, av, ets, lts, ul, ll) =>
            {
                _tickCount++;
                _ch.Writer.TryWrite(new TickRecord
                {
                    ExchangeTimestamp = ets, LocalTimestamp = lts,
                    LastPrice = (long)(l * TickRecord.PriceScale),
                    Volume = v, Turnover = t, OpenInterest = oi,
                    BidPrice1 = (long)(bp * TickRecord.PriceScale), BidVolume1 = bv,
                    AskPrice1 = (long)(ap * TickRecord.PriceScale), AskVolume1 = av,
                    Flags = (l >= ul ? 1 : 0) | (l <= ll ? 2 : 0)
                });
            };
            _oc = () => OnConnected?.Invoke();
            _od = r => OnDisconnected?.Invoke(r);
            _ol = (id, msg) => { };

            _h = C.Create(fp);
            if (_h == IntPtr.Zero) throw new Exception("CtpMd_Create failed");
            C.RegCB(_h, _ot, _oc, _od, _ol);
        }

        public void Connect(string addr) => C.Connect(_h, addr);
        public void Login(string b, string u, string p) => C.Login(_h, b, u, p);
        public int Subscribe(params string[] ss) => C.Subscribe(_h, ss, ss.Length);
        public int Unsubscribe(params string[] ss) => C.Unsubscribe(_h, ss, ss.Length);

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { C.Release(_h); _h = IntPtr.Zero; }
            _ch.Writer.Complete();
        }
    }
}

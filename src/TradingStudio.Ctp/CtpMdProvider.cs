using System;
using System.Threading.Channels;
using TradingStudio.Core.Models;

namespace TradingStudio.Ctp
{
    public class CtpMdProvider : IDisposable
    {
        IntPtr _h;
        Channel<TickRecord> _ch = Channel.CreateBounded<TickRecord>(10000);
        public ChannelReader<TickRecord> Reader => _ch.Reader;
        public event Action OnConnected;
        public event Action<int> OnDisconnected;

        CtpOnTickDelegate _ot; CtpOnConnectedDelegate _oc; CtpOnDisconnectedDelegate _od; CtpOnLoginDelegate _ol;

        public CtpMdProvider(string fp="./ctp_flow")
        {
            System.IO.Directory.CreateDirectory(fp);
            _ot = (l,v,t,oi,bp,bv,ap,av,ets,lts,ul,ll) => _ch.Writer.TryWrite(new TickRecord{ExchangeTimestamp=ets,LocalTimestamp=lts,LastPrice=(long)(l*TickRecord.PriceScale),Volume=v,Turnover=t,OpenInterest=oi,BidPrice1=(long)(bp*TickRecord.PriceScale),BidVolume1=bv,AskPrice1=(long)(ap*TickRecord.PriceScale),AskVolume1=av,Flags=(l>=ul?1:0)|(l<=ll?2:0)});
            _oc = () => OnConnected?.Invoke();
            _od = r => OnDisconnected?.Invoke(r);
            _ol = (id,msg) => { /* TCS handled in App */ };
            _h = CtpInterop.CtMd_Create(fp);
            if(_h==IntPtr.Zero) throw new Exception("Create failed");
            CtpInterop.CtMd_RegisterCallbacks(_h,_ot,_oc,_od,_ol);
        }
        public void Connect(string a) { CtpInterop.CtMd_Connect(_h,a); }
        public void Login(string b,string u,string p) { CtpInterop.CtMd_Login(_h,b,u,p); }
        public int Subscribe(params string[] ss) => CtpInterop.CtMd_Subscribe(_h,ss,ss.Length);
        public void Dispose() { if(_h!=IntPtr.Zero){CtpInterop.CtMd_Release(_h);_h=IntPtr.Zero;} _ch.Writer.Complete(); }
    }
}

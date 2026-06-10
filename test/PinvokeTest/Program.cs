using System; using System.Runtime.InteropServices;
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void CtpOnTickDelegate(double l, int v, double t, double oi, double bp, int bv, double ap, int av, long ets, long lts, double ul, double ll);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void CtpOnConnectedDelegate();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void CtpOnDisconnectedDelegate(int r);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void CtpOnLoginDelegate(int eid, string em);
public static class X { [DllImport("TradingStudioCtpBridge",CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr CtpMd_Create(string s);
[DllImport("TradingStudioCtpBridge",CallingConvention=CallingConvention.Cdecl)]public static extern void CtpMd_RegisterCallbacks(IntPtr h, CtpOnTickDelegate ot, CtpOnConnectedDelegate oc, CtpOnDisconnectedDelegate od, CtpOnLoginDelegate ol);
[DllImport("TradingStudioCtpBridge",CallingConvention=CallingConvention.Cdecl)]public static extern int CtpMd_Connect(IntPtr h,string s);
[DllImport("TradingStudioCtpBridge",CallingConvention=CallingConvention.Cdecl)]public static extern int CtpMd_Login(IntPtr h,string a,string b,string c);
[DllImport("TradingStudioCtpBridge",CallingConvention=CallingConvention.Cdecl)]public static extern int CtpMd_Subscribe(IntPtr h,string[] s,int n);
[DllImport("TradingStudioCtpBridge",CallingConvention=CallingConvention.Cdecl)]public static extern void CtpMd_Release(IntPtr h); }
class P { static void Main() { Console.WriteLine("OK"); var h = X.CtMd_Create("f"); Console.WriteLine(h); X.CtMd_Release(h); } }

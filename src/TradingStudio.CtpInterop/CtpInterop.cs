using System;
using System.Runtime.InteropServices;

namespace TradingStudio.Ctp
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtpOnTickDelegate(double l, int v, double t, double oi, double bp, int bv, double ap, int av, long ets, long lts, double ul, double ll);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtpOnConnectedDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtpOnDisconnectedDelegate(int r);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtpOnLoginDelegate(int eid, string em);

    public static class CtpInterop
    {
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CtpMd_Create(string flowPath);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtpMd_RegisterCallbacks(IntPtr h, CtpOnTickDelegate ot, CtpOnConnectedDelegate oc, CtpOnDisconnectedDelegate od, CtpOnLoginDelegate ol);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CtpMd_Connect(IntPtr h, string frontAddr);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CtpMd_Login(IntPtr h, string brokerId, string userId, string password);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CtpMd_Subscribe(IntPtr h, string[] symbols, int count);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CtpMd_Unsubscribe(IntPtr h, string[] symbols, int count);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtpMd_Release(IntPtr h);
    }
}

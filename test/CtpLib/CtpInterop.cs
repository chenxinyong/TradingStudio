using System;
using System.Runtime.InteropServices;

namespace CTP
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtpOnTickDelegate(double l, int v, double t, double oi, double bp, int bv, double ap, int av, long ets, long lts, double ul, double ll);

    public static class CtpInterop
    {
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CtpMd_Create(string flowPath);
        [DllImport("TradingStudioCtpBridge", CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtpMd_RegisterCallbacks(IntPtr h, CtpOnTickDelegate ot);
    }
}

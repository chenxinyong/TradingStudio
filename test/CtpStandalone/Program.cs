using System; using System.Runtime.InteropServices;

// Use FtdcNet.CTP 1.4.0 as reference test
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RtnCB(IntPtr p, int type, IntPtr param);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FrontCB(IntPtr p, int type, int reason);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RspCB(IntPtr p, int type, IntPtr param, IntPtr rspInfo, int reqId, bool last);

static class M {
    [DllImport("ftdc2c_ctp",EntryPoint="MdCreateApi")] internal static extern IntPtr C(string f,bool u,bool m);
    [DllImport("ftdc2c_ctp",EntryPoint="MdRegisterCallback")] internal static extern void R(IntPtr h,FrontCB fcb,RspCB rcb,RtnCB tncb,IntPtr u);
    [DllImport("ftdc2c_ctp",EntryPoint="MdRegisterFront")] internal static extern void F(IntPtr h,string a);
    [DllImport("ftdc2c_ctp",EntryPoint="MdInit")] internal static extern void I(IntPtr h);
    [DllImport("ftdc2c_ctp",EntryPoint="MdGetApiVersion")] internal static extern IntPtr V();
    [DllImport("ftdc2c_ctp",EntryPoint="MdReqUserLogin")] internal static extern int L(IntPtr h,IntPtr fld,int rid);
}

class P {
    static void Main() {
        Console.WriteLine("Testing FtdcNet.CTP 1.4.0...");
        Console.WriteLine("Version: " + Marshal.PtrToStringAnsi(M.V()));
        var h=M.C("./cf",false,false);
        var conn=new System.Threading.Tasks.TaskCompletionSource<bool>();
        M.R(h,(p,t,r)=>{Console.WriteLine($"Front:{t} r={r}");if(t==1)conn.TrySetResult(true);},null,(p,t,param)=>{Console.WriteLine($"Rtn:{t}");},IntPtr.Zero);
        M.F(h,"tcp://180.168.146.187:10131");
        M.I(h);
        System.Threading.Thread.Sleep(5000);
        Console.WriteLine(conn.Task.IsCompleted?"CONNECTED!":"TIMEOUT");
    }
}

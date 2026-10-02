using System.Runtime.InteropServices;

namespace QFTPlus;
internal static class GraphicsAdapters
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Name;
        public uint Vendor,Device,Subsystem,Revision;
        public UIntPtr VideoMemory,SystemMemory,SharedMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Enumerate(IntPtr self,uint index,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Describe(IntPtr self,out Description description);
    static T Method<T>(IntPtr obj,int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size));
    internal static List<(int Index,string Name)> List()
    {
        var result=new List<(int,string)>();var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if(CreateDXGIFactory1(ref iid,out var factory)<0)return result;
        try
        {
            for(uint index=0;index<16;index++)
            {
                if(Method<Enumerate>(factory,12)(factory,index,out var adapter)<0)break;
                try {if(Method<Describe>(adapter,10)(adapter,out var desc)>=0&&(desc.Flags&2)==0)result.Add(((int)index,desc.Name));}
                finally {Marshal.Release(adapter);}
            }
        }
        finally {Marshal.Release(factory);}
        return result;
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Runner.SolidWorks;

// 显式 IDispatch 调用，不依赖 dynamic 读取宿主类型库。
internal static class DispatchReader
{
    public static object Read(object instance, string name)
    {
        var dispatch = (INativeDispatch)instance;
        var iid = Guid.Empty;
        var ids = new int[1];
        Marshal.ThrowExceptionForHR(dispatch.GetIDsOfNames(ref iid, [name], 1, 0x409, ids));
        var parameters = new DISPPARAMS();
        var hr = dispatch.Invoke(ids[0], ref iid, 0x409, 1, ref parameters, out var result, out var exception, out _);
        if (hr < 0) throw new COMException(string.IsNullOrWhiteSpace(exception.bstrDescription) ? $"读取 {name} 失败" : exception.bstrDescription, hr);
        return result;
    }

    public static string Revision(object instance) => Convert.ToString(Read(instance, "RevisionNumber"), CultureInfo.InvariantCulture) ?? "";
    public static int DocumentCount(object instance) => Convert.ToInt32(Read(instance, "GetDocumentCount"), CultureInfo.InvariantCulture);

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeDispatch
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr info);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] names,
            uint count, uint lcid, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] ids);
        [PreserveSig] int Invoke(int member, ref Guid iid, uint lcid, ushort flags, ref DISPPARAMS parameters,
            [MarshalAs(UnmanagedType.Struct)] out object result, out EXCEPINFO exception, out uint argumentError);
    }
}

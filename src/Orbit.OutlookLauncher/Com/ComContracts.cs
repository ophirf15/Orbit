using System.Runtime.InteropServices;

namespace Extensibility
{
    [ComImport]
    [Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744")]
    [TypeLibType(TypeLibTypeFlags.FDual | TypeLibTypeFlags.FDispatchable)]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IDTExtensibility2
    {
        [DispId(1)]
        void OnConnection(
            [In] [MarshalAs(UnmanagedType.IDispatch)] object Application,
            [In] ext_ConnectMode ConnectMode,
            [In] [MarshalAs(UnmanagedType.IDispatch)] object AddInInst,
            [In] [Out] [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(2)]
        void OnDisconnection(
            [In] ext_DisconnectMode RemoveMode,
            [In] [Out] [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(3)]
        void OnAddInsUpdate([In] [Out] [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(4)]
        void OnStartupComplete([In] [Out] [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);

        [DispId(5)]
        void OnBeginShutdown([In] [Out] [MarshalAs(UnmanagedType.SafeArray)] ref Array custom);
    }

    [Guid("289E9AF1-4973-11D1-AE81-00A0C90F26F4")]
    public enum ext_ConnectMode
    {
        ext_cm_AfterStartup = 0,
        ext_cm_Startup = 1,
        ext_cm_External = 2,
        ext_cm_CommandLine = 3,
        ext_cm_Solution = 4,
        ext_cm_UISetup = 5,
    }

    [Guid("289E9AF2-4973-11D1-AE81-00A0C90F26F4")]
    public enum ext_DisconnectMode
    {
        ext_dm_HostShutdown = 0,
        ext_dm_UserClosed = 1,
        ext_dm_UISetupComplete = 2,
        ext_dm_SolutionClosed = 3,
    }
}

namespace Office
{
    [ComImport]
    [Guid("000C0396-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDispatchable)]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRibbonExtensibility
    {
        [DispId(1)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetCustomUI([In] [MarshalAs(UnmanagedType.BStr)] string RibbonID);
    }

    /// <summary>Minimal IRibbonControl so onAction callbacks bind in Classic Outlook.</summary>
    [ComImport]
    [Guid("000C0395-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDispatchable)]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRibbonControl
    {
        [DispId(1)]
        string Id
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            get;
        }

        [DispId(2)]
        object Context
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }

        [DispId(3)]
        string Tag
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            get;
        }
    }

    [ComImport]
    [Guid("000C03A1-0000-0000-C000-000000000046")]
    [TypeLibType(TypeLibTypeFlags.FDispatchable)]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRibbonUI
    {
        [DispId(1)]
        void Invalidate();

        [DispId(2)]
        void InvalidateControl([In] [MarshalAs(UnmanagedType.BStr)] string ControlID);
    }

    /// <summary>
    /// Explicit dispinterface for ribbon callbacks. Outlook resolves onAction by name
    /// on the add-in object; AutoDual + this interface makes OnSendToOrbit discoverable.
    /// </summary>
    [ComVisible(true)]
    [Guid("A7C4E2B1-9F3D-4E8A-B2C5-1D6E7F809A12")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IOrbitRibbonCallbacks
    {
        [DispId(1)]
        void Ribbon_Load(object ribbonUi);

        [DispId(2)]
        void OnSendToOrbit(IRibbonControl control);
    }
}

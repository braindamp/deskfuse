using System.Runtime.InteropServices;

namespace DeskFuse.AudioBroker;

/// <summary>
/// The Windows Software Device API.
///
/// A software device is how one installed driver package becomes several
/// independent devices: the broker creates one instance per enrolled phone
/// with a stable instance id, and Windows starts the DeskFuse Audio driver
/// for each of them. The returned handle owns the device: closing it
/// removes the device, and with it the phone's audio endpoint.
/// </summary>
internal static class SwDevice
{
    private const string Dll = "cfgmgr32.dll";

    internal const uint CapabilityRemovable = 0x00000001;
    internal const uint CapabilitySilentInstall = 0x00000002;
    internal const uint CapabilityDriverRequired = 0x00000008;
    internal const uint DevPropTypeString = 0x00000012;

    // DEVPKEY_Device_FriendlyName
    private static readonly Guid DevPropDeviceFriendlyName =
        new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const int DevPropDeviceFriendlyNamePid = 14;
    private const int DevPropStoreDevicePropertySet = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SwDeviceCreateInfo
    {
        public uint CbSize;
        public IntPtr PszInstanceId;
        public IntPtr PszzHardwareIds;
        public IntPtr PszzCompatibleIds;
        public IntPtr PContainerId;
        public uint CapabilityFlags;
        public IntPtr PszDeviceDescription;
        public IntPtr PszDeviceLocation;
        public IntPtr PSecurityDescriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropCompKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public int Store;
        public IntPtr LocaleName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevProperty
    {
        public DevPropCompKey CompKey;
        public uint Type;
        public uint BufferSize;
        public IntPtr Buffer;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SwDeviceCreateCallback(IntPtr hSwDevice, int createResult,
        IntPtr pContext, IntPtr pszDeviceInstanceId);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SwDeviceCreate(
        [MarshalAs(UnmanagedType.LPWStr)] string pszEnumeratorName,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszParentDeviceInstance,
        ref SwDeviceCreateInfo pCreateInfo,
        uint cPropertyCount,
        IntPtr pProperties,
        SwDeviceCreateCallback pCallback,
        IntPtr pContext,
        out IntPtr phSwDevice);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern void SwDeviceClose(IntPtr hSwDevice);

    /// <summary>
    /// Creates one software device instance of the DeskFuse Audio driver.
    /// The caller owns the returned handle: disposing it removes the device
    /// and therefore the phone's endpoint.
    /// </summary>
    internal static SwDeviceInstance Create(string instanceId, string hardwareId, string friendlyName,
        string enumerator = "ROOT", string? parent = "HTREE\\ROOT\\0",
        uint capabilities = CapabilityRemovable | CapabilitySilentInstall, bool withProperty = true)
    {
        // The hardware id list is a MULTI_SZ: every entry is null terminated
        // and the list ends with a second null.
        var instanceIdBuffer = Marshal.StringToHGlobalUni(instanceId);
        var hardwareIdsBuffer = Marshal.StringToHGlobalUni(hardwareId + "\0");
        var descriptionBuffer = Marshal.StringToHGlobalUni(friendlyName);
        var nameBuffer = Marshal.StringToHGlobalUni(friendlyName);
        var info = new SwDeviceCreateInfo
        {
            CbSize = (uint)Marshal.SizeOf<SwDeviceCreateInfo>(),
            PszInstanceId = instanceIdBuffer,
            PszzHardwareIds = hardwareIdsBuffer,
            PszzCompatibleIds = IntPtr.Zero,
            PContainerId = IntPtr.Zero,
            CapabilityFlags = capabilities,
            PszDeviceDescription = descriptionBuffer,
            PszDeviceLocation = IntPtr.Zero,
            PSecurityDescriptor = IntPtr.Zero,
        };
        var property = new DevProperty
        {
            CompKey = new DevPropCompKey
            {
                FormatId = DevPropDeviceFriendlyName,
                PropertyId = DevPropDeviceFriendlyNamePid,
                Store = DevPropStoreDevicePropertySet,
                LocaleName = IntPtr.Zero,
            },
            Type = DevPropTypeString,
            BufferSize = (uint)((friendlyName.Length + 1) * sizeof(char)),
            Buffer = nameBuffer,
        };
        var properties = Marshal.AllocHGlobal(Marshal.SizeOf<DevProperty>());
        // The callback must stay alive for as long as the device handle
        // does: Windows may call it at any time until the handle is closed.
        var callback = default(SwDeviceCreateCallback)!;
        try
        {
            Marshal.StructureToPtr(property, properties, false);
            var context = new CreateContext();
            var handle = GCHandle.Alloc(context);
            try
            {
                callback = (h, result, ctx, instance) =>
                    context.Complete(result, instance == IntPtr.Zero ? null : Marshal.PtrToStringUni(instance));
                var hr = SwDeviceCreate("ROOT", null, ref info, 1, properties, callback,
                    GCHandle.ToIntPtr(handle), out var device);
                if (hr != 0)
                    throw new InvalidOperationException(
                        $"Windows refused to create the phone speaker device (HRESULT 0x{hr:X8}).");
                var created = context.Wait(TimeSpan.FromSeconds(10));
                if (created is null)
                {
                    SwDeviceClose(device);
                    throw new TimeoutException("Windows did not report the new phone speaker device.");
                }
                if (created.Value.Result != 0)
                {
                    SwDeviceClose(device);
                    throw new InvalidOperationException(
                        $"Windows could not start the DeskFuse Audio driver for this phone " +
                        $"(HRESULT 0x{created.Value.Result:X8}).");
                }
                return new SwDeviceInstance(device, created.Value.InstanceId ?? instanceId, friendlyName, callback);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            Marshal.DestroyStructure(properties, typeof(DevProperty));
            Marshal.FreeHGlobal(properties);
            Marshal.FreeHGlobal(nameBuffer);
            Marshal.FreeHGlobal(instanceIdBuffer);
            Marshal.FreeHGlobal(hardwareIdsBuffer);
            Marshal.FreeHGlobal(descriptionBuffer);
        }
    }

    private sealed class CreateContext
    {
        private readonly TaskCompletionSource<(int Result, string? InstanceId)> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(int result, string? instanceId) =>
            _completion.TrySetResult((result, instanceId));

        public (int Result, string? InstanceId)? Wait(TimeSpan timeout)
        {
            if (!_completion.Task.Wait(timeout)) return null;
            return _completion.Task.Result;
        }
    }
}

/// <summary>One created software device: the handle that owns its lifetime.</summary>
internal sealed class SwDeviceInstance : IDisposable
{
    private IntPtr _handle;
    private readonly Delegate _callback;

    internal SwDeviceInstance(IntPtr handle, string deviceInstanceId, string friendlyName, Delegate callback)
    {
        _handle = handle;
        _callback = callback;
        DeviceInstanceId = deviceInstanceId;
        FriendlyName = friendlyName;
    }

    /// <summary>The device instance id Windows assigned.</summary>
    public string DeviceInstanceId { get; }

    /// <summary>The device name Windows shows for this instance.</summary>
    public string FriendlyName { get; }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) SwDevice.SwDeviceClose(handle);
        GC.KeepAlive(_callback);
    }
}

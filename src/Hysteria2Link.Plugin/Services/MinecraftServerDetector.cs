using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hysteria2Link.Plugin.Services;

internal static class MinecraftServerDetector
{
    private const int AddressFamilyInterNetwork = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint TcpStateListen = 2;

    public static async Task<IReadOnlyList<MinecraftServerStatus>> FindAsync(CancellationToken cancellationToken = default)
    {
        var javaProcesses = Process.GetProcessesByName("java")
            .Concat(Process.GetProcessesByName("javaw"))
            .ToArray();
        HashSet<int> processIds;
        try
        {
            processIds = javaProcesses.Select(process => process.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in javaProcesses)
                process.Dispose();
        }

        if (processIds.Count == 0)
            return [];

        var ports = GetListeningPorts(processIds);
        var checks = ports.Select(async port =>
        {
            try
            {
                return await MinecraftStatusClient.QueryAsync(
                    "127.0.0.1",
                    port,
                    TimeSpan.FromMilliseconds(900),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.Where(result => result is not null)
            .Cast<MinecraftServerStatus>()
            .OrderBy(result => result.Port)
            .ToArray();
    }

    private static IReadOnlyList<int> GetListeningPorts(HashSet<int> processIds)
    {
        var bufferSize = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            sort: true,
            AddressFamilyInterNetwork,
            TcpTableClass.OwnerPidListener,
            0);
        if (result != ErrorInsufficientBuffer)
            return [];

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferSize,
                sort: true,
                AddressFamilyInterNetwork,
                TcpTableClass.OwnerPidListener,
                0);
            if (result != 0)
                return [];

            var count = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var ports = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPointer, index * rowSize));
                if (row.State != TcpStateListen || !processIds.Contains(unchecked((int)row.OwningPid)))
                    continue;

                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort & 0xFF00) >> 8));
                if (port is > 0 and <= 65535)
                    ports.Add(port);
            }

            return ports.Order().ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private enum TcpTableClass
    {
        OwnerPidListener = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outputBufferLength,
        bool sort,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);
}

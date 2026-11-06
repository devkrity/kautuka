using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Packets;

/// <summary>Bounded native NFQUEUE transport. Disposing closes the consumer without a fail-open flag.</summary>
internal sealed unsafe partial class LinuxNfQueue : IQueueVerdicts, IDisposable
{
    private readonly ushort queue;
    private int descriptor = -1;

    public LinuxNfQueue(ushort queue)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("NFQUEUE requires Linux.");
        }
        this.queue = queue;
        descriptor = NativeSocket(16, 3 | 0x80000, 12); // AF_NETLINK, SOCK_RAW|CLOEXEC, NETLINK_NETFILTER
        if (descriptor < 0)
        {
            throw NativeFailure("open netfilter socket");
        }
        try
        {
            var address = new NetlinkAddress { Family = 16 };
            if (NativeBind(descriptor, ref address, 12) != 0)
            {
                throw NativeFailure("bind netfilter socket");
            }
            Configure(1, bind: true);
            Configure(2, bind: false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Run(Action<QueuedPacket> handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        byte[] buffer = new byte[131072];
        // Callback exceptions deliberately escape. The owner must Dispose, never resume uncertain state.
        while (!cancellationToken.IsCancellationRequested)
        {
            int count = Receive(buffer, 250);
            if (count == 0)
            {
                continue;
            }
            int offset = 0;
            while (offset < count)
            {
                if (count - offset < 16)
                {
                    throw new InvalidDataException("Truncated netlink datagram.");
                }
                uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
                if (rawLength < 20 || rawLength > count - offset)
                {
                    throw new InvalidDataException("Invalid netlink message length.");
                }
                int length = (int)rawLength;
                handler(NfQueueProtocol.Decode(buffer.AsSpan(offset, length), queue));
                offset += (length + 3) & ~3;
            }
        }
    }

    public void Allow(uint packetId, uint handle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(handle);
        Send(NfQueueProtocol.Verdict(queue, packetId, handle));
    }

    public void Drop(uint packetId) => Send(NfQueueProtocol.Verdict(queue, packetId, 0));

    public void Dispose()
    {
        int current = Interlocked.Exchange(ref descriptor, -1);
        if (current >= 0)
        {
            NativeClose(current);
        }
    }

    private void Configure(uint sequence, bool bind)
    {
        Send(NfQueueProtocol.Configure(queue, sequence, bind));
        byte[] buffer = new byte[4096];
        int count = Receive(buffer, 5000);
        if (count == 0)
        {
            throw new IOException("Timed out configuring NFQUEUE.");
        }
        int error = NfQueueProtocol.Acknowledgement(buffer.AsSpan(0, count), sequence);
        if (error != 0)
        {
            throw new IOException($"Kernel refused NFQUEUE configuration (errno {-error}).");
        }
    }

    private int Receive(byte[] buffer, int timeoutMilliseconds)
    {
        var poll = new PollDescriptor { Descriptor = descriptor, Events = 1 };
        int ready = NativePoll(ref poll, 1, timeoutMilliseconds);
        if (ready < 0)
        {
            if (Marshal.GetLastPInvokeError() == 4) // EINTR
            {
                return 0;
            }
            throw NativeFailure("poll netfilter socket");
        }
        if (ready == 0)
        {
            return 0;
        }
        if ((poll.ReturnedEvents & ~1) != 0)
        {
            throw new IOException("Netfilter socket reported an error; consumer must stop.");
        }

        var peer = new NetlinkAddress();
        uint peerLength = 12;
        nint received;
        fixed (byte* pointer = buffer)
        {
            received = NativeReceive(descriptor, pointer, (nuint)buffer.Length, 0x20, ref peer, ref peerLength);
        }
        if (received <= 0)
        {
            throw NativeFailure("receive netfilter message");
        }
        if (received > buffer.Length || peerLength != 12 || peer.Family != 16 || peer.ProcessId != 0)
        {
            throw new InvalidDataException("Truncated or non-kernel netfilter datagram.");
        }
        return (int)received;
    }

    private void Send(byte[] buffer)
    {
        var kernel = new NetlinkAddress { Family = 16 };
        nint sent;
        fixed (byte* pointer = buffer)
        {
            sent = NativeSend(descriptor, pointer, (nuint)buffer.Length, 0, ref kernel, 12);
        }
        if (sent != buffer.Length)
        {
            throw NativeFailure("send netfilter message");
        }
    }

    private static IOException NativeFailure(string operation) => new($"Cannot {operation} (errno {Marshal.GetLastPInvokeError()}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct NetlinkAddress
    {
        public ushort Family;
        public ushort Padding;
        public uint ProcessId;
        public uint Groups;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int NativeSocket(int domain, int type, int protocol);

    [LibraryImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static partial int NativeBind(int descriptor, ref NetlinkAddress address, uint length);

    [LibraryImport("libc", EntryPoint = "sendto", SetLastError = true)]
    private static partial nint NativeSend(int descriptor, byte* buffer, nuint length, int flags, ref NetlinkAddress address, uint addressLength);

    [LibraryImport("libc", EntryPoint = "recvfrom", SetLastError = true)]
    private static partial nint NativeReceive(int descriptor, byte* buffer, nuint length, int flags, ref NetlinkAddress address, ref uint addressLength);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int NativePoll(ref PollDescriptor descriptors, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int descriptor);
}

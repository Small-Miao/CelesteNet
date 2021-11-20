using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Celeste.Mod.CelesteNet.Server {
    public class UDPReceiverRole : MultipleSocketBinderRole {

        private class Worker : RoleWorker {

            public Worker(UDPReceiverRole role, NetPlusThread thread) : base(role, thread) { }

            protected override void StartWorker(Socket socket, CancellationToken token) {
                // Receive packets as long as the token isn't canceled
                // TODO We can optimize this with recvmmsg
                byte[] dgBuffer = new byte[Role.Server.Settings.UDPMaxDatagramSize];
                int dgSize;

                EnterActiveZone();
                token.Register(() => socket.Close());
                while (!token.IsCancellationRequested) {
                    EndPoint dgramSender = new IPEndPoint(IPAddress.Any, ((IPEndPoint) Role.EndPoint).Port);
                    ExitActiveZone();
                    try {
                        dgSize = socket.ReceiveFrom(dgBuffer, dgBuffer.Length, SocketFlags.None, ref dgramSender);
                    } catch (SocketException) {
                        // There's no better way to do this, as far as I know...
                        if (token.IsCancellationRequested) 
                            return;
                        throw;
                    }
                    EnterActiveZone();

                    // Try to get the associated connection
                    if (!Role.endPointMap.TryGetValue(dgramSender, out ConPlusTCPUDPConnection? con) || con == null) {
                        // Get the connection token
                        if (dgSize != 4)
                            continue;
                        int conToken = BitConverter.ToInt32(dgBuffer, 0);

                        // Get the connection from the token
                        if (!Role.conTokenMap.TryGetValue(conToken, out con))
                            continue;

                        // Add the connection to the map
                        Role.endPointMap.TryAdd(dgramSender, con!);
                        continue;
                    }

                    try {
                        // Handle the UDP datagram
                        con.HandleUDPDatagram(dgBuffer, dgSize);
                    } catch (Exception e) {
                        Logger.Log(LogLevel.WRN, "tcprecv", $"Error while reading from connection {con}: {e}");
                        con.DecreaseUDPScore();
                    }
                }
                ExitActiveZone();
            }

            public new UDPReceiverRole Role => (UDPReceiverRole) base.Role;

        }

        private ConcurrentDictionary<int, ConPlusTCPUDPConnection> conTokenMap;
        private ConcurrentDictionary<EndPoint, ConPlusTCPUDPConnection> endPointMap;

        public UDPReceiverRole(NetPlusThreadPool pool, CelesteNetServer server, EndPoint endPoint) : base(pool, ProtocolType.Udp, endPoint) {
            Server = server;
            conTokenMap = new ConcurrentDictionary<int, ConPlusTCPUDPConnection>();
            endPointMap = new ConcurrentDictionary<EndPoint, ConPlusTCPUDPConnection>();
        }

        public override NetPlusThreadRole.RoleWorker CreateWorker(NetPlusThread thread) => new Worker(this, thread);
        
        public void AddConnection(ConPlusTCPUDPConnection con) {
            conTokenMap.TryAdd(con.ConnectionToken, con);
            con.OnUDPDeath += UDPDeath;
        }

        public void RemoveConnection(ConPlusTCPUDPConnection con) {
            con.OnUDPDeath -= UDPDeath;
            conTokenMap.TryRemove(con.ConnectionToken, out _);
            lock (con.UDPLock) {
                if (con.UDPEndpoint != null)
                    endPointMap.TryRemove(con.UDPEndpoint, out _);
            }
        }

        private void UDPDeath(CelesteNetTCPUDPConnection con, EndPoint ep) {
            endPointMap.TryRemove(ep, out _);
        }

        public CelesteNetServer Server { get; }

    }
}
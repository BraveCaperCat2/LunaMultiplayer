using Lidgren.Network;
using LmpCommon;
using LmpCommon.Message;
using LmpCommon.Message.Data.MasterServer;
using LmpCommon.Message.Interface;
using LmpCommon.Message.MasterServer;
using LmpCommon.Message.Types;
using LmpCommon.RepoRetrievers;
using LmpCommon.Time;
using LmpGlobal;
using LmpMasterServer.Log;
using LmpMasterServer.Structure;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LmpMasterServer.Lidgren
{
    public class MasterServer
    {
        public static volatile bool RunServer;

        private static readonly TimeSpan ServerTickInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan OfflineServerCleanupInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CountryCodeRefreshInterval = TimeSpan.FromSeconds(30);
        public static ushort Port { get; set; } = 8700;
        public static ConcurrentDictionary<long, Server> ServerDictionary { get; } = new ConcurrentDictionary<long, Server>();
        private static TimeoutConcurrentDictionary<IPAddress, object> _lastServerListRequests =
            new TimeoutConcurrentDictionary<IPAddress, object>(1000);
        private static MasterServerMessageFactory MasterServerMessageFactory { get; } = new MasterServerMessageFactory();

        // Only servers that report one of these versions will be added to the list
        // This can be used to drop server-list support for older versions and prevent servers for other people's forks of LMP from appearing on the actual LMP server list.
        // Currently, it's just all the versions that are available on github, plus 0.29.2 and 0.30.0, regardless of compattibility or support for those versions.
        public static readonly string[] ValidServerVersions = {
            // Old versions (<0.29.1)
            "0.0.5",
            "0.0.6",
            "0.0.7",
            "0.0.8",
            "0.0.9",
            "0.0.10",
            "0.1.11",
            "0.1.12",
            "0.1.13",
            "0.2.16",
            "0.2.17",
            "0.2.22",
            "0.3.28",
            "0.4.33",
            "0.5.36",
            "0.6.37",
            "0.7.41",
            "0.8.42",
            "0.9.44",
            "0.10.46",
            "0.11.47",
            "0.12.49",
            "0.13.50",
            "0.15.0",
            "0.16.0",
            "0.17.0",
            "0.18.0",
            "0.19.0",
            "0.20.0",
            "0.20.1",
            "0.20.2",
            "0.21.0",
            "0.22.0",
            "0.24.0",
            "0.25.0",
            "0.26.0",
            "0.27.0",
            "0.28.0",
            "0.29.0",
            // Current version
            "0.29.1",
            // Future versions (>0.29.1)
            "0.29.2",
            "0.30.0"
        };

        public static async Task StartAsync()
        {
            var config = new NetPeerConfiguration("masterserver")
            {
                Port = Port,
                SuppressUnreliableUnorderedAcks = true,
                PingInterval = 0.5f,
                ConnectionTimeout = (float)ServerTimeout.TotalSeconds
            };

            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);

            var peer = new NetServer(config);
            peer.Start();

            _ = Task.Run(() => CheckMasterServerListed());

            LunaLog.Info($"Master server {LmpVersioning.CurrentVersion} started! Поехали!");
            RemoveExpiredServers();
            StartCountryCodeRefreshTask();

            while (RunServer)
            {
                NetIncomingMessage msg;
                while ((msg = peer.ReadMessage()) != null)
                {
                    switch (msg.MessageType)
                    {
                        case NetIncomingMessageType.DebugMessage:
                            LunaLog.NetworkDebug(msg.ReadString());
                            break;
                        case NetIncomingMessageType.VerboseDebugMessage:
                            LunaLog.NetworkVerboseDebug(msg.ReadString());
                            break;
                        case NetIncomingMessageType.WarningMessage:
                            LunaLog.Warning(msg.ReadString());
                            break;
                        case NetIncomingMessageType.ErrorMessage:
                            LunaLog.Error(msg.ReadString());
                            break;
                        case NetIncomingMessageType.UnconnectedData:
                            var message = GetMessage(msg);
                            if (message == null)
                                break;
                            if (message.VersionMismatch)
                                break;
                            HandleMessage(message, msg, peer);
                            break;
                    }
                }
                await Task.Delay(ServerTickInterval);
            }
            peer.Shutdown("So long and thanks for all the fish!");
        }

        private static IMasterServerMessageBase GetMessage(NetIncomingMessage msg)
        {
            try
            {
                var message = MasterServerMessageFactory.Deserialize(msg, LunaNetworkTime.UtcNow.Ticks) as IMasterServerMessageBase;
                return message;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CheckMasterServerListed()
        {
            var ownAddress = LunaNetUtils.GetOwnExternalIpAddress();
            if (ownAddress != null)
            {
                var ownEndpoint = new IPEndPoint(ownAddress, Port);
                if (!MasterServerRetriever.MasterServers.Contains(ownEndpoint))
                {
                    LunaLog.Error($"You're not listed in the master servers list ({RepoConstants.MasterServersListShortUrl}). Clients/Servers won't see you");
                }
                else
                {
                    LunaLog.Normal("You're correctly listed in the master servers list");
                }
            }
            else
            {
                LunaLog.Error("Could not retrieve own external IP address, master server likely won't function properly");
            }
        }

        private static void HandleMessage(IMasterServerMessageBase message, NetIncomingMessage netMsg, NetPeer peer)
        {
            if (BannedIpsRetriever.IsBanned(netMsg.SenderEndPoint))
            {
                LunaLog.Debug($"Ignoring BANNED IP: {netMsg.SenderEndPoint}");
                return;
            }

            try
            {
                switch ((message?.Data as MsBaseMsgData)?.MasterServerMessageSubType)
                {
                    case MasterServerMessageSubType.RegisterServer:
                        RegisterServer(message, netMsg);
                        break;
                    case MasterServerMessageSubType.RequestServers:
                        HandleListRequest(netMsg, peer);
                        break;
                    case MasterServerMessageSubType.Introduction:
                        var msgData = (MsIntroductionMsgData)message.Data;
                        if (ServerDictionary.TryGetValue(msgData.Id, out var server))
                        {
                            _ = Task.Run(() =>
                            {
                                if (!server.InternalEndpoint6.Address.Equals(IPAddress.IPv6Loopback)
                                    && !msgData.InternalEndpoint6.Address.Equals(IPAddress.IPv6Loopback))
                                {
                                    // Both client and server are listening on IPv6, try an IPv6 firewall punchthrough
                                    // This also triggers a first punchthrough on IPv4 with the public addresses
                                    LunaLog.Normal($"INTRODUCTION request from: " +
                                                   $"{netMsg.SenderEndPoint} ({msgData.InternalEndpoint6}) " +
                                                   $"to server: {server.ExternalEndpoint} ({server.InternalEndpoint6})");
                                    peer.Introduce(server.InternalEndpoint6, server.ExternalEndpoint,
                                        msgData.InternalEndpoint6, // client internal
                                        netMsg.SenderEndPoint, // client external
                                        msgData.Token); // request token

                                    // Give the first introduction attempt some time
                                    Thread.Sleep(50);
                                } else {
                                    LunaLog.Normal(
                                        $"INTRODUCTION request from: {netMsg.SenderEndPoint} to server: {server.ExternalEndpoint}");
                                }
                                // The IPv4 punchthrough is triggered all the time
                                peer.Introduce(server.InternalEndpoint, server.ExternalEndpoint,
                                    msgData.InternalEndpoint, // client internal
                                    netMsg.SenderEndPoint, // client external
                                    msgData.Token); // request token
                            });
                        }
                        else
                        {
                            LunaLog.Warning($"Client {netMsg.SenderEndPoint} requested introduction to non listed host!");
                        }
                        break;
                    case MasterServerMessageSubType.STUNBindingRequest:
                        HandleBindingRequest(netMsg, peer);
                        break;
                }
            }
            catch (Exception e)
            {
                LunaLog.Error($"Error handling message. Details: {e}");
            }
        }

        private static void HandleListRequest(NetIncomingMessage netMsg, NetPeer peer)
        {
            // Limit server list requests to one per second per source IP address
            // because this is basically a (n * 200)-times UDP amplification service (n: number of registered servers).
            // If masterservers ever listen on IPv6, this needs to be updated to mask IPv6 addresses to 64 bits.
            var address = netMsg.SenderEndPoint.Address;

            if (_lastServerListRequests.TryGet(address, out _))
            {
                LunaLog.Debug($"LIST REQUEST RATE LIMIT EXCEEDED from: {netMsg.SenderEndPoint}");
                return;
            }

            _lastServerListRequests.TryAdd(address, null);

            LunaLog.Normal($"LIST REQUEST from: {netMsg.SenderEndPoint}");
            SendServerLists(netMsg, peer);
        }

        /// <summary>
        /// Return the list of servers that match the version specified
        /// </summary>
        private static void SendServerLists(NetIncomingMessage netMsg, NetPeer peer)
        {
            foreach (var server in ServerDictionary.Values.ToArray())
            {
                var msgData = MasterServerMessageFactory.CreateNewMessageData<MsReplyServersMsgData>();

                msgData.Id = server.Id;
                msgData.ServerVersion = server.ServerVersion;
                msgData.Password = server.Password;
                msgData.Cheats = server.Cheats;
                msgData.Description = server.Description;
                msgData.Country = server.Country;
                msgData.Website = server.Website;
                msgData.WebsiteText = server.WebsiteText;
                msgData.RainbowEffect = server.RainbowEffect;
                Array.Copy(server.Color, msgData.Color, 3);
                msgData.ExternalEndpoint = server.ExternalEndpoint;
                msgData.GameMode = server.GameMode;
                msgData.InternalEndpoint = server.InternalEndpoint;
                msgData.InternalEndpoint6 = server.InternalEndpoint6;
                msgData.MaxPlayers = server.MaxPlayers;
                msgData.ModControl = server.ModControl;
                msgData.DedicatedServer = server.DedicatedServer;
                msgData.PlayerCount = server.PlayerCount;
                msgData.ServerName = server.ServerName;
                msgData.VesselUpdatesSendMsInterval = server.VesselUpdatesSendMsInterval;
                msgData.WarpMode = server.WarpMode;
                msgData.TerrainQuality = server.TerrainQuality;

                var msg = MasterServerMessageFactory.CreateNew<MainMstSrvMsg>(msgData);
                var outMsg = peer.CreateMessage(msg.GetMessageSize());

                msg.Serialize(outMsg);
                peer.SendUnconnectedMessage(outMsg, netMsg.SenderEndPoint);

                //Force send of packets
                peer.FlushSendQueue();
            }
        }

        private static void RegisterServer(IMessageBase message, NetIncomingMessage netMsg)
        {
            var msgData = (MsRegisterServerMsgData)message.Data;

            if (!ValidServerVersions.Contains(msgData.ServerVersion))
            {
                LunaLog.Debug($"Server registration rejected for server at {netMsg.SenderEndPoint} with provided version {msgData.ServerVersion}. (not in ValidServerVersions list)");
                return;
            }

            if (ServerDictionary.TryGetValue(msgData.Id, out var existing))
            {
                existing.Update(msgData, netMsg.SenderEndPoint);
            } else {
                ServerDictionary.TryAdd(msgData.Id, new Server(msgData, netMsg.SenderEndPoint));
                LunaLog.Normal($"NEW SERVER: {netMsg.SenderEndPoint}");
            }
        }

        private static void HandleBindingRequest(NetIncomingMessage netMsg, NetPeer peer)
        {
            LunaLog.Normal($"STUN REQUEST from: {netMsg.SenderEndPoint}");

            var msgData = MasterServerMessageFactory.CreateNewMessageData<MsSTUNSuccessResponseMsgData>();

            msgData.TransportAddress = netMsg.SenderEndPoint;

            var msg = MasterServerMessageFactory.CreateNew<MainMstSrvMsg>(msgData);
            var outMsg = peer.CreateMessage(msg.GetMessageSize());

            msg.Serialize(outMsg);
            peer.SendUnconnectedMessage(outMsg, netMsg.SenderEndPoint);

            //Force send of packets
            peer.FlushSendQueue();
        }

        private static void RemoveExpiredServers()
        {
            var t = Task.Run(async () =>
            {
                while (RunServer)
                {
                    var currentTimeInTicks = LunaNetworkTime.UtcNow.Ticks;
                    var serversIdsToRemove = ServerDictionary
                        .Where(s => currentTimeInTicks - s.Value.LastRegisterTime > ServerTimeout.Ticks
                                    || BannedIpsRetriever.IsBanned(s.Value.ExternalEndpoint))
                        .ToArray();

                    foreach (var serverId in serversIdsToRemove)
                    {
                        LunaLog.Normal($"REMOVING SERVER: {serverId.Value.ExternalEndpoint}");
                        ServerDictionary.TryRemove(serverId.Key, out _);
                    }

                    await Task.Delay(OfflineServerCleanupInterval);
                }
            });
            _ = t.ContinueWith(
                (t2) => {
                    LunaLog.Fatal(t2.Exception.ToString());
                    Environment.Exit(1);
                },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default
            );
        }

        private static void StartCountryCodeRefreshTask()
        {
            var t = Task.Run(async () =>
            {
                while(RunServer)
                {
                    if (Server.CountryCodeRefreshQueue.TryDequeue(out var item))
                    {
                        (var id, var endpoint) = item;
                        if (ServerDictionary.TryGetValue(id, out var server))
                        {
                            try 
                            {
                                if (await server.SetCountryFromEndpointAsync(endpoint))
                                    await Task.Delay(Server.MinCountryCodeRefreshInterval);
                                else
                                    Server.CountryCodeRefreshQueue.Enqueue(item);
                            }
                            catch
                            {
                                Server.CountryCodeRefreshQueue.Enqueue(item);
                            }
                        }
                    } else {
                        await Task.Delay(CountryCodeRefreshInterval);
                    }
                }
            });
            _ = t.ContinueWith(
                (t2) => {
                    LunaLog.Fatal(t2.Exception.ToString());
                    Environment.Exit(1);
                },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default
            );
        }
    }
}

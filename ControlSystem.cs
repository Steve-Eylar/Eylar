using Crestron.SimplSharp;
using Crestron.SimplSharpPro;
using Heijden.DNS;
using Microsoft.Owin.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Eylar
{
    public class ControlSystem : CrestronControlSystem
    {
        public ControlSystem()
            : base()
        {
            try
            {
                Crestron.SimplSharpPro.CrestronThread.Thread.MaxNumberOfUserThreads = 20;

                CrestronEnvironment.SystemEventHandler += new SystemEventHandler(_ControllerSystemEventHandler);
                CrestronEnvironment.ProgramStatusEventHandler += new ProgramStatusEventHandler(_ControllerProgramEventHandler);
                CrestronEnvironment.EthernetEventHandler += new EthernetEventHandler(_ControllerEthernetEventHandler);
            }
            catch (Exception e)
            {
                ErrorLog.Error("Error in the constructor: {0}", e.Message);
            }
        }

        public override void InitializeSystem()
        {
            try
            {
                Task.Run(() => OwinServer());   // :8080
                Task.Run(() => Web());          // :1234
                Task.Run(() => OwinApiStart()); // :9000

                Crestron.SimplSharpPro.CrestronThread.Thread.Sleep(1000);
                var client = new Client("Web");
                client.Get("http://localhost:1234/");

                Task.Run(() => BroadcastForServices());
            }
            catch (Exception e)
            {
                ErrorLog.Error("Error in InitializeSystem: {0}", e.Message);
            }
        }

        private async Task BroadcastForServices()
        {
            try
            {
                var ifaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                CrestronConsole.PrintLine($"Found {ifaces.Length} Network Interfaces.");
                foreach (var adapter in ifaces)
                {
                    CrestronConsole.PrintLine($"  {adapter.Name} Type:{adapter.NetworkInterfaceType} Multi:{adapter.SupportsMulticast} {adapter.Description}");

                    if (adapter.Name.StartsWith("eth"))
                    {
                        if (!adapter.SupportsMulticast)
                        {
                            CrestronConsole.PrintLine($"Adapter doesn't support Multicast");
                            //continue; // multicast is meaningless for this type of connection
                        }

                        if (OperationalStatus.Up != adapter.OperationalStatus)
                        {
                            CrestronConsole.PrintLine($"this adapter is off or not connected: {adapter.OperationalStatus}");
                            //continue; // this adapter is off or not connected
                        }

                        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        {
                            CrestronConsole.PrintLine($"Adapter is loopback address");
                            //continue; // strip out loopback addresses
                        }

                        var p = adapter.GetIPProperties().GetIPv4Properties();
                        if (null == p)
                        {
                            CrestronConsole.PrintLine($"IPv4 is not configured on this adapter");
                            //continue; // IPv4 is not configured on this adapter
                        }

                        var addresses = adapter.GetIPProperties().UnicastAddresses;
                        CrestronConsole.PrintLine($"Found {addresses.Count} Unicast Addresses.");

                        var retries = 5;
                        var scanTime = new TimeSpan(0, 1, 0);
                        var cancellationToken = new CancellationToken();
                        var requestBytes = new byte[1024];
                        var retryDelayMilliseconds = 500;

                        foreach (var ipv4Address in addresses)
                        {
                            var ifaceIndex = p.Index;
                            CrestronConsole.PrintLine($"Scanning on iface {adapter.Name} {ipv4Address.IPv4Mask}  idx {ifaceIndex}, IP: {ipv4Address}");

                            using (var client = new UdpClient())
                            {
                                for (var i = 0; i < retries; i++)
                                {
                                    try
                                    {
                                        var socket = client.Client;
                                        if (socket.IsBound) continue;

                                        socket.SetSocketOption(SocketOptionLevel.IP,
                                            SocketOptionName.MulticastInterface,
                                            IPAddress.HostToNetworkOrder(ifaceIndex));
                                        client.ExclusiveAddressUse = false;
                                        socket.SetSocketOption(SocketOptionLevel.Socket,
                                            SocketOptionName.ReuseAddress, true);
                                        socket.SetSocketOption(SocketOptionLevel.Socket,
                                            SocketOptionName.ReceiveTimeout, (int)scanTime.TotalMilliseconds);
                                        client.ExclusiveAddressUse = false;

                                        var localEp = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 5353);

                                        CrestronConsole.PrintLine($"Attempting to bind to {localEp} on adapter {adapter.Name}");
                                        socket.Bind(localEp);
                                        CrestronConsole.PrintLine($"Bound to {localEp}");

                                        var multicastAddress = System.Net.IPAddress.Parse("224.0.0.251");
                                        var multOpt = new MulticastOption(multicastAddress, ifaceIndex);
                                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multOpt);
                                        CrestronConsole.PrintLine("Bound to multicast address");

                                        // Start a receive loop
                                        var shouldCancel = false;
                                        var recTask = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                while (!Volatile.Read(ref shouldCancel))
                                                {
                                                    var res = await client.ReceiveAsync().ConfigureAwait(false);
                                                    onResponse(res.RemoteEndPoint.Address, res.Buffer);
                                                }
                                            }
                                            catch when (Volatile.Read(ref shouldCancel))
                                            {
                                                // If we're canceling, eat any exceptions that come from here   
                                            }
                                        }, cancellationToken);

                                        var broadcastEp = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("224.0.0.251"), 5353);

                                        CrestronConsole.PrintLine($"About to send on iface {adapter.Name}");
                                        await client.SendAsync(requestBytes, requestBytes.Length, broadcastEp).ConfigureAwait(false);
                                        CrestronConsole.PrintLine($"Sent mDNS query on iface {adapter.Name}");


                                        // wait for responses
                                        await Task.Delay(scanTime, cancellationToken).ConfigureAwait(false);

                                        Volatile.Write(ref shouldCancel, true);

                                        ((IDisposable)client).Dispose();
                                        CrestronConsole.PrintLine("Done Scanning");

                                        await recTask.ConfigureAwait(false);
                                        return;
                                    }
                                    catch (Exception e)
                                    {
                                        CrestronConsole.PrintLine($"Exception with network request, IP {ipv4Address}\n: {e}");
                                        if (i + 1 >= retries) // last one, pass underlying out
                                        {
                                            ExceptionDispatchInfo.Capture(e).Throw();
                                            throw;
                                        }
                                    }
                                    await Task.Delay(retryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"ShowNetworkInterfaces error: {e.Message}");
            }
        }

        private void onResponse(System.Net.IPAddress addr, byte[] buffer)
        {
            var dict = new Dictionary<string, Response>();
            var resp = new Response(new System.Net.IPEndPoint(addr, 5353), buffer);
            var firstPtr = resp.RecordsPTR.FirstOrDefault();
            var name = firstPtr?.PTRDNAME.Split('.')[0] ?? string.Empty;
            var addrString = addr.ToString();

            CrestronConsole.PrintLine($"onResponse: IP: {addrString}, {(string.IsNullOrEmpty(name) ? string.Empty : $"Name: {name}, ")}Bytes: {buffer.Length}, IsResponse: {resp.header.QR}");

            if (resp.header.QR)
            {
                var key = $"{addrString}{(string.IsNullOrEmpty(name) ? "" : $": {name}")}";
                lock (dict)
                {
                    dict[key] = resp;
                }
            }
            
            CrestronConsole.PrintLine($"onResponse: Response found {dict.Count} items");
            foreach (var kvp in dict)
            {
                CrestronConsole.PrintLine($"  {kvp.Key}={kvp.Value.header}");
            }
        }


        private void OwinApiStart()
        {
            string baseAddress = "http://127.0.0.1:9000/";

            try
            {
                using (WebApp.Start<OwinApi>(url: baseAddress))
                {
                    CrestronConsole.PrintLine("OwinApiStart Started");

                    var client = new Client("OwinApiStart");
                    client.Get(baseAddress + "api/owinapi");

                    Crestron.SimplSharpPro.CrestronThread.Thread.Sleep(Crestron.SimplSharp.Timeout.Infinite);
                }
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"OwinApiStart: {e}");
            }
            finally
            {
                CrestronConsole.PrintLine("OwinApiStart: Terminated");
            }
        }

        private void Web()
        {
            try
            {
                CrestronConsole.PrintLine("Web Starting");
                new WebServer(1234);
                CrestronConsole.PrintLine("Web Started");
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"Web: {e}");
            }
            finally
            {
                CrestronConsole.PrintLine("Web: Terminated");
            }

        }

        private void OwinServer()
        {
            try
            {
                using (WebApp.Start<OwinStartup>("http://127.0.0.1:8080"))
                {
                    CrestronConsole.PrintLine("OwinServer Started");

                    var client = new Client("OwinServer");
                    client.Get("http://127.0.0.1:8080/");

                    Crestron.SimplSharpPro.CrestronThread.Thread.Sleep(Crestron.SimplSharp.Timeout.Infinite);
                }
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"OwinServer: Error {e}");
            }
            finally
            {
                CrestronConsole.PrintLine("OwinServer: Terminated");
            }
        }

        void _ControllerEthernetEventHandler(EthernetEventArgs ethernetEventArgs)
        {
            switch (ethernetEventArgs.EthernetEventType)
            {//Determine the event type Link Up or Link Down
                case (eEthernetEventType.LinkDown):
                    if (ethernetEventArgs.EthernetAdapter == EthernetAdapterType.EthernetLANAdapter)
                    {
                    }
                    break;
                case (eEthernetEventType.LinkUp):
                    if (ethernetEventArgs.EthernetAdapter == EthernetAdapterType.EthernetLANAdapter)
                    {
                    }
                    break;
            }
        }

        void _ControllerProgramEventHandler(eProgramStatusEventType programStatusEventType)
        {
            switch (programStatusEventType)
            {
                case (eProgramStatusEventType.Paused):
                    break;
                case (eProgramStatusEventType.Resumed):
                    break;
                case (eProgramStatusEventType.Stopping):
                    break;
            }

        }

        void _ControllerSystemEventHandler(eSystemEventType systemEventType)
        {
            switch (systemEventType)
            {
                case (eSystemEventType.DiskInserted):
                    break;
                case (eSystemEventType.DiskRemoved):
                    break;
                case (eSystemEventType.Rebooting):
                    break;
            }
        }
    }
}
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;
using WifiDirectService.Services;
using WifiDirectService.Protocol;
using WifiDirectService.Sockets;

class Program
{
    static Service wifiService = null!;
    private static CancellationTokenSource? _discoveryCts;
    private static readonly Sockets socket = new Sockets();
    private static SendJsonMessage smj = new SendJsonMessage();

    static async Task Main(string[] args)
    {
        wifiService = new Service();
        while (true)
        {
            string? input = await Console.In.ReadLineAsync();
            if (string.IsNullOrEmpty(input)) continue;

            try
            {
                // Parseamos el comando enviado por Electron
                var command = JsonSerializer.Deserialize<Command>(input);
                if (command == null) continue;

                string ipAddress = command.Ip;

                switch (command.Action)
                {
                    case "START_DISCOVERY":
                        StartDiscovery();
                        break;

                    case "STOP_DISCOVERY":
                        wifiService.Stop();
                        StopDiscovery();
                        break;

                    case "CONNECT":
                        string? idRecibido = command.Values;
                        await wifiService.ConnectToDevice(idRecibido);
                        break;

                    case "START_SERVER":
                        _ = Task.Run(async () =>
                        {
                            await socket.StartServerSocket(ipAddress);
                        });

                        socket.Dispose();
                        break;

                    case "START_CLIENT":
                        string? file = command.Values;
                        await socket.StartClientSocket(ipAddress, file);
                        break;

                    case "EXIT":
                        socket.Dispose();
                        wifiService.Stop();
                        return;
                }
            }
            catch (Exception ex)
            {
                smj.SendMessage(new { error = ex.Message });
            }
        }
    }

    static void StartDiscovery()
    {
        wifiService.Init();
        _discoveryCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            while (!_discoveryCts.Token.IsCancellationRequested)
            {
                var snapshot = wifiService.discoveredDevices.ToArray();
             
                var deviceList = snapshot.Select(kvp => new {
                    id = kvp.Value.Id,
                    name = string.IsNullOrEmpty(kvp.Value.Name) ? "Dispositivo anónimo" : kvp.Value.Name
                }).ToList();

                // Enviamos la lista como JSON a Electron
                smj.SendMessage(new { event_type = "DEVICES_UPDATED", devices = deviceList });

                await Task.Delay(3000);
            }
        });
    }

    static void StopDiscovery()
    {
        _discoveryCts?.Cancel();
        smj.SendMessage(new { status = "DISCOVERY_STOPPED" });
    }
}
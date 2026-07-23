using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;
using WifiDirectService.Services;
using WifiDirectService.Protocol;

class Program
{
    static Service wifiService = null!;
    private static CancellationTokenSource? _discoveryCts;

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

                    case "GET_FILE":
                        string? nameFile = command.Values;
                        wifiService.GetFile(nameFile);
                        break;

                    case "EXIT":
                        return;
                }
            }
            catch (Exception ex)
            {
                SendJson(new { error = ex.Message });
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
                SendJson(new { event_type = "DEVICES_UPDATED", devices = deviceList });

                await Task.Delay(3000);
            }
        });
    }

    static void StopDiscovery()
    {
        _discoveryCts?.Cancel();
        SendJson(new { status = "DISCOVERY_STOPPED" });
    }

    static void SendJson(object data)
    {
        string json = JsonSerializer.Serialize(data);
        Console.WriteLine(json);
    }
}
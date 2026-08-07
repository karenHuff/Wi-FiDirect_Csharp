using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using WifiDirectService.Services;

class Program
{
    static Service wifiService = null!;

    static async Task Main(string[] args)
    {
        try
        {
            wifiService = new Service();
            while (true)
            {
                Console.Clear();
                Console.WriteLine("WI-FI DIRECT");
                Console.WriteLine("---------------------------------------");
                Console.WriteLine("1. Iniciar descubrimiento y anunciar");
                Console.WriteLine("2. Conectar dispositivo");
                Console.WriteLine("3. Detener servicio");
                Console.WriteLine("---------------------------------------");
                Console.Write("\nSelecciona una opción: ");

                string? option = Console.ReadLine();

                switch (option)
                {
                    case "1":
                        wifiService.Init();

                        var cts = new CancellationTokenSource();

                        Task renderTask = Task.Run(async () =>
                        {
                            while (!cts.Token.IsCancellationRequested)
                            {
                                Console.Clear();
                                Console.WriteLine("========================================");
                                Console.WriteLine("       ¡DISPOSITIVOS ENCONTRADOS!       ");
                                Console.WriteLine(" (Presione cualquier tecla para salir)  ");
                                Console.WriteLine("========================================");

                                // leemos desde DiscoveredDevices
                                var snapshot = wifiService.discoveredDevices.ToArray();

                                    int index = 1;
                                    foreach (var kvp in snapshot)
                                    {
                                        var dev = kvp.Value;
                                        string name = string.IsNullOrEmpty(dev.Name) ? "Dispositivo anónimo / Sin nombre" : dev.Name;

                                        Console.WriteLine($"{index}. Nombre: {name}");
                                        Console.WriteLine($"   ID    : {dev.Id}");
                                        Console.WriteLine("----------------------------------------");
                                        index++;
                                    }                                

                                await Task.Delay(3000);
                            }
                        });

                        Console.ReadKey(true);

                        cts.Cancel();
                        await renderTask;
                        break;

                    case "2":
                        await wifiService.ConnectToDevice();
                        Console.ReadKey();
                        break;

                    case "3":
                        wifiService.Stop();
                        Console.ReadKey();
                        break;

                    default:
                        Console.WriteLine("\nOpción inválida. Presiona una tecla para continuar...");
                        Console.ReadKey();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nOcurrió un error general: {ex.Message}");
            Console.ReadKey();
            return;
        }
    }
}
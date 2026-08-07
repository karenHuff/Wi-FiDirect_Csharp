using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using WifiDirectService.Sockets;

namespace WifiDirectService.Services
{
    public class Service : IDisposable
    {
        public readonly ConcurrentDictionary<string, DeviceInformation> discoveredDevices = new ConcurrentDictionary<string, DeviceInformation>();
        private readonly List<WiFiDirectDevice> _connectedDevices = new List<WiFiDirectDevice>();

        private DeviceWatcher? watcher;
        private WiFiDirectAdvertisementPublisher? publisher;
        private WiFiDirectConnectionListener? connectionListener;
        private bool isDisposed;

        static Socket socket = new Socket();

        public IReadOnlyDictionary<string, DeviceInformation> DiscoveredDevices => discoveredDevices;

        // iniciar publicador
        public void Init()
        {
            if (watcher != null) return;

            try
            {
                discoveredDevices.Clear();

                // iniciar publicador
                publisher = new WiFiDirectAdvertisementPublisher();
                publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                publisher.Advertisement.IsAutonomousGroupOwnerEnabled = false;

                publisher.Advertisement.SupportedConfigurationMethods.Clear();
                publisher.Advertisement.SupportedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                publisher.Start();

                Console.WriteLine("Iniciando publicador");
                
                // escuchar solicitudes de conexión entrantes
                connectionListener = new WiFiDirectConnectionListener();                
                connectionListener.ConnectionRequested += OnConnectionRequested;

                // configurar watcher
                string selector = WiFiDirectDevice.GetDeviceSelector(
                    WiFiDirectDeviceSelectorType.AssociationEndpoint);

                watcher = DeviceInformation.CreateWatcher(
                    selector,
                    new string[] { "System.Devices.Aep.DeviceAddress" },
                    DeviceInformationKind.AssociationEndpoint
                );

                watcher.Added += OnDeviceAdded;
                watcher.Removed += OnDeviceRemoved;
                watcher.Updated += OnDeviceUpdated;
                watcher.Stopped += OnWatcherStopped;

                watcher.Start();

                Console.WriteLine("Discovery started");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nOcurrió un error: {ex.Message}");
            }
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
        {
            discoveredDevices.AddOrUpdate(
                device.Id,
                device,
                (id, oldDevice) => device
            );
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            Task.Run(async () =>
            {
                try
                {
                    var updatedDevice = await DeviceInformation.CreateFromIdAsync(update.Id);
                    discoveredDevices.AddOrUpdate(updatedDevice.Id, updatedDevice, (id, old) => updatedDevice);
                }
                catch { }
            });
        }


        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            Console.WriteLine($"[REMOVED] {update.Id}");
            discoveredDevices.TryRemove(update.Id, out _);
        }


        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            Console.WriteLine("discovery stopped ");
        }

        // manejo de solicitudes entrantes
        private async void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            Console.WriteLine("\n¡Solicitud de conexión Wi-Fi Direct recibida!");

            try
            {
                WiFiDirectConnectionRequest connectionRequest = args.GetConnectionRequest();

                WiFiDirectConnectionParameters parameters = new WiFiDirectConnectionParameters();
                parameters.GroupOwnerIntent = 0; // Preferencia para ser cliente
                parameters.PreferenceOrderedConfigurationMethods.Clear();
                parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                string deviceId = connectionRequest.DeviceInformation.Id;
                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(deviceId, parameters);
                
                if (wfdDevice == null)
                {
                    Console.WriteLine("Error: No se pudo obtener la instancia de WiFiDirectDevice (Devolvió null).");
                    return;
                }

                RegisterConnectedDevice(wfdDevice);

                var endpointPairs = wfdDevice.GetConnectionEndpointPairs();
                if (endpointPairs.Count > 0)
                {
                    // obtener IP
                    string remoteHost = endpointPairs[0].RemoteHostName.RawName;

                    Console.WriteLine("\n====== CONEXIÓN ESTABLECIDA CON ÉXITO ======");
                    Console.WriteLine($"IP Local: {endpointPairs[0].LocalHostName.RawName}");
                    Console.WriteLine($"IP Remota: {endpointPairs[0].RemoteHostName.RawName}");
                    Console.WriteLine("============================================\n");

                    await Task.Delay(1500);

                    // Iniciar socket
                    _ = Task.Run(async () =>
                    {
                        try 
                        {
                            await socket.StartClientSocket(remoteHost, "");
                        } 
                        catch (Exception socketEx)
                        {
                            Console.WriteLine($"\nError en el cliente: {socketEx.Message}");
                        }
                    });
                }
                else
                {
                    Console.WriteLine("No se pudieron negociar los Endpoints de red.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al procesar conexión entrante: {ex.Message}");
            }
        }

        // Conectar dispositivos
        public async Task ConnectToDevice()
        {
            if (watcher != null && watcher.Status == DeviceWatcherStatus.Started)
            {
                watcher.Stop();
            }

            if (discoveredDevices.IsEmpty)
            {
                Console.WriteLine("\nNo hay dispositivos a los que conectarse.");
                return;
            }

            var snapshot = discoveredDevices.ToArray();

            Console.WriteLine("\n=== SELECCIONAR DISPOSITIVO PARA CONECTAR ===");

            for (int i = 0; i < snapshot.Length; i++)
            {
                string name = string.IsNullOrEmpty(snapshot[i].Value.Name) ? "Dispositivo anónimo" : snapshot[i].Value.Name;
                Console.WriteLine($"{i + 1}. {name}");
            }

            Console.Write("\nSelecciona el número del dispositivo: ");
            if (!int.TryParse(Console.ReadLine(), out int index) || index < 1 || index > snapshot.Length)
            {
                return;
            }

            var selectDevice = snapshot[index - 1].Value;
            string deviceId = selectDevice.Id;

            if (!discoveredDevices.TryGetValue(deviceId, out var selectedDevice))
            {
                Console.WriteLine("El dispositivo ya no está disponible.");
                return;
            }

            try
            {
                // configurar parámetros de conexión estándar
                WiFiDirectConnectionParameters parameters = new WiFiDirectConnectionParameters();
                parameters.GroupOwnerIntent = 14; // propietario del grupo
                parameters.PreferenceOrderedConfigurationMethods.Clear();
                parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                DeviceInformation deviceInfo = await DeviceInformation.CreateFromIdAsync(selectedDevice.Id);

                // Proceso de Emparejamiento por Software Customizado
                if (deviceInfo.Pairing != null && !deviceInfo.Pairing.IsPaired)
                {
                    Console.WriteLine("Emparejando de forma automática por software...");

                    deviceInfo.Pairing.Custom.PairingRequested += (sender, args) =>
                    {
                        args.Accept();
                    };

                    var pairingTask = deviceInfo.Pairing.Custom.PairAsync(
                        DevicePairingKinds.ConfirmOnly,
                        DevicePairingProtectionLevel.None
                    ).AsTask();

                    var timeoutTask = Task.Delay(15000);
                    var completedTask = await Task.WhenAny(pairingTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        Console.WriteLine("Tiempo de espera agotado para el emparejamiento. Intentando conexión directa...");
                    }
                    else
                    {
                        DevicePairingResult pairingResult = await pairingTask;
                        Console.WriteLine($"Resultado del emparejamiento: {pairingResult.Status}");
                    }
                }

                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(selectedDevice.Id, parameters);

                RegisterConnectedDevice(wfdDevice);

                var endpointPairs = wfdDevice.GetConnectionEndpointPairs();
                if (endpointPairs.Count > 0)
                {
                    string localHost = endpointPairs[0].LocalHostName.RawName;

                    Console.WriteLine("\n====== CONEXIÓN ESTABLECIDA CON ÉXITO ======");
                    Console.WriteLine($"IP Servidor: {endpointPairs[0].LocalHostName.RawName}");
                    Console.WriteLine($"IP Cliente: {endpointPairs[0].RemoteHostName.RawName}");
                    Console.WriteLine("============================================\n");

                    _ = Task.Run(async () => {
                        try
                        {
                            await socket.StartServerSocket(endpointPairs[0].LocalHostName.RawName);
                        }
                        catch (Exception socketEx)
                        {
                            Console.WriteLine($"Error en el servidor: {socketEx.Message}");
                        }
                    });
                }
                else
                {
                    Console.WriteLine("No se pudieron negociar los Endpoints de red.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ocurrió un error al conectar dispositivo: {ex.Message}");
            }
        }

        private void RegisterConnectedDevice(WiFiDirectDevice device)
        {
            lock (_connectedDevices) {
                _connectedDevices.Add(device);
            }
        }

        private void StopWatcher() {
            if (watcher != null)
            {
                watcher.Stop();
            }
        }

        public void Stop()
        {
            lock (_connectedDevices)
            {
                foreach (var wfdDevice in _connectedDevices)
                {
                    try
                    {
                        wfdDevice.Dispose();
                    }
                    catch { }
                }

                _connectedDevices.Clear();
            }

            // detener listener
            if (connectionListener != null)
            {
                connectionListener.ConnectionRequested -= OnConnectionRequested;
                connectionListener = null;
            }

            // detener watcher
            if (watcher != null)
            {
                watcher.Added -= OnDeviceAdded;
                watcher.Removed -= OnDeviceRemoved;
                watcher.Updated -= OnDeviceUpdated;
                watcher.Stopped -= OnWatcherStopped;

                StopWatcher();
                watcher = null;
            }

            // Detener publisher
            if (publisher != null)
            {
                publisher.Stop();
                publisher = null;
                Console.WriteLine("Plublisher stopped");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed) return;

            if (disposing)
            {
                Stop();
            }

            isDisposed = true;
        }
    }
}
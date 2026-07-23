using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json; 
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

using WifiDirectService.Sockets;

namespace WifiDirectService.Services
{
    public class Service : IDisposable
    {
        public readonly ConcurrentDictionary<string, DeviceInformation> discoveredDevices =
            new ConcurrentDictionary<string, DeviceInformation>();

        private readonly List<WiFiDirectDevice> _connectedDevices = new List<WiFiDirectDevice>();

        private DeviceWatcher? watcher;
        private WiFiDirectAdvertisementPublisher? publisher;
        private WiFiDirectConnectionListener? connectionListener;
        private Socket socket = new Socket();

        string? nameFile;

        private void SendJsonMessage(object data)
        {
            Console.WriteLine(JsonSerializer.Serialize(data));
        }

        public void Init()
        {
            if (watcher != null) return;

            try
            {
                discoveredDevices.Clear();

                // Iniciar publicador
                publisher = new WiFiDirectAdvertisementPublisher();
                publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                publisher.Advertisement.IsAutonomousGroupOwnerEnabled = false;

                publisher.Advertisement.SupportedConfigurationMethods.Clear();
                publisher.Advertisement.SupportedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                publisher.Start();

                connectionListener = new WiFiDirectConnectionListener();
                connectionListener.ConnectionRequested += OnConnectionRequested;

                SendJsonMessage(new { event_type = "PUBLISHER_STARTED", message = "Iniciando publicador" });

                // Configurar watcher
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

                SendJsonMessage(new { event_type = "DISCOVERY_STARTED", message = "Discovery started" });
            }
            catch (Exception ex)
            {
                SendJsonMessage(new { event_type = "ERROR", message = $"Ocurrió un error: {ex.Message}" });
            }
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
        {
            discoveredDevices.AddOrUpdate(device.Id, device, (id, oldDevice) => device);
            NotifyDevicesChanged();
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            Task.Run(async () =>
            {
                try
                {
                    var updatedDevice = await DeviceInformation.CreateFromIdAsync(update.Id);
                    discoveredDevices.AddOrUpdate(updatedDevice.Id, updatedDevice, (id, old) => updatedDevice);
                    NotifyDevicesChanged();
                }
                catch { }
            });
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            discoveredDevices.TryRemove(update.Id, out _);
            NotifyDevicesChanged();
        }

        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            SendJsonMessage(new { event_type = "DISCOVERY_STOPPED", message = "discovery stopped" });
        }

        // Envía la lista a Electron cada vez que el Watcher detecta un cambio
        private void NotifyDevicesChanged()
        {
            var deviceList = discoveredDevices.Values.Select(dev => new {
                id = dev.Id,
                name = string.IsNullOrEmpty(dev.Name) ? "Dispositivo anónimo / Sin nombre" : dev.Name
            }).ToList();

            SendJsonMessage(new { event_type = "DEVICES_UPDATED", devices = deviceList });            
        }

        // Manejo de solicitudes entrantes
        private async void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            WiFiDirectConnectionParameters parameters = new WiFiDirectConnectionParameters();
            parameters.GroupOwnerIntent = 0; // dar prioridad al ususario que solicita la conexión
            parameters.PreferenceOrderedConfigurationMethods.Clear();
            parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

            try
            {
                SendJsonMessage(new { event_type = "REQUEST", message = "Solicitud de conexión Wi-Fi Direct recibida" });
                WiFiDirectConnectionRequest connectionRequest = args.GetConnectionRequest();

                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(connectionRequest.DeviceInformation.Id, parameters);

                // guardar el dispositivo en la lista
                lock (_connectedDevices)
                {
                    _connectedDevices.Add(wfdDevice);
                }

                var endpointPairs = wfdDevice.GetConnectionEndpointPairs();

                if (endpointPairs.Count > 0)
                {
                    SendJsonMessage(new { 
                        event_type = "CONNECTION_SUCCESS",
                        message = "Dispositivo conectado",
                        status = "cliente",
                        ip_servidor = endpointPairs[0].RemoteHostName.RawName
                    });

                    await Task.Delay(3000);

                    await socket.StartClientSocket(endpointPairs[0].RemoteHostName.RawName, nameFile);
                }
            }
            catch (Exception ex)
            {
                SendJsonMessage(new { event_type = "CONNECTION_FAILED", message = $"Error al procesar la conexión entrante: {ex.Message}" });
            }
        }

        // conectar dispositivos
        public async Task ConnectToDevice(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                SendJsonMessage(new { event_type = "CONNECTION_FAILED", message = "El ID del dispositivo no es válido." });
                return;
            }

            if (!discoveredDevices.TryGetValue(deviceId, out var selectedDevice))
            {
                SendJsonMessage(new { event_type = "CONNECTION_FAILED", message = "El dispositivo ya no está disponible en la lista." });
                return;
            }

            SendJsonMessage(new { event_type = "CONNECTION_ATTEMPT", message = $"Intentando conectar a: {selectedDevice.Name}..." });

            try
            {
                // configurar parámetros de conexión estándar
                WiFiDirectConnectionParameters parameters = new WiFiDirectConnectionParameters();
                parameters.GroupOwnerIntent = 14; // definir como propietario del grupo
                parameters.PreferenceOrderedConfigurationMethods.Clear();
                parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                DeviceInformation deviceInfo = await DeviceInformation.CreateFromIdAsync(selectedDevice.Id);
                
                // proceso de emparejamiento por software customatizado
                if (deviceInfo.Pairing != null && !deviceInfo.Pairing.IsPaired)
                {
                    SendJsonMessage(new { event_type = "PAIRING_ATTEMPT", message = "Verificando enlace de seguridad..." });

                    // Activamos un emparejamiento para windows
                    deviceInfo.Pairing.Custom.PairingRequested += (sender, args) =>
                    {
                        // Aceptamos automáticamente cualquier confirmación para agilizar la conexión entre PCs
                        args.Accept();
                    };

                    var pairingTask = deviceInfo.Pairing.Custom.PairAsync(
                        DevicePairingKinds.ConfirmOnly,
                        DevicePairingProtectionLevel.None
                    ).AsTask();

                    var delayTask = Task.Delay(15000);

                    var completedTask = await Task.WhenAny(pairingTask, delayTask);

                    if (completedTask == delayTask)
                    {
                        SendJsonMessage(new { event_type = "PAIRING_SKIPPED", message = "Tiempo de espera agotado para el emparejamiento. Intentando conexión dorecta..." });
                    }
                    else
                    {
                        DevicePairingResult pairingResult = await pairingTask;
                        SendJsonMessage(new { event_type = "PAIRING_RESULT", message = $"Resultado del emparejamiento: {pairingResult.Status}" });
                    }
                }

                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(selectedDevice.Id);

                // guardar el dispositivo en la lista
                lock (_connectedDevices)
                {
                    _connectedDevices.Add(wfdDevice);
                }

                var endpointPairs = wfdDevice.GetConnectionEndpointPairs();

                if (endpointPairs.Count > 0)
                {
                    SendJsonMessage(new
                    {
                        event_type = "CONNECTION_SUCCESS",
                        message = "Dipspositivo conectado",
                        status = "servidor",
                        ip_servidor = endpointPairs[0].LocalHostName.RawName
                    });

                    // Iniciar servidor
                    _ = Task.Run(() => socket.StartServerSocket(endpointPairs[0].LocalHostName.RawName));
                }
                else
                {
                    SendJsonMessage(new { event_type = "CONNECTION_FAILED", message = "No se pudieron negociar los endpoints de red" });
                }
            }
            catch (Exception ex)
            {
                SendJsonMessage(new { event_type = "CONNECTION_FAILED", message = ex.Message });
            }
        }

        // Obtener archivo del front
        public void GetFile(string? file)
        {
            nameFile = file;
        }

        public void Stop()
        {
            lock (_connectedDevices) {
                foreach (var wfdDevice in _connectedDevices)
                {
                    try
                    {
                        wfdDevice.Dispose();
                    }
                    catch { }
                }

                _connectedDevices.Clear();
                SendJsonMessage(new { event_type = "DROP_GROUP", message = "Grupos eliminados" });
            }

            if (connectionListener != null)
            {
                connectionListener.ConnectionRequested -= OnConnectionRequested;
                connectionListener = null;
            }

            if (watcher != null)
            {
                watcher.Stop();
                watcher = null;
            }

            if (publisher != null)
            {
                publisher.Stop();
                publisher = null;
                SendJsonMessage(new { event_type = "PUBLISHER_STOPPED", message = "Publisher stopped" });
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
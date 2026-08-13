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

using WifiDirectService.Protocol;

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

        private static SendJsonMessage sjm = new SendJsonMessage();
        public IReadOnlyDictionary<string, DeviceInformation> DiscoveredDevices => discoveredDevices;

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

                sjm.SendMessage(new { event_type = "PUBLISHER_STARTED", message = "Iniciando publicador" });

                connectionListener = new WiFiDirectConnectionListener();

                // escuchar solicitudes entrantes
                connectionListener.ConnectionRequested += OnConnectionRequested;

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

                sjm.SendMessage(new { event_type = "DISCOVERY_STARTED", message = "Discovery started" });
            }
            catch (Exception ex)
            {
                sjm.SendMessage(new { event_type = "ERROR", message = $"Ocurrió un error: {ex.Message}" });
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
            sjm.SendMessage(new { event_type = "DISCOVERY_STOPPED", message = "discovery stopped" });
        }

        // Envía la lista a Electron cada vez que el Watcher detecta un cambio
        private void NotifyDevicesChanged()
        {
            var deviceList = discoveredDevices.Values.Select(dev => new {
                id = dev.Id,
                name = string.IsNullOrEmpty(dev.Name) ? "Dispositivo anónimo / Sin nombre" : dev.Name
            }).ToList();

            sjm.SendMessage(new { event_type = "DEVICES_UPDATED", devices = deviceList });            
        }

        // Manejo de solicitudes entrantes
        private async void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            try
            {
                sjm.SendMessage(new { event_type = "REQUEST", message = "¡Solicitud de conexión Wi-Fi Direct recibida!" });
                WiFiDirectConnectionRequest connectionRequest = args.GetConnectionRequest();

                WiFiDirectConnectionParameters parameters = new WiFiDirectConnectionParameters();
                parameters.GroupOwnerIntent = 0; // Preferencia para ser cliente
                parameters.PreferenceOrderedConfigurationMethods.Clear();
                parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                string deviceId = connectionRequest.DeviceInformation.Id;
                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(deviceId, parameters);


                if (wfdDevice == null)
                {
                    sjm.SendMessage(new { event_type = "ERROR", message = "No se pudo obtener la instancia de WiFiDirectDevice" });
                    return;
                }

                RegisterConnectedDevice(wfdDevice);

                var endpointPairs = wfdDevice.GetConnectionEndpointPairs();
                if (endpointPairs.Count > 0)
                {
                    string remoteHost = endpointPairs[0].RemoteHostName.RawName;
                    sjm.SendMessage(new
                    {
                        event_type = "CONNECTION_SUCCESS",
                        message = "Dispositivo conectado",
                        status = "cliente",
                        ip_servidor = remoteHost
                    });
                }
                else
                {
                    sjm.SendMessage(new { type_event = "ERROR", message = "No se pudieron negociar los Endpoint de red" });
                }
            }
            catch (Exception ex)
            {
                sjm.SendMessage(new { event_type = "CONNECTION_FAILED", message = $"Error al procesar la conexión entrante: {ex.Message}" });
                sjm.SendMessage(new { event_type = "CONNECION_FAILED", message = $"HResult: {ex.HResult}" });
            }
        }

        // conectar dispositivos
        public async Task ConnectToDevice(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                sjm.SendMessage(new { event_type = "CONNECTION_FAILED", message = "El ID del dispositivo no es válido." });
                return;
            }

            if (!discoveredDevices.TryGetValue(deviceId, out var selectedDevice))
            {
                sjm.SendMessage(new { event_type = "CONNECTION_FAILED", message = "El dispositivo ya no está disponible en la lista." });
                return;
            }

            sjm.SendMessage(new { event_type = "CONNECTION_ATTEMPT", message = $"Intentando conectar a: {selectedDevice.Name}..." });

            try
            {
                // configurar parámetros de conexión estándar
                WiFiDirectConnectionParameters parameters = new WiFiDirectConnectionParameters();
                parameters.GroupOwnerIntent = 14; // prioridad para ser propietario del grupo
                parameters.PreferenceOrderedConfigurationMethods.Clear();
                parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                DeviceInformation deviceInfo = await DeviceInformation.CreateFromIdAsync(selectedDevice.Id);
                
                // proceso de emparejamiento por software customizado
                if (deviceInfo.Pairing != null && !deviceInfo.Pairing.IsPaired)
                {
                    sjm.SendMessage(new { event_type = "PAIRING_ATTEMPT", message = "Verificando enlace de seguridad..." });

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

                    var timeoutTask = Task.Delay(15000);
                    var completedTask = await Task.WhenAny(pairingTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        sjm.SendMessage(new { event_type = "PAIRING_SKIPPED", message = "Tiempo de espera agotado para el emparejamiento. Intentando conexión directa..." });
                    }
                    else
                    {
                        DevicePairingResult pairingResult = await pairingTask;
                        sjm.SendMessage(new { event_type = "PAIRING_RESULT", message = $"Resultado del emparejamiento: {pairingResult.Status}" });
                    }
                }

                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(selectedDevice.Id, parameters);

                RegisterConnectedDevice(wfdDevice);

                var endpointPairs = wfdDevice.GetConnectionEndpointPairs();
                if (endpointPairs.Count > 0)
                {
                    string localhost = endpointPairs[0].LocalHostName.RawName;
                    sjm.SendMessage(new
                    {
                        event_type = "CONNECTION_SUCCESS",
                        message = "Dipspositivo conectado",
                        status = "servidor",
                        ip_servidor = localhost
                    });
                }
                else
                {
                    sjm.SendMessage(new { event_type = "CONNECTION_FAILED", message = "No se pudieron negociar los endpoints de red" });
                }
            }
            catch (Exception ex)
            {
                sjm.SendMessage(new { event_type = "CONNECTION_FAILED", message = ex.Message });
            }
        }

        private void RegisterConnectedDevice(WiFiDirectDevice device)
        {
            lock (_connectedDevices)
            {
                _connectedDevices.Add(device);
            }
        }

        private void StopWatcher()
        {
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

            Dispose();
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
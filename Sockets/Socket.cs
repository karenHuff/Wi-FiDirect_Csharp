using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WifiDirectService.Sockets
{
    public class Socket : IDisposable
    {
        private TcpListener? listener;
        private const int PORT = 8881;
        private const string DIR = @"C:/archivosRecibidos/";
        private bool isDisposed;

        // Iniciar servidor
        public async Task StartServerSocket(string ipAddress, CancellationToken cancellationToken = default)
        {
            try
            {
                listener = new TcpListener(IPAddress.Parse(ipAddress), PORT);
                Console.WriteLine($"Servidor escuchando en el puerto: {PORT}");
                listener.Start();

                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    Console.WriteLine("Cliente conectado!");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            using (NetworkStream stream = client.GetStream())
                            {
                                // leer longitud del nombre
                                byte[] nameLengthBuffer = new byte[2];
                                await stream.ReadExactlyAsync(nameLengthBuffer, 0, 2, cancellationToken);
                                ushort nameLength = BinaryPrimitives.ReadUInt16BigEndian(nameLengthBuffer);

                                // leer nombre del archivo
                                byte[] nameBytes = new byte[nameLength];
                                await stream.ReadExactlyAsync(nameBytes, 0, nameLength, cancellationToken);
                                string fileName = Encoding.UTF8.GetString(nameBytes);

                                // leer tamaño del archivo
                                byte[] fileSizeBuffer = new byte[8];
                                await stream.ReadExactlyAsync(fileSizeBuffer, 0, 8, cancellationToken);
                                long fileSize = BinaryPrimitives.ReadInt64BigEndian(fileSizeBuffer);

                                Console.WriteLine($"Recibiendo '{fileName}' ({fileSize} bytes)...");

                                // crear directorio si no existe
                                Directory.CreateDirectory(DIR);

                                // limpiar nombre del archivo
                                string cleanName = Path.GetFileName(fileName);
                                string fullPath = Path.Join(DIR, cleanName);

                                byte[] buffer = new byte[8192];
                                long totalRead = 0;

                                using (FileStream fs = new FileStream(
                                           fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                                {
                                    while (totalRead < fileSize)
                                    {
                                        int toRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                                        int bytesRead = await stream.ReadAsync(buffer, 0, toRead);

                                        if (bytesRead <= 0)
                                            throw new EndOfStreamException("La conexión se cerró inesperadamente antes de completar el archivo.");

                                        await fs.WriteAsync(buffer, 0, bytesRead);
                                        totalRead += bytesRead;
                                    }

                                    Console.WriteLine($"Archivo {cleanName} recibido con éxito.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error al procesar cliente: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ocurrió un error: {ex.Message}");
            }
        }

        // Iniciar cliente y transferencia de archivo
        public async Task StartClientSocket(string ipAddress, string? filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    throw new FileNotFoundException("El archivo que intentas enviar no existe.", filePath);
                }

                using var client = new TcpClient();
                await client.ConnectAsync(ipAddress, PORT);

                using NetworkStream stream = client.GetStream();
                using BinaryWriter writer = new BinaryWriter(stream);

                string fileName = Path.GetFileName(filePath);
                byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
                ushort nameLength = (ushort)nameBytes.Length;
                long fileSize = new FileInfo(filePath).Length;

                Console.WriteLine($"Conectado al servidor. Enviando {fileName}...");

                // escribir Encabezado
                byte[] headerBuffer = new byte[2 + nameBytes.Length + 8];
                
                // longitud nombre
                BinaryPrimitives.WriteUInt16BigEndian(headerBuffer.AsSpan(0, 2), (ushort)nameBytes.Length);
                // nombre
                nameBytes.CopyTo(headerBuffer.AsSpan(2, nameBytes.Length));
                // tamaño del archivo
                BinaryPrimitives.WriteInt64BigEndian(headerBuffer.AsSpan(2 + nameBytes.Length, 8), fileSize);

                await stream.WriteAsync(headerBuffer, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                // escribir Payload del archivo
                await using FileStream fileStream = File.OpenRead(filePath);
                byte[] buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);

                Console.WriteLine("Archivo enviado con éxito");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error en el cliente: {ex.Message}");
            }
        }

        public void StopServer()
        {
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch {}

                listener = null;
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed) return;

            if (disposing)
            {
                StopServer();
            }

            isDisposed = true;
        }
    }
}
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WifiDirectService.Services;

namespace WifiDirectService.Sockets
{
    public class Socket : IDisposable
    {
        static Service wifiService = null!;

        const int PORT = 8881;

        private static void SendJsonMessage(object data)
        {
            Console.WriteLine(JsonSerializer.Serialize(data));
        }

        // Iniciar servidor
        public async Task StartServerSocket(string ipAddress)
        {
            try
            {
                wifiService = new Service();
                TcpListener listener = new TcpListener(IPAddress.Parse(ipAddress), PORT);
                SendJsonMessage(new { event_type = "SERVER", message = $"Servidor escuchando en el puerto: {PORT}" });
                listener.Start();

                String dir = @"C:/archivosRecibidos/";

                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    SendJsonMessage(new { event_type = "SERVER", message = "Cliente conectado!" });

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            using (NetworkStream stream = client.GetStream())
                            using (BinaryReader reader = new BinaryReader(stream))
                            {
                                ushort nameLength = ReadUInt16BigEndian(reader);

                                byte[] nameBytes = reader.ReadBytes(nameLength);
                                string fileName = Encoding.UTF8.GetString(nameBytes);

                                long fileSize = ReadInt64BigEndian(reader);

                                SendJsonMessage(new { event_type = "SERVER", message = $"Recibiendo '{fileName}'..." });

                                // crear carpeta si no existe
                                Directory.CreateDirectory(dir);
                                 
                                // obtener nombre del archivo
                                string name = Path.GetFileName(fileName);
                                
                                string pathDir = Path.Join(dir, name);

                                byte[] buffer = new byte[8192];
                                long totalRead = 0;

                                using (FileStream fs = 
                                        new FileStream(
                                            pathDir, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true 
                                        ))
                                {
                                    while (totalRead < fileSize)
                                    {
                                        int toRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                                        int bytesRead = await stream.ReadAsync(buffer, 0, toRead);
                                        if (bytesRead <= 0) break;

                                        await fs.WriteAsync(buffer, 0, bytesRead);
                                        totalRead += bytesRead;
                                    }


                                    SendJsonMessage(new
                                    {
                                        event_type = "SERVER",
                                        message = "Archivo recibido"
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SendJsonMessage(new { event_type = "SERVER", message = $"Error al procesar los datos: {ex.Message}" });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                SendJsonMessage(new { event_type = "ERROR", message = ex.Message });
            }
        }

        // Iniciar cliente y transferencia de archivo
        public async Task StartClientSocket(string ipAddress, string? filePath)
        {
            SendJsonMessage(new { event_type = "file", message = filePath });
            try
            {
                if (!File.Exists(filePath))
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

                SendJsonMessage(new { event_type = "CLIENT", message = $"Conectado al servidor. Enviando '{fileName}'..." });

                WriteUInt16BigEndian(writer, nameLength);
                writer.Write(nameBytes);
                WriteInt64BigEndian(writer, fileSize);

                using FileStream fileStream = File.OpenRead(filePath);
                byte[] buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, bytesRead);
                }

                SendJsonMessage(new
                {
                    event_type = "CLIENT",
                    message = "Archivo enviado",
                    file_name = fileName,
                    size_bytes = fileSize
                });
            }
            catch (Exception ex)
            {
                SendJsonMessage(new { event_type = "ERROR", message = ex.Message });
            }
        }

        // métodos auxiliares
        static ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(2);
            if (BitConverter.IsLittleEndian) Array.Reverse(data);
            return BitConverter.ToUInt16(data, 0);
        }

        static long ReadInt64BigEndian(BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(8);
            if (BitConverter.IsLittleEndian) Array.Reverse(data);
            return BitConverter.ToInt64(data, 0);
        }

        static void WriteUInt16BigEndian(BinaryWriter writer, ushort value)
        {
            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(data);
            writer.Write(data);
        }

        static void WriteInt64BigEndian(BinaryWriter writer, long value)
        {
            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(data);
            writer.Write(data);
        }

        public void Dispose() { }
    }
}
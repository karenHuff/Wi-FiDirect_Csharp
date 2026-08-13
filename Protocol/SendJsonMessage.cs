using System;
using System.Text;
using System.Text.Json;

namespace WifiDirectService.Protocol
{ 
    public class SendJsonMessage
    {
        public void SendMessage(object data)
        {
            Console.WriteLine(JsonSerializer.Serialize(data));
        }
    }
}
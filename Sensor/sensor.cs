using System;
using System.IO;
using System.Net.Sockets;

class Sensor
{
    static void Main()
    {
        string gatewayIP = "127.0.0.1";
        int gatewayPort = 5001;

        Console.Write("Enter sensor ID: ");
        string sensorID = Console.ReadLine() ?? "unknown";

        Console.WriteLine("Connecting to gateway...");

        using TcpClient client = new TcpClient(gatewayIP, gatewayPort);
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new StreamReader(stream);
        using StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

        Console.WriteLine("Connected to gateway.");

        // 1) Startup handshake
        SendMessage(writer, $"HELLO | {sensorID}");
        string helloResponse = ReadGatewayResponse(reader);
        if (helloResponse != "OK")
        {
            Console.WriteLine("Gateway rejected HELLO. Closing sensor.");
            return;
        }

        // 2) Register supported types at startup
        RegisterTypes(writer, reader);

        bool running = true;

        while (running)
        {
            Console.WriteLine();
            Console.WriteLine("1 - Send Temperature");
            Console.WriteLine("2 - Send Humidity");
            Console.WriteLine("3 - Register Types");
            Console.WriteLine("4 - Send Heartbeat");
            Console.WriteLine("5 - Request Video Stream");
            Console.WriteLine("6 - Disconnect");
            Console.Write("Choice: ");

            string choice = Console.ReadLine() ?? "";

            switch (choice)
            {
                case "1":
                    Console.Write("Temperature value: ");
                    string tempValue = Console.ReadLine() ?? "0";
                    SendData(writer, reader, sensorID, "TEMP", tempValue);
                    break;

                case "2":
                    Console.Write("Humidity value: ");
                    string humValue = Console.ReadLine() ?? "0";
                    SendData(writer, reader, sensorID, "HUM", humValue);
                    break;

                case "3":
                    RegisterTypes(writer, reader);
                    break;

                case "4":
                    SendMessage(writer, $"HEARTBEAT | {sensorID}");
                    ReadGatewayResponse(reader);
                    break;

                case "5":
                    SendMessage(writer, $"VIDEO_REQUEST | {sensorID}");
                    ReadGatewayResponse(reader);
                    break;

                case "6":
                    SendMessage(writer, $"DISCONNECT | {sensorID}");
                    ReadGatewayResponse(reader);
                    running = false;
                    break;

                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }

        Console.WriteLine("Sensor disconnected.");
    }

    static void RegisterTypes(StreamWriter writer, StreamReader reader)
    {
        SendMessage(writer, "TYPES | TEMP,HUM,RUIDO");
        ReadGatewayResponse(reader);
    }

    static void SendData(StreamWriter writer, StreamReader reader, string sensorID, string type, string value)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        SendMessage(writer, $"DATA | {sensorID} | {type} | {value} | {timestamp}");
        ReadGatewayResponse(reader);
    }

    static void SendMessage(StreamWriter writer, string message)
    {
        Console.WriteLine($"SENSOR -> GATEWAY: {message}");
        writer.WriteLine(message);
    }

    static string ReadGatewayResponse(StreamReader reader)
    {
        string response = reader.ReadLine() ?? "";
        Console.WriteLine($"GATEWAY -> SENSOR: {response}");
        return response;
    }
}
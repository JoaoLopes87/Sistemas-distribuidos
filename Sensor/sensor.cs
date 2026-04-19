using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

class Sensor
{
    static object sensorLock = new object();

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

        // HELLO
        SendMessage(writer, $"HELLO | {sensorID}");
        string helloResponse = ReadGatewayResponse(reader);
        if (!helloResponse.StartsWith("OK"))
        {
            Console.WriteLine("Gateway rejected HELLO. Closing sensor.");
            return;
        }

        // Extrai os tipos permitidos da resposta "OK | TEMP,HUM,RUIDO"
        string[] tiposPermitidos = Array.Empty<string>();
        string[] helloParts = helloResponse.Split('|', StringSplitOptions.TrimEntries);
        if (helloParts.Length > 1)
            tiposPermitidos = helloParts[1].Split(',', StringSplitOptions.TrimEntries);

        RegisterTypes(writer, reader, sensorID, tiposPermitidos);

        // Heartbeat
        Thread heartbeatT = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(30000);
                lock (sensorLock)
                {
                    SendMessage(writer, $"HEARTBEAT | {sensorID}");
                    ReadGatewayResponse(reader);
                }
            }
        });
        heartbeatT.IsBackground = true;
        heartbeatT.Start();

        bool running = true;

        while (running)
        {
            Console.WriteLine();
            Console.WriteLine("1 - Send Temperature");
            Console.WriteLine("2 - Send Humidity");
            Console.WriteLine("3 - Send Noise");
            Console.WriteLine("4 - Send PM2.5");
            Console.WriteLine("5 - Send PM10");
            Console.WriteLine("6 - Send Luminosity");
            Console.WriteLine("7 - Register Types");
            Console.WriteLine("8 - Request Video Stream");
            Console.WriteLine("9 - Disconnect");
            Console.Write("Choice: ");

            string choice = Console.ReadLine() ?? "";

            switch (choice)
            {
                case "1":
                    Console.Write("Temperature value: ");
                    string tempValue = Console.ReadLine() ?? "0";
                    lock (sensorLock)
                    {
                        SendData(writer, reader, sensorID, "TEMP", tempValue);
                    }
                    break;

                case "2":
                    Console.Write("Humidity value: ");
                    string humValue = Console.ReadLine() ?? "0";
                    lock (sensorLock)
                    {
                        SendData(writer, reader, sensorID, "HUM", humValue);
                    }
                    break;

                case "3":
                    Console.Write("Noise value (dB): ");
                    string ruidoValue = Console.ReadLine() ?? "0";
                    lock (sensorLock)
                    {
                        SendData(writer, reader, sensorID, "RUIDO", ruidoValue);
                    }
                    break;

                case "4":
                    Console.Write("PM2.5 value: ");
                    string pmValue = Console.ReadLine() ?? "0";
                    lock (sensorLock)
                    {
                        SendData(writer, reader, sensorID, "PM2.5", pmValue);
                    }
                    break;

                case "5":
                    Console.Write("PM10 value: ");
                    string pmValue2 = Console.ReadLine() ?? "0";
                    lock (sensorLock)
                    {
                        SendData(writer, reader, sensorID, "PM10", pmValue2);
                    }
                    break;

                case "6":
                    Console.Write("Luminosity value: ");
                    string lumValue = Console.ReadLine() ?? "0";
                    lock (sensorLock)
                    {
                        SendData(writer, reader, sensorID, "LUM", lumValue);
                    }
                    break;

                case "7":
                    lock (sensorLock)
                    {
                        RegisterTypes(writer, reader, sensorID, tiposPermitidos);
                    }
                    break;

                case "8":
                    lock (sensorLock)
                    {
                        SendMessage(writer, $"VIDEO_REQUEST | {sensorID}");
                        ReadGatewayResponse(reader);
                    }
                    break;

                case "9":
                    lock (sensorLock)
                    {
                        SendMessage(writer, $"DISCONNECT | {sensorID}");
                        ReadGatewayResponse(reader);
                        running = false;
                    }
                    break;

                default:
                    Console.WriteLine("Invalid option.");
                    break;
            }
        }

        Console.WriteLine("Sensor disconnected.");
    }

    static void RegisterTypes(StreamWriter writer, StreamReader reader, string sensorID, string[] tipos)
    {
        string tiposStr = string.Join(",", tipos);
        SendMessage(writer, $"TYPES | {sensorID} | {tiposStr}");
        string response = ReadGatewayResponse(reader);
        if (response != "OK")
            Console.WriteLine($"TYPES rejected: {response}");
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
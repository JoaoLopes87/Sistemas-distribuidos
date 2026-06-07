using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using RabbitMQ.Client;

class Sensor
{
    static object sensorLock = new object();
    static IConnection? rabbitConn = null;
    static IChannel? rabbitChan = null;
    static string zona = "";

    static void Main()
    {
        string gatewayIP = "127.0.0.1";
        int gatewayPort = 5002;

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

        // Parse "OK | ZONA_NORTE | TEMP,HUM,RUIDO" (novo) ou "OK | TEMP,HUM,RUIDO" (antigo)
        string[] helloParts = helloResponse.Split('|', StringSplitOptions.TrimEntries);
        string[] tiposPermitidos = Array.Empty<string>();
        if (helloParts.Length >= 3)
        {
            zona = helloParts[1];
            tiposPermitidos = helloParts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (helloParts.Length >= 2)
        {
            tiposPermitidos = helloParts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        RegisterTypes(writer, reader, sensorID, tiposPermitidos);
        ConnectRabbitMq();

        // Heartbeat via TCP
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
                    Console.Write("Temperature value (ex: 20c, 70f, 300k): ");
                    string tempValue = Console.ReadLine() ?? "0";
                    PublishData(sensorID, "TEMP", tempValue);
                    break;

                case "2":
                    Console.Write("Humidity value (ex: 65, 65%): ");
                    string humValue = Console.ReadLine() ?? "0";
                    PublishData(sensorID, "HUM", humValue);
                    break;

                case "3":
                    Console.Write("Noise value (ex: 85, 85db): ");
                    string ruidoValue = Console.ReadLine() ?? "0";
                    PublishData(sensorID, "RUIDO", ruidoValue);
                    break;

                case "4":
                    Console.Write("PM2.5 value: ");
                    string pmValue = Console.ReadLine() ?? "0";
                    PublishData(sensorID, "PM2.5", pmValue);
                    break;

                case "5":
                    Console.Write("PM10 value: ");
                    string pmValue2 = Console.ReadLine() ?? "0";
                    PublishData(sensorID, "PM10", pmValue2);
                    break;

                case "6":
                    Console.Write("Luminosity value (ex: 500, 500lux): ");
                    string lumValue = Console.ReadLine() ?? "0";
                    PublishData(sensorID, "LUM", lumValue);
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

        rabbitChan?.CloseAsync().GetAwaiter().GetResult();
        rabbitConn?.CloseAsync().GetAwaiter().GetResult();
        Console.WriteLine("Sensor disconnected.");
    }

    static void ConnectRabbitMq()
    {
        try
        {
            var factory = new ConnectionFactory() { HostName = "localhost", UserName = "admin", Password = "password123" };
            rabbitConn = factory.CreateConnectionAsync().GetAwaiter().GetResult();
            rabbitChan = rabbitConn.CreateChannelAsync().GetAwaiter().GetResult();
            rabbitChan.ExchangeDeclareAsync(exchange: "sensor_data", type: ExchangeType.Topic, durable: false, autoDelete: false, arguments: null, passive: false, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
            Console.WriteLine("Connected to RabbitMQ.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("RabbitMQ unavailable: " + ex.Message);
        }
    }

    static void PublishData(string sensorID, string type, string value)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        if (rabbitChan == null)
        {
            Console.WriteLine("RabbitMQ not connected, data not sent.");
            return;
        }

        var msg = new
        {
            SensorId = sensorID,
            Zona     = zona,
            Tipo     = type,
            Valor    = value,
            Timestamp = timestamp
        };

        string json = JsonSerializer.Serialize(msg);
        string routingKey = $"sensor.{zona}.{type}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        rabbitChan.BasicPublishAsync(exchange: "sensor_data", routingKey: routingKey, body: body, cancellationToken: default).GetAwaiter().GetResult();
        Console.WriteLine($"SENSOR -> RABBITMQ [{routingKey}]: {json}");
    }

    static void RegisterTypes(StreamWriter writer, StreamReader reader, string sensorID, string[] tipos)
    {
        string tiposStr = string.Join(",", tipos);
        SendMessage(writer, $"TYPES | {sensorID} | {tiposStr}");
        string response = ReadGatewayResponse(reader);
        if (response != "OK")
            Console.WriteLine($"TYPES rejected: {response}");
    }

    static void SendMessage(StreamWriter writer, string message)
    {
        Console.WriteLine($"SENSOR -> GATEWAY (TCP): {message}");
        writer.WriteLine(message);
    }

    static string ReadGatewayResponse(StreamReader reader)
    {
        string response = reader.ReadLine() ?? "";
        Console.WriteLine($"GATEWAY -> SENSOR: {response}");
        return response;
    }
}

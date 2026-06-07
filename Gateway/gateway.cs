using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Grpc.Net.Client;
using PreProcessamentoGrpc;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

class Gateway
{
    static TcpClient serverClient = null!;
    static StreamWriter serverWriter = null!;
    static StreamReader serverReader = null!;
    static PreProcessamentoGrpc.PreProcessamentoService.PreProcessamentoServiceClient grpcClient = null!;

    static IConnection rabbitConnection = null!;
    static IChannel rabbitChannel = null!;

    static Dictionary<string, SensorInfo> sensores = new Dictionary<string, SensorInfo>();

    static object lockObject = new object();

    static void MonitorSensors()
    {
        while (true)
        {
            bool changed = false;
            lock (lockObject)
            {
                foreach (var sensor in sensores)
                {
                    var v = sensor.Value;

                    if (DateTime.TryParse(v.LastSync, out DateTime last))
                    {
                        if ((DateTime.Now - last).TotalSeconds > 75)
                        {
                            if (v.Estado != "inativo")
                            {
                                v.Estado = "inativo";
                                Console.WriteLine($"Sensor {sensor.Key} marcado como INATIVO");
                                changed = true;
                            }
                        }
                    }
                }
            }

            Thread.Sleep(5000);

            if (changed)
            {
                lock (lockObject)
                {
                    WriteSensorsCsv("sensors_.csv");
                }
            }
        }
    }

    static void Main()
    {
        var channel = GrpcChannel.ForAddress("http://localhost:5166");
        grpcClient = new PreProcessamentoGrpc.PreProcessamentoService.PreProcessamentoServiceClient(channel);

        LoadSensorsFromCsv("sensors_.csv");
        InitRabbitMq();
        ConnectToServer();

        new Thread(MonitorSensors) { IsBackground = true }.Start();

        Console.WriteLine("Gateway ready and listening for sensor events.");
        Console.WriteLine("Press ENTER to exit.");
        Console.ReadLine();

        rabbitChannel?.CloseAsync(200, "Closing channel", false, default).GetAwaiter().GetResult();
        rabbitConnection?.CloseAsync(200, "Closing connection", TimeSpan.Zero, false, default).GetAwaiter().GetResult();
        serverClient?.Close();
    }

    static void ConnectToServer()
    {
        string serverIP = "127.0.0.1";
        int serverPort = 6000;

        Console.WriteLine("Connecting to server...");

        serverClient = new TcpClient(serverIP, serverPort);
        NetworkStream serverStream = serverClient.GetStream();
        serverReader = new StreamReader(serverStream);
        serverWriter = new StreamWriter(serverStream) { AutoFlush = true };

        Console.WriteLine("Connected to server.");
    }

    static void InitRabbitMq()
    {
        var factory = new ConnectionFactory() { HostName = "localhost" };
        rabbitConnection = factory.CreateConnectionAsync(cancellationToken: default).GetAwaiter().GetResult();
        rabbitChannel = rabbitConnection.CreateChannelAsync(cancellationToken: default).GetAwaiter().GetResult();

        rabbitChannel.ExchangeDeclareAsync(exchange: "sensor_data", type: ExchangeType.Topic, durable: false, autoDelete: false, arguments: null, passive: false, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
        var queueDeclare = rabbitChannel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true, arguments: null, passive: false, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
        var queueName = queueDeclare.QueueName;

        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in sensores.Values)
        {
            zones.Add(sensor.Zona);
        }

        foreach (var zone in zones)
        {
            string zoneNorm = zone.ToLowerInvariant();
            string routingKey = $"sensor.{zoneNorm}.*";
            rabbitChannel.QueueBindAsync(queue: queueName, exchange: "sensor_data", routingKey: routingKey, arguments: null, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
            Console.WriteLine($"Gateway subscribed to topic: {routingKey}");
        }

        var consumer = new AsyncEventingBasicConsumer(rabbitChannel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            string body = Encoding.UTF8.GetString(ea.Body.ToArray());
            ProcessSensorMessage(body);
            await rabbitChannel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: default);
        };

        rabbitChannel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: default).GetAwaiter().GetResult();
    }

    static void ProcessSensorMessage(string message)
    {
        Console.WriteLine("SENSOR -> GATEWAY: " + message);

        SensorMessage? sensorMessage;
        try
        {
            sensorMessage = JsonSerializer.Deserialize<SensorMessage>(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to parse sensor message: " + ex.Message);
            return;
        }

        if (sensorMessage == null || string.IsNullOrWhiteSpace(sensorMessage.SensorId) || string.IsNullOrWhiteSpace(sensorMessage.Tipo))
        {
            Console.WriteLine("Invalid sensor payload received.");
            return;
        }

        if (!sensores.TryGetValue(sensorMessage.SensorId, out SensorInfo info))
        {
            Console.WriteLine($"Unknown sensor: {sensorMessage.SensorId}");
            return;
        }

        if (!string.Equals(info.Zona, sensorMessage.Zona, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Sensor zone mismatch: expected {info.Zona}, got {sensorMessage.Zona}");
            return;
        }

        if (!string.Equals(info.Estado, "ativo", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Sensor {sensorMessage.SensorId} is not active.");
            return;
        }

        if (Array.IndexOf(info.Tipos, sensorMessage.Tipo) < 0)
        {
            Console.WriteLine($"Unsupported type {sensorMessage.Tipo} for sensor {sensorMessage.SensorId}.");
            return;
        }

        var timestampRequest = new TimestampRequest { Timestamp = sensorMessage.Timestamp };
        var timestampResponse = grpcClient.ValidarTimestamp(timestampRequest);
        if (!timestampResponse.Valido)
        {
            Console.WriteLine($"Timestamp validation failed: {timestampResponse.Erro}");
            return;
        }

        var escalaRequest = new EscalaRequest { Type = sensorMessage.Tipo, Value = sensorMessage.Valor };
        var escalaResponse = grpcClient.ConverterEscala(escalaRequest);
        if (!escalaResponse.Valido)
        {
            Console.WriteLine($"Scale conversion failed: {escalaResponse.Erro}");
            return;
        }

        string convertedValue = escalaResponse.NewValue.ToString(CultureInfo.InvariantCulture);
        var valorRequest = new ValorRequest { Type = sensorMessage.Tipo, Value = convertedValue };
        var valorResponse = grpcClient.NormalizarValor(valorRequest);
        if (!valorResponse.Valido)
        {
            Console.WriteLine($"Value normalization failed: {valorResponse.Erro}");
            return;
        }

        string valorFinal = valorResponse.NewValue;
        string serverMessage = $"STORE | {sensorMessage.SensorId} | {info.Zona} | {sensorMessage.Tipo} | {valorFinal} | {timestampResponse.NewTimeStamp}";

        lock (lockObject)
        {
            try
            {
                serverWriter.WriteLine(serverMessage);
                string serverResponse = serverReader.ReadLine() ?? string.Empty;

                Console.WriteLine("GATEWAY -> SERVER: " + serverMessage);
                Console.WriteLine("SERVER -> GATEWAY: " + serverResponse);

                if (serverResponse != "STORED")
                {
                    Console.WriteLine("Server did not store data.");
                    return;
                }

                info.LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to send data to server: " + ex.Message);
            }
        }
    }

    static void WriteSensorsCsv(string filePath)
    {
        var lines = new List<string>();

        foreach (KeyValuePair<string, SensorInfo> sensor in sensores)
        {
            string id = sensor.Key;
            SensorInfo values = sensor.Value;
            string line = $"{id}:{values.Estado}:{values.Zona}:[{string.Join(",", values.Tipos)}]:{values.LastSync}";
            lines.Add(line);
        }

        File.WriteAllLines(filePath, lines);
    }

    static void LoadSensorsFromCsv(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] parts = line.Split(':');
            if (parts.Length < 5)
            {
                Console.WriteLine("Invalid CSV line: " + line);
                continue;
            }

            string id = parts[0].Trim();
            string estado = parts[1].Trim();
            string zona = parts[2].Trim();
            string tiposRaw = parts[3].Trim().Trim('[', ']');
            string[] tipos = tiposRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string lastSync = parts[4].Trim();

            sensores[id] = new SensorInfo(estado, zona, tipos, lastSync);
        }

        Console.WriteLine("Loaded sensors from CSV: " + sensores.Count);
    }
}

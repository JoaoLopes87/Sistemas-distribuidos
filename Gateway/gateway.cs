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

        new Thread(MonitorSensors) { IsBackground = true }.Start();

        ConnectToServer();
        InitRabbitMq();

        int gatewayPort = 5002;
        TcpListener sensorListener = new TcpListener(IPAddress.Any, gatewayPort);
        sensorListener.Start();

        Console.WriteLine("Gateway listening for sensors on TCP and RabbitMQ...");

        while (true)
        {
            TcpClient sensorClient = sensorListener.AcceptTcpClient();
            Console.WriteLine("Sensor connected via TCP.");

            Thread sensorThread = new Thread(HandleSensor);
            sensorThread.IsBackground = true;
            sensorThread.Start(sensorClient);
        }
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
        try
        {
            var factory = new ConnectionFactory() { HostName = "localhost", UserName = "admin", Password = "password123" };
            rabbitConnection = factory.CreateConnectionAsync(cancellationToken: default).GetAwaiter().GetResult();
            rabbitChannel = rabbitConnection.CreateChannelAsync(cancellationToken: default).GetAwaiter().GetResult();

            rabbitChannel.ExchangeDeclareAsync(exchange: "sensor_data", type: ExchangeType.Topic, durable: false, autoDelete: false, arguments: null, passive: false, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
            var queueDeclare = rabbitChannel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true, arguments: null, passive: false, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
            var queueName = queueDeclare.QueueName;

            var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sensor in sensores.Values)
                zones.Add(sensor.Zona);

            foreach (var zone in zones)
            {
                string routingKey = $"sensor.{zone}.*";
                rabbitChannel.QueueBindAsync(queue: queueName, exchange: "sensor_data", routingKey: routingKey, arguments: null, noWait: false, cancellationToken: default).GetAwaiter().GetResult();
                Console.WriteLine($"Gateway subscribed to RabbitMQ topic: {routingKey}");
            }

            var consumer = new AsyncEventingBasicConsumer(rabbitChannel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                string body = Encoding.UTF8.GetString(ea.Body.ToArray());
                ProcessRabbitMqMessage(body);
                await rabbitChannel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: default);
            };

            rabbitChannel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: default).GetAwaiter().GetResult();
            Console.WriteLine("RabbitMQ consumer ready.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("RabbitMQ unavailable, skipping: " + ex.Message);
        }
    }

    static void ProcessRabbitMqMessage(string message)
    {
        Console.WriteLine("RABBITMQ -> GATEWAY: " + message);

        SensorMessage? sensorMessage;
        try
        {
            sensorMessage = JsonSerializer.Deserialize<SensorMessage>(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to parse RabbitMQ message: " + ex.Message);
            return;
        }

        if (sensorMessage == null || string.IsNullOrWhiteSpace(sensorMessage.SensorId) || string.IsNullOrWhiteSpace(sensorMessage.Tipo))
        {
            Console.WriteLine("Invalid RabbitMQ payload.");
            return;
        }

        if (!sensores.TryGetValue(sensorMessage.SensorId, out SensorInfo? info))
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

        ForwardToServer(sensorMessage.SensorId, info, sensorMessage.Tipo, sensorMessage.Valor, sensorMessage.Timestamp);
    }

    static void ForwardToServer(string sensorId, SensorInfo info, string type, string value, string timestamp)
    {
        try
        {
            var timestampResponse = grpcClient.ValidarTimestamp(new TimestampRequest { Timestamp = timestamp });

            var escalaResponse = grpcClient.ConverterEscala(new EscalaRequest { Type = type, Value = value });
            if (!escalaResponse.Valido)
            {
                Console.WriteLine($"Scale conversion failed: {escalaResponse.Erro}");
                return;
            }

            string convertedValue = escalaResponse.NewValue.ToString(CultureInfo.InvariantCulture);
            var valorResponse = grpcClient.NormalizarValor(new ValorRequest { Type = type, Value = convertedValue });
            if (!valorResponse.Valido)
            {
                Console.WriteLine($"Value normalization failed: {valorResponse.Erro}");
                return;
            }

            string valorFinal = escalaResponse.NewValue.ToString(CultureInfo.InvariantCulture);
            string serverMessage = $"STORE | {sensorId} | {info.Zona} | {type} | {valorFinal} | {timestampResponse.NewTimeStamp}";

            lock (lockObject)
            {
                try
                {
                    serverWriter.WriteLine(serverMessage);
                    string serverResponse = serverReader.ReadLine() ?? string.Empty;

                    Console.WriteLine("GATEWAY -> SERVER: " + serverMessage);
                    Console.WriteLine("SERVER -> GATEWAY: " + serverResponse);

                    if (serverResponse == "STORED")
                        info.LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    else
                        Console.WriteLine("Server did not store data.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to send data to server: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("gRPC error: " + ex.Message);
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

    static void HandleSensor(object? obj)
    {
        TcpClient sensorClient = (TcpClient)obj!;
        NetworkStream stream = sensorClient.GetStream();
        StreamReader reader = new StreamReader(stream);
        StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

        string sensorID = "UNKNOWN";
        bool valido = false;
        bool tiposValidados = false;

        try
        {
            while (true)
            {
                string? message = reader.ReadLine();

                if (message == null)
                    break;

                Console.WriteLine("SENSOR -> GATEWAY: " + message);
                string[] parts = message.Split('|', StringSplitOptions.TrimEntries);

                if (parts.Length == 0)
                {
                    SendSensorResponse(writer, "ERROR | Empty message");
                    continue;
                }

                string command = parts[0].Trim();

                if (command == "HELLO")
                {
                    if (parts.Length < 2)
                    {
                        SendSensorResponse(writer, "ERROR | Missing sensor_id");
                        continue;
                    }

                    string requestedSensorId = parts[1].Trim();

                    if (!sensores.ContainsKey(requestedSensorId))
                    {
                        SendSensorResponse(writer, "ERROR | Unknown sensor");
                        continue;
                    }

                    SensorInfo info = sensores[requestedSensorId];
                    if (info.Estado != "ativo")
                    {
                        SendSensorResponse(writer, "ERROR | Sensor not active");
                        continue;
                    }

                    sensorID = requestedSensorId;
                    valido = true;
                    tiposValidados = false;

                    string tiposPermitidos = string.Join(",", info.Tipos);
                    SendSensorResponse(writer, $"OK | {info.Zona} | {tiposPermitidos}");
                    continue;
                }

                if (!valido || !sensores.ContainsKey(sensorID))
                {
                    SendSensorResponse(writer, "ERROR | Sensor not validated");
                    continue;
                }

                if (command == "TYPES")
                {
                    if (parts.Length < 3)
                    {
                        SendSensorResponse(writer, "ERROR | Missing types");
                        continue;
                    }

                    string typesSensorId = parts[1].Trim();
                    if (typesSensorId != sensorID)
                    {
                        SendSensorResponse(writer, "ERROR | Sensor ID mismatch");
                        continue;
                    }

                    string[] requestedTypes = parts[2]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    SensorInfo info = sensores[sensorID];
                    bool allTypesValid = true;

                    foreach (string t in requestedTypes)
                    {
                        if (Array.IndexOf(info.Tipos, t) < 0)
                        {
                            allTypesValid = false;
                            break;
                        }
                    }

                    if (!allTypesValid)
                    {
                        SendSensorResponse(writer, "ERROR | Unsupported type");
                        continue;
                    }

                    tiposValidados = true;
                    SendSensorResponse(writer, "OK");
                }

                else if (command == "DATA")
                {
                    if (parts.Length < 5)
                    {
                        SendSensorResponse(writer, "ERROR | DATA format invalid");
                        continue;
                    }

                    if (!tiposValidados)
                    {
                        SendSensorResponse(writer, "ERROR | TYPES not registered");
                        continue;
                    }

                    string dataSensorId = parts[1].Trim();
                    string type = parts[2].Trim();
                    string value = parts[3].Trim();
                    string timestamp = parts[4].Trim();

                    if (dataSensorId != sensorID)
                    {
                        SendSensorResponse(writer, "ERROR | Sensor ID mismatch");
                        continue;
                    }

                    SensorInfo info = sensores[sensorID];
                    if (Array.IndexOf(info.Tipos, type) < 0)
                    {
                        SendSensorResponse(writer, "ERROR | Unsupported type");
                        continue;
                    }

                    TimestampResponse timestampResp;
                    EscalaResponse escalaResp;
                    ValorResponse valorResp;

                    try
                    {
                        timestampResp = grpcClient.ValidarTimestamp(new TimestampRequest { Timestamp = timestamp });

                        escalaResp = grpcClient.ConverterEscala(new EscalaRequest { Type = type, Value = value });
                        if (!escalaResp.Valido)
                        {
                            SendSensorResponse(writer, $"ERROR | {escalaResp.Erro}");
                            continue;
                        }

                        valorResp = grpcClient.NormalizarValor(new ValorRequest { Type = type, Value = escalaResp.NewValue.ToString(CultureInfo.InvariantCulture) });
                        if (!valorResp.Valido)
                        {
                            SendSensorResponse(writer, $"ERROR | {valorResp.Erro}");
                            continue;
                        }
                    }
                    catch (Exception grpcEx)
                    {
                        Console.WriteLine("gRPC error: " + grpcEx.Message);
                        SendSensorResponse(writer, "ERROR | Pre-processing service unavailable");
                        continue;
                    }

                    string valorFinal = escalaResp.NewValue.ToString(CultureInfo.InvariantCulture);
                    string serverMessage = $"STORE | {sensorID} | {info.Zona} | {type} | {valorFinal} | {timestampResp.NewTimeStamp}";

                    bool stored = false;
                    lock (lockObject)
                    {
                        try
                        {
                            serverWriter.WriteLine(serverMessage);
                            string serverResponse = serverReader.ReadLine() ?? "";

                            Console.WriteLine("GATEWAY -> SERVER: " + serverMessage);
                            Console.WriteLine("SERVER -> GATEWAY: " + serverResponse);

                            if (serverResponse == "STORED")
                            {
                                stored = true;
                                info.LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                            }
                        }
                        catch (Exception serverEx)
                        {
                            Console.WriteLine("Server communication error: " + serverEx.Message);
                        }
                    }

                    if (stored)
                        SendSensorResponse(writer, "ACK");
                    else
                        SendSensorResponse(writer, "ERROR | Server did not store data");
                }

                else if (command == "VIDEO_REQUEST")
                {
                    if (parts.Length < 2)
                    {
                        SendSensorResponse(writer, "ERROR | Missing sensor_id");
                        continue;
                    }

                    string requestSensorId = parts[1].Trim();
                    if (requestSensorId != sensorID)
                    {
                        SendSensorResponse(writer, "ERROR | Sensor ID mismatch");
                        continue;
                    }

                    SendSensorResponse(writer, "ACK");
                }

                else if (command == "HEARTBEAT")
                {
                    if (parts.Length < 2)
                    {
                        SendSensorResponse(writer, "ERROR | Missing sensor_id");
                        continue;
                    }

                    string heartbeatSensorId = parts[1].Trim();
                    if (heartbeatSensorId != sensorID)
                    {
                        SendSensorResponse(writer, "ERROR | Sensor ID mismatch");
                        continue;
                    }

                    lock (lockObject)
                    {
                        sensores[sensorID].LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                        sensores[sensorID].Estado = "ativo";
                    }

                    SendSensorResponse(writer, "ALIVE");
                }

                else if (command == "DISCONNECT")
                {
                    if (parts.Length < 2)
                    {
                        SendSensorResponse(writer, "ERROR | Missing sensor_id");
                        continue;
                    }

                    string disconnectSensorId = parts[1].Trim();
                    if (disconnectSensorId != sensorID)
                    {
                        SendSensorResponse(writer, "ERROR | Sensor ID mismatch");
                        continue;
                    }

                    SendSensorResponse(writer, "BYE");
                    Console.WriteLine("Sensor disconnected: " + sensorID);
                    break;
                }

                else
                {
                    SendSensorResponse(writer, "ERROR | Unknown command");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Sensor connection lost: " + ex.Message);
        }

        reader.Close();
        writer.Close();
        sensorClient.Close();
    }

    static void SendSensorResponse(StreamWriter writer, string response)
    {
        writer.WriteLine(response);
        Console.WriteLine("GATEWAY -> SENSOR: " + response);
    }
}

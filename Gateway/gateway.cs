using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Grpc.Net.Client;
using PreProcessamentoGrpc;

class Gateway
{
    static TcpClient serverClient;
    static StreamWriter serverWriter;
    static StreamReader serverReader;
    static PreProcessamentoGrpc.PreProcessamentoService.PreProcessamentoServiceClient grpcClient;

    static Dictionary<string, SensorInfo> sensores = new Dictionary<string, SensorInfo>();

    static object lockObject = new object();

    static Mutex mtx = new Mutex();

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

        string serverIP = "127.0.0.1";
        int serverPort = 6000;
        int gatewayPort = 5002;

        Console.WriteLine("Connecting to server...");

        serverClient = new TcpClient(serverIP, serverPort);
        NetworkStream serverStream = serverClient.GetStream();
        serverReader = new StreamReader(serverStream);
        serverWriter = new StreamWriter(serverStream) { AutoFlush = true };

        Console.WriteLine("Connected to server.");

        TcpListener sensorListener = new TcpListener(IPAddress.Any, gatewayPort);

        sensorListener.Start();

        Console.WriteLine("Gateway listening for sensors...");

        while (true)
        {
            TcpClient sensorClient = sensorListener.AcceptTcpClient();

            Console.WriteLine("Sensor connected.");

            Thread sensorThread = new Thread(HandleSensor);
            sensorThread.IsBackground = true;

            sensorThread.Start(sensorClient);
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

    static void HandleSensor(object obj)
    {
        TcpClient sensorClient = (TcpClient)obj;
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
                string message = reader.ReadLine();

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

                    // Envia os tipos permitidos na resposta
                    string tiposPermitidos = string.Join(",", info.Tipos);
                    SendSensorResponse(writer, $"OK | {tiposPermitidos}");
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

                    var request = new TimestampRequest
                    {
                        Timestamp = timestamp
                    };

                    var response = grpcClient.ValidarTimestamp(request);

              //      if (!DateTime.TryParseExact(
              //              timestamp,
              //              "yyyy-MM-ddTHH:mm:ss",
              //              CultureInfo.InvariantCulture,
              //              DateTimeStyles.None,
              //              out _))
              //      {
              //          SendSensorResponse(writer, "ERROR | Invalid timestamp");
              //          continue;
              //      }

                    var requestEscala = new EscalaRequest
                    {
                        Type = type,
                        Value = value
                    }; 

                    var responseEscala = grpcClient.ConverterEscala(requestEscala);

                    if (!responseEscala.Valido)
                    {
                        SendSensorResponse(writer, $"ERROR | {responseEscala.Erro}");
                        continue;
                    }

                    string valorFinal = responseEscala.NewValue.ToString();

                    var requestValor = new ValorRequest
                    {
                        Type = type,
                        Value = valorFinal
                    };

                    var responseValor = grpcClient.NormalizarValor(requestValor);

                    if (!responseValor.Valido)
                    {
                        SendSensorResponse(writer, $"ERROR | {responseValor.Erro}");
                        continue;
                    }

                   // if (!PreProcessar(type, valorFinal, out string erroPreProcessamento))
                    //{
                    //    SendSensorResponse(writer, $"ERROR | {erroPreProcessamento}");
                    //    continue;
                   // }

                    string serverMessage = $"STORE | {sensorID} | {info.Zona} | {type} | {valorFinal} | {response.NewTimeStamp}";

                    lock (lockObject)
                    {
                        serverWriter.WriteLine(serverMessage);
                        string serverResponse = serverReader.ReadLine() ?? "";

                        Console.WriteLine("GATEWAY -> SERVER: " + serverMessage);
                        Console.WriteLine("SERVER -> GATEWAY: " + serverResponse);

                        if (serverResponse != "STORED")
                        {
                            SendSensorResponse(writer, "ERROR | Server did not store data");
                            continue;
                        }
                    }

                    lock (lockObject)
                    {
                        info.LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    }


                    SendSensorResponse(writer, "ACK");
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

    static bool PreProcessar(string type, string value, out string erro)
    {
        erro = "";

        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
        {
            erro = "Value is not numeric";
            return false;
        }

        (double min, double max) = type switch
        {
            "TEMP"  => (-50.0, 100.0),
            "HUM"   => (0.0, 100.0),
            "RUIDO" => (0.0, 200.0),
            "PM2.5" => (0.0, 1000.0),
            "PM10"  => (0.0, 1000.0),
            "AR"    => (0.0, 500.0),
            "LUM"   => (0.0, 100000.0),
            _       => (double.MinValue, double.MaxValue)
        };

        if (val < min || val > max)
        {
            erro = $"Value {val} out of range for type {type}";
            return false;
        }

        return true;
    }
}

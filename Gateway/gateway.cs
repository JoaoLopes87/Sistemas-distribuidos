using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

class Gateway
{
    static TcpClient serverClient;
    static StreamWriter serverWriter;
    static StreamReader serverReader;

    static Dictionary<string, SensorInfo> sensores = new Dictionary<string, SensorInfo>();

    static object lockObject = new object();

    static void Main()
    {
        LoadSensorsFromCsv("sensors_.csv");

        string serverIP = "127.0.0.1";
        int serverPort = 6000;
        int gatewayPort = 5001;

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

    static void LoadSensorsFromCsv(string filePath)
    {
        string[] lines = File.ReadAllLines(filePath);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

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
                {
                    break;
                }

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
                    SendSensorResponse(writer, "OK");
                    continue;
                }

                if (!valido || !sensores.ContainsKey(sensorID))
                {
                    SendSensorResponse(writer, "ERROR | Sensor not validated");
                    continue;
                }

                if (command == "TYPES")
                {
                    if (parts.Length < 2)
                    {
                        SendSensorResponse(writer, "ERROR | Missing types");
                        continue;
                    }

                    string[] requestedTypes = parts[1]
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

                    if (!DateTime.TryParseExact(
                            timestamp,
                            "yyyy-MM-ddTHH:mm:ss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out _))
                    {
                        SendSensorResponse(writer, "ERROR | Invalid timestamp");
                        continue;
                    }

                    string serverMessage = $"STORE | {sensorID} | {info.Zona} | {type} | {value} | {timestamp}";

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
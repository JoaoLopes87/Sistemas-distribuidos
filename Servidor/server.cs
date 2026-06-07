using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;

class Server
{
    static readonly object dbLock = new object();
    static readonly string connectionString = "Data Source=sensors.db;";

    static AnalisePrevGrpc.AnalisePrevService.AnalisePrevServiceClient analiseClient;
    static readonly object analiselock = new object();
    static Dictionary<string, Dictionary<string, string>> ultimosValoresPorSensor = new();

    static void Main()
    {
        try
        {
            var channel = GrpcChannel.ForAddress("http://localhost:5166");
            analiseClient = new AnalisePrevGrpc.AnalisePrevService.AnalisePrevServiceClient(channel);

            InitDatabase();

            int port = 6000;
            TcpListener server = new TcpListener(IPAddress.Any, port);
            server.Start();
            Console.WriteLine("Server started...");
            Console.WriteLine("Waiting for connections...");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Console.WriteLine("New client connected.");
                Thread clientThread = new Thread(HandleClient);
                clientThread.IsBackground = true;
                clientThread.Start(client);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
        }
    }

    static void InitDatabase()
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            string createMeasurements = @"
                CREATE TABLE IF NOT EXISTS measurements (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    sensor_id TEXT NOT NULL,
                    zone      TEXT NOT NULL,
                    type      TEXT NOT NULL,
                    value     TEXT NOT NULL,
                    timestamp TEXT NOT NULL
                );";

            string createAnalises = @"
                CREATE TABLE IF NOT EXISTS analises (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    sensor_id     TEXT NOT NULL,
                    zone          TEXT NOT NULL,
                    analysis_type TEXT NOT NULL,
                    resultado     TEXT NOT NULL,
                    timestamp     TEXT NOT NULL
                );";

            using var cmd = new SqliteCommand(createMeasurements, connection);
            cmd.ExecuteNonQuery();

            using var cmd2 = new SqliteCommand(createAnalises, connection);
            cmd2.ExecuteNonQuery();

            Console.WriteLine("Database initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Database initialization error: {ex.Message}");
            throw;
        }
    }

    static void HandleClient(object obj)
    {
        TcpClient client = (TcpClient)obj;
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 30000; 
        stream.WriteTimeout = 30000;

        StreamReader reader = new StreamReader(stream);
        StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

        try
        {
            while (true)
            {
                string message = reader.ReadLine();
                if (message == null)
                    break;

                Console.WriteLine("GATEWAY -> SERVER: " + message);

                string[] parts = message.Split('|', StringSplitOptions.TrimEntries);

                if (parts.Length == 0)
                {
                    writer.WriteLine("ERROR | Empty message");
                    continue;
                }

                string command = parts[0].Trim();

                if (command == "STORE")
                {
                    if (parts.Length < 6)
                    {
                        writer.WriteLine("ERROR | STORE format invalid");
                        Console.WriteLine("Invalid STORE message (missing fields).");
                        continue;
                    }

                    string sensorId = parts[1].Trim();
                    string zone = parts[2].Trim();
                    string type = parts[3].Trim();
                    string value = parts[4].Trim();
                    string timestamp = parts[5].Trim();

                    if (string.IsNullOrWhiteSpace(sensorId) || 
                        string.IsNullOrWhiteSpace(zone) || 
                        string.IsNullOrWhiteSpace(type) || 
                        string.IsNullOrWhiteSpace(value) || 
                        string.IsNullOrWhiteSpace(timestamp))
                    {
                        writer.WriteLine("ERROR | STORE fields cannot be empty");
                        continue;
                    }

                    bool stored = StoreMeasurement(sensorId, zone, type, value, timestamp);

                    if (stored)
                    {
                        writer.WriteLine("STORED");
                        Console.WriteLine("SERVER -> GATEWAY: STORED");

                        lock (analiselock)
                        {
                            if (!ultimosValoresPorSensor.ContainsKey(sensorId))
                                ultimosValoresPorSensor[sensorId] = new Dictionary<string, string>();
                            ultimosValoresPorSensor[sensorId][type] = value;
                        }

                        var sensor = ultimosValoresPorSensor[sensorId];
                        string now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                        // Analisar — sempre
                        var (min, max, media) = GetZoneStats(zone, type);
                        var analisarResponse = analiseClient.Analisar(new AnalisePrevGrpc.AnalisarRequest
                        {
                            Type  = type,
                            Value = value,
                            Zona  = zone,
                            Min   = min,
                            Max   = max,
                            Media = media
                        });
                        StoreAnalise(sensorId, zone, "ANALISE", analisarResponse.Mensagem, now);
                        Console.WriteLine($"ANÁLISE: {analisarResponse.Mensagem}");

                        // DetetarPol — requer PM2.5 ou PM10 acima do limiar
                        double valPm25 = ParseOrZero(sensor.GetValueOrDefault("PM2.5", ""));
                        double valPm10 = ParseOrZero(sensor.GetValueOrDefault("PM10",  ""));
                        double valAr   = ParseOrZero(sensor.GetValueOrDefault("AR",    ""));

                        if ((sensor.ContainsKey("PM2.5") || sensor.ContainsKey("PM10")) &&
                            (valPm25 > 35 || valPm10 > 50 || valAr > 100))
                        {
                            var polResponse = analiseClient.DetetarPol(new AnalisePrevGrpc.PolRequest
                            {
                                Zona = zone,
                                Pm25 = sensor.GetValueOrDefault("PM2.5", ""),
                                Pm10 = sensor.GetValueOrDefault("PM10",  ""),
                                Ar   = sensor.GetValueOrDefault("AR",    "")
                            });
                            StoreAnalise(sensorId, zone, "POLUICAO", polResponse.Mensagem, now);
                            Console.WriteLine($"POLUIÇÃO: {polResponse.Mensagem}");
                        }

                        // PrevRisco — requer (TEMP e HUM) ou (PM2.5 e AR) acima do limiar
                        double valTemp = ParseOrZero(sensor.GetValueOrDefault("TEMP", ""));
                        double valHum  = ParseOrZero(sensor.GetValueOrDefault("HUM",  ""));

                        bool temRiscoTermico      = sensor.ContainsKey("TEMP") && sensor.ContainsKey("HUM") &&
                                                    (valTemp >= 30 || valHum >= 60);
                        bool temRiscoRespiratorio = sensor.ContainsKey("PM2.5") && sensor.ContainsKey("AR") &&
                                                    (valPm25 > 35 || valAr > 100);

                        if (temRiscoTermico || temRiscoRespiratorio)
                        {
                            var riscoResponse = analiseClient.PrevRisco(new AnalisePrevGrpc.RiscoRequest
                            {
                                Zona = zone,
                                Temp = sensor.GetValueOrDefault("TEMP",  ""),
                                Hum  = sensor.GetValueOrDefault("HUM",   ""),
                                Pm25 = sensor.GetValueOrDefault("PM2.5", ""),
                                Ar   = sensor.GetValueOrDefault("AR",    "")
                            });
                            StoreAnalise(sensorId, zone, "RISCO", riscoResponse.Mensagem, now);
                            Console.WriteLine($"RISCO: {riscoResponse.Mensagem}");
                        }
                    }
                    else
                    {
                        writer.WriteLine("ERROR | Database error");
                        Console.WriteLine("SERVER -> GATEWAY: ERROR | Database error");
                    }
                }
                else
                {
                    writer.WriteLine("ERROR | Unknown command");
                    Console.WriteLine("Unknown command received.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Client disconnected: " + ex.Message);
        }
        finally
        {
            reader.Close();
            writer.Close();
            client.Close();
        }
    }

    static void StoreAnalise(string sensorId, string zone, string analysisType, string resultado, string timestamp)
    {
        try
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                string insert = @"
                    INSERT INTO analises (sensor_id, zone, analysis_type, resultado, timestamp)
                    VALUES (@sensorId, @zone, @analysisType, @resultado, @timestamp);";

                using var cmd = new SqliteCommand(insert, connection);
                cmd.Parameters.AddWithValue("@sensorId",     sensorId);
                cmd.Parameters.AddWithValue("@zone",         zone);
                cmd.Parameters.AddWithValue("@analysisType", analysisType);
                cmd.Parameters.AddWithValue("@resultado",    resultado);
                cmd.Parameters.AddWithValue("@timestamp",    timestamp);

                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Analise store error: " + ex.Message);
        }
    }

    static double ParseOrZero(string s)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
            return v;
        return 0.0;
    }

    static (string min, string max, string media) GetZoneStats(string zone, string type)
    {
        try
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                string query = @"
                    SELECT
                        MIN(CAST(value AS REAL)),
                        MAX(CAST(value AS REAL)),
                        AVG(CAST(value AS REAL))
                    FROM measurements
                    WHERE zone = @zone AND type = @type;";

                using var cmd = new SqliteCommand(query, connection);
                cmd.Parameters.AddWithValue("@zone", zone);
                cmd.Parameters.AddWithValue("@type", type);

                using var reader = cmd.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    string min   = Math.Round(reader.GetDouble(0), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string max   = Math.Round(reader.GetDouble(1), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string media = Math.Round(reader.GetDouble(2), 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return (min, max, media);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Stats error: " + ex.Message);
        }

        return ("", "", "");
    }

    static bool StoreMeasurement(string sensorId, string zone, string type, string value, string timestamp)
    {
        try
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                string insert = @"
                    INSERT INTO measurements (sensor_id, zone, type, value, timestamp)
                    VALUES (@sensorId, @zone, @type, @value, @timestamp);";

                using var cmd = new SqliteCommand(insert, connection);
                cmd.Parameters.AddWithValue("@sensorId", sensorId);
                cmd.Parameters.AddWithValue("@zone", zone);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@value", value);
                cmd.Parameters.AddWithValue("@timestamp", timestamp);

                cmd.ExecuteNonQuery();
            }

            Console.WriteLine($"Stored in DB: {sensorId} | {zone} | {type} | {value} | {timestamp}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("DB error: " + ex.Message);
            return false;
        }
    }
}
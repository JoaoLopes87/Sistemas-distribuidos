using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Data.Sqlite;
using Grpc.Net.Client;
using AnalisePrevGrpc;

class Server
{
    static readonly object dbLock = new object();
    static readonly string connectionString = "Data Source=sensors.db;";
    static AnalisePrevService.AnalisePrevServiceClient? analysisClient;

    static void Main()
    {
        try
        {
            InitDatabase();
            InitAnalysisClient();
            StartCommandLoop();

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

            string createTable = @"
                CREATE TABLE IF NOT EXISTS measurements (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    sensor_id TEXT NOT NULL,
                    zone      TEXT NOT NULL,
                    type      TEXT NOT NULL,
                    value     TEXT NOT NULL,
                    timestamp TEXT NOT NULL
                );";

            string createAnalysisTable = @"
                CREATE TABLE IF NOT EXISTS analysis_results (
                    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    sensor_id          TEXT NOT NULL,
                    zone               TEXT NOT NULL,
                    type               TEXT NOT NULL,
                    value              TEXT NOT NULL,
                    timestamp          TEXT NOT NULL,
                    nivel              TEXT NOT NULL,
                    mensagem           TEXT,
                    erro               TEXT,
                    sample_count       INTEGER,
                    min                REAL,
                    max                REAL,
                    mean               REAL,
                    trend              TEXT,
                    analysis_timestamp TEXT NOT NULL
                );";

            using var cmd = new SqliteCommand(createTable, connection);
            cmd.ExecuteNonQuery();

            using var cmdAnalysis = new SqliteCommand(createAnalysisTable, connection);
            cmdAnalysis.ExecuteNonQuery();

            Console.WriteLine("Database initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Database initialization error: {ex.Message}");
            throw;
        }
    }

    static void InitAnalysisClient()
    {
        try
        {
            var channel = GrpcChannel.ForAddress("http://localhost:5166");
            analysisClient = new AnalisePrevService.AnalisePrevServiceClient(channel);
            Console.WriteLine("Analysis RPC client initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize analysis client: {ex.Message}");
            throw;
        }
    }

    static void StartCommandLoop()
    {
        Thread commandThread = new Thread(CommandLoop);
        commandThread.IsBackground = true;
        commandThread.Start();
    }

    static void CommandLoop()
    {
        PrintHelp();

        while (true)
        {
            Console.Write("SERVER> ");
            string? command = Console.ReadLine();
            if (command == null)
                continue;

            command = command.Trim();
            if (string.IsNullOrEmpty(command))
                continue;

            if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
            }
            else if (command.Equals("measurements", StringComparison.OrdinalIgnoreCase))
            {
                ListMeasurements();
            }
            else if (command.Equals("analyses", StringComparison.OrdinalIgnoreCase))
            {
                ListAnalyses();
            }
            else if (command.StartsWith("query sensor ", StringComparison.OrdinalIgnoreCase))
            {
                string sensorId = command.Substring(13).Trim();
                QueryMeasurements("sensor_id", sensorId);
            }
            else if (command.StartsWith("query zone ", StringComparison.OrdinalIgnoreCase))
            {
                string zone = command.Substring(11).Trim();
                QueryMeasurements("zone", zone);
            }
            else if (command.Equals("analyze", StringComparison.OrdinalIgnoreCase))
            {
                PerformManualAnalysis();
            }
            else
            {
                Console.WriteLine("Unknown command. Type 'help' for available commands.");
            }
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("Available server commands:");
        Console.WriteLine("  help               - Show this help message");
        Console.WriteLine("  measurements       - List recently stored measurements");
        Console.WriteLine("  analyses           - List recent analysis results");
        Console.WriteLine("  query sensor <id>  - Query stored measurements for a sensor");
        Console.WriteLine("  query zone <zone>  - Query stored measurements for a zone");
        Console.WriteLine("  analyze            - Trigger a manual analysis request");
    }

    static void ListMeasurements()
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            string query = @"SELECT id, sensor_id, zone, type, value, timestamp FROM measurements ORDER BY id DESC LIMIT 50;";
            using var cmd = new SqliteCommand(query, connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                Console.WriteLine($"[{reader.GetInt64(0)}] {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetString(3)} | {reader.GetString(4)} | {reader.GetString(5)}");
            }
        }
    }

    static void ListAnalyses()
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            string query = @"SELECT id, sensor_id, zone, type, value, nivel, mensagem, erro, sample_count, min, max, mean, trend, analysis_timestamp FROM analysis_results ORDER BY id DESC LIMIT 50;";
            using var cmd = new SqliteCommand(query, connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                Console.WriteLine($"[{reader.GetInt64(0)}] {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetString(3)} | {reader.GetString(4)} | nivel={reader.GetString(5)} | msg={reader.GetString(6)} | erro={reader.GetString(7)} | samples={reader.GetInt32(8)} min={reader.GetDouble(9)} max={reader.GetDouble(10)} mean={reader.GetDouble(11):F2} trend={reader.GetString(12)} | {reader.GetString(13)}");
            }
        }
    }

    static void QueryMeasurements(string column, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine("Query value cannot be empty.");
            return;
        }

        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            string query = $@"SELECT id, sensor_id, zone, type, value, timestamp FROM measurements WHERE {column} = @value ORDER BY id DESC LIMIT 50;";
            using var cmd = new SqliteCommand(query, connection);
            cmd.Parameters.AddWithValue("@value", value);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                Console.WriteLine($"[{reader.GetInt64(0)}] {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetString(3)} | {reader.GetString(4)} | {reader.GetString(5)}");
            }
        }
    }

    static void PerformManualAnalysis()
    {
        Console.Write("Sensor ID: ");
        string sensorId = Console.ReadLine()?.Trim() ?? string.Empty;
        Console.Write("Zone: ");
        string zone = Console.ReadLine()?.Trim() ?? string.Empty;
        Console.Write("Type: ");
        string type = Console.ReadLine()?.Trim() ?? string.Empty;
        Console.Write("Value: ");
        string value = Console.ReadLine()?.Trim() ?? string.Empty;
        Console.Write("Timestamp (yyyy-MM-ddTHH:mm:ss): ");
        string timestamp = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(sensorId) || string.IsNullOrWhiteSpace(zone) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(timestamp))
        {
            Console.WriteLine("All fields are required to run a manual analysis.");
            return;
        }

        bool stored = StoreMeasurement(sensorId, zone, type, value, timestamp);
        if (stored)
        {
            Console.WriteLine("Measurement stored. Running analysis...");
            AnalyzeMeasurement(sensorId, zone, type, value, timestamp);
        }
        else
        {
            Console.WriteLine("Failed to store measurement for manual analysis.");
        }
    }

    static void AnalyzeMeasurement(string sensorId, string zone, string type, string value, string timestamp)
    {
        if (analysisClient == null)
        {
            Console.WriteLine("Analysis client is not initialized.");
            var statsInit = ComputeStats(sensorId, zone, type, 100);
            StoreAnalysisResult(sensorId, zone, type, value, timestamp, "ERROR", string.Empty, "Analysis client not initialized", statsInit.count, statsInit.min, statsInit.max, statsInit.mean, statsInit.trend);
            return;
        }

        try
        {
            var response = analysisClient.Analisar(new AnalisarRequest
            {
                Type = type,
                Value = value,
                Zona = zone
            });

            // compute statistics from stored measurements
            var stats = ComputeStats(sensorId, zone, type, 100);

            string nivel = response.Valido ? response.Nivel : "ERROR";
            string mensagem = response.Valido ? response.Mensagem : string.Empty;
            string erro = response.Valido ? string.Empty : response.Erro;

            StoreAnalysisResult(sensorId, zone, type, value, timestamp, nivel, mensagem, erro, stats.count, stats.min, stats.max, stats.mean, stats.trend);
            Console.WriteLine($"Analysis result for {sensorId}: {nivel} - {mensagem}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Analysis RPC error: " + ex.Message);
            var statsErr = ComputeStats(sensorId, zone, type, 100);
            StoreAnalysisResult(sensorId, zone, type, value, timestamp, "ERROR", string.Empty, ex.Message, statsErr.count, statsErr.min, statsErr.max, statsErr.mean, statsErr.trend);
        }
    }

    static (int count, double min, double max, double mean, string trend) ComputeStats(string sensorId, string zone, string type, int limit = 100)
    {
        try
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                string query = @"SELECT value, timestamp FROM measurements WHERE sensor_id = @sensorId AND zone = @zone AND type = @type ORDER BY id DESC LIMIT @limit;";
                using var cmd = new SqliteCommand(query, connection);
                cmd.Parameters.AddWithValue("@sensorId", sensorId);
                cmd.Parameters.AddWithValue("@zone", zone);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                var values = new List<double>();
                var times = new List<DateTime>();

                while (reader.Read())
                {
                    string sVal = reader.GetString(0);
                    string sTime = reader.GetString(1);
                    if (double.TryParse(sVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v) && DateTime.TryParse(sTime, out DateTime t))
                    {
                        values.Add(v);
                        times.Add(t);
                    }
                }

                if (values.Count == 0)
                    return (0, 0.0, 0.0, 0.0, "UNKNOWN");

                values.Reverse(); // oldest first
                times.Reverse();

                int n = values.Count;
                double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double v = values[i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                }
                double mean = sum / n;

                // trend via simple linear regression on index (time progression)
                if (n < 2)
                    return (n, min, max, mean, "STABLE");

                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i;
                    double y = values[i];
                    sx += x;
                    sy += y;
                    sxx += x * x;
                    sxy += x * y;
                }

                double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
                string trend = Math.Abs(slope) < 1e-6 ? "STABLE" : (slope > 0 ? "INCREASING" : "DECREASING");

                return (n, min, max, mean, trend);
            }
        }
        catch
        {
            return (0, 0.0, 0.0, 0.0, "ERROR");
        }
    }

    static void StoreAnalysisResult(string sensorId, string zone, string type, string value, string timestamp, string nivel, string mensagem, string erro, int sampleCount, double min, double max, double mean, string trend)
    {
        try
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                string insert = @"
                    INSERT INTO analysis_results (sensor_id, zone, type, value, timestamp, nivel, mensagem, erro, sample_count, min, max, mean, trend, analysis_timestamp)
                    VALUES (@sensorId, @zone, @type, @value, @timestamp, @nivel, @mensagem, @erro, @sampleCount, @min, @max, @mean, @trend, @analysisTimestamp);";

                using var cmd = new SqliteCommand(insert, connection);
                cmd.Parameters.AddWithValue("@sensorId", sensorId);
                cmd.Parameters.AddWithValue("@zone", zone);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@value", value);
                cmd.Parameters.AddWithValue("@timestamp", timestamp);
                cmd.Parameters.AddWithValue("@nivel", nivel);
                cmd.Parameters.AddWithValue("@mensagem", mensagem);
                cmd.Parameters.AddWithValue("@erro", erro);
                cmd.Parameters.AddWithValue("@sampleCount", sampleCount);
                cmd.Parameters.AddWithValue("@min", min);
                cmd.Parameters.AddWithValue("@max", max);
                cmd.Parameters.AddWithValue("@mean", mean);
                cmd.Parameters.AddWithValue("@trend", trend);
                cmd.Parameters.AddWithValue("@analysisTimestamp", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));

                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to store analysis result: " + ex.Message);
        }
    }

    static void HandleClient(object? obj)
    {
        if (obj is not TcpClient client)
        {
            return;
        }

        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 30000; 
        stream.WriteTimeout = 30000;

        StreamReader reader = new StreamReader(stream);
        StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

        try
        {
            while (true)
            {
                string? message = reader.ReadLine();
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
                        AnalyzeMeasurement(sensorId, zone, type, value, timestamp);
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
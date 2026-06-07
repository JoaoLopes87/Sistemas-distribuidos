using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Data.Sqlite;

class Server
{
    static readonly object dbLock = new object();
    static readonly string connectionString = "Data Source=sensors.db;";

    static void Main()
    {
        try
        {
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

            string createTable = @"
                CREATE TABLE IF NOT EXISTS measurements (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    sensor_id TEXT NOT NULL,
                    zone      TEXT NOT NULL,
                    type      TEXT NOT NULL,
                    value     TEXT NOT NULL,
                    timestamp TEXT NOT NULL
                );";

            using var cmd = new SqliteCommand(createTable, connection);
            cmd.ExecuteNonQuery();

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
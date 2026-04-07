using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

class Server
{
    static readonly object fileLock = new object();

    static void Main()
    {
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

    static void HandleClient(object obj)
    {
        TcpClient client = (TcpClient)obj;
        NetworkStream stream = client.GetStream();
        StreamReader reader = new StreamReader(stream);
        StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

        try
        {
            while (true)
            {
                string message = reader.ReadLine();

                if (message == null)
                {
                    break;
                }

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

                    if (sensorId == "" || zone == "" || type == "" || value == "" || timestamp == "")
                    {
                        writer.WriteLine("ERROR | STORE fields cannot be empty");
                        Console.WriteLine("Invalid STORE message (empty fields).");
                        continue;
                    }

                    string fileName = type + ".txt";
                    string line = $"{sensorId} | {zone} | {type} | {value} | {timestamp}";

                    lock (fileLock)
                    {
                        File.AppendAllText(fileName, line + Environment.NewLine);
                    }

                    Console.WriteLine("Stored measurement in " + fileName + ": " + line);
                    writer.WriteLine("STORED");
                    Console.WriteLine("SERVER -> GATEWAY: STORED");
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

        reader.Close();
        writer.Close();
        client.Close();
    }
}
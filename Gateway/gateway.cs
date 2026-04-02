using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

class Gateway
{
    static string[] lines = File.ReadAllLines("sensors_.csv");

    static TcpClient serverClient;

    static StreamWriter serverWriter;

    static Dictionary<string,SensorInfo> sensores = new Dictionary<string, SensorInfo>();

    static object lockObject=new object();
    
    static void Main()
    {

        foreach (string line in lines)
        {
            string[] keywords = line.Split(':');

            string id = keywords[0];
            string estado = keywords[1];
            string zona = keywords[2];
            string sensores_new = keywords[3].Trim('[', ']');
            string[] tipos = sensores_new.Split(',');

            sensores.Add(id, new SensorInfo(estado,zona,tipos));

        }

        string serverIP="127.0.0.1";

        int serverPort=6000;

        int gatewayPort=5001;

        Console.WriteLine("Connecting to server...");

        serverClient=new TcpClient(serverIP, serverPort);

        NetworkStream serverStream = serverClient.GetStream();

        serverWriter=new StreamWriter(serverStream);

        serverWriter.AutoFlush=true;

        Console.WriteLine("Connected to server.");

        TcpListener sensorListener=new TcpListener(IPAddress.Any, gatewayPort);

        sensorListener.Start();

        Console.WriteLine("Gateway listening for sensors...");

        while (true)
        {
            TcpClient sensorClient=sensorListener.AcceptTcpClient();

            Console.WriteLine("Sensor connected.");

            Thread sensorThread=new Thread(HandleSensor); 

            sensorThread.Start(sensorClient);
        }
    }

    static void HandleSensor(object obj)
    {
        TcpClient sensorClient=(TcpClient)obj;

        NetworkStream stream=sensorClient.GetStream();

        StreamReader reader=new StreamReader(stream);

        string sensorID="UNKNOWN";
        bool valido = false;

        try
        {
            while (true)
            {
                string message=reader.ReadLine();

                bool encontrado = false;

                if (message == null)
                {
                    break;
                }
                Console.WriteLine("Sensor message:" + message);

                string[] parts = message.Split(' ');

                if (parts[0] == "HELLO")
                {

                    foreach(KeyValuePair<string,SensorInfo> item in sensores)
                    {
                        if (item.Key == parts[1])
                        {
                            sensorID = parts[1];
                            encontrado = true;
                            string estado = item.Value.Estado;

                            if (estado == "ativo")
                            {
                                Console.WriteLine("Sensor Valido!");
                                valido=true;
                            }

                            else if (estado== "manutenção")
                            {
                                Console.WriteLine("Sensor em manutenção!");
                            }
                        }
                    }

                    if (encontrado==true)
                    {
                        Console.WriteLine("Sensor identified: " + sensorID);
                        
                    } 

                    else {
                        Console.WriteLine("Sensor is not defined!");
                    }
                }

                else if (parts[0] == "TYPES")
                {
                    string[] tps = parts[2].Split(',');
                    Console.WriteLine(tps);

                    if (!sensores.ContainsKey(sensorID))
                    {
                        Console.WriteLine("Sensor não identificado!");
                        continue;
                    }

                    if (!valido)
                    {
                        Console.WriteLine("Sensor não validado!");
                        continue;
                    }
                    
                    var sensor = sensores[sensorID];
                    bool tipoValido = true;

                    foreach (string t in tps)
                    {
                        if (!sensor.Tipos.Contains(t))
                        {
                            Console.WriteLine("Tipo não corresponde");
                            tipoValido=false;
                        }
                    }
                    if (tipoValido)
                    {
                        Console.WriteLine($"Tipos Validados {parts[2]}");
                    }
                    }

                else if (parts[0] == "DATA")
                {
                    string type=parts[1];
                    string value=parts[2];

                    string serverMessage= $"SENSOR_DATA {sensorID} {type} {value}";

                    lock (lockObject)
                    {
                        serverWriter.WriteLine(serverMessage);
                    }
                    Console.WriteLine("Forwarded to server: " + serverMessage);
                }

                else if (parts[0]=="DISCONNECT")
                {
                    Console.WriteLine("Sensor disconnected: " + sensorID);
                    break;    
                }
            }
        }

        catch (Exception)
        {
            Console.WriteLine("Sensor connection lost.");
        }
        reader.Close();
        sensorClient.Close();
    }
}
using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;

class Gateway
{
    static TcpClient serverClient;

    static StreamWriter serverWriter;

    static object lockObject=new object();
    
    static void Main()
    {
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

        try
        {
            while (true)
            {
                string message=reader.ReadLine();

                if (message == null)
                {
                    break;
                }
                Console.WriteLine("Sensor message:" + message);

                string[] parts = message.Split(' ');

                if (parts[0] == "HELLO")
                {
                    sensorID=parts[1];

                    Console.WriteLine("Sensor identified: " + sensorID);
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
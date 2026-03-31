using System;
using System.Net.Sockets;
using System.IO;

class Sensor
{
    static void Main()
    {
        string gatewayIP="127.0.0.1";
        int gatewayPort=5001;

        Console.Write("Enter sensor ID: ");
        string sensorID=Console.ReadLine();

        Console.WriteLine("Connecting to gateway...");

        TcpClient client=new TcpClient(gatewayIP, gatewayPort);
        NetworkStream stream=client.GetStream();
        StreamWriter writer=new StreamWriter(stream);
        writer.AutoFlush=true;
        Console.WriteLine("Connected to gateway.");

        writer.WriteLine($"HELLO {sensorID}");

        bool running=true;

        while (running)
        {
            Console.WriteLine();

            Console.WriteLine("1 - Send Temperature");
            Console.WriteLine("2 - Send Humidity");
            Console.WriteLine("3 - Send Types"); 
            Console.WriteLine("4 - Disconnect");

            Console.Write("Choice: ");

            string choice=Console.ReadLine();

            if (choice == "1")
            {
                Console.Write("Temperature value: ");
                string temp=Console.ReadLine();

                writer.WriteLine($"DATA TEMP | {temp}");
            }
            else if (choice == "2")
            {
                Console.Write("Humidity value: ");
                string hum = Console.ReadLine();

                writer.WriteLine($"DATA HUM | {hum}");
            }
            else if (choice == "3")
            {
                Console.WriteLine("Temperature value and pm value");
                string types = Console.ReadLine();
                string[] parts = types.Split(' ');
                string result = " ";
                foreach (string part in parts)
                {
                    result += part + " | ";
                }
                writer.WriteLine($"TYPES | {result}");
            }
            else if (choice == "4")
            {
                writer.WriteLine("DISCONNECT");

                running = false;
            }
        }

        client.Close();

        Console.WriteLine("Sensor disconnected.");
    }
}
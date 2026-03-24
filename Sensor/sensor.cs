using System;
using System.Net.Sockets;
using System.Text;

class Sensor
{
    static void Main()
    {
        Console.Write("Sensor ID: ");
        string sensorID = Console.ReadLine();

        TcpClient client = new TcpClient("127.0.0.1", 5000);

        NetworkStream stream = client.GetStream();

        Send(stream, "CONNECT|" + sensorID);

        while (true)
        {
            Console.WriteLine("\n1 - Send temperature");
            Console.WriteLine("2 - Send noise");
            Console.WriteLine("3 - Exit");

            string option = Console.ReadLine();

            if (option == "1")
            {
                Console.Write("Temperature: ");
                string value = Console.ReadLine();

                string message = "DATA|" +
                DateTime.Now.ToString("s") +
                "|" + sensorID +
                "|SCHOOL_ZONE|TEMP|" + value;

                Send(stream, message);
            }

            else if (option == "2")
            {
                Console.Write("Noise: ");
                string value = Console.ReadLine();

                string message = "DATA|" +
                DateTime.Now.ToString("s") +
                "|" + sensorID +
                "|SCHOOL_ZONE|NOISE|" + value;

                Send(stream, message);
            }

            else if (option == "3")
            {
                Send(stream, "DISCONNECT|" + sensorID);
                break;
            }
        }

        client.Close();
    }

    static void Send(NetworkStream stream, string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        stream.Write(data, 0, data.Length);
    }
}